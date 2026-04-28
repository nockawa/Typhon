namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Wire shape for the profiler live SSE delta stream (#289 unified pipeline). Discriminated by <see cref="Kind"/>:
/// <list type="bullet">
///   <item><c>"metadata"</c> — <see cref="Metadata"/> non-null. Full snapshot, emitted on connect / reconnect.</item>
///   <item><c>"tickSummaryAdded"</c> — <see cref="TickSummary"/> non-null. One per tick the builder finalizes.</item>
///   <item><c>"chunkAdded"</c> — <see cref="ChunkEntry"/> non-null. One per chunk the builder flushes.</item>
///   <item><c>"threadInfoAdded"</c> — <see cref="ThreadInfo"/> non-null. One per (slot, name) pair as workers claim slots.</item>
///   <item><c>"globalMetricsUpdated"</c> — <see cref="GlobalMetrics"/> non-null. ~1 Hz coalesced.</item>
///   <item><c>"heartbeat"</c> — <see cref="Status"/> non-null. Connection-state change or 5 s idle pulse.</item>
///   <item><c>"shutdown"</c> — emitted when the engine sends a Shutdown frame; clients render a final state.</item>
/// </list>
/// Every frame ships as a default SSE <c>message</c> event (no <c>event:</c> prefix) because the client's
/// <c>useEventSource</c> hook only listens to <c>onmessage</c>. Clients switch on <see cref="Kind"/>.
/// </summary>
public record LiveStreamEventDto(
    string Kind,
    ProfilerMetadataDto Metadata = null,
    TickSummaryDto TickSummary = null,
    ChunkManifestEntryDto ChunkEntry = null,
    GlobalMetricsDto GlobalMetrics = null,
    ThreadInfoDto ThreadInfo = null,
    string Status = null);

/// <summary>
/// One (slot → thread name + kind) mapping. Emitted via <c>threadInfoAdded</c> SSE delta as the engine's worker
/// threads claim slots and publish their managed thread name. Independent of chunk loading — the client doesn't
/// have to have fetched any chunk to know which slot is which thread.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> is the wire-byte form of <c>Typhon.Profiler.ThreadKind</c> (Main=0, Worker=1, Pool=2,
/// Other=3). Encoded as a plain byte rather than an enum so the OpenAPI spec stays a primitive number type and
/// doesn't require client-side enum codegen for a stable wire contract.
/// </remarks>
public record ThreadInfoDto(byte ThreadSlot, string Name, int ManagedThreadId, byte Kind);
