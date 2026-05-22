using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;

namespace CairnCoop.Session
{
    public enum SessionPhase { Lobby, Loading, InGame, PostGame }

    public sealed class RejoinToken
    {
        public ulong  PlatformUID;
        public int    LastCheckpointID;
        public float  ExpiresAt;  // UnityEngine.Time.time
    }

    public sealed class PlayerSlot
    {
        public int    SlotIndex;
        public PeerID PeerID;
        public ulong  PlatformUID;   // SteamID64
        public string DisplayName = "";
        public bool   IsConnected;
        public bool   IsHost;
        public uint   RTT;           // round-trip time ms
        public int    LastCheckpointID;
    }

    public sealed partial class SessionState
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.SessionState");

        public PeerID        LocalPeerID  { get; }
        public PeerID        HostPeerID   { get; set; }
        public SessionPhase  Phase        { get; private set; } = SessionPhase.Lobby;
        public bool          IsHost       => HostPeerID == LocalPeerID;
        public int           LocalSlot    { get; private set; } = -1;
        public int           MaxPlayers   { get; private set; } = 8;
        public int           CurrentCheckpointID { get; private set; }

        private readonly PlayerSlot[] _slots;
        private readonly Dictionary<PeerID, RejoinToken> _rejoinTokens = new();

        public SessionState(PeerID localPeerID)
        {
            LocalPeerID = localPeerID;
            _slots = new PlayerSlot[CairnCoop.Protocol.Protocol.MaxPlayers];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = new PlayerSlot { SlotIndex = i };
        }

        // ----------------------------------------------------------------
        // Host setup
        // ----------------------------------------------------------------
        public void BeginHost(PeerID hostPeer, int maxPlayers)
        {
            HostPeerID = hostPeer;
            MaxPlayers = Math.Clamp(maxPlayers, 2, CairnCoop.Protocol.Protocol.MaxPlayers);
            Phase      = SessionPhase.Lobby;

            // Assign slot 0 to host
            _slots[0].PeerID      = hostPeer;
            _slots[0].PlatformUID = hostPeer.Value;
            _slots[0].IsConnected = true;
            _slots[0].IsHost      = true;
            LocalSlot = 0;

            Log.LogInfo($"Session started. Host slot=0, maxPlayers={MaxPlayers}");
        }

        public void BeginHostMigration(PeerID newHost)
        {
            HostPeerID = newHost;
            int slot = GetSlotByPeer(newHost);
            if (slot >= 0) _slots[slot].IsHost = true;
        }

        // ----------------------------------------------------------------
        // Slot management
        // ----------------------------------------------------------------
        public int AssignSlot(PeerID peer, ulong platformUID)
        {
            for (int i = 1; i < MaxPlayers; i++) // slot 0 = host
            {
                if (!_slots[i].IsConnected)
                {
                    _slots[i].PeerID      = peer;
                    _slots[i].PlatformUID = platformUID;
                    _slots[i].IsConnected = true;
                    _slots[i].IsHost      = false;
                    Phase = SessionPhase.InGame;
                    return i;
                }
            }
            return -1; // full
        }

        public void SetLocalSlot(byte slot)
        {
            LocalSlot = slot;
            _slots[slot].PeerID      = LocalPeerID;
            _slots[slot].PlatformUID = LocalPeerID.Value;
            _slots[slot].IsConnected = true;
            Phase = SessionPhase.InGame;
        }

        public void RegisterRemotePlayer(PeerID peer, byte slot)
        {
            _slots[slot].PeerID      = peer;
            _slots[slot].PlatformUID = peer.Value;
            _slots[slot].IsConnected = true;
        }

        public void MarkDisconnected(PeerID peer, DisconnectReason reason)
        {
            int slot = GetSlotByPeer(peer);
            if (slot < 0) return;

            // Save rejoin token (valid 10 min)
            _rejoinTokens[peer] = new RejoinToken
            {
                PlatformUID       = _slots[slot].PlatformUID,
                LastCheckpointID  = _slots[slot].LastCheckpointID,
                ExpiresAt         = UnityEngine.Time.time + 600f,
            };

            _slots[slot].IsConnected = false;
        }

        public void RemoveSlot(byte slot)
        {
            _slots[slot].PeerID      = PeerID.Invalid;
            _slots[slot].IsConnected = false;
        }

        // ----------------------------------------------------------------
        // Lookup helpers
        // ----------------------------------------------------------------
        public int GetSlotByPeer(PeerID peer)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].PeerID == peer) return i;
            return -1;
        }

        public PeerID GetPeerBySlot(int slot)
            => slot >= 0 && slot < _slots.Length ? _slots[slot].PeerID : PeerID.Invalid;

        public PlayerSlot? GetSlot(int index)
            => index >= 0 && index < _slots.Length ? _slots[index] : null;

        public IEnumerable<PlayerSlot> ConnectedSlots
            => _slots.Where(s => s.IsConnected);

        public IEnumerable<PeerID> ConnectedPeers
            => ConnectedSlots.Select(s => s.PeerID);

        // ----------------------------------------------------------------
        // Checkpoint
        // ----------------------------------------------------------------
        public void SetCheckpoint(int cpID)
        {
            CurrentCheckpointID = cpID;
            if (cpID >= 0 && cpID < 32)
                _activatedCheckpoints |= (1 << cpID);
            foreach (var s in ConnectedSlots) s.LastCheckpointID = cpID;
        }

        // ----------------------------------------------------------------
        // Rejoin
        // ----------------------------------------------------------------
        public bool TryGetRejoinToken(PeerID peer, out RejoinToken token)
        {
            // Clean expired
            var expired = _rejoinTokens
                .Where(kv => UnityEngine.Time.time > kv.Value.ExpiresAt)
                .Select(kv => kv.Key).ToList();
            foreach (var k in expired) _rejoinTokens.Remove(k);

            return _rejoinTokens.TryGetValue(peer, out token!);
        }

        // ----------------------------------------------------------------
        // Host election (lowest PeerID wins)
        // ----------------------------------------------------------------
        public PeerID ElectNewHost()
            => ConnectedPeers.OrderBy(p => p.Value).FirstOrDefault();

        // ----------------------------------------------------------------
        // RTT
        // ----------------------------------------------------------------
        public void UpdateRTT(PeerID peer, uint rttMs)
        {
            int slot = GetSlotByPeer(peer);
            if (slot >= 0) _slots[slot].RTT = rttMs;
        }

        // ----------------------------------------------------------------
        // Build active-player bitmask for snapshots
        // ----------------------------------------------------------------
        public byte BuildActiveMask()
        {
            byte mask = 0;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].IsConnected) mask |= (byte)(1 << i);
            return mask;
        }
    }
}
