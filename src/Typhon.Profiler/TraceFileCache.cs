using System.Runtime.InteropServices;

namespace Typhon.Profiler;

// Binary layout definitions for the `.typhon-trace-cache` sidecar file. The cache is a deterministic function of its source `.typhon-trace`:
// built once on first open, reused on subsequent opens of the same file. Invalidated (and rebuilt) when the source's fingerprint changes.
//
// File-level layout (see also claude/scratch/scalable-profiler-load-design.md §5):
//   [CacheHeader — 128 B fixed]
//   [SectionTable — N × SectionTableEntry]
//   [TickIndex]
//   [TickSummaries]
//   [GlobalMetrics]
//   [ChunkManifest]
//   [FoldedChunkData]   — LZ4-compressed record byte streams, one blob per manifest entry
//   [SpanNameTable]     — optional
//
// The header's SectionTableOffset + Length lets a reader locate any section in O(1) after reading the first 128 bytes. The layout is designed for
// append-only writes during build (sections streamed linearly) with a final rewind-and-patch to finalize header + section-table offsets.

/// <summary>
/// Fixed 128-byte header at the start of a `.typhon-trace-cache` file. Contains the source-file fingerprint (for invalidation), format versioning,
/// and a pointer to the section table.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CacheHeader
{
    /// <summary>File magic: ASCII "TPCH" (0x48_43_50_54 little-endian).</summary>
    public uint Magic;

    /// <summary>Cache format version. Current: 1. Bump on breaking layout changes.</summary>
    public ushort Version;

    /// <summary>Flags (reserved).</summary>
    public ushort Flags;

    /// <summary>
    /// SHA-256 of (source mtime-ticks + source length + first 4 KB + last 4 KB). If the source file changes in any meaningful way, this mismatches
    /// and the cache is discarded on next open. Cheap to compute (~1 ms regardless of source size) and collision-resistant enough for our use.
    /// </summary>
    public unsafe fixed byte SourceFingerprint[32];

    /// <summary>
    /// Copy of the source file's <see cref="TraceFileHeader.Version"/>. If the source format revs, caches built against the old format are
    /// automatically invalidated without relying on fingerprint alone.
    /// </summary>
    public ushort SourceVersion;

    /// <summary>
    /// Version tag for the chunker / fold policy. Bump whenever <see cref="TraceFileCacheConstants.TickCap"/>, <see cref="TraceFileCacheConstants.ByteCap"/>,
    /// or the async-completion fold logic changes in a way that makes old caches incorrect. Readers that see a different ChunkerVersion treat the
    /// cache as stale and rebuild.
    /// </summary>
    public ushort ChunkerVersion;

    /// <summary>Offset (in bytes from the start of the cache file) of the section table.</summary>
    public long SectionTableOffset;

    /// <summary>Length in bytes of the section table (== <see cref="SectionTableEntry"/>.SizeInBytes × entry count).</summary>
    public long SectionTableLength;

    /// <summary>UTC timestamp when this cache was built (DateTime.UtcNow.Ticks). Informational; not used for invalidation.</summary>
    public long CreatedUtcTicks;

    /// <summary>Padding to 128 bytes. Zero-initialized; readers must ignore.</summary>
    public unsafe fixed byte Reserved[60];

    public const uint MagicValue = 0x48_43_50_54; // 'T','P','C','H' little-endian
    public const ushort CurrentVersion = 1;
}

/// <summary>
/// One entry in the section table. Identifies a named section by <see cref="SectionId"/> and locates it by byte offset + length within the cache
/// file. Sections are always written contiguously in the file; the table gives random access without requiring the reader to walk section headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SectionTableEntry
{
    /// <summary>Section identifier (see <see cref="CacheSectionId"/>).</summary>
    public ushort SectionId;

    /// <summary>Per-section flags (reserved; section-specific).</summary>
    public ushort Flags;

    /// <summary>Padding to align <see cref="Offset"/> on an 8-byte boundary.</summary>
    public uint Padding;

    /// <summary>Byte offset of the section payload from the start of the cache file.</summary>
    public long Offset;

    /// <summary>Byte length of the section payload.</summary>
    public long Length;
}

/// <summary>
/// Identifies each section in the cache file. Values are wire-stable; never renumber or reuse a retired ID — only append.
/// </summary>
public enum CacheSectionId : ushort
{
    /// <summary>Not valid; guards against zero-initialized entries.</summary>
    Invalid = 0,

    /// <summary>Per-tick index (<see cref="TickIndexEntry"/>[]), sorted by tick number. Enables binary search for source-file seeks.</summary>
    TickIndex = 1,

    /// <summary>Per-tick aggregates (<see cref="TickSummary"/>[]). Drives the viewer's overview-timeline render.</summary>
    TickSummaries = 2,

    /// <summary>Global metrics (<see cref="GlobalMetricsFixed"/> + optional per-system aggregates).</summary>
    GlobalMetrics = 3,

    /// <summary>Chunk manifest (<see cref="ChunkManifestEntry"/>[]). Drives the client's cache keying + the server's chunk-serving path.</summary>
    ChunkManifest = 4,

    /// <summary>Concatenated LZ4-compressed chunk payloads. Addressed by <see cref="ChunkManifestEntry.CacheByteOffset"/> + Length.</summary>
    FoldedChunkData = 5,

    /// <summary>Flat copy of the source file's optional span-name intern table. Count prefix (u16) + entries (u16 id, short-string name).</summary>
    SpanNameTable = 6,
}

/// <summary>
/// One entry in the tick index. Locates a given tick's byte range inside the *source* `.typhon-trace` file, for fast seek-to-tick during cache
/// rebuild or future features (scrub-to-timestamp). Separate from the <see cref="ChunkManifestEntry"/> which addresses the *cache* file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TickIndexEntry
{
    /// <summary>Offset in the source file where this tick's events begin. First field so the 8-byte value is 8-aligned within an array.</summary>
    public long ByteOffsetInSource;

    /// <summary>Tick number (1-based per the source decoder's counter).</summary>
    public uint TickNumber;

    /// <summary>Length in bytes of this tick's event stream in the source (spans one or more compressed blocks in practice).</summary>
    public uint ByteLengthInSource;

    /// <summary>Number of events in this tick (after fold — completion records collapsed into their kickoffs).</summary>
    public uint EventCount;

    /// <summary>Padding to keep the struct 8-byte-aligned (sizeof == 24).</summary>
    public uint Padding;
}

/// <summary>
/// Per-tick rollup shipped to the client as the overview feed. Small enough that all ticks for a 500K-tick trace fit in ~12 MB, so the client
/// fetches the entire summary on open and renders the timeline from it without any chunk loads.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TickSummary
{
    /// <summary>Tick number.</summary>
    public uint TickNumber;

    /// <summary>Total wall-clock duration of the tick in microseconds.</summary>
    public float DurationUs;

    /// <summary>Event count for this tick.</summary>
    public uint EventCount;

    /// <summary>Longest single-system duration observed in this tick (µs). Drives the color-scale normalization on the timeline.</summary>
    public float MaxSystemDurationUs;

    /// <summary>Bitmask of system indices that ran in this tick. Bit N set iff system index N had any activity. Caps at 64 systems; systems beyond
    /// index 63 don't set bits (overview still accurate for count/duration; bitmask is just a rough "did this system run" indicator).</summary>
    public ulong ActiveSystemsBitmask;

    /// <summary>
    /// Absolute start timestamp of this tick in microseconds (relative to the same origin as <see cref="GlobalMetricsFixed.GlobalStartUs"/>).
    /// Added in chunker v2 so the viewer can map viewRange-in-µs back to a tickNumber range without reading any chunk payload. Ticks with idle
    /// gaps between them (duration &lt; scheduler period) are handled correctly — this is the true wall-clock start, not a cumulative sum of
    /// durations.
    /// </summary>
    public double StartUs;
}

/// <summary>
/// One entry in the chunk manifest. Addresses a chunk's folded byte range inside the cache file's FoldedChunkData section. A chunk covers a
/// half-open tick range [FromTick, ToTick). Both endpoints are stored explicitly so the manifest can be consumed in any order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ChunkManifestEntry
{
    /// <summary>First tick included in this chunk (inclusive).</summary>
    public uint FromTick;

    /// <summary>First tick NOT included (exclusive). <c>ToTick - FromTick</c> is the chunk's tick count.</summary>
    public uint ToTick;

    /// <summary>Byte offset of the compressed chunk payload within the cache file's FoldedChunkData section (absolute cache-file offset).</summary>
    public long CacheByteOffset;

    /// <summary>Length of the compressed payload (input to LZ4 decompress).</summary>
    public uint CacheByteLength;

    /// <summary>Total number of records in this chunk (after fold).</summary>
    public uint EventCount;

    /// <summary>Uncompressed payload size (output size of LZ4 decompress; needed to pre-size the client's decode buffer).</summary>
    public uint UncompressedBytes;

    /// <summary>
    /// Per-chunk flag bits. Also serves as the 4-byte tail padding keeping the struct 8-byte-aligned (sizeof = 32); the wire size and field
    /// offset match the former <c>Padding</c> field exactly, so v7 caches built with Flags=0 are upward-compatible as "normal, non-continuation"
    /// chunks — the reader just sees zero bits, which is the no-flags-set state.
    /// <para>
    /// <b>Bit 0: <see cref="TraceFileCacheConstants.FlagIsContinuation"/></b> — set when this chunk starts mid-tick (continuation of the tick
    /// whose first events lived in the PREVIOUS chunk). Continuation chunks have NO <c>TickStart</c> record at their head; the decoder must
    /// seed its tick counter to <c>FromTick</c> directly rather than <c>FromTick - 1</c>. See the chunker-version-8 changelog entry for the
    /// reason mid-tick splitting exists.
    /// </para>
    /// <para>
    /// Bits 1-31: reserved for future per-chunk flags. Builders MUST write zero for reserved bits; readers MUST ignore unknown bits so that
    /// a future v8+ feature that sets a new bit doesn't break older readers on the same version.
    /// </para>
    /// </summary>
    public uint Flags;
}

/// <summary>
/// Fixed-size global metrics header. Followed in-section by a variable-length tail of per-system aggregates
/// (<see cref="SystemAggregateDuration"/>[]), with count == <see cref="SystemAggregateCount"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GlobalMetricsFixed
{
    public double GlobalStartUs;
    public double GlobalEndUs;
    public double MaxTickDurationUs;
    public double MaxSystemDurationUs;
    public double P95TickDurationUs;
    public long TotalEvents;
    public uint TotalTicks;
    public uint SystemAggregateCount;
}

/// <summary>
/// Aggregate duration for one system across the whole trace. Written after <see cref="GlobalMetricsFixed"/> in the GlobalMetrics section.
/// Used by the viewer to color-rank systems in the legend or to compute "which system dominates the trace" queries without loading any chunks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemAggregateDuration
{
    public ushort SystemIndex;
    public ushort Padding;
    public uint InvocationCount;
    public double TotalDurationUs;
}

/// <summary>
/// Wire envelope shipped at the start of each HTTP chunk response (uncompressed prefix, then the LZ4 payload). Identifies the chunk for the
/// client's cache key without requiring a LZ4 decode first.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ChunkWireHeader
{
    public uint Magic;
    public uint FromTick;
    public uint ToTick;
    public uint RecordCount;

    public const uint MagicValue = 0x4B_43_50_54; // 'T','P','C','K' little-endian
}

/// <summary>
/// Compile-time constants for the cache format. Kept in one place so writer, reader, builder, and tests all agree.
/// </summary>
public static class TraceFileCacheConstants
{
    /// <summary>Maximum ticks per chunk — upper bound on how coarse a chunk can be in tick count.</summary>
    public const int TickCap = 100;

    /// <summary>Maximum uncompressed bytes per chunk — upper bound on how big a chunk can be in payload size.</summary>
    public const int ByteCap = 1 * 1024 * 1024;

    /// <summary>
    /// Maximum events per chunk — closes a chunk at the next tick boundary when the running event count reaches this threshold.
    /// Complements <see cref="ByteCap"/> for regions where records are small and numerous (dense allocation bursts, high-frequency
    /// scheduler chunks, etc.): byte-cap alone could let tens of thousands of small records pile into one chunk that decodes slowly
    /// and dominates the client-side LRU budget. Splitting on event count instead caps the per-chunk decode cost at a bounded
    /// number of record decodes, regardless of their compressed byte ratio.
    ///
    /// 50 000 chosen empirically: at ~300 bytes average decoded per event (span + alloc mix), this is ~15 MB of resident heap per
    /// chunk — comfortable against the client's 500 MB LRU budget (30+ chunks headroom) and decodes in roughly 100-200 ms on a modern
    /// CPU. For ticks that fit under this cap, they're emitted as single whole chunks — no intra-tick splitting, no client-side merge.
    /// </summary>
    public const int EventCap = 50_000;

    /// <summary>
    /// Mid-tick byte cap. A SINGLE tick's accumulated record bytes exceeding this trigger the builder to close the current chunk
    /// in the middle of the tick and start a new continuation chunk (marked with <see cref="FlagIsContinuation"/>). Deliberately
    /// larger than <see cref="ByteCap"/> (2×) so that well-sized ticks never trip it — only genuinely pathological single ticks
    /// (e.g., >1 MiB of records in one tick) get split, keeping the client-side <c>mergeTickData</c> path cold for the common case.
    /// </summary>
    public const int IntraTickByteCap = 2 * ByteCap;

    /// <summary>
    /// Mid-tick event cap. Parallels <see cref="IntraTickByteCap"/> but counts records instead of bytes. A single tick whose event
    /// count hits this value triggers a mid-tick chunk close. 2× <see cref="EventCap"/> (100 000) so that a tick marginally over the
    /// normal event cap still emits as a single chunk — only genuinely pathological dense ticks split. Tuned independently of
    /// <see cref="EventCap"/> — they're separate dials so we can tighten one without affecting the other as workload data arrives.
    /// </summary>
    public const int IntraTickEventCap = 2 * EventCap;

    /// <summary>Bit 0 of <see cref="ChunkManifestEntry.Flags"/> — chunk starts mid-tick (continuation of the previous chunk's last tick).</summary>
    public const uint FlagIsContinuation = 0x1;

    /// <summary>
    /// Current chunker policy version. Incremented when <see cref="TickCap"/>, <see cref="ByteCap"/>, or the fold logic changes in a way that
    /// invalidates existing caches. Readers that see a different value must rebuild the cache.
    /// v2: added <c>TickSummary.StartUs</c>.
    /// v3: server-side async-completion fold — kickoff records carry the full async duration; completion records dropped from the stream.
    /// v4: tick duration computed from TickStart→TickEnd wall time only (no span-endTs extension) so folded kickoffs whose end extends past
    ///     TickEnd don't bloat the summary and cause adjacent ticks to appear overlapping in the viewer's selection math.
    /// v5: also stopped extending lastTs inside the fold path itself — there was a second duration-extension site that v4 missed. With this,
    ///     tick durations are purely wall-clock TickStart→TickEnd, regardless of whether fold fires within the chunk.
    /// v6: pre-first-tick events (MemoryAllocEvent, GcStart, GcEnd, GcSuspension) are buffered and prepended to the first chunk's byte
    ///     stream instead of being silently dropped. Old caches built with v5 are missing engine-startup memory events that land before
    ///     the first TickStart — readers that see v5 must rebuild against a v6 builder to surface them.
    /// v7: added <c>TraceEventKind.ThreadInfo</c> (kind 77). Emitted at slot claim — typically pre-first-tick — and added to the pre-tick
    ///     buffer path. Old v6 caches don't surface thread names; readers must rebuild against v7 to populate lane labels.
    /// v8: two combined changes, both invalidating prior caches:
    ///     (a) <see cref="EventCap"/> as a tick-boundary chunk-close trigger — shrinks the worst-case per-chunk decode cost in dense
    ///         multi-tick regions that previously squeaked under <see cref="ByteCap"/> because of small record sizes.
    ///     (b) Intra-tick splitting — a single pathologically dense tick (e.g., 2 M events in one tick) can now be split across multiple
    ///         chunks via <see cref="IntraTickByteCap"/> / <see cref="IntraTickEventCap"/>. Continuation chunks are marked with
    ///         <see cref="FlagIsContinuation"/>. The decoder must seed its tick counter to FromTick directly for continuation chunks
    ///         (vs FromTick - 1 for normal chunks). The former <c>ChunkManifestEntry.Padding</c> u32 is now <c>Flags</c>; offset and
    ///         size are unchanged, so v7-on-disk entries read back with Flags=0 which correctly means "normal, non-continuation."
    /// </summary>
    public const ushort CurrentChunkerVersion = 8;

    /// <summary>Sidecar file extension, appended to the source path (e.g., <c>foo.typhon-trace</c> → <c>foo.typhon-trace-cache</c>).</summary>
    public const string CacheFileExtension = "-cache";

    /// <summary>Size of the prefix + suffix regions read from the source file to feed the fingerprint hash. 4 KB each side.</summary>
    public const int FingerprintEdgeBytes = 4 * 1024;
}
