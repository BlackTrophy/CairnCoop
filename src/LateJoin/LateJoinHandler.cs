using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using UnityEngine;

namespace CairnCoop.LateJoin
{
    /// <summary>
    /// Handles the Late-Join protocol (Channel 5, Reliable, fragmented).
    ///
    /// HOST side:
    ///   1. Serialises AuthoritativeWorldState → compressed byte array
    ///   2. Fragments into 1 KB chunks
    ///   3. Sends all chunks to the joining peer
    ///
    /// CLIENT side:
    ///   1. Receives fragments, reassembles
    ///   2. Deserialises and applies InitialWorldState
    ///   3. Spawns remote pawns at their last known positions
    /// </summary>
    public sealed class LateJoinHandler
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.LateJoin");

        private readonly NetworkManager _net;

        // CLIENT: reassembly buffer per sending peer
        private readonly Dictionary<PeerID, FragmentAssembler> _assemblers = new();

        public LateJoinHandler(NetworkManager net) { _net = net; }

        // ================================================================
        // HOST SIDE
        // ================================================================

        public void SendInitialState(PeerID peer, bool isRejoin)
        {
            if (!_net.IsHost) return;
            // Send synchronously — all scene objects should be ready since we're called
            // from OnPeerConnected which fires after the connection is fully established.
            SendInitialStateSync(peer, isRejoin);
        }

        private void SendInitialStateSync(PeerID peer, bool isRejoin)
        {
            Log.LogInfo($"Sending initial state to {peer} (rejoin={isRejoin})");

            byte[] data = SerializeWorldState();
            byte[] compressed = Compress(data);
            int totalFrags = PacketSerializer.FragmentCount(compressed.Length);

            Log.LogInfo($"World state: {data.Length} B raw, {compressed.Length} B compressed, {totalFrags} fragments");

            for (int i = 0; i < totalFrags; i++)
            {
                var frag = PacketSerializer.GetFragment(compressed, i);
                SendFragment(peer, i, totalFrags, (uint)compressed.Length, frag.Span);
            }

            // Send LateJoinComplete
            Span<byte> completeBuf = stackalloc byte[4];
            PacketSerializer.WriteHeader(completeBuf, PacketType.LateJoinComplete, 0xFF);
            _net.Transport!.Send(peer, completeBuf.Slice(0, 4), Channel.LateJoin, SendMode.Reliable);

            Log.LogInfo($"Late-join data sent to {peer}.");
        }

        private void SendFragment(PeerID peer, int idx, int total, uint totalBytes, ReadOnlySpan<byte> payload)
        {
            int headerSize = System.Runtime.CompilerServices.Unsafe.SizeOf<LateJoinFragmentHeader>();
            Span<byte> buf = new byte[headerSize + payload.Length];

            var fragHeader = new LateJoinFragmentHeader
            {
                Header         = new PacketHeader(PacketType.LateJoinData, 0xFF,
                                    idx == total - 1 ? (byte)0x02 : (byte)0x01),
                FragmentIndex  = (ushort)idx,
                TotalFragments = (ushort)total,
                TotalBytes     = totalBytes,
            };

            PacketSerializer.Write(buf, fragHeader);
            payload.CopyTo(buf.Slice(headerSize));

            _net.Transport!.Send(peer, buf, Channel.LateJoin, SendMode.Reliable);
        }

        // ================================================================
        // CLIENT SIDE — called from PacketRouter
        // ================================================================

        public void OnLateJoinRequest(PeerID from) { /* host handles implicitly */ }

        public void OnLateJoinFragment(PeerID from, PacketHeader header, ReadOnlySpan<byte> payload)
        {
            if (!PacketSerializer.TryRead(
                    payload.Slice(-System.Runtime.CompilerServices.Unsafe.SizeOf<PacketHeader>()),
                    out LateJoinFragmentHeader fragHeader))
            {
                // Re-read: header was stripped by router, re-parse from payload start
                if (!PacketSerializer.TryRead(payload, out fragHeader)) return;
            }

            if (!_assemblers.TryGetValue(from, out var asm))
            {
                asm = new FragmentAssembler(fragHeader.TotalFragments, fragHeader.TotalBytes);
                _assemblers[from] = asm;
            }

            int dataOffset = System.Runtime.CompilerServices.Unsafe.SizeOf<LateJoinFragmentHeader>();
            asm.AddFragment(fragHeader.FragmentIndex, payload.Slice(dataOffset));
        }

        public void OnLateJoinComplete(PeerID from)
        {
            if (!_assemblers.TryGetValue(from, out var asm))
            {
                Log.LogWarning("LateJoinComplete received but no assembler found.");
                return;
            }
            _assemblers.Remove(from);

            if (!asm.IsComplete)
            {
                Log.LogError("LateJoin data incomplete — missing fragments.");
                return;
            }

            byte[] compressed  = asm.GetData();
            byte[] decompressed = Decompress(compressed);
            ApplyInitialState(decompressed);
        }

        public void OnSceneLoaded(string sceneName)
        {
            // Nothing — state is applied after LateJoinComplete
        }

        // ================================================================
        // SERIALIZATION
        // ================================================================

        private byte[] SerializeWorldState()
        {
            using var ms  = new MemoryStream();
            using var bw  = new BinaryWriter(ms);

            var state   = _net.HostSim?.State ?? throw new InvalidOperationException("No state");
            var session = _net.Session!;

            // Scene name
            bw.Write(GameBridge.GetCurrentSceneName() ?? "");

            // Checkpoint
            bw.Write(session.CurrentCheckpointID);

            // Active player count
            byte mask = session.BuildActiveMask();
            bw.Write(mask);

            for (int i = 0; i < CairnCoop.Protocol.Protocol.MaxPlayers; i++)
            {
                if ((mask & (1 << i)) == 0) continue;

                var ps = state.Players[i];
                bw.Write(i);                         // slot
                bw.Write(ps.Position.X);
                bw.Write(ps.Position.Y);
                bw.Write(ps.Position.Z);
                bw.Write(ps.Yaw);
                bw.Write(ps.Stamina);
                bw.Write((byte)ps.ClimbState);
                bw.Write(session.GetPeerBySlot(i).Value);
            }

            // Anchors
            bw.Write((ushort)state.Anchors.Count);
            foreach (var anchor in state.Anchors)
            {
                bw.Write(anchor.AnchorID);
                bw.Write(anchor.Position.X); bw.Write(anchor.Position.Y); bw.Write(anchor.Position.Z);
                bw.Write(anchor.Normal.X);   bw.Write(anchor.Normal.Y);   bw.Write(anchor.Normal.Z);
                bw.Write(anchor.OwnerSlot);
            }

            return ms.ToArray();
        }

        private void ApplyInitialState(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            string sceneName   = br.ReadString();
            int checkpointID   = br.ReadInt32();
            byte activeMask    = br.ReadByte();

            Log.LogInfo($"Applying late-join state: scene={sceneName} checkpoint={checkpointID}");

            // Load correct scene if needed
            var currentScene = GameBridge.GetCurrentSceneName();
            if (sceneName != currentScene)
                GameBridge.LoadScene(sceneName);

            _net.Session?.SetCheckpoint(checkpointID);
            GameBridge.ActivateCheckpoint(checkpointID);

            // Spawn remote players at their positions
            for (int i = 0; i < CairnCoop.Protocol.Protocol.MaxPlayers; i++)
            {
                if ((activeMask & (1 << i)) == 0) continue;

                int slot        = br.ReadInt32();
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float yaw       = br.ReadSingle();
                float stamina   = br.ReadSingle();
                byte state      = br.ReadByte();
                ulong peerUID   = br.ReadUInt64();
                var peer        = new PeerID(peerUID);

                if (peer == _net.Transport?.LocalPeerID) continue; // skip self

                _net.RemotePlayers?.SpawnPlayer(peer, slot);
                // Position will be set on first snapshot received
            }

            // Restore anchors
            ushort anchorCount = br.ReadUInt16();
            for (int i = 0; i < anchorCount; i++)
            {
                ushort anchorID = br.ReadUInt16();
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float nx = br.ReadSingle(), ny = br.ReadSingle(), nz = br.ReadSingle();
                byte ownerSlot  = br.ReadByte();
                GameBridge.SpawnAnchorVisual(anchorID, new Vector3(px, py, pz), new Vector3(nx, ny, nz));
            }

            Log.LogInfo("Late-join state applied successfully.");
        }

        // ================================================================
        // COMPRESSION
        // ================================================================
        private static byte[] Compress(byte[] data)
        {
            using var ms  = new MemoryStream();
            using var gz  = new GZipStream(ms, CompressionMode.Compress);
            gz.Write(data, 0, data.Length);
            gz.Flush();
            return ms.ToArray();
        }

        private static byte[] Decompress(byte[] data)
        {
            using var ms   = new MemoryStream(data);
            using var gz   = new GZipStream(ms, CompressionMode.Decompress);
            using var out_ = new MemoryStream();
            gz.CopyTo(out_);
            return out_.ToArray();
        }
    }

    // ================================================================
    //  Fragment reassembly
    // ================================================================
    internal sealed class FragmentAssembler
    {
        private readonly byte[][] _fragments;
        private int _received;
        private readonly uint _totalBytes;

        public bool IsComplete => _received == _fragments.Length;

        public FragmentAssembler(int total, uint totalBytes)
        {
            _fragments = new byte[total][];
            _totalBytes = totalBytes;
        }

        public void AddFragment(int index, ReadOnlySpan<byte> data)
        {
            if (index < 0 || index >= _fragments.Length) return;
            if (_fragments[index] != null) return; // duplicate

            _fragments[index] = data.ToArray();
            _received++;
        }

        public byte[] GetData()
        {
            var result = new byte[_totalBytes];
            int offset = 0;
            foreach (var frag in _fragments)
            {
                frag.CopyTo(result, offset);
                offset += frag.Length;
            }
            return result;
        }
    }
}
