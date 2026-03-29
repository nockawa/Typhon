using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Abstract base class for high-resolution periodic timers using a three-phase hybrid wait strategy (Sleep → Yield → Spin) on a dedicated background thread.
/// Self-calibrates across Windows, Linux, and macOS.
/// </summary>
/// <remarks>
/// Extends <see cref="ResourceNode"/> to participate in the resource tree (under the <c>Timer</c> root node). Implements <see cref="IMetricSource"/> to
/// expose timing metrics (tick count, timing error, missed ticks) to the resource graph.
/// <para/>
/// Derived classes provide the scheduling policy via two abstract methods:
/// <see cref="GetNextTick"/> determines when to wake, and
/// <see cref="ExecuteCallbacks"/> determines what to run.
/// The base class owns the wait loop, thread lifecycle, calibration, and timing metrics.
/// </remarks>
[PublicAPI]
public abstract class HighResolutionTimerServiceBase : ResourceNode, IMetricSource
{
    // ═══════════════════════════════════════════════════════════════
    // Calibration thresholds
    // ═══════════════════════════════════════════════════════════════

    private readonly long _sleepThreshold;
    private readonly long _spinThreshold;

    // ═══════════════════════════════════════════════════════════════
    // Thread lifecycle
    // ═══════════════════════════════════════════════════════════════

    private Thread _thread;
    private volatile bool _shutdown;
    private readonly object _lifecycleLock = new();

    // ═══════════════════════════════════════════════════════════════
    // Timing metrics
    // ═══════════════════════════════════════════════════════════════

    private double _lastTimingErrorUs;

    // ═══════════════════════════════════════════════════════════════
    // Abstract methods — scheduling policy
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the <see cref="Stopwatch"/> timestamp of the next tick to wait for. Called by the timer loop after each callback execution to determine the
    /// next wake-up time.
    /// </summary>
    /// <remarks>
    /// Returning a timestamp in the past causes the wait loop to exit immediately and call <see cref="ExecuteCallbacks"/> without delay (catch-up behavior).
    /// Derived classes should use metronome-style advancement (nextTick += interval) rather than computing from current time, to prevent drift accumulation.
    /// </remarks>
    /// <returns>
    /// The <see cref="Stopwatch.GetTimestamp"/> value at which the next tick should fire. Return <see cref="long.MaxValue"/> to idle indefinitely
    /// (e.g., no registrations).
    /// </returns>
    protected abstract long GetNextTick();

    /// <summary>
    /// Execute the callback(s) due at this tick. Called by the timer loop immediately after the three-phase wait completes.
    /// </summary>
    /// <param name="scheduledTick">
    /// The <see cref="Stopwatch"/> timestamp that was targeted (as returned by <see cref="GetNextTick"/>).
    /// </param>
    /// <param name="actualTick">
    /// The <see cref="Stopwatch"/> timestamp when the wait actually completed.
    /// The difference <c>abs(actualTick - scheduledTick)</c> is the timing error for this tick.
    /// </param>
    protected abstract void ExecuteCallbacks(long scheduledTick, long actualTick);

    /// <summary>Thread name set by derived classes for diagnostics.</summary>
    protected abstract string ThreadName { get; }

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Total number of ticks executed since the timer started.</summary>
    public long TickCount { get; private set; }

    /// <summary>
    /// Number of ticks where the actual wake-up time exceeded the scheduled time by more than one expected interval (indicating the timer fell behind).
    /// </summary>
    public long MissedTicks { get; private set; }

    /// <summary>
    /// Exponential moving average of <c>abs(actualTick - scheduledTick)</c>, expressed in microseconds. Uses EMA with alpha = 0.01 (~100-sample window).
    /// </summary>
    public double MeanTimingErrorUs { get; private set; }

    /// <summary>
    /// Maximum observed timing error in microseconds since the timer started.
    /// </summary>
    public double MaxTimingErrorUs { get; private set; }

    /// <summary>Calibrated <c>Thread.Sleep(1)</c> worst-case duration, measured at startup.</summary>
    public TimeSpan CalibratedSleepResolution => TimeSpan.FromSeconds(_sleepThreshold / 1.5 / Stopwatch.Frequency);

    /// <summary>Whether the timer thread is currently running.</summary>
    public bool IsRunning => _thread != null && _thread.IsAlive;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the base class and registers in the resource tree.
    /// </summary>
    /// <param name="id">Resource node ID (e.g., "SharedTimer", "DeadlineWatchdog").</param>
    /// <param name="parent">
    /// Parent resource node. For shared timer: <c>registry.Timer</c>. For dedicated timers: <c>registry.TimerDedicated</c>.
    /// </param>
    protected HighResolutionTimerServiceBase(string id, IResource parent) : base(id, ResourceType.Service, parent)
    {
        _sleepThreshold = CalibrateSleepResolution();
        _spinThreshold = (long)(Stopwatch.Frequency * 0.000_050); // 50µs
    }

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes timing metrics for resource graph snapshots.
    /// </summary>
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("TickCount", TickCount);
        writer.WriteThroughput("MissedTicks", MissedTicks);
        writer.WriteDuration("TimingError", (long)_lastTimingErrorUs, (long)MeanTimingErrorUs, (long)MaxTimingErrorUs);
    }

    /// <inheritdoc />
    public void ResetPeaks() => MaxTimingErrorUs = MeanTimingErrorUs;

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Whether this timer has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Start the timer thread. Idempotent — does nothing if already running. For dedicated timers, call this after construction. For the shared timer,
    /// <see cref="HighResolutionSharedTimerService.Register"/> calls this automatically.
    /// </summary>
    public void Start()
    {
        if (_thread != null && _thread.IsAlive)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _shutdown = false;
            _thread = new Thread(TimerLoop)
            {
                IsBackground = true,
                Name = ThreadName
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Stops the timer thread and disposes managed resources. Follows the standard <see cref="ResourceNode"/> disposal pattern.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            _shutdown = true;
            _thread?.Join(TimeSpan.FromSeconds(2));
        }

        base.Dispose(disposing);
        IsDisposed = true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Protected helpers for derived classes
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds to the missed ticks counter. Called by derived classes when they detect the timer fell behind and had to skip forward.
    /// </summary>
    protected void AddMissedTicks(long count) => MissedTicks += count;

    // ═══════════════════════════════════════════════════════════════
    // Calibration
    // ═══════════════════════════════════════════════════════════════

    internal static long? CalibrationSleepThreshold;
    internal static void ResetCalibrationSleepThreshold() => CalibrationSleepThreshold = null;
    
    /// <summary>
    /// Measures actual Thread.Sleep(1) duration to calibrate phase thresholds. Called once during construction. Returns 1.5x the worst-case observed duration.
    /// </summary>
    private static long CalibrateSleepResolution()
    {
        const int warmupRounds = 3;
        const int measureRounds = 10;
        
        // Unit-test must not execute this method every time, the result is always the same, and we take a penalty of 20ms at least
        // So we cache the value
        if (CalibrationSleepThreshold.HasValue)
        {
            return CalibrationSleepThreshold.Value;
        }
        
        long worst = 0;

        // Warm up — let the OS scheduler settle
        for (var i = 0; i < warmupRounds; i++)
        {
            Thread.Sleep(1);
        }

        for (var i = 0; i < measureRounds; i++)
        {
            var before = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            var elapsed = Stopwatch.GetTimestamp() - before;

            if (elapsed > worst)
            {
                worst = elapsed;
            }
        }

        // 1.5x safety margin: ensures we never Sleep() when it would overshoot
        CalibrationSleepThreshold = (long)(worst * 1.5);
        return CalibrationSleepThreshold.Value;
    }

    // ═══════════════════════════════════════════════════════════════
    // Timer loop (core)
    // ═══════════════════════════════════════════════════════════════

    private void TimerLoop()
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        long tickCount = 0;

        while (!_shutdown)
        {
            // Ask derived class: when is the next tick?
            var nextTick = GetNextTick();

            // long.MaxValue = idle signal (no registrations)
            if (nextTick == long.MaxValue)
            {
                Thread.Sleep(100);
                continue;
            }

            // ── Phase 1: Sleep (CPU-friendly, coarse) ────────────────
            while (true)
            {
                var remaining = nextTick - Stopwatch.GetTimestamp();
                if (remaining <= _sleepThreshold)
                {
                    break;
                }

                Thread.Sleep(1);
            }

            // ── Phase 2: Yield (low CPU, medium precision) ───────────
            while (true)
            {
                var remaining = nextTick - Stopwatch.GetTimestamp();
                if (remaining <= _spinThreshold)
                {
                    break;
                }

                Thread.Yield();
            }

            // ── Phase 3: Spin (precise, burns CPU briefly) ───────────
            while (Stopwatch.GetTimestamp() < nextTick)
            {
                Thread.SpinWait(1);
            }

            // Record timing error
            var actualTick = Stopwatch.GetTimestamp();
            tickCount++;

            var errorTicks = Math.Abs(actualTick - nextTick);
            var errorUs = (double)errorTicks / Stopwatch.Frequency * 1_000_000.0;

            _lastTimingErrorUs = errorUs;

            // EMA with alpha = 0.01 (~100-sample smoothing window)
            MeanTimingErrorUs = MeanTimingErrorUs * 0.99 + errorUs * 0.01;

            if (errorUs > MaxTimingErrorUs)
            {
                MaxTimingErrorUs = errorUs;
            }

            TickCount = tickCount;

            // Execute callbacks (derived class policy)
            ExecuteCallbacks(nextTick, actualTick);
        }
    }
}
