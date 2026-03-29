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
    /// Resolves the effective worker count, applying the auto-detect formula if <see cref="WorkerCount"/> is -1.
    /// </summary>
    internal int ResolveWorkerCount() => WorkerCount == -1 ? Math.Max(1, Environment.ProcessorCount - 4) : WorkerCount;
}
