using System;
using System.Threading.Tasks;
using CairnCoop.Protocol;

namespace CairnCoop.Network
{
    /// <summary>
    /// Unique identifier for a network peer.
    /// Wraps the underlying platform ID (SteamID64 or EOS ProductUserId hash).
    /// </summary>
    public readonly struct PeerID : IEquatable<PeerID>
    {
        public readonly ulong Value;
        public PeerID(ulong v) { Value = v; }
        public bool IsValid => Value != 0;
        public static PeerID Invalid => new(0);

        public bool Equals(PeerID other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is PeerID p && Equals(p);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Peer({Value:X16})";
        public static bool operator ==(PeerID a, PeerID b) => a.Value == b.Value;
        public static bool operator !=(PeerID a, PeerID b) => a.Value != b.Value;
    }

    public enum DisconnectReason
    {
        Unknown,
        Timeout,
        Kicked,
        ProtocolMismatch,
        HostMigration,
        UserDisconnect,
    }

    /// <summary>
    /// Transport abstraction — swap Steam ↔ EOS without touching higher layers.
    /// All events are raised on the Unity main thread.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Our own peer ID on this transport.</summary>
        PeerID LocalPeerID { get; }

        /// <summary>Fired when a new peer connects or completes handshake.</summary>
        event Action<PeerID> OnPeerConnected;

        /// <summary>Fired when a peer disconnects (timeout or explicit).</summary>
        event Action<PeerID, DisconnectReason> OnPeerDisconnected;

        /// <summary>
        /// Fired when a packet arrives.
        /// ArraySegment points into a reusable receive buffer —
        /// callers MUST process synchronously or copy the data.
        /// </summary>
        event Action<PeerID, Channel, ArraySegment<byte>> OnReceive;

        /// <summary>Create or join a session. lobbyCode = SteamID64 of host as string.</summary>
        Task<bool> ConnectAsync(string lobbyCode);

        /// <summary>
        /// Send a packet to a single peer.
        /// For Channel.Events and Channel.Connection, mode must be Reliable.
        /// </summary>
        void Send(PeerID peer, ReadOnlySpan<byte> data, Channel channel, SendMode mode = SendMode.Unreliable);

        /// <summary>Broadcast to all connected peers.</summary>
        void Broadcast(ReadOnlySpan<byte> data, Channel channel, SendMode mode = SendMode.Unreliable,
                       PeerID exclude = default);

        /// <summary>Gracefully disconnect a peer (sends goodbye before closing socket).</summary>
        void Disconnect(PeerID peer, DisconnectReason reason = DisconnectReason.UserDisconnect);

        /// <summary>
        /// Must be called from Unity Update() to pump incoming messages and
        /// flush send queues.
        /// </summary>
        void Tick();

        bool IsConnected { get; }
        int  ConnectedPeerCount { get; }
    }
}
