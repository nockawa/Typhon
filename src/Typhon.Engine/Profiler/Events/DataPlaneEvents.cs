using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionInit"/>.</summary>
[TraceEvent(TraceEventKind.DataTransactionInit, EmitEncoder = true)]
public ref partial struct DataTransactionInitEvent
{
    [BeginParam]
    public long Tsn;
    [BeginParam]
    public ushort UowId;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionPrepare"/>.</summary>
[TraceEvent(TraceEventKind.DataTransactionPrepare, EmitEncoder = true)]
public ref partial struct DataTransactionPrepareEvent
{
    [BeginParam]
    public long Tsn;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionValidate"/>.</summary>
[TraceEvent(TraceEventKind.DataTransactionValidate, EmitEncoder = true)]
public ref partial struct DataTransactionValidateEvent
{
    [BeginParam]
    public long Tsn;
    [BeginParam]
    public int EntryCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionCleanup"/>.</summary>
[TraceEvent(TraceEventKind.DataTransactionCleanup, EmitEncoder = true)]
public ref partial struct DataTransactionCleanupEvent
{
    [BeginParam]
    public long Tsn;
    [BeginParam]
    public int EntityCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataMvccVersionCleanup"/>.</summary>
[TraceEvent(TraceEventKind.DataMvccVersionCleanup, EmitEncoder = true)]
public ref partial struct DataMvccVersionCleanupEvent
{
    [BeginParam]
    public long Pk;
    public ushort EntriesFreed;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataIndexBTreeRangeScan"/>.</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeRangeScan, EmitEncoder = true)]
public ref partial struct DataIndexBTreeRangeScanEvent
{
    public int ResultCount;
    public byte RestartCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataIndexBTreeBulkInsert"/>.</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeBulkInsert, EmitEncoder = true)]
public ref partial struct DataIndexBTreeBulkInsertEvent
{
    [BeginParam]
    public int BufferId;
    [BeginParam]
    public int EntryCount;

}
