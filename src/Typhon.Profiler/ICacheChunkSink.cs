using System;
using System.Collections.Generic;

namespace Typhon.Profiler;

/// <summary>
/// Abstraction over "where flushed chunks go" for <see cref="IncrementalCacheBuilder"/>. Two implementations:
/// <see cref="FileCacheSink"/> wraps <see cref="TraceFileCacheWriter"/> and produces a complete sidecar cache file
/// (replay path); <c>AppendOnlyChunkSink</c> (live path) writes only chunk bytes to a temp file and skips the trailer.
/// </summary>
/// <remarks>
/// <para>
/// The sink is fully responsible for laying out compressed chunk bytes — the builder hands raw uncompressed records and the
/// resulting (offset, length) come back so the builder can record the chunk in its in-memory manifest. For replay sinks,
/// trailer sections are also written via <see cref="WriteTrailer"/> at finalize time. For live sinks, those sections live in
/// memory only and the trailer call is a no-op (<see cref="SupportsTrailer"/> returns <c>false</c>).
/// </para>
/// </remarks>
public interface ICacheChunkSink : IDisposable
{
    /// <summary>
    /// LZ4-compress and append a chunk's records to the sink's underlying storage. Returns the byte offset and lengths needed
    /// to populate the matching <see cref="ChunkManifestEntry"/>.
    /// </summary>
    (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords);

    /// <summary>True if this sink writes a trailer (TickSummaries / GlobalMetrics / ChunkManifest / SpanNameTable + cache header).</summary>
    bool SupportsTrailer { get; }

    /// <summary>
    /// Write trailer sections + finalize the cache header. Replay sinks (<see cref="FileCacheSink"/>) implement this; live sinks throw.
    /// Idempotent guard not required — builder calls this at most once on dispose.
    /// </summary>
    /// <param name="sourceMetadataBytes">
    /// Optional verbatim source metadata (header + system / archetype / component-type tables, in <c>TraceFileWriter</c> wire format). When
    /// non-empty, the sink emits a <see cref="CacheSectionId.SourceMetadata"/> section; the caller must set
    /// <see cref="CacheHeaderFlags.IsSelfContained"/> on <paramref name="headerTemplate"/> so loaders project metadata from these bytes
    /// instead of opening a sibling source file. Pass <see cref="ReadOnlySpan{T}.Empty"/> for source-derived caches.
    /// </param>
    void WriteTrailer(
        IReadOnlyList<TickSummary> tickSummaries,
        in GlobalMetricsFixed globalMetrics,
        IReadOnlyList<SystemAggregateDuration> systemAggregates,
        IReadOnlyList<ChunkManifestEntry> chunkManifest,
        IReadOnlyDictionary<int, string> spanNames,
        ReadOnlySpan<byte> sourceMetadataBytes,
        in CacheHeader headerTemplate);
}
