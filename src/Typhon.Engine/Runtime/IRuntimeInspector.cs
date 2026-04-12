using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Interface for deep runtime inspection. When set on <see cref="RuntimeOptions.Inspector"/>, the <see cref="DagScheduler"/> calls these methods at
/// each instrumentation point.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be thread-safe: <see cref="OnChunkStart"/> and <see cref="OnChunkEnd"/> are called from worker threads concurrently.
/// All other methods are called from the scheduler's timer thread (single-threaded).
/// </para>
/// <para>
/// Performance contract: each callback should complete in &lt; 30 ns. The recommended pattern is to write a blittable struct to a per-thread ring buffer
/// and defer all processing to a background thread.
/// </para>
/// </remarks>
[PublicAPI]
public interface IRuntimeInspector
{
    /// <summary>Called when a tick begins. Single-threaded (timer thread).</summary>
    /// <param name="tickNumber">Monotonic tick number.</param>
    /// <param name="timestampTicks">High-resolution timestamp (<c>Stopwatch.GetTimestamp()</c>).</param>
    void OnTickStart(long tickNumber, long timestampTicks);

    /// <summary>Called when a tick phase starts. Single-threaded.</summary>
    void OnPhaseStart(TickPhase phase, long timestampTicks);

    /// <summary>Called when a tick phase ends. Single-threaded.</summary>
    void OnPhaseEnd(TickPhase phase, long timestampTicks);

    /// <summary>Called when a system becomes ready (all predecessors completed). Can be called from any worker.</summary>
    void OnSystemReady(int systemIndex, long timestampTicks);

    /// <summary>Called when a system chunk starts executing on a worker thread. Called from the worker thread.</summary>
    void OnChunkStart(int systemIndex, int chunkIndex, int workerId, long timestampTicks, int totalChunks);

    /// <summary>Called when a system chunk finishes executing. Called from the worker thread.</summary>
    void OnChunkEnd(int systemIndex, int chunkIndex, int workerId, long timestampTicks, int entitiesProcessed);

    /// <summary>Called when a system is skipped. Can be called from any worker.</summary>
    void OnSystemSkipped(int systemIndex, SkipReason reason, long timestampTicks);

    /// <summary>Called when a tick ends. Single-threaded (timer thread).</summary>
    void OnTickEnd(long tickNumber, long timestampTicks);

    /// <summary>Called once at scheduler startup with the full system definitions. Single-threaded.</summary>
    void OnSchedulerStarted(SystemDefinition[] systems, int workerCount, float baseTickRate);

    /// <summary>Called when the scheduler is stopping. Flush all buffered data.</summary>
    void OnSchedulerStopping();
}
