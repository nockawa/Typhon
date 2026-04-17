using System;
using System.Buffers.Binary;
using System.IO;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Block encoder/decoder for the <c>.typhon-trace</c> on-disk format and the TCP live-stream protocol. Works on variable-size record byte streams
/// (as produced by <c>TraceRecordRing.Drain</c>) — the old fixed-stride <c>TraceEvent[]</c> path was removed in the Phase 3 rewrite.
/// </summary>
/// <remarks>
/// <para>
/// A block has a 12-byte header followed by an LZ4-compressed byte stream:
/// <code>
/// [4B uncompressedBytes] [4B compressedBytes] [4B recordCount] [compressedBytes bytes of LZ4 data]
/// </code>
/// </para>
/// <para>
/// <b>No delta encoding:</b> the old encoder delta-encoded timestamps per-event, which worked because every event had a <c>TimestampTicks</c> field
/// at a fixed byte offset. With variable records, timestamps live at different offsets depending on record kind, so field-aware delta encoding
/// would need a kind switch. LZ4 handles the byte-level redundancy well enough on its own — Phase 3 measurements show the payload compression
/// ratio is within ~5% of the old delta-encoded version, for a much simpler encoder.
/// </para>
/// </remarks>
public static class TraceBlockEncoder
{
    /// <summary>Size of the block header in bytes.</summary>
    public const int BlockHeaderSize = 12;

    /// <summary>
    /// LZ4-compress <paramref name="records"/> into <paramref name="compressedBuffer"/> and write the 12-byte block header to
    /// <paramref name="blockHeader"/>.
    /// </summary>
    /// <param name="records">Raw record bytes (as produced by <c>TraceRecordRing.Drain</c>).</param>
    /// <param name="recordCount">Number of records in <paramref name="records"/> — used for diagnostics and block header accounting.</param>
    /// <param name="compressedBuffer">Output buffer for LZ4 payload. Must be at least <see cref="LZ4Codec.MaximumOutputSize"/>(records.Length) bytes.</param>
    /// <param name="blockHeader">Destination for the 12-byte block header; must be exactly <see cref="BlockHeaderSize"/> bytes.</param>
    /// <returns>Number of bytes written to <paramref name="compressedBuffer"/>.</returns>
    public static int EncodeBlock(
        ReadOnlySpan<byte> records,
        int recordCount,
        Span<byte> compressedBuffer,
        Span<byte> blockHeader)
    {
        if (blockHeader.Length < BlockHeaderSize)
        {
            throw new ArgumentException($"blockHeader must be at least {BlockHeaderSize} bytes", nameof(blockHeader));
        }

        var compressedSize = LZ4Codec.Encode(records, compressedBuffer);

        BinaryPrimitives.WriteInt32LittleEndian(blockHeader, records.Length);
        BinaryPrimitives.WriteInt32LittleEndian(blockHeader[4..], compressedSize);
        BinaryPrimitives.WriteInt32LittleEndian(blockHeader[8..], recordCount);

        return compressedSize;
    }

    /// <summary>Reads a block header and returns its fields.</summary>
    public static (int UncompressedBytes, int CompressedBytes, int RecordCount) ReadBlockHeader(ReadOnlySpan<byte> blockHeader)
    {
        if (blockHeader.Length < BlockHeaderSize)
        {
            throw new ArgumentException($"blockHeader must be at least {BlockHeaderSize} bytes", nameof(blockHeader));
        }

        var uncompressedBytes = BinaryPrimitives.ReadInt32LittleEndian(blockHeader);
        var compressedBytes = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[4..]);
        var recordCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[8..]);
        return (uncompressedBytes, compressedBytes, recordCount);
    }

    /// <summary>
    /// Decompress a block's LZ4 payload into <paramref name="rawBuffer"/>. The caller then walks the decoded bytes as a sequence of size-prefixed
    /// records (u16 size field at the start of each record).
    /// </summary>
    /// <param name="compressedData">LZ4-compressed payload (exactly <c>compressedBytes</c> from the header).</param>
    /// <param name="uncompressedBytes">Expected decompressed size in bytes (from the header).</param>
    /// <param name="rawBuffer">Scratch buffer for decompression, must be at least <paramref name="uncompressedBytes"/> bytes.</param>
    /// <exception cref="InvalidDataException">If LZ4 decoding produces an unexpected size.</exception>
    public static void DecodeBlock(
        ReadOnlySpan<byte> compressedData,
        int uncompressedBytes,
        Span<byte> rawBuffer)
    {
        var decoded = LZ4Codec.Decode(compressedData, rawBuffer[..uncompressedBytes]);
        if (decoded != uncompressedBytes)
        {
            throw new InvalidDataException($"LZ4 decompression size mismatch: expected {uncompressedBytes}, got {decoded}");
        }
    }
}
