// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

#if !TELEMETRY

// Default implementation
internal static class AccessControlImpl
{
    const ulong IdleState        = 0;
    const ulong SharedState      = 0x80000000;
    const ulong ExclusiveState   = 0x40000000;
    const ulong StateMask        = 0xC0000000;
    const ulong FreeMask         = 0x3FFFF000;
    const ulong CounterMask      = 0x00000FFF;

    // |63|62|61|60|59|58|57|56|55|54|53|52|51|50|49|48|47|46|45|44|43|42|41|40|39|38|37|36|35|34|33|32|
    // |State|                      FREE (18b)                       |          COUNTER (12b)          |

    // |31|30|29|28|27|26|25|24|23|22|21|20|19|18|17|16|15|14|13|12|11|10|09|08|07|06|05|04|03|02|01|00|
    // |State|                      FREE (18b)                       |          COUNTER (12b)          |
    //private volatile ulong _lockData;
    internal static void EnterSharedAccess(ref ulong lockData)
    {
        while (true)
        {
            var data = lockData;
            var state = data & StateMask;
            ulong newData = 0;
            switch (state)
            {
                case IdleState:
                    // Switch from Idle to: Shared | 0 | 1
                    newData = SharedState | 1;
                    break;
                case SharedState:
                    // Already in Shared, increment counter: Shared | 0 | +1
                    newData = data + 1;
                    break;
                case ExclusiveState:
                    // Switch from Exclusive to Shared: wait for idle, then retry
                    WaitForState(ref lockData, IdleState);
                    continue;
            }

            if (Interlocked.CompareExchange(ref lockData, newData, data) == data)
            {
                return;
            }
        }
    }

    internal static void ExitSharedAccess(ref ulong lockData)
    {
        while (true)
        {
            var data = lockData;
            var curState = data & StateMask;
            var curCounter = data & CounterMask;
            ulong newData = 0;
            Debug.Assert(curState == SharedState, $"Can't call ExitSharedAccess on a state other than Shared. Current state is {GetStateName(data)}");
            Debug.Assert(curCounter > 0, $"Counter has to be greater than 0. Current counter value is {curCounter}");
            
            // Decrement ref counter
            newData = data - 1;

            // If counter becomes 0, we switch the state back to idle
            if ((newData & CounterMask) == 0)
            {
                // Idle | 0 | 0
                newData = IdleState;
            }

            if (Interlocked.CompareExchange(ref lockData, newData, data) == data)
            {
                return;
            }
        }
    }

    internal static void EnterExclusiveAccess(ref ulong lockData)
    {
        while (true)
        {
            var data = lockData;
            var state = data & StateMask;
            ulong newData = 0;
            switch (state)
            {
                // Switch from Idle to: Exclusive | 0 | 1
                case IdleState:
                    newData = ExclusiveState | 1;
                    break;

                // Shared or Exclusive, wait for idle then retry
                case SharedState:
                case ExclusiveState:
                    WaitForState(ref lockData, IdleState);
                    continue;
            }

            if (Interlocked.CompareExchange(ref lockData, newData, data) == data)
            {
                return;
            }
        }
    }

    internal static void ExitExclusiveAccess(ref ulong lockData)
    {
        while (true)
        {
            var data = lockData;
            var curState = data & StateMask;
            var curCounter = data & CounterMask;
            ulong newData = 0;
            Debug.Assert(curState == ExclusiveState, $"Can't call ExitExclusiveAccess on a state other than Exclusive. Current state is {GetStateName(data)}");
            Debug.Assert(curCounter > 0, $"Counter has to be greater than 0. Current counter value is {curCounter}");
            
            // Decrement ref counter
            newData = data - 1;

            // If counter becomes 0, we switch the state back to idle
            if ((newData & CounterMask) == 0)
            {
                // Idle | 0 | 0
                newData = IdleState;
            }

            if (Interlocked.CompareExchange(ref lockData, newData, data) == data)
            {
                return;
            }
        }
    }

    internal static void Reset(ref ulong data) => data = 0;
    
    private static string GetStateName(ulong state)
    {
        // Mask to get only the state bits
        switch (state & StateMask)
        {
            case IdleState:
                return "Idle";
            case SharedState:
                return "Shared";
            case ExclusiveState:
                return "Exclusive";
        }

        return "Unknown";
    }

    private static void WaitForState(ref ulong lockData, ulong state)
    {
        SpinWait spin = new SpinWait();
        while (true)
        {
            var curState = lockData & StateMask;
            if (curState == state)
            {
                return;
            }

            spin.SpinOnce();
        }
    }
}
#endif

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

    public bool IsLockedByCurrentThread => _lockedByThreadId == System.Environment.CurrentManagedThreadId;
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
        var ct = System.Environment.CurrentManagedThreadId;

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
        var ct = System.Environment.CurrentManagedThreadId;

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
        var ct = System.Environment.CurrentManagedThreadId;

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
