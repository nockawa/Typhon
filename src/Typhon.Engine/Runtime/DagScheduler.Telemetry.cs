using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

public sealed partial class DagScheduler
{
    /// <summary>
    /// Computes derived metrics from raw timestamps and records the tick into the telemetry ring buffer.
    /// Called at the end of every tick by the timer thread (single writer, no contention).
    /// </summary>
    private void ComputeAndRecordTelemetry(long tickStart, long tickEnd)
    {
        var tickDurationTicks = tickEnd - tickStart;
        var actualMs = (float)(tickDurationTicks * 1000.0 / Stopwatch.Frequency);
        var targetMs = 1000f / _options.BaseTickRate;
        var overrunRatio = actualMs / targetMs;

        // Tick-to-tick interval: the real period seen by the simulation
        var tickIntervalMs = 0f;
        if (_previousTickStart > 0)
        {
            tickIntervalMs = (float)((tickStart - _previousTickStart) * 1000.0 / Stopwatch.Frequency);
        }

        _previousTickStart = tickStart;

        var activeSystemCount = 0;
        var totalEntitiesProcessed = 0;

        for (var i = 0; i < _systemCount; i++)
        {
            ref var sm = ref _currentTickSystemMetrics[i];
            sm.SystemIndex = i;

            if (sm.FirstChunkGrabTick > 0 && sm.ReadyTick > 0)
            {
                activeSystemCount++;
                totalEntitiesProcessed += sm.EntitiesProcessed;
                sm.TransitionLatencyUs = TicksToUs(sm.FirstChunkGrabTick - sm.ReadyTick);
                sm.DurationUs = TicksToUs(sm.LastChunkDoneTick - sm.FirstChunkGrabTick);

                // Straggler gap for multi-chunk parallel systems (Pipeline and parallel QuerySystem).
                // Per-chunk accumulators (TotalChunkWorkTicks, MaxChunkWorkTicks, WorkerBitmap) are populated
                // on worker threads via AccumulateChunkTelemetry; here we fold them into the final metrics.
                if (TelemetryConfig.SchedulerActive && TelemetryConfig.SchedulerTrackStragglerGap)
                {
                    var sys = Systems[i];
                    if ((sys.Type == SystemType.PipelineSystem || sys.IsParallelQuery) 
                        && sys.TotalChunks > 1 && sm.WorkersTouched > 1 && sm.TotalChunkWorkTicks > 0)
                    {
                        // Ideal-parallel: if the total CPU time were split evenly across all participating workers.
                        // Wall-clock span of the system (DurationUs) minus ideal-parallel = imbalance penalty.
                        var idealParallelUs = TicksToUs(sm.TotalChunkWorkTicks) / sm.WorkersTouched;
                        var gap = sm.DurationUs - idealParallelUs;
                        
                        // Clamp: floating-point rounding can push an ideally-balanced gap microscopically negative.
                        sm.StragglerGapUs = gap > 0f ? gap : 0f;
                        sm.MaxChunkDurationUs = TicksToUs(sm.MaxChunkWorkTicks);
                    }
                }
            }
            else
            {
                if (sm.SkipReason == SkipReason.NotSkipped)
                {
                    sm.SkipReason = SkipReason.RunIfFalse;
                }
            }
        }

        // Capture event queue depth for telemetry and overload detection
        var queueDepth = 0;
        for (var i = 0; i < _eventQueues.Length; i++)
        {
            queueDepth += _eventQueues[i].Count;
        }

        // Update overload state machine
        var previousLevel = _overloadDetector.CurrentLevel;
        var levelChanged = _overloadDetector.Update(overrunRatio, queueDepth);
        _tickMultiplier = _overloadDetector.TickMultiplier;

        if (levelChanged)
        {
            LogOverloadLevelChanged(previousLevel, _overloadDetector.CurrentLevel, _currentTickNumber);

            if (_overloadDetector.CurrentLevel == OverloadLevel.PlayerShedding && previousLevel != OverloadLevel.PlayerShedding)
            {
                OnCriticalOverloadCallback?.Invoke();
            }
        }

        var tickTelemetry = new TickTelemetry
        {
            TickNumber = _currentTickNumber,
            TargetDurationMs = targetMs,
            ActualDurationMs = actualMs,
            OverrunRatio = overrunRatio,
            TickIntervalMs = tickIntervalMs,
            ActiveWorkerCount = _workerCount,
            ActiveSystemCount = activeSystemCount,
            TotalEntitiesProcessed = totalEntitiesProcessed,
            CurrentLevel = _overloadDetector.CurrentLevel,
            TickMultiplier = _tickMultiplier,
            EventQueueDepth = queueDepth
        };

        // Enrich with subscription metrics (Output phase duration, deltas pushed, overflows)
        TelemetryEnrichCallback?.Invoke(ref tickTelemetry);

        _telemetryRing.Record(in tickTelemetry, _currentTickSystemMetrics.AsSpan(0, _systemCount));

        // Warn on overrun
        if (overrunRatio > 1.0f)
        {
            LogTickOverrun(_currentTickNumber, actualMs, targetMs, overrunRatio);
        }

        _currentTickNumber++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float TicksToUs(long ticks) => (float)((double)ticks / Stopwatch.Frequency * 1_000_000.0);
}
