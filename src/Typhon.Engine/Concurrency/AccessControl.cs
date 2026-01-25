// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// ReSharper disable RedundantNullableFlowAttribute

namespace Typhon.Engine;

[PublicAPI]
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public struct NewAccessControl
{
    private ulong _data;

    public void Reset() => AccessControlImpl.Reset(ref _data);

    public bool EnterSharedAccess(TimeSpan? timeOut = null, CancellationToken token = default, IContentionTarget target = null)
        => AccessControlImpl.EnterSharedAccess(ref _data, timeOut, token, target);

    public void ExitSharedAccess(IContentionTarget target = null)
        => AccessControlImpl.ExitSharedAccess(ref _data, target);

    public bool EnterExclusiveAccess(TimeSpan? timeOut = null, CancellationToken token = default, IContentionTarget target = null)
        => AccessControlImpl.EnterExclusiveAccess(ref _data, timeOut, token, target);

    public void ExitExclusiveAccess(IContentionTarget target = null)
        => AccessControlImpl.ExitExclusiveAccess(ref _data, target);

    public bool TryEnterExclusiveAccess(IContentionTarget target = null) => AccessControlImpl.TryEnterExclusiveAccess(ref _data, target);

    public bool TryPromoteToExclusiveAccess(TimeSpan? timeOut = null, CancellationToken token = default, IContentionTarget target = null) => AccessControlImpl.TryPromoteToExclusiveAccess(ref _data, timeOut, token, target);

    public void DemoteFromExclusiveAccess(IContentionTarget target = null) => AccessControlImpl.DemoteFromExclusiveAccess(ref _data, target);
    public bool IsLockedByCurrentThread => AccessControlImpl.IsLockedByCurrentThread(ref _data);

    internal int LockedByThreadId => AccessControlImpl.LockedByThreadId(ref _data);
    internal int SharedUsedCounter => AccessControlImpl.SharedUsedCounter(ref _data);
    private string DebuggerDisplay => AccessControlImpl.DebuggerDisplay(ref _data);
}

// Default implementation
internal static partial class AccessControlImpl
{
    // Bit layout, from least to most significant:
    //  8 Shared Usage
    //  8 Shared waiters Counter
    //  8 Exclusive waiters
    //  8 Promoter waiters
    // 20 Operation Block Id
    // 10 Exclusive thread Id
    //  2 States
    
    const ulong SharedCounterMask       = 0x0000_0000_0000_00FF;
    const ulong SharedWaitersMask       = 0x0000_0000_0000_FF00;
    const ulong ExclusiveWaitersMask    = 0x0000_0000_00FF_0000;
    const ulong PromoterWaitersMask     = 0x0000_0000_FF00_0000;
    const ulong OperationsBlockIdMask   = 0x000F_FFFF_0000_0000;
    const ulong ThreadIdMask            = 0x3FF0_0000_0000_0000;
    const ulong StateMask               = 0xC000_0000_0000_0000;
    const ulong IdleState               = 0x0000_0000_0000_0000;
    const ulong SharedState             = 0x8000_0000_0000_0000;
    const ulong ExclusiveState          = 0x4000_0000_0000_0000;

    const int SharedWaitersShift        = 8;
    const int ExclusiveWaitersShift     = 16;
    const int PromoterWaitersShift      = 24;
    const int ThreadIdShift             = 52;

    /// <summary>
    /// Computes elapsed time in microseconds from a Stopwatch start tick.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeElapsedUs(long startTicks)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTicks;
        return (elapsed * 1_000_000) / Stopwatch.Frequency;
    }
    
    internal static bool EnterSharedAccess(ref ulong lockData, TimeSpan? timeOut = null, CancellationToken token = default, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;

        var ld = new LockData(ref lockData, timeOut, token);

        while (true)
        {
            switch (ld.State)
            {
                // Switch from Idle to Shared
                case IdleState:
                    // We can start shared only if there are no waiting promoters or exclusives
                    if (ld.CanShareStart)
                    {
                        ld.State = SharedState;
                        ld.SharedCounter = 1;
                        break;
                    }

                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.SharedWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Shared))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                // Already in Shared, increment counter
                case SharedState:
                    ld.SharedCounter++;
                    break;

                // Can't enter shared because we are in exclusive, wait for idle and loop
                case ExclusiveState:
                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.SharedWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Shared))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

// TOFIX
/*
#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS
#endif
*/

            if (!ld.TryUpdate())
            {
                if (ld.IsTimedOutOrCanceled)
                {
                    // TOFIX
                    // ld.AddTimedOutOrCanceledOperation();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.Canceled, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }

                    return false;
                }

                continue;
            }

            // Succeed - record telemetry
            if (hadToWait && level >= TelemetryLevel.Light)
            {
                target?.RecordContention(ComputeElapsedUs(waitStartTicks));
            }
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.SharedAcquired, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
            }

// TOFIX
/*
#if TELEMETRY
            ld.AddOperation(ref op);
#endif
*/
            return true;
        }
    }
    
    internal static void ExitSharedAccess(ref ulong lockData, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref lockData);

        while (true)
        {
            switch (ld.State)
            {
                // Either stay in Shared or switch to idle
                case SharedState:
                    // Stay Shared counter -1
                    --ld.SharedCounter;

                    // If counter becomes 0 switch to Idle
                    if (ld.SharedCounter == 0)
                    {
                        ld.State = IdleState;
                    }

                    break;

                case ExclusiveState:
                    break;


                case IdleState:
                    break;
            }

// TOFIX
/*
#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();
#endif
*/

            if (!ld.TryUpdate())
            {
                continue;
            }

            // Record telemetry
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.SharedReleased, 0);
            }

// TOFIX
/*
#if TELEMETRY
            if (isLastOp)
            {
                // TODO OTLP reporting here

                // Uncomment to enable delayed log activity
                // Console.WriteLine(ToDebugString(blockId, ref op));
                Allocator.FreeChain(blockId);
            }
            else
            {
                ld.AddOperation(ref op);
            }
#endif
            */
            return;
        }
    }

    internal static bool EnterExclusiveAccess(ref ulong lockData, TimeSpan? timeOut = null, CancellationToken token = default, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;

        var ld = new LockData(ref lockData, timeOut, token);

        while (true)
        {
            switch (ld.State)
            {
                // Switch from Idle to Exclusive
                case IdleState:
                    // We can start shared only if there are no waiting promoters or exclusives
                    if (ld.CanExclusiveStart)
                    {
                        // Switch from Idle to Exclusive
                        ld.State = ExclusiveState;
                        ld.ThreadId = Environment.CurrentManagedThreadId;
                        break;
                    }

                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Exclusive))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                // Shared access is active, wait for it to become idle then retry
                case SharedState:
                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Exclusive))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                case ExclusiveState:
                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Exclusive))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

// TOFIX
/*
#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS
#endif
*/

            if (!ld.TryUpdate())
            {
                if (ld.IsTimedOutOrCanceled)
                {
                    // TOFIX
                    // ld.AddTimedOutOrCanceledOperation();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.Canceled, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }

                    return false;
                }

                continue;
            }

            // Succeed - record telemetry
            if (hadToWait && level >= TelemetryLevel.Light)
            {
                target?.RecordContention(ComputeElapsedUs(waitStartTicks));
            }
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.ExclusiveAcquired, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
            }

// TOFIX
/*
#if TELEMETRY
            // Succeed, add the operation to the log and exit
            ld.AddOperation(ref op);
#endif
            */
            return true;
        }
    }

    internal static bool TryEnterExclusiveAccess(ref ulong lockData, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref lockData, null, CancellationToken.None);

        if (ld.State != IdleState)
        {
            return false;
        }

        // Switch from Idle to Exclusive
        ld.State = ExclusiveState;
        ld.ThreadId = Environment.CurrentManagedThreadId;

// TOFIX
/*
#if TELEMETRY
        op.LockData = ld.Staging;
        op.Now();                       // As close as possible to the CAS
#endif
*/

        if (!ld.TryUpdate())
        {
            return false;
        }

        // Record telemetry
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.ExclusiveAcquired, 0);
        }

// TOFIX
/*
#if TELEMETRY
        // Succeed, add the operation to the log and exit
        ld.AddOperation(ref op);
#endif
        */
        return true;
    }

    internal static void ExitExclusiveAccess(ref ulong lockData, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref lockData);

        while (true)
        {
            switch (ld.State)
            {
                // Switch back to idle
                case ExclusiveState:
                    var curThread = Environment.CurrentManagedThreadId;
                    if (ld.ThreadId != curThread)
                    {
                        // TODO OTLP reporting here
                        Debug.Assert(false);
                    }

                    ld.State = IdleState;
                    ld.ThreadId = 0;
                    break;

                case SharedState:
                    break;

                case IdleState:
                    break;
            }

// TOFIX
/*
#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();
#endif
*/

            if (!ld.TryUpdate())
            {
                continue;
            }

            // Record telemetry
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.ExclusiveReleased, 0);
            }

// TOFIX
/*
#if TELEMETRY
            ld.AddOperation(ref op);
#endif
*/
            return;
        }
    }
    
    public static void Reset(ref ulong data) => data = 0;

    public static bool IsLockedByCurrentThread(ref ulong data)
    {
        var threadId = (int)((data & ThreadIdMask) >> ThreadIdShift);
        return threadId == Environment.CurrentManagedThreadId;
    }

    public static bool TryPromoteToExclusiveAccess(ref ulong lockData, TimeSpan? timeOut, CancellationToken token, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;
        var ld = new LockData(ref lockData, timeOut, token);

        while (true)
        {
            if (ld.State != SharedState)
            {
                ThrowInvalidOperation("Can't promote to exclusive because it's not shared.");
            }

            if (!ld.CanPromoteToExclusive)
            {
                // We have to wait our turn
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.PromoteToExclusiveStart, 0);
                    }
                }

                if (!ld.WaitForIdleState(LockData.WaitFor.Promote))
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                    }

                    return false;
                }

                // Fetch the updated state after waiting
                ld.Fetch();
                continue;
            }

// TOFIX
/*
#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS
#endif
*/

            ld.State = ExclusiveState;
            ld.SharedCounter = 1;
            ld.ThreadId = Environment.CurrentManagedThreadId;

            if (!ld.TryUpdate())
            {
                if (ld.IsTimedOutOrCanceled)
                {
                    // TOFIX
                    // ld.AddTimedOutOrCanceledOperation();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.Canceled, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }

                    return false;
                }

                continue;
            }

            // Succeed - record telemetry
            if (hadToWait && level >= TelemetryLevel.Light)
            {
                target?.RecordContention(ComputeElapsedUs(waitStartTicks));
            }
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.PromoteToExclusiveAcquired, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
            }

// TOFIX
/*
#if TELEMETRY
            // Succeed, add the operation to the log and exit
            ld.AddOperation(ref op);
#endif
*/
            return true;
        }
    }
    
    internal static void DemoteFromExclusiveAccess(ref ulong lockData, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref lockData);

        while (true)
        {
            switch (ld.State)
            {
                // Switch back to idle
                case ExclusiveState:
                    var curThread = Environment.CurrentManagedThreadId;
                    if (ld.ThreadId != curThread)
                    {
                        // TODO OTLP reporting here
                        Debug.Assert(false);
                    }

                    ld.ThreadId = 0;
                    ld.State = SharedState;
                    break;

                case SharedState:
                    break;

                case IdleState:
                    break;
            }

// TOFIX
/*
#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();
#endif
*/

            if (!ld.TryUpdate())
            {
                continue;
            }

            // Record telemetry
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.DemoteToShared, 0);
            }

// TOFIX
/*
#if TELEMETRY
            ld.AddOperation(ref op);
#endif
*/
            return;
        }
    }
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidOperation(string message) => throw new InvalidOperationException(message);

    public static int LockedByThreadId(ref ulong lockData) => (int)((lockData & ThreadIdMask) >> ThreadIdShift);

    public static int SharedUsedCounter(ref ulong lockData) => (int)(lockData & SharedCounterMask);

    internal static string DebuggerDisplay(ref ulong lockData)
    {
        var shared = (lockData & SharedState) != 0 ? "Shared Used Counter" : "Shared Waiters";
        return $"State: {GetStateName(lockData)}, ThreadId: {LockedByThreadId(ref lockData)}, {shared}: {SharedUsedCounter(ref lockData)}, " +
               $"Promoter Waiters: {(lockData & PromoterWaitersMask) >> PromoterWaitersShift}, " +
               $"Exclusive Waiters: {(lockData & ExclusiveWaitersMask) >> ExclusiveWaitersShift}";
    }

    private static string GetStateName(ulong state) =>
        (state & StateMask) switch
        {
            IdleState => "Idle",
            SharedState => "Shared",
            ExclusiveState => "Exclusive",
            _ => "Unknown"
        };
}

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 8 bytes of data.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct AccessControl
{
    public void Reset()
    {
        _lockedByThreadId = 0;
        _sharedUsedCounter = 0;
    }

    private volatile int _lockedByThreadId;
    private volatile int _sharedUsedCounter;

    public bool IsLockedByCurrentThread => _lockedByThreadId == Environment.CurrentManagedThreadId;
    public int LockedByThreadId => _lockedByThreadId;
    public int SharedUsedCounter => _sharedUsedCounter;

    public void EnterSharedAccess()
    {
        // Currently exclusively locked, wait it's over
        if (_lockedByThreadId != 0)
        {
            var sw = new SpinWait();
            while (_lockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
        }

        // Increment shared usage
        Interlocked.Increment(ref _sharedUsedCounter);

        // Double check on exclusive, in a loop because we need to restore the shared counter to prevent deadlock
        // So we loop until we meet the criteria
        while (_lockedByThreadId != 0)
        {
            Interlocked.Decrement(ref _sharedUsedCounter);
            var sw = new SpinWait();
            while (_lockedByThreadId != 0)
            {
                sw.SpinOnce();
            }

            Interlocked.Increment(ref _sharedUsedCounter);
        }
    }

    public void ExitSharedAccess() => Interlocked.Decrement(ref _sharedUsedCounter);

    public void EnterExclusiveAccess()
    {
        var ct = Environment.CurrentManagedThreadId;

        // Fast path: exclusive lock works immediately
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) == 0)
        {
            // No shared use: we're good to go
            if (_sharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared use is over
            var sw = new SpinWait();
            while (_sharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }

            return;
        }

        // Slow path: wait the shared concurrent use is over
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
            {
                sw.SpinOnce();
            }

            // Exit if there's no shared access neither
            if (_sharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared access to be over
            while (_sharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    public bool TryEnterExclusiveAccess()
    {
        var ct = Environment.CurrentManagedThreadId;

        // Fast path: exclusive lock works immediately
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) == 0)
        {
            // No shared use: we're good to go
            if (_sharedUsedCounter == 0)
            {
                return true;
            }
            else
            {
                Interlocked.Exchange(ref _lockedByThreadId, 0);
            }
        }

        return false;
    }

    public bool TryPromoteToExclusiveAccess()
    {
        var ct = Environment.CurrentManagedThreadId;

        // We can enter only if we are the only user (_sharedUsedCounter == 1)
        if (_sharedUsedCounter != 1)
        {
            return false;
        }

        // Try to exclusively lock
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
        {
            return false;
        }

        // Double check now we're locked that we're still the only shared user
        if (_sharedUsedCounter != 1)
        {
            // Another concurrent user came at the same time, remove exclusive access and quit with failure
            Interlocked.Exchange(ref _lockedByThreadId, 0);
            return false;
        }

        return true;
    }

    public void DemoteFromExclusiveAccess() => Interlocked.Exchange(ref _lockedByThreadId, 0);

    public void ExitExclusiveAccess() => Interlocked.Exchange(ref _lockedByThreadId, 0);
}
