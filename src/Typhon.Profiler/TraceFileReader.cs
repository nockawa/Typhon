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
/// Reads a <c>.typhon-trace</c> binary trace file. Provides sequential block-by-block access to trace events, restoring delta-encoded timestamps to absolute values.
/// </summary>
public sealed class TraceFileReader : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryReader _binaryReader;
    private byte[] _compressedBuffer;
    private byte[] _rawBuffer;
    private bool _disposed;

    private static readonly int EventSize = Unsafe.SizeOf<TraceEvent>();

    /// <summary>File header, available after <see cref="ReadHeader"/>.</summary>
    public TraceFileHeader Header { get; private set; }

    /// <summary>System definitions, available after <see cref="ReadSystemDefinitions"/>.</summary>
    public IReadOnlyList<SystemDefinitionRecord> Systems => _systems;

    private readonly List<SystemDefinitionRecord> _systems = [];

    public TraceFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _compressedBuffer = new byte[64 * 1024];
        _rawBuffer = new byte[64 * 1024];
    }

    /// <summary>
    /// Reads and validates the file header. Must be called first.
    /// </summary>
    /// <exception cref="InvalidDataException">If the magic or version is invalid.</exception>
    public TraceFileHeader ReadHeader()
    {
        Span<byte> headerBytes = stackalloc byte[Unsafe.SizeOf<TraceFileHeader>()];
        _stream.ReadExactly(headerBytes);

        Header = MemoryMarshal.Read<TraceFileHeader>(headerBytes);

        if (Header.Magic != TraceFileHeader.MagicValue)
        {
            throw new InvalidDataException(
                $"Invalid trace file magic: 0x{Header.Magic:X8} (expected 0x{TraceFileHeader.MagicValue:X8})");
        }

        if (Header.Version > TraceFileHeader.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported trace file version: {Header.Version} (max supported: {TraceFileHeader.CurrentVersion})");
        }

        return Header;
    }

    /// <summary>
    /// Reads the system definition table. Must be called after <see cref="ReadHeader"/>.
    /// </summary>
    public IReadOnlyList<SystemDefinitionRecord> ReadSystemDefinitions()
    {
        _systems.Clear();

        var count = _binaryReader.ReadUInt16();

        for (var i = 0; i < count; i++)
        {
            var index = _binaryReader.ReadUInt16();

            var nameLen = _binaryReader.ReadByte();
            var nameBytes = _binaryReader.ReadBytes(nameLen);
            var name = Encoding.UTF8.GetString(nameBytes);

            var type = _binaryReader.ReadByte();
            var priority = _binaryReader.ReadByte();
            var isParallel = _binaryReader.ReadBoolean();
            var tierFilter = _binaryReader.ReadByte();

            var predCount = _binaryReader.ReadByte();
            var predecessors = new ushort[predCount];
            for (var p = 0; p < predCount; p++)
            {
                predecessors[p] = _binaryReader.ReadUInt16();
            }

            var succCount = _binaryReader.ReadByte();
            var successors = new ushort[succCount];
            for (var s = 0; s < succCount; s++)
            {
                successors[s] = _binaryReader.ReadUInt16();
            }

            _systems.Add(new SystemDefinitionRecord
            {
                Index = index,
                Name = name,
                Type = type,
                Priority = priority,
                IsParallel = isParallel,
                TierFilter = tierFilter,
                Predecessors = predecessors,
                Successors = successors
            });
        }

        return _systems;
    }

    /// <summary>Span name intern table, available after <see cref="ReadSpanNames"/>.</summary>
    public IReadOnlyDictionary<int, string> SpanNames => _spanNames;

    private readonly Dictionary<int, string> _spanNames = new();

    /// <summary>
    /// Reads the span name intern table. Must be called after <see cref="ReadSystemDefinitions"/>.
    /// </summary>
    public IReadOnlyDictionary<int, string> ReadSpanNames()
    {
        var magic = _binaryReader.ReadUInt32();
        if (magic != TraceFileWriter.SpanNameTableMagic)
        {
            // Not a span name table — seek back and return
            _stream.Position -= 4;
            return _spanNames;
        }

        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var id = _binaryReader.ReadUInt16();
            var nameLen = _binaryReader.ReadByte();
            var nameBytes = _binaryReader.ReadBytes(nameLen);
            _spanNames[id] = Encoding.UTF8.GetString(nameBytes);
        }

        return _spanNames;
    }

    /// <summary>
    /// Reads the next compressed block of events. Returns false if no more blocks.
    /// Events are returned with absolute timestamps (delta decoding applied).
    /// </summary>
    /// <param name="events">Receives the decoded events. Empty if no more blocks.</param>
    /// <returns>True if a block was read, false if end of stream.</returns>
    public bool ReadNextBlock(out TraceEvent[] events)
    {
        events = [];

        // Read block header: uncompressed (4B) + compressed (4B) + event count (4B)
        Span<byte> blockHeader = stackalloc byte[12];
        var bytesRead = _stream.Read(blockHeader);
        if (bytesRead == 0)
        {
            return false;
        }

        if (bytesRead < 4)
        {
            return false; // Not enough data for any header
        }

        // Check if this is a span name table marker (appears before events and at end of file)
        var firstWord = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
        if (firstWord == TraceFileWriter.SpanNameTableMagic)
        {
            // Seek back and read (skip) the span name table, then continue looking for event blocks
            _stream.Position -= bytesRead;
            ReadSpanNames();
            return ReadNextBlock(out events); // recurse to try the next block
        }

        if (bytesRead < 12)
        {
            return false; // Truncated block — stop reading
        }

        var (uncompressedSize, compressedSize, eventCount) = TraceBlockEncoder.ReadBlockHeader(blockHeader);

        // Grow buffers if needed
        if (_compressedBuffer.Length < compressedSize)
        {
            _compressedBuffer = new byte[compressedSize];
        }

        if (_rawBuffer.Length < uncompressedSize)
        {
            _rawBuffer = new byte[uncompressedSize];
        }

        // Read compressed data
        _stream.ReadExactly(_compressedBuffer.AsSpan(0, compressedSize));

        // Decompress + undo delta encoding (via shared encoder)
        events = new TraceEvent[eventCount];
        TraceBlockEncoder.DecodeBlock(
            _compressedBuffer.AsSpan(0, compressedSize),
            uncompressedSize,
            eventCount,
            _rawBuffer,
            events);

        return true;
    }

    /// <summary>
    /// Reads all events from the file (after header + system defs + span names) into a single list.
    /// Also reads trailing span name table written at shutdown.
    /// </summary>
    public List<TraceEvent> ReadAllEvents()
    {
        var all = new List<TraceEvent>();

        while (ReadNextBlock(out var events))
        {
            all.AddRange(events);
        }

        // Try to read trailing span name table (written at shutdown with all interned names)
        try
        {
            if (_stream.Position < _stream.Length)
            {
                ReadSpanNames();
            }
        }
        catch
        {
            // No trailing span names — the initial table (or none) is used
        }

        return all;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _binaryReader.Dispose();
        _stream.Dispose();
    }
}
