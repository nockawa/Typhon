
#if TELEMETRY

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AccessOperationChunk
{
    internal int AccessCounter;
    internal AccessOperations Operations;
}

[InlineArray(Count)]
internal struct  AccessOperations
{
    internal const int Count = 6;
    private AccessOperation _element;
    
    // 120 bytes structure
}

internal static partial class AccessControlImpl
{
    private static readonly ChainedBlockAllocator<AccessOperationChunk> Allocator;

    static AccessControlImpl()
    {
        // 6 Operations are 120 bytes, +4 for AccessCounter, +4 for the chain header = 128. Fits in two cache lines -> Optimal!
        Allocator = new ChainedBlockAllocator<AccessOperationChunk>(1024);
    }

    private static readonly ThreadLocal<StringBuilder> CachedToDebugStringBuilders = new(() => new StringBuilder(2048));
    private static readonly ThreadLocal<StringBuilder> CachedLogDataStringBuilders = new(() => new StringBuilder(512));
    
    private static string LogData(ulong data)
    {
        var sb = CachedLogDataStringBuilders.Value.Clear();

        sb.Append($"State: {GetStateName(data)}\t");
        sb.Append($"Counter: {data&CounterMask}\t");
        sb.Append($"Shared Waiters: {(data & SharedWaitersMask) >> SharedWaitersShift}\t");
        sb.Append($"Exclusive Waiters: {(data & ExclusiveWaitersMask) >> ExclusiveWaitersShift}\t");
        sb.Append($"Promoter Waiters: {(data & PromoterWaitersMask) >> PromoterWaitersShift}\t");
        sb.Append($"BlockId: {(data & OperationsBlockIdMask) >> OperationsBlockIdShift}\t");
        if ((data & ExclusiveState) != 0)
        {
            sb.Append($" (Thread:{(data&ThreadIdMask) >> ThreadIdShift}) ");
        }

        return sb.ToString();
    }

    private static string ToAlignedOp(OperationType type)
    {
        switch (type)
        {
            case OperationType.EnterSharedAccess:       return "Shared    Start";
            case OperationType.ExitSharedAccess:        return "Shared    Exit ";
            case OperationType.EnterExclusiveAccess:    return "Exclusive Start";
            case OperationType.ExitExclusiveAccess:     return "Exclusive Exit ";
            case OperationType.SharedStartWait:         return "Wait (S)  Start";
            case OperationType.SharedEndWait:           return "Wait (S)  End  ";
            case OperationType.ExclusiveStartWait:      return "Wait (E)  Start";
            case OperationType.ExclusiveEndWait:        return "Wait (E)  End  ";
            case OperationType.TimedOutOrCanceled:      return "Timeout/Cancel ";
            default: return "?";
        }
    }
    
    private static void LogOp(ref AccessOperation op, StringBuilder sb)
    {
        var dt = new DateTime(op.Tick);
        var data = LogData(op.LockData);
        sb.AppendLine($"[{dt:O}]\t| Thread: {op.ThreadId}\t| Op: {ToAlignedOp(op.Type)}\t| Data: [{data}]");
    }

    private static string ToDebugString(int blockId, ref AccessOperation lastOp)
    {
        var sb = CachedToDebugStringBuilders.Value.Clear();
        sb.AppendLine($"Lock #{blockId}:");

        var stop = false;
        foreach (var curBlockId in Allocator.EnumerateChainedBlock(blockId))
        {
            ref var ops = ref Allocator.Get(curBlockId);
            for (int i = 0; i < AccessOperations.Count; i++)
            {
                ref var op = ref ops.Operations[i];
                if (op.IsEmpty)
                {
                    stop = true;
                    break;
                }
                LogOp(ref op, sb);
            }
            if (stop)
            {
                break;
            }
        }

        if (!Unsafe.IsNullRef(ref lastOp))
        {
            LogOp(ref lastOp, sb);
        }
        
        return sb.ToString();
    }
    
    private static string GetStateName(ulong state)
    {
        switch (state&StateMask)
        {
            case IdleState:
                return "Idle  ";
            case SharedState:
                return "Shared";
            case ExclusiveState:
                return "Exclus";
        }

        return "Unknown";
    }
}
#endif