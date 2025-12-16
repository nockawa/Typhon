using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
internal class TransactionChain : IDisposable
{
    private const int PoolMaxSize = 16;
            
    internal Transaction Head { get; private set; }

    internal Transaction Tail { get; private set; }

    internal long MinTSN { get; private set; }

    private AccessControl _control;
    private readonly Queue<Transaction> _pool;
    private long _nextFreeId;
    
    public TransactionChain()
    {
        _nextFreeId = 1;
        _pool = new Queue<Transaction>();
        for (int i = 0; i < PoolMaxSize; i++)
        {
            _pool.Enqueue(new Transaction());
        }
    }

    public AccessControl Control => _control;
    
    // Under lock of the caller
    public void PushHead(Transaction transaction)
    {
        var curHead = Head;
        Head = transaction;
        transaction.Next = curHead;

        if (curHead == null)
        {
            Tail = transaction;
            MinTSN = transaction.TSN;
        }
    }

    public void WalkHeadToTail(Func<Transaction, bool> predicate)
    {
        _control.EnterSharedAccess();
            
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
        _control.EnterExclusiveAccess();

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

    public Transaction CreateTransaction(DatabaseEngine dbe)
    {
        _control.EnterExclusiveAccess();
        if (!_pool.TryDequeue(out var t))
        {
            t = new Transaction();
        }

        t.Init(dbe, Interlocked.Increment(ref _nextFreeId));
        _control.ExitExclusiveAccess();

        // Are we getting short on Ids? The max is 1 << 47
        if (_nextFreeId > (1L << 46))
        {
            // TODO Log some warning
        }
        
        return t;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        _pool.Clear();
        IsDisposed = true;
        GC.SuppressFinalize(this);
    }

    public bool IsDisposed { get; private set; }
}