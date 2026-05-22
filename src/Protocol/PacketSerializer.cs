using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CairnCoop.Protocol
{
    /// <summary>
    /// Zero-allocation binary serialization using MemoryMarshal.
    /// All structs are Pack=1 — no padding, direct cast from byte span.
    /// </summary>
    public static class PacketSerializer
    {
        // ----------------------------------------------------------------
        // Write helpers
        // ----------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Write<T>(Span<byte> dest, in T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            MemoryMarshal.Write(dest, ref Unsafe.AsRef(in value));
            return size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteHeader(Span<byte> dest, PacketType type, byte playerID, byte flags = 0)
        {
            var h = new PacketHeader(type, playerID, flags);
            return Write(dest, h);
        }

        // ----------------------------------------------------------------
        // Read helpers
        // ----------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ReadOnlySpan<byte> src) where T : unmanaged
            => MemoryMarshal.Read<T>(src);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryRead<T>(ReadOnlySpan<byte> src, out T value) where T : unmanaged
        {
            if (src.Length < Unsafe.SizeOf<T>()) { value = default; return false; }
            value = MemoryMarshal.Read<T>(src);
            return true;
        }

        public static bool TryReadHeader(ReadOnlySpan<byte> src, out PacketHeader header)
            => TryRead(src, out header);

        // ----------------------------------------------------------------
        // WorldSnapshot: variable-length due to active-player mask
        // ----------------------------------------------------------------

        public static unsafe int WriteWorldSnapshot(
            Span<byte> dest, uint tick, uint serverMs,
            byte activeMask, byte cpMask,
            ReadOnlySpan<PlayerStatePacket> players)
        {
            int offset = 0;
            // We don't write the header here — caller does, with their playerID
            offset += Write(dest.Slice(offset), tick);
            offset += Write(dest.Slice(offset), serverMs);
            dest[offset++] = activeMask;
            dest[offset++] = cpMask;
            dest[offset++] = 0; // pad
            dest[offset++] = 0;

            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                if ((activeMask & (1 << i)) != 0)
                {
                    offset += Write(dest.Slice(offset), players[i]);
                }
            }
            return offset;
        }

        public static bool TryReadWorldSnapshot(
            ReadOnlySpan<byte> src,
            out uint tick, out uint serverMs,
            out byte activeMask, out byte cpMask,
            Span<PlayerStatePacket> outPlayers)
        {
            tick = 0; serverMs = 0; activeMask = 0; cpMask = 0;
            int offset = 0;
            if (src.Length < 8) return false;

            tick      = MemoryMarshal.Read<uint>(src.Slice(offset)); offset += 4;
            serverMs  = MemoryMarshal.Read<uint>(src.Slice(offset)); offset += 4;
            activeMask = src[offset++];
            cpMask     = src[offset++];
            offset += 2; // pad

            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                if ((activeMask & (1 << i)) != 0)
                {
                    int sz = Unsafe.SizeOf<PlayerStatePacket>();
                    if (src.Length - offset < sz) return false;
                    outPlayers[i] = MemoryMarshal.Read<PlayerStatePacket>(src.Slice(offset));
                    offset += sz;
                }
            }
            return true;
        }

        // ----------------------------------------------------------------
        // Late-join fragmentation
        // ----------------------------------------------------------------

        public static int FragmentCount(int totalBytes)
            => (totalBytes + Protocol.MaxFragmentPayload - 1) / Protocol.MaxFragmentPayload;

        public static ReadOnlyMemory<byte> GetFragment(ReadOnlyMemory<byte> data, int fragIndex)
        {
            int start = fragIndex * Protocol.MaxFragmentPayload;
            int len   = Math.Min(Protocol.MaxFragmentPayload, data.Length - start);
            return data.Slice(start, len);
        }
    }
}
