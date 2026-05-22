using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using UnityEngine;

namespace CairnCoop.Host
{
    /// <summary>
    /// Host-authoritative simulation layer.
    ///
    /// Responsibilities:
    ///   - Collect client inputs
    ///   - Validate movement, stamina, anchors
    ///   - Build and broadcast WorldSnapshot each server tick
    ///   - Send correction packets when client prediction drifts too far
    /// </summary>
    public sealed partial class HostSimulator
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.HostSimulator");

        private readonly NetworkManager _net;
        private readonly ServerPlayerValidator _validator = new();
        private readonly AuthoritativeGameState _state;

        // Per-peer: last confirmed PlayerState (built from input integration)
        private readonly Dictionary<PeerID, PlayerStatePacket> _lastConfirmedState = new();
        private readonly Dictionary<PeerID, InputFramePacket>  _pendingInputs      = new();

        // Anchor ID counter
        private ushort _nextAnchorID = 1;

        public HostSimulator(NetworkManager net)
        {
            _net   = net;
            _state = new AuthoritativeGameState(CairnCoop.Protocol.Protocol.MaxPlayers);
        }

        // ----------------------------------------------------------------
        // Called by PacketRouter when an InputFrame arrives
        // ----------------------------------------------------------------
        public void ProcessClientInput(PeerID from, InputFramePacket input)
        {
            _pendingInputs[from] = input;
        }

        // ----------------------------------------------------------------
        // Called from NetworkManager on 20 Hz tick
        // ----------------------------------------------------------------
        public void BroadcastWorldSnapshot()
        {
            // 1. Integrate all pending client inputs
            IntegratePendingInputs();

            // 2. Sample local (host) player state directly from game
            int hostSlot = _net.Session!.LocalSlot;
            _state.Players[hostSlot] = GameBridge.SampleLocalPlayerState((uint)_net.Ticks.CurrentTick);

            // 3. Build snapshot packet
            Span<byte> buf = stackalloc byte[512];
            int offset = 0;

            offset += PacketSerializer.WriteHeader(buf, PacketType.WorldSnapshot,
                (byte)hostSlot);

            offset += PacketSerializer.WriteWorldSnapshot(
                buf.Slice(offset),
                _net.Ticks.CurrentTick,
                (uint)(Time.time * 1000),
                _net.Session.BuildActiveMask(),
                (byte)_net.Session.BuildCheckpointMask(),
                _state.Players.AsSpan());

            // 4. Broadcast to all clients
            _net.Transport!.Broadcast(buf.Slice(0, offset),
                Channel.Snapshot, SendMode.Unreliable);
        }

        // ----------------------------------------------------------------
        // Integrate client inputs → authoritative positions
        // ----------------------------------------------------------------
        private void IntegratePendingInputs()
        {
            foreach (var (peer, input) in _pendingInputs)
            {
                int slot = _net.Session!.GetSlotByPeer(peer);
                if (slot < 0) continue;

                var prev = _state.Players[slot];

                // Build candidate new state from input
                var candidate = IntegrateInput(prev, input);

                // Validate movement plausibility
                if (!_validator.ValidateMovement(prev, candidate, 1f / 20f))
                {
                    Log.LogWarning($"[Anti-Cheat] Invalid movement from slot {slot} — sending correction");
                    SendCorrection(peer, prev);
                    continue;
                }
                if (!_validator.ValidateStamina(prev, candidate, 1f / 20f))
                {
                    Log.LogWarning($"[Anti-Cheat] Stamina hack from slot {slot}");
                    candidate.Stamina = prev.Stamina; // override stamina
                }

                _state.Players[slot] = candidate;

                // Check if correction needed (prediction error > 5 cm)
                if (_lastConfirmedState.TryGetValue(peer, out var last))
                {
                    float err = Vector3.Distance(
                        last.Position.ToUnity(), candidate.Position.ToUnity());
                    if (err > 0.05f)
                        SendCorrection(peer, candidate);
                }
                _lastConfirmedState[peer] = candidate;
            }
            _pendingInputs.Clear();
        }

        private static PlayerStatePacket IntegrateInput(
            PlayerStatePacket prev, InputFramePacket input)
        {
            // Simple kinematic integration — mirrors LocalPlayerController.SimulateOneFrame
            var vel  = prev.Velocity.ToUnity();
            var pos  = prev.Position.ToUnity();
            var move = new Vector2(input.MoveX, input.MoveY);
            const float dt = 1f / 20f;

            switch ((ClimberState)prev.ClimbState)
            {
                case ClimberState.Climbing:
                case ClimberState.Traversing:
                    // Clamp to max climb speed
                    float climbSpeed = 2.0f * Mathf.Clamp01(prev.Stamina / 0.3f);
                    vel = new Vector3(move.x, move.y, 0) * climbSpeed;
                    pos += vel * dt;
                    break;

                case ClimberState.Jumping:
                case ClimberState.Falling:
                    vel += Physics.gravity * dt;
                    pos += vel * dt;
                    break;

                case ClimberState.Rappelling:
                    vel = new Vector3(move.x * 0.5f, -1.5f * (1f - move.y * 0.5f), 0);
                    pos += vel * dt;
                    break;
            }

            float stamDrain = GetStaminaDrain((ClimberState)prev.ClimbState,
                (input.LeftTrigger + input.RightTrigger) / 510f);
            float newStam = Mathf.Clamp01(prev.Stamina - stamDrain * dt);

            return new PlayerStatePacket
            {
                Tick       = prev.Tick + 1,
                Position   = pos,
                Yaw        = prev.Yaw, // yaw comes from client, not integrated here
                Velocity   = vel,
                Stamina    = newStam,
                ClimbState = prev.ClimbState,
                GripLeftID = prev.GripLeftID,
                GripRightID = prev.GripRightID,
                Flags      = prev.Flags,
            };
        }

        private static float GetStaminaDrain(ClimberState state, float gripIntensity) => state switch
        {
            ClimberState.Climbing   => 0.04f + gripIntensity * 0.06f,
            ClimberState.Traversing => 0.02f,
            ClimberState.Rappelling => 0.01f,
            ClimberState.Resting    => -0.05f, // regenerate
            _                       => 0f,
        };

        // ----------------------------------------------------------------
        // Correction
        // ----------------------------------------------------------------
        private void SendCorrection(PeerID peer, PlayerStatePacket correct)
        {
            var pkt = new ServerCorrectionPacket
            {
                Header        = new PacketHeader(PacketType.ServerCorrection, 0xFF),
                CorrectedTick = correct.Tick,
                State         = correct,
            };
            Span<byte> buf = stackalloc byte[
                System.Runtime.CompilerServices.Unsafe.SizeOf<ServerCorrectionPacket>()];
            PacketSerializer.Write(buf, pkt);
            _net.Transport!.Send(peer, buf, Channel.Events, SendMode.Reliable);
        }

        // ----------------------------------------------------------------
        // Anchor validation (called from PacketRouter)
        // ----------------------------------------------------------------
        public bool ValidateAnchor(PeerID from, AnchorEventPacket pkt)
        {
            if (pkt.EventType == 1) return true; // removal always valid

            Vector3 pos    = pkt.Position.ToUnity();
            Vector3 normal = pkt.Normal.ToUnity();

            // 1. Must hit wall geometry
            if (!Physics.Raycast(pos - normal * 0.05f, normal, out _, 0.2f,
                LayerMask.GetMask("ClimbableWall", "Default")))
            {
                Log.LogWarning($"Anchor rejected: no wall at {pos}");
                return false;
            }

            // 2. Must not overlap existing anchor
            foreach (var anchor in _state.Anchors)
            {
                if (Vector3.Distance(anchor.Position.ToUnity(), pos) < 0.3f)
                {
                    Log.LogWarning("Anchor rejected: too close to existing anchor");
                    return false;
                }
            }

            // 3. Stamina check for placing anchor
            int slot = _net.Session!.GetSlotByPeer(from);
            if (slot >= 0 && _state.Players[slot].Stamina < 0.1f)
            {
                Log.LogWarning("Anchor rejected: insufficient stamina");
                return false;
            }

            pkt = pkt with { AnchorID = _nextAnchorID++ };
            _state.Anchors.Add(new AnchorState
            {
                AnchorID = pkt.AnchorID,
                Position = pkt.Position,
                Normal   = pkt.Normal,
                OwnerSlot = (byte)slot,
            });
            return true;
        }
    }

    // ----------------------------------------------------------------
    //  Authoritative World State
    // ----------------------------------------------------------------
    public sealed class AuthoritativeGameState
    {
        public PlayerStatePacket[] Players;
        public List<AnchorState>   Anchors = new();
        public int                 CheckpointMask;

        public AuthoritativeGameState(int maxPlayers)
        {
            Players = new PlayerStatePacket[maxPlayers];
        }

        public int BuildCheckpointMask() => CheckpointMask;
    }

    public sealed class AnchorState
    {
        public ushort     AnchorID;
        public NetVector3 Position;
        public NetVector3 Normal;
        public byte       OwnerSlot;
    }

    // ----------------------------------------------------------------
    //  Input validation
    // ----------------------------------------------------------------
    public sealed class ServerPlayerValidator
    {
        private const float MAX_CLIMB_SPEED    = 2.5f;   // m/s
        private const float MAX_FALL_SPEED     = 30f;    // m/s terminal velocity
        private const float LATENCY_TOLERANCE  = 0.6f;   // m per tick extra buffer

        public bool ValidateMovement(
            PlayerStatePacket prev, PlayerStatePacket next, float dt)
        {
            float moved = Vector3.Distance(
                prev.Position.ToUnity(), next.Position.ToUnity());

            float maxAllowed = ((ClimberState)prev.ClimbState switch
            {
                ClimberState.Falling => MAX_FALL_SPEED,
                _                   => MAX_CLIMB_SPEED,
            } * dt) + LATENCY_TOLERANCE;

            return moved <= maxAllowed;
        }

        public bool ValidateStamina(
            PlayerStatePacket prev, PlayerStatePacket next, float dt)
        {
            float delta = next.Stamina - prev.Stamina;
            const float MAX_REGEN = 0.07f; // 7% per second
            return delta <= MAX_REGEN * dt + 0.001f;
        }
    }
}
