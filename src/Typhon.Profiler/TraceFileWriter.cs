using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Writes a <c>.typhon-trace</c> binary trace file. Thread-safe for concurrent event appending via <see cref="WriteEvents"/> (called from the background
/// flush thread).
/// </summary>
/// <remarks>
/// File layout:
/// <code>
/// [TraceFileHeader]          64 bytes, fixed
/// [SystemDefinitionTable]    variable length, written once after header
/// [CompressedBlock]*         repeating: BlockHeader + LZ4-compressed TraceEvent[]
/// </code>
/// Each compressed block contains events from one or more ticks, delta-encoded for better compression.
/// </remarks>
public sealed class TraceFileWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly Stream _stream;
    private readonly byte[] _rawBuffer;
    private readonly byte[] _compressedBuffer;
    private bool _disposed;

    /// <summary>Maximum events per compressed block before forcing a flush.</summary>
    private const int MaxEventsPerBlock = 4096;

    /// <summary>Size of a single TraceEvent in bytes.</summary>
    private static readonly int EventSize = Unsafe.SizeOf<TraceEvent>();

    public TraceFileWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // Pre-allocate buffers for compression
        _rawBuffer = new byte[MaxEventsPerBlock * EventSize];
        _compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(_rawBuffer.Length)];
    }

    /// <summary>
    /// Writes the file header. Must be called exactly once before any other writes.
    /// </summary>
    public void WriteHeader(in TraceFileHeader header)
    {
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1));
        _stream.Write(span);
    }

    /// <summary>
    /// Writes the system definition table. Must be called exactly once after the header.
    /// </summary>
    public void WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord> systems)
    {
        _writer.Write((ushort)systems.Length);

        foreach (var sys in systems)
        {
            _writer.Write(sys.Index);

            var nameBytes = Encoding.UTF8.GetBytes(sys.Name);
            _writer.Write((byte)Math.Min(nameBytes.Length, 255));
            _writer.Write(nameBytes, 0, Math.Min(nameBytes.Length, 255));

            _writer.Write(sys.Type);
            _writer.Write(sys.Priority);
            _writer.Write(sys.IsParallel);
            _writer.Write(sys.TierFilter);

            // Predecessors
            _writer.Write((byte)sys.Predecessors.Length);
            foreach (var pred in sys.Predecessors)
            {
                _writer.Write(pred);
            }

            // Successors
            _writer.Write((byte)sys.Successors.Length);
            foreach (var succ in sys.Successors)
            {
                _writer.Write(succ);
            }
        }

        _writer.Flush();
    }

    /// <summary>Magic marker written before the trailing span name table to distinguish it from event blocks.</summary>
    public const uint SpanNameTableMagic = 0x4E_41_50_53; // "SPAN" little-endian

    /// <summary>
    /// Writes the span name intern table. Called after system definitions (empty) and at shutdown (populated).
    /// </summary>
    public void WriteSpanNames(IReadOnlyDictionary<int, string> spanNames)
    {
        _writer.Write(SpanNameTableMagic);
        _writer.Write((ushort)spanNames.Count);
        foreach (var kv in spanNames)
        {
            _writer.Write((ushort)kv.Key);
            var nameBytes = Encoding.UTF8.GetBytes(kv.Value);
            _writer.Write((byte)Math.Min(nameBytes.Length, 255));
            _writer.Write(nameBytes, 0, Math.Min(nameBytes.Length, 255));
        }

        _writer.Flush();
    }

    /// <summary>
    /// Writes a batch of trace events as a compressed block. Events are delta-encoded (timestamps relative to the first event in the block) before LZ4 compression.
    /// </summary>
    /// <param name="events">Events to write. Must not exceed <see cref="MaxEventsPerBlock"/>.</param>
    public void WriteEvents(ReadOnlySpan<TraceEvent> events)
    {
        if (events.IsEmpty)
        {
            return;
        }

        if (events.Length > MaxEventsPerBlock)
        {
            throw new ArgumentException($"Event count {events.Length} exceeds max {MaxEventsPerBlock}");
        }

        Span<byte> blockHeader = stackalloc byte[TraceBlockEncoder.BlockHeaderSize];
        var compressedSize = TraceBlockEncoder.EncodeBlock(events, _rawBuffer, _compressedBuffer, blockHeader);

        _stream.Write(blockHeader);
        _stream.Write(_compressedBuffer.AsSpan(0, compressedSize));
    }

    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
        _stream.Dispose();
    }
}
