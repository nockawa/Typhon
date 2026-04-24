using System;
using System.Runtime.CompilerServices;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// Scheduler ↔ profiler bridge. The thin wrapper methods here forward scheduler events (tick/system/chunk boundaries) into <see cref="TyphonEvent"/>
/// so the Tracy-style profiler can capture them. Each wrapper pays zero CPU cost when <see cref="TelemetryConfig.ProfilerActive"/> is false — the JIT
/// folds the entire body away.
/// </summary>
/// <remarks>
/// <b>Chunk bracketing:</b> chunks are no longer emitted as paired Start/End instants. A per-thread-local pending-start timestamp lets
/// <see cref="InspectorChunkStart"/> record the start time, which <see cref="InspectorChunkEnd"/> then folds into a single
/// <c>SchedulerChunkEvent</c> span record (with both start and end timestamps + entitiesProcessed) via <see cref="TyphonEvent.EmitSchedulerChunk"/>.
/// Halves the record count for scheduler events, which are the highest-frequency events the profiler sees.
/// </remarks>
public partial class DagScheduler
{
    [ThreadStatic]
    private static long PendingChunkStart;

    [ThreadStatic]
    private static int PendingChunkSystemIdx;

    [ThreadStatic]
    private static int PendingChunkIndex;

    [ThreadStatic]
    private static int PendingChunkTotal;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorTickStart(long tickNumber, long timestamp)
    {
        TyphonEvent.SetCurrentTickNumber((int)tickNumber);
        TyphonEvent.EmitTickStart(timestamp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorTickEnd(long tickNumber, long timestamp) => TyphonEvent.EmitTickEnd(timestamp, overloadLevel: 0, tickMultiplier: 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorSystemReady(int sysIdx, long timestamp) => TyphonEvent.EmitSystemReady((ushort)sysIdx, predecessorCount: 0, timestamp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorSystemSkipped(int sysIdx, SkipReason reason, long timestamp) => TyphonEvent.EmitSystemSkipped((ushort)sysIdx, (byte)reason, timestamp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorChunkStart(int sysIdx, int chunkIndex, long timestamp, int totalChunks)
    {
        PendingChunkStart = timestamp;
        PendingChunkSystemIdx = sysIdx;
        PendingChunkIndex = chunkIndex;
        PendingChunkTotal = totalChunks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorChunkEnd(int sysIdx, int chunkIndex, long timestamp, int entitiesProcessed)
    {
        var startTs = PendingChunkStart;
        if (startTs == 0)
        {
            return;  // no pending start — profiler was off or we missed the start
        }

        TyphonEvent.EmitSchedulerChunk(sysIdx, chunkIndex, PendingChunkTotal, startTs, timestamp, entitiesProcessed);
        PendingChunkStart = 0;
    }
}
