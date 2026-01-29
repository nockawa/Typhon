// unset

using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Strongly-typed telemetry options for DI/IOptions pattern.
/// These options can be bound from configuration files or environment variables.
/// </summary>
[PublicAPI]
public class TelemetryOptions
{
    /// <summary>
    /// The configuration section name for telemetry settings.
    /// </summary>
    public const string SectionName = "Typhon:Telemetry";

    /// <summary>
    /// Master switch for all telemetry. When false, all telemetry is disabled
    /// regardless of individual component settings.
    /// Default: false
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// AccessControl synchronization primitive telemetry options.
    /// </summary>
    public AccessControlTelemetryOptions AccessControl { get; set; } = new();

    /// <summary>
    /// PagedMMF (memory-mapped file) telemetry options.
    /// </summary>
    public PagedMMFTelemetryOptions PagedMMF { get; set; } = new();

    /// <summary>
    /// BTree index telemetry options.
    /// </summary>
    public BTreeTelemetryOptions BTree { get; set; } = new();

    /// <summary>
    /// Transaction system telemetry options.
    /// </summary>
    public TransactionTelemetryOptions Transaction { get; set; } = new();
}

/// <summary>
/// Telemetry options for <see cref="AccessControl"/> synchronization primitive.
/// Tracks contention, wait times, and potential misuse patterns.
/// </summary>
[PublicAPI]
public class AccessControlTelemetryOptions
{
    /// <summary>
    /// Enable AccessControl telemetry. Requires master <see cref="TelemetryOptions.Enabled"/> to be true.
    /// Default: false
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Track contention events (when a thread has to wait for access).
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackContention { get; set; } = true;

    /// <summary>
    /// Track the duration of contention wait times.
    /// Adds overhead of Stopwatch calls when contention occurs.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackContentionDuration { get; set; } = true;

    /// <summary>
    /// Track shared vs exclusive access patterns.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackAccessPatterns { get; set; } = true;
}

/// <summary>
/// Telemetry options for <see cref="PagedMMF"/> and <see cref="ManagedPagedMMF"/>.
/// Tracks page cache behavior, I/O operations, and memory usage.
/// </summary>
[PublicAPI]
public class PagedMMFTelemetryOptions
{
    /// <summary>
    /// Enable PagedMMF telemetry. Requires master <see cref="TelemetryOptions.Enabled"/> to be true.
    /// Default: false
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Track page allocation events.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackPageAllocations { get; set; } = true;

    /// <summary>
    /// Track page eviction events from the cache.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackPageEvictions { get; set; } = true;

    /// <summary>
    /// Track I/O read and write operations.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackIOOperations { get; set; } = true;

    /// <summary>
    /// Track cache hit/miss ratios.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackCacheHitRatio { get; set; } = true;
}

/// <summary>
/// Telemetry options for B+Tree index operations.
/// Tracks node operations, search performance, and structural changes.
/// </summary>
[PublicAPI]
public class BTreeTelemetryOptions
{
    /// <summary>
    /// Enable BTree telemetry. Requires master <see cref="TelemetryOptions.Enabled"/> to be true.
    /// Default: false
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Track node split operations.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackNodeSplits { get; set; } = true;

    /// <summary>
    /// Track node merge operations.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackNodeMerges { get; set; } = true;

    /// <summary>
    /// Track search depth statistics.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackSearchDepth { get; set; } = true;

    /// <summary>
    /// Track key comparison counts during operations.
    /// Default: false (high overhead in hot paths)
    /// </summary>
    public bool TrackKeyComparisons { get; set; }
}

/// <summary>
/// Telemetry options for transaction operations.
/// Tracks commits, rollbacks, conflicts, and timing.
/// </summary>
[PublicAPI]
public class TransactionTelemetryOptions
{
    /// <summary>
    /// Enable Transaction telemetry. Requires master <see cref="TelemetryOptions.Enabled"/> to be true.
    /// Default: false
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Track commit/rollback operations.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackCommitRollback { get; set; } = true;

    /// <summary>
    /// Track concurrency conflicts.
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackConflicts { get; set; } = true;

    /// <summary>
    /// Track transaction duration (from creation to commit/rollback).
    /// Default: true (when telemetry is enabled)
    /// </summary>
    public bool TrackDuration { get; set; } = true;
}
