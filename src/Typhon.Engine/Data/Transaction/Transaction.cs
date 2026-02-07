// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;

namespace Typhon.Engine;

[PublicAPI]
[DebuggerDisplay("TSN {TSN}, State: {State}")]
public unsafe class Transaction : IDisposable
{
    private const int RandomAccessCachedPagesCount = 8;
    private const int ComponentInfosMaxCapacity = 131;

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
        /// Disposes the ChunkAccessor fields by reference to avoid struct copying.
        /// ChunkAccessor is a ~1KB struct - accessing it through a class field creates a copy.
        /// This method ensures the actual fields are disposed, not copies.
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

    private Dictionary<Type, ComponentInfoBase> _componentInfos;

    private int? _committedOperationCount;
    private int _deletedComponentCount;
    private ChangeSet _changeSet;

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

    public void Init(DatabaseEngine dbe, long tsn)
    {
        _dbe = dbe;
        _isDisposed = false;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = _dbe.MMF.CreateChangeSet();
        State = TransactionState.Created;
        TSN = tsn;

        _dbe.TransactionChain.PushHead(this);
    }

    internal void Reset()
    {
        _dbe = null;
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
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (State != TransactionState.Committed)
        {
            Rollback();
        }

        // Dispose all ChunkAccessors to release pages back to the page cache.
        // This is critical! Without this, pages remain in Shared state and cannot
        // be evicted by the clock-sweep algorithm, leading to page cache exhaustion
        // and deadlock when segments need to grow.
        // NOTE: We call DisposeAccessors() instead of accessing the fields directly
        // because ChunkAccessor is a struct - accessing it through a class field
        // would create a copy, disposing the copy but leaving the original untouched.
        foreach (var info in _componentInfos.Values)
        {
            info.DisposeAccessors();
        }
        
        _dbe.TransactionChain.Remove(this);
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

        return UpdateComponent(pk, ref Unsafe.NullRef<T>());
    }

    public bool DeleteEntity<TC1, TC2>(long pk) where TC1 : unmanaged where TC2 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref Unsafe.NullRef<TC1>());
        res &= UpdateComponent(pk, ref Unsafe.NullRef<TC2>());
        return res;
    }

    public bool DeleteEntity<TC1, TC2, TC3>(long pk) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var res = UpdateComponent(pk, ref Unsafe.NullRef<TC1>());
        res &= UpdateComponent(pk, ref Unsafe.NullRef<TC2>());
        res &= UpdateComponent(pk, ref Unsafe.NullRef<TC3>());
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
            
            using var ch = info.CompRevTableAccessor.GetChunkHandle(compRevInfo.CompRevTableFirstChunkId, false);
            ref var header = ref ch.AsRef<CompRevStorageHeader>();
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
            
            using var ch = info.CompRevTableAccessor.GetChunkHandle(compRevInfo.CompRevTableFirstChunkId, false);
            ref var header = ref ch.AsRef<CompRevStorageHeader>();
            return header.FirstItemRevision + (compRevInfo.CurRevisionIndex - header.FirstItemIndex);
        }
    }

    public ComponentCollectionAccessor<T> CreateComponentCollectionAccessor<T>(ref ComponentCollection<T> field) where T : unmanaged 
        => new(_changeSet, _dbe.GetComponentCollectionVSBS<T>(), ref field);

    public ReadOnlyCollectionEnumerator<T> GetReadOnlyCollectionEnumerator<T>(ref ComponentCollection<T> field) where T : unmanaged =>
        new(_dbe.GetComponentCollectionVSBS<T>(), field._bufferId);

    public int GetComponentCollectionRefCounter<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
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

    internal ChunkHandle GetCompRevStorageHeader<T>(long entity)
    {
        var ci = GetComponentInfoSingle(typeof(T));
        var result = GetCompRevTableFirstChunkId(entity, ci);
        if (result.IsFailure)
        {
            return default;
        }

        return ci.CompRevTableAccessor.GetChunkHandle(result.Value, false);
    }

    internal int GetRevisionCount<T>(long entity)
    {
        using var ch = GetCompRevStorageHeader<T>(entity);
        if (ch.IsDefault)
        {
            return -1;
        }

        return ch.AsRef<CompRevStorageHeader>().ItemCount;
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
        var componentType = typeof(T);
        
        // Fetch the cached info or create it if it's the first time we've operated on this Component type
        var info = GetComponentInfo(componentType);

        // Allocate the chunk that will store the component's chunk
        var componentChunkId = info.CompContentSegment.AllocateChunk(false);

        // Allocate the component revision storage as it's a new component
        var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, componentChunkId);

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
        using var ch = info.CompContentAccessor.GetChunkHandle(componentChunkId, true);
        int compSize = info.ComponentTable.ComponentStorageSize;
        new Span<byte>(Unsafe.AsPointer(ref comp), compSize).CopyTo(ch.AsSpan().Slice(info.ComponentTable.ComponentOverhead));
    }
    
    private void CreateComponents<T>(long pk, ReadOnlySpan<T> compList) where T : unmanaged
    {
        var componentType = typeof(T);
        
        // Fetch the cached info or create it if it's the first time we've operated on this Component type
        var info = GetComponentInfo(componentType);

        for (int i = 0; i < compList.Length; i++)
        {
            // Allocate the chunk that will store the component's chunk
            var componentChunkId = info.CompContentSegment.AllocateChunk(false);

            // Allocate the component revision storage as it's a new component
            var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, componentChunkId);

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
            using var ch = info.CompContentAccessor.GetChunkHandle(componentChunkId, true);
            compList.Slice(i, 1).Cast<T, byte>().CopyTo(ch.AsSpan().Slice(info.ComponentTable.ComponentOverhead));
        }
    }
    
    private bool ReadComponent<T>(long pk, out T t) where T : unmanaged
    {
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
                // NotFound, SnapshotInvisible, or Deleted — all mean no readable component
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

        // If there is a valid component, copy its content to the destination
        t = default;
        int size = info.ComponentTable.ComponentStorageSize;
        using var handle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId, false);
        handle.AsSpan().Slice(info.ComponentTable.ComponentOverhead).CopyTo(new Span<byte>(Unsafe.AsPointer(ref t), size));

        return true;
    }
    
    private bool ReadComponents<T>(long pk, out T[] t) where T : unmanaged
    {
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
            using var handle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId, false);
            handle.AsSpan().Slice(info.ComponentTable.ComponentOverhead).Cast<byte, T>().CopyTo(destSpan.Slice(destIndex++));
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
    
    private bool UpdateComponent<T>(long pk, ref T comp) where T : unmanaged
    {
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
            ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, isDelete);
        }

        // Set up the component header
        if (!isDelete)
        {
            // Copy the component data
            using var handle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId, true);
            int componentSize = info.ComponentTable.ComponentStorageSize;
            var src = new Span<byte>(Unsafe.AsPointer(ref comp), componentSize);
            var dst = handle.AsSpan().Slice(info.ComponentTable.ComponentOverhead);
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
                        kvp.Value.VSBS.BufferAddRef(srcBufferId, ref kvp.Value.Accessor);
                    }
                }
            }
            return true;
        }

        return true;
    }

    private bool UpdateComponents<T>(long pk, ReadOnlySpan<T> compList) where T : unmanaged
    {
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
                ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, isDelete);
            }

            // Set up the component header
            if (!isDelete)
            {
                // Copy the component data
                using var handle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId, true);
                var dst = handle.AsSpan().Slice(info.ComponentTable.ComponentOverhead);
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
                            kvp.Value.VSBS.BufferAddRef(srcBufferId, ref kvp.Value.Accessor);
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
                ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, true);
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

    private Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus> GetCompRevInfoFromIndex(
        long pk, ComponentInfoSingle info, long tick)
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
        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true);
            while (enumerator.MoveNext())
            {
                ref var element = ref enumerator.Current;

                if (element.IsVoid)
                {
                    continue;
                }

                if (element.TSN > TSN)
                {
                    break;
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
            PrevRevisionIndex = prevCompRevisionIndex
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
                {
                    using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true);
                    while (enumerator.MoveNext())
                    {
                        ref var element = ref enumerator.Current;
                        if (element.TSN > TSN)
                        {
                            break;
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
                        PrevRevisionIndex = prevCompRevisionIndex
                    });
                }
            }
        } while (vsba.NextChunk());

        return true;
    }

    /// <summary>
    /// <b>THE COMPONENT COMMIT METHOD (a big one, bold upper case were mandatory!)</b>
    /// </summary>
    /// <param name="pk">The primary key of the entity the component belong to</param>
    /// <param name="info">The component info object belonging to the component type</param>
    /// <param name="compRevInfo">The cached RevInfo object, it's a ref struct of the cache's content, meaning if you mutate if, you must update the cache!</param>
    /// <param name="isRollback"><c>False</c> to commit changes, <c>true</c> to rollback them.</param>
    /// <param name="conflictSolver">If <c>null</c> we solve conflict automatically with "last wins"</param>
    /// <returns>Return <c>true</c> if the whole component is deleted (all components versions were deleted/rollbacked)</returns>
    /// <remarks>
    /// <para>
    /// If <paramref name="conflictSolver"/> is given, this method can be called twice, first time to build the conflict list, second time to actually commit
    /// the changes.
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
        using var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor);
        var lastCommitRevisionIndex = compRev.LastCommitRevisionIndex;
        var elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

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
            // Get now the ChunkId of the component revision corresponding to unchanged data (the one before our transaction started), because
            //  PrevCompContentChunkId could be replaced by the committing revision if there is a conflict
            var readCompChunkId = compRevInfo.PrevCompContentChunkId;

            // BuildPhase: do we have a conflict that requires us to create a new revision?
            var hasConflict = (conflictSolver?.IsBuildPhase ?? true) && (lastCommitRevisionIndex >= compRevInfo.CurRevisionIndex);
            if (hasConflict)
            {
                // Record conflict for observability
                _dbe?.RecordConflict();
                // Create a new revision
                ComponentRevisionManager.AddCompRev(info, ref compRevInfo, TSN, false);
                
                // Copy the revision we are dealing with to the new one (the whole data, indices + content)
                var dstChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
                var srcChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId);
                var sizeToCopy = info.ComponentTable.ComponentTotalSize;
                new Span<byte>(srcChunk, sizeToCopy).CopyTo(new Span<byte>(dstChunk, sizeToCopy));

                // Update the indexInChunk and curElements to point to the new revision
                elementHandle.Dispose();
                elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
            }
            
            // Do we have a conflict to record?
            if (hasConflict && conflictSolver != null)
            {
                using var lastCommitHandle = compRev.GetRevisionElement(lastCommitRevisionIndex);

                var overhead = info.ComponentTable.ComponentOverhead;
                var readChunk = info.CompContentAccessor.GetChunkAddress(readCompChunkId) + overhead;
                var committingChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId) + overhead;
                var toCommitChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead;
                var committedChunk = info.CompContentAccessor.GetChunkAddress(lastCommitHandle.Element.ComponentChunkId) + overhead;
                
                conflictSolver.AddEntry(pk, info, readChunk, committedChunk, committingChunk, toCommitChunk);
            }

            // We are either in the build phase with no conflict (or latest wins) or in the commit phase
            else
            {
                // Update the indices (PK and secondary), the revision will be indexed, but as long as the CompRevTransactionIsolatedFlag flag is set,
                //  it won't be visible to queries
                // Skip index updates for deleted components (CurCompContentChunkId == 0)
                if (compRevInfo.CurCompContentChunkId != 0)
                {
                    UpdateIndices(pk, info, compRevInfo, readCompChunkId);
                }

                // Set the TSN of the revision to the transaction's one, removing the Isolation flag
                elementHandle.Commit(TSN);

                // Update Last Commit Revision Index
                compRev.SetLastCommitRevisionIndex(Math.Max(lastCommitRevisionIndex, compRevInfo.CurRevisionIndex));
            }
        }

        elementHandle.Dispose();

        // If this transaction is the oldest (the tail), we can remove the previous revision (if any), it is also the right place and time to clean up void
        //  revisions (the entry of a rolled back commit)
        _dbe.TransactionChain.Control.EnterSharedAccess(ref WaitContext.Null);
        var isTail = _dbe.TransactionChain.Tail == this;
        long nextMinTSN = isTail ? _dbe.TransactionChain.Tail.Next?.TSN ?? _dbe.TransactionChain.NextFreeId : 0;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        if (isTail)
        {
            var isDeleted = ComponentRevisionManager.CleanUpUnusedEntries(info, ref compRevInfo, ref compRevTableAccessor, nextMinTSN);
            dirtyFirstChunk = true;

            if (isDeleted)
            {
                // For AllowMultiple components, we can't easily remove individual entries from the PK index buffer
                // without tracking elementId. Skip cleanup for AllowMultiple to avoid corrupting the index.
                // TODO: Implement proper cleanup for AllowMultiple using elementId tracking
                if (info is ComponentInfoMultiple)
                {
                    // Don't free the revision chain - the buffer still references it
                    // The entry will show as deleted when read (ComponentChunkId == 0)
                }
                else
                {
                    // Remove the index for single components
                    var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
                    info.PrimaryKeyIndex.Remove(pk, out _, ref accessor);
                    accessor.Dispose();

                    revTableSegment.FreeChunk(firstChunkId);
                    return true;
                }
            }
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
            using var prevHandle = info.CompContentAccessor.GetChunkHandle(prevCompChunkId);
            using var curHandle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId);
            var prev = prevHandle.Address;
            var cur = curHandle.Address;
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

    public bool Rollback()
    {
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

        using var activity = TyphonActivitySource.StartActivity("Transaction.Rollback");
        activity?.SetTag(TyphonSpanAttributes.TransactionTsn, TSN);
        activity?.SetTag(TyphonSpanAttributes.TransactionComponentCount, _componentInfos.Count);

        // Get the minimum tick of all transactions because we'll remove component versions that are older
        var context = new CommitContext { IsRollback = true };

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

        // New state
        State = TransactionState.Rollbacked;
        activity?.SetTag(TyphonSpanAttributes.TransactionStatus, "rolledback");
        _dbe?.RecordRollback();
        return true;
    }

    [PublicAPI]
    public delegate void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver);

    [ThreadStatic]
    private static ConcurrencyConflictSolver ThreadLocalConflictSolver;
    
    private static ConcurrencyConflictSolver GetConflictSolver()
    {
        if (ThreadLocalConflictSolver == null)
        {
            ThreadLocalConflictSolver = new ConcurrencyConflictSolver();
        }
        else
        {
            ThreadLocalConflictSolver.Reset();
        }
        return ThreadLocalConflictSolver;
    }
    
    [PublicAPI]
    public class ConcurrencyConflictSolver
    {
        private const int MaxEntryCountToKeep = 1024;
        
        internal ConcurrencyConflictSolver()
        {
            _entries = new List<Entry>(16);
            IsBuildPhase = true;
        }
        
        internal void Reset()
        {
            if (_entries.Capacity > MaxEntryCountToKeep)
            {
                _entries = new List<Entry>(16);
            }
            else
            {
                _entries.Clear();
            }
            IsBuildPhase = true;
        }
        
        internal bool IsBuildPhase { get; set; }

        public int EntryCount => _entries.Count;
        public ref Entry this[int index] => ref CollectionsMarshal.AsSpan(_entries)[index];
        
        internal void AddEntry(long pk, ComponentInfoBase info, byte* readData, byte* committedData, byte* committingData, byte* toCommitData) => 
            _entries.Add(new Entry(pk, info, readData, committedData, committingData, toCommitData));

        private List<Entry> _entries;
        
        [PublicAPI]
        public struct Entry
        {
            private byte* _readData;
            private byte* _committedData;
            private byte* _committingData;
            private byte* _toCommitData;
            private ComponentInfoBase _info;
            public long PrimaryKey { get; private set; }
            public Type ComponentType => _info.ComponentTable.Definition.POCOType;
            public DBComponentDefinition ComponentDefinition => _info.ComponentTable.Definition;

            public void TakeRead<T>() where T : unmanaged => ToCommitData<T>() = ReadData<T>();
            public void TakeCommitted<T>() where T : unmanaged => ToCommitData<T>() = CommittedData<T>();
            public void TakeCommitting<T>() where T : unmanaged => ToCommitData<T>() = CommittingData<T>();

            public ref T ReadData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_readData);
            public ref T CommittedData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_committedData);
            public ref T CommittingData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_committingData);
            public ref T ToCommitData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_toCommitData);
            internal Entry(long pk, ComponentInfoBase info, byte* readData, byte* committedData, byte* committingData, byte* toCommitData)
            {
                PrimaryKey = pk;
                _readData = readData;
                _committedData = committedData;
                _committingData = committingData;
                _toCommitData = toCommitData;
                _info = info;

                // Default is last revision wins, so we copy the committing data to the toCommit data
                var componentSize = info.ComponentTable.ComponentStorageSize;
                new Span<byte>(_committingData, componentSize).CopyTo(new Span<byte>(_toCommitData, componentSize));
            }
        }
    }

    internal ref struct CommitContext
    {
        public long PrimaryKey;
        public ComponentInfoBase Info;
        public ref ComponentInfoBase.CompRevInfo CompRevInfo;
        public ConcurrencyConflictSolver Solver;
        public bool IsRollback;
    }
    
    public bool Commit(ConcurrencyConflictHandler handler = null)
    {
        // Nothing to do if the transaction is empty
        if (State is TransactionState.Created)
        {
            return true;
        }

        // Can't commit a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed)
        {
            return false;
        }

        using var activity = TyphonActivitySource.StartActivity("Transaction.Commit");
        activity?.SetTag(TyphonSpanAttributes.TransactionTsn, TSN);
        activity?.SetTag(TyphonSpanAttributes.TransactionComponentCount, _componentInfos.Count);

        var startTicks = Stopwatch.GetTimestamp();

        var conflictSolver = handler != null ? GetConflictSolver() : null;
        var context = new CommitContext { IsRollback = false, Solver = conflictSolver };
        var hasConflict = false;

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

        // Check if any conflicts were detected (recorded via _dbe.RecordConflict() in CommitComponent)
        // Note: We track conflicts at the engine level, not per-commit, so we rely on the conflict solver
        if (conflictSolver != null && conflictSolver.EntryCount > 0)
        {
            hasConflict = true;
        }

        activity?.SetTag(TyphonSpanAttributes.TransactionConflictDetected, hasConflict);
        activity?.SetTag(TyphonSpanAttributes.TransactionStatus, "committed");

        // New state
        State = TransactionState.Committed;

        // Record commit duration for observability
        var elapsedUs = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;
        _dbe?.RecordCommitDuration(elapsedUs);

        return true;
    }
}