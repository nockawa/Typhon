using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
internal class TransactionChain : ResourceNode, IDebugPropertiesProvider
{
    private const int PoolMaxSize = 16;
            
    internal Transaction Head { get; private set; }

    internal Transaction Tail { get; private set; }

    internal long MinTSN { get; private set; }
    internal long NextFreeId => _nextFreeId;

    /// <summary>
    /// Gets the count of active transactions in the chain.
    /// </summary>
    /// <remarks>
    /// Maintained via atomic increment/decrement in PushHead/Remove for O(1) access.
    /// </remarks>
    internal int ActiveCount => _activeCount;

    private AccessControl _control;
    private readonly Queue<Transaction> _pool;
    private readonly int _maxActiveTransactions;
    private long _nextFreeId;
    private int _activeCount;

    public TransactionChain(int maxActiveTransactions, IResource parent) : base("TransactionChain", ResourceType.TransactionPool, parent)
    {
        _maxActiveTransactions = maxActiveTransactions;
        _nextFreeId = 1;
        _pool = new Queue<Transaction>();
        for (int i = 0; i < PoolMaxSize; i++)
        {
            _pool.Enqueue(new Transaction());
        }
    }

    /// <summary>
    /// Sets the next free TSN to the given value. Used during engine reload to restore the TSN counter
    /// from the persisted header so that MVCC visibility works for entities created by previous sessions.
    /// </summary>
    internal void SetNextFreeId(long value) => _nextFreeId = value;

    public ref AccessControl Control => ref _control;

    // Under lock of the caller
    public void PushHead([TransfersOwnership] Transaction transaction)
    {
        Interlocked.Increment(ref _activeCount);
        var curHead = Head;
        Head = transaction;
        transaction.Next = curHead;
        transaction.Previous = null; // New head has no predecessor (clear stale link from pool recycling)

        if (curHead == null)
        {
            Tail = transaction;
            MinTSN = transaction.TSN;
        }
        else
        {
            curHead.Previous = transaction; // Maintain reverse link for Tail→Head traversal
        }
    }

    public void WalkHeadToTail(Func<Transaction, bool> predicate)
    {
        _control.EnterSharedAccess(ref WaitContext.Null);

        var cur = Head;
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

    public void Remove(Transaction transaction)
    {
        _control.EnterExclusiveAccess(ref WaitContext.Null);
        Interlocked.Decrement(ref _activeCount);

        if (transaction.Next != null)
        {
            transaction.Next.Previous = transaction.Previous;
        }

        if (transaction.Previous != null)
        {
            transaction.Previous.Next = transaction.Next;
        }

        if (Tail == transaction)
        {
            Tail = transaction.Previous;
            MinTSN = Tail?.TSN ?? 0;
        }

        if (Head == transaction)
        {
            Head = transaction.Next;
        }

        if (_pool.Count < PoolMaxSize)
        {
            transaction.Reset();
            _pool.Enqueue(transaction);
        }

        _control.ExitExclusiveAccess();
    }

    [return: TransfersOwnership]
    public Transaction CreateTransaction(DatabaseEngine dbe, UnitOfWork uow = null)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_control.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("TransactionChain/CreateTransaction", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        if (_activeCount >= _maxActiveTransactions)
        {
            _control.ExitExclusiveAccess();
            ThrowHelper.ThrowResourceExhausted("Data/TransactionChain/CreateTransaction", ResourceType.Service, _activeCount, _maxActiveTransactions);
        }

        if (!_pool.TryDequeue(out var t))
        {
            t = new Transaction();
        }

        t.Init(dbe, Interlocked.Increment(ref _nextFreeId), uow);
        _control.ExitExclusiveAccess();

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