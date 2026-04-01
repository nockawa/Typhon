using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Production DAG scheduler for the Typhon Runtime. Executes a static system DAG on a pool of worker threads, with any-worker dispatch and
/// inline continuation.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="HighResolutionTimerServiceBase"/> to leverage its three-phase wait (Sleep → Yield → Spin), self-calibrated sleep threshold,
/// timing error tracking, and <see cref="ResourceNode"/> lifecycle.
/// </para>
/// <para>
/// The timer thread acts as a metronome: it resets per-tick state, bumps the generation counter (waking workers), and waits for tick completion.
/// All dispatch decisions happen on workers (POC decision D2: any-worker dispatch, no scheduler thread).
/// </para>
/// <para>
/// Between ticks, workers use a three-phase wait referencing <c>_nextTickTimestamp</c> (set by the timer thread). This saves CPU during the idle gap while
/// maintaining sub-microsecond wake latency in the final spin phase.
/// </para>
/// </remarks>
[PublicAPI]
public sealed partial class DagScheduler : HighResolutionTimerServiceBase
{
    // ═══════════════════════════════════════════════════════════════
    // Immutable DAG structure
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Static DAG system definitions (name, type, priority, dependencies). Immutable after construction.</summary>
    public SystemDefinition[] Systems { get; }

    private readonly int _systemCount;
    private readonly int[] _rootSystems;
    private readonly int _workerCount;
    private readonly RuntimeOptions _options;
    private readonly int[] _topologicalOrder;
    private readonly EventQueueBase[] _eventQueues;

    // ═══════════════════════════════════════════════════════════════
    // Per-system mutable state (reset each tick)
    // ═══════════════════════════════════════════════════════════════

    private readonly CacheLinePaddedInt[] _nextChunk;
    private readonly CacheLinePaddedInt[] _remainingChunks;
    private readonly CacheLinePaddedInt[] _remainingDeps;
    private readonly int[] _isReady;
    private readonly bool[] _systemFailed;

    // Reset templates (immutable after construction)
    private readonly int[] _templateDeps;
    private readonly int[] _templateChunks;

    // ═══════════════════════════════════════════════════════════════
    // Workers
    // ═══════════════════════════════════════════════════════════════

    private readonly Thread[] _workers;

    // ═══════════════════════════════════════════════════════════════
    // Tick synchronization
    // ═══════════════════════════════════════════════════════════════

    private int _tickGeneration;
    private int _tickInProgress;
    private int _workerShutdown;
    private CacheLinePaddedInt _systemsRemaining;
    private long _nextTickTimestamp;       // Used by GetNextTick for metronome advancement
    private long _currentTickNumber;

    // Between-tick wake signal. Workers block on this (kernel wait = zero CPU).
    // Timer thread sets it when bumping the generation counter.
    // SpinCount=0: go straight to kernel wait (no user-mode spinning — the tick interval is ms-scale, so spinning would waste CPU for no benefit).
    private readonly ManualResetEventSlim _tickStartSignal = new(false, 0);

    // Tick interval in Stopwatch ticks
    private readonly long _tickIntervalTicks;

    // ═══════════════════════════════════════════════════════════════
    // Overload management
    // ═══════════════════════════════════════════════════════════════

    private readonly OverloadDetector _overloadDetector;
    private int _tickMultiplier = 1;

    // ═══════════════════════════════════════════════════════════════
    // Telemetry
    // ═══════════════════════════════════════════════════════════════

    private readonly TickTelemetryRing _telemetryRing;
    private readonly SystemTelemetry[] _currentTickSystemMetrics;
    private long _previousTickStart; // For tick-to-tick interval measurement

    // Per-worker telemetry accumulators (deep mode)
    private readonly long[] _workerActiveTicks;
    private readonly long[] _workerIdleTicks;

    // ═══════════════════════════════════════════════════════════════
    // Tick lifecycle hooks (set by TyphonRuntime)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Called at tick start (before root dispatch). Creates UoW.</summary>
    internal Action<DagScheduler> TickStartCallback;

    /// <summary>Called at tick end (after all systems complete). Flushes/disposes UoW.</summary>
    internal Action<DagScheduler> TickEndCallback;

    /// <summary>Called before a CallbackSystem/QuerySystem executes. Creates per-system Transaction. Returns TickContext.</summary>
    internal Func<int, TickContext> SystemStartCallback;

    /// <summary>Called after a CallbackSystem/QuerySystem completes. Commits/disposes per-system Transaction.</summary>
    internal Action<int, bool> SystemEndCallback;

    /// <summary>
    /// Optional callback to enrich <see cref="TickTelemetry"/> with additional metrics (e.g., subscription Output phase).
    /// Called during <see cref="ComputeAndRecordTelemetry"/> before recording.
    /// </summary>
    internal EnrichTelemetryDelegate TelemetryEnrichCallback;

    internal delegate void EnrichTelemetryDelegate(ref TickTelemetry telemetry);

    /// <summary>Called when overload level transitions to <see cref="OverloadLevel.PlayerShedding"/>.</summary>
    internal Action OnCriticalOverloadCallback;

    // ── Parallel QuerySystem callbacks (set by TyphonRuntime) ──

    /// <summary>Called once before parallel QuerySystem chunk dispatch. Builds entity set, returns totalChunks (0 = skip).</summary>
    internal Func<int, int> ParallelQueryPrepareCallback;

    /// <summary>Called per chunk: creates Transaction on worker thread, slices entities, calls Execute, commits.</summary>
    internal Action<int, int, int> ParallelQueryChunkCallback;

    /// <summary>Called once after all chunks complete (or on skip). Returns pooled entity list.</summary>
    internal Action<int> ParallelQueryCleanupCallback;

    // ═══════════════════════════════════════════════════════════════
    // Logging
    // ═══════════════════════════════════════════════════════════════

    private readonly ILogger _logger;

    // ═══════════════════════════════════════════════════════════════
    // HighResolutionTimerServiceBase overrides
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override string ThreadName => "Typhon.TickDriver";

    /// <inheritdoc />
    protected override long GetNextTick()
    {
        if (_workerShutdown != 0)
        {
            return long.MaxValue;
        }

        _nextTickTimestamp += _tickIntervalTicks * _tickMultiplier;
        return _nextTickTimestamp;
    }

    /// <inheritdoc />
    protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
    {
        if (_workerShutdown != 0)
        {
            return;
        }

        if (_workerCount == 1)
        {
            ExecuteTickSingleThreaded(actualTick);
        }
        else
        {
            ExecuteTickMultiThreaded(actualTick);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new DAG scheduler.
    /// </summary>
    /// <param name="systems">System definitions from <see cref="DagBuilder.Build"/>.</param>
    /// <param name="topologicalOrder">Topological order from <see cref="DagBuilder.Build"/>.</param>
    /// <param name="options">Runtime configuration.</param>
    /// <param name="parent">Parent resource node (typically <see cref="IResourceRegistry.Runtime"/>).</param>
    /// <param name="eventQueues">Event queues to reset at each tick start. Can be empty.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DagScheduler(SystemDefinition[] systems, int[] topologicalOrder, RuntimeOptions options, IResource parent, EventQueueBase[] eventQueues = null, 
        ILogger logger = null) : base("DagScheduler", parent)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(topologicalOrder);
        ArgumentNullException.ThrowIfNull(options);

        Systems = systems;
        _topologicalOrder = topologicalOrder;
        _systemCount = systems.Length;
        _options = options;
        _eventQueues = eventQueues ?? [];
        _logger = logger ?? NullLogger.Instance;
        _workerCount = options.ResolveWorkerCount();

        // Tick interval
        _tickIntervalTicks = Stopwatch.Frequency / options.BaseTickRate;

        // Find root systems (zero predecessors)
        var rootCount = 0;
        for (var i = 0; i < _systemCount; i++)
        {
            if (systems[i].PredecessorCount == 0)
            {
                rootCount++;
            }
        }

        _rootSystems = new int[rootCount];
        var rootIdx = 0;
        for (var i = 0; i < _systemCount; i++)
        {
            if (systems[i].PredecessorCount == 0)
            {
                _rootSystems[rootIdx++] = i;
            }
        }

        // Allocate per-system state arrays
        _nextChunk = new CacheLinePaddedInt[_systemCount];
        _remainingChunks = new CacheLinePaddedInt[_systemCount];
        _remainingDeps = new CacheLinePaddedInt[_systemCount];
        _isReady = new int[_systemCount];
        _systemFailed = new bool[_systemCount];

        // Build reset templates
        _templateDeps = new int[_systemCount];
        _templateChunks = new int[_systemCount];
        for (var i = 0; i < _systemCount; i++)
        {
            _templateDeps[i] = systems[i].PredecessorCount;
            _templateChunks[i] = systems[i].TotalChunks;
        }

        // Overload detection
        _overloadDetector = new OverloadDetector(options.Overload, options.BaseTickRate);

        // Telemetry
        var ringCapacity = options.TelemetryRingCapacity;
        if (ringCapacity < 1 || (ringCapacity & (ringCapacity - 1)) != 0)
        {
            ringCapacity = 1024;
        }

        _telemetryRing = new TickTelemetryRing(ringCapacity, _systemCount);
        _currentTickSystemMetrics = new SystemTelemetry[_systemCount];

        // Per-worker telemetry
        _workerActiveTicks = new long[_workerCount];
        _workerIdleTicks = new long[_workerCount];

        // Create worker threads (not started yet)
        if (_workerCount > 1)
        {
            _workers = new Thread[_workerCount];
            for (var i = 0; i < _workerCount; i++)
            {
                var workerId = i;
                _workers[i] = new Thread(() => WorkerLoop(workerId))
                {
                    IsBackground = true,
                    Name = $"Typhon.Worker-{i}"
                };
            }
        }
        else
        {
            _workers = [];
        }

        // Initialize next tick timestamp to now (first GetNextTick call will advance it)
        _nextTickTimestamp = Stopwatch.GetTimestamp();
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the worker threads and the tick driver timer thread.
    /// </summary>
    public new void Start()
    {
        LogStarted(_systemCount, _workerCount, _options.BaseTickRate);

        // Start worker threads
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i].Start();
            LogWorkerStarted(i);
        }

        // Start the timer thread (HighResolutionTimerServiceBase.Start)
        base.Start();
    }

    /// <summary>
    /// Shuts down the scheduler: stops accepting new ticks, waits for the current tick to finish, joins worker threads, then stops the timer thread.
    /// </summary>
    public void Shutdown()
    {
        LogShutdownRequested();

        // Signal workers to exit
        _workerShutdown = 1;
        Interlocked.Increment(ref _tickGeneration);
        _tickStartSignal.Set(); // Wake any blocked workers

        // Join worker threads (guard against unstarted threads)
        JoinWorkers();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            // Ensure workers are signaled to stop
            _workerShutdown = 1;
            Interlocked.Increment(ref _tickGeneration);
            _tickStartSignal.Set();
            JoinWorkers();
            _tickStartSignal.Dispose();
        }

        // Base class stops the timer thread and disposes the resource node
        base.Dispose(disposing);
    }

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Telemetry ring buffer for diagnostic inspection.</summary>
    public TickTelemetryRing Telemetry => _telemetryRing;

    /// <summary>Returns a ref to the current tick's SystemTelemetry for the given system index. Used by TyphonRuntime to write entity counts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref SystemTelemetry GetCurrentSystemMetrics(int sysIdx) => ref _currentTickSystemMetrics[sysIdx];

    /// <summary>Returns the event queue at the given index. Used by TyphonRuntime to populate TickContext.ConsumedQueues.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EventQueueBase GetEventQueue(int index) => _eventQueues[index];

    /// <summary>Number of event queues registered.</summary>
    internal int EventQueueCount => _eventQueues.Length;

    /// <summary>Current overload response level.</summary>
    public OverloadLevel CurrentOverloadLevel => _overloadDetector.CurrentLevel;

    /// <summary>Number of worker threads.</summary>
    public int WorkerCount => _workerCount;

    /// <summary>Number of systems in the DAG.</summary>
    public int SystemCount => _systemCount;

    /// <summary>Number of ticks executed so far.</summary>
    public long CurrentTickNumber => _currentTickNumber;

    // ═══════════════════════════════════════════════════════════════
    // Multi-threaded tick execution
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteTickMultiThreaded(long tickStartTimestamp)
    {
        // 1. Reset per-system state
        ResetTickState();

        // 2. Tick start hook (TyphonRuntime creates UoW)
        TickStartCallback?.Invoke(this);

        // 3. Mark root systems ready (evaluate runIf and ReactiveSkip for roots before waking workers)
        var readyNow = Stopwatch.GetTimestamp();
        foreach (var root in _rootSystems)
        {
            _currentTickSystemMetrics[root].ReadyTick = readyNow;
            var sys = Systems[root];
            if (sys.RunIf != null && !sys.RunIf())
            {
                _currentTickSystemMetrics[root].SkipReason = SkipReason.RunIfFalse;
                // Skip root — dispatch its successors immediately.
                // Safe to call here: workers haven't woken yet.
                OnSystemComplete(root, -1, false);
            }
            else if (sys.ReactiveSkip != null && sys.ReactiveSkip())
            {
                _currentTickSystemMetrics[root].SkipReason = SkipReason.EmptyInput;
                OnSystemComplete(root, -1, false);
            }
            else
            {
                var overloadSkip = CheckOverloadSkip(root);
                if (overloadSkip != SkipReason.NotSkipped)
                {
                    _currentTickSystemMetrics[root].SkipReason = overloadSkip;
                    OnSystemComplete(root, -1, false);
                }
                else if (sys.IsParallelQuery)
                {
                    DispatchParallelQuery(root, -1, false);
                }
                else
                {
                    MarkSystemReady(root);
                }
            }
        }

        // 3. Activate tick — bump generation + signal workers (D7)
        _tickInProgress = 1;
        Interlocked.Increment(ref _tickGeneration);
        _tickStartSignal.Set();

        // 4. Wait for all systems to complete.
        //    The timer thread must spin here — Thread.Yield() on Windows can delay up to 15.6ms,
        //    which would cascade into all subsequent ticks. One core spinning for the tick
        //    duration (~0.1-5ms) is acceptable; delayed completion detection is not.
        while (_systemsRemaining.Value > 0)
        {
            Thread.SpinWait(1);
        }

        _tickInProgress = 0;
        _tickStartSignal.Reset(); // Workers will block again on next between-tick wait

        // 5. Tick end hook (TyphonRuntime flushes/disposes UoW)
        TickEndCallback?.Invoke(this);

        var tickEndTimestamp = Stopwatch.GetTimestamp();

        // 6. Record telemetry
        //    Note: _nextTickTimestamp is updated by GetNextTick() which the base timer loop
        //    calls immediately after this method returns. Workers briefly see a stale value
        //    (~100ns) and spin, then GetNextTick publishes the correct target and they sleep.
        ComputeAndRecordTelemetry(tickStartTimestamp, tickEndTimestamp);
    }

    // ═══════════════════════════════════════════════════════════════
    // Single-threaded tick execution (WorkerCount == 1)
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteTickSingleThreaded(long tickStartTimestamp)
    {
        // Reset metrics, event queues, and failure flags
        for (var i = 0; i < _systemCount; i++)
        {
            _currentTickSystemMetrics[i] = default;
        }

        Array.Clear(_systemFailed);

        foreach (var queue in _eventQueues)
        {
            queue.Reset();
        }

        // Tick start hook
        TickStartCallback?.Invoke(this);

        // Execute in topological order
        for (var i = 0; i < _topologicalOrder.Length; i++)
        {
            var sysIdx = _topologicalOrder[i];
            var sys = Systems[sysIdx];

            var readyTick = Stopwatch.GetTimestamp();
            _currentTickSystemMetrics[sysIdx].ReadyTick = readyTick;

            // Check if a predecessor failed
            if (_systemFailed[sysIdx])
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.DependencyFailed;
                // Propagate failure to successors
                foreach (var succ in sys.Successors)
                {
                    _systemFailed[succ] = true;
                }

                continue;
            }

            // Evaluate runIf
            if (sys.RunIf != null && !sys.RunIf())
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.RunIfFalse;
                continue;
            }

            if (sys.ReactiveSkip != null && sys.ReactiveSkip())
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.EmptyInput;
                continue;
            }

            {
                var overloadSkip = CheckOverloadSkip(sysIdx);
                if (overloadSkip != SkipReason.NotSkipped)
                {
                    _currentTickSystemMetrics[sysIdx].SkipReason = overloadSkip;
                    continue;
                }
            }

            var startTick = Stopwatch.GetTimestamp();
            _currentTickSystemMetrics[sysIdx].FirstChunkGrabTick = startTick;

            if (sys.IsParallelQuery)
            {
                var totalChunks = ParallelQueryPrepareCallback?.Invoke(sysIdx) ?? 0;
                if (totalChunks <= 0)
                {
                    ParallelQueryCleanupCallback?.Invoke(sysIdx);
                }
                else
                {
                    Systems[sysIdx].TotalChunks = totalChunks;
                    var chunkFailed = false;
                    for (var chunk = 0; chunk < totalChunks; chunk++)
                    {
                        try
                        {
                            ParallelQueryChunkCallback?.Invoke(sysIdx, chunk, totalChunks);
                        }
                        catch (Exception ex)
                        {
                            chunkFailed = true;
                            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                            _systemFailed[sysIdx] = true;
                            LogSystemException(sysIdx, sys.Name, ex);
                            foreach (var succ in sys.Successors)
                            {
                                _systemFailed[succ] = true;
                            }
                        }
                    }

                    ParallelQueryCleanupCallback?.Invoke(sysIdx);
                    if (chunkFailed)
                    {
                        // Failure already propagated above
                    }
                }
            }
            else if (sys.Type == SystemType.PipelineSystem)
            {
                for (var chunk = 0; chunk < sys.TotalChunks; chunk++)
                {
                    try
                    {
                        sys.PipelineChunkAction(chunk, sys.TotalChunks);
                    }
                    catch (Exception ex)
                    {
                        _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                        _systemFailed[sysIdx] = true;
                        LogSystemException(sysIdx, sys.Name, ex);
                        foreach (var succ in sys.Successors)
                        {
                            _systemFailed[succ] = true;
                        }
                    }
                }
            }
            else // CallbackSystem or non-parallel QuerySystem — single invocation
            {
                var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
                var success = true;
                try
                {
                    sys.CallbackAction(ctx);
                }
                catch (Exception ex)
                {
                    success = false;
                    _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                    _systemFailed[sysIdx] = true;
                    LogSystemException(sysIdx, sys.Name, ex);
                    // Propagate failure to successors
                    foreach (var succ in sys.Successors)
                    {
                        _systemFailed[succ] = true;
                    }
                }

                SystemEndCallback?.Invoke(sysIdx, success);
            }

            var endTick = Stopwatch.GetTimestamp();
            _currentTickSystemMetrics[sysIdx].LastChunkDoneTick = endTick;
            _currentTickSystemMetrics[sysIdx].WorkersTouched = sys.IsParallelQuery ? sys.TotalChunks : 1;
        }

        // Tick end hook
        TickEndCallback?.Invoke(this);

        var tickEndTimestamp = Stopwatch.GetTimestamp();
        ComputeAndRecordTelemetry(tickStartTimestamp, tickEndTimestamp);
    }

    // ═══════════════════════════════════════════════════════════════
    // Worker loop
    // ═══════════════════════════════════════════════════════════════

    private void WorkerLoop(int workerId)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        LogWorkerStarted(workerId);

        var lastGen = _tickGeneration;

        while (_workerShutdown == 0)
        {
            // ═══ Between-tick: kernel wait on signal ═══
            // Workers block here with zero CPU cost. The timer thread signals _tickStartSignal when the next tick fires. Wake latency is ~1-5µs
            // (kernel transition) — negligible against a 16ms tick gap.
            while (_tickGeneration == lastGen)
            {
                if (_workerShutdown != 0)
                {
                    return;
                }

                _tickStartSignal.Wait(TimeSpan.FromMilliseconds(50));
            }

            if (_workerShutdown != 0)
            {
                return;
            }

            lastGen = _tickGeneration;

            // ═══ Within-tick: find and process work ═══
            var trackUtilization = TelemetryConfig.SchedulerActive && TelemetryConfig.SchedulerTrackWorkerUtilization;

            var idleSpins = 0;
            while (_tickInProgress == 1 && _systemsRemaining.Value > 0)
            {
                var sysIdx = FindReadySystem();
                if (sysIdx >= 0)
                {
                    idleSpins = 0;
                    ProcessSystem(sysIdx, workerId, trackUtilization);
                }
                else
                {
                    // D5: spin briefly, then yield.
                    // First ~100 iterations (~1µs) spin with PAUSE for lowest latency.
                    // After that, yield the core — there's genuinely no work and spinning wastes CPU on narrow DAGs. Adds ~1µs dispatch latency but saves a core.
                    idleSpins++;
                    if (idleSpins <= 100)
                    {
                        if (trackUtilization)
                        {
                            var idleStart = Stopwatch.GetTimestamp();
                            Thread.SpinWait(4);
                            _workerIdleTicks[workerId] += Stopwatch.GetTimestamp() - idleStart;
                        }
                        else
                        {
                            Thread.SpinWait(4);
                        }
                    }
                    else
                    {
                        if (trackUtilization)
                        {
                            var idleStart = Stopwatch.GetTimestamp();
                            Thread.Yield();
                            _workerIdleTicks[workerId] += Stopwatch.GetTimestamp() - idleStart;
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // System discovery and dispatch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Linear scan of ready systems. Returns the index of a system that can be processed, or -1 if no work is available.
    /// </summary>
    /// <remarks>
    /// POC validated this O(n) scan is negligible up to 1,000 systems.
    /// The _isReady array fits in 2 cache lines for 16 systems — hot in L1.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindReadySystem()
    {
        for (var i = 0; i < _systemCount; i++)
        {
            if (_isReady[i] != 1)
            {
                continue;
            }

            if (Systems[i].Type == SystemType.PipelineSystem || Systems[i].IsParallelQuery)
            {
                // Multi-chunk system: only return if chunks remain
                if (_nextChunk[i].Value < Systems[i].TotalChunks)
                {
                    return i;
                }
            }
            else
            {
                // CallbackSystem/non-parallel QuerySystem: return index, CAS claim happens in ProcessSystem
                return i;
            }
        }

        return -1;
    }

    private void ProcessSystem(int sysIdx, int workerId, bool trackUtilization)
    {
        var sys = Systems[sysIdx];

        if (sys.IsParallelQuery)
        {
            ProcessParallelQuery(sysIdx, workerId, trackUtilization);
        }
        else if (sys.Type == SystemType.PipelineSystem)
        {
            ProcessPipeline(sysIdx, workerId, trackUtilization);
        }
        else // CallbackSystem or non-parallel QuerySystem — same single-invocation path
        {
            ProcessCallbackOrQuery(sysIdx, workerId, trackUtilization);
        }
    }

    private void ProcessCallbackOrQuery(int sysIdx, int workerId, bool trackUtilization)
    {
        // Atomic claim: only one worker wins
        if (Interlocked.CompareExchange(ref _isReady[sysIdx], 0, 1) != 1)
        {
            return;
        }

        // runIf was already evaluated at dispatch time (OnSystemComplete or root marking).
        // System lifecycle hook: create per-system Transaction (called on the worker thread)
        var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
        var workStart = Stopwatch.GetTimestamp();
        RecordFirstChunkGrab(sysIdx, workStart);

        var success = true;
        try
        {
            Systems[sysIdx].CallbackAction(ctx);
        }
        catch (Exception ex)
        {
            success = false;
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
            _systemFailed[sysIdx] = true;
            LogSystemException(sysIdx, Systems[sysIdx].Name, ex);
        }
        finally
        {
            // System lifecycle hook: commit/dispose per-system Transaction
            SystemEndCallback?.Invoke(sysIdx, success);

            var workEnd = Stopwatch.GetTimestamp();
            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            RecordSystemDone(sysIdx, workEnd);
            _currentTickSystemMetrics[sysIdx].WorkersTouched = 1;
        }

        OnSystemComplete(sysIdx, workerId, trackUtilization);
    }

    private void ProcessPipeline(int sysIdx, int workerId, bool trackUtilization)
    {
        // runIf was already evaluated at dispatch time. If we're here, the system should execute.
        var sys = Systems[sysIdx];

        while (true)
        {
            // Early-exit: stop grabbing chunks if a prior chunk already failed — remaining work would be discarded
            if (_systemFailed[sysIdx])
            {
                break;
            }

            var chunk = Interlocked.Increment(ref _nextChunk[sysIdx].Value) - 1;
            if (chunk >= sys.TotalChunks)
            {
                break;
            }

            if (chunk == 0)
            {
                RecordFirstChunkGrab(sysIdx, Stopwatch.GetTimestamp());
            }

            var workStart = Stopwatch.GetTimestamp();
            try
            {
                sys.PipelineChunkAction(chunk, sys.TotalChunks);
            }
            catch (Exception ex)
            {
                _systemFailed[sysIdx] = true;
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                LogSystemException(sysIdx, sys.Name, ex);
            }

            var workEnd = Stopwatch.GetTimestamp();

            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            // D8: countdown — last completer dispatches successors
            var remaining = Interlocked.Decrement(ref _remainingChunks[sysIdx].Value);
            if (remaining == 0)
            {
                RecordSystemDone(sysIdx, workEnd);
                OnSystemComplete(sysIdx, workerId, trackUtilization);
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel QuerySystem dispatch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Multi-worker chunk dispatch for parallel QuerySystems. Each worker grabs chunks via atomic counter.
    /// Unlike <see cref="ProcessPipeline"/>, each chunk has its own Transaction lifecycle (managed by the chunk callback).
    /// </summary>
    private void ProcessParallelQuery(int sysIdx, int workerId, bool trackUtilization)
    {
        var sys = Systems[sysIdx];
        while (true)
        {
            // Early-exit: stop grabbing chunks if a prior chunk already failed — remaining work would be discarded
            if (_systemFailed[sysIdx])
            {
                break;
            }

            var chunk = Interlocked.Increment(ref _nextChunk[sysIdx].Value) - 1;
            if (chunk >= sys.TotalChunks)
            {
                break;
            }

            if (chunk == 0)
            {
                RecordFirstChunkGrab(sysIdx, Stopwatch.GetTimestamp());
            }

            var workStart = Stopwatch.GetTimestamp();
            try
            {
                ParallelQueryChunkCallback?.Invoke(sysIdx, chunk, sys.TotalChunks);
            }
            catch (Exception ex)
            {
                _systemFailed[sysIdx] = true;
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                LogSystemException(sysIdx, sys.Name, ex);
            }

            var workEnd = Stopwatch.GetTimestamp();

            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            // D8: countdown — last completer dispatches successors and runs cleanup
            var remaining = Interlocked.Decrement(ref _remainingChunks[sysIdx].Value);
            if (remaining == 0)
            {
                RecordSystemDone(sysIdx, workEnd);
                _currentTickSystemMetrics[sysIdx].WorkersTouched = sys.TotalChunks;
                ParallelQueryCleanupCallback?.Invoke(sysIdx);
                OnSystemComplete(sysIdx, workerId, trackUtilization);
                break;
            }
        }
    }

    /// <summary>
    /// Prepares and dispatches a parallel QuerySystem. Called from <see cref="OnSystemComplete"/> (successor dispatch)
    /// or root system marking. Runs the prepare callback to compute chunk count, then either marks the system ready
    /// for multi-worker chunk grabbing or handles the zero-entity skip case.
    /// </summary>
    private void DispatchParallelQuery(int sysIdx, int workerId, bool trackUtilization)
    {
        var totalChunks = ParallelQueryPrepareCallback?.Invoke(sysIdx) ?? 0;
        if (totalChunks <= 0)
        {
            // Empty entity set — skip, dispatch successors
            ParallelQueryCleanupCallback?.Invoke(sysIdx);
            OnSystemComplete(sysIdx, workerId, trackUtilization);
            return;
        }

        Systems[sysIdx].TotalChunks = totalChunks;
        _remainingChunks[sysIdx].Value = totalChunks;
        // MEMORY: relies on x86 TSO for store ordering — needs release/acquire barrier on ARM
        MarkSystemReady(sysIdx);
    }

    // ═══════════════════════════════════════════════════════════════
    // Completion and successor dispatch
    // ═══════════════════════════════════════════════════════════════

    private void OnSystemComplete(int sysIdx, int workerId, bool trackUtilization)
    {
        Interlocked.Decrement(ref _systemsRemaining.Value);

        // D2: any-worker dispatch — iterate successors
        var successors = Systems[sysIdx].Successors;
        foreach (var succIdx in successors)
        {
            // Propagate failure: writing true to a bool is idempotent and atomic on x86
            if (_systemFailed[sysIdx])
            {
                _systemFailed[succIdx] = true;
            }

            var depsLeft = Interlocked.Decrement(ref _remainingDeps[succIdx].Value);
            if (depsLeft == 0)
            {
                _currentTickSystemMetrics[succIdx].ReadyTick = Stopwatch.GetTimestamp();
                var succ = Systems[succIdx];

                // Check if any predecessor failed — skip this system entirely
                if (_systemFailed[succIdx])
                {
                    _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.DependencyFailed;
                    OnSystemComplete(succIdx, workerId, trackUtilization);
                }
                // Evaluate runIf here — before any worker can grab chunks.
                // This is thread-safe: only one thread decrements the last dependency to zero.
                else if (succ.RunIf != null && !succ.RunIf())
                {
                    _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.RunIfFalse;
                    OnSystemComplete(succIdx, workerId, trackUtilization);
                }
                else if (succ.ReactiveSkip != null && succ.ReactiveSkip())
                {
                    _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.EmptyInput;
                    OnSystemComplete(succIdx, workerId, trackUtilization);
                }
                else
                {
                    var overloadSkip = CheckOverloadSkip(succIdx);
                    if (overloadSkip != SkipReason.NotSkipped)
                    {
                        _currentTickSystemMetrics[succIdx].SkipReason = overloadSkip;
                        OnSystemComplete(succIdx, workerId, trackUtilization);
                    }
                    else if (succ.IsParallelQuery)
                    {
                        // Parallel QuerySystem: prepare entity set, then mark ready for multi-worker chunk grab
                        DispatchParallelQuery(succIdx, workerId, trackUtilization);
                    }
                    else if (succ.Type == SystemType.CallbackSystem || succ.Type == SystemType.QuerySystem)
                    {
                        // D3: inline continuation for single-invocation successors
                        ExecuteInline(succIdx, workerId, trackUtilization);
                    }
                    else
                    {
                        MarkSystemReady(succIdx);
                    }
                }
            }
        }
    }

    private void ExecuteInline(int sysIdx, int workerId, bool trackUtilization)
    {
        // runIf was already evaluated by the caller (OnSystemComplete).
        var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
        var workStart = Stopwatch.GetTimestamp();
        RecordFirstChunkGrab(sysIdx, workStart);

        var success = true;
        try
        {
            Systems[sysIdx].CallbackAction(ctx);
        }
        catch (Exception ex)
        {
            success = false;
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
            _systemFailed[sysIdx] = true;
            LogSystemException(sysIdx, Systems[sysIdx].Name, ex);
        }
        finally
        {
            SystemEndCallback?.Invoke(sysIdx, success);

            var workEnd = Stopwatch.GetTimestamp();
            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            RecordSystemDone(sysIdx, workEnd);
            _currentTickSystemMetrics[sysIdx].WorkersTouched = 1;
        }

        // Recursively dispatch successors
        OnSystemComplete(sysIdx, workerId, trackUtilization);
    }

    // ═══════════════════════════════════════════════════════════════
    // Overload skip check
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether a system should be skipped due to tick divisor or overload throttling/shedding.
    /// Returns <see cref="SkipReason.NotSkipped"/> if the system should execute normally.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SkipReason CheckOverloadSkip(int sysIdx)
    {
        var sys = Systems[sysIdx];

        // Baseline TickDivisor (active even at Normal load)
        if (sys.TickDivisor > 1 && _currentTickNumber % sys.TickDivisor != 0)
        {
            return SkipReason.Throttled;
        }

        var level = _overloadDetector.CurrentLevel;
        if (level == OverloadLevel.Normal)
        {
            return SkipReason.NotSkipped;
        }

        // Level 1+: Shed Low-priority systems with CanShed
        if (sys.Priority == SystemPriority.Low && sys.CanShed)
        {
            return SkipReason.Shed;
        }

        // Level 1+: Throttle Normal-priority systems via ThrottledTickDivisor
        if (sys.Priority == SystemPriority.Normal && sys.ThrottledTickDivisor > 1 && _currentTickNumber % sys.ThrottledTickDivisor != 0)
        {
            return SkipReason.Throttled;
        }

        return SkipReason.NotSkipped;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void JoinWorkers()
    {
        foreach (var worker in _workers)
        {
            if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
            {
                worker.Join(TimeSpan.FromSeconds(5));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkSystemReady(int sysIdx) => _isReady[sysIdx] = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordFirstChunkGrab(int sysIdx, long timestamp) =>
        Interlocked.CompareExchange(ref _currentTickSystemMetrics[sysIdx].FirstChunkGrabTick, timestamp, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordSystemDone(int sysIdx, long timestamp) =>
        _currentTickSystemMetrics[sysIdx].LastChunkDoneTick = timestamp;

    private void ResetTickState()
    {
        for (var i = 0; i < _systemCount; i++)
        {
            _nextChunk[i].Value = 0;
            _remainingChunks[i].Value = _templateChunks[i];
            _remainingDeps[i].Value = _templateDeps[i];
            _isReady[i] = 0;
            _currentTickSystemMetrics[i] = default;
        }

        _systemsRemaining.Value = _systemCount;
        Array.Clear(_systemFailed);

        // Reset event queues at tick start
        foreach (var queue in _eventQueues)
        {
            queue.Reset();
        }

        // Reset per-worker utilization counters
        if (TelemetryConfig.SchedulerActive && TelemetryConfig.SchedulerTrackWorkerUtilization)
        {
            Array.Clear(_workerActiveTicks);
            Array.Clear(_workerIdleTicks);
        }
    }

}
