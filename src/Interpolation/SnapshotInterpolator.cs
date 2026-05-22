using System;
using System.Runtime.CompilerServices;

namespace CairnCoop.Interpolation
{
    /// <summary>
    /// Generic ring-buffer snapshot interpolator.
    /// T must be a struct with a float ServerTime field.
    /// Caller provides a Lerp delegate so this stays type-agnostic.
    /// </summary>
    public sealed class SnapshotInterpolator<T> where T : struct
    {
        private const int BUFFER_SIZE = 32;

        private readonly T[] _buffer = new T[BUFFER_SIZE];
        private readonly float[] _times = new float[BUFFER_SIZE];
        private int _head;
        private int _count;

        private readonly Func<T, T, float, T> _lerp;

        public SnapshotInterpolator(Func<T, T, float, T> lerp)
        {
            _lerp = lerp;
        }

        public void AddSnapshot(T snapshot, float serverTime)
        {
            int idx = _head % BUFFER_SIZE;
            _buffer[idx] = snapshot;
            _times[idx]  = serverTime;
            _head++;
            if (_count < BUFFER_SIZE) _count++;
        }

        /// <summary>
        /// Returns interpolated value at renderTime.
        /// renderTime = serverTime - 100ms interpolation buffer.
        /// Returns false if not enough data yet.
        /// </summary>
        public bool TryGetInterpolated(float renderTime, out T result)
        {
            result = default;
            if (_count < 2) return false;

            // Find the two snapshots bracketing renderTime
            // Buffer is not guaranteed sorted if clock jumps — linear scan
            float fromTime = float.MinValue, toTime = float.MaxValue;
            int fromIdx = -1, toIdx = -1;

            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + BUFFER_SIZE * 2) % BUFFER_SIZE;
                float t = _times[idx];
                if (t <= renderTime && t > fromTime) { fromTime = t; fromIdx = idx; }
                if (t >  renderTime && t < toTime)  { toTime   = t; toIdx   = idx; }
            }

            if (fromIdx < 0 || toIdx < 0)
            {
                // Extrapolate from most recent if behind renderTime
                int latestIdx = (_head - 1 + BUFFER_SIZE * 2) % BUFFER_SIZE;
                result = _buffer[latestIdx];
                return true;
            }

            float span = toTime - fromTime;
            float t2   = span > 0.0001f ? (renderTime - fromTime) / span : 0f;
            t2 = Math.Clamp(t2, 0f, 1f);

            result = _lerp(_buffer[fromIdx], _buffer[toIdx], t2);
            return true;
        }

        public void Clear()
        {
            _head  = 0;
            _count = 0;
        }
    }
}
