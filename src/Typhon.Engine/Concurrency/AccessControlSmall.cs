// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
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
    // 12 bits for reference counter, 20 bits for ThreadId
    private const int ThreadIdShift = 12;
    private const int SharedUsedCounterMask = (1 << ThreadIdShift) - 1;

    public void Reset() => _data = 0;

    private int _data;

    public bool IsLockedByCurrentThread => Environment.CurrentManagedThreadId == LockedByThreadId;

    public int LockedByThreadId => _data >> ThreadIdShift;
    public int SharedUsedCounter => _data & SharedUsedCounterMask;
    
    private ref struct AtomicChange
    {
        public AtomicChange(ref int source, TimeSpan? timeOut = null, CancellationToken token = default)
        {
            _source = ref source;
            _spinWait = new SpinWait();
            _timeOut = (timeOut!=null) ? (DateTime.UtcNow + timeOut.Value) : DateTime.MaxValue;
            _token = token;
            Fetch();
        }
        
        public int Initial;
        public int NewValue;

        private readonly ref int _source;
        private SpinWait _spinWait;
        private readonly DateTime _timeOut;
        private readonly CancellationToken _token;

        public bool Commit() => Interlocked.CompareExchange(ref _source, NewValue, Initial) == Initial;

        public void ForceCommit(Func<int, int> valueToCommit)
        {
            while (true)
            {
                Fetch();
                NewValue = valueToCommit(Initial);
                if (Commit())
                {
                    return;
                }
            }
        }

        public void Fetch() => Initial = _source;

        public bool Wait()
        {
            if (_token.IsCancellationRequested || DateTime.UtcNow >= _timeOut)
            {
                return false;
            }
            
            _spinWait.SpinOnce();
            
            Fetch();
            return true;
        }
        
        public bool WaitFor(Func<int, bool> predicate)
        {
            Fetch();
            while (true)
            {
                if (predicate(Initial))
                {
                    return true;
                }
                
                if (!Wait())
                {
                    return false;
                }
            }
        }
    }
    
    public bool EnterSharedAccess(TimeSpan? timeOut = null, CancellationToken token = default)
    {
        var ac = new AtomicChange(ref _data, timeOut, token);

        while (true)
        {
            if (!ac.WaitFor(d => (d >> ThreadIdShift) == 0))
            {
                return false;
            }

            if ((ac.Initial & SharedUsedCounterMask) >= SharedUsedCounterMask)
            {
                ThrowInvalidOperationException("Too many concurrent shared accesses");
            }
            
            ac.NewValue = ac.Initial + 1;
            if (ac.Commit())
            {
                return true;
            }
        }
    }

    public void ExitSharedAccess()
    {
        var ac = new AtomicChange(ref _data);
        ac.ForceCommit(d =>
        {
            var counter = d & SharedUsedCounterMask;
            if (counter == 0)
            {
                ThrowInvalidOperationException("Exiting shared access without entering it first");
            }
            return d - 1;
        });
    }

    public bool EnterExclusiveAccess(TimeSpan? timeOut = null, CancellationToken token = default)
    {
        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data, timeOut, token);

        if ((ac.Initial & ~SharedUsedCounterMask) == ct)
        {
            ThrowInvalidOperationException("Cannot enter exclusive access while already holding it");       
        }
        
        // Fast path
        ac.Initial = 0;                     // We override initial value because we can only switch to exclusive access immediately from idle (data==0)
        ac.NewValue = ct;
        if (ac.Commit())
        {
            return true;
        }
        
        // Slow path
        while (true)
        {
            if (!ac.WaitFor(d => d == 0))
            {
                return false;
            }

            ac.NewValue = ct;
            if (ac.Commit())
            {
                return true;
            }
        }        
    }

    public void ExitExclusiveAccess()
    {
        var ac = new AtomicChange(ref _data);
        var expectedThread = Environment.CurrentManagedThreadId << ThreadIdShift;
        ac.ForceCommit(d =>
        {
            if ((d & ~SharedUsedCounterMask) != expectedThread)
            {
                ThrowInvalidOperationException("ExitExclusiveAccess called by a thread that doesn't own the lock");
            }
            return 0;
        });
    }

    public bool TryPromoteToExclusiveAccess(TimeSpan? timeOut = null, CancellationToken token = default)
    {
        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data, timeOut, token);

        while (true)
        {
            var counter = ac.Initial & SharedUsedCounterMask;

            // Must be in shared mode to promote (counter > 0)
            if (counter == 0)
            {
                ThrowInvalidOperationException("Cannot promote to exclusive without holding shared access");
            }

            // We can only promote if we are the only user (counter == 1)
            if (counter != 1)
            {
                return false;
            }

            ac.NewValue = ct;
            if (ac.Commit())
            {
                return true;
            }

            if (!ac.Wait())
            {
                return false;
            }
        }
    }
    
    public bool Enter(bool exclusive, TimeSpan? timeOut = null, CancellationToken token = default) => exclusive ? EnterExclusiveAccess(timeOut, token) : EnterSharedAccess(timeOut, token);

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
    
    [DoesNotReturn]
    private static void ThrowInvalidOperationException(string msg) => throw new InvalidOperationException(msg);
}