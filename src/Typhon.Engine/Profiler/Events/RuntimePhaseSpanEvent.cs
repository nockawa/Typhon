using System;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.RuntimePhaseSpan"/>. Wraps one <see cref="TickPhase"/> region inside
/// <c>TyphonRuntime.OnTickEndInternal</c> as a real span, so child spans (PageCacheFlush, BTreeInsert, …) attach via <c>parentSpanId</c>.
/// </summary>
[TraceEvent(TraceEventKind.RuntimePhaseSpan, FactoryName = "BeginRuntimePhase", EmitEncoder = true)] public ref partial struct RuntimePhaseSpanEvent
{
    [BeginParam(ParamType = "TickPhase")]
    public byte Phase;

}
