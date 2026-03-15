// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
[DebuggerDisplay("TSN {TSN}, State: {State}")]
public unsafe partial class Transaction : IDisposable
{
    private const int RandomAccessCachedPagesCount = 8;
    private const int ComponentInfosMaxCapacity = 131;
    private const int DeferredEnqueueBatchCapacity = 256;

    /// <summary>
    /// Number of entity operations between epoch refreshes. Each operation touches ~4-20 pages.
    /// At 128 ops × ~10 pages/op = ~1280 pages — refreshes before saturating a 1024-page cache.
    /// </summary>
    private const int EpochRefreshInterval = 128;

    public enum TransactionState
    {
        Invalid = 0,
        Created,            // New object, no operation done yet
        InProgress,         // At least one operation added to the transaction
        Rollbacked,         // Was rollbacked by the user or during dispose
        Committed           // Was committed by the user
    }

    public TransactionState State { get; private set; }
    private bool _isDisposed;
    private DatabaseEngine _dbe;
    private EpochManager _epochManager;

#if DEBUG
    private int _debugOwningThreadId;
#endif

    private Dictionary<Type, ComponentInfo> _componentInfos;

    private int? _committedOperationCount;
    private int _deletedComponentCount;
    private int _entityOperationCount;
    private ChangeSet _changeSet;

    // Reused across pooled Transaction lifetimes — collects deferred enqueue entries per commit/rollback
    // to flush in a single batch (one lock acquire instead of N). Never re-allocated after warmup.
    private List<DeferredCleanupManager.CleanupEntry> _deferredEnqueueBatch;

    // Hoisted accessors for batch index maintenance (set per component type during Commit)
    private ChunkAccessor<PersistentStore>[] _batchIndexAccessors;
    private ChunkAccessor<PersistentStore> _batchPkAccessor;
    private ChunkAccessor<PersistentStore> _batchTailAccessor;
    private bool _batchIndexActive;
    private int _batchEntityCount;

    /// <summary>The UoW that owns this transaction (null for legacy <c>CreateTransaction()</c> path, UoW ID effectively 0).</summary>
    internal UnitOfWork OwningUnitOfWork { get; private set; }

    /// <summary>When true, <see cref="Dispose"/> also disposes <see cref="OwningUnitOfWork"/>. Set by <c>CreateQuickTransaction()</c>.</summary>
    internal bool OwnsUnitOfWork { get; set; }

    /// <summary>When true, all write operations (Create/Update/Delete/Commit) are forbidden. No ChangeSet or UoW is allocated.</summary>
    public bool IsReadOnly { get; internal set; }

    /// <summary>UoW ID for revision stamping. 0 until UoW Registry (#51) assigns real IDs.</summary>
    internal ushort UowId => OwningUnitOfWork?.UowId ?? 0;

    public Transaction Next { get; internal set; }

    public int CommittedOperationCount
    {
        get
        {
            if (_committedOperationCount.HasValue == false)
            {
                var count = 0;
                foreach (var componentInfo in _componentInfos.Values)
                {
                    count += componentInfo.EntryCount;
                }
                _committedOperationCount = count + _deletedComponentCount;
            }
            return _committedOperationCount.Value;
        }
    }
    
    public long TSN { get; private set; }

    public Transaction()
    {
        _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
    }

    public void Init(DatabaseEngine dbe, long tsn, UnitOfWork uow = null, bool readOnly = false)
    {
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        _dbe.LogTxInitPhase(tsn, "entering epoch");
        _ = _epochManager.EnterScope(); // Depth unused: Transaction uses ExitScopeUnordered (not LIFO)
        _dbe.LogTxInitPhase(tsn, "epoch entered");
        _isDisposed = false;
        IsReadOnly = readOnly;
        OwningUnitOfWork = uow;
#if DEBUG
        _debugOwningThreadId = Environment.CurrentManagedThreadId;
#endif
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _entityOperationCount = 0;
        _changeSet = readOnly ? null : (uow?.ChangeSet ?? _dbe.MMF.CreateChangeSet());
        State = TransactionState.Created;
        TSN = tsn;

        _dbe.TransactionChain.PushHead(this);
    }

    internal void Reset()
    {
        _dbe = null;
        _epochManager = null;
        OwningUnitOfWork = null;
        OwnsUnitOfWork = false;
        IsReadOnly = false;
#if DEBUG
        _debugOwningThreadId = 0;
#endif
        if (_componentInfos.Capacity <= ComponentInfosMaxCapacity)
        {
            _componentInfos.Clear();
        }
        else
        {
            _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
        }
        // Don't touch _isDisposed on purpose

        TSN = 0;
        Next = null;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = null;
        _deferredEnqueueBatch?.Clear();

        // Clean up ECS state
        CleanupEcsState();
    }

    [Conditional("DEBUG")]
    private void AssertThreadAffinity()
    {
#if DEBUG
        Debug.Assert(
            _debugOwningThreadId == Environment.CurrentManagedThreadId,
            "Transaction thread affinity violation: current thread differs from the creating thread. " +
            "Transactions are single-thread-affine — all operations must run on the creating thread.");
#endif
    }

    /// <summary>Throws if the transaction cannot accept new operations (read-only or already finalized).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMutable()
    {
        if (IsReadOnly)
        {
            ThrowHelper.ThrowInvalidOp("Cannot perform write operations on a read-only transaction");
        }

        if (State > TransactionState.InProgress)
        {
            ThrowHelper.ThrowInvalidOp($"Cannot perform CRUD on a transaction in state {State}");
        }
    }

    /// <summary>Validates and performs a state transition. Illegal transitions assert in Debug.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TransitionTo(TransactionState newState)
    {
        Debug.Assert(IsLegalTransition(State, newState),
            $"Illegal transaction state transition: {State} → {newState}");
        State = newState;
    }

    private static bool IsLegalTransition(TransactionState from, TransactionState to) => (from, to) switch
    {
        (TransactionState.Created, TransactionState.InProgress) => true,
        (TransactionState.Created, TransactionState.Committed) => true,
        (TransactionState.Created, TransactionState.Rollbacked) => true,
        (TransactionState.InProgress, TransactionState.Committed) => true,
        (TransactionState.InProgress, TransactionState.Rollbacked) => true,
        _ => false,
    };

    public void Dispose()
    {
        // Dispose transient batch accessors first — safe even on double-dispose or if never created
        // (ChunkAccessor<PersistentStore>.Dispose() is idempotent: no-op when _segment==null).
        _batchPkAccessor.Dispose();
        _batchTailAccessor.Dispose();

        if (_isDisposed)
        {
            return;
        }

        AssertThreadAffinity();

        // Read-only transactions have no ChangeSet, UoW, or commit/rollback path —
        // just exit the epoch scope and remove from the chain.
        if (IsReadOnly)
        {
            _isDisposed = true;
            ExitEpochAndRemove();
            return;
        }

        // Capture before ExitEpochAndRemove — Remove pools and Reset() nulls _dbe
        var dbe = _dbe;
        var tsn = TSN;

        dbe.LogTxDispose(tsn, "EnsureCompleted");
        EnsureCompleted();
        dbe.LogTxDispose(tsn, "ProcessDeferredCleanups");
        ProcessDeferredCleanups();
        dbe.LogTxDispose(tsn, "FlushAccessors");
        FlushAccessors();
        dbe.LogTxDispose(tsn, "PersistIfNeeded");
        PersistIfNeeded();
        // Mark disposed BEFORE ExitEpochAndRemove: Remove() pools the object, and a lock-free
        // CreateTransaction can immediately dequeue and Init it (_isDisposed = false). If we set _isDisposed = true AFTER Remove returns, we'd overwrite the
        // new owner's flag, causing their Dispose to skip Remove — leaking the chain node.
        _isDisposed = true;
        dbe.LogTxDispose(tsn, "ExitEpochAndRemove");
        ExitEpochAndRemove();
    }

    /// <summary>Auto-rollback if not yet committed.</summary>
    private void EnsureCompleted()
    {
        if (State != TransactionState.Committed)
        {
            Rollback();
        }
    }

    /// <summary>Process deferred cleanups if this transaction was blocking them as tail.</summary>
    private void ProcessDeferredCleanups()
    {
        if (_dbe.DeferredCleanupManager.QueueSize > 0)
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
            if (_dbe.TransactionChain.Control.EnterSharedAccess(ref wc))
            {
                var isTail = _dbe.TransactionChain.Tail == this;
                var nextMinTSN = isTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
                _dbe.TransactionChain.Control.ExitSharedAccess();

                if (isTail)
                {
                    _dbe.DeferredCleanupManager.ProcessDeferredCleanups(TSN, nextMinTSN, _dbe, _changeSet);
                }
            }
        }
    }

    /// <summary>Dispose all ChunkAccessors to flush dirty pages.</summary>
    private void FlushAccessors()
    {
        foreach (var info in _componentInfos.Values)
        {
            info.DisposeAccessors();
        }
    }

    /// <summary>WAL-less GroupCommit: write dirty pages to OS cache.</summary>
    private void PersistIfNeeded()
    {
        if (State == TransactionState.Committed && _dbe.WalManager == null
            && OwningUnitOfWork?.DurabilityMode == DurabilityMode.GroupCommit)
        {
            _changeSet.SaveChanges();
        }
    }

    /// <summary>Exit epoch scope, remove from chain, auto-dispose owned UoW.</summary>
    private void ExitEpochAndRemove()
    {
        // Capture before Remove() — Remove pools the transaction and Reset() nulls _dbe
        var dbe = _dbe;
        var tsn = TSN;

        dbe.LogTxDispose(tsn, "ExitEpoch");
        _epochManager.ExitScopeUnordered();
        var owningUow = OwnsUnitOfWork ? OwningUnitOfWork : null;
        dbe.LogTxDispose(tsn, "ChainRemove");
        _dbe.TransactionChain.Remove(this);
        if (owningUow != null)
        {
            dbe.LogTxDispose(tsn, "UoWDispose");
            owningUow.Dispose();
        }
        dbe.LogTxDispose(tsn, "ExitEpochAndRemoveDone");
    }

    public long CreateEntity<T>(ref T t) where T : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        using var activity = TyphonActivitySource.StartActivity("Transaction.CreateEntity");
        activity?.SetTag(TyphonSpanAttributes.ComponentType, typeof(T).Name);

        var pk = _dbe.GetNewPrimaryKey();
        activity?.SetTag(TyphonSpanAttributes.EntityId, pk);

        CreateComponent(pk, ref t);
        return pk;
    }

    public long CreateEntity<TC1, TC2>(ref TC1 t, ref TC2 u) where TC1 : unmanaged where TC2 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        var pk = _dbe.GetNewPrimaryKey();

        CreateComponent(pk, ref t);
        CreateComponent(pk, ref u);
        return pk;
    }

    public long CreateEntity<TC1, TC2, TC3>(ref TC1 t, ref TC2 u, ref TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        var pk = _dbe.GetNewPrimaryKey();

        CreateComponent(pk, ref t);
        CreateComponent(pk, ref u);
        CreateComponent(pk, ref v);
        return pk;
    }

    public long CreateEntity<TC1, TC2>(ref TC1 t, Span<TC2> u) where TC1 : unmanaged where TC2 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        var pk = _dbe.GetNewPrimaryKey();

        CreateComponent(pk, ref t);
        CreateComponents(pk, u);
        return pk;
    }

    public bool ReadEntity<T>(long pk, out T t) where T : unmanaged
    {
        using var activity = TyphonActivitySource.StartActivity("Transaction.ReadEntity");
        activity?.SetTag(TyphonSpanAttributes.EntityId, pk);
        activity?.SetTag(TyphonSpanAttributes.ComponentType, typeof(T).Name);

        var result = ReadComponent(pk, out t);
        activity?.SetTag(TyphonSpanAttributes.ReadFound, result);
        return result;
    }

    public bool ReadEntity<TC1, TC2>(long pk, out TC1 t, out TC2 u) where TC1 : unmanaged where TC2 : unmanaged
    {
        var res = ReadComponent(pk, out t);
        res &= ReadComponent(pk, out u);
        return res;
    }

    public bool ReadEntity<TC1, TC2, TC3>(long pk, out TC1 t, out TC2 u, out TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        var res = ReadComponent(pk, out t);
        res &= ReadComponent(pk, out u);
        res &= ReadComponent(pk, out v);
        return res;
    }

    public bool ReadEntity<TC1, TC2>(long pk, out TC1 t, out TC2[] u) where TC1 : unmanaged where TC2 : unmanaged
    {
        var res = ReadComponent(pk, out t);
        res &= ReadComponents(pk, out u);
        return res;
    }

    public bool UpdateEntity<T>(long pk, ref T t) where T : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        using var activity = TyphonActivitySource.StartActivity("Transaction.UpdateEntity");
        activity?.SetTag(TyphonSpanAttributes.EntityId, pk);
        activity?.SetTag(TyphonSpanAttributes.ComponentType, typeof(T).Name);

        return UpdateComponent(pk, ref t);
    }

    public bool UpdateEntity<TC1, TC2>(long pk, ref TC1 t, ref TC2 u) where TC1 : unmanaged where TC2 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref t);
        res &= UpdateComponent(pk, ref u);
        return res;
    }

    public bool UpdateEntity<TC1, TC2, TC3>(long pk, ref TC1 t, ref TC2 u, ref TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref t);
        res &= UpdateComponent(pk, ref u);
        res &= UpdateComponent(pk, ref v);
        return res;
    }

    public bool UpdateEntity<TC1, TC2>(long pk, ref TC1 t, ReadOnlySpan<TC2> u) where TC1 : unmanaged where TC2 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref t);
        res &= UpdateComponents(pk, u);
        return res;
    }

    public bool DeleteEntity<T>(long pk) where T : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        using var activity = TyphonActivitySource.StartActivity("Transaction.DeleteEntity");
        activity?.SetTag(TyphonSpanAttributes.EntityId, pk);
        activity?.SetTag(TyphonSpanAttributes.ComponentType, typeof(T).Name);

        return DeleteComponent<T>(pk);
    }

    public bool DeleteEntity<TC1, TC2>(long pk) where TC1 : unmanaged where TC2 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        var res = DeleteComponent<TC1>(pk);
        res &= DeleteComponent<TC2>(pk);
        return res;
    }

    public bool DeleteEntity<TC1, TC2, TC3>(long pk) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        var res = DeleteComponent<TC1>(pk);
        res &= DeleteComponent<TC2>(pk);
        res &= DeleteComponent<TC3>(pk);
        return res;
    }

    public bool DeleteEntities<T>(long pk) where T : unmanaged
    {
        EnsureMutable();
        State = TransactionState.InProgress;

        return UpdateComponents(pk, ReadOnlySpan<T>.Empty);
    }
    
    public int GetComponentRevision<T>(long pk) where T : unmanaged
    {
        AssertThreadAffinity();
        var info = GetComponentInfo(typeof(T));
        ComponentInfo.CompRevInfo compRevInfo;
        if (info.IsMultiple)
        {
            if (!info.MultipleCache.TryGetValue(pk, out var compRevInfoList))
            {
                return -1;
            }
            compRevInfo = compRevInfoList[0];
        }
        else
        {
            if (!info.SingleCache.TryGetValue(pk, out compRevInfo))
            {
                return -1;
            }
        }

        // After getting from the cache, check if it was deleted
        if (compRevInfo.CurCompContentChunkId == 0)
        {
            return -1;
        }

        return compRevInfo.ReadCommitSequence + (compRevInfo.CurRevisionIndex - compRevInfo.ReadRevisionIndex);
    }

    public ComponentCollectionAccessor<T> CreateComponentCollectionAccessor<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ComponentCollectionAccessor<T>(_changeSet, _dbe.GetComponentCollectionVSBS<T>(), ref field);
    }

    public ReadOnlyCollectionEnumerator<T> GetReadOnlyCollectionEnumerator<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ReadOnlyCollectionEnumerator<T>(_dbe.GetComponentCollectionVSBS<T>(), field._bufferId);
    }

    public int GetComponentCollectionRefCounter<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        var vsbs = _dbe.GetComponentCollectionVSBS<T>();
        using var a = new VariableSizedBufferAccessor<T, PersistentStore>(vsbs, field._bufferId);

        return a.RefCounter;
    }
    
    [PublicAPI]
    public ref struct ReadOnlyCollectionEnumerator<T> where T : unmanaged
    {
        private BufferEnumerator<T, PersistentStore> _enumerator;

        public ReadOnlyCollectionEnumerator(VariableSizedBufferSegment<T, PersistentStore> vsbs, int bufferId)
        {
            _enumerator = vsbs.EnumerateBuffer(bufferId);
        }

        public ReadOnlyCollectionEnumerator<T> GetEnumerator() => this;

        public ref readonly T Current
        {
            get => ref _enumerator.Current;
        }
        
        public bool MoveNext() => _enumerator.MoveNext();

        public void Dispose() => _enumerator.Dispose();
    }

    /// <summary>
    /// Returns an enumerator that streams all entities with component <typeparamref name="T"/> in primary key order, filtered by MVCC visibility at this
    /// transaction's snapshot.
    /// </summary>
    /// <param name="minPK">Inclusive lower bound of the PK range (default: all).</param>
    /// <param name="maxPK">Inclusive upper bound of the PK range (default: all).</param>
    public PKEntityEnumerator<T> EnumeratePK<T>(long minPK = long.MinValue, long maxPK = long.MaxValue) where T : unmanaged
    {
        AssertThreadAffinity();
        var ct = _dbe.GetComponentTable<T>() ?? throw new InvalidOperationException($"Component '{typeof(T).Name}' is not registered.");

        if (ct.Definition.AllowMultiple)
        {
            throw new InvalidOperationException("EnumeratePK does not support AllowMultiple components. Use the single-value component API.");
        }

        return new PKEntityEnumerator<T>(ct, this, minPK, maxPK, _changeSet);
    }

    /// <summary>
    /// Returns an enumerator that streams entities via any index (primary or secondary) in key order, filtered by MVCC visibility at this transaction's snapshot.
    /// For PK indexes, <typeparamref name="TKey"/> must be <c>long</c>.
    /// </summary>
    public IndexEntityEnumerator<T, TKey> EnumerateIndex<T, TKey>(IndexRef indexRef, TKey minKey, TKey maxKey) where T : unmanaged where TKey : unmanaged
    {
        AssertThreadAffinity();
        indexRef.Validate();

        var ct = indexRef.Table;
        BTree<TKey, PersistentStore> typedIndex;

        if (indexRef.IsPrimaryKey)
        {
            if (ct.PrimaryKeyIndex is not BTree<TKey, PersistentStore> pkIndex)
            {
                throw new InvalidOperationException($"PK index requires TKey=long, caller specified '{typeof(TKey).Name}'.");
            }

            typedIndex = pkIndex;
        }
        else
        {
            var ifi = ct.IndexedFieldInfos[indexRef.FieldIndex];
            if (ifi.Index is not BTree<TKey, PersistentStore> skIndex)
            {
                throw new InvalidOperationException(
                    $"TKey type mismatch: index uses '{ifi.Index.GetType().GenericTypeArguments[0].Name}', caller specified '{typeof(TKey).Name}'.");
            }

            typedIndex = skIndex;
        }

        return new IndexEntityEnumerator<T, TKey>(typedIndex, ct, this, minKey, maxKey, _changeSet);
    }

    /// <summary>
    /// MVCC-correct streaming enumerator over the primary key B+Tree leaf chain.
    /// Yields entities in PK order. Use <see cref="CurrentComponent"/> for zero-copy ref access
    /// into page memory, or <see cref="Current"/> for a convenience copy.
    /// </summary>
    [PublicAPI]
    public ref struct PKEntityEnumerator<T> where T : unmanaged
    {
        private BTree<long, PersistentStore>.RangeEnumerator _inner;
        private ChunkAccessor<PersistentStore> _compRevAccessor;
        private ChunkAccessor<PersistentStore> _compContentAccessor;
        private readonly Transaction _tx;
        private readonly long _transactionTSN;
        private readonly int _componentOverhead;
        private int _entityCount;
        private long _currentPK;
        private ReadOnlySpan<byte> _currentComponentSpan;
        private bool _disposed;

        internal PKEntityEnumerator(ComponentTable ct, Transaction tx, long minPK, long maxPK, ChangeSet changeSet)
        {
            _inner = ct.PrimaryKeyIndex.EnumerateRange(minPK, maxPK);
            _compRevAccessor = ct.CompRevTableSegment.CreateChunkAccessor(changeSet);
            _compContentAccessor = ct.ComponentSegment.CreateChunkAccessor(changeSet);
            _tx = tx;
            _transactionTSN = tx.TSN;
            _componentOverhead = ct.ComponentOverhead;
            _entityCount = 0;
            _currentPK = 0;
            _currentComponentSpan = default;
            _disposed = false;
        }

        public PKEntityEnumerator<T> GetEnumerator() => this;

        /// <summary>Convenience accessor — copies the component into a tuple. Prefer <see cref="CurrentComponent"/> for zero-copy.</summary>
        public (long EntityPK, T Component) Current => (_currentPK, MemoryMarshal.AsRef<T>(_currentComponentSpan));

        /// <summary>Zero-copy ref into the epoch-protected page memory. Valid until the next <see cref="MoveNext"/> call.</summary>
        public ref readonly T CurrentComponent => ref MemoryMarshal.AsRef<T>(_currentComponentSpan);

        /// <summary>The primary key of the current entity.</summary>
        public long CurrentEntityPK => _currentPK;

        public bool MoveNext()
        {
            while (_inner.MoveNext())
            {
                var kv = _inner.Current;
                long pk = kv.Key;
                int compRevFirstChunkId = kv.Value;

                // MVCC visibility check
                var result = RevisionChainReader.WalkChain(ref _compRevAccessor, compRevFirstChunkId, _transactionTSN);
                if (result.IsFailure || result.Value.CurCompContentChunkId == 0)
                {
                    // SnapshotInvisible or Deleted — skip
                    continue;
                }

                // Store span into page memory — no copy
                _currentPK = pk;
                var src = _compContentAccessor.GetChunkAsReadOnlySpan(result.Value.CurCompContentChunkId);
                _currentComponentSpan = src.Slice(_componentOverhead);

                // Epoch refresh cadence
                if (++_entityCount % EpochRefreshInterval == 0)
                {
                    _tx.EnumerateRefreshEpoch();
                }

                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _inner.Dispose();
                _compRevAccessor.Dispose();
                _compContentAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// MVCC-correct streaming enumerator over any index B+Tree leaf chain.
    /// Use <see cref="CurrentComponent"/> for zero-copy ref access into page memory, or <see cref="Current"/> for a convenience copy.
    /// Supports both unique and AllowMultiple indexes (including PK).
    /// </summary>
    [PublicAPI]
    public ref struct IndexEntityEnumerator<T, TKey> where T : unmanaged where TKey : unmanaged
    {
        // Unique index path
        private BTree<TKey, PersistentStore>.RangeEnumerator _innerUnique;
        // AllowMultiple index path
        private BTree<TKey, PersistentStore>.RangeMultipleEnumerator _innerMultiple;
        private ReadOnlySpan<int> _currentValues;
        private int _currentValueIndex;

        private ChunkAccessor<PersistentStore> _compRevAccessor;
        private ChunkAccessor<PersistentStore> _compContentAccessor;
        private readonly Transaction _tx;
        private readonly long _transactionTSN;
        private readonly int _componentOverhead;
        private readonly bool _isAllowMultiple;
        private int _entityCount;
        private long _currentPK;
        private TKey _currentKey;
        private ReadOnlySpan<byte> _currentComponentSpan;
        private bool _disposed;

        internal IndexEntityEnumerator(BTree<TKey, PersistentStore> index, ComponentTable ct, Transaction tx, TKey minKey, TKey maxKey, ChangeSet changeSet)
        {
            _isAllowMultiple = index.AllowMultiple;
            if (_isAllowMultiple)
            {
                _innerMultiple = index.EnumerateRangeMultiple(minKey, maxKey);
                _innerUnique = default;
            }
            else
            {
                _innerUnique = index.EnumerateRange(minKey, maxKey);
                _innerMultiple = default;
            }

            _compRevAccessor = ct.CompRevTableSegment.CreateChunkAccessor(changeSet);
            _compContentAccessor = ct.ComponentSegment.CreateChunkAccessor(changeSet);
            _tx = tx;
            _transactionTSN = tx.TSN;
            _componentOverhead = ct.ComponentOverhead;
            _entityCount = 0;
            _currentPK = 0;
            _currentKey = default;
            _currentComponentSpan = default;
            _currentValues = default;
            _currentValueIndex = 0;
            _disposed = false;
        }

        public IndexEntityEnumerator<T, TKey> GetEnumerator() => this;

        /// <summary>Convenience accessor — copies the component into a tuple. Prefer <see cref="CurrentComponent"/> for zero-copy.</summary>
        public (long EntityPK, TKey Key, T Component) Current => (_currentPK, _currentKey, MemoryMarshal.AsRef<T>(_currentComponentSpan));

        /// <summary>Zero-copy ref into the epoch-protected page memory. Valid until the next <see cref="MoveNext"/> call.</summary>
        public ref readonly T CurrentComponent => ref MemoryMarshal.AsRef<T>(_currentComponentSpan);

        /// <summary>The primary key of the current entity.</summary>
        public long CurrentEntityPK => _currentPK;

        /// <summary>The index key of the current entry.</summary>
        public TKey CurrentKey => _currentKey;

        public bool MoveNext()
        {
            if (_isAllowMultiple)
            {
                return MoveNextMultiple();
            }

            return MoveNextUnique();
        }

        private bool MoveNextUnique()
        {
            while (_innerUnique.MoveNext())
            {
                var kv = _innerUnique.Current;
                int compRevFirstChunkId = kv.Value;

                // MVCC visibility check
                var result = RevisionChainReader.WalkChain(ref _compRevAccessor, compRevFirstChunkId, _transactionTSN);
                if (result.IsFailure || result.Value.CurCompContentChunkId == 0)
                {
                    continue;
                }

                // Recover EntityPK from CompRevStorageHeader
                _currentPK = _compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).EntityPK;
                _currentKey = kv.Key;

                // Store span into page memory — no copy
                var src = _compContentAccessor.GetChunkAsReadOnlySpan(result.Value.CurCompContentChunkId);
                _currentComponentSpan = src.Slice(_componentOverhead);

                if (++_entityCount % EpochRefreshInterval == 0)
                {
                    _tx.EnumerateRefreshEpoch();
                }

                return true;
            }

            return false;
        }

        private bool MoveNextMultiple()
        {
            while (true)
            {
                // Try to consume remaining values from current key's VSBS buffer
                while (_currentValueIndex < _currentValues.Length)
                {
                    int compRevFirstChunkId = _currentValues[_currentValueIndex++];

                    var result = RevisionChainReader.WalkChain(ref _compRevAccessor, compRevFirstChunkId, _transactionTSN);
                    if (result.IsFailure || result.Value.CurCompContentChunkId == 0)
                    {
                        continue;
                    }

                    _currentPK = _compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).EntityPK;

                    // Store span into page memory — no copy
                    var src = _compContentAccessor.GetChunkAsReadOnlySpan(result.Value.CurCompContentChunkId);
                    _currentComponentSpan = src.Slice(_componentOverhead);

                    if (++_entityCount % EpochRefreshInterval == 0)
                    {
                        _tx.EnumerateRefreshEpoch();
                    }

                    return true;
                }

                // Try next chunk of the same key's VSBS buffer (guard: _currentValues.Length > 0 prevents calling NextChunk before the first MoveNextKey,
                // when no VSBS buffer is open yet)
                if (_currentValues.Length > 0 && _innerMultiple.NextChunk())
                {
                    _currentValues = _innerMultiple.CurrentValues;
                    _currentValueIndex = 0;
                    continue;
                }

                // Advance to the next key
                if (!_innerMultiple.MoveNextKey())
                {
                    return false;
                }

                _currentKey = _innerMultiple.CurrentKey;
                _currentValues = _innerMultiple.CurrentValues;
                _currentValueIndex = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_isAllowMultiple)
            {
                _innerMultiple.Dispose();
            }
            else
            {
                _innerUnique.Dispose();
            }

            _compRevAccessor.Dispose();
            _compContentAccessor.Dispose();
        }
    }

    internal ref CompRevStorageHeader GetCompRevStorageHeader<T>(long entity)
    {
        var ci = GetComponentInfo(typeof(T));
        var result = GetCompRevTableFirstChunkId(entity, ci);
        if (result.IsFailure)
        {
            return ref Unsafe.NullRef<CompRevStorageHeader>();
        }

        return ref ci.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(result.Value);
    }

    internal int GetRevisionCount<T>(long entity)
    {
        ref var header = ref GetCompRevStorageHeader<T>(entity);
        if (Unsafe.IsNullRef(ref header))
        {
            return -1;
        }

        return header.ItemCount;
    }

    private ComponentInfo GetComponentInfo(Type componentType)
    {
        if (_componentInfos.TryGetValue(componentType, out var info))
        {
            return info;
        }

        var ct = _dbe.GetComponentTable(componentType) ?? 
                 throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");

        var isMultiple = ct.Definition.AllowMultiple;
        info = new ComponentInfo(isMultiple)
        {
            ComponentTable       = ct,
            CompContentSegment   = ct.ComponentSegment,
            CompRevTableSegment  = ct.CompRevTableSegment,
            PrimaryKeyIndex      = ct.PrimaryKeyIndex,
            CompContentAccessor  = ct.ComponentSegment.CreateChunkAccessor(_changeSet),
            CompRevTableAccessor = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet),
            SingleCache          = isMultiple ? null : new Dictionary<long, ComponentInfo.CompRevInfo>(),
            MultipleCache        = isMultiple ? new Dictionary<long, List<ComponentInfo.CompRevInfo>>() : null,
        };

        _componentInfos.Add(componentType, info);
        return info;
    }

    /// <summary>
    /// Flush all pending dirty state and advance the epoch within this transaction.
    /// Must only be called at a quiescent point — no B+Tree OLC write locks held,
    /// no ChunkAccessor<PersistentStore> mid-operation.
    /// </summary>
    private void FlushAndRefreshEpoch()
    {
        // 1. Flush dirty state on all live per-transaction ChunkAccessors
        foreach (var ci in _componentInfos.Values)
        {
            ci.CompContentAccessor.CommitChanges();
            ci.CompRevTableAccessor.CommitChanges();
        }

        // 2. Cap DirtyCounter at 1 for all tracked pages, clear ChangeSet.
        //    After clearing, re-registration in MarkSlotDirty brings DC to exactly 2
        //    (ChangeSet.Add→DC=1, then EnsureDirtyAtLeast(2)→DC=2), so checkpoint needs at most 2 cycles.
        _changeSet?.ReleaseExcessDirtyMarks();

        // 3. Advance epoch atomically (no unpinned window)
        var newEpoch = _epochManager.RefreshScope();

        // 4. Keep warm accessor cache alive — update its epoch so RentWarmAccessor sees a match.
        //    Without this, the next RentWarmAccessor creates a new accessor with empty slot cache,
        //    forcing all hot pages through RequestPageEpoch which re-stamps their AccessEpoch,
        //    making them permanently epoch-protected and causing unbounded cache pressure.
        ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(newEpoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckEpochRefresh()
    {
        if (++_entityOperationCount >= EpochRefreshInterval)
        {
            FlushAndRefreshEpoch();
            _entityOperationCount = 0;
        }
    }

    /// <summary>
    /// Epoch refresh for bulk enumerators. Read-only transactions just refresh the epoch scope (no dirty state to flush).
    /// Read-write transactions delegate to <see cref="FlushAndRefreshEpoch"/> which also flushes ChunkAccessor<PersistentStore> dirty state.
    /// </summary>
    internal void EnumerateRefreshEpoch()
    {
        if (IsReadOnly)
        {
            _epochManager.RefreshScope();
            return;
        }

        FlushAndRefreshEpoch();
    }

    private void CreateComponent<T>(long pk, ref T comp) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);

        // Fetch the cached info or create it if it's the first time we've operated on this Component type
        _dbe.LogCommitCreateComponent(TSN, componentType.Name, pk, "GetComponentInfo");
        var info = GetComponentInfo(componentType);

        // Allocate the chunk that will store the component's chunk
        _dbe.LogCommitCreateComponent(TSN, componentType.Name, pk, "AllocateChunk");
        var componentChunkId = info.CompContentSegment.AllocateChunk(false, _changeSet);

        // Allocate the component revision storage as it's a new component
        _dbe.LogCommitCreateComponent(TSN, componentType.Name, pk, "AllocCompRevStorage");
        var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, UowId, componentChunkId, pk);

        var entry = new ComponentInfo.CompRevInfo
        {
            Operations = ComponentInfo.OperationType.Created,
            PrevCompContentChunkId = 0,
            PrevRevisionIndex = -1,
            CurCompContentChunkId = componentChunkId,
            CompRevTableFirstChunkId = compRevChunkId,
            CurRevisionIndex = 0,
            ReadCommitSequence = 1,
            ReadRevisionIndex = 0
        };

        info.AddNew(pk, entry);

        // Copy the component data
        _dbe.LogCommitCreateComponent(TSN, componentType.Name, pk, "GetChunkAsSpan");
        int compSize = info.ComponentTable.ComponentStorageSize;
        var dst = info.CompContentAccessor.GetChunkAsSpan(componentChunkId, true);
        new Span<byte>(Unsafe.AsPointer(ref comp), compSize).CopyTo(dst.Slice(info.ComponentTable.ComponentOverhead));

        CheckEpochRefresh();
    }

    private void CreateComponents<T>(long pk, ReadOnlySpan<T> compList) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);

        // Fetch the cached info or create it if it's the first time we've operated on this Component type
        var info = GetComponentInfo(componentType);

        for (int i = 0; i < compList.Length; i++)
        {
            // Allocate the chunk that will store the component's chunk
            var componentChunkId = info.CompContentSegment.AllocateChunk(false, _changeSet);

            // Allocate the component revision storage as it's a new component
            var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, UowId, componentChunkId, pk);

            var entry = new ComponentInfo.CompRevInfo
            {
                Operations = ComponentInfo.OperationType.Created,
                PrevCompContentChunkId = 0,
                PrevRevisionIndex = -1,
                CurCompContentChunkId = componentChunkId,
                CompRevTableFirstChunkId = compRevChunkId,
                CurRevisionIndex = 0,
                ReadCommitSequence = 1,
                ReadRevisionIndex = 0
            };

            info.AddNew(pk, entry);

            // Copy the component data
            var dst = info.CompContentAccessor.GetChunkAsSpan(componentChunkId, true);
            compList.Slice(i, 1).Cast<T, byte>().CopyTo(dst.Slice(info.ComponentTable.ComponentOverhead));
        }

        CheckEpochRefresh();
    }

    private bool ReadComponent<T>(long pk, out T t) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var info = GetComponentInfo(componentType);

        // Check if we already have this component in the cache
        ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var exists);
        if (!exists)
        {
            // Couldn't find in the cache, get it from the index
            var result = GetCompRevInfoFromIndex(pk, info, TSN);
            if (result.IsFailure)
            {
                // NotFound, SnapshotInvisible, or Deleted — all mean no readable component. Remove the default entry that GetValueRefOrAddDefault added to
                // avoid leaving a zombie CompRevInfo (all zeros) that would corrupt subsequent operations on the same PK within this transaction.
                info.SingleCache.Remove(pk);
                t = default;
                return false;
            }
            compRevInfo = result.Value;
            compRevInfo.Operations |= ComponentInfo.OperationType.Read;
        }

        // Deleted component ?
        if (compRevInfo.CurCompContentChunkId == 0)
        {
            t = default;
            return false;
        }

        // If there is a valid component, copy its content to the destination.
        // No shared lock needed: deferred chunk freeing guarantees content chunks remain valid for the transaction's lifetime.
        t = default;
        int size = info.ComponentTable.ComponentStorageSize;
        var src = info.CompContentAccessor.GetChunkAsReadOnlySpan(compRevInfo.CurCompContentChunkId);
        src.Slice(info.ComponentTable.ComponentOverhead).CopyTo(new Span<byte>(Unsafe.AsPointer(ref t), size));

        return true;
    }

    private bool ReadComponents<T>(long pk, out T[] t) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var info = GetComponentInfo(componentType);

        // Check if we already have this component in the cache
        if (!info.MultipleCache.TryGetValue(pk, out var compRevInfoList))
        {
            // Couldn't find in the cache, get it from the index
            if (!GetCompRevInfoFromIndex(pk, info, TSN, out compRevInfoList))
            {
                t = null;
                return false;
            }

            // Add to cache for future operations (revision tracking, updates, etc.)
            info.MultipleCache[pk] = compRevInfoList;
        }

        var compRevInfoSpan = CollectionsMarshal.AsSpan(compRevInfoList);

        t = new T[compRevInfoSpan.Length];
        var deletedCount = 0;
        var destSpan = t.AsSpan();
        int destIndex = 0;
        for (int i = 0; i < compRevInfoSpan.Length; i++)
        {
            ref var compRevInfo = ref compRevInfoSpan[i];
            compRevInfo.Operations |= ComponentInfo.OperationType.Read;

            // Skip deleted components
            if (compRevInfo.CurCompContentChunkId == 0)
            {
                ++deletedCount;
                continue;
            }

            // If there is a valid component, copy its content to the destination
            var chunkSpan = info.CompContentAccessor.GetChunkAsReadOnlySpan(compRevInfo.CurCompContentChunkId);
            chunkSpan.Slice(info.ComponentTable.ComponentOverhead).Cast<byte, T>().CopyTo(destSpan.Slice(destIndex++));
        }

        // Deleted items were skipped, we need to trim the list...
        if (deletedCount > 0)
        {
            // ... or remove it if everything was deleted
            if (deletedCount == t.Length)
            {
                t = null;
                return false;
            }
            Array.Resize(ref t, t.Length - deletedCount);
        }

        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DeleteComponent<T>(long pk) where T : unmanaged
    {
        if (_dbe.GetComponentTable(typeof(T)).Definition.AllowMultiple)
        {
            return UpdateComponents(pk, ReadOnlySpan<T>.Empty);
        }
        return UpdateComponent(pk, ref Unsafe.NullRef<T>());
    }

    private bool UpdateComponent<T>(long pk, ref T comp) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var isDelete = Unsafe.IsNullRef(ref comp);
        
        // Fetch the cached info or create it if it's the first time we operate on this Component type
        var info = GetComponentInfo(componentType);

        // Check if the component is in the cache (meaning we already made an operation on it in this transaction)
        ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var compRevCached);
        if (compRevCached)
        {
            // Can't update a deleted component...
            if ((compRevInfo.Operations & ComponentInfo.OperationType.Deleted) == ComponentInfo.OperationType.Deleted)
            {
                return false;
            }

            // Check if we need to delete a component we previously added
            if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
            {
                info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
                compRevInfo.CurCompContentChunkId = 0;
            }
        }

        // No component in cache
        else
        {
            // Fetch the cache by getting the revision closest to the transaction tick, if we fail it means there's no revision, so no component for this
            //  PK, we return false
            var result = GetCompRevInfoFromIndex(pk, info, TSN);
            if (result.Status == RevisionReadStatus.NotFound || result.Status == RevisionReadStatus.SnapshotInvisible)
            {
                // Remove the default entry that GetValueRefOrAddDefault added to avoid leaving
                // a zombie CompRevInfo (all zeros) that would corrupt subsequent operations on
                // the same PK within this transaction.
                info.SingleCache.Remove(pk);
                return false;
            }
            compRevInfo = result.Value; // Works for both Success AND Deleted (3-arg constructor)
        }

        // Update the operation types
        compRevInfo.Operations |= (isDelete ? ComponentInfo.OperationType.Deleted : ComponentInfo.OperationType.Updated);

        // First mutating operation on this component in this transaction: create a new component version
        if ((!compRevCached) || ((compRevInfo.Operations & ComponentInfo.OperationType.Read) != 0))
        {
            // Add a new component version for the current component, if there is no data, it means we are deleting the component, we still
            //  need to add a new version with an empty CurCompContentChunkId
            ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, UowId, isDelete);
        }

        // Set up the component header
        if (!isDelete)
        {
            // Copy the component data
            int componentSize = info.ComponentTable.ComponentStorageSize;
            var src = new Span<byte>(Unsafe.AsPointer(ref comp), componentSize);
            var dst = info.CompContentAccessor.GetChunkAsSpan(compRevInfo.CurCompContentChunkId, true).Slice(info.ComponentTable.ComponentOverhead);
            src.CopyTo(dst);

            // If the component has collections, update the RefCounter of unchanged ones
            var ct = info.ComponentTable;
            if (ct.HasCollections)
            {
                foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                {
                    var offsetToCollectionField = kvp.Key;
                    var srcBufferId = src.Slice(offsetToCollectionField).Cast<byte, int>()[0];
                    var dstBufferId = dst.Slice(offsetToCollectionField).Cast<byte, int>()[0];
                    if (srcBufferId == dstBufferId)
                    {
                        var accessor = kvp.Value.Segment.CreateChunkAccessor(_changeSet);
                        kvp.Value.BufferAddRef(srcBufferId, ref accessor);
                        accessor.Dispose();
                    }
                }
            }
        }

        CheckEpochRefresh();
        return true;
    }

    private bool UpdateComponents<T>(long pk, ReadOnlySpan<T> compList) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var isDelete = compList.Length == 0;

        // Fetch the cached info or create it if it's the first time we operate on this Component type
        var info = GetComponentInfo(componentType);

        // Check if the component is in the cache (meaning we already made an operation on it in this transaction)
        var compRevCached = info.MultipleCache.TryGetValue(pk, out var compRevInfoList);
        if (!compRevCached)
        {
            // Fetch the cache by getting the revision closest to the transaction tick, if we fail it means there's no revision, so no component for this
            //  PK, we return false
            if (!GetCompRevInfoFromIndex(pk, info, TSN, out compRevInfoList))
            {
                return false;
            }

            // Add to cache so the updates are tracked and committed
            info.MultipleCache[pk] = compRevInfoList;
        }

        // x source items, y destination items, three cases:
        // 1. x == y easy
        // 2. x < y, update the x items to destination, remove the excess from destination (y - x)
        // 3. x > y, update y items from source to destination, add the excess to destination (x - y)
        var compRevInfoSpan = CollectionsMarshal.AsSpan(compRevInfoList);
        var overlapCount = Math.Min(compList.Length, compRevInfoSpan.Length);
        var i = 0;

        // Case 1
        // min(x, y) the item count shared by source and dest
        for ( ; i < overlapCount; i++)
        {
            ref var compRevInfo = ref compRevInfoSpan[i];

            // Can't update a deleted component...
            if ((compRevInfo.Operations & ComponentInfo.OperationType.Deleted) == ComponentInfo.OperationType.Deleted)
            {
                return false;
            }

            // Check if we need to delete a component we previously added
            if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
            {
                info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
            }

            // Update the operation types
            compRevInfo.Operations |= (isDelete ? ComponentInfo.OperationType.Deleted : ComponentInfo.OperationType.Updated);

            // First mutating operation on this component in this transaction: create a new component version
            // Also create a new revision if the component was deleted (CurCompContentChunkId == 0) - to resurrect it
            if ((!compRevCached) || ((compRevInfo.Operations & ComponentInfo.OperationType.Read) != 0) || compRevInfo.CurCompContentChunkId == 0)
            {
                // Add a new component version for the current component, if there is no data, it means we are deleting the component, we still
                //  need to add a new version with an empty CurCompContentChunkId
                ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, UowId, isDelete);
            }

            // Set up the component header
            if (!isDelete)
            {
                // Copy the component data
                var dst = info.CompContentAccessor.GetChunkAsSpan(compRevInfo.CurCompContentChunkId, true).Slice(info.ComponentTable.ComponentOverhead);
                var src = compList.Slice(i, 1).Cast<T, byte>();
                src.CopyTo(dst);
            
                // If the component has collections, update the RefCounter of unchanged ones
                var ct = info.ComponentTable;
                if (ct.HasCollections)
                {
                    foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                    {
                        var offsetToCollectionField = kvp.Key;
                        var srcBufferId = src.Slice(offsetToCollectionField).Cast<byte, int>()[0];
                        var dstBufferId = dst.Slice(offsetToCollectionField).Cast<byte, int>()[0];
                        if (srcBufferId == dstBufferId)
                        {
                            var accessor = kvp.Value.Segment.CreateChunkAccessor(_changeSet);
                            kvp.Value.BufferAddRef(srcBufferId, ref accessor);
                            accessor.Dispose();
                        }
                    }
                }
            }
        }

        // Case 2: Mark excess items as deleted
        if (compList.Length < compRevInfoSpan.Length)
        {
            for (int j = i; j < compRevInfoSpan.Length; j++)
            {
                ref var compRevInfo = ref compRevInfoSpan[j];
                compRevInfo.Operations |= ComponentInfo.OperationType.Deleted;
                ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, UowId, true);
            }
        }
        
        // Case 3
        else if (compList.Length > compRevInfoSpan.Length)
        {
            CreateComponents(pk, compList.Slice(compRevInfoSpan.Length));
        }

        CheckEpochRefresh();
        return true;
    }

    private Result<int, BTreeLookupStatus> GetCompRevTableFirstChunkId(long pk, ComponentInfo info)
    {
        var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
        var result = info.PrimaryKeyIndex.TryGet(pk, ref accessor);
        accessor.Dispose();
        return result;
    }

    private Result<ComponentInfo.CompRevInfo, RevisionReadStatus> GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick)
    {
        var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
        var lookupResult = info.PrimaryKeyIndex.TryGet(pk, ref accessor);
        accessor.Dispose();
        if (lookupResult.IsFailure)
        {
            return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(RevisionReadStatus.NotFound);
        }

        return RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, lookupResult.Value, TSN);
    }

    private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out List<ComponentInfo.CompRevInfo> compRevInfoList)
    {
        var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
        using var vsba = info.PrimaryKeyIndex.TryGetMultiple(pk, ref accessor);
        if (!vsba.IsValid)
        {
            accessor.Dispose();
            compRevInfoList = null;
            return false;
        }
        accessor.Dispose();

        compRevInfoList = new List<ComponentInfo.CompRevInfo>(vsba.TotalCount);
        do
        {
            var compRevChunks = vsba.Elements;
            foreach (int compRevFirstChunkId in compRevChunks)
            {
                var result = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN);

                // WalkChain returns SnapshotInvisible when no committed entry is visible — skip this chain.
                // Both Success and Deleted carry valid revision metadata that callers need.
                if (result.Status != RevisionReadStatus.SnapshotInvisible)
                {
                    compRevInfoList.Add(result.Value);
                }
            }
        } while (vsba.NextChunk());

        return true;
    }

    /// <summary>
    /// Create a WaitContext that respects both the UoW deadline and a subsystem-specific timeout.
    /// The tighter deadline wins.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WaitContext ComposeWaitContext(ref UnitOfWorkContext ctx, TimeSpan subsystemTimeout)
        => WaitContext.FromDeadline(Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));

    /// <summary>
    /// Rolls back a single component revision: frees content chunks, voids revision entries, and enqueues for deferred cleanup.
    /// </summary>
    /// <returns><c>true</c> if the component was created (rev table chunk freed — caller must remove from cache); <c>false</c> otherwise.</returns>
    private bool RollbackComponent(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;
        ref var compRevInfo = ref context.CompRevInfo;

        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var componentSegment = info.CompContentSegment;
        var revTableSegment = info.CompRevTableSegment;

        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;

        // Get the chunk storing the revision we want to roll back as well as the index of the element
        var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId);
        var elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Validate CurRevisionIndex — chain compaction by another transaction's cleanup may have
        // shifted entry positions since our read. Best-effort (no lock).
        if (elementHandle.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
        {
            var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
            if (fixedIndex >= 0)
            {
                compRevInfo.CurRevisionIndex = fixedIndex;
                elementHandle = compRev.GetRevisionElement(fixedIndex);
            }
        }

        // Free the chunk storing the content (if any)
        if (compRevInfo.CurCompContentChunkId != 0)
        {
            componentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
        }

        // If we roll back a created component, we must delete the revision table chunk
        if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
        {
            revTableSegment.FreeChunk(firstChunkId);

            // Early exit: the RevTable chunk is gone — continuing would access freed memory.
            return true;
        }

        // In case of update or delete, mark void the revision entry we added
        if ((compRevInfo.Operations & (ComponentInfo.OperationType.Updated | ComponentInfo.OperationType.Deleted)) != 0)
        {
            compRev.VoidElement(elementHandle);
        }

        // Enqueue for deferred cleanup
        _deferredEnqueueBatch ??= new List<DeferredCleanupManager.CleanupEntry>(16);
        _deferredEnqueueBatch.Add(new DeferredCleanupManager.CleanupEntry { Table = info.ComponentTable, PrimaryKey = pk, FirstChunkId = firstChunkId });

        if (_deferredEnqueueBatch.Count >= DeferredEnqueueBatchCapacity)
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // Clear stale revision tracking
        compRevInfo.PrevCompContentChunkId = -1;
        compRevInfo.PrevRevisionIndex = 0;

        return false;
    }

    /// <summary>
    /// Detects write-write conflicts and resolves them via handler invocation or "last wins" semantics.
    /// Must be called inside the per-entity revision chain lock (when handler is provided) or outside (last-wins path).
    /// </summary>
    /// <remarks>
    /// Two complementary checks for the handler path:
    /// <list type="number">
    /// <item>CommitSequence: detects any commit since our read (monotonic counter, immune to revision index ordering and cleanup compaction).</item>
    /// <item>Invisible-commit TSN check: detects committed entries by higher-TSN transactions invisible to our snapshot.</item>
    /// </list>
    /// Without handler: uses the original index-based check as best-effort for "last wins".
    /// </remarks>
    private static void DetectAndResolveConflict(ref CommitContext context, long pk, ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, 
        ComponentRevision compRev, ref ComponentRevisionManager.ElementRevisionHandle elementHandle, short lastCommitRevisionIndex, int readCompChunkId, 
        bool lockHeld, long tsn, ushort uowId, DatabaseEngine dbe)
    {
        var conflictSolver = context.Solver;

        var hasConflict = (conflictSolver != null) ? 
            compRev.CommitSequence != compRevInfo.ReadCommitSequence || 
            (lastCommitRevisionIndex >= 0 && compRev.GetRevisionElement(lastCommitRevisionIndex).Element.TSN > tsn) : 
            lastCommitRevisionIndex >= compRevInfo.CurRevisionIndex;

        if (!hasConflict)
        {
            return;
        }

        // Record conflict for observability
        dbe?.RecordConflict();

        // Save the orphan index before AddCompRev changes CurRevisionIndex
        var conflictOrphanIndex = compRevInfo.CurRevisionIndex;

        // Create a new revision for the resolved data (under existing lock when handler is provided)
        ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, uowId, false, lockAlreadyHeld: lockHeld);

        // Copy the dirty-write data to the new revision as starting point
        var dstChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
        var srcChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId);
        var sizeToCopy = info.ComponentTable.ComponentTotalSize;
        new Span<byte>(srcChunk, sizeToCopy).CopyTo(new Span<byte>(dstChunk, sizeToCopy));

        // Update elementHandle to point to the new revision
        elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Invoke the handler to resolve the conflict (under lock, so committedData is guaranteed fresh)
        if (conflictSolver != null)
        {
            var lastCommitHandle = compRev.GetRevisionElement(lastCommitRevisionIndex);
            var overhead = info.ComponentTable.ComponentOverhead;
            var readChunkAddr = info.CompContentAccessor.GetChunkAddress(readCompChunkId) + overhead;
            var committingChunkAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId) + overhead;
            var toCommitChunkAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead;
            var committedChunkAddr = info.CompContentAccessor.GetChunkAddress(lastCommitHandle.Element.ComponentChunkId) + overhead;

            conflictSolver.Setup(pk, info, readChunkAddr, committedChunkAddr, committingChunkAddr, toCommitChunkAddr);
            context.Handler(ref conflictSolver);
        }

        // Void the orphaned entry at the old position and free its content chunk
        var conflictOrphan = compRev.GetRevisionElement(conflictOrphanIndex);
        if (conflictOrphan.Element.ComponentChunkId > 0)
        {
            info.CompContentSegment.FreeChunk(conflictOrphan.Element.ComponentChunkId);
        }
        conflictOrphan.Element.Void();
    }

    /// <summary>
    /// Relocates a revision entry to the end of the chain when our entry is at or behind LCRI.
    /// This happens when a later-created transaction committed at a higher index — without relocation,
    /// the chain walk would shadow our value with the older commit at the higher index.
    /// </summary>
    /// <remarks>Must be called under the per-entity revision chain exclusive lock.</remarks>
    private static ComponentRevisionManager.ElementRevisionHandle RelocateRevisionEntry(
        ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, ComponentRevision compRev, long tsn, ushort uowId)
    {
        // Save the chunk that holds our modified data
        var oldContentChunkId = compRevInfo.CurCompContentChunkId;

        // Create new entry at end of chain (under existing lock)
        ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, uowId, false, lockAlreadyHeld: true);

        // Copy our data to the new content chunk
        var srcAddr = info.CompContentAccessor.GetChunkAddress(oldContentChunkId);
        var dstAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
        new Span<byte>(srcAddr, info.ComponentTable.ComponentTotalSize)
            .CopyTo(new Span<byte>(dstAddr, info.ComponentTable.ComponentTotalSize));

        // Free the old content chunk and void the orphaned entry. The old entry at PrevRevisionIndex retains
        // IsolationFlag=true which blocks deferred cleanup compaction. Voiding it makes it a harmless placeholder.
        info.CompContentSegment.FreeChunk(oldContentChunkId);
        compRev.GetRevisionElement(compRevInfo.PrevRevisionIndex).Element.Void();

        return compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
    }

    /// <summary>Action struct for commit iteration: delegates to <see cref="CommitComponentCore"/>.</summary>
    private struct CommitAction : IEntryAction
    {
        public Transaction Tx;
        public void Process(ref CommitContext ctx) => Tx.CommitComponentCore(ref ctx);
    }

    /// <summary>Action struct for rollback iteration: delegates to <see cref="RollbackComponent"/>.</summary>
    private struct RollbackAction : IEntryAction
    {
        public Transaction Tx;
        public void Process(ref CommitContext ctx) => Tx.RollbackComponent(ref ctx);
    }

    /// <summary>
    /// Commits a single component revision: acquires revision chain lock (when handler provided), detects/resolves conflicts,
    /// updates indices, clears IsolationFlag, and updates LCRI.
    /// </summary>
    private void CommitComponentCore(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;
        ref var compRevInfo = ref context.CompRevInfo;
        var conflictSolver = context.Solver;
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;

        var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId);
        var lastCommitRevisionIndex = compRev.LastCommitRevisionIndex;
        var elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Validate CurRevisionIndex — chain compaction may have shifted entry positions since our read
        if (elementHandle.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
        {
            var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
            if (fixedIndex >= 0)
            {
                compRevInfo.CurRevisionIndex = fixedIndex;
                elementHandle = compRev.GetRevisionElement(fixedIndex);
            }
        }

        // Capture readCompChunkId before AddCompRev shifts PrevCompContentChunkId
        var readCompChunkId = compRevInfo.PrevCompContentChunkId;

        // When a handler is provided, hold the per-entity revision chain lock during detect-resolve-commit to prevent TOCTOU races
        var lockHeld = false;
        if (conflictSolver != null)
        {
            ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
            var wcCommit = ComposeWaitContext(ref context.Ctx, TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!lockHeader.Control.EnterExclusiveAccess(ref wcCommit))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/CommitConflict", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
            lockHeld = true;
            lastCommitRevisionIndex = lockHeader.LastCommitRevisionIndex;

            // Re-validate CurRevisionIndex under lock (race-free)
            var curElement = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
            if (curElement.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
            {
                var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(
                    ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
                if (fixedIndex < 0)
                {
                    ThrowHelper.ThrowInvalidOp("CommitComponentCore: revision entry lost after chain compaction");
                }
                compRevInfo.CurRevisionIndex = fixedIndex;
                elementHandle = compRev.GetRevisionElement(fixedIndex);
            }

            // Relocate if our entry is at or behind LCRI
            if (lastCommitRevisionIndex >= 0 && compRevInfo.CurRevisionIndex <= lastCommitRevisionIndex)
            {
                elementHandle = RelocateRevisionEntry(info, ref compRevInfo, compRev, TSN, UowId);
            }
        }

        try
        {
            DetectAndResolveConflict(ref context, pk, info, ref compRevInfo, compRev,
                ref elementHandle, lastCommitRevisionIndex, readCompChunkId, lockHeld, TSN, UowId, _dbe);

            // Commit the revision: update indices, clear IsolationFlag, update LastCommitRevisionIndex
            if (compRevInfo.CurCompContentChunkId != 0)
            {
                if (_batchIndexActive)
                {
                    IndexMaintainer.UpdateIndices(pk, info, compRevInfo, readCompChunkId, _changeSet, TSN, _batchIndexAccessors,
                        ref _batchPkAccessor, ref _batchTailAccessor);
                }
                else
                {
                    IndexMaintainer.UpdateIndices(pk, info, compRevInfo, readCompChunkId, _changeSet, TSN);
                }
            }
            else if (readCompChunkId != 0)
            {
                if (_batchIndexActive)
                {
                    IndexMaintainer.RemoveSecondaryIndices(pk, info, readCompChunkId, compRevInfo.CompRevTableFirstChunkId, _changeSet, TSN,
                        _batchIndexAccessors, ref _batchTailAccessor);
                }
                else
                {
                    IndexMaintainer.RemoveSecondaryIndices(pk, info, readCompChunkId, compRevInfo.CompRevTableFirstChunkId, _changeSet, TSN);
                }
            }

            // Periodic flush: bound dirty counter inflation for large transactions
            if (_batchIndexActive && (++_batchEntityCount & 0x3FF) == 0)
            {
                for (int i = 0; i < _batchIndexAccessors.Length; i++)
                {
                    _batchIndexAccessors[i].CommitChanges();
                }
                _batchPkAccessor.CommitChanges();
                if (info.ComponentTable.TailVSBS != null)
                {
                    _batchTailAccessor.CommitChanges();
                }

                // Flush warm accessors: exit+enter cycle performs a single CommitChanges per cache
                ChunkBasedSegment<PersistentStore>.ExitBatchMode();
                ChunkBasedSegment<PersistentStore>.EnterBatchMode();

                _changeSet.ReleaseExcessDirtyMarks();
            }

            elementHandle.Commit(TSN);
            compRev.SetLastCommitRevisionIndex(Math.Max(lastCommitRevisionIndex, compRevInfo.CurRevisionIndex));
            if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == 0)
            {
                compRev.IncrementCommitSequence();
            }
        }
        finally
        {
            if (lockHeld)
            {
                ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                lockHeader.Control.ExitExclusiveAccess();
            }
        }

        // Enqueue for deferred cleanup
        _deferredEnqueueBatch ??= new List<DeferredCleanupManager.CleanupEntry>(16);
        _deferredEnqueueBatch.Add(new DeferredCleanupManager.CleanupEntry { Table = info.ComponentTable, PrimaryKey = pk, FirstChunkId = firstChunkId });
        if (_deferredEnqueueBatch.Count >= DeferredEnqueueBatchCapacity)
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        compRevInfo.PrevCompContentChunkId = -1;
        compRevInfo.PrevRevisionIndex = 0;
    }

    public bool Rollback(ref UnitOfWorkContext ctx)
    {
        AssertThreadAffinity();

        // Nothing to do if the transaction is empty
        if (State is TransactionState.Created)
        {
            return true;
        }

        // Can't roll back a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed)
        {
            return false;
        }

        // No yield point — rollback/cleanup must always complete
        using var holdoff = ctx.EnterHoldoff();

        using var activity = TyphonActivitySource.StartActivity("Transaction.Rollback");
        activity?.SetTag(TyphonSpanAttributes.TransactionTsn, TSN);
        activity?.SetTag(TyphonSpanAttributes.TransactionComponentCount, _componentInfos.Count);

        var context = new CommitContext();
#pragma warning disable CS9093 // ref-assign is safe: CommitContext is a ref struct that never escapes this method
        context.Ctx = ref ctx;
#pragma warning restore CS9093

        // Determine tail status once for the entire rollback — saves N-1 shared lock acquires on TransactionChain
        var wcTail = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wcTail))
        {
            ThrowHelper.ThrowLockTimeout("TransactionChain/RollbackTailCheck", TimeoutOptions.Current.TransactionChainLockTimeout);
        }
        context.IsTail = _dbe.TransactionChain.Tail == this;
        context.NextMinTSN = context.IsTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
        context.TailTSN = _dbe.TransactionChain.MinTSN;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        var rollbackAction = new RollbackAction { Tx = this };
        // Hoisted outside loop to avoid per-iteration stackalloc accumulation (CA2014)
        Span<long> createdPkBuffer = stackalloc long[128];
        // Process every Component Type and their components
        foreach (var componentInfo in _componentInfos.Values)
        {
            context.Info = componentInfo;

            componentInfo.ForEachMutableEntry(ref context, ref rollbackAction);

            // Remove rolled-back Created entities from Single cache.
            // Can't modify dictionary during ForEachMutableEntry, so do a second pass.
            if (!componentInfo.IsMultiple)
            {
                var cacheCount = componentInfo.SingleCache.Count;
                Span<long> toRemove = cacheCount <= 128 ? createdPkBuffer[..cacheCount] : new long[cacheCount];
                var removeCount = 0;
                foreach (var kvp in componentInfo.SingleCache)
                {
                    if ((kvp.Value.Operations & ComponentInfo.OperationType.Created) != 0)
                    {
                        toRemove[removeCount++] = kvp.Key;
                    }
                }
                for (var i = 0; i < removeCount; i++)
                {
                    componentInfo.SingleCache.Remove(toRemove[i]);
                    _deletedComponentCount++;
                }
            }
        }

        // Flush batched deferred enqueue entries (non-tail path: single lock acquire for all entities)
        if (_deferredEnqueueBatch is { Count: > 0 })
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // New state
        TransitionTo(TransactionState.Rollbacked);
        activity?.SetTag(TyphonSpanAttributes.TransactionStatus, "rolledback");
        _dbe?.RecordRollback();
        return true;
    }

    public bool Rollback()
    {
        var ctx = UnitOfWorkContext.None;
        return Rollback(ref ctx);
    }

    /// <summary>Serialize to WAL (or flush pages for WAL-less), transition to Committed, and record metrics.</summary>
    private void PersistAndFinalize(ref UnitOfWorkContext ctx, long startTicks)
    {
        // WAL serialization (after conflict resolution, before state transition)
        long walHighLsn = 0;
        if (_dbe.WalManager != null && State != TransactionState.Created)
        {
            walHighLsn = WalSerializer.SerializeToWal(_componentInfos, _dbe.WalManager, TSN, UowId, ref ctx);
        }

        // Durability wait for Immediate mode
        if (walHighLsn > 0 && OwningUnitOfWork?.DurabilityMode == DurabilityMode.Immediate)
        {
            Debug.Assert(_dbe.WalManager != null);
            _dbe.WalManager.RequestFlush();
            var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.DefaultCommitTimeout);
            _dbe.WalManager.WaitForDurable(walHighLsn, ref wc);
        }

        // WAL-less Immediate: persist dirty data pages and fsync before returning from Commit.
        // This is the WAL-less equivalent of the WAL FUA path above — data is on stable storage when Commit returns.
        if (_dbe.WalManager == null && OwningUnitOfWork?.DurabilityMode == DurabilityMode.Immediate)
        {
            // Flush batched dirty flags from long-lived accessors to the ChangeSet (BTree accessors are already
            // disposed inline during CommitComponentCore, so their pages are already tracked).
            foreach (var kvp in _componentInfos)
            {
                kvp.Value.CompContentAccessor.CommitChanges();
                kvp.Value.CompRevTableAccessor.CommitChanges();
            }

            _changeSet.SaveChanges();
            _dbe.MMF.FlushToDisk();
        }

        // New state
        TransitionTo(TransactionState.Committed);

        // Record commit duration for observability
        var elapsedUs = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;
        _dbe?.RecordCommitDuration(elapsedUs);
    }

    public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
    {
        AssertThreadAffinity();

        // Read-only transactions have nothing to commit — trivially succeed
        if (IsReadOnly)
        {
            return true;
        }

        // Nothing to commit if the transaction is empty, but still process deferred cleanups
        // in case this transaction (as the tail) is blocking cleanup of entities modified by others.
        if (State is TransactionState.Created)
        {
            if (_dbe.DeferredCleanupManager.QueueSize > 0)
            {
                var wcDeferred = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
                if (_dbe.TransactionChain.Control.EnterSharedAccess(ref wcDeferred))
                {
                    var isTailForDeferred = _dbe.TransactionChain.Tail == this;
                    var nextMinTSNDeferred = isTailForDeferred ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
                    _dbe.TransactionChain.Control.ExitSharedAccess();

                    if (isTailForDeferred)
                    {
                        _dbe.DeferredCleanupManager.ProcessDeferredCleanups(TSN, nextMinTSNDeferred, _dbe, _changeSet);
                    }
                }
            }

            return true;
        }

        // Can't commit a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed)
        {
            return false;
        }

        // ── Yield point: safe to cancel before any modifications ──
        ctx.ThrowIfCancelled();

        using var activity = TyphonActivitySource.StartActivity("Transaction.Commit");
        activity?.SetTag(TyphonSpanAttributes.TransactionTsn, TSN);
        activity?.SetTag(TyphonSpanAttributes.TransactionComponentCount, _componentInfos.Count);

        var startTicks = Stopwatch.GetTimestamp();

        _dbe.LogCommitStart(TSN, _componentInfos.Count);

        // ── Holdoff: entire commit loop runs to completion ──
        using var holdoff = ctx.EnterHoldoff();

        var conflictSolver = handler != null ? ConcurrencyConflictSolver.GetConflictSolver() : null;
        var context = new CommitContext { Solver = conflictSolver, Handler = handler };
#pragma warning disable CS9093 // ref-assign is safe: CommitContext is a ref struct that never escapes this method
        context.Ctx = ref ctx;
#pragma warning restore CS9093
        var hasConflict = false;

        // Determine tail status once for the entire commit — saves N-1 shared lock acquires on TransactionChain
        var wcTail = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wcTail))
        {
            ThrowHelper.ThrowLockTimeout("TransactionChain/CommitTailCheck", TimeoutOptions.Current.TransactionChainLockTimeout);
        }
        context.IsTail = _dbe.TransactionChain.Tail == this;
        context.NextMinTSN = context.IsTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
        context.TailTSN = _dbe.TransactionChain.MinTSN;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        _dbe.LogCommitPhase(TSN, "CommitComponentCore");

        // Process every Component Type and their components
        var commitAction = new CommitAction { Tx = this };
        foreach (var kvp in _componentInfos)
        {
            context.Info = kvp.Value;
            var info = kvp.Value;
            _dbe.LogCommitComponentEntries(TSN, kvp.Key.Name, info.EntryCount);

            // Start a sub-span for this component type
            using var componentActivity = TyphonActivitySource.StartActivity("Transaction.CommitComponentCore");
            componentActivity?.SetTag(TyphonSpanAttributes.ComponentType, kvp.Key.Name);

            // Hoist accessor creation for batch index maintenance
            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            _batchIndexAccessors = new ChunkAccessor<PersistentStore>[indexedFieldInfos.Length];
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                _batchIndexAccessors[i] = indexedFieldInfos[i].Index.Segment.CreateChunkAccessor(_changeSet);
            }
            _batchPkAccessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
            var tailVSBS = info.ComponentTable.TailVSBS;
            _batchTailAccessor = tailVSBS != null
                ? tailVSBS.Segment.CreateChunkAccessor(_changeSet)
                : default;
            _batchIndexActive = true;
            _batchEntityCount = 0;
            ChunkBasedSegment<PersistentStore>.EnterBatchMode();

            try
            {
                kvp.Value.ForEachMutableEntry(ref context, ref commitAction);
            }
            finally
            {
                // Exit batch mode + dispose hoisted accessors
                ChunkBasedSegment<PersistentStore>.ExitBatchMode();
                _batchIndexActive = false;
                for (int i = 0; i < _batchIndexAccessors.Length; i++)
                {
                    _batchIndexAccessors[i].Dispose();
                }
                _batchPkAccessor.Dispose();
                if (tailVSBS != null)
                {
                    _batchTailAccessor.Dispose();
                }
                _batchIndexAccessors = null;
            }

            _dbe.LogCommitComponentDone(TSN, kvp.Key.Name);
        }

        _dbe.LogCommitPhase(TSN, "DeferredCleanup");

        // Enqueue current transaction's entities for deferred cleanup (single lock acquire for all entities).
        // Processing happens in Dispose (after cached indices are no longer relevant) or via FlushDeferredCleanups.
        if (_deferredEnqueueBatch is { Count: > 0 })
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // Flush ECS pending operations (spawns, destroys, enable/disable)
        _dbe.LogCommitPhase(TSN, "EcsFlush");
        FlushEcsPendingOperations();

        // Check if any conflicts were detected during the commit loop
        if (conflictSolver is { HasConflict: true })
        {
            hasConflict = true;
        }

        activity?.SetTag(TyphonSpanAttributes.TransactionConflictDetected, hasConflict);
        activity?.SetTag(TyphonSpanAttributes.TransactionStatus, "committed");

        _dbe.LogCommitPhase(TSN, "PersistAndFinalize");
        PersistAndFinalize(ref ctx, startTicks);
        _dbe.LogCommitPhase(TSN, "Complete");
        return true;
    }

    public bool Commit(ConcurrencyConflictHandler handler = null)
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
        return Commit(ref ctx, handler);
    }

}