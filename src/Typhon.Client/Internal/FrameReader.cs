using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Typhon.Client.Internal;

/// <summary>
/// Reads length-prefixed MemoryPack frames from a stream. Reuses a single buffer that grows as needed.
/// </summary>
/// <remarks>
/// Wire format: <c>[4-byte payload length LE][N bytes MemoryPack payload]</c>.
/// Handles TCP fragmentation by looping on partial reads.
/// </remarks>
internal sealed class FrameReader
{
    private byte[] _buffer;

    internal FrameReader(int initialCapacity)
    {
        _buffer = new byte[Math.Max(initialCapacity, 256)];
    }

    /// <summary>
    /// Read one complete frame from the stream. Blocks until the frame is fully received.
    /// </summary>
    /// <returns>A <see cref="ReadOnlyMemory{T}"/> over the payload bytes (valid until the next call).</returns>
    /// <exception cref="EndOfStreamException">The connection was closed before the frame completed.</exception>
    /// <exception cref="InvalidDataException">The length prefix exceeds the sanity limit (16 MB).</exception>
    internal ReadOnlyMemory<byte> ReadFrame(Stream stream)
    {
        // Read 4-byte length prefix
        ReadExact(stream, _buffer, 0, 4);
        var payloadLength = BitConverter.ToInt32(_buffer, 0);

        if (payloadLength <= 0 || payloadLength > 16 * 1024 * 1024) // 16 MB sanity limit
        {
            throw new InvalidDataException($"Invalid frame length: {payloadLength}");
        }

        EnsureCapacity(payloadLength);
        ReadExact(stream, _buffer, 0, payloadLength);

        return new ReadOnlyMemory<byte>(_buffer, 0, payloadLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
        {
            return;
        }

        // Grow to next power of two
        var newSize = _buffer.Length;
        while (newSize < required)
        {
            newSize <<= 1;
        }

        _buffer = new byte[newSize];
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var received = 0;
        while (received < count)
        {
            var n = stream.Read(buffer, offset + received, count - received);
            if (n == 0)
            {
                throw new EndOfStreamException("Connection closed");
            }

            received += n;
        }
    }
}
