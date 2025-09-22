// unset

using JetBrains.Annotations;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 4 bytes of data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct AccessControlSmall
{
    // 12bits for reference counter, 20bits for ThreadId
    private const int ThreadIdShift = 12;
    private const int SharedUsedCounterMask = (1 << ThreadIdShift) - 1;

    public void Reset() => _data = 0;

    private volatile int _data;

    public bool IsLockedByCurrentThread => System.Environment.CurrentManagedThreadId == LockedByThreadId;

    public int LockedByThreadId => _data >> ThreadIdShift;
    public int SharedUsedCounter => _data & SharedUsedCounterMask;

    public void EnterSharedAccess()
    {
        // Currently exclusively locked, wait it's over
        if (LockedByThreadId != 0)
        {
            var sw = new SpinWait();
            while (LockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
        }

        // Increment shared usage
        Interlocked.Increment(ref _data);

        // Double check on exclusive, in a loop because we need to restore the shared counter to prevent deadlock
        // So we loop until we meet the criteria
        while (LockedByThreadId != 0)
        {
            Interlocked.Decrement(ref _data);
            var sw = new SpinWait();
            while (LockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
            Interlocked.Increment(ref _data);
        }
    }

    public void ExitSharedAccess() => Interlocked.Decrement(ref _data);

    public void Enter(bool exclusive)
    {
        if (exclusive)
        {
            EnterExclusiveAccess();
        }
        else
        {
            EnterSharedAccess();
        }
    }
    
    public void Exit(bool exclusive)
    {
        if (exclusive)
        {
            ExitExclusiveAccess();
        }
        else
        {
            ExitSharedAccess();
        }
    }
    
    public void EnterExclusiveAccess()
    {
        var ct = System.Environment.CurrentManagedThreadId << ThreadIdShift;

        // Fast path: exclusive lock works immediately
        var suc = SharedUsedCounter;
        if (Interlocked.CompareExchange(ref _data, ct | suc, suc) == suc)
        {
            // No shared use: we're good to go
            if (SharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared use is over
            var sw = new SpinWait();
            while (SharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }

            return;
        }

        // Slow path: wait the shared concurrent use is over
        {
            var sw = new SpinWait();
            suc = SharedUsedCounter;
            while (Interlocked.CompareExchange(ref _data, ct | suc, suc) != suc)
            {
                sw.SpinOnce();
                suc = SharedUsedCounter;
            }

            // Exit if there's no shared access neither
            if (SharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared access to be over
            while (SharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    public bool TryPromoteToExclusiveAccess()
    {
        var ct = System.Environment.CurrentManagedThreadId << ThreadIdShift;

        // We can enter only if we are the only user (counter == 1)
        if (SharedUsedCounter != 1)
        {
            return false;
        }

        // Try to exclusively lock
        var suc = SharedUsedCounter;
        if (Interlocked.CompareExchange(ref _data, ct | suc, suc) != suc)
        {
            return false;
        }

        // Double check now we're locked that we're still the only shared user
        if (SharedUsedCounter != 1)
        {
            // Another concurrent user came at the same time, remove exclusive access and quit with failure
            Interlocked.And(ref _data, SharedUsedCounterMask);
            return false;
        }

        return true;
    }

    public void DemoteFromExclusiveAccess() => Interlocked.And(ref _data, SharedUsedCounterMask);

    public void ExitExclusiveAccess() => Interlocked.And(ref _data, SharedUsedCounterMask);
}