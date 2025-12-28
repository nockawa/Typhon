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
        public LockData(
#if TELEMETRY
            ChainedBlockAllocator<AccessOperationChunk> allocator, 
#endif
            ref ulong data, TimeSpan? timeOut = null, CancellationToken token = default)
        {
            _data = ref data;
            _initial = data;
            _staging = data;
            _token = token;
            _timeOutAt = (timeOut != null) ? (DateTime.UtcNow + timeOut.Value) : DateTime.MaxValue;
#if TELEMETRY
            _allocator = allocator;
            EnsureOperationsBlockIdAllocated();
#endif
        }
        
        private ref ulong _data;
        private ulong _initial;
        private ulong _staging;
        private readonly DateTime _timeOutAt;
        private readonly CancellationToken _token;

#if TELEMETRY
        private readonly ChainedBlockAllocator<AccessOperationChunk> _allocator;
        private bool _hasAllocatedBlock;
#endif
        
        public bool IsTimedOutOrCanceled => (DateTime.UtcNow > _timeOutAt) || _token.IsCancellationRequested;
        
        public int Counter
        {
            get => (int)(_staging & CounterMask);
            set => _staging = (_staging & ~CounterMask) | (uint)value;
        }

#if TELEMETRY       
        public int OperationsBlockId
        {
            get => (int)((_staging & OperationsBlockIdMask) >> OperationsBlockIdShift);
            set => _staging = (_staging & ~OperationsBlockIdMask) | (ulong)value << OperationsBlockIdShift;
        }
#endif

        public int SharedWaiters
        {
            get => (int)((_staging & SharedWaitersMask) >> SharedWaitersShift);
            set => _staging = (_staging & ~SharedWaitersMask) | (ulong)value << SharedWaitersShift;
        }
        
        public int ExclusiveWaiters
        {
            get => (int)((_staging & ExclusiveWaitersMask) >> ExclusiveWaitersShift);
            set => _staging = (_staging & ~ExclusiveWaitersMask) | (ulong)value << ExclusiveWaitersShift;
        }
        
        public int PromoterWaiters
        {
            get => (int)((_staging & PromoterWaitersMask) >> PromoterWaitersShift);
            set => _staging = (_staging & ~PromoterWaitersMask) | (ulong)value << PromoterWaitersShift;
        }
        
        public int ThreadId
        {
            get => (int)((_staging & ThreadIdMask) >> ThreadIdShift);
            set => _staging = (_staging & ~ThreadIdMask) | (ulong)value << ThreadIdShift;
        }

        public ulong State
        {
            get => _staging & StateMask;
            set => _staging = (_staging & ~StateMask) | value;
        }

        public ulong Staging => _staging;

        public bool TryUpdate()
        {
            var succeed = Interlocked.CompareExchange(ref _data, _staging, _initial) == _initial;

            if (!succeed)
            {
#if TELEMETRY
                if (_hasAllocatedBlock)
                {
                    // Extract block ID directly from _staging to avoid allocating a new block via the getter
                    var blockId = OperationsBlockId;
                    if (blockId != 0)
                    {
                        _allocator.Free(blockId);
                    }
                    _hasAllocatedBlock = false;
                }
#endif
                Fetch();
            }

            return succeed;
        }

        internal void Fetch()
        {
            _initial = _staging = _data;
            EnsureOperationsBlockIdAllocated();
        }

#if TELEMETRY

        private ref AccessOperationChunk ConcurrentGetOperationsBlockId()
        {
            var blockId = OperationsBlockId;
            ref var opChunk = ref Allocator.Get(blockId);

            // Increment the access counter to prevent concurrent free of it
            Interlocked.Increment(ref opChunk.AccessCounter);
            
            return ref opChunk;
        }
        
        public void FreeAccessOperations(int blockId)
        {
            ref var opChunk = ref Allocator.Get(blockId);
            SpinWait? sw = null;
            while (opChunk.AccessCounter != 0)
            {
                sw ??= new SpinWait();
                sw.Value.SpinOnce();
            }
            Allocator.Free(blockId);
        }
        
        
        public void AddOperation(ref AccessOperation op)
        {
            // Get the first bank of operations
            ref var rootChunk = ref ConcurrentGetOperationsBlockId();
            ref var curChunk = ref rootChunk;
            
            while (true)
            {
                // Parse them to find the first empty
                for (int i = 0; i < AccessOperations.Count; i++)
                {
                    // Try to reserve the entry, will fail if it's already taken -> we loop
                    ref var intPtr = ref Unsafe.As<AccessOperation, int>(ref curChunk.Operations[i]);
                    if (Interlocked.CompareExchange(ref intPtr, 1, 0) != 0)
                    {
                        continue;
                    }

                    // Successfully reserved, copy the data and exit
                    curChunk.Operations[i] = op;
                    
                    // Uncomment to enable real-time log activity
                    /*
                    var sb = CachedToDebugStringBuilders.Value.Clear();
                    LogOp(ref op, sb);
                    Console.Write(sb.ToString());
                    */

                    // Decrement the access counter to exit the safe section
                    Interlocked.Decrement(ref rootChunk.AccessCounter);
                    return;
                }

                // All the entries are taken, go to the next bank
                ref var nextChunk = ref Allocator.Next(ref curChunk);
        
                // End of the chain? Allocate a new bank and loop
                if (Unsafe.IsNullRef(ref nextChunk))
                {
                    nextChunk = ref Allocator.SafeAppend(ref curChunk);
                }

                curChunk = ref nextChunk;
            }
        }

        public void AddWaitOperation(OperationType type)
        {
            var op = AccessOperation.Wait(type);
            op.LockData = Staging;
            AddOperation(ref op);
        }

        public void AddTimedOutOrCanceledOperation()
        {
            var op = AccessOperation.TimedOutOrCanceled();
            op.LockData = Staging;
            AddOperation(ref op);
        }
#endif
        
        public bool IsIdleNoWaiters => (_staging & ~OperationsBlockIdMask) == 0;
        public bool CanShareStart => (_staging & (PromoterWaitersMask | ExclusiveWaitersMask)) == 0;

        public bool WaitForSharedCanStart()
        {
            var res = false;
            
            // Increment the Shared Waiter counter
            Interlocked.Add(ref _data, 1UL << SharedWaitersShift);
            
            var sw = new SpinWait();
            while ((DateTime.UtcNow < _timeOutAt) && !_token.IsCancellationRequested)
            {
                var cur = _data;
                
                // Can't be exclusive, without exclusive or promoters waiters (they have the priority)
                if (((cur&StateMask) != ExclusiveState) && ((cur & (ExclusiveWaitersMask | PromoterWaitersMask)) == 0))
                {
                    res = true;
                    break;
                }
                sw.SpinOnce();
//                Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] {LogData(cur)}");
            }

            // Decrement the Shared Waiter counter
            Interlocked.Add(ref _data, unchecked((ulong)(-(1L << SharedWaitersShift))));
            
            return res;
        }
        
        public bool WaitForTransitionFromExclusiveToShared()
        {
            var res = false;
            
            // Increment the Shared Waiter counter
            Interlocked.Add(ref _data, 1UL << SharedWaitersShift);
            
            var sw = new SpinWait();
            while ((DateTime.UtcNow < _timeOutAt) && !_token.IsCancellationRequested)
            {
                var cur = _data;
                
                // Must be idle, and transitioning to Shared access is the least priority, which means we won't do it as long as there are
                //  Exclusive or Promoter waiters
                if (((cur&StateMask) == IdleState) && ((cur & (ExclusiveWaitersMask | PromoterWaitersMask)) == 0))
                {
                    res = true;
                    break;
                }
                sw.SpinOnce();
            }

            // Decrement the Shared Waiter counter
            Interlocked.Add(ref _data, unchecked((ulong)(-(1L << SharedWaitersShift))));
            
            return res;
        }

        public bool WaitForTransitionFromExclusiveToExclusive()
        {
            var res = false;
            
            // Safely increment the Exclusive Waiter counter
            Interlocked.Add(ref _data, 1UL << ExclusiveWaitersShift);
            
            var sw = new SpinWait();
            while ((DateTime.UtcNow < _timeOutAt) && !_token.IsCancellationRequested)
            {
                var cur = _data;
                
                // Must be idle, and transitioning to Exclusive access is lower priority than Promoter waiters
                if (((cur&StateMask) == IdleState) && ((cur & PromoterWaitersMask) == 0))
                {
                    res = true;
                    break;
                }

                sw.SpinOnce();
            }

            // Decrement the Exclusive Waiter counter
            Interlocked.Add(ref _data, unchecked((ulong)(-(1L << ExclusiveWaitersShift))));
            
            return res;
        }

#if TELEMETRY
        private void EnsureOperationsBlockIdAllocated()
        {
            var blockId = OperationsBlockId;
            if (blockId == 0)
            {
                _allocator.Allocate(out blockId);
                Debug.Assert(blockId < (int)(OperationsBlockIdMask >> OperationsBlockIdShift));
                _hasAllocatedBlock = true;
                OperationsBlockId = blockId;
            }
        }
#else
        [Conditional("TELEMETRY")]
        private void EnsureOperationsBlockIdAllocated()
        {
        }

        [Conditional("TELEMETRY")]
        public void AddWaitOperation(OperationType op)
        {
        }

        [Conditional("TELEMETRY")]
        public void AddTimedOutOrCanceledOperation()
        {
        }

        [Conditional("TELEMETRY")]
        public void AddOperation(ref object op)
        {
        }
#endif
    }
}