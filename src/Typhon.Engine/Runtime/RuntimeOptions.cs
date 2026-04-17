using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for the DAG scheduler and runtime tick loop.
/// </summary>
[PublicAPI]
public class RuntimeOptions
{
    /// <summary>
    /// Target tick rate in Hz. Default: 60.
    /// The scheduler uses metronome-style tick advancement to prevent drift.
    /// </summary>
    public int BaseTickRate { get; set; } = 60;

    /// <summary>
    /// Number of worker threads for parallel system execution.
    /// Set to -1 (default) for automatic: <c>Math.Max(1, Environment.ProcessorCount - 4)</c>.
    /// Set to 1 for single-threaded debug mode (systems execute in topological order on the timer thread).
    /// </summary>
    public int WorkerCount { get; set; } = -1;

    /// <summary>
    /// Capacity of the telemetry ring buffer (number of ticks retained). Must be a power of 2.
    /// Default: 1024 (~17 seconds at 60Hz, ~200KB).
    /// </summary>
    public int TelemetryRingCapacity { get; set; } = 1024;

    /// <summary>
    /// Subscription server configuration. Set to non-null to enable the TCP subscription server.
    /// If null, no subscription server is started (subscriptions disabled).
    /// </summary>
    public SubscriptionServerOptions SubscriptionServer { get; set; }

    /// <summary>
    /// Overload detection and response configuration. Always active with sensible defaults.
    /// </summary>
    public OverloadOptions Overload { get; set; } = new();

    /// <summary>
    /// Minimum number of entities per chunk for parallel QuerySystem dispatch.
    /// Controls granularity: fewer entities per chunk = more parallelism but more overhead (Transaction creation per chunk).
    /// Entity sets smaller than this value still use the parallel chunk path with <c>totalChunks=1</c>.
    /// Default: 64.
    /// </summary>
    public int ParallelQueryMinChunkSize { get; set; } = 64;

    /// <summary>
    /// Resolves the effective worker count, applying the auto-detect formula if <see cref="WorkerCount"/> is -1.
    /// </summary>
    internal int ResolveWorkerCount() => WorkerCount == -1 ? Math.Max(1, Environment.ProcessorCount - 4) : WorkerCount;
}
