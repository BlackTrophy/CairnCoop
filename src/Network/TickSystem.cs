namespace CairnCoop.Network
{
    /// <summary>
    /// Monotone tick counter + server-time estimation.
    /// Tick = 20 Hz snapshot unit.
    /// </summary>
    public sealed class TickSystem
    {
        public uint  CurrentTick   { get; private set; }
        public float ServerTime    { get; private set; }
        public float LocalTime     { get; private set; }
        public float ClockOffset   { get; private set; } // local - server

        private const float TICK_RATE = 20f;
        private const float TICK_DT   = 1f / TICK_RATE;
        private float _accumulator;

        public void Advance(float deltaTime)
        {
            LocalTime  += deltaTime;
            ServerTime += deltaTime; // corrected by SyncServerTime

            _accumulator += deltaTime;
            while (_accumulator >= TICK_DT)
            {
                _accumulator -= TICK_DT;
                CurrentTick++;
            }
        }

        /// <summary>
        /// Called when we receive a world snapshot from the host.
        /// Adjusts server-time estimate using simple clock sync.
        /// </summary>
        public void SyncServerTime(uint serverTick, uint serverTimeMs)
        {
            float serverTimeSec = serverTimeMs / 1000f;
            // Low-pass filter to avoid jitter
            float measured = serverTimeSec;
            ServerTime = ServerTime * 0.9f + measured * 0.1f;
            ClockOffset = LocalTime - ServerTime;
        }

        public float RenderTime => ServerTime - 0.1f; // 100 ms interpolation buffer
    }
}
