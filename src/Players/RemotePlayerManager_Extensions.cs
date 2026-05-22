// Extension methods to expose internal state needed by SpectatorController
namespace CairnCoop.Players
{
    public sealed partial class RemotePlayerManager
    {
        public RemotePlayer? GetPlayer(Network.PeerID peer)
            => _players.TryGetValue(peer, out var r) ? r : null;
    }

    public sealed partial class RemotePlayer
    {
        // Expose the pawn GameObject for the spectator camera
        public UnityEngine.GameObject? Pawn => _pawn;
    }
}
