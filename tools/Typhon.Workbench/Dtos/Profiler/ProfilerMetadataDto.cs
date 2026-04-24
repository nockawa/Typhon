namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Full wire shape returned by <c>GET /api/sessions/{id}/profiler/metadata</c> once the sidecar cache build completes.
/// Everything the Workbench client needs to render the timeline overview + drive chunk fetches.
/// </summary>
/// <param name="Fingerprint">SHA-256 fingerprint of the source file as uppercase hex (64 chars). Used by OPFS chunk cache as subdirectory key.</param>
public record ProfilerMetadataDto(
    string Fingerprint,
    ProfilerHeaderDto Header,
    SystemDefinitionDto[] Systems,
    ArchetypeDto[] Archetypes,
    ComponentTypeDto[] ComponentTypes,
    System.Collections.Generic.Dictionary<int, string> SpanNames,
    GlobalMetricsDto GlobalMetrics,
    TickSummaryDto[] TickSummaries,
    ChunkManifestEntryDto[] ChunkManifest,
    GcSuspensionDto[] GcSuspensions);

/// <summary>Header fields projected from <c>TraceFileHeader</c>. All primitive types — JSON-friendly.</summary>
public record ProfilerHeaderDto(
    int Version,
    long TimestampFrequency,
    float BaseTickRate,
    byte WorkerCount,
    ushort SystemCount,
    ushort ArchetypeCount,
    ushort ComponentTypeCount,
    long CreatedUtcTicks,
    long SamplingSessionStartQpc);

/// <summary>One system in the DAG.</summary>
public record SystemDefinitionDto(
    ushort Index,
    string Name,
    byte Type,
    byte Priority,
    bool IsParallel,
    byte TierFilter,
    ushort[] Predecessors,
    ushort[] Successors);

/// <summary>Archetype-id → name mapping.</summary>
public record ArchetypeDto(ushort ArchetypeId, string Name);

/// <summary>Component-type-id → name mapping.</summary>
public record ComponentTypeDto(int ComponentTypeId, string Name);

/// <summary>Per-tick overview row. Used to render the tick-overview strip at the top of the Profiler panel.</summary>
/// <param name="ActiveSystemsBitmask">64-bit bitmask of active system indices. Serialized as decimal string to preserve precision.</param>
public record TickSummaryDto(
    uint TickNumber,
    double StartUs,
    float DurationUs,
    uint EventCount,
    float MaxSystemDurationUs,
    string ActiveSystemsBitmask);

/// <summary>One entry of the chunk manifest — tells the client which chunk covers a given tick range.</summary>
public record ChunkManifestEntryDto(
    uint FromTick,
    uint ToTick,
    uint EventCount,
    bool IsContinuation);

/// <summary>Session-wide aggregate metrics. Computed once during cache build.</summary>
public record GlobalMetricsDto(
    double GlobalStartUs,
    double GlobalEndUs,
    double MaxTickDurationUs,
    double MaxSystemDurationUs,
    double P95TickDurationUs,
    long TotalEvents,
    uint TotalTicks,
    SystemAggregateDto[] SystemAggregates);

/// <summary>Per-system invocation count + total duration, summed across all ticks.</summary>
public record SystemAggregateDto(
    ushort SystemIndex,
    uint InvocationCount,
    double TotalDurationUs);

/// <summary>Single GC suspension instance. Rendered as overlay on the GC gauge track.</summary>
public record GcSuspensionDto(
    double StartUs,
    double DurationUs,
    byte ThreadSlot);
