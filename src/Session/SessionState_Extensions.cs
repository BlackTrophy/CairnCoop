// Additional helpers for checkpoint mask building
namespace CairnCoop.Session
{
    public sealed partial class SessionState
    {
        // Bitmask of activated checkpoint IDs (up to 32 checkpoints)
        // Used in WorldSnapshotPacket and Late Join handshake.
        private int _activatedCheckpoints;

        public int BuildCheckpointMask()
        {
            return _activatedCheckpoints;
        }
    }
}
