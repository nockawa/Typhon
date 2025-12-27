
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AccessOperation
{
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

[InlineArray(Count)]
internal struct  AccessOperations
{
    internal const int Count = 6;
    private AccessOperation _element;
    
    // 120 bytes structure
}

#if TELEMETRY
internal static class AccessControlImpl
{
    private static readonly ChainedBlockAllocator<AccessOperations> Allocator;

    static AccessControlImpl()
    {
        // 6 Operations are 120 bytes, +4 for the chain header = 124.
        // We ask 124 for the total size to be 128 header included and to align on two cache lines for faster memory accesses.
        Allocator = new ChainedBlockAllocator<AccessOperations>(1024, 124);
    }
    
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
    
    const int OperationsBlockIdShift    = 8;
    const int SharedWaitersShift        = 22;
    const int ExclusiveWaitersShift     = 32;
    const int PromoterWaitersShift      = 42;
    const int ThreadIdShift             = 52;

    [StructLayout(LayoutKind.Sequential)]
    internal ref struct LockData
    {
        public LockData(ChainedBlockAllocator<AccessOperations> allocator, ref ulong data, TimeSpan? timeOut = null, CancellationToken token = default)
        {
            _data = ref data;
            _allocator = allocator;
            _initial = data;
            _staging = data;
            _token = token;
            _timeOutAt = (timeOut != null) ? (DateTime.UtcNow + timeOut.Value) : DateTime.MaxValue;
            EnsureOperationsBlockIdAllocated();
        }
        
        private ref ulong _data;
        private readonly ChainedBlockAllocator<AccessOperations> _allocator;
        private ulong _initial;
        private ulong _staging;
        private bool _hasAllocatedBlock;
        private readonly DateTime _timeOutAt;
        private readonly CancellationToken _token;

        public bool IsTimedOutOrCanceled => (DateTime.UtcNow > _timeOutAt) || _token.IsCancellationRequested;
        
        public string DebugInfo
        {
            get
            {
                var blockId = OperationsBlockId;
                return (blockId == 0) ? "<Empty>" : ToDebugString(blockId, ref Unsafe.NullRef<AccessOperation>());
            }
        }

        public int Counter
        {
            get => (int)(_staging & CounterMask);
            set => _staging = (_staging & ~CounterMask) | (uint)value;
        }

        public int OperationsBlockId
        {
            get => (int)((_staging & OperationsBlockIdMask) >> OperationsBlockIdShift);
            set => _staging = (_staging & ~OperationsBlockIdMask) | (ulong)value << OperationsBlockIdShift;
        }

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
                if (_hasAllocatedBlock)
                {
                    // Extract block ID directly from _staging to avoid allocating a new block via the getter
                    var blockId = (int)((_staging & OperationsBlockIdMask) >> OperationsBlockIdShift);
                    if (blockId != 0)
                    {
                        _allocator.Free(blockId);
                    }
                    _hasAllocatedBlock = false;
                }
                Fetch();
            }

            return succeed;
        }

        internal void Fetch()
        {
            _initial = _staging = _data;
            EnsureOperationsBlockIdAllocated();
        }

        public void AddOperation(ref AccessOperation op)
        {
            var blockId = OperationsBlockId;
            Debug.Assert(blockId != 0);
            
            // Get the first bank of operations
            ref var ops = ref Allocator.Get(blockId);
            while (true)
            {
                // Parse them to find the first empty
                for (int i = 0; i < AccessOperations.Count; i++)
                {
                    // Try to reserve the entry, will fail if it's already taken -> we loop
                    ref var intPtr = ref Unsafe.As<AccessOperation, int>(ref ops[i]);
                    if (Interlocked.CompareExchange(ref intPtr, 1, 0) != 0)
                    {
                        continue;
                    }

                    // Successfully reserved, copy the data and exit
                    ops[i] = op;

                    var sb = CachedToDebugStringBuilders.Value.Clear();
                    LogOp(ref op, sb);
                    Console.Write(sb.ToString());
                    
                    return;
                }

                // All the entries are taken, go to the next bank
                ref var nextOps = ref Allocator.Next(ref ops);
        
                // End of the chain? Allocate a new bank and loop
                if (Unsafe.IsNullRef(ref nextOps))
                {
                    nextOps = ref Allocator.SafeAppend(ref ops);
                }

                ops = ref nextOps;
            }
        }

        public void AddWaitOperation(AccessOperation.OperationType type)
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

        private void EnsureOperationsBlockIdAllocated()
        {
            var blockId = (int)((_staging & OperationsBlockIdMask) >> OperationsBlockIdShift);
            if (blockId == 0)
            {
                _allocator.Allocate(out blockId);
                Debug.Assert(blockId < (int)(OperationsBlockIdMask >> OperationsBlockIdShift));
                _hasAllocatedBlock = true;
                OperationsBlockId = blockId;
            }
        }
    }
    
    internal static bool EnterSharedAccess(ref ulong lockData, TimeSpan? timeOut = null, CancellationToken token = default)
    {
        var op = new AccessOperation(AccessOperation.OperationType.EnterSharedAccess);
        var ld = new LockData(Allocator, ref lockData, timeOut, token);
        
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
                    ld.AddWaitOperation(AccessOperation.OperationType.SharedStartWait);
                    if (!ld.WaitForSharedCanStart())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }
                    ld.AddWaitOperation(AccessOperation.OperationType.SharedEndWait);
                    
                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
                
                // Already in Shared, increment counter
                case SharedState:
                    ld.Counter++;
                    break;

                // Can't enter shared because we are in exclusive, wait for idle and loop
                case ExclusiveState:
                    ld.AddWaitOperation(AccessOperation.OperationType.SharedStartWait);

                    if (!ld.WaitForTransitionFromExclusiveToShared())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }

                    ld.AddWaitOperation(AccessOperation.OperationType.SharedEndWait);

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS

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
            ld.AddOperation(ref op);
            return true;
        }
    }
    
    internal static void ExitSharedAccess(ref ulong lockData)
    {
        var op = new AccessOperation(AccessOperation.OperationType.ExitSharedAccess);
        var ld = new LockData(Allocator, ref lockData);

        while (true)
        {
            var blockId = ld.OperationsBlockId;
            var isLastOp = false;

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
                        isLastOp = ld.IsIdleNoWaiters;
                        if (isLastOp)
                        {
                            ld.OperationsBlockId = 0;
                        }
                    }

                    break;

                case ExclusiveState:
                    break;
                
                
                case IdleState:
                    break;
            }

            op.LockData = ld.Staging;
            op.Now();

            if (!ld.TryUpdate())
            {
                continue;
            }

            if (isLastOp)
            {
                // TODO OTLP reporting here
                //Console.WriteLine(ToDebugString(blockId, ref op));
                    
                // Free the whole chain as the lock turns back to idle
                Allocator.Free(blockId);
            }
            else
            {
                ld.AddOperation(ref op);
            }
            return;
        }
    }

    internal static bool EnterExclusiveAccess(ref ulong lockData, TimeSpan? timeOut = null, CancellationToken token = default)
    {
        var op = new AccessOperation(AccessOperation.OperationType.EnterExclusiveAccess);
        var ld = new LockData(Allocator, ref lockData, timeOut, token);

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
                    ld.AddWaitOperation(AccessOperation.OperationType.ExclusiveStartWait);

                    if (!ld.WaitForTransitionFromExclusiveToExclusive())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }

                    ld.AddWaitOperation(AccessOperation.OperationType.ExclusiveEndWait);

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
                    ld.AddWaitOperation(AccessOperation.OperationType.ExclusiveStartWait);

                    if (!ld.WaitForTransitionFromExclusiveToExclusive())
                    {
                        ld.AddTimedOutOrCanceledOperation();
                        return false;
                    }

                    ld.AddWaitOperation(AccessOperation.OperationType.ExclusiveEndWait);

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

            op.LockData = ld.Staging;
            op.Now();                       // As close as possible to the CAS

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
            ld.AddOperation(ref op);
            return true;
        }
    }

    internal static void ExitExclusiveAccess(ref ulong lockData)
    {
        var op = new AccessOperation(AccessOperation.OperationType.ExitExclusiveAccess);
        var ld = new LockData(Allocator, ref lockData);

        while (true)
        {
            var blockId = ld.OperationsBlockId;
            var isLastOp = false;

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
                        isLastOp = ld.IsIdleNoWaiters;
                        if (isLastOp)
                        {
                            ld.OperationsBlockId = 0;
                        }
                    }

                    break;

                case SharedState:
                    break;
                
                case IdleState:
                    break;
            }

            op.LockData = ld.Staging;
            op.Now();

            if (!ld.TryUpdate())
            {
                continue;
            }

            if (isLastOp)
            {
                // TODO OTLP reporting here
                //Console.WriteLine(ToDebugString(blockId, ref op));
    
                // Free the whole chain as the lock turns back to idle
                Allocator.Free(blockId);
            }
            else
            {
                ld.AddOperation(ref op);
            }
            return;
        }    
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

    private static string ToAlignedOp(AccessOperation.OperationType type)
    {
        switch (type)
        {
            case AccessOperation.OperationType.EnterSharedAccess:       return "Shared    Start";
            case AccessOperation.OperationType.ExitSharedAccess:        return "Shared    Exit ";
            case AccessOperation.OperationType.EnterExclusiveAccess:    return "Exclusive Start";
            case AccessOperation.OperationType.ExitExclusiveAccess:     return "Exclusive Exit ";
            case AccessOperation.OperationType.SharedStartWait:         return "Wait (S)  Start";
            case AccessOperation.OperationType.SharedEndWait:           return "Wait (S)  End  ";
            case AccessOperation.OperationType.ExclusiveStartWait:      return "Wait (E)  Start";
            case AccessOperation.OperationType.ExclusiveEndWait:        return "Wait (E)  End  ";
            case AccessOperation.OperationType.TimedOutOrCanceled:      return "Timeout/Cancel ";
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
                ref var op = ref ops[i];
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

    private static void WaitForState(ref uint lockData, uint state)
    {
        var spin = new SpinWait();
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

    public static void Reset(ref ulong data)
    {
        data = 0;
    }
}
#endif
