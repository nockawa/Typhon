namespace Typhon.Profiler;

/// <summary>
/// Identifies the type of a <see cref="TraceEvent"/> in the profiling stream.
/// </summary>
public enum TraceEventType : byte
{
    /// <summary>Tick has started. <see cref="TraceEvent.TickNumber"/> is set.</summary>
    TickStart = 0,

    /// <summary>Tick has ended. <see cref="TraceEvent.TickNumber"/> and <see cref="TraceEvent.Payload"/> (overload level) are set.</summary>
    TickEnd = 1,

    /// <summary>A tick phase has started. <see cref="TraceEvent.Phase"/> identifies which phase.</summary>
    PhaseStart = 2,

    /// <summary>A tick phase has ended. <see cref="TraceEvent.Phase"/> identifies which phase.</summary>
    PhaseEnd = 3,

    /// <summary>A system became ready (all predecessors completed). <see cref="TraceEvent.SystemIndex"/> is set.</summary>
    SystemReady = 4,

    /// <summary>A system (or chunk of a parallel system) started executing on a worker.</summary>
    ChunkStart = 5,

    /// <summary>A system (or chunk of a parallel system) finished executing. <see cref="TraceEvent.EntitiesProcessed"/> is set.</summary>
    ChunkEnd = 6,

    /// <summary>A system was skipped. <see cref="TraceEvent.SkipReason"/> is set.</summary>
    SystemSkipped = 7,

    /// <summary>An OTel Activity/span started. <see cref="TraceEvent.Payload"/> contains the interned span name ID.</summary>
    SpanStart = 8,

    /// <summary>An OTel Activity/span ended. <see cref="TraceEvent.Payload"/> contains the interned span name ID.</summary>
    SpanEnd = 9
}
