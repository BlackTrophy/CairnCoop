using System.Runtime.CompilerServices;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace CairnCoop.Client
{
    /// <summary>
    /// Manages the local player's network participation:
    ///   - Collects input each frame and sends to host
    ///   - Maintains prediction buffer for rollback
    ///   - Applies server corrections smoothly
    ///   - Sends IK and rope snapshots on their respective timers
    /// </summary>
    public sealed class LocalPlayerController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.LocalPlayer");

        private NetworkManager    _net    = null!;
        private int               _slot;
        private PredictionBuffer  _pred   = new();

        // Correction smoothing
        private Vector3 _correctionOffset  = Vector3.zero;
        private int     _correctionFrames;
        private const float CORRECTION_SPEED = 0.02f; // 2 cm per frame @ 60fps

        // IK tracking
        private IK.LocalIKTracker? _ikTracker;
        // Rope tracking
        private Rope.LocalRopeTracker? _ropeTracker;

        // Pawn attachment state
        private bool _pawnReady;

        // ----------------------------------------------------------------
        // Initialize (called from PacketRouter after HandshakeAck)
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        public void Initialize(NetworkManager net, byte slot)
        {
            _net  = net;
            _slot = slot;
            _pawnReady = false;
        }

        // ----------------------------------------------------------------
        // Called from NetworkManager.Update → TickSnapshot
        // ----------------------------------------------------------------
        public void SendInput()
        {
            if (GameBridge.LocalPawn == null) return;

            var input = GameBridge.CollectInput(_net.Ticks.CurrentTick);
            _pred.SaveInput(_net.Ticks.CurrentTick, input);

            var pkt = new InputFramePacket
            {
                Header       = new PacketHeader(PacketType.InputFrame, (byte)_slot),
                Tick         = _net.Ticks.CurrentTick,
                MoveX        = input.MoveX,
                MoveY        = input.MoveY,
                Buttons      = input.Buttons,
                LeftTrigger  = input.LeftTrigger,
                RightTrigger = input.RightTrigger,
            };

            System.Span<byte> buf = stackalloc byte[Unsafe.SizeOf<InputFramePacket>()];
            PacketSerializer.Write(buf, pkt);
            _net.Transport!.Send(_net.Session!.HostPeerID, buf,
                Channel.Input, SendMode.Unreliable);
        }

        public void SendIKUpdate()   => _ikTracker?.SendSnapshot();
        public void SendRopeUpdate() => _ropeTracker?.SendSnapshot();

        // ----------------------------------------------------------------
        // Called when we receive a WorldSnapshot from host
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        public void OnServerSnapshot(uint tick, PlayerStatePacket serverState)
        {
            var predicted = _pred.GetState(tick);
            if (!predicted.HasValue) return;

            float posErr  = Vector3.Distance(
                predicted.Value.Position.ToUnity(), serverState.Position.ToUnity());
            float stamErr = Mathf.Abs(predicted.Value.Stamina - serverState.Stamina);

            if (posErr < 0.05f && stamErr < 0.02f) return; // good enough

            // Rollback and re-simulate
            Rollback(tick, serverState);
        }

        // ----------------------------------------------------------------
        // Called when host sends an explicit ServerCorrection packet
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        public void OnServerCorrection(ServerCorrectionPacket pkt)
        {
            Log.LogDebug($"Correction at tick {pkt.CorrectedTick}");
            Rollback(pkt.CorrectedTick, pkt.State);
        }

        // ----------------------------------------------------------------
        // Rollback
        // ----------------------------------------------------------------
        private void Rollback(uint fromTick, PlayerStatePacket correctedState)
        {
            _pred.OverwriteState(fromTick, correctedState);

            uint current = _net.Ticks.CurrentTick;
            for (uint t = fromTick + 1; t <= current; t++)
            {
                var inp = _pred.GetInput(t);
                if (!inp.HasValue) break;
                var prev = _pred.GetState(t - 1) ?? correctedState;
                var next = SimulateFrame(prev, inp.Value);
                _pred.OverwriteState(t, next);
            }

            // Apply correction with smoothing
            var corrCurrent = _pred.GetState(current);
            if (corrCurrent == null || GameBridge.LocalPawn == null) return;

            float err = Vector3.Distance(
                GameBridge.LocalPawn.transform.position,
                corrCurrent.Value.Position.ToUnity());

            if (err > 2.0f)
            {
                // Hard snap
                GameBridge.LocalPawn.transform.position = corrCurrent.Value.Position.ToUnity();
                _correctionOffset = Vector3.zero;
                _correctionFrames = 0;
            }
            else
            {
                _correctionOffset = corrCurrent.Value.Position.ToUnity()
                                  - GameBridge.LocalPawn.transform.position;
                _correctionFrames = Mathf.Max(1, (int)(err / CORRECTION_SPEED));
            }
        }

        // ----------------------------------------------------------------
        // Unity Update — smooth correction + pawn attachment poll
        // ----------------------------------------------------------------
        private void Update()
        {
            // Poll for pawn attachment until ready (replaces coroutine)
            if (!_pawnReady && GameBridge.LocalPawn != null)
            {
                var pawn = GameBridge.LocalPawn;
                _ikTracker   = pawn.gameObject.AddComponent<IK.LocalIKTracker>();
                _ikTracker.Initialize(_net, _slot);
                _ropeTracker = pawn.gameObject.AddComponent<Rope.LocalRopeTracker>();
                _ropeTracker.Initialize(_net, _slot);
                _pawnReady = true;
                Log.LogInfo($"LocalPlayerController attached to pawn (slot {_slot})");
            }

            if (_correctionFrames > 0 && GameBridge.LocalPawn != null)
            {
                var step = _correctionOffset / _correctionFrames;
                GameBridge.LocalPawn.transform.position += step;
                _correctionFrames--;
                if (_correctionFrames == 0) _correctionOffset = Vector3.zero;
            }
        }

        // ----------------------------------------------------------------
        // Frame simulation (mirrors HostSimulator.IntegrateInput)
        // ----------------------------------------------------------------
        private static PlayerStatePacket SimulateFrame(
            PlayerStatePacket prev, InputFramePacket input)
        {
            var vel  = prev.Velocity.ToUnity();
            var pos  = prev.Position.ToUnity();
            var move = new Vector2(input.MoveX, input.MoveY);
            const float dt = 1f / 20f;

            switch ((ClimberState)prev.ClimbState)
            {
                case ClimberState.Climbing:
                case ClimberState.Traversing:
                    float speed = 2.0f * Mathf.Clamp01(prev.Stamina / 0.3f);
                    vel = new Vector3(move.x, move.y, 0) * speed;
                    pos += vel * dt;
                    break;
                case ClimberState.Jumping:
                case ClimberState.Falling:
                    vel += Physics.gravity * dt;
                    pos += vel * dt;
                    break;
            }

            float drain   = HostSimulator_GetStamDrain((ClimberState)prev.ClimbState);
            float newStam = Mathf.Clamp01(prev.Stamina - drain * dt);

            return new PlayerStatePacket
            {
                Tick        = prev.Tick + 1,
                Position    = pos,
                Yaw         = prev.Yaw,
                Velocity    = vel,
                Stamina     = newStam,
                ClimbState  = prev.ClimbState,
                GripLeftID  = prev.GripLeftID,
                GripRightID = prev.GripRightID,
                Flags       = prev.Flags,
            };
        }

        // Duplicate to avoid circular dependency — matches HostSimulator values
        private static float HostSimulator_GetStamDrain(ClimberState s) => s switch
        {
            ClimberState.Climbing   => 0.04f,
            ClimberState.Traversing => 0.02f,
            ClimberState.Resting    => -0.05f,
            _                       => 0f,
        };
    }
}
