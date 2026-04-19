using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Profiler.Gc;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Static lifecycle façade for the Tracy-style profiler. Manages the consumer drain thread, the per-exporter consume threads, and the exporter list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Start/Stop semantics:</b> idempotent. Calling <see cref="Start"/> on an already-running profiler is a no-op (does NOT throw). Calling
/// <see cref="Stop"/> on a stopped profiler is also a no-op. This avoids brittle test setup and accommodates restart-after-crash patterns.
/// </para>
/// <para>
/// <b>Producer gate vs lifecycle:</b> <see cref="Start"/> does NOT enable the producer hot path. The producer gate is
/// <see cref="TelemetryConfig.ProfilerActive"/>, which is set once at <see cref="TelemetryConfig"/> class load from the config file. If
/// <c>ProfilerActive == false</c>, calling <see cref="Start"/> is harmless but no events will ever be emitted. If <c>ProfilerActive == true</c>,
/// events accumulate in slot ring buffers regardless of whether the profiler is started — until <see cref="Start"/> is called there's just no consumer
/// to drain them, so eventually the producers see drops.
/// </para>
/// <para>
/// <b>Exporter mutation:</b> <see cref="AttachExporter"/>/<see cref="DetachExporter"/> are only valid while the profiler is stopped. Attempting to
/// mutate the exporter list while running throws <see cref="InvalidOperationException"/> — concurrent mutation would race the consumer thread reading
/// the list during fan-out.
/// </para>
/// <para>
/// <b>Threading model:</b> Start/Stop are serialized on a static lock. The consumer thread runs on its own (managed by
/// <see cref="ProfilerConsumerThread"/>'s <see cref="HighResolutionTimerServiceBase"/> base). One dedicated OS thread per attached exporter runs
/// <c>foreach (batch in exporter.Queue.GetConsumingEnumerable) { ProcessBatch; Release; }</c> until CompleteAdding signals shutdown.
/// </para>
/// </remarks>
public static class TyphonProfiler
{
    private static readonly Lock LifecycleLock = new();
    private static readonly List<IProfilerExporter> Exporters = new();
    private static ProfilerConsumerThread Consumer;
    private static Thread[] ExporterThreads;
    private static CancellationTokenSource ExporterCts;
    private static GcTracingHost GcTracing;
    private static bool Running;

    /// <summary>True while the profiler consumer thread is running.</summary>
    public static bool IsRunning
    {
        get
        {
            lock (LifecycleLock)
            {
                return Running;
            }
        }
    }

    /// <summary>
    /// Attach an exporter. Must be called before <see cref="Start"/> — attaching while running throws.
    /// </summary>
    public static void AttachExporter(IProfilerExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        lock (LifecycleLock)
        {
            if (Running)
            {
                throw new InvalidOperationException("Cannot attach an exporter while the profiler is running. Call Stop() first, attach, then Start() again.");
            }
            Exporters.Add(exporter);
        }
    }

    /// <summary>
    /// Detach an exporter. Must be called while the profiler is stopped. Returns <c>true</c> if the exporter was found and removed.
    /// </summary>
    public static bool DetachExporter(IProfilerExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        lock (LifecycleLock)
        {
            if (Running)
            {
                throw new InvalidOperationException("Cannot detach an exporter while the profiler is running. Call Stop() first, detach, then Start() again.");
            }
            return Exporters.Remove(exporter);
        }
    }

    /// <summary>
    /// Start the profiler consumer thread and all attached exporter threads. Idempotent — calling on an already-running profiler is a no-op.
    /// </summary>
    /// <param name="parent">Resource-tree parent for the consumer thread. Typically <c>registry.Profiler</c>.</param>
    /// <param name="metadata">Static session description passed to each exporter's <see cref="IProfilerExporter.Initialize"/>.</param>
    /// <param name="options">Tunable parameters. <c>null</c> uses defaults (1 ms cadence, 4-deep queues, 8 KB merge buffer).</param>
    public static void Start(IResource parent, ProfilerSessionMetadata metadata, ProfilerOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(metadata);

        lock (LifecycleLock)
        {
            if (Running)
            {
                return; // idempotent
            }

            options ??= new ProfilerOptions();
            options.Validate();

            // Snapshot the exporter list so the consumer iterates a stable instance even if a future API ever allows live mutation.
            var exporterSnapshot = new List<IProfilerExporter>(Exporters);

            // Initialize each exporter BEFORE the consumer starts producing batches.
            foreach (var exporter in exporterSnapshot)
            {
                exporter.Initialize(metadata);
            }

            // Spawn one OS thread per exporter to drain its queue. They block on GetConsumingEnumerable until CompleteAdding is called.
            ExporterCts = new CancellationTokenSource();
            ExporterThreads = new Thread[exporterSnapshot.Count];
            for (var i = 0; i < exporterSnapshot.Count; i++)
            {
                var exporter = exporterSnapshot[i];
                ExporterThreads[i] = new Thread(() => ExporterConsumeLoop(exporter, ExporterCts.Token))
                {
                    IsBackground = true,
                    Name = $"TyphonProfilerExporter[{exporter.Name}]"
                };
                ExporterThreads[i].Start();
            }

            // Build and start the consumer drain thread. HighResolutionTimerServiceBase.Start spawns the timer thread.
            Consumer = new ProfilerConsumerThread(parent, options, exporterSnapshot);
            Consumer.Start();

            // Opt-in .NET runtime GC tracing. The host is only constructed when configuration explicitly enabled it — no cost otherwise.
            if (TelemetryConfig.ProfilerGcTracingActive)
            {
                GcTracing = new GcTracingHost();
                GcTracing.Start();
            }

            Running = true;
        }
    }

    /// <summary>
    /// Stop the profiler. Performs a final drain pass, signals all exporter queues to complete, joins all threads, then calls
    /// <see cref="IProfilerExporter.Flush"/> + <see cref="IDisposable.Dispose"/> on each attached exporter. Idempotent.
    /// </summary>
    public static void Stop()
    {
        ProfilerConsumerThread consumerToTearDown;
        Thread[] exporterThreadsToJoin;
        CancellationTokenSource ctsToCancel;
        List<IProfilerExporter> exportersToFlush;
        GcTracingHost gcTracingToDispose;

        lock (LifecycleLock)
        {
            if (!Running)
            {
                return;
            }

            consumerToTearDown = Consumer;
            exporterThreadsToJoin = ExporterThreads;
            ctsToCancel = ExporterCts;
            exportersToFlush = consumerToTearDown.Exporters;
            gcTracingToDispose = GcTracing;

            Consumer = null;
            ExporterThreads = null;
            ExporterCts = null;
            GcTracing = null;
            Running = false;
        }

        // Detach GC tracing first so the CLR stops delivering events before we start tearing down the consumer side.
        // Stop() drains the ingestion thread's final records into the ring so the consumer's FinalDrainAndComplete picks them up.
        gcTracingToDispose?.Dispose();

        // ── Tear-down outside the lock so blocked operations don't deadlock with future Start/Stop callers ──

        // 1. Stop the timer loop WITHOUT disposing the consumer. After this returns, no timer-driven DrainAndFanOut can be running, so the final
        //    drain in step 2 has exclusive access to _mergeScratch / _offsets. Keeping the object alive means the final drain runs against fully
        //    initialized state instead of relying on "Dispose happened to not null these fields" brittleness.
        consumerToTearDown.StopTimer();

        // 2. Final drain + signal exporter queues to complete.
        consumerToTearDown.FinalDrainAndComplete();

        // Snapshot the exporter-queue drop count now, while the exporter list is still accessible.
        long exporterDrops = 0;
        foreach (var exporter in exportersToFlush)
        {
            exporterDrops += exporter.Queue.DroppedBatches;
        }
        STotalDroppedExporterBatches = exporterDrops;
        TotalBatchesFannedOut = consumerToTearDown.BatchesFannedOut;
        TotalRecordsFannedOut = consumerToTearDown.RecordsFannedOut;
        FinalDrainPasses = consumerToTearDown.FinalDrainPasses;
        FinalDrainZeroProgressPasses = consumerToTearDown.FinalDrainZeroProgressPasses;
        FinalDrainPendingBytes = consumerToTearDown.FinalDrainPendingBytes;
        SSlotStateDump = ProfilerConsumerThread.DumpSlotStates();

        // 3. Now dispose the consumer — timer is already stopped, so Dispose just runs the managed-resource cleanup + resource-tree deregistration.
        consumerToTearDown.Dispose();

        // 4. Wait for exporter threads to finish processing the tail.
        foreach (var t in exporterThreadsToJoin)
        {
            if (!t.Join(TimeSpan.FromSeconds(5)))
            {
                // Exporter is stuck — cancel the token to force the foreach loop to exit and try once more.
                ctsToCancel.Cancel();
                t.Join(TimeSpan.FromSeconds(1));
            }
        }

        ctsToCancel.Dispose();

        // Snapshot the first FileExporter's processed counters — captures how many batches/records actually made it to disk.
        // Compare against <see cref="TotalBatchesFannedOut"/> to see if records were lost between fan-out and file-write.
        foreach (var exporter in exportersToFlush)
        {
            if (exporter is Exporters.FileExporter fe)
            {
                FirstExporterBatchesProcessed = fe.BatchesProcessed;
                FirstExporterRecordsProcessed = fe.RecordsProcessed;
                break;
            }
        }

        // 4. Flush + dispose each exporter on the calling thread.
        foreach (var exporter in exportersToFlush)
        {
            try { exporter.Flush(); } catch { /* swallow flush errors during shutdown */ }
            try { exporter.Dispose(); } catch { /* swallow dispose errors during shutdown */ }
        }
    }

    /// <summary>
    /// Loop body for each exporter's dedicated thread. Drains the queue via the blocking enumerator until <c>CompleteAdding</c> is called or the
    /// cancellation token fires. Each batch is processed and then released so the pool refcount stays balanced.
    /// </summary>
    private static void ExporterConsumeLoop(IProfilerExporter exporter, CancellationToken ct)
    {
        try
        {
            foreach (var batch in exporter.Queue.GetConsumingEnumerable(ct))
            {
                try
                {
                    exporter.ProcessBatch(batch);
                }
                catch
                {
                    // Swallow per-batch exporter errors so one bad batch doesn't kill the whole exporter thread.
                    // Future: log via [LoggerMessage] once profiler logging is in place.
                }
                finally
                {
                    batch.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via the cancellation token — exit silently.
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Total events dropped across all slots due to ring-buffer overflow.</summary>
    public static long TotalDroppedEvents => TyphonEvent.TotalDroppedEvents;

    /// <summary>
    /// Snapshot of total exporter-queue drops captured at <see cref="Stop"/> time (before the exporter list is cleared). Non-zero
    /// means the consumer produced batches faster than the exporter could drain them — events made it past the producer ring but
    /// didn't reach the sink (file / TCP wire). Distinct from <see cref="TotalDroppedEvents"/> which is producer-side ring overflow.
    /// Read AFTER <see cref="Stop"/> for post-mortem diagnostics.
    /// </summary>
    public static long TotalDroppedExporterBatches => STotalDroppedExporterBatches;
    private static long STotalDroppedExporterBatches;

    /// <summary>Diagnostic: batches/records the consumer fanned out (snapshot at Stop).</summary>
    public static long TotalBatchesFannedOut { get; private set; }
    public static long TotalRecordsFannedOut { get; private set; }

    /// <summary>Diagnostic: batches/records the FIRST attached exporter actually processed (snapshot at Stop).</summary>
    public static long FirstExporterBatchesProcessed { get; private set; }
    public static long FirstExporterRecordsProcessed { get; private set; }

    /// <summary>Diagnostic: how many final-drain passes ran before the consumer's <c>AllSlotsEmpty</c> returned true.</summary>
    public static long FinalDrainPasses { get; private set; }
    public static long FinalDrainZeroProgressPasses { get; private set; }
    public static long FinalDrainPendingBytes { get; private set; }

    public static string SlotStateDump => SSlotStateDump ?? "(not captured)";
    private static string SSlotStateDump;

    /// <summary>Number of slots currently claimed.</summary>
    public static int ActiveSlotCount => TyphonEvent.ActiveSlotCount;

    /// <summary>
    /// Reset the profiler's static state. Tests only — must not be called while the profiler is running on production traffic.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (LifecycleLock)
        {
            if (Running)
            {
                throw new InvalidOperationException("Stop the profiler before resetting test state.");
            }
            Exporters.Clear();
        }
    }
}
