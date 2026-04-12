using System.Runtime.CompilerServices;

namespace Typhon.Engine;

public partial class DagScheduler
{
    /// <summary>
    /// Sets the runtime inspector for deep tracing. Must be called before <see cref="Start"/>.
    /// Only effective when <see cref="TelemetryConfig.SchedulerDeepTrace"/> is true.
    /// </summary>
    internal void SetInspector(IRuntimeInspector inspector)
    {
        if (!TelemetryConfig.SchedulerDeepTrace)
        {
            return;
        }

        _inspector = inspector;
        _inspector.OnSchedulerStarted(Systems, _workerCount, _options.BaseTickRate);
    }

    /// <summary>Notify inspector that a tick has started.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorTickStart(long tickNumber, long timestamp)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnTickStart(tickNumber, timestamp);
        }
    }

    /// <summary>Notify inspector that a tick has ended.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorTickEnd(long tickNumber, long timestamp)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnTickEnd(tickNumber, timestamp);
        }
    }

    /// <summary>Notify inspector that a system became ready.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorSystemReady(int sysIdx, long timestamp)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnSystemReady(sysIdx, timestamp);
        }
    }

    /// <summary>Notify inspector that a system was skipped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorSystemSkipped(int sysIdx, SkipReason reason, long timestamp)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnSystemSkipped(sysIdx, reason, timestamp);
        }
    }

    /// <summary>Notify inspector that a chunk started executing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorChunkStart(int sysIdx, int chunkIndex, int workerId, long timestamp, int totalChunks)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnChunkStart(sysIdx, chunkIndex, workerId, timestamp, totalChunks);
        }
    }

    /// <summary>Notify inspector that a chunk finished executing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorChunkEnd(int sysIdx, int chunkIndex, int workerId, long timestamp, int entitiesProcessed)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnChunkEnd(sysIdx, chunkIndex, workerId, timestamp, entitiesProcessed);
        }
    }

    /// <summary>Notify inspector of a phase transition.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorPhaseStart(TickPhase phase, long timestamp)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnPhaseStart(phase, timestamp);
        }
    }

    /// <summary>Notify inspector of a phase end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorPhaseEnd(TickPhase phase, long timestamp)
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnPhaseEnd(phase, timestamp);
        }
    }

    /// <summary>Flush inspector data and notify shutdown.</summary>
    private void InspectorShutdown()
    {
        if (TelemetryConfig.SchedulerDeepTrace)
        {
            _inspector?.OnSchedulerStopping();
        }
    }
}
