// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public struct NewAccessControl
{
    private ulong _data;
    
    public void Reset() => AccessControlImpl.Reset(ref _data);
    
    public bool EnterSharedAccess(TimeSpan? timeOut = null, CancellationToken token = default) 
        => AccessControlImpl.EnterSharedAccess(ref _data, timeOut, token);
    
    public void ExitSharedAccess() 
        => AccessControlImpl.ExitSharedAccess(ref _data);
    
    public bool EnterExclusiveAccess(TimeSpan? timeOut = null, CancellationToken token = default) 
        => AccessControlImpl.EnterExclusiveAccess(ref _data, timeOut, token);
    
    public void ExitExclusiveAccess() 
        => AccessControlImpl.ExitExclusiveAccess(ref _data);
}

// Default implementation
internal static partial class AccessControlImpl
{
    // Bit layout, from least to most significant:
    //  8 Usage Counter
    // 14 Operation Block Id
    // 10 Exclusive waiters
    // 10 Shared waiters
    // 10 Promoter waiters
    // 10 Exclusive thread Id
    //  2 States
    
    const ulong CounterMask             = 0x0000_0000_0000_00FF;
    const ulong OperationsBlockIdMask   = 0x0000_0000_003F_FF00;
    const ulong SharedWaitersMask       = 0x0000_0000_FFC0_0000;
    const ulong ExclusiveWaitersMask    = 0x0000_03FF_0000_0000;
    const ulong PromoterWaitersMask     = 0x000F_FC00_0000_0000;
    const ulong ThreadIdMask            = 0x3FF0_0000_0000_0000;
    const ulong StateMask               = 0xC000_0000_0000_0000;
    const ulong IdleState               = 0x0000_0000_0000_0000;
    const ulong SharedState             = 0x8000_0000_0000_0000;
    const ulong ExclusiveState          = 0x4000_0000_0000_0000;

#if TELEMETRY
    const int OperationsBlockIdShift    = 8;
#endif
    const int SharedWaitersShift        = 22;
    const int ExclusiveWaitersShift     = 32;
    const int PromoterWaitersShift      = 42;
    const int ThreadIdShift             = 52;
    
    internal static bool EnterSharedAccess(ref ulong lockData, TimeSpan? timeOut = null, CancellationToken token = default)
    {
#if TELEMETRY
        var op = new AccessOperation(OperationType.EnterSharedAccess);
        var ld = new LockData(Allocator, ref lockData, timeOut, token);
#else
        var ld = new LockData(ref lockData, timeOut, token);
#endif
        
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
                        ld.Counter = 1;
                        break;
                    }
                    
                    // We have to wait our turn
                    ld.AddWaitOperation(OperationType.SharedStartWait);
                    if (!ld.WaitForSharedCanStart())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }
                    ld.AddWaitOperation(OperationType.SharedEndWait);
                    
                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
                
                // Already in Shared, increment counter
                case SharedState:
                    ld.Counter++;
                    break;

                // Can't enter shared because we are in exclusive, wait for idle and loop
                case ExclusiveState:
                    ld.AddWaitOperation(OperationType.SharedStartWait);

                    if (!ld.WaitForTransitionFromExclusiveToShared())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }

                    ld.AddWaitOperation(OperationType.SharedEndWait);

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS 
#endif

            if (!ld.TryUpdate())
            {
                if (ld.IsTimedOutOrCanceled)
                {
                    ld.AddTimedOutOrCanceledOperation();
                    return false;
                }

                continue;
            }

            // Succeed, add the operation to the log and exit
#if TELEMETRY
            ld.AddOperation(ref op);
#endif
            return true;
        }
    }
    
    internal static void ExitSharedAccess(ref ulong lockData)
    {
#if TELEMETRY
        var op = new AccessOperation(OperationType.ExitSharedAccess);
        var ld = new LockData(Allocator, ref lockData);
#else
        var ld = new LockData(ref lockData);
#endif

        while (true)
        {
#if TELEMETRY
            var blockId = ld.OperationsBlockId;
            var isLastOp = false;
#endif

            switch (ld.State)
            {
                // Either stay in Shared or switch to idle
                case SharedState:
                    // Stay Shared counter -1
                    --ld.Counter;

                    // If counter becomes 0 switch to Idle
                    if (ld.Counter == 0)
                    {
                        ld.State = IdleState;
#if TELEMETRY
                        isLastOp = ld.IsIdleNoWaiters;
                        if (isLastOp)
                        {
                            ld.OperationsBlockId = 0;
                        }
#endif
                    }

                    break;

                case ExclusiveState:
                    break;
                
                
                case IdleState:
                    break;
            }

#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();
#endif

            if (!ld.TryUpdate())
            {
                continue;
            }

#if TELEMETRY
            if (isLastOp)
            {
                // TODO OTLP reporting here
                
                // Uncomment to enable delayed log activity
                Console.WriteLine(ToDebugString(blockId, ref op));

                ld.FreeBlock();
            }
            else
            {
                ld.AddOperation(ref op);
            }
#endif
            return;
        }
    }

    internal static bool EnterExclusiveAccess(ref ulong lockData, TimeSpan? timeOut = null, CancellationToken token = default)
    {
#if TELEMETRY
        var op = new AccessOperation(OperationType.EnterExclusiveAccess);
        var ld = new LockData(Allocator, ref lockData, timeOut, token);
#else
        var ld = new LockData(ref lockData, timeOut, token);
#endif

        while (true)
        {
            switch (ld.State)
            {
                // Switch from Idle to Exclusive
                case IdleState:
                    // Switch from Idle to Exclusive
                    ld.State = ExclusiveState;
                    ld.Counter = 1;
                    ld.ThreadId = Environment.CurrentManagedThreadId;
                    break;

                // Shared access is active, wait for it to become idle then retry
                case SharedState:
                    ld.AddWaitOperation(OperationType.ExclusiveStartWait);

                    if (!ld.WaitForTransitionFromExclusiveToExclusive())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }

                    ld.AddWaitOperation(OperationType.ExclusiveEndWait);

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                // Either reentrancy because it's the same thread already in exclusive, or another thread has the lock and we wait
                case ExclusiveState:
                    /*var curThread = Environment.CurrentManagedThreadId;
                    
                    // Same thread, increment counter and quit
                    if (ld.ThreadId == curThread)
                    {
                        ld.Counter++;
                        break;
                    }*/
                    
                    // Different thread, wait for idle and loop
                    ld.AddWaitOperation(OperationType.ExclusiveStartWait);

                    if (!ld.WaitForTransitionFromExclusiveToExclusive())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }

                    ld.AddWaitOperation(OperationType.ExclusiveEndWait);

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS 
#endif

            if (!ld.TryUpdate())
            {
                if (ld.IsTimedOutOrCanceled)
                {
                    ld.AddTimedOutOrCanceledOperation();
                    return false;
                }

                continue;
            }

#if TELEMETRY
            // Succeed, add the operation to the log and exit
            ld.AddOperation(ref op);
#endif
            return true;
        }
    }

    internal static void ExitExclusiveAccess(ref ulong lockData)
    {
#if TELEMETRY
        var op = new AccessOperation(OperationType.ExitExclusiveAccess);
        var ld = new LockData(Allocator, ref lockData);
#else
        var ld = new LockData(ref lockData);
#endif

        while (true)
        {
#if TELEMETRY
            var blockId = ld.OperationsBlockId;
            var isLastOp = false;
#endif

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
                    
                    --ld.Counter;
                    if (ld.Counter == 0)
                    {
                        ld.State = IdleState;
                        ld.ThreadId = 0;
#if TELEMETRY
                        isLastOp = ld.IsIdleNoWaiters;
                        if (isLastOp)
                        {
                            ld.OperationsBlockId = 0;
                        }
#endif
                    }

                    break;

                case SharedState:
                    break;
                
                case IdleState:
                    break;
            }

#if TELEMETRY
            op.LockData = ld.Staging;
            op.Now();
#endif

            if (!ld.TryUpdate())
            {
                continue;
            }

#if TELEMETRY
            if (isLastOp)
            {
                // TODO OTLP reporting here

                // Uncomment to enable delayed log activity
                Console.WriteLine(ToDebugString(blockId, ref op));

                ld.FreeBlock();
            }
            else
            {
                ld.AddOperation(ref op);
            }
#endif
            return;
        }    
    }
    
    public static void Reset(ref ulong data) => data = 0;
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
