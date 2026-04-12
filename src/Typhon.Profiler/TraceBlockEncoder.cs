using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Shared block encoder/decoder for the <c>.typhon-trace</c> on-disk format and the TCP live-stream protocol.
/// </summary>
/// <remarks>
/// <para>
/// A block consists of a 12-byte header followed by LZ4-compressed, delta-encoded events:
/// <code>
/// [4B uncompressedSize] [4B compressedSize] [4B eventCount] [compressedSize bytes LZ4 data]
/// </code>
/// </para>
/// <para>
/// Both the file writer and the TCP live-stream inspector use this encoder so they emit byte-identical block bodies. Changes to the delta-encoding
/// or compression scheme are made in one place.
/// </para>
/// </remarks>
public static class TraceBlockEncoder
{
    /// <summary>Size of the block header in bytes.</summary>
    public const int BlockHeaderSize = 12;

    /// <summary>Size of one <see cref="TraceEvent"/> in bytes (blittable; 32 bytes).</summary>
    public static readonly int EventSize = Unsafe.SizeOf<TraceEvent>();

    /// <summary>
    /// Delta-encodes a span of events into <paramref name="rawBuffer"/>, LZ4-compresses into <paramref name="compressedBuffer"/>,
    /// and writes the 12-byte block header to <paramref name="blockHeader"/>.
    /// </summary>
    /// <param name="events">Events to encode (absolute timestamps).</param>
    /// <param name="rawBuffer">Scratch buffer, must be at least <c>events.Length * EventSize</c> bytes.</param>
    /// <param name="compressedBuffer">Output buffer for compressed data, must be at least <c>LZ4Codec.MaximumOutputSize(events.Length * EventSize)</c> bytes.</param>
    /// <param name="blockHeader">Destination for the 12-byte block header; must be exactly 12 bytes.</param>
    /// <returns>Number of bytes written to <paramref name="compressedBuffer"/>.</returns>
    public static int EncodeBlock(
        ReadOnlySpan<TraceEvent> events,
        Span<byte> rawBuffer,
        Span<byte> compressedBuffer,
        Span<byte> blockHeader)
    {
        if (blockHeader.Length < BlockHeaderSize)
        {
            throw new ArgumentException($"blockHeader must be at least {BlockHeaderSize} bytes", nameof(blockHeader));
        }

        var rawSize = events.Length * EventSize;
        var rawSpan = rawBuffer[..rawSize];
        var eventDest = MemoryMarshal.Cast<byte, TraceEvent>(rawSpan);

        events.CopyTo(eventDest);

        // Apply delta encoding in reverse order so we don't overwrite source data
        for (var i = eventDest.Length - 1; i > 0; i--)
        {
            eventDest[i].TimestampTicks -= eventDest[i - 1].TimestampTicks;
        }

        var compressedSize = LZ4Codec.Encode(rawSpan, compressedBuffer);

        BinaryPrimitives.WriteInt32LittleEndian(blockHeader, rawSize);
        BinaryPrimitives.WriteInt32LittleEndian(blockHeader[4..], compressedSize);
        BinaryPrimitives.WriteInt32LittleEndian(blockHeader[8..], events.Length);

        return compressedSize;
    }

    /// <summary>
    /// Reads a block header and returns its fields.
    /// </summary>
    public static (int UncompressedSize, int CompressedSize, int EventCount) ReadBlockHeader(ReadOnlySpan<byte> blockHeader)
    {
        if (blockHeader.Length < BlockHeaderSize)
        {
            throw new ArgumentException($"blockHeader must be at least {BlockHeaderSize} bytes", nameof(blockHeader));
        }

        var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(blockHeader);
        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[4..]);
        var eventCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[8..]);
        return (uncompressedSize, compressedSize, eventCount);
    }

    /// <summary>
    /// Decompresses a block's LZ4 payload and restores absolute timestamps by undoing delta encoding.
    /// </summary>
    /// <param name="compressedData">LZ4-compressed payload (exactly <c>compressedSize</c> bytes from the header).</param>
    /// <param name="uncompressedSize">Expected decompressed size in bytes (from the header).</param>
    /// <param name="eventCount">Number of events in the block (from the header).</param>
    /// <param name="rawBuffer">Scratch buffer for decompression, must be at least <paramref name="uncompressedSize"/> bytes.</param>
    /// <param name="eventsOut">Destination for decoded events; must hold at least <paramref name="eventCount"/> elements.</param>
    /// <exception cref="InvalidDataException">If LZ4 decoding produces an unexpected size.</exception>
    public static void DecodeBlock(
        ReadOnlySpan<byte> compressedData,
        int uncompressedSize,
        int eventCount,
        Span<byte> rawBuffer,
        Span<TraceEvent> eventsOut)
    {
        var decoded = LZ4Codec.Decode(compressedData, rawBuffer[..uncompressedSize]);
        if (decoded != uncompressedSize)
        {
            throw new InvalidDataException($"LZ4 decompression size mismatch: expected {uncompressedSize}, got {decoded}");
        }

        var rawEvents = MemoryMarshal.Cast<byte, TraceEvent>(rawBuffer[..uncompressedSize]);
        rawEvents[..eventCount].CopyTo(eventsOut);

        // Undo delta encoding: cumulative sum of timestamps
        for (var i = 1; i < eventCount; i++)
        {
            eventsOut[i].TimestampTicks += eventsOut[i - 1].TimestampTicks;
        }
    }
}
