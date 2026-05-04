using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>.</summary>
[TraceEvent(TraceEventKind.RuntimeTransactionLifecycle, EmitEncoder = true)]
public ref partial struct RuntimeTransactionLifecycleEvent
{
    [BeginParam]
    public ushort SysIdx;
    public uint TxDurUs;
    public byte Success;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeSubscriptionOutputExecute"/>.</summary>
[TraceEvent(TraceEventKind.RuntimeSubscriptionOutputExecute, EmitEncoder = true)]
public ref partial struct RuntimeSubscriptionOutputExecuteEvent
{
    [BeginParam]
    public long Tick;
    [BeginParam]
    public byte Level;
    public ushort ClientCount;
    public ushort ViewsRefreshed;
    public uint DeltasPushed;
    public ushort OverflowCount;

}
