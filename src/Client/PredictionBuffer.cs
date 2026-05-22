using CairnCoop.Protocol;

namespace CairnCoop.Client
{
    /// <summary>
    /// Ring-buffer storing (InputFrame, PlayerState) pairs for the last 128 ticks.
    /// Used for client-side prediction and rollback.
    /// </summary>
    public sealed class PredictionBuffer
    {
        private const int SIZE = 128;   // 128 ticks @ 20 Hz = 6.4 s history

        private readonly InputFramePacket?   [] _inputs = new InputFramePacket?[SIZE];
        private readonly PlayerStatePacket?  [] _states = new PlayerStatePacket?[SIZE];

        private static int Idx(uint tick) => (int)(tick % SIZE);

        public void SaveInput(uint tick, InputFramePacket input)
            => _inputs[Idx(tick)] = input;

        public void SaveState(uint tick, PlayerStatePacket state)
            => _states[Idx(tick)] = state;

        public InputFramePacket? GetInput(uint tick) => _inputs[Idx(tick)];
        public PlayerStatePacket? GetState(uint tick) => _states[Idx(tick)];

        public void OverwriteState(uint tick, PlayerStatePacket state)
            => _states[Idx(tick)] = state;

        public void Clear()
        {
            for (int i = 0; i < SIZE; i++) { _inputs[i] = null; _states[i] = null; }
        }
    }
}
