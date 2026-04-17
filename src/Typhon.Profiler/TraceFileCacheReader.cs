using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Opens and reads a `.typhon-trace-cache` sidecar file. Small sections (<see cref="TickIndex"/>, <see cref="TickSummaries"/>,
/// <see cref="ChunkManifest"/>, <see cref="GlobalMetrics"/>, <see cref="SpanNames"/>) are loaded eagerly on construction — they're capped
/// at tens of MB even for 500K-tick traces, and the server/client need random access to them anyway. The bulk FoldedChunkData section stays
/// on disk; callers read chunks on demand via <see cref="ReadChunkRaw"/> or <see cref="DecompressChunk"/>.
/// </summary>
/// <remarks>
/// Construction validates the magic, version, and section-table consistency. Fingerprint verification against the source is a separate concern
/// (see <see cref="VerifyFingerprint"/> and <see cref="ComputeSourceFingerprint"/>) — the reader does NOT open the source file itself.
/// </remarks>
public sealed class TraceFileCacheReader : IDisposable
{
    private readonly Stream _stream;
    private readonly Dictionary<CacheSectionId, SectionTableEntry> _sectionsByid = new();
    private readonly List<TickIndexEntry> _tickIndex = new();
    private readonly List<TickSummary> _tickSummaries = new();
    private readonly List<ChunkManifestEntry> _chunkManifest = new();
    // Index from FromTick → manifest position. Built once when the cache is loaded so endpoint handlers can resolve chunk lookups in O(1)
    // instead of rescanning the manifest on every request. Keyed by FromTick alone — (fromTick, toTick) pairs are 1:1 in a valid manifest,
    // so the ToTick value is validated against the stored entry after the dictionary lookup.
    private readonly Dictionary<uint, int> _chunkIndexByFromTick = new();
    private readonly List<SystemAggregateDuration> _systemAggregates = new();
    private readonly Dictionary<int, string> _spanNames = new();
    private GlobalMetricsFixed _globalMetrics;
    private CacheHeader _header;
    private bool _disposed;

    public TraceFileCacheReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanSeek)
        {
            throw new ArgumentException("TraceFileCacheReader requires a seekable stream.", nameof(stream));
        }

        ReadHeader();
        ReadSectionTable();
        LoadSmallSections();
    }

    /// <summary>The cache file's header. Contains version, fingerprint, and section-table location.</summary>
    public ref readonly CacheHeader Header => ref _header;

    public IReadOnlyList<TickIndexEntry> TickIndex => _tickIndex;
    public IReadOnlyList<TickSummary> TickSummaries => _tickSummaries;
    public IReadOnlyList<ChunkManifestEntry> ChunkManifest => _chunkManifest;
    /// <summary>O(1) index from a chunk's FromTick to its position in <see cref="ChunkManifest"/>. Endpoint handlers should use this to
    /// resolve chunk lookups instead of scanning the manifest linearly.</summary>
    public IReadOnlyDictionary<uint, int> ChunkIndexByFromTick => _chunkIndexByFromTick;
    public ref readonly GlobalMetricsFixed GlobalMetrics => ref _globalMetrics;
    public IReadOnlyList<SystemAggregateDuration> SystemAggregates => _systemAggregates;
    public IReadOnlyDictionary<int, string> SpanNames => _spanNames;

    /// <summary>
    /// Read a chunk's compressed bytes into <paramref name="compressedDestination"/>. The destination must be at least
    /// <paramref name="entry"/>.CacheByteLength bytes long. No decompression happens here.
    /// </summary>
    public void ReadChunkRaw(in ChunkManifestEntry entry, Span<byte> compressedDestination)
    {
        ThrowIfDisposed();
        if (compressedDestination.Length < entry.CacheByteLength)
        {
            throw new ArgumentException($"Destination too small: need {entry.CacheByteLength}, got {compressedDestination.Length}.", nameof(compressedDestination));
        }
        _stream.Position = entry.CacheByteOffset;
        _stream.ReadExactly(compressedDestination[..(int)entry.CacheByteLength]);
    }

    /// <summary>
    /// Read and LZ4-decompress a chunk into <paramref name="uncompressedDestination"/>. <paramref name="compressedScratch"/> is a caller-supplied
    /// scratch buffer for the compressed read (callers pool these across many chunks). Both buffers must be sized ≥ the entry's respective
    /// lengths.
    /// </summary>
    public void DecompressChunk(in ChunkManifestEntry entry, Span<byte> uncompressedDestination, Span<byte> compressedScratch)
    {
        ThrowIfDisposed();
        if (uncompressedDestination.Length < entry.UncompressedBytes)
        {
            throw new ArgumentException($"Uncompressed destination too small: need {entry.UncompressedBytes}, got {uncompressedDestination.Length}.", nameof(uncompressedDestination));
        }
        if (compressedScratch.Length < entry.CacheByteLength)
        {
            throw new ArgumentException($"Compressed scratch too small: need {entry.CacheByteLength}, got {compressedScratch.Length}.", nameof(compressedScratch));
        }

        _stream.Position = entry.CacheByteOffset;
        var compressed = compressedScratch[..(int)entry.CacheByteLength];
        _stream.ReadExactly(compressed);

        var decoded = LZ4Codec.Decode(compressed, uncompressedDestination[..(int)entry.UncompressedBytes]);
        if (decoded != (int)entry.UncompressedBytes)
        {
            throw new InvalidDataException($"LZ4 decode size mismatch for chunk [{entry.FromTick}, {entry.ToTick}): expected {entry.UncompressedBytes}, got {decoded}.");
        }
    }

    /// <summary>
    /// Compute the 32-byte source-file fingerprint: SHA-256 of source mtime-ticks + length + first 4 KB + last 4 KB. Cheap (~1 ms) and
    /// collision-resistant enough to detect any meaningful mutation.
    /// </summary>
    public static void ComputeSourceFingerprint(string sourcePath, Span<byte> destination32)
    {
        if (destination32.Length < 32)
        {
            throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination32));
        }
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        using var sha = SHA256.Create();
        Span<byte> meta = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(meta[..8], info.LastWriteTimeUtc.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(meta.Slice(8, 8), info.Length);
        sha.TransformBlock(meta.ToArray(), 0, meta.Length, null, 0);

        using var fs = File.OpenRead(sourcePath);
        var edgeBuf = new byte[TraceFileCacheConstants.FingerprintEdgeBytes];
        var prefixLen = (int)Math.Min(edgeBuf.Length, fs.Length);
        fs.ReadExactly(edgeBuf.AsSpan(0, prefixLen));
        sha.TransformBlock(edgeBuf, 0, prefixLen, null, 0);

        if (fs.Length > edgeBuf.Length * 2)
        {
            fs.Position = fs.Length - edgeBuf.Length;
            fs.ReadExactly(edgeBuf.AsSpan(0, edgeBuf.Length));
            sha.TransformBlock(edgeBuf, 0, edgeBuf.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        sha.Hash!.CopyTo(destination32);
    }

    /// <summary>
    /// Verify the cache's header fingerprint against a freshly-computed fingerprint for the source file. Returns true if they match (cache is
    /// still valid for this source).
    /// </summary>
    public unsafe bool VerifyFingerprint(ReadOnlySpan<byte> expectedFingerprint32)
    {
        if (expectedFingerprint32.Length < 32)
        {
            throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(expectedFingerprint32));
        }
        fixed (byte* fpPtr = _header.SourceFingerprint)
        {
            var headerFingerprint = new ReadOnlySpan<byte>(fpPtr, 32);
            return headerFingerprint.SequenceEqual(expectedFingerprint32[..32]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────────

    private void ReadHeader()
    {
        _stream.Position = 0;
        Span<byte> headerBytes = stackalloc byte[Unsafe.SizeOf<CacheHeader>()];
        _stream.ReadExactly(headerBytes);
        _header = MemoryMarshal.Read<CacheHeader>(headerBytes);

        if (_header.Magic != CacheHeader.MagicValue)
        {
            throw new InvalidDataException($"Invalid cache file magic: 0x{_header.Magic:X8} (expected 0x{CacheHeader.MagicValue:X8}).");
        }
        if (_header.Version != CacheHeader.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported cache version: {_header.Version} (reader supports {CacheHeader.CurrentVersion}).");
        }
        if (_header.ChunkerVersion != TraceFileCacheConstants.CurrentChunkerVersion)
        {
            throw new InvalidDataException(
                $"Cache chunker version {_header.ChunkerVersion} does not match reader's {TraceFileCacheConstants.CurrentChunkerVersion}. " +
                "Cache must be rebuilt.");
        }
    }

    private void ReadSectionTable()
    {
        _stream.Position = _header.SectionTableOffset;
        var entrySize = Unsafe.SizeOf<SectionTableEntry>();
        var totalLength = (int)_header.SectionTableLength;
        if (totalLength % entrySize != 0)
        {
            throw new InvalidDataException($"Section table length {totalLength} is not a multiple of entry size {entrySize}.");
        }
        var entryCount = totalLength / entrySize;

        var buffer = new byte[totalLength];
        _stream.ReadExactly(buffer);
        var entries = MemoryMarshal.Cast<byte, SectionTableEntry>(buffer);
        for (var i = 0; i < entryCount; i++)
        {
            var entry = entries[i];
            if (entry.SectionId == (ushort)CacheSectionId.Invalid)
            {
                continue;
            }
            _sectionsByid[(CacheSectionId)entry.SectionId] = entry;
        }
    }

    private void LoadSmallSections()
    {
        if (_sectionsByid.TryGetValue(CacheSectionId.TickIndex, out var tickIndexSec))
        {
            LoadStructArray(tickIndexSec, _tickIndex);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.TickSummaries, out var tickSummariesSec))
        {
            LoadStructArray(tickSummariesSec, _tickSummaries);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.ChunkManifest, out var manifestSec))
        {
            LoadStructArray(manifestSec, _chunkManifest);
            // Build the FromTick → index map once; every /api/trace/chunk and /api/trace/chunk-binary request uses it to short-circuit what
            // used to be a full-manifest scan. For a 10K-entry manifest that's ~50 µs per request saved — negligible solo, real during a
            // timeline load that fires hundreds of chunk requests.
            _chunkIndexByFromTick.EnsureCapacity(_chunkManifest.Count);
            for (var i = 0; i < _chunkManifest.Count; i++)
            {
                _chunkIndexByFromTick[_chunkManifest[i].FromTick] = i;
            }
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.GlobalMetrics, out var metricsSec))
        {
            LoadGlobalMetrics(metricsSec);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.SpanNameTable, out var spanSec))
        {
            LoadSpanNameTable(spanSec);
        }
    }

    private void LoadStructArray<T>(SectionTableEntry section, List<T> destination) where T : unmanaged
    {
        _stream.Position = section.Offset;
        var entrySize = Unsafe.SizeOf<T>();
        if (section.Length % entrySize != 0)
        {
            throw new InvalidDataException($"Section {(CacheSectionId)section.SectionId} length {section.Length} not a multiple of entry size {entrySize}.");
        }
        var count = (int)(section.Length / entrySize);
        var buffer = new byte[section.Length];
        _stream.ReadExactly(buffer);
        var typed = MemoryMarshal.Cast<byte, T>(buffer);
        destination.Capacity = count;
        for (var i = 0; i < count; i++)
        {
            destination.Add(typed[i]);
        }
    }

    private void LoadGlobalMetrics(SectionTableEntry section)
    {
        _stream.Position = section.Offset;
        var fixedSize = Unsafe.SizeOf<GlobalMetricsFixed>();
        Span<byte> fixedBuf = stackalloc byte[fixedSize];
        _stream.ReadExactly(fixedBuf);
        _globalMetrics = MemoryMarshal.Read<GlobalMetricsFixed>(fixedBuf);

        var aggCount = (int)_globalMetrics.SystemAggregateCount;
        if (aggCount > 0)
        {
            var aggSize = Unsafe.SizeOf<SystemAggregateDuration>();
            var buffer = new byte[aggCount * aggSize];
            _stream.ReadExactly(buffer);
            var typed = MemoryMarshal.Cast<byte, SystemAggregateDuration>(buffer);
            _systemAggregates.Capacity = aggCount;
            for (var i = 0; i < aggCount; i++)
            {
                _systemAggregates.Add(typed[i]);
            }
        }
    }

    private void LoadSpanNameTable(SectionTableEntry section)
    {
        _stream.Position = section.Offset;
        Span<byte> u16Buf = stackalloc byte[2];
        _stream.ReadExactly(u16Buf);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(u16Buf);
        for (var i = 0; i < count; i++)
        {
            _stream.ReadExactly(u16Buf);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(u16Buf);
            var len = _stream.ReadByte();
            if (len < 0)
            {
                throw new InvalidDataException("Unexpected EOF in span name table.");
            }
            var nameBuf = new byte[len];
            _stream.ReadExactly(nameBuf);
            _spanNames[id] = Encoding.UTF8.GetString(nameBuf);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TraceFileCacheReader));
        }
    }
}
