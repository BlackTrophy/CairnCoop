using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Logging;
using CairnCoop.Protocol;
using Steamworks;

namespace CairnCoop.Network
{
    /// <summary>
    /// Transport implementation using the Steam ISteamNetworking P2P API.
    ///
    /// WHY NOT ISteamNetworkingMessages (the newer API)?
    ///   SteamNetworkingMessagesSessionRequest_t contains a SteamNetworkingIdentity,
    ///   which is a union-like struct with string fields — non-blittable.
    ///   In an IL2CPP context, Il2CppInterop cannot build a delegate trampoline for
    ///   non-blittable struct parameters, so Callback&lt;SteamNetworkingMessagesSessionRequest_t&gt;
    ///   always throws at construction ("non-blittable struct … not supported").
    ///   This affects even direct Callback&lt;T&gt;.Create() calls because
    ///   com.rlabrecque.steamworks.net.dll is in BepInEx/interop/ (IL2CPP stub),
    ///   meaning the IL2CPP bridge is always involved.
    ///
    /// WHY ISteamNetworking WORKS:
    ///   SteamP2PSessionRequest_t    → { CSteamID m_steamIDRemote }            blittable ✓
    ///   SteamP2PSessionConnectFail_t → { CSteamID m_steamIDRemote; byte err } blittable ✓
    ///   The IL2CPP delegate trampoline handles these without issues.
    ///
    /// Protocol mapping:
    ///   Channel (0-6) → nChannel parameter (same integer value).
    ///   SendMode.Reliable   → EP2PSend.k_EP2PSendReliable
    ///   SendMode.Unreliable → EP2PSend.k_EP2PSendUnreliableNoDelay
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.SteamTransport");

        private const int RECV_BUFFER_SIZE = 4096;
        private const int CHANNELS_COUNT   = 7;   // Channel enum values 0..6

        // Reuse one receive buffer per Tick to minimise GC pressure.
        private readonly byte[]                    _recvBuffer     = new byte[RECV_BUFFER_SIZE];
        private readonly Dictionary<ulong, PeerID> _peerBySteamID  = new();
        private readonly List<PeerID>              _connectedPeers = new();
        private CSteamID                           _localSteamID;

        // ----------------------------------------------------------------
        // ITransport events
        // ----------------------------------------------------------------
        public event Action<PeerID>?                              OnPeerConnected;
        public event Action<PeerID, DisconnectReason>?            OnPeerDisconnected;
        public event Action<PeerID, Channel, ArraySegment<byte>>? OnReceive;

        public PeerID LocalPeerID        { get; private set; }
        public bool   IsConnected        => _connectedPeers.Count > 0;
        public int    ConnectedPeerCount => _connectedPeers.Count;

        // ----------------------------------------------------------------
        // Steam P2P callbacks — blittable structs, no IL2CPP delegate issues.
        // NOTE: com.rlabrecque.steamworks.net (UPM package) drops the "Steam"
        // prefix from callback structs: SteamP2PSessionRequest_t → P2PSessionRequest_t.
        // Keep managed delegate refs alive so the GC doesn't collect them before
        // DelegateSupport has pinned the native trampoline.
        // ----------------------------------------------------------------
        private Callback<P2PSessionRequest_t>?     _sessionRequestCb;
        private Callback<P2PSessionConnectFail_t>? _sessionFailedCb;
        private Action<P2PSessionRequest_t>?     _reqDelegate;
        private Action<P2PSessionConnectFail_t>? _failDelegate;

        // ----------------------------------------------------------------
        // Constructor
        // ----------------------------------------------------------------
        public SteamTransport()
        {
            if (!SteamAPI.IsSteamRunning())
                throw new InvalidOperationException("Steam is not running.");

            _localSteamID = SteamUser.GetSteamID();
            LocalPeerID   = new PeerID(_localSteamID.m_SteamID);

            // P2PSessionRequest_t / P2PSessionConnectFail_t contain only CSteamID (uint64)
            // and byte fields — fully blittable — so DelegateSupport.ConvertDelegate succeeds.
            // (The original ISteamNetworkingMessages callbacks contained SteamNetworkingIdentity,
            //  a non-blittable union, which is why they always failed.)
            // Keep the managed Action<> refs alive so the GC doesn't collect them before
            // the native trampoline is fully pinned by DelegateSupport.
            _reqDelegate  = OnSessionRequest;
            _failDelegate = OnSessionFailed;
            _sessionRequestCb = Callback<P2PSessionRequest_t>.Create(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<
                    Callback<P2PSessionRequest_t>.DispatchDelegate>(_reqDelegate));
            _sessionFailedCb = Callback<P2PSessionConnectFail_t>.Create(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<
                    Callback<P2PSessionConnectFail_t>.DispatchDelegate>(_failDelegate));

            Log.LogInfo($"SteamTransport initialized. Local SteamID: {_localSteamID}");
        }

        // ----------------------------------------------------------------
        // Connect — host's SteamID64 as string
        // ----------------------------------------------------------------
        public async Task<bool> ConnectAsync(string lobbyCode)
        {
            if (!ulong.TryParse(lobbyCode, out ulong hostSteamID))
            {
                Log.LogError($"Invalid lobby code: {lobbyCode}");
                return false;
            }

            // Register the host as a known peer so send/receive works immediately.
            RegisterPeer(new CSteamID(hostSteamID));
            Log.LogInfo($"Connecting to host {hostSteamID}…");

            // Give the Steam P2P handshake a moment before the caller sends the
            // application-level handshake packet.
            await Task.Delay(100);
            return true;
        }

        // ----------------------------------------------------------------
        // Send / Broadcast
        // ----------------------------------------------------------------
        public void Send(PeerID peer, ReadOnlySpan<byte> data, Channel channel, SendMode mode)
        {
            EP2PSend sendType = mode == SendMode.Reliable
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliableNoDelay;

            // IL2CPP interop needs a byte[] — copy is unavoidable through the bridge.
            // Packets are small (<= ~400 B) so the alloc is acceptable.
            byte[] buf = data.ToArray();
            SteamNetworking.SendP2PPacket(
                new CSteamID(peer.Value), buf, (uint)buf.Length, sendType, (int)channel);
        }

        public void Broadcast(ReadOnlySpan<byte> data, Channel channel, SendMode mode,
                              PeerID exclude = default)
        {
            foreach (var peer in _connectedPeers)
                if (peer != exclude)
                    Send(peer, data, channel, mode);
        }

        // ----------------------------------------------------------------
        // Tick — pump all 7 channels + run Steam callbacks
        // ----------------------------------------------------------------
        public void Tick()
        {
            for (int ch = 0; ch < CHANNELS_COUNT; ch++)
                PumpChannel((Channel)ch);

            SteamAPI.RunCallbacks();
        }

        private void PumpChannel(Channel channel)
        {
            // Drain all queued packets for this channel.
            while (SteamNetworking.IsP2PPacketAvailable(out uint msgSize, (int)channel))
            {
                uint readLen = Math.Min(msgSize, (uint)RECV_BUFFER_SIZE);

                if (!SteamNetworking.ReadP2PPacket(
                        _recvBuffer, readLen, out uint bytesRead, out CSteamID sender, (int)channel))
                    break;

                EnsurePeerRegistered(sender);
                OnReceive?.Invoke(
                    new PeerID(sender.m_SteamID),
                    channel,
                    new ArraySegment<byte>(_recvBuffer, 0, (int)bytesRead));
            }
        }

        // ----------------------------------------------------------------
        // Disconnect
        // ----------------------------------------------------------------
        public void Disconnect(PeerID peer,
                               DisconnectReason reason = DisconnectReason.UserDisconnect)
        {
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer.Value));
            RemovePeer(peer, reason);
        }

        // ----------------------------------------------------------------
        // Steam P2P callbacks
        // ----------------------------------------------------------------
        private void OnSessionRequest(P2PSessionRequest_t param)
        {
            // Auto-accept all incoming P2P sessions.
            // Application-level access control is enforced at the handshake layer.
            SteamNetworking.AcceptP2PSessionWithUser(param.m_steamIDRemote);
            EnsurePeerRegistered(param.m_steamIDRemote);
        }

        private void OnSessionFailed(P2PSessionConnectFail_t param)
        {
            ulong sid = param.m_steamIDRemote.m_SteamID;
            if (_peerBySteamID.TryGetValue(sid, out var peer))
            {
                Log.LogWarning($"P2P session failed with {sid} (error={param.m_eP2PSessionError})");
                RemovePeer(peer, DisconnectReason.Timeout);
            }
        }

        // ----------------------------------------------------------------
        // Peer registry
        // ----------------------------------------------------------------
        private void RegisterPeer(CSteamID steamID)
        {
            if (_peerBySteamID.ContainsKey(steamID.m_SteamID)) return;
            var peer = new PeerID(steamID.m_SteamID);
            _peerBySteamID[steamID.m_SteamID] = peer;
            _connectedPeers.Add(peer);
            OnPeerConnected?.Invoke(peer);
        }

        private void EnsurePeerRegistered(CSteamID steamID) => RegisterPeer(steamID);

        private void RemovePeer(PeerID peer, DisconnectReason reason)
        {
            _peerBySteamID.Remove(peer.Value);
            _connectedPeers.Remove(peer);
            OnPeerDisconnected?.Invoke(peer, reason);
        }

        public void Dispose()
        {
            foreach (var peer in _connectedPeers.ToArray())
                Disconnect(peer, DisconnectReason.UserDisconnect);
        }
    }
}
