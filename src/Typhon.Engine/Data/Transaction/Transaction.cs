// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
[DebuggerDisplay("TSN {TSN}, State: {State}")]
public unsafe class Transaction : IDisposable
{
    private const int RandomAccessCachedPagesCount = 8;
    private const int ComponentInfosMaxCapacity = 131;
    private const int DeferredEnqueueBatchCapacity = 256;

    internal abstract class ComponentInfoBase
    {
        [Flags]
        public enum OperationType
        {
            Undefined = 0,
            Created   = 1,
            Read      = 2,
            Updated   = 4,
            Deleted   = 8
        }

        public struct CompRevInfo
        {
            // Current operation type on the component for this transaction
            public OperationType Operations;

            /// ChunkId of the first CompRevTable chunk for the component (the entry point of the chain with the CompRevStorageHeader being used)
            public int CompRevTableFirstChunkId;

            /// The index in the revision table of the revision BEFORE being changed in this transaction. -1 if there's none.
            public short PrevRevisionIndex;

            /// The index in the revision table of the revision being used in this transaction.
            /// This is NOT relative to <see cref="CompRevStorageHeader.FirstItemIndex"/> but to the start of the chain (first element of the first chunk).
            public short CurRevisionIndex;
            
            /// The ChunkId storing the component content revision BEFORE the transaction (the previous one). 0 if there's none.
            public int PrevCompContentChunkId;

            /// The ChunkId storing the component content corresponding to the revision of this CompRevInfo instance
            public int CurCompContentChunkId;

            /// Monotonic commit counter captured at read time. Compared against header.CommitSequence during commit to detect any commits since our
            /// read — immune to revision index ordering and cleanup compaction that can fool TSN/LCRI-based detection.
            public int ReadCommitSequence;

        }

        public abstract bool IsMultiple { get; }
        public abstract int EntryCount { get; }
        public ComponentTable ComponentTable;
        public ChunkBasedSegment CompContentSegment;
        public ChunkBasedSegment CompRevTableSegment;
        public BTree<long> PrimaryKeyIndex;
        public ChunkAccessor CompContentAccessor;
        public ChunkAccessor CompRevTableAccessor;
        public abstract void AddNew(long pk, CompRevInfo entry);

        /// <summary>
        /// Disposes the ChunkAccessor fields to flush dirty pages.
        /// </summary>
        public void DisposeAccessors()
        {
            CompContentAccessor.Dispose();
            CompRevTableAccessor.Dispose();
        }
    }

    internal class ComponentInfoSingle : ComponentInfoBase
    {
        public override bool IsMultiple => false;
        public override int EntryCount => CompRevInfoCache.Count;

        public override void AddNew(long pk, CompRevInfo entry) => CompRevInfoCache.Add(pk, entry);

        public Dictionary<long, CompRevInfo> CompRevInfoCache;
    }

    internal class ComponentInfoMultiple : ComponentInfoBase
    {
        public override bool IsMultiple => true;
        public override int EntryCount => CompRevInfoCache.Count;

        public override void AddNew(long pk, CompRevInfo entry)
        {
            // We might want to access this component again in the Transaction to let's cache the PK/CompRev
            if (!CompRevInfoCache.TryGetValue(pk, out var list))
            {
                list = [];
                CompRevInfoCache.Add(pk, list);
            }
            list.Add(entry);
        }

        public Dictionary<long, List<CompRevInfo>> CompRevInfoCache;
    }

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

    private Dictionary<Type, ComponentInfoBase> _componentInfos;

    private int? _committedOperationCount;
    private int _deletedComponentCount;
    private ChangeSet _changeSet;

    // Reused across pooled Transaction lifetimes — collects deferred enqueue entries per commit/rollback
    // to flush in a single batch (one lock acquire instead of N). Never re-allocated after warmup.
    private List<DeferredCleanupManager.CleanupEntry> _deferredEnqueueBatch;

    /// <summary>The UoW that owns this transaction (null for legacy <c>CreateTransaction()</c> path, UoW ID effectively 0).</summary>
    internal UnitOfWork OwningUnitOfWork { get; private set; }

    /// <summary>When true, <see cref="Dispose"/> also disposes <see cref="OwningUnitOfWork"/>. Set by <c>CreateQuickTransaction()</c>.</summary>
    internal bool OwnsUnitOfWork { get; set; }

    /// <summary>UoW ID for revision stamping. 0 until UoW Registry (#51) assigns real IDs.</summary>
    internal ushort UowId => OwningUnitOfWork?.UowId ?? 0;

    public Transaction Previous { get; internal set; }
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
        _componentInfos = new Dictionary<Type, ComponentInfoBase>(ComponentInfosMaxCapacity);
    }

    public void Init(DatabaseEngine dbe, long tsn, UnitOfWork uow = null)
    {
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        _ = _epochManager.EnterScope(); // Depth unused: Transaction uses ExitScopeUnordered (not LIFO)
        _isDisposed = false;
        OwningUnitOfWork = uow;
#if DEBUG
        _debugOwningThreadId = Environment.CurrentManagedThreadId;
#endif
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = uow?.ChangeSet ?? _dbe.MMF.CreateChangeSet();
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
#if DEBUG
        _debugOwningThreadId = 0;
#endif
        if (_componentInfos.Capacity <= ComponentInfosMaxCapacity)
        {
            _componentInfos.Clear();
        }
        else
        {
            _componentInfos = new Dictionary<Type, ComponentInfoBase>(ComponentInfosMaxCapacity);
        }
        // Don't touch _isDisposed on purpose

        TSN = 0;
        Next = null;
        Previous = null;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = null;
        _deferredEnqueueBatch?.Clear();
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        AssertThreadAffinity();

        if (State != TransactionState.Committed)
        {
            Rollback();
        }

        // Process deferred cleanups if this transaction was blocking them. The queue holds entries from OTHER transactions whose cleanup was deferred because
        // this transaction (as tail) was blocking.
        if (_dbe.DeferredCleanupManager.QueueSize > 0)
        {
            var wcDeferred = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
            if (_dbe.TransactionChain.Control.EnterSharedAccess(ref wcDeferred))
            {
                var isTail = _dbe.TransactionChain.Tail == this;
                var nextMinTSN = isTail ? Previous?.TSN ?? (_dbe.TransactionChain.NextFreeId + 1) : 0;
                _dbe.TransactionChain.Control.ExitSharedAccess();

                if (isTail)
                {
                    _dbe.DeferredCleanupManager.ProcessDeferredCleanups(TSN, nextMinTSN, _dbe, _changeSet);
                }
            }
        }

        // Dispose all ChunkAccessors to flush dirty pages.
        foreach (var info in _componentInfos.Values)
        {
            info.DisposeAccessors();
        }

        // WAL-less mode: GroupCommit writes dirty pages to OS cache (Layer 1→2) per transaction.
        // Deferred skips this — UoW.Flush handles the full SaveChangesAsync → FlushToDisk pipeline.
        // Immediate already saved in Commit.
        if (State == TransactionState.Committed && _dbe.WalManager == null
            && OwningUnitOfWork?.DurabilityMode == DurabilityMode.GroupCommit)
        {
            _changeSet.SaveChanges();
        }

        // Exit epoch scope after accessors are disposed but before removing from the chain. This allows pages to be evicted once no transaction
        // references them. Use unordered exit because transactions on the same thread can be disposed in any order (not necessarily LIFO), unlike EpochGuard
        // which is stack-bound.
        _epochManager.ExitScopeUnordered();

        // Capture UoW reference before Remove() — Remove() may call Reset() which clears these fields
        var owningUow = OwnsUnitOfWork ? OwningUnitOfWork : null;

        _dbe.TransactionChain.Remove(this);

        // Auto-dispose UoW if this transaction owns it (CreateQuickTransaction pattern)
        owningUow?.Dispose();

        _isDisposed = true;
    }

    public long CreateEntity<T>(ref T t) where T : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return -1;
        }
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
        if (State > TransactionState.InProgress)
        {
            return -1;
        }
        State = TransactionState.InProgress;
        var pk = _dbe.GetNewPrimaryKey();

        CreateComponent(pk, ref t);
        CreateComponent(pk, ref u);
        return pk;
    }

    public long CreateEntity<TC1, TC2, TC3>(ref TC1 t, ref TC2 u, ref TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return -1;
        }
        State = TransactionState.InProgress;
        var pk = _dbe.GetNewPrimaryKey();

        CreateComponent(pk, ref t);
        CreateComponent(pk, ref u);
        CreateComponent(pk, ref v);
        return pk;
    }

    public long CreateEntity<TC1, TC2>(ref TC1 t, Span<TC2> u) where TC1 : unmanaged where TC2 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return -1;
        }
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
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        using var activity = TyphonActivitySource.StartActivity("Transaction.UpdateEntity");
        activity?.SetTag(TyphonSpanAttributes.EntityId, pk);
        activity?.SetTag(TyphonSpanAttributes.ComponentType, typeof(T).Name);

        return UpdateComponent(pk, ref t);
    }

    public bool UpdateEntity<TC1, TC2>(long pk, ref TC1 t, ref TC2 u) where TC1 : unmanaged where TC2 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref t);
        res &= UpdateComponent(pk, ref u);
        return res;
    }

    public bool UpdateEntity<TC1, TC2, TC3>(long pk, ref TC1 t, ref TC2 u, ref TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref t);
        res &= UpdateComponent(pk, ref u);
        res &= UpdateComponent(pk, ref v);
        return res;
    }

    public bool UpdateEntity<TC1, TC2>(long pk, ref TC1 t, ReadOnlySpan<TC2> u) where TC1 : unmanaged where TC2 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref t);
        res &= UpdateComponents(pk, u);
        return res;
    }

    public bool DeleteEntity<T>(long pk) where T : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        using var activity = TyphonActivitySource.StartActivity("Transaction.DeleteEntity");
        activity?.SetTag(TyphonSpanAttributes.EntityId, pk);
        activity?.SetTag(TyphonSpanAttributes.ComponentType, typeof(T).Name);

        return DeleteComponent<T>(pk);
    }

    public bool DeleteEntity<TC1, TC2>(long pk) where TC1 : unmanaged where TC2 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = DeleteComponent<TC1>(pk);
        res &= DeleteComponent<TC2>(pk);
        return res;
    }

    public bool DeleteEntity<TC1, TC2, TC3>(long pk) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = DeleteComponent<TC1>(pk);
        res &= DeleteComponent<TC2>(pk);
        res &= DeleteComponent<TC3>(pk);
        return res;
    }

    public bool DeleteEntities<T>(long pk) where T : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        return UpdateComponents(pk, ReadOnlySpan<T>.Empty);
    }
    
    public int GetComponentRevision<T>(long pk) where T : unmanaged
    {
        AssertThreadAffinity();
        var info = GetComponentInfo(typeof(T));
        if (info.IsMultiple)
        {
            var infoMultiple = (ComponentInfoMultiple)info;
            if (!infoMultiple.CompRevInfoCache.TryGetValue(pk, out var compRevInfoList))
            {
                return -1;
            }

            var compRevInfo = compRevInfoList[0];
            
            // After getting from the cache, check if it was deleted
            if (compRevInfo.CurCompContentChunkId == 0)
            {
                return -1;
            }
            
            ref var header = ref info.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevInfo.CompRevTableFirstChunkId);
            return header.FirstItemRevision + (compRevInfo.CurRevisionIndex - header.FirstItemIndex);
        }
        else
        {
            var infoSingle = (ComponentInfoSingle)info;
            if (!infoSingle.CompRevInfoCache.TryGetValue(pk, out var compRevInfo))
            {
                return -1;
            }

            // After getting from the cache, check if it was deleted
            if (compRevInfo.CurCompContentChunkId == 0)
            {
                return -1;
            }

            ref var header = ref info.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevInfo.CompRevTableFirstChunkId);
            return header.FirstItemRevision + (compRevInfo.CurRevisionIndex - header.FirstItemIndex);
        }
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
        using var a = new VariableSizedBufferAccessor<T>(vsbs, field._bufferId);

        return a.RefCounter;
    }
    
    [PublicAPI]
    public ref struct ReadOnlyCollectionEnumerator<T> where T : unmanaged
    {
        private BufferEnumerator<T> _enumerator;

        public ReadOnlyCollectionEnumerator(VariableSizedBufferSegment<T> vsbs, int bufferId)
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

    internal ref CompRevStorageHeader GetCompRevStorageHeader<T>(long entity)
    {
        var ci = GetComponentInfoSingle(typeof(T));
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

    private ComponentInfoBase GetComponentInfo(Type componentType)
    {
        if (_componentInfos.TryGetValue(componentType, out var info))
        {
            return info;
        }

        var ct = _dbe.GetComponentTable(componentType);
        if (ct == null)
        {
            throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");
        }

        if (!ct.Definition.AllowMultiple)
        {
            info = new ComponentInfoSingle
            {
                ComponentTable          = ct,
                CompContentSegment      = ct.ComponentSegment,
                CompRevTableSegment     = ct.CompRevTableSegment,
                PrimaryKeyIndex         = ct.PrimaryKeyIndex,
                CompContentAccessor     = ct.ComponentSegment.CreateChunkAccessor(_changeSet),
                CompRevTableAccessor    = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet),
                CompRevInfoCache        = new Dictionary<long, ComponentInfoBase.CompRevInfo>()
            };
        }
        else
        {
            info = new ComponentInfoMultiple
            {
                ComponentTable          = ct,
                CompContentSegment      = ct.ComponentSegment,
                CompRevTableSegment     = ct.CompRevTableSegment,
                PrimaryKeyIndex         = ct.PrimaryKeyIndex,
                CompContentAccessor     = ct.ComponentSegment.CreateChunkAccessor(_changeSet),
                CompRevTableAccessor    = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet),
                CompRevInfoCache        = new Dictionary<long, List<ComponentInfoBase.CompRevInfo>>()
            };
        }

        _componentInfos.Add(componentType, info);

        return info;
    }

    private ComponentInfoSingle GetComponentInfoSingle(Type componentType)
    {
        if (_componentInfos.TryGetValue(componentType, out var info))
        {
            return (ComponentInfoSingle)info;
        }

        var ct = _dbe.GetComponentTable(componentType);
        if (ct == null)
        {
            throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");
        }

        var entry = new ComponentInfoSingle
        {
            ComponentTable          = ct,
            CompContentSegment      = ct.ComponentSegment,
            CompRevTableSegment     = ct.CompRevTableSegment,
            PrimaryKeyIndex         = ct.PrimaryKeyIndex,
            CompContentAccessor     = ct.ComponentSegment.CreateChunkAccessor(_changeSet),
            CompRevTableAccessor    = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet),
            CompRevInfoCache        = new Dictionary<long, ComponentInfoBase.CompRevInfo>()
        };

        _componentInfos.Add(componentType, entry);

        return entry;
    }

    private ComponentInfoMultiple GetComponentInfoMultiple(Type componentType)
    {
        if (_componentInfos.TryGetValue(componentType, out var info))
        {
            return (ComponentInfoMultiple)info;
        }

        var ct = _dbe.GetComponentTable(componentType);
        if (ct == null)
        {
            throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");
        }

        var entry = new ComponentInfoMultiple
        {
            ComponentTable          = ct,
            CompContentSegment      = ct.ComponentSegment,
            CompRevTableSegment     = ct.CompRevTableSegment,
            PrimaryKeyIndex         = ct.PrimaryKeyIndex,
            CompContentAccessor     = ct.ComponentSegment.CreateChunkAccessor(_changeSet),
            CompRevTableAccessor    = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet),
            CompRevInfoCache        = new Dictionary<long, List<ComponentInfoBase.CompRevInfo>>()
        };

        _componentInfos.Add(componentType, entry);

        return entry;
    }

    private void CreateComponent<T>(long pk, ref T comp) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        
        // Fetch the cached info or create it if it's the first time we've operated on this Component type
        var info = GetComponentInfo(componentType);

        // Allocate the chunk that will store the component's chunk
        var componentChunkId = info.CompContentSegment.AllocateChunk(false);

        // Allocate the component revision storage as it's a new component
        var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, UowId, componentChunkId);

        var entry = new ComponentInfoBase.CompRevInfo
        {
            Operations = ComponentInfoBase.OperationType.Created,
            PrevCompContentChunkId = 0,
            PrevRevisionIndex = -1,
            CurCompContentChunkId = componentChunkId,
            CompRevTableFirstChunkId = compRevChunkId,
            CurRevisionIndex = 0
        };

        info.AddNew(pk, entry);

        // Copy the component data
        int compSize = info.ComponentTable.ComponentStorageSize;
        var dst = info.CompContentAccessor.GetChunkAsSpan(componentChunkId, true);
        new Span<byte>(Unsafe.AsPointer(ref comp), compSize).CopyTo(dst.Slice(info.ComponentTable.ComponentOverhead));
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
            var componentChunkId = info.CompContentSegment.AllocateChunk(false);

            // Allocate the component revision storage as it's a new component
            var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, UowId, componentChunkId);

            var entry = new ComponentInfoBase.CompRevInfo
            {
                Operations = ComponentInfoBase.OperationType.Created,
                PrevCompContentChunkId = 0,
                PrevRevisionIndex = -1,
                CurCompContentChunkId = componentChunkId,
                CompRevTableFirstChunkId = compRevChunkId,
                CurRevisionIndex = 0
            };

            info.AddNew(pk, entry);

            // Copy the component data
            var dst = info.CompContentAccessor.GetChunkAsSpan(componentChunkId, true);
            compList.Slice(i, 1).Cast<T, byte>().CopyTo(dst.Slice(info.ComponentTable.ComponentOverhead));
        }
    }
    
    private bool ReadComponent<T>(long pk, out T t) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var info = GetComponentInfoSingle(componentType);

        // Check if we already have this component in the cache
        ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.CompRevInfoCache, pk, out var exists);
        if (!exists)
        {
            // Couldn't find in the cache, get it from the index
            var result = GetCompRevInfoFromIndex(pk, info, TSN);
            if (result.IsFailure)
            {
                // NotFound, SnapshotInvisible, or Deleted — all mean no readable component. Remove the default entry that GetValueRefOrAddDefault added to
                // avoid leaving a zombie CompRevInfo (all zeros) that would corrupt subsequent operations on the same PK within this transaction.
                info.CompRevInfoCache.Remove(pk);
                t = default;
                return false;
            }
            compRevInfo = result.Value;
            compRevInfo.Operations |= ComponentInfoBase.OperationType.Read;
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
        var info = GetComponentInfoMultiple(componentType);

        // Check if we already have this component in the cache
        if (!info.CompRevInfoCache.TryGetValue(pk, out var compRevInfoList))
        {
            // Couldn't find in the cache, get it from the index
            if (!GetCompRevInfoFromIndex(pk, info, TSN, out compRevInfoList))
            {
                t = null;
                return false;
            }

            // Add to cache for future operations (revision tracking, updates, etc.)
            info.CompRevInfoCache[pk] = compRevInfoList;
        }

        var compRevInfoSpan = CollectionsMarshal.AsSpan(compRevInfoList);

        t = new T[compRevInfoSpan.Length];
        var deletedCount = 0;
        var destSpan = t.AsSpan();
        int destIndex = 0;
        for (int i = 0; i < compRevInfoSpan.Length; i++)
        {
            ref var compRevInfo = ref compRevInfoSpan[i];
            compRevInfo.Operations |= ComponentInfoBase.OperationType.Read;

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
        var info = GetComponentInfoSingle(componentType);

        // Check if the component is in the cache (meaning we already made an operation on it in this transaction)
        ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.CompRevInfoCache, pk, out var compRevCached);
        if (compRevCached)
        {
            // Can't update a deleted component...
            if ((compRevInfo.Operations & ComponentInfoBase.OperationType.Deleted) == ComponentInfoBase.OperationType.Deleted)
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
                info.CompRevInfoCache.Remove(pk);
                return false;
            }
            compRevInfo = result.Value; // Works for both Success AND Deleted (3-arg constructor)
        }

        // Update the operation types
        compRevInfo.Operations |= (isDelete ? ComponentInfoBase.OperationType.Deleted : ComponentInfoBase.OperationType.Updated);

        // First mutating operation on this component in this transaction: create a new component version
        if ((!compRevCached) || ((compRevInfo.Operations & ComponentInfoBase.OperationType.Read) != 0))
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
            return true;
        }

        return true;
    }

    private bool UpdateComponents<T>(long pk, ReadOnlySpan<T> compList) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var isDelete = compList.Length == 0;

        // Fetch the cached info or create it if it's the first time we operate on this Component type
        var info = GetComponentInfoMultiple(componentType);

        // Check if the component is in the cache (meaning we already made an operation on it in this transaction)
        var compRevCached = info.CompRevInfoCache.TryGetValue(pk, out var compRevInfoList);
        if (!compRevCached)
        {
            // Fetch the cache by getting the revision closest to the transaction tick, if we fail it means there's no revision, so no component for this
            //  PK, we return false
            if (!GetCompRevInfoFromIndex(pk, info, TSN, out compRevInfoList))
            {
                return false;
            }

            // Add to cache so the updates are tracked and committed
            info.CompRevInfoCache[pk] = compRevInfoList;
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
            if ((compRevInfo.Operations & ComponentInfoBase.OperationType.Deleted) == ComponentInfoBase.OperationType.Deleted)
            {
                return false;
            }

            // Check if we need to delete a component we previously added
            if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
            {
                info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
            }

            // Update the operation types
            compRevInfo.Operations |= (isDelete ? ComponentInfoBase.OperationType.Deleted : ComponentInfoBase.OperationType.Updated);

            // First mutating operation on this component in this transaction: create a new component version
            // Also create a new revision if the component was deleted (CurCompContentChunkId == 0) - to resurrect it
            if ((!compRevCached) || ((compRevInfo.Operations & ComponentInfoBase.OperationType.Read) != 0) || compRevInfo.CurCompContentChunkId == 0)
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
                compRevInfo.Operations |= ComponentInfoBase.OperationType.Deleted;
                ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, UowId, true);
            }
        }
        
        // Case 3
        else if (compList.Length > compRevInfoSpan.Length)
        {
            CreateComponents(pk, compList.Slice(compRevInfoSpan.Length));
        }

        return true;
    }

    private Result<int, BTreeLookupStatus> GetCompRevTableFirstChunkId(long pk, ComponentInfoSingle info)
    {
        var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
        var result = info.PrimaryKeyIndex.TryGet(pk, ref accessor);
        accessor.Dispose();
        return result;
    }

    private Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus> GetCompRevInfoFromIndex(long pk, ComponentInfoSingle info, long tick)
    {
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;

        int compRevFirstChunkId;
        {
            var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
            var lookupResult = info.PrimaryKeyIndex.TryGet(pk, ref accessor);
            accessor.Dispose();
            if (lookupResult.IsFailure)
            {
                return new Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus>(RevisionReadStatus.NotFound);
            }
            compRevFirstChunkId = lookupResult.Value;
        }

        short prevCompRevisionIndex = -1;
        short curCompRevisionIndex = -1;
        int prevCompChunkId = 0;
        int curCompChunkId = 0;

        // CommitSequence must be captured INSIDE the shared lock (held by RevisionEnumerator) so that ReadCS and the chain walk observe the same consistent
        // chain state. Capturing it outside the lock creates a race: cleanup or another commit can modify the chain between ReadCS capture and the lock
        // acquisition, leaving ReadCS consistent with a state the chain walk never sees.
        int readCommitSequence;

        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true);
            readCommitSequence = compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).CommitSequence;
            while (enumerator.MoveNext())
            {
                ref var element = ref enumerator.Current;

                if (element.IsVoid)
                {
                    continue;
                }

                // Do NOT break on TSN > reader.TSN — entries in the chain are NOT guaranteed to be in monotonically increasing TSN order. A higher-TSN
                // transaction can write (AddCompRev) before a lower-TSN transaction, placing its entry at a lower index. Breaking early would miss
                // committed entries with lower TSN at higher indices.
                if (element.TSN > TSN)
                {
                    continue;
                }

                // Update the current revision (and the previous) if a valid entry (tick == 0 means a rollbacked entry) and it's not an isolated one
                if ((element.TSN > 0) && !element.IsolationFlag)
                {
                    prevCompRevisionIndex = curCompRevisionIndex;
                    prevCompChunkId = curCompChunkId;
                    curCompRevisionIndex = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                    curCompChunkId = element.ComponentChunkId;
                }
            }
        }

        if (curCompRevisionIndex == -1)
        {
            return new Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus>(RevisionReadStatus.SnapshotInvisible);
        }

        var compRevInfo = new ComponentInfoBase.CompRevInfo
        {
            Operations = ComponentInfoBase.OperationType.Undefined,
            CompRevTableFirstChunkId = compRevFirstChunkId,
            CurCompContentChunkId = curCompChunkId,
            CurRevisionIndex = curCompRevisionIndex,
            PrevCompContentChunkId = prevCompChunkId,
            PrevRevisionIndex = prevCompRevisionIndex,
            ReadCommitSequence = readCommitSequence
        };

        // Tombstoned entity: carry the value (callers like UpdateComponent need revision metadata) but signal Deleted
        if (curCompChunkId == 0)
        {
            return new Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus>(compRevInfo, RevisionReadStatus.Deleted);
        }

        return new Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus>(compRevInfo);
    }

    private bool GetCompRevInfoFromIndex(long pk, ComponentInfoMultiple info, long tick, out List<ComponentInfoBase.CompRevInfo> compRevInfoList)
    {
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;

        var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
        using var vsba = info.PrimaryKeyIndex.TryGetMultiple(pk, ref accessor);
        if (!vsba.IsValid)
        {
            accessor.Dispose();
            compRevInfoList = null;
            return false;
        }
        accessor.Dispose();

        compRevInfoList = new List<ComponentInfoBase.CompRevInfo>(vsba.TotalCount);
        do
        {
            var compRevChunks = vsba.Elements;
            foreach (int compRevFirstChunkId in compRevChunks)
            {
                short prevCompRevisionIndex = -1;
                short curCompRevisionIndex = -1;
                int prevCompChunkId = 0;
                int curCompChunkId = 0;
                // CommitSequence captured INSIDE the shared lock (same rationale as GetCompRevInfoFromIndex)
                int readCommitSequence;

                {
                    using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true);
                    readCommitSequence = compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).CommitSequence;
                    while (enumerator.MoveNext())
                    {
                        ref var element = ref enumerator.Current;
                        if (element.TSN > TSN)
                        {
                            continue;
                        }

                        // Update the current revision (and the previous) if a valid entry (tick == 0 means a rollbacked entry) and it's not an isolated one
                        if ((element.TSN > 0) && !element.IsolationFlag)
                        {
                            prevCompRevisionIndex = curCompRevisionIndex;
                            prevCompChunkId = curCompChunkId;
                            curCompRevisionIndex = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                            curCompChunkId = element.ComponentChunkId;
                        }
                    }
                }

                if (curCompRevisionIndex != -1)
                {
                    compRevInfoList.Add(new ComponentInfoBase.CompRevInfo
                    {
                        Operations = ComponentInfoBase.OperationType.Undefined,
                        CompRevTableFirstChunkId = compRevFirstChunkId,
                        CurCompContentChunkId = curCompChunkId,
                        CurRevisionIndex = curCompRevisionIndex,
                        PrevCompContentChunkId = prevCompChunkId,
                        PrevRevisionIndex = prevCompRevisionIndex,
                        ReadCommitSequence = readCommitSequence
                    });
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
    /// <b>THE COMPONENT COMMIT METHOD (a big one, bold upper case were mandatory!)</b>
    /// </summary>
    /// <returns>Return <c>true</c> if the whole component is deleted (all component versions were deleted/rollbacked)</returns>
    /// <remarks>
    /// <para>
    /// When a <see cref="ConcurrencyConflictHandler"/> is provided, the commit sequence for each entity is made atomic by holding the per-entity revision
    /// chain lock during conflict detection, resolution, and commit.
    /// This prevents TOCTOU races where another transaction could commit between our conflict check and our commit.
    /// </para>
    /// <para>
    /// Conflict detection compares the <see cref="CompRevStorageElement.ComponentChunkId"/> at <c>LastCommitRevisionIndex</c> with the chunk we originally
    /// read. This catches all write-write conflicts regardless of revision allocation order.
    /// </para>
    /// </remarks>
    private bool CommitComponent(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;
        ref var compRevInfo = ref context.CompRevInfo;
        var isRollback = context.IsRollback;
        var conflictSolver = context.Solver;

        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var componentSegment = info.CompContentSegment;
        var revTableSegment = info.CompRevTableSegment;

        // Get the chunk of the header, pin it because we might access another chunk while walking the chain
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;
        var dirtyFirstChunk = false;

        // Get the chunk storing the revision we want to commit as well as the index of the element
        var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId);
        var lastCommitRevisionIndex = compRev.LastCommitRevisionIndex;
        var elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Validate CurRevisionIndex — chain compaction by another transaction's cleanup may have
        // shifted entry positions since our read. Best-effort (no lock) for rollback and no-handler paths.
        if (elementHandle.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
        {
            var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
            if (fixedIndex >= 0)
            {
                compRevInfo.CurRevisionIndex = fixedIndex;
                elementHandle = compRev.GetRevisionElement(fixedIndex);
            }
        }

        // Clear the entry of the transaction component revision if it's a rollback
        if (isRollback)
        {
            // If any, free the chunk storing the content
            if (compRevInfo.CurCompContentChunkId != 0)
            {
                componentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
            }

            // If we roll back a created component, we must delete the revision table chunk
            if ((compRevInfo.Operations & ComponentInfoBase.OperationType.Created) == ComponentInfoBase.OperationType.Created)
            {
                revTableSegment.FreeChunk(firstChunkId);

                // WARNING: Normal early exit, I usually don't like it, but from this point the RevTable Start chunk is gone, going on into the rest of the
                //  code would be dangerous as we have pointers that would be bad.
                return true;
            }

            // In case of update or delete, mark void the revision entry we added
            if ((compRevInfo.Operations & (ComponentInfoBase.OperationType.Updated | ComponentInfoBase.OperationType.Deleted)) != 0)
            {
                compRev.VoidElement(elementHandle);
            }
        }

        // Commit the revision
        else
        {
            // Capture the ChunkId of the component content we originally read, because PrevCompContentChunkId// will be shifted by AddCompRev if we need to
            // create a new revision for conflict resolution
            var readCompChunkId = compRevInfo.PrevCompContentChunkId;

            // When a conflict handler is provided, we hold the per-entity revision chain lock during the entire detect-resolve-commit sequence to prevent
            // TOCTOU races (another transaction committing between our conflict check and our commit would make committedData stale).
            // Without a handler, the "last wins" path uses the original index-based detection as a best-effort check.
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

                // Re-read lastCommitRevisionIndex under lock (authoritative value)
                lastCommitRevisionIndex = lockHeader.LastCommitRevisionIndex;

                // Re-validate CurRevisionIndex under lock — chain compaction between our read and lock acquisition may have shifted entry positions. Under
                // the exclusive lock, no concurrent compaction can happen, so this validation is race-free.
                {
                    var curElement = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
                    if (curElement.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
                    {
                        var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(
                            ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
                        if (fixedIndex < 0)
                        {
                            ThrowHelper.ThrowInvalidOp("CommitComponent: revision entry lost after chain compaction");
                        }
                        compRevInfo.CurRevisionIndex = fixedIndex;
                        elementHandle = compRev.GetRevisionElement(fixedIndex);
                    }
                }

                // Relocate: if our entry is at or behind LCRI, a later-created transaction committed at a higher index (because it wrote after us). Without
                // relocation, the chain walk (which returns the highest-index committed entry) would shadow our value with the older commit at the higher
                // index. Move our entry to the chain end so the most recently committed value is always at the highest index.
                if (lastCommitRevisionIndex >= 0 && compRevInfo.CurRevisionIndex <= lastCommitRevisionIndex)
                {
                    // Save the chunk that holds our modified data
                    var oldContentChunkId = compRevInfo.CurCompContentChunkId;

                    // Create new entry at end of chain (under existing lock)
                    ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, UowId, false, lockAlreadyHeld: true);

                    // Copy our data to the new content chunk
                    var srcAddr = info.CompContentAccessor.GetChunkAddress(oldContentChunkId);
                    var dstAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
                    new Span<byte>(srcAddr, info.ComponentTable.ComponentTotalSize)
                        .CopyTo(new Span<byte>(dstAddr, info.ComponentTable.ComponentTotalSize));

                    // Free the old content chunk and void the orphaned entry. The old entry at// PrevRevisionIndex retains IsolationFlag=true which blocks
                    // deferred cleanup compaction. Voiding it (without decrementing ItemCount) makes it a harmless placeholder that cleanup will skip over.
                    info.CompContentSegment.FreeChunk(oldContentChunkId);
                    compRev.GetRevisionElement(compRevInfo.PrevRevisionIndex).Element.Void();

                    elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
                }

            }

            try
            {
                // Detect write-write conflict (two complementary checks for the handler path):
                //
                // 1. CommitSequence: detects any commit since our read (monotonic counter, immune to revision index ordering and cleanup compaction).
                //
                // 2. Invisible-commit TSN check: detects committed entries by higher-TSN transactions that are invisible to our snapshot (TSN > our.TSN).
                //    This happens when a transaction created after us commits before us — our chain walk filters it out (TSN-based snapshot visibility) but
                //    the entity state has changed.
                //
                // Without handler: use the original index-based check as best-effort for "last wins".
                var hasConflict = (conflictSolver != null) ? 
                    compRev.CommitSequence != compRevInfo.ReadCommitSequence || 
                        (lastCommitRevisionIndex >= 0 && compRev.GetRevisionElement(lastCommitRevisionIndex).Element.TSN > TSN) : 
                    lastCommitRevisionIndex >= compRevInfo.CurRevisionIndex;

                if (hasConflict)
                {
                    // Record conflict for observability
                    _dbe?.RecordConflict();

                    // Save the orphan index before AddCompRev changes CurRevisionIndex
                    var conflictOrphanIndex = compRevInfo.CurRevisionIndex;

                    // Create a new revision for the resolved data (under existing lock when handler is provided)
                    ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, UowId, false, lockAlreadyHeld: lockHeld);

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
                        var readChunk = info.CompContentAccessor.GetChunkAddress(readCompChunkId) + overhead;
                        var committingChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId) + overhead;
                        var toCommitChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead;
                        var committedChunk = info.CompContentAccessor.GetChunkAddress(lastCommitHandle.Element.ComponentChunkId) + overhead;

                        conflictSolver.Setup(pk, info, readChunk, committedChunk, committingChunk, toCommitChunk);
                        context.Handler(ref conflictSolver);
                    }

                    // Void the orphaned entry at the old position and free its content chunk. The data was copied to the new entry; the handler has finished
                    // using PrevCompContentChunkId as committingChunk. Without voiding, the IsolationFlag=true orphan blocks deferred cleanup compaction.
                    var conflictOrphan = compRev.GetRevisionElement(conflictOrphanIndex);
                    if (conflictOrphan.Element.ComponentChunkId > 0)
                    {
                        info.CompContentSegment.FreeChunk(conflictOrphan.Element.ComponentChunkId);
                    }
                    conflictOrphan.Element.Void();
                }

                // Commit the revision: update indices, clear IsolationFlag, update LastCommitRevisionIndex
                if (compRevInfo.CurCompContentChunkId != 0)
                {
                    UpdateIndices(pk, info, compRevInfo, readCompChunkId);
                }
                else if (readCompChunkId != 0)
                {
                    RemoveSecondaryIndices(info, readCompChunkId, compRevInfo.CompRevTableFirstChunkId);
                }

                elementHandle.Commit(TSN);
                compRev.SetLastCommitRevisionIndex(Math.Max(lastCommitRevisionIndex, compRevInfo.CurRevisionIndex));
                compRev.IncrementCommitSequence();

            }
            finally
            {
                if (lockHeld)
                {
                    ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                    lockHeader.Control.ExitExclusiveAccess();
                }
            }
        }

        // Enqueue for deferred cleanup — all cleanup is processed AFTER the commit loop, never inline.
        // This avoids chain compaction while other transactions hold cached revision indices.
        _deferredEnqueueBatch ??= new List<DeferredCleanupManager.CleanupEntry>(16);
        _deferredEnqueueBatch.Add(new DeferredCleanupManager.CleanupEntry { Table = info.ComponentTable, PrimaryKey = pk, FirstChunkId = firstChunkId });

        // Flush at capacity to bound memory — content is transient, safe to drain mid-loop
        if (_deferredEnqueueBatch.Count >= DeferredEnqueueBatchCapacity)
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // As we committed/rolled back the current revision, we don't need to keep track of the previous one anymore
        compRevInfo.PrevCompContentChunkId = -1;
        compRevInfo.PrevRevisionIndex = 0;

        if (dirtyFirstChunk)
        {
            compRevTableAccessor.DirtyChunk(firstChunkId);
        }

        return false;
    }

    private void UpdateIndices(long pk, ComponentInfoBase info, ComponentInfoBase.CompRevInfo compRevInfo, int prevCompChunkId)
    {
        // If there's a previous revision, we need to update the indices if some indexed fields changed
        var startChunkId = compRevInfo.CompRevTableFirstChunkId;
        if (prevCompChunkId != 0)
        {
            var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId);
            var prevSpan = new Span<byte>(prev, info.ComponentTable.ComponentTotalSize);
            var curSpan = new Span<byte>(cur, info.ComponentTable.ComponentTotalSize);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                // The update changed the field?
                if (prevSpan.Slice(ifi.OffsetToField, ifi.Size).SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)) == false)
                {
                    var accessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);
                    if (ifi.Index.AllowMultiple)
                    {
                        ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor);
                        *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                    }
                    else
                    {
                        ifi.Index.Remove(&prev[ifi.OffsetToField], out var val, ref accessor);
                        ifi.Index.Add(&cur[ifi.OffsetToField], val, ref accessor);
                    }
                    accessor.Dispose();
                }
                else if (ifi.Index.AllowMultiple)
                {
                    // Carry forward the elementId for unchanged AllowMultiple fields so that
                    // the new content chunk has valid buffer references for later removal (e.g., on delete).
                    *(int*)&cur[ifi.OffsetToIndexElementId] = *(int*)&prev[ifi.OffsetToIndexElementId];
                }
            }
        }

        // No previous revision, it means we're adding the first component revision, add the indices
        // But only if this is truly a new component (Created operation), not a resurrection (Updated operation with prevCompChunkId == 0)
        else if ((compRevInfo.Operations & ComponentInfoBase.OperationType.Created) == ComponentInfoBase.OperationType.Created)
        {
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId);

            // Update the index with this new entry
            {
                var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
                info.PrimaryKeyIndex.Add(pk, startChunkId, ref accessor);
                accessor.Dispose();
            }

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                var accessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);
                if (ifi.Index.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                }
                else
                {
                    ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                }
                accessor.Dispose();
            }
        }
    }

    private void RemoveSecondaryIndices(ComponentInfoBase info, int prevCompChunkId, int startChunkId)
    {
        var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
        var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            var accessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);
            if (ifi.Index.AllowMultiple)
            {
                ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor);
            }
            else
            {
                ifi.Index.Remove(&prev[ifi.OffsetToField], out _, ref accessor);
            }
            accessor.Dispose();
        }
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

        var context = new CommitContext { IsRollback = true };
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
        context.NextMinTSN = context.IsTail ? _dbe.TransactionChain.Tail.Previous?.TSN ?? (_dbe.TransactionChain.NextFreeId + 1) : 0;
        context.TailTSN = _dbe.TransactionChain.MinTSN;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        var deletedComponentSingles = new List<long>();
        // Process every Component Type and their components
        foreach (var componentInfo in _componentInfos.Values)
        {
            context.Info = componentInfo;
            deletedComponentSingles.Clear();

            switch (componentInfo)
            {
                case ComponentInfoSingle single:
                    foreach (var key in single.CompRevInfoCache.Keys)
                    {
                        context.PrimaryKey = key;
                        context.CompRevInfo = ref CollectionsMarshal.GetValueRefOrNullRef(single.CompRevInfoCache, key);

                        // Nothing to rollback if we only read the component
                        if (context.CompRevInfo.Operations == ComponentInfoBase.OperationType.Read)
                        {
                            continue;
                        }

                        if (CommitComponent(ref context))
                        {
                            deletedComponentSingles.Add(context.PrimaryKey);
                        }
                    }

                    foreach (var pk in deletedComponentSingles)
                    {
                        single.CompRevInfoCache.Remove(pk);
                        _deletedComponentCount++;
                    }
                    break;

                case ComponentInfoMultiple multiple:
                    foreach (var key in multiple.CompRevInfoCache.Keys)
                    {
                        context.PrimaryKey = key;
                        var comRevInfoList = CollectionsMarshal.AsSpan(CollectionsMarshal.GetValueRefOrNullRef(multiple.CompRevInfoCache, key));

                        for (int i = 0; i < comRevInfoList.Length; i++)
                        {
                            ref ComponentInfoBase.CompRevInfo compRevInfo = ref comRevInfoList[i];
                            context.CompRevInfo = ref compRevInfo;

                            // Nothing to rollback if we only read the component
                            if (context.CompRevInfo.Operations == ComponentInfoBase.OperationType.Read)
                            {
                                continue;
                            }

                            if (CommitComponent(ref context))
                            {
                                deletedComponentSingles.Add(context.PrimaryKey);
                            }
                        }
                    }
                    break;
            }
        }

        // Flush batched deferred enqueue entries (non-tail path: single lock acquire for all entities)
        if (_deferredEnqueueBatch is { Count: > 0 })
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // New state
        State = TransactionState.Rollbacked;
        activity?.SetTag(TyphonSpanAttributes.TransactionStatus, "rolledback");
        _dbe?.RecordRollback();
        return true;
    }

    public bool Rollback()
    {
        var ctx = UnitOfWorkContext.None;
        return Rollback(ref ctx);
    }

    [PublicAPI]
    public delegate void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver);

    [ThreadStatic]
    private static ConcurrencyConflictSolver ThreadLocalConflictSolver;
    
    private static ConcurrencyConflictSolver GetConflictSolver()
    {
        ThreadLocalConflictSolver ??= new ConcurrencyConflictSolver();
        ThreadLocalConflictSolver.Reset();
        return ThreadLocalConflictSolver;
    }
    
    /// <summary>
    /// Provides conflict resolution context when a write-write conflict is detected during
    /// <see cref="Transaction.Commit(ConcurrencyConflictHandler)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// During commit, each entity is checked for conflicts (another transaction committed new data since our read).
    /// When a conflict is detected and a <see cref="ConcurrencyConflictHandler"/> was provided, the solver is populated
    /// with the conflicting entity's data, then the handler is invoked once per conflicting entity.
    /// </para>
    /// <para>The four data views are:</para>
    /// <list type="bullet">
    /// <item><description><see cref="ReadData{T}"/>: the component state at the time of <c>ReadEntity</c> (our transaction's snapshot baseline).</description></item>
    /// <item><description><see cref="CommittedData{T}"/>: the latest committed state by another transaction (the value that caused the conflict).</description></item>
    /// <item><description><see cref="CommittingData{T}"/>: the dirty-write state from our <c>UpdateEntity</c> call (what we intended to commit).</description></item>
    /// <item><description><see cref="ToCommitData{T}"/>: the output buffer — initialized with <c>CommittingData</c> (last writer wins). The handler writes the resolved value here.</description></item>
    /// </list>
    /// <para>
    /// The default resolution (before the handler runs) is "last writer wins": the committing data is copied into <c>ToCommitData</c>.
    /// The handler can override this by writing a different value (e.g., rebasing a delta onto the latest committed state).
    /// </para>
    /// </remarks>
    [PublicAPI]
    public class ConcurrencyConflictSolver
    {
        private byte* _readData;
        private byte* _committedData;
        private byte* _committingData;
        private byte* _toCommitData;
        private ComponentInfoBase _info;

        internal ConcurrencyConflictSolver() { }

        /// <summary>Primary key of the conflicting entity.</summary>
        public long PrimaryKey { get; private set; }

        /// <summary>The .NET type of the component (e.g. <c>typeof(CompA)</c>).</summary>
        public Type ComponentType => _info.ComponentTable.Definition.POCOType;

        /// <summary>The component definition with field metadata.</summary>
        public DBComponentDefinition ComponentDefinition => _info.ComponentTable.Definition;

        /// <summary>Whether a conflict was detected for this solver instance.</summary>
        public bool HasConflict { get; private set; }

        /// <summary>Copies <see cref="ReadData{T}"/> into <see cref="ToCommitData{T}"/> (discard all changes, revert to read snapshot).</summary>
        public void TakeRead<T>() where T : unmanaged => ToCommitData<T>() = ReadData<T>();

        /// <summary>Copies <see cref="CommittedData{T}"/> into <see cref="ToCommitData{T}"/> (accept the other transaction's value).</summary>
        public void TakeCommitted<T>() where T : unmanaged => ToCommitData<T>() = CommittedData<T>();

        /// <summary>Copies <see cref="CommittingData{T}"/> into <see cref="ToCommitData{T}"/> (last writer wins — this is the default).</summary>
        public void TakeCommitting<T>() where T : unmanaged => ToCommitData<T>() = CommittingData<T>();

        /// <summary>Returns a ref to the component state as read by our transaction (snapshot baseline).</summary>
        public ref T ReadData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_readData);

        /// <summary>Returns a ref to the latest committed state by another transaction (the conflicting value).</summary>
        public ref T CommittedData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_committedData);

        /// <summary>Returns a ref to our dirty-write state from <c>UpdateEntity</c> (what we intended to commit).</summary>
        public ref T CommittingData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_committingData);

        /// <summary>Returns a ref to the output buffer. Write the resolved value here. Initialized with <see cref="CommittingData{T}"/>.</summary>
        public ref T ToCommitData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_toCommitData);

        internal void Setup(long pk, ComponentInfoBase info, byte* readData, byte* committedData, byte* committingData, byte* toCommitData)
        {
            PrimaryKey = pk;
            _readData = readData;
            _committedData = committedData;
            _committingData = committingData;
            _toCommitData = toCommitData;
            _info = info;
            HasConflict = true;

            // Default is last revision wins, so we copy the committing data to the toCommit data
            var componentSize = info.ComponentTable.ComponentStorageSize;
            new Span<byte>(_committingData, componentSize).CopyTo(new Span<byte>(_toCommitData, componentSize));
        }

        internal void Reset()
        {
            HasConflict = false;
        }
    }

    internal ref struct CommitContext
    {
        public long PrimaryKey;
        public ComponentInfoBase Info;
        public ref ComponentInfoBase.CompRevInfo CompRevInfo;
        public ConcurrencyConflictSolver Solver;
        public ConcurrencyConflictHandler Handler;
        public bool IsRollback;
        public ref UnitOfWorkContext Ctx;

        // Hoisted from per-entity to per-commit: determined once before the entity loop
        public bool IsTail;
        public long NextMinTSN;  // Valid when IsTail == true: the TSN to keep revisions for
        public long TailTSN;     // Valid when IsTail == false: the blocking tail's TSN
    }
    
    public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
    {
        AssertThreadAffinity();

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
                    var nextMinTSNDeferred = isTailForDeferred ? _dbe.TransactionChain.Tail.Previous?.TSN ?? (_dbe.TransactionChain.NextFreeId + 1) : 0;
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

        // ── Holdoff: entire commit loop runs to completion ──
        using var holdoff = ctx.EnterHoldoff();

        var conflictSolver = handler != null ? GetConflictSolver() : null;
        var context = new CommitContext { IsRollback = false, Solver = conflictSolver, Handler = handler };
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
        context.NextMinTSN = context.IsTail ? _dbe.TransactionChain.Tail.Previous?.TSN ?? (_dbe.TransactionChain.NextFreeId + 1) : 0;
        context.TailTSN = _dbe.TransactionChain.MinTSN;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        // Process every Component Type and their components
        foreach (var kvp in _componentInfos)
        {
            var componentType = kvp.Key;
            var componentInfo = kvp.Value;
            context.Info = componentInfo;

            // Start a sub-span for this component type
            using var componentActivity = TyphonActivitySource.StartActivity("Transaction.CommitComponent");
            componentActivity?.SetTag(TyphonSpanAttributes.ComponentType, componentType.Name);

            switch (componentInfo)
            {
                case ComponentInfoSingle single:
                    foreach (var key in single.CompRevInfoCache.Keys)
                    {
                        context.PrimaryKey = key;
                        context.CompRevInfo = ref CollectionsMarshal.GetValueRefOrNullRef(single.CompRevInfoCache, key);

                        // Nothing to commit if we only read the component
                        if (context.CompRevInfo.Operations == ComponentInfoBase.OperationType.Read)
                        {
                            continue;
                        }

                        CommitComponent(ref context);
                    }
                    break;

                case ComponentInfoMultiple multiple:
                    foreach (var key in multiple.CompRevInfoCache.Keys)
                    {
                        context.PrimaryKey = key;
                        var comRevInfoList = CollectionsMarshal.AsSpan(CollectionsMarshal.GetValueRefOrNullRef(multiple.CompRevInfoCache, key));

                        foreach (ref var compRevInfo in comRevInfoList)
                        {
                            context.CompRevInfo = ref compRevInfo;

                            // Nothing to commit if we only read the component
                            if (context.CompRevInfo.Operations == ComponentInfoBase.OperationType.Read)
                            {
                                continue;
                            }

                            CommitComponent(ref context);
                        }
                    }
                    break;
            }
        }

        // Enqueue current transaction's entities for deferred cleanup (single lock acquire for all entities).
        // Processing happens in Dispose (after cached indices are no longer relevant) or via FlushDeferredCleanups.
        if (_deferredEnqueueBatch is { Count: > 0 })
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // Check if any conflicts were detected during the commit loop
        if (conflictSolver is { HasConflict: true })
        {
            hasConflict = true;
        }

        activity?.SetTag(TyphonSpanAttributes.TransactionConflictDetected, hasConflict);
        activity?.SetTag(TyphonSpanAttributes.TransactionStatus, "committed");

        // WAL serialization (after conflict resolution, before state transition)
        long walHighLsn = 0;
        if (_dbe.WalManager != null && State != TransactionState.Created)
        {
            walHighLsn = SerializeToWal(ref ctx);
        }

        // Durability wait for Immediate mode
        if (walHighLsn > 0 && OwningUnitOfWork?.DurabilityMode == DurabilityMode.Immediate)
        {
            _dbe.WalManager.RequestFlush();
            var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.DefaultCommitTimeout);
            _dbe.WalManager.WaitForDurable(walHighLsn, ref wc);
        }

        // WAL-less Immediate: persist dirty data pages and fsync before returning from Commit.
        // This is the WAL-less equivalent of the WAL FUA path above — data is on stable storage when Commit returns.
        if (_dbe.WalManager == null && OwningUnitOfWork?.DurabilityMode == DurabilityMode.Immediate)
        {
            // Flush batched dirty flags from long-lived accessors to the ChangeSet (BTree accessors are already
            // disposed inline during CommitComponent, so their pages are already tracked).
            foreach (var kvp in _componentInfos)
            {
                kvp.Value.CompContentAccessor.CommitChanges();
                kvp.Value.CompRevTableAccessor.CommitChanges();
            }

            _changeSet.SaveChanges();
            _dbe.MMF.FlushToDisk();
        }

        // New state
        State = TransactionState.Committed;

        // Record commit duration for observability
        var elapsedUs = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;
        _dbe?.RecordCommitDuration(elapsedUs);

        return true;
    }

    public bool Commit(ConcurrencyConflictHandler handler = null)
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
        return Commit(ref ctx, handler);
    }

    /// <summary>
    /// Serializes all committed component changes into the WAL commit buffer. Called after CommitComponent loop completes (all conflicts resolved,
    /// revisions visible) but before State = Committed.
    /// </summary>
    /// <returns>The highest LSN assigned to the serialized records, or 0 if nothing was serialized.</returns>
    private long SerializeToWal(ref UnitOfWorkContext ctx)
    {
        // Count non-Read operations and total payload size
        int recordCount = 0;
        int totalPayload = 0;

        foreach (var kvp in _componentInfos)
        {
            var info = kvp.Value;
            var storageSize = info.ComponentTable.ComponentStorageSize;

            switch (info)
            {
                case ComponentInfoSingle single:
                    foreach (var pk in single.CompRevInfoCache.Keys)
                    {
                        ref var cri = ref CollectionsMarshal.GetValueRefOrNullRef(single.CompRevInfoCache, pk);
                        if (cri.Operations == ComponentInfoBase.OperationType.Read)
                        {
                            continue;
                        }

                        recordCount++;
                        var isDelete = (cri.Operations & ComponentInfoBase.OperationType.Deleted) != 0;
                        var bodyLen = WalRecordHeader.SizeInBytes + (isDelete ? 0 : storageSize);
                        totalPayload += WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
                    }
                    break;

                case ComponentInfoMultiple multiple:
                    foreach (var pk in multiple.CompRevInfoCache.Keys)
                    {
                        var list = CollectionsMarshal.AsSpan(CollectionsMarshal.GetValueRefOrNullRef(multiple.CompRevInfoCache, pk));
                        foreach (ref var cri in list)
                        {
                            if (cri.Operations == ComponentInfoBase.OperationType.Read)
                            {
                                continue;
                            }

                            recordCount++;
                            var isDelete = (cri.Operations & ComponentInfoBase.OperationType.Deleted) != 0;
                            var bodyLen = WalRecordHeader.SizeInBytes + (isDelete ? 0 : storageSize);
                            totalPayload += WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
                        }
                    }
                    break;
            }
        }

        if (recordCount == 0)
        {
            return 0;
        }

        // Claim space in the commit buffer
        var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.DefaultCommitTimeout);
        var claim = _dbe.WalManager.CommitBuffer.TryClaim(totalPayload, recordCount, ref wc);

        if (!claim.IsValid)
        {
            return 0;
        }

        try
        {
            // Initialize writer state for the claimed region
            var writer = new WalRecordWriter
            {
                DataSpan = claim.DataSpan,
                WriteOffset = 0,
                RecordIndex = 0,
                CurrentLsn = claim.FirstLSN,
                TotalRecordCount = recordCount
            };

            foreach (var kvp in _componentInfos)
            {
                var info = kvp.Value;
                var componentTypeId = info.ComponentTable.WalTypeId;
                var storageSize = info.ComponentTable.ComponentStorageSize;

                switch (info)
                {
                    case ComponentInfoSingle single:
                        foreach (var pk in single.CompRevInfoCache.Keys)
                        {
                            ref var cri = ref CollectionsMarshal.GetValueRefOrNullRef(single.CompRevInfoCache, pk);
                            if (cri.Operations == ComponentInfoBase.OperationType.Read)
                            {
                                continue;
                            }

                            WriteWalRecord(ref writer, pk, componentTypeId, storageSize, ref cri, info);
                        }
                        break;

                    case ComponentInfoMultiple multiple:
                        foreach (var pk in multiple.CompRevInfoCache.Keys)
                        {
                            var list = CollectionsMarshal.AsSpan(CollectionsMarshal.GetValueRefOrNullRef(multiple.CompRevInfoCache, pk));
                            foreach (ref var cri in list)
                            {
                                if (cri.Operations == ComponentInfoBase.OperationType.Read)
                                {
                                    continue;
                                }

                                WriteWalRecord(ref writer, pk, componentTypeId, storageSize, ref cri, info);
                            }
                        }
                        break;
                }
            }

            _dbe.WalManager.CommitBuffer.Publish(ref claim);
            return writer.HighestLsn;
        }
        catch
        {
            _dbe.WalManager.CommitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }

    /// <summary>
    /// Mutable state for writing WAL records within a single claim.
    /// Groups the cursor state that advances as records are written.
    /// </summary>
    private ref struct WalRecordWriter
    {
        public Span<byte> DataSpan;
        public int WriteOffset;
        public int RecordIndex;
        public long CurrentLsn;
        public int TotalRecordCount;

        /// <summary>
        /// Returns the highest LSN that was written (CurrentLsn - 1 after writing).
        /// </summary>
        public readonly long HighestLsn => CurrentLsn - 1;
    }

    /// <summary>
    /// Writes a single WAL chunk (chunk header + record header + payload + chunk footer) into the claim data span.
    /// CRC and PrevCRC are left as 0 — the WAL writer thread patches them before disk write.
    /// </summary>
    private void WriteWalRecord(ref WalRecordWriter writer, long entityId, ushort componentTypeId, int storageSize,
        ref ComponentInfoBase.CompRevInfo cri, ComponentInfoBase info)
    {
        var isDelete = (cri.Operations & ComponentInfoBase.OperationType.Deleted) != 0;
        var isCreate = (cri.Operations & ComponentInfoBase.OperationType.Created) != 0;
        var payloadLength = isDelete ? 0 : storageSize;

        // Determine WAL operation type
        WalOperationType opType;
        if (isDelete)
        {
            opType = WalOperationType.Delete;
        }
        else if (isCreate)
        {
            opType = WalOperationType.Create;
        }
        else
        {
            opType = WalOperationType.Update;
        }

        // Build flags
        var flags = WalRecordFlags.None;
        if (writer.RecordIndex == 0)
        {
            flags |= WalRecordFlags.UowBegin;
        }

        if (writer.RecordIndex == writer.TotalRecordCount - 1)
        {
            flags |= WalRecordFlags.UowCommit;
        }

        var bodyLen = WalRecordHeader.SizeInBytes + payloadLength;
        var chunkSize = (ushort)(WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes);

        // Write chunk header (PrevCRC=0 — patched by WAL writer)
        var chunkHeader = new WalChunkHeader
        {
            ChunkType = (ushort)WalChunkType.Transaction,
            ChunkSize = chunkSize,
            PrevCRC = 0,
        };
        MemoryMarshal.Write(writer.DataSpan[writer.WriteOffset..], in chunkHeader);

        // Write record header
        var recordOffset = writer.WriteOffset + WalChunkHeader.SizeInBytes;
        var header = new WalRecordHeader
        {
            LSN = writer.CurrentLsn,
            TransactionTSN = TSN,
            UowEpoch = UowId,
            ComponentTypeId = componentTypeId,
            EntityId = entityId,
            PayloadLength = (ushort)payloadLength,
            OperationType = (byte)opType,
            Flags = (byte)flags,
        };
        MemoryMarshal.Write(writer.DataSpan[recordOffset..], in header);

        // Write payload (component data) for non-delete operations
        if (payloadLength > 0 && cri.CurCompContentChunkId > 0)
        {
            var srcSpan = info.CompContentAccessor.GetChunkAsReadOnlySpan(cri.CurCompContentChunkId);
            var payloadDst = writer.DataSpan.Slice(recordOffset + WalRecordHeader.SizeInBytes, payloadLength);
            srcSpan[..payloadLength].CopyTo(payloadDst);
        }

        // Write chunk footer (CRC=0 — patched by WAL writer)
        var footerOffset = writer.WriteOffset + chunkSize - WalChunkFooter.SizeInBytes;
        var footer = new WalChunkFooter { CRC = 0 };
        MemoryMarshal.Write(writer.DataSpan[footerOffset..], in footer);

        writer.WriteOffset += chunkSize;
        writer.CurrentLsn++;
        writer.RecordIndex++;
    }
}