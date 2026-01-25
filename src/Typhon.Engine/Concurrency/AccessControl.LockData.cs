using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

internal static partial class AccessControlImpl
{
    [StructLayout(LayoutKind.Sequential)]
    internal ref struct LockData
    {
        public LockData(ref ulong data, TimeSpan? timeOut = null, CancellationToken token = default)
        {
            _data = ref data;
            _initial = _staging = _data;
            _token = token;
            _timeOutAt = (timeOut != null) ? (DateTime.UtcNow + timeOut.Value) : DateTime.MaxValue;
        }
        
        private ref ulong _data;
        private ulong _initial;
        private ulong _staging;
        private readonly DateTime _timeOutAt;
        private readonly CancellationToken _token;

        public bool IsTimedOutOrCanceled => (DateTime.UtcNow > _timeOutAt) || _token.IsCancellationRequested;
        
        public int SharedCounter
        {
            get => (int)(_staging & SharedCounterMask);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~SharedCounterMask) | (uint)value;
            }
        }
        
        public int SharedWaiters
        {
            get => (int)((_staging & SharedWaitersMask) >> SharedWaitersShift);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~SharedWaitersMask) | (ulong)value << SharedWaitersShift;
            }
        }


        public int ExclusiveWaiters
        {
            get => (int)((_staging & ExclusiveWaitersMask) >> ExclusiveWaitersShift);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~ExclusiveWaitersMask) | (ulong)value << ExclusiveWaitersShift;
            }
        }

        public int PromoterWaiters
        {
            get => (int)((_staging & PromoterWaitersMask) >> PromoterWaitersShift);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~PromoterWaitersMask) | (ulong)value << PromoterWaitersShift;
            }
        }

        public int ThreadId
        {
            get => (int)((_staging & ThreadIdMask) >> ThreadIdShift);
            set => _staging = (_staging & ~ThreadIdMask) | ((ulong)value << ThreadIdShift) & ThreadIdMask;
        }

        public ulong State
        {
            get => _staging & StateMask;
            set => _staging = (_staging & ~StateMask) | value;
        }

        public ulong Staging
        {
            get => _staging;
            set => _staging = value;
        }

        public bool TryUpdate()
        {
            var succeed = Interlocked.CompareExchange(ref _data, _staging, _initial) == _initial;

            if (!succeed)
            {
                Fetch();
            }

            return succeed;
        }

        internal void Fetch()
        {
            _initial = _staging = _data;
        }
        
        public bool IsIdleNoWaiters => (_staging & ~OperationsBlockIdMask) == 0;
        public bool CanShareStart => (_staging & (PromoterWaitersMask | ExclusiveWaitersMask)) == 0;
        public bool CanExclusiveStart => (_staging & PromoterWaitersMask) == 0;
        public bool CanPromoteToExclusive => (_initial & SharedCounterMask) == 1;

        public bool WaitForSharedCanStart()
        {
            var res = false;
            
            // Increment the Shared Waiter counter
            //Interlocked.Add(ref _data, 1UL << SharedWaitersShift);
            
            var sw = new SpinWait();
            var maxWaitCounter = 100;
            while ((DateTime.UtcNow < _timeOutAt) && !_token.IsCancellationRequested && (--maxWaitCounter > 0))
            {
                var cur = _data;
                
                var state = (cur&StateMask);
                
                // A concurrent change of state may occur and if that's the case, we need to exist the wait and reassess
                if (state == ExclusiveState)
                {
                    res = true;
                    break;
                }
                
                // Can't be exclusive, without exclusive or promoters waiters (they have the priority)
                if (((cur & (ExclusiveWaitersMask | PromoterWaitersMask)) == 0))
                {
                    res = true;
                    break;
                }
                sw.SpinOnce();
//                Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] {LogData(cur)}");
            }

            // Decrement the Shared Waiter counter
            //Interlocked.Add(ref _data, unchecked((ulong)(-(1L << SharedWaitersShift))));
            
            return (maxWaitCounter==0) || res;
        }

        internal enum WaitFor
        {
            Shared,
            Exclusive,
            Promote
        }
        
        public bool WaitForIdleState(WaitFor waitFor)
        {
            // Log the operation start and set the value to increment as a waiter
            int waitIncValue;
            bool overflow;
            switch (waitFor)
            {
                case WaitFor.Shared:    
                    // TOFIX
                    // AddWaitOperation(OperationType.SharedStartWait);
                    overflow = SharedWaiters == byte.MaxValue;
                    waitIncValue = 1 << SharedWaitersShift;    
                    break;
                case WaitFor.Exclusive: 
                    // TOFIX
                    // AddWaitOperation(OperationType.ExclusiveStartWait); 
                    overflow = ExclusiveWaiters == byte.MaxValue;
                    waitIncValue = 1 << ExclusiveWaitersShift; 
                    break;
                case WaitFor.Promote:   
                    // TOFIX
                    // AddWaitOperation(OperationType.PromoteStartWait);
                    overflow = PromoterWaiters == byte.MaxValue;
                    waitIncValue = 1 << PromoterWaitersShift;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(waitFor), waitFor, null);
            }

            if (!overflow)
            {
                // Increment the appropriate waiter
                Staging += (ulong)waitIncValue;

                // Make the update, two possibilities
                if (!TryUpdate())
                {
                    // Concurrent update came in between, return true to signal "hey, let's try again"
                    return true;
                }
                // Keep going on as expected
            }
            
            // Enter the wait loop where we fetch the lock data and check for idle state
            var sw = new SpinWait();
            var maxWaitCounter = 1000;
            var res = false;
            while ((DateTime.UtcNow < _timeOutAt) && !_token.IsCancellationRequested && (--maxWaitCounter > 0))
            {
                var data = _data;
                var threadId = (data & ThreadIdMask) >> ThreadIdShift;
                
                // Idle ?
                if ((data&StateMask) == IdleState)
                {
                    // Set result to true to signal to reassess
                    res = true;
                    break;
                }
                
                sw.SpinOnce();
            }

            // Update res to be either true (try again/reassess) or false (timeout or cancellation)
            res = (maxWaitCounter==0) || res;

            if (!overflow)
            {
                // Decrement the waiter counter, can only be made through a 64-bits CAS, we only care about decrement the value we initially added,
                // Keep trying if we fail.
                while (true)
                {
                    var oldData = _data;
                    var newData = oldData - (ulong)waitIncValue;
                    if (Interlocked.CompareExchange(ref _data, newData, oldData) == oldData)
                    {
                        break;
                    }
                }
            }
            
            // Log if we timed out or were canceled
            if (!res)
            {
                // TOFIX
                // AddTimedOutOrCanceledOperation();
                return false;
            }

            return true;
        }
    }
}