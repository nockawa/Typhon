using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
internal class TransactionChain : ResourceNode, IDebugPropertiesProvider
{
    private const int PoolMaxSize = 16;

    private Transaction _head;
    private Transaction _tail;
    private long _minTSN;

    internal Transaction Head => Volatile.Read(ref _head);
    internal Transaction Tail => Volatile.Read(ref _tail);
    internal long MinTSN => Volatile.Read(ref _minTSN);
    internal long NextFreeId => _nextFreeId;

    /// <summary>
    /// Gets the count of active transactions in the chain.
    /// </summary>
    /// <remarks>
    /// Maintained via atomic increment/decrement in PushHead/Remove for O(1) access.
    /// </remarks>
    internal int ActiveCount => _activeCount;

    private AccessControl _control;
    private readonly ConcurrentQueue<Transaction> _pool;
    private readonly int _maxActiveTransactions;
    private long _nextFreeId;
    private int _activeCount;

    public TransactionChain(int maxActiveTransactions, IResource parent) : base("TransactionChain", ResourceType.TransactionPool, parent)
    {
        _maxActiveTransactions = maxActiveTransactions;
        _nextFreeId = 1;
        _pool = new ConcurrentQueue<Transaction>();
        for (int i = 0; i < PoolMaxSize; i++)
        {
            _pool.Enqueue(new Transaction());
        }
    }

    /// <summary>
    /// Sets the next free TSN to the given value. Used during engine reload to restore the TSN counter from the persisted header so that MVCC visibility
    /// works for entities created by previous sessions.
    /// </summary>
    internal void SetNextFreeId(long value) => _nextFreeId = value;

    public ref AccessControl Control => ref _control;

    /// <summary>
    /// Lock-free CAS-based stack push. Concurrent with Remove (which holds exclusive lock) and other PushHead callers (CAS contention only).
    /// </summary>
    public void PushHead([TransfersOwnership] Transaction transaction)
    {
        Interlocked.Increment(ref _activeCount);
        Transaction oldHead;
        do
        {
            oldHead = Volatile.Read(ref _head);
            transaction.Next = oldHead;
        } while (Interlocked.CompareExchange(ref _head, transaction, oldHead) != oldHead);

        if (oldHead == null)
        {
#pragma warning disable TYPHON004 // CAS on _tail — returned old value is intentionally discarded (conditional write, not a leak)
            Interlocked.CompareExchange(ref _tail, transaction, null);
#pragma warning restore TYPHON004
            Volatile.Write(ref _minTSN, transaction.TSN);
        }
    }

    public void WalkHeadToTail(Func<Transaction, bool> predicate)
    {
        _control.EnterSharedAccess(ref WaitContext.Null);

        var cur = Volatile.Read(ref _head);
        while (cur != null)
        {
            if (!predicate(cur))
            {
                break;
            }

            cur = cur.Next;
        }

        _control.ExitSharedAccess();
    }

    /// <summary>
    /// Computes the next MinTSN by walking the singly-linked chain to find the second-to-last transaction.
    /// Caller must hold shared lock on Control.
    /// </summary>
    internal long ComputeNextMinTSN()
    {
        var cur = Volatile.Read(ref _head);
        Transaction secondToLast = null;
        while (cur != null && cur.Next != null)
        {
            secondToLast = cur;
            cur = cur.Next;
        }
        return secondToLast?.TSN ?? (_nextFreeId + 1);
    }

    /// <summary>
    /// Removes a transaction from the chain. Holds exclusive lock to serialize with other Removes.
    /// Uses CAS on _head with re-scan fallback to handle concurrent PushHead.
    /// </summary>
    public void Remove(Transaction transaction)
    {
        _control.EnterExclusiveAccess(ref WaitContext.Null);
        Interlocked.Decrement(ref _activeCount);

        // Scan for predecessor (singly-linked)
        var cur = Volatile.Read(ref _head);
        Transaction prev = null;
        while (cur != null && cur != transaction)
        {
            prev = cur;
            cur = cur.Next;
        }

        // Unlink
        if (prev != null)
        {
            prev.Next = transaction.Next;
        }
        else
        {
            if (Interlocked.CompareExchange(ref _head, transaction.Next, transaction) != transaction)
            {
                // PushHead prepended nodes between our Volatile.Read and CAS — re-scan for new predecessor
                var p = Volatile.Read(ref _head);
                while (p.Next != transaction)
                {
                    p = p.Next;
                }
                p.Next = transaction.Next;
            }
        }

        // Tail maintenance
        if (Volatile.Read(ref _tail) == transaction)
        {
            var newTail = Volatile.Read(ref _head);
            if (newTail != null)
            {
                while (newTail.Next != null)
                {
                    newTail = newTail.Next;
                }
            }
            Volatile.Write(ref _tail, newTail);
            Volatile.Write(ref _minTSN, newTail?.TSN ?? 0);
        }

        // Capture pool decision under lock; transaction is fully unlinked, so Reset+Enqueue can safely happen outside the critical section to reduce hold time.
        bool shouldPool = _pool.Count < PoolMaxSize;

        _control.ExitExclusiveAccess();

        if (shouldPool)
        {
            transaction.Reset();
            _pool.Enqueue(transaction);
        }
    }

    /// <summary>
    /// Allocates a monotonically-increasing TSN without creating a Transaction.
    /// Used by <see cref="PointInTimeAccessor"/> to get an MVCC visibility snapshot point.
    /// </summary>
    internal long AllocateTSN() => Interlocked.Increment(ref _nextFreeId);

    /// <summary>
    /// Lock-free transaction creation. No lock acquired — uses ConcurrentQueue for pooling, atomic TSN increment, and CAS-based PushHead.
    /// </summary>
    [return: TransfersOwnership]
    public Transaction CreateTransaction(DatabaseEngine dbe, UnitOfWork uow = null, bool readOnly = false)
    {
        if (_activeCount >= _maxActiveTransactions)
        {
            ThrowHelper.ThrowResourceExhausted("Data/TransactionChain/CreateTransaction", ResourceType.Service, _activeCount, _maxActiveTransactions);
        }

        if (!_pool.TryDequeue(out var t))
        {
            t = new Transaction();
        }

        t.Init(dbe, Interlocked.Increment(ref _nextFreeId), uow, readOnly);

        // Are we getting short on Ids? The max is 1 << 47
        if (_nextFreeId > (1L << 46))
        {
            // TODO Log some warning
        }

        return t;
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            _pool.Clear();
        }

        base.Dispose(disposing);
        IsDisposed = true;
    }

    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["ActiveCount"] = _activeCount,
            ["MaxActive"] = _maxActiveTransactions,
            ["MinTSN"] = MinTSN,
            ["NextFreeId"] = _nextFreeId,
            ["Pool.Available"] = _pool.Count,
            ["Pool.MaxSize"] = PoolMaxSize,
        };
}