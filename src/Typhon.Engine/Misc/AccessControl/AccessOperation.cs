#if TELEMETRY
using System;
using System.Runtime.InteropServices;
#endif

namespace Typhon.Engine;

internal enum OperationType : byte
{
    None = 0,
    EnterSharedAccess,
    ExitSharedAccess,
    EnterExclusiveAccess,
    ExitExclusiveAccess,
    SharedStartWait,
    SharedEndWait,
    ExclusiveStartWait,
    ExclusiveEndWait,
    TimedOutOrCanceled
}

#if TELEMETRY

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AccessOperation
{
    private byte _marker;               // 1
    internal OperationType Type;        // 2
    internal ushort ThreadId;           // 4
    internal ulong LockData;            // 12
    internal long Tick;                 // 20

    public bool IsEmpty => _marker == 0;

    public static AccessOperation Wait(OperationType type)
    {
        var res = new AccessOperation(type);
        res.Now();
        return res;
    }

    public static AccessOperation TimedOutOrCanceled()
    {
        var res = new AccessOperation(OperationType.TimedOutOrCanceled);
        res.Now();
        return res;
    }

    public AccessOperation(OperationType type)
    {
        _marker = 0xFF;     // Anything but 0 is fine, we just make sure the first byte is non-zero to indicate the entry is occupied
        Type = type;
        ThreadId = (ushort)Environment.CurrentManagedThreadId;
    }

    public void Now() => Tick = DateTime.UtcNow.Ticks;
}

#endif