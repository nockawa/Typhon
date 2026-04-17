using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Profiler;

/// <summary>
/// Reads a <c>.typhon-trace</c> binary trace file (format v3 — variable-size typed records). Provides sequential block-by-block access,
/// yielding a raw byte span that the caller walks as a sequence of size-prefixed records.
/// </summary>
/// <remarks>
/// <para>
/// Typical use pattern:
/// <code>
/// using var reader = new TraceFileReader(stream);
/// reader.ReadHeader();
/// reader.ReadSystemDefinitions();
/// reader.ReadArchetypes();
/// reader.ReadComponentTypes();
/// while (reader.ReadNextBlock(out var records))
/// {
///     var pos = 0;
///     while (pos &lt; records.Length)
///     {
///         var size = BinaryPrimitives.ReadUInt16LittleEndian(records.Span[pos..]);
///         var kind = (TraceEventKind)records.Span[pos + 2];
///         // dispatch to typed codec based on kind
///         pos += size;
///     }
/// }
/// reader.ReadSpanNames();  // optional trailing table
/// </code>
/// </para>
/// </remarks>
public sealed class TraceFileReader : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryReader _binaryReader;
    private byte[] _compressedBuffer;
    /// <summary>
    /// Pooled buffer handed out via <see cref="ReadNextBlock"/>. The block is LZ4-decoded directly into this buffer — no staging
    /// copy. The returned <see cref="ReadOnlyMemory{Byte}"/> is valid only until the next call to <see cref="ReadNextBlock"/> or
    /// <see cref="Dispose"/>, at which point this buffer is returned to <see cref="ArrayPool{T}.Shared"/> and a new one is rented.
    /// Null when no block has been read yet.
    /// </summary>
    private byte[] _rentedBlock;
    private bool _disposed;

    /// <summary>File header, available after <see cref="ReadHeader"/>.</summary>
    public TraceFileHeader Header { get; private set; }

    /// <summary>System definitions, available after <see cref="ReadSystemDefinitions"/>.</summary>
    public IReadOnlyList<SystemDefinitionRecord> Systems => _systems;
    private readonly List<SystemDefinitionRecord> _systems = [];

    /// <summary>Archetype table, available after <see cref="ReadArchetypes"/>.</summary>
    public IReadOnlyList<ArchetypeRecord> Archetypes => _archetypes;
    private readonly List<ArchetypeRecord> _archetypes = [];

    /// <summary>Component type table, available after <see cref="ReadComponentTypes"/>.</summary>
    public IReadOnlyList<ComponentTypeRecord> ComponentTypes => _componentTypes;
    private readonly List<ComponentTypeRecord> _componentTypes = [];

    /// <summary>Span name intern table, available after <see cref="ReadSpanNames"/>.</summary>
    public IReadOnlyDictionary<int, string> SpanNames => _spanNames;
    private readonly Dictionary<int, string> _spanNames = new();

    public TraceFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _compressedBuffer = new byte[64 * 1024];
    }

    /// <summary>Reads and validates the file header. Must be called first.</summary>
    /// <exception cref="InvalidDataException">If magic or version is wrong.</exception>
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
        if (Header.Version != TraceFileHeader.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported trace file version: {Header.Version}. This build only reads version {TraceFileHeader.CurrentVersion}. " +
                "The .typhon-trace format was rewritten in the typed-event release; older files are unreadable.");
        }
        return Header;
    }

    /// <summary>Reads the system definition table. Call after <see cref="ReadHeader"/>.</summary>
    public IReadOnlyList<SystemDefinitionRecord> ReadSystemDefinitions()
    {
        _systems.Clear();
        var count = _binaryReader.ReadUInt16();

        for (var i = 0; i < count; i++)
        {
            var index = _binaryReader.ReadUInt16();
            var name = ReadShortString();
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

    /// <summary>Reads the archetype table. Call after <see cref="ReadSystemDefinitions"/>.</summary>
    public IReadOnlyList<ArchetypeRecord> ReadArchetypes()
    {
        _archetypes.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var archetypeId = _binaryReader.ReadUInt16();
            var name = ReadShortString();
            _archetypes.Add(new ArchetypeRecord { ArchetypeId = archetypeId, Name = name });
        }
        return _archetypes;
    }

    /// <summary>Reads the component type table. Call after <see cref="ReadArchetypes"/>.</summary>
    public IReadOnlyList<ComponentTypeRecord> ReadComponentTypes()
    {
        _componentTypes.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var id = _binaryReader.ReadInt32();
            var name = ReadShortString();
            _componentTypes.Add(new ComponentTypeRecord { ComponentTypeId = id, Name = name });
        }
        return _componentTypes;
    }

    /// <summary>
    /// Reads the next compressed block of raw records. Returns the decoded byte payload in <paramref name="records"/>; caller walks it as a
    /// sequence of u16-size-prefixed records.
    /// </summary>
    /// <param name="records">
    /// Receives the decoded block bytes. <see cref="ReadOnlyMemory{Byte}.Empty"/> on end-of-stream. The returned memory is rented from
    /// <see cref="ArrayPool{T}.Shared"/> and is valid only until the next call to <see cref="ReadNextBlock"/> or <see cref="Dispose"/>. Do not
    /// stash the slice across iterations — the underlying buffer is returned to the pool on the next call and may be handed to another caller.
    /// </param>
    /// <param name="recordCount">Number of records the block contains (from the block header).</param>
    /// <returns><c>true</c> if a block was read, <c>false</c> if end of stream.</returns>
    public bool ReadNextBlock(out ReadOnlyMemory<byte> records, out int recordCount)
    {
        records = default;
        recordCount = 0;

        Span<byte> blockHeader = stackalloc byte[TraceBlockEncoder.BlockHeaderSize];
        var bytesRead = _stream.Read(blockHeader);
        if (bytesRead == 0)
        {
            return false;
        }

        if (bytesRead < 4)
        {
            return false;
        }

        var firstWord = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
        if (firstWord == TraceFileWriter.SpanNameTableMagic)
        {
            // Span name table marker — seek back, read it, then recurse.
            _stream.Position -= bytesRead;
            ReadSpanNames();
            return ReadNextBlock(out records, out recordCount);
        }

        if (bytesRead < TraceBlockEncoder.BlockHeaderSize)
        {
            return false;
        }

        var (uncompressedBytes, compressedBytes, count) = TraceBlockEncoder.ReadBlockHeader(blockHeader);
        recordCount = count;

        if (_compressedBuffer.Length < compressedBytes)
        {
            _compressedBuffer = new byte[compressedBytes];
        }

        // Return the previously-rented block before renting a new one — the caller's ReadOnlyMemory<byte> from the prior call becomes invalid
        // at this point per the documented lifetime contract on this method. ArrayPool.Rent may hand back a buffer larger than requested, so we
        // always slice to `uncompressedBytes` when exposing it via ReadOnlyMemory. LZ4 decodes directly into the rented buffer — no staging copy.
        if (_rentedBlock != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBlock);
            _rentedBlock = null;
        }
        _rentedBlock = ArrayPool<byte>.Shared.Rent(uncompressedBytes);

        _stream.ReadExactly(_compressedBuffer.AsSpan(0, compressedBytes));
        TraceBlockEncoder.DecodeBlock(_compressedBuffer.AsSpan(0, compressedBytes), uncompressedBytes, _rentedBlock);

        records = new ReadOnlyMemory<byte>(_rentedBlock, 0, uncompressedBytes);
        return true;
    }

    /// <summary>
    /// Reads the span name table if present at the current stream position. Returns the cumulative dictionary (merges with any previously-read
    /// span name table). Tolerates end-of-stream — if fewer than 4 bytes remain, the method returns the current dictionary unchanged instead of
    /// throwing, since the span name table is an optional trailing structure.
    /// </summary>
    public IReadOnlyDictionary<int, string> ReadSpanNames()
    {
        // Guard against EOF: the span name table is optional, so a stream at EOF (or with < 4 bytes left) is valid "no table present".
        if (_stream.CanSeek && _stream.Length - _stream.Position < sizeof(uint))
        {
            return _spanNames;
        }

        var magic = _binaryReader.ReadUInt32();
        if (magic != TraceFileWriter.SpanNameTableMagic)
        {
            _stream.Position -= 4;
            return _spanNames;
        }

        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var id = _binaryReader.ReadUInt16();
            var name = ReadShortString();
            _spanNames[id] = name;
        }
        return _spanNames;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_rentedBlock != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBlock);
            _rentedBlock = null;
        }
        _binaryReader.Dispose();
        _stream.Dispose();
    }

    private string ReadShortString()
    {
        var len = _binaryReader.ReadByte();
        var bytes = _binaryReader.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }
}
