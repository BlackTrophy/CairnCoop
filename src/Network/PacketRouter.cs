using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using CairnCoop.Protocol;
using CairnCoop.Session;
using UnityEngine;
using Unsafe = System.Runtime.CompilerServices.Unsafe;

namespace CairnCoop.Network
{
    /// <summary>
    /// Dispatches incoming packets to the correct handler.
    /// All calls arrive on the Unity main thread (pumped via Transport.Tick).
    /// </summary>
    public sealed class PacketRouter
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.PacketRouter");

        private readonly NetworkManager _net;

        public PacketRouter(NetworkManager net) { _net = net; }

        // ----------------------------------------------------------------
        // Entry point — called by ITransport.OnReceive
        // ----------------------------------------------------------------
        public void Route(PeerID from, Channel channel, ArraySegment<byte> data)
        {
            if (data.Count < Unsafe.SizeOf<PacketHeader>()) return;

            var span = new ReadOnlySpan<byte>(data.Array, data.Offset, data.Count);
            var header = PacketSerializer.Read<PacketHeader>(span);
            var payload = span.Slice(Unsafe.SizeOf<PacketHeader>());

            switch (header.Type)
            {
                // ---- Connection ----
                case PacketType.Handshake:
                    HandleHandshake(from, header, payload); break;
                case PacketType.HandshakeAck:
                    HandleHandshakeAck(from, header, payload); break;
                case PacketType.Ping:
                    HandlePing(from, header, payload); break;
                case PacketType.Pong:
                    HandlePong(from, header, payload); break;
                case PacketType.PlayerJoined:
                    HandlePlayerJoined(from, header, payload); break;
                case PacketType.PlayerLeft:
                    HandlePlayerLeft(from, header, payload); break;
                case PacketType.HostMigration:
                    HandleHostMigration(from, header, payload); break;

                // ---- Events ----
                case PacketType.AnchorPlaced:
                case PacketType.AnchorRemoved:
                    HandleAnchorEvent(from, header, payload); break;
                case PacketType.CheckpointActivated:
                    HandleCheckpoint(from, header, payload); break;
                case PacketType.PlayerRespawned:
                    HandleRespawn(from, header, payload); break;
                case PacketType.GripChanged:
                    HandleGripChanged(from, header, payload); break;
                case PacketType.RopeAttached:
                case PacketType.RopeDetached:
                    HandleRopeEvent(from, header, payload); break;
                case PacketType.ServerCorrection:
                    HandleServerCorrection(from, header, payload); break;

                // ---- Snapshots (host broadcasts, clients receive) ----
                case PacketType.WorldSnapshot:
                    HandleWorldSnapshot(from, header, span); break;

                // ---- IK ----
                case PacketType.IKUpdate:
                    HandleIKUpdate(from, header, payload); break;

                // ---- Rope ----
                case PacketType.RopeUpdate:
                    HandleRopeUpdate(from, header, payload); break;

                // ---- Input (host receives from clients) ----
                case PacketType.InputFrame:
                    HandleInputFrame(from, header, payload); break;

                // ---- Late Join ----
                case PacketType.LateJoinRequest:
                    _net.LateJoin?.OnLateJoinRequest(from); break;
                case PacketType.LateJoinData:
                    _net.LateJoin?.OnLateJoinFragment(from, header, payload); break;
                case PacketType.LateJoinComplete:
                    _net.LateJoin?.OnLateJoinComplete(from); break;

                default:
                    Log.LogWarning($"Unknown packet type 0x{(ushort)header.Type:X4} from {from}"); break;
            }
        }

        // ================================================================
        // HANDLERS
        // ================================================================

        private void HandleHandshake(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!_net.IsHost) return; // Only host handles handshakes
            if (!PacketSerializer.TryRead(payload, out HandshakePacket pkt)) return;

            if (pkt.ProtocolVersion != CairnCoop.Protocol.Protocol.Version)
            {
                Log.LogWarning($"Protocol mismatch from {from}: got {pkt.ProtocolVersion}, expected {CairnCoop.Protocol.Protocol.Version}");
                var kick = new HandshakeAckPacket
                {
                    Header          = new PacketHeader(PacketType.HandshakeAck, 0xFF),
                    ProtocolVersion = CairnCoop.Protocol.Protocol.Version,
                    AssignedPlayerID = 0xFF, // rejected
                };
                Span<byte> buf = stackalloc byte[Unsafe.SizeOf<HandshakeAckPacket>()];
                PacketSerializer.Write(buf, kick);
                _net.Transport!.Send(from, buf, Channel.Connection, SendMode.Reliable);
                _net.Transport.Disconnect(from, DisconnectReason.ProtocolMismatch);
                return;
            }

            int slot = _net.Session!.AssignSlot(from, pkt.SteamID);
            if (slot < 0)
            {
                Log.LogWarning($"Session full, rejecting {from}");
                return;
            }

            var ack = new HandshakeAckPacket
            {
                Header           = new PacketHeader(PacketType.HandshakeAck, (byte)slot),
                ProtocolVersion  = CairnCoop.Protocol.Protocol.Version,
                AssignedPlayerID = (byte)slot,
            };
            Span<byte> ackBuf = stackalloc byte[Unsafe.SizeOf<HandshakeAckPacket>()];
            PacketSerializer.Write(ackBuf, ack);
            _net.Transport!.Send(from, ackBuf, Channel.Connection, SendMode.Reliable);

            Log.LogInfo($"Assigned slot {slot} to {from}");
            BroadcastPlayerJoined(from, (byte)slot);
        }

        private void HandleHandshakeAck(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(payload, out HandshakeAckPacket ack)) return;
            if (ack.AssignedPlayerID == 0xFF)
            {
                Log.LogError("Join rejected by host (full or protocol mismatch)");
                return;
            }
            _net.Session!.SetLocalSlot(ack.AssignedPlayerID);
            _net.Session.HostPeerID = from;

            // Spawn LocalPlayerController now that we have a slot
            var lpc = _net.gameObject.AddComponent<Client.LocalPlayerController>();
            lpc.Initialize(_net, ack.AssignedPlayerID);
            _net.SetLocalPlayer(lpc);

            Log.LogInfo($"Joined session as player {ack.AssignedPlayerID}");
        }

        private void HandlePing(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(payload, out PingPacket ping)) return;
            var pong = new PingPacket
            {
                Header     = new PacketHeader(PacketType.Pong, (byte)_net.Session!.LocalSlot),
                SendTimeMs = ping.SendTimeMs,
            };
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PingPacket>()];
            PacketSerializer.Write(buf, pong);
            _net.Transport!.Send(from, buf, Channel.Connection, SendMode.Reliable);
        }

        private void HandlePong(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(payload, out PingPacket pong)) return;
            uint rtt = (uint)(UnityEngine.Time.time * 1000) - pong.SendTimeMs;
            _net.Session?.UpdateRTT(from, rtt);
        }

        private void HandlePlayerJoined(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            // payload: byte slotID + 8 bytes steamID
            if (payload.Length < 9) return;
            byte slot    = payload[0];
            ulong steamID = MemoryMarshal.Read<ulong>(payload.Slice(1));
            _net.Session!.RegisterRemotePlayer(new PeerID(steamID), slot);
            _net.RemotePlayers!.SpawnPlayer(new PeerID(steamID), slot);
            Log.LogInfo($"Player joined: slot={slot} steamID={steamID}");
        }

        private void HandlePlayerLeft(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1) return;
            byte slot = payload[0];
            var peer  = _net.Session!.GetPeerBySlot(slot);
            _net.RemotePlayers!.DespawnPlayer(peer);
            _net.Session.RemoveSlot(slot);
        }

        private void HandleHostMigration(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 8) return;
            ulong newHostID = MemoryMarshal.Read<ulong>(payload);
            var newHostPeer = new PeerID(newHostID);
            _net.Session!.HostPeerID = newHostPeer;
            Log.LogInfo($"Host migrated to {newHostPeer}");
        }

        private void HandleWorldSnapshot(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> full)
        {
            if (_net.IsHost) return; // Host doesn't need own snapshot
            // Skip header bytes
            var payload = full.Slice(Unsafe.SizeOf<PacketHeader>());

            Span<PlayerStatePacket> players = stackalloc PlayerStatePacket[CairnCoop.Protocol.Protocol.MaxPlayers];
            bool ok = PacketSerializer.TryReadWorldSnapshot(payload,
                out uint tick, out uint serverMs,
                out byte activeMask, out byte cpMask,
                players);
            if (!ok) return;

            _net.Ticks.SyncServerTime(tick, serverMs);
            _net.RemotePlayers?.ApplyWorldSnapshot(tick, activeMask, cpMask, players);
            _net.LocalPlayer?.OnServerSnapshot(tick, players[_net.Session!.LocalSlot]);
        }

        private void HandleIKUpdate(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(payload, out IKSnapshotPacket pkt)) return;
            if (_net.IsHost)
            {
                // Relay to all other clients by rebroadcasting the raw bytes
                Span<byte> buf = stackalloc byte[Unsafe.SizeOf<IKSnapshotPacket>()];
                PacketSerializer.Write(buf, pkt);
                _net.Transport!.Broadcast(buf.ToArray(), Channel.IK, SendMode.Unreliable, exclude: from);
            }
            else
            {
                _net.RemotePlayers?.ApplyIKSnapshot(from, pkt);
            }
        }

        private void HandleRopeUpdate(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(payload, out RopeSnapshotPacket pkt)) return;
            if (_net.IsHost)
            {
                // Relay
                Span<byte> buf = stackalloc byte[Unsafe.SizeOf<RopeSnapshotPacket>()];
                PacketSerializer.Write(buf, pkt);
                _net.Transport!.Broadcast(buf.ToArray(), Channel.Rope, SendMode.Unreliable, exclude: from);
            }
            else
            {
                _net.RemotePlayers?.ApplyRopeSnapshot(from, pkt);
            }
        }

        private void HandleInputFrame(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!_net.IsHost) return;
            if (!PacketSerializer.TryRead(payload, out InputFramePacket pkt)) return;
            _net.HostSim?.ProcessClientInput(from, pkt);
        }

        private void HandleAnchorEvent(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(payload, out AnchorEventPacket pkt)) return;
            if (_net.IsHost)
            {
                bool valid = _net.HostSim!.ValidateAnchor(from, pkt);
                pkt.IsValid = (byte)(valid ? 1 : 0);
                // Broadcast validated (or rejected) anchor to all
                Span<byte> anchorBuf = stackalloc byte[Unsafe.SizeOf<AnchorEventPacket>()];
                PacketSerializer.Write(anchorBuf, pkt);
                _net.Transport!.Broadcast(anchorBuf.ToArray(), Channel.Events, SendMode.Reliable);
            }
            else
            {
                // Client applies anchor
                _net.RemotePlayers?.ApplyAnchorEvent(pkt);
            }
        }

        private void HandleCheckpoint(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 4) return;
            int cpID = MemoryMarshal.Read<int>(payload);
            _net.Session?.SetCheckpoint(cpID);
            // Trigger game checkpoint — via GameBridge
            GameBridge.ActivateCheckpoint(cpID);
        }

        private void HandleRespawn(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1) return;
            byte slot = payload[0];
            var peer  = _net.Session!.GetPeerBySlot(slot);
            int cpID  = _net.Session.CurrentCheckpointID;
            _net.RemotePlayers?.RespawnPlayer(peer, cpID);
        }

        private void HandleGripChanged(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 2) return;
            byte gripLeft  = payload[0];
            byte gripRight = payload[1];
            _net.RemotePlayers?.UpdateGrip(from, gripLeft, gripRight);
        }

        private void HandleRopeEvent(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            // Full event relayed via HandleRopeUpdate — this is just for attach/detach state
            _net.RemotePlayers?.HandleRopeEvent(from, hdr.Type);
        }

        private void HandleServerCorrection(PeerID from, PacketHeader hdr, ReadOnlySpan<byte> payload)
        {
            if (_net.IsHost) return;
            if (!PacketSerializer.TryRead(payload, out ServerCorrectionPacket pkt)) return;
            _net.LocalPlayer?.OnServerCorrection(pkt);
        }

        // ================================================================
        // BROADCAST HELPERS (host → all)
        // ================================================================

        public void BroadcastPlayerJoined(PeerID peer, byte slot)
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PacketHeader>() + 9];
            int offset = 0;
            offset += PacketSerializer.WriteHeader(buf, PacketType.PlayerJoined, slot);
            buf[offset++] = slot;
            ulong peerVal = peer.Value;
            MemoryMarshal.Write(buf.Slice(offset), ref peerVal);
            _net.Transport!.Broadcast(buf.Slice(0, offset + 8), Channel.Events, SendMode.Reliable);
        }

        public void BroadcastPlayerLeftEvent(PeerID peer)
        {
            byte slot = (byte)(_net.Session?.GetSlotByPeer(peer) ?? 0xFF);
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PacketHeader>() + 1];
            int offset = PacketSerializer.WriteHeader(buf, PacketType.PlayerLeft, slot);
            buf[offset] = slot;
            _net.Transport!.Broadcast(buf.Slice(0, offset + 1), Channel.Events, SendMode.Reliable);
        }

        public void BroadcastHostMigration(PeerID newHost)
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PacketHeader>() + 8];
            int offset = PacketSerializer.WriteHeader(buf, PacketType.HostMigration, 0xFF);
            ulong hostVal = newHost.Value;
            MemoryMarshal.Write(buf.Slice(offset), ref hostVal);
            _net.Transport!.Broadcast(buf.Slice(0, offset + 8), Channel.Connection, SendMode.Reliable);
        }

        // ================================================================
        // Public send helpers for sub-systems
        // ================================================================

        public void SendCheckpointActivated(int checkpointID)
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PacketHeader>() + 4];
            int offset = PacketSerializer.WriteHeader(buf, PacketType.CheckpointActivated,
                (byte)_net.Session!.LocalSlot);
            MemoryMarshal.Write(buf.Slice(offset), ref checkpointID);
            _net.Transport!.Send(_net.Session.HostPeerID,
                buf.Slice(0, offset + 4), Channel.Events, SendMode.Reliable);
        }

        public void SendAnchorEvent(in AnchorEventPacket pkt)
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PacketHeader>() + Unsafe.SizeOf<AnchorEventPacket>()];
            int offset = PacketSerializer.WriteHeader(buf, pkt.Header.Type, pkt.Header.PlayerID);
            PacketSerializer.Write(buf.Slice(offset), pkt);
            _net.Transport!.Send(_net.Session!.HostPeerID,
                buf, Channel.Events, SendMode.Reliable);
        }

        public void SendGripChanged(byte leftID, byte rightID)
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<PacketHeader>() + 2];
            int offset = PacketSerializer.WriteHeader(buf, PacketType.GripChanged,
                (byte)_net.Session!.LocalSlot);
            buf[offset]     = leftID;
            buf[offset + 1] = rightID;
            _net.Transport!.Send(_net.Session.HostPeerID,
                buf.Slice(0, offset + 2), Channel.Events, SendMode.Reliable);
        }

    }
}
