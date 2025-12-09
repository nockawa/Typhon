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
[DebuggerDisplay("Id: {Id}, State: {State}, Creation {TransactionDateTime.ToString(\"yyyy-MM-ddTHH:mm:ss.fffff\")}")]
public unsafe class Transaction : IDisposable
{
    private const int RandomAccessCachedPagesCount = 8;
    private const int ComponentInfosMaxCapacity = 131;
    
    internal struct ComponentData
    {
        public ComponentData(Type type, void* data)
        {
            Type = type;
            Data = data;
        }
        public readonly Type Type;
        public readonly void* Data;
    }

    internal class ComponentInfo
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

        public ComponentTable ComponentTable;
        public ChunkBasedSegment CompContentSegment;
        public ChunkBasedSegment CompRevTableSegment;
        public LongSingleBTree PrimaryKeyIndex;
        public ChunkRandomAccessor CompContentAccessor;
        public ChunkRandomAccessor CompRevTableAccessor;
        public Dictionary<long, CompRevInfo> CompRevInfoCache;
    }

    public enum TransactionState
    {
        Invalid = 0,
        Created,            // New object, no operation done yet
        InProgress,         // At least one operation added to the transaction
        Rollbacked,         // Was rollbacked by the user or during dispose
        Committed           // Was committed by the user
    }

    // Transaction acts as a single point in time for queries, this point in time is the construction datetime.
    public long TransactionTick { get; private set; }
    public DateTime TransactionDateTime => new DateTime(TransactionTick);

    public TransactionState State { get; private set; }
    private bool _isDisposed;
    private DatabaseEngine _dbe;

    private Dictionary<Type, ComponentInfo> _componentInfos;

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
                    count += componentInfo.CompRevInfoCache.Count;
                }
                _committedOperationCount = count + _deletedComponentCount;
            }
            return _committedOperationCount.Value;
        }
    }
    
    public int Id { get; private set; }

    public Transaction()
    {
        _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
    }

    public void Init(DatabaseEngine dbe, int id)
    {
        _dbe = dbe;
        _isDisposed = false;
        TransactionTick = DateTime.UtcNow.Ticks;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = _dbe.MMF.CreateChangeSet();
        State = TransactionState.Created;
        Id = id;

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
            _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
        }
        // Don't touch _isDisposed on purpose
        
        TransactionTick = 0;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _changeSet = null;
        State = TransactionState.Invalid;
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

        _dbe.TransactionChain.Remove(this);
        _isDisposed = true;
    }

    public long CreateEntity<T>(ref T t) where T : unmanaged 
        => CreateEntity(new ComponentData(typeof(T), Unsafe.AsPointer(ref t)));
    public long CreateEntity<TC1, TC2>(ref TC1 t, ref TC2 u) where TC1 : unmanaged where TC2 : unmanaged
        => CreateEntity(new ComponentData(typeof(TC1), Unsafe.AsPointer(ref t)), new ComponentData(typeof(TC2), Unsafe.AsPointer(ref u)));
    public long CreateEntity<TC1, TC2, TC3>(ref TC1 t, ref TC2 u, ref TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
        => CreateEntity(new ComponentData(typeof(TC1), Unsafe.AsPointer(ref t)), new ComponentData(typeof(TC2), Unsafe.AsPointer(ref u)), new ComponentData(typeof(TC3), Unsafe.AsPointer(ref v)));

    public bool ReadEntity<T>(long pk, out T t) where T : unmanaged
    {
        t = default;
        return ReadEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)));
    }

    public bool ReadEntity<TC1, TC2>(long pk, out TC1 t, out TC2 u) where TC1 : unmanaged where TC2 : unmanaged
    {
        t = default;
        u = default;
        return ReadEntity(pk, new ComponentData(typeof(TC1), Unsafe.AsPointer(ref t)), new ComponentData(typeof(TC2), Unsafe.AsPointer(ref u)));
    }

    public bool ReadEntity<TC1, TC2, TC3>(long pk, out TC1 t, out TC2 u, out TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
    {
        t = default;
        u = default;
        v = default;
        return ReadEntity(pk, new ComponentData(typeof(TC1), Unsafe.AsPointer(ref t)), new ComponentData(typeof(TC2), Unsafe.AsPointer(ref u)), new ComponentData(typeof(TC3), Unsafe.AsPointer(ref v)));
    }

    public bool UpdateEntity<T>(long pk, ref T t) where T : unmanaged 
        => UpdateEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)));

    public bool UpdateEntity<TC1, TC2>(long pk, ref TC1 t, ref TC2 u) where TC1 : unmanaged where TC2 : unmanaged 
        => UpdateEntity(pk, new ComponentData(typeof(TC1), Unsafe.AsPointer(ref t)), new ComponentData(typeof(TC2), Unsafe.AsPointer(ref u)));

    public bool UpdateEntity<TC1, TC2, TC3>(long pk, ref TC1 t, ref TC2 u, ref TC3 v) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged 
        => UpdateEntity(pk, new ComponentData(typeof(TC1), Unsafe.AsPointer(ref t)), new ComponentData(typeof(TC2), Unsafe.AsPointer(ref u)), new ComponentData(typeof(TC3), Unsafe.AsPointer(ref v)));

    public bool DeleteEntity<T>(long pk) where T : unmanaged
        => UpdateEntity(pk, new ComponentData(typeof(T), null));

    public bool DeleteEntity<TC1, TC2>(long pk) where TC1 : unmanaged where TC2 : unmanaged
        => UpdateEntity(pk, new ComponentData(typeof(TC1), null), new ComponentData(typeof(TC2), null));

    public bool DeleteEntity<TC1, TC2, TC3>(long pk) where TC1 : unmanaged where TC2 : unmanaged where TC3 : unmanaged
        => UpdateEntity(pk, new ComponentData(typeof(TC1), null), new ComponentData(typeof(TC2), null), new ComponentData(typeof(TC3), null));

    public int GetComponentRevision<T>(long pk) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        if (!info.CompRevInfoCache.TryGetValue(pk, out var compRevInfo))
        {
            return -1;
        }

        using var ch = info.CompRevTableAccessor.GetChunkHandle(compRevInfo.CompRevTableFirstChunkId, false);
        ref var header = ref ch.AsRef<CompRevStorageHeader>();
        return header.FirstItemRevision + (compRevInfo.CurRevisionIndex - header.FirstItemIndex);
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
        var ci = GetComponentInfo(typeof(T));
        if (!GetCompRevTableFirstChunkId(entity, ci, out var firstChunkId))
        {
            return default;
        }
        
        return ci.CompRevTableAccessor.GetChunkHandle(firstChunkId, false);
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

    private ComponentInfo GetComponentInfo(Type componentType)
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

        info = new ComponentInfo
        {
            ComponentTable          = ct,
            CompContentSegment      = ct.ComponentSegment,
            CompRevTableSegment     = ct.CompRevTableSegment,
            PrimaryKeyIndex         = ct.PrimaryKeyIndex,
            CompContentAccessor     = ct.ComponentSegment.CreateChunkRandomAccessor(RandomAccessCachedPagesCount, _changeSet),
            CompRevTableAccessor    = ct.CompRevTableSegment.CreateChunkRandomAccessor(RandomAccessCachedPagesCount, _changeSet),
            CompRevInfoCache        = new Dictionary<long, ComponentInfo.CompRevInfo>()
        };

        _componentInfos.Add(componentType, info);

        return info;
    }

    private long CreateEntity(params ComponentData[] data)
    {
        if (State > TransactionState.InProgress)
        {
            return -1;
        }
        State = TransactionState.InProgress;

        var pk = _dbe.GetNewPrimaryKey();
        var compTick = DateTime.UtcNow.Ticks;

        for (int i = 0; i < data.Length; i++)
        {
            var componentType = data[i].Type;

            // Fetch the cached info or create it if it's the first time we operate on this Component type
            var info = GetComponentInfo(componentType);

            // Allocate the chunk that will store the component's chunk
            var componentChunkId = info.CompContentSegment.AllocateChunk(false);

            // Allocate the component revision storage as it's a new component
            var compRevChunkId = AllocCompRevStorage(info, compTick, componentChunkId);

            // We might want to access this component again in the Transaction to let's cache the PK/CompRev
            info.CompRevInfoCache.Add(pk, new ComponentInfo.CompRevInfo
            {
                Operations                  = ComponentInfo.OperationType.Created,
                PrevCompContentChunkId      = 0,
                PrevRevisionIndex           = -1,
                CurCompContentChunkId       = componentChunkId, 
                CompRevTableFirstChunkId    = compRevChunkId, 
                CurRevisionIndex            = 0
            });

            // Copy the component data
            using var ch = info.CompContentAccessor.GetChunkHandle(componentChunkId, true);
            int compSize = info.ComponentTable.ComponentStorageSize;
            new Span<byte>(data[i].Data, compSize).CopyTo(ch.AsSpan().Slice(info.ComponentTable.ComponentOverhead));
        }

        return pk;
    }

    private bool ReadEntity(long pk, params ComponentData[] data)
    {
        int notFoundCount = 0;
        for (int i = 0; i < data.Length; i++)
        {
            var componentType = data[i].Type;
            var info = GetComponentInfo(componentType);

            // Check if we already have this component in the cache
            ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.CompRevInfoCache, pk, out var exists);
            if (!exists)
            {
                // Couldn't find in the cache, get it from the index
                if (!GetCompRevInfoFromIndex(pk, info, TransactionTick, out compRevInfo))
                {
                    // No component for this PK/Tick
                    ++notFoundCount;
                    continue;
                }

                compRevInfo.Operations |= ComponentInfo.OperationType.Read;
            }

            // If there is a valid component, copy its content to the destination
            if (compRevInfo.CurCompContentChunkId != 0)
            {
                int size = info.ComponentTable.ComponentStorageSize;
                using var handle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId, false);
                handle.AsSpan().Slice(info.ComponentTable.ComponentOverhead).CopyTo(new Span<byte>(data[i].Data, size));
            }
            else
            {
                ++notFoundCount;
            }
        }

        return notFoundCount == 0;
    }

    private bool UpdateEntity(long pk, params ComponentData[] data)
    {
        if (State > TransactionState.InProgress)
        {
            return false;
        }
        State = TransactionState.InProgress;

        var componentTick = DateTime.UtcNow.Ticks;
        for (int i = 0; i < data.Length; i++)
        {
            var componentType = data[i].Type;
            var isDelete = data[i].Data == null;

            // Fetch the cached info or create it if it's the first time we operate on this Component type
            var info = GetComponentInfo(componentType);

            // Check if the component is in the cache (meaning we already made an operation on it in this transaction)
            ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.CompRevInfoCache, pk, out var compRevCached);
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
                }
            }

            // No component in cache
            else
            {
                // Fetch the cache by getting the revision closest to the transaction tick, if we fail it means there's no revision, so no component for this
                //  PK, we return false
                if (!GetCompRevInfoFromIndex(pk, info, TransactionTick, out compRevInfo))
                {
                    return false;
                }
            }

            // Update the operation types
            compRevInfo.Operations |= (isDelete ? ComponentInfo.OperationType.Deleted : ComponentInfo.OperationType.Updated);

            // First mutating operation on this component in this transaction: create a new component version
            if ((!compRevCached) || ((compRevInfo.Operations & ComponentInfo.OperationType.Read) != 0))
            {
                // Add a new component version for the current component, if there is no data it means we are delete the component, we still
                //  need to add a new version with an empty CurCompContentChunkId
                AddCompRev(info, ref compRevInfo, componentTick, isDelete);
            }

            // Set up the component header
            if (!isDelete)
            {
                // Copy the component data
                using var handle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId, true);
                int componentSize = info.ComponentTable.ComponentStorageSize;
                var src = new Span<byte>(data[i].Data, componentSize);
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
                            kvp.Value.Item1.BufferAddRef(srcBufferId, kvp.Value.Item2);
                        }
                    }
                }
            }
        }

        return true;
    }

    private int AllocCompRevStorage(ComponentInfo info, long tick, int firstComponentChunkId)
    {
        var chunkId = info.CompRevTableSegment.AllocateChunk(false);
        using var handle = info.CompRevTableAccessor.GetChunkHandle(chunkId, true);
        var stream = handle.AsStream();
        
        ref var header = ref stream.PopRef<CompRevStorageHeader>();
        
        // Initialize the header
        header.NextChunkId = 0;
        header.FirstItemRevision = 1;
        header.Control = default;
        header.FirstItemIndex = 0;
        header.ItemCount = 1;
        header.ChainLength = 1;
        header.LastCommitRevisionIndex = -1;

        // Initialize the first element
        ref var chunkElements = ref stream.PopRef<CompRevStorageElement>();
        chunkElements.DateTime = PackedDateTime48.FromDateTimeTicks(tick);
        chunkElements.IsolationFlag = true;                                  // Isolate this revision from the rest of the database (other transactions)
        chunkElements.ComponentChunkId = firstComponentChunkId;

        return chunkId;
    }

    private bool GetCompRevTableFirstChunkId(long pk, ComponentInfo info, out int firstChunkId)
    {
        using var accessor = info.PrimaryKeyIndex.Segment.CreateChunkRandomAccessor(8, _changeSet);
        return info.PrimaryKeyIndex.TryGet(pk, out firstChunkId, accessor);
    }

    private static int ComputeRevElementCount(int chainLength) => ComponentTable.CompRevCountInRoot + ((chainLength - 1) * ComponentTable.CompRevCountInNext);
    private void AddCompRev(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, long tick, bool isDelete)
    {
        var compRevTableAccessor = info.CompRevTableAccessor;
        var compContent = info.CompContentSegment;

        using var handle = compRevTableAccessor.GetChunkHandle(compRevInfo.CompRevTableFirstChunkId, true);
        var stream = handle.AsStream();

        // Get the chunk of the header
        ref var firstHeader = ref stream.PopRef<CompRevStorageHeader>();

        // Enter exclusive access for the Revision Table
        firstHeader.Control.EnterExclusiveAccess();

        // Check if we need to add one more chunk to the chain
        if (ComputeRevElementCount(firstHeader.ChainLength) == firstHeader.ItemCount)
        {
            GrowChain(info, ref compRevInfo, ref firstHeader);
        }

        // Add our new entry
        var newRevIndex = (short)(firstHeader.FirstItemIndex + firstHeader.ItemCount);
        var indexInChunk = GetRevisionLocation(compRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, newRevIndex, out var curChunkId);

        Span<CompRevStorageElement> curChunkElements;
        ChunkHandle curChunkHandle = default;

        // Still in the first chunk? The elements are right after
        if (compRevInfo.CompRevTableFirstChunkId == curChunkId)
        {
            curChunkElements = stream.PopSpan<CompRevStorageElement>(ComponentTable.CompRevCountInRoot);
        }
        
        // In another chunk, the subsequent ones have a one int header (the ID of the next chunk in the chain), then the elements
        else
        {
            curChunkHandle = compRevTableAccessor.GetChunkHandle(curChunkId, true);
            curChunkElements = curChunkHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
        } 

        // Allocate a new component
        var componentChunkId = isDelete ? 0 : compContent.AllocateChunk(false);

        // Add our new entry
        curChunkElements[indexInChunk].DateTime = PackedDateTime48.FromDateTimeTicks(tick);
        curChunkElements[indexInChunk].IsolationFlag = true;
        curChunkElements[indexInChunk].ComponentChunkId = componentChunkId;

        // Update the compRevInfo
        compRevInfo.PrevCompContentChunkId = compRevInfo.CurCompContentChunkId;
        compRevInfo.PrevRevisionIndex = compRevInfo.CurRevisionIndex;
        compRevInfo.CurCompContentChunkId = componentChunkId;
        compRevInfo.CurRevisionIndex = newRevIndex;

        // One more item, update the header
        firstHeader.ItemCount++;
        
        // Cleanups
        curChunkHandle.Dispose();
        firstHeader.Control.ExitExclusiveAccess();
    }

    private void GrowChain(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, ref CompRevStorageHeader firstHeader)
    {
        var compRevTableAccessor = info.CompRevTableAccessor;
        var compRevTable = info.CompRevTableSegment;
        
        // Special case, the first revision is in the first chunk, we need to walk to the end of the chain and add a new chunk there
        if (firstHeader.FirstItemIndex < ComponentTable.CompRevCountInRoot)
        {
            var enumerator = new RevisionEnumerator(compRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, false, false);
            enumerator.StepToChunk(firstHeader.ChainLength - 1, false);         // Walk to the last chunk in the chain
            enumerator.NextChunkId = compRevTable.AllocateChunk(true);          // Allocated, clear content to make sure the next chunk ID is 0, set as next
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);
            firstHeader.ChainLength++;
        }
        else
        {
            // Locate the first index in the chain, we add a chunk just before it
            var (firstChunkInChain, firstItemIndexInChunk) = CompRevStorageHeader.GetRevisionLocation(firstHeader.FirstItemIndex);
            var enumerator = new RevisionEnumerator(compRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, false, false);
            enumerator.StepToChunk(firstChunkInChain-1, false);                 // In a circular buffer, the chunk before the first is the last one

            // Get the ID of the first chunk in the chain
            var firstChunkIndexInChain = enumerator.NextChunkId;
            
            // Add a new chunk after the last in the chain
            var newChunkId = compRevTable.AllocateChunk(true);              // Clear content to make sure the next chunk ID is 0
            enumerator.NextChunkId = newChunkId;
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);

            // Copy the elements from the first chunk to the new chunk
            using var newChunkHandle = compRevTableAccessor.GetChunkHandle(newChunkId, true);
            using var firstChunkHandle = compRevTableAccessor.GetChunkHandle(firstChunkIndexInChain, true);
            var newChunkElements = newChunkHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            var firstChunkElements = firstChunkHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            firstChunkElements.Slice(0, firstItemIndexInChunk).CopyTo(newChunkElements);
            
            firstHeader.ChainLength++;                                              // One more item in the chain
            firstHeader.FirstItemIndex += (short)ComponentTable.CompRevCountInNext; // We added a chunk before, the first item index gets shifted
        }
        compRevTableAccessor.DirtyChunk(compRevInfo.CompRevTableFirstChunkId);
    }    
    
    [PublicAPI]
    internal ref struct RevisionEnumerator : IDisposable
    {
        private readonly ChunkRandomAccessor _compRevTableAccessor;
        private ChunkHandle _firstChunkHandle;
        private ChunkHandle _curChunkHandle;
        private ref CompRevStorageHeader _header;
        private Span<CompRevStorageElement> _elements;
        private readonly int _firstChunkId;
        private short _itemCountLeft;
        private short _indexInChunk;
        private ref int _nextChunkId;
        private readonly bool _exclusiveAccess;
        private bool _hasLopped;
        private short _revisionIndex;

        public ref CompRevStorageHeader Header => ref _header;
        public int RevisionIndex => _revisionIndex;
        public int IndexInChunk => _indexInChunk;
        public bool HasLopped => _hasLopped;
        
        public ref CompRevStorageElement Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get
            {
                if (_itemCountLeft >= 0)
                {
                    return ref _elements[_indexInChunk];
                }
                return ref Unsafe.NullRef<CompRevStorageElement>();                
            }
        }
        
        public ref int NextChunkId => ref _nextChunkId;
        public Span<CompRevStorageElement> Elements => _elements;
        public Span<CompRevStorageElement> CurrentAsSpan => _elements.Slice(_indexInChunk, 1);
        public int CurChunkId { get; private set; }
        public bool IsFirstChunk => CurChunkId == _firstChunkId;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            if (--_itemCountLeft < 0)
            {
                return false;
            }
            
            ++_revisionIndex;
            if (++_indexInChunk == _elements.Length)
            {
                _indexInChunk = 0;
                if (!StepToChunk(1, true))
                {
                    return false;
                }
            }

            return true;
        }

        public RevisionEnumerator(ChunkRandomAccessor compRevTableAccessor, int compRevFirstChunkId, bool exclusiveAccess, bool goToFirstItem)
        {
            _compRevTableAccessor = compRevTableAccessor;
            _exclusiveAccess = exclusiveAccess;
            _firstChunkId = compRevFirstChunkId;
            _firstChunkHandle = compRevTableAccessor.GetChunkHandle(compRevFirstChunkId, false);
            _header = ref _firstChunkHandle.AsRef<CompRevStorageHeader>();
            if (!_header.Control.IsLockedByCurrentThread)
            {
                _header.Control.Enter(_exclusiveAccess);
            }
            _itemCountLeft = _header.ItemCount;
            _nextChunkId = ref _header.NextChunkId;

            _indexInChunk = goToFirstItem ? _header.FirstItemIndex : (short)0;
            if (_indexInChunk < ComponentTable.CompRevCountInRoot)
            {
                var chunkContent = _firstChunkHandle.AsSpan();
                _nextChunkId = ref chunkContent.Cast<byte, int>()[0];
                _elements = chunkContent.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
                CurChunkId = compRevFirstChunkId;
            }
            else
            {
                var (chunkIndexInChain, index) = CompRevStorageHeader.GetRevisionLocation(_indexInChunk);
                _indexInChunk = (short)index;
                StepToChunk(chunkIndexInChain, false);
            }
            --_indexInChunk;        // We pre-increment in MoveNext, so we start one before
            _revisionIndex = -1;
        }

        public bool StepToChunk(int stepCount, bool loop)
        {
            for (int i = 0; i < stepCount; i++)
            {
                _curChunkHandle.Dispose();
                _curChunkHandle = default;
                if (_nextChunkId == 0)
                {
                    if (loop)
                    {
                        CurChunkId = _firstChunkId;
                        _curChunkHandle = _compRevTableAccessor.GetChunkHandle(_firstChunkId, false);
                        var stream = _curChunkHandle.AsStream();
                        _nextChunkId = ref stream.PopRef<int>();
                        _elements = stream.PopSpan<CompRevStorageElement>(ComponentTable.CompRevCountInRoot);
                        _hasLopped = true;
                        return true;
                    }

                    CurChunkId = -1;
                    _nextChunkId = ref Unsafe.NullRef<int>();
                    _elements = Span<CompRevStorageElement>.Empty;
                    return false;
                }

                {
                    CurChunkId = _nextChunkId;
                    _curChunkHandle = _compRevTableAccessor.GetChunkHandle(_nextChunkId, false);
                    var stream = _curChunkHandle.AsStream();
                    _nextChunkId = ref stream.PopRef<int>();
                    _elements = stream.PopSpan<CompRevStorageElement>(ComponentTable.CompRevCountInNext);
                }
            }
            return true;
        }

        public void Dispose()
        {
            if (!_header.Control.IsLockedByCurrentThread)
            {
                _header.Control.Exit(_exclusiveAccess);
            }
            _firstChunkHandle.Dispose();
            _curChunkHandle.Dispose();
        }
    }

    private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out ComponentInfo.CompRevInfo compRevInfo)
    {
        var compRevTableAccessor = info.CompRevTableAccessor;

        using var accessor = info.PrimaryKeyIndex.Segment.CreateChunkRandomAccessor(8, _changeSet);
        if (!info.PrimaryKeyIndex.TryGet(pk, out var compRevFirstChunkId, accessor))
        {
            compRevInfo = default;
            return false;
        }

        var res = true;
        compRevInfo = default;
        short prevCompRevisionIndex = -1;
        short curCompRevisionIndex = -1;
        int prevCompChunkId = 0;
        int curCompChunkId = 0;
        {
            using var enumerator = new RevisionEnumerator(compRevTableAccessor, compRevFirstChunkId, false, true);
            while (enumerator.MoveNext())
            {
                ref var element = ref enumerator.Current;
                if (element.DateTime.Ticks > tick)
                {
                    break;
                }
            
                // Update the current revision (and the previous) if a valid entry (tick == 0 means a rollbacked entry) and it's not an isolated one
                if ((element.DateTime.Ticks > 0) && !element.IsolationFlag)
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
            res = false;
            goto Exit;
        }

        compRevInfo = new ComponentInfo.CompRevInfo
        {
            Operations = ComponentInfo.OperationType.Undefined,
            CompRevTableFirstChunkId = compRevFirstChunkId,
            CurCompContentChunkId = curCompChunkId,
            CurRevisionIndex = curCompRevisionIndex,
            PrevCompContentChunkId = prevCompChunkId,
            PrevRevisionIndex = prevCompRevisionIndex
        };
        
        Exit:
        compRevTableAccessor.UnpinChunk(compRevFirstChunkId);

        return res;
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
        
        var compRevTableAccessor = info.CompRevTableAccessor;
        var componentSegment = info.CompContentSegment;
        var revTableSegment = info.CompRevTableSegment;

        // Get the chunk of the header, pin it because we might access other chunk while walking the chain
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;
        var firstChunkHandle = compRevTableAccessor.GetChunkHandle(firstChunkId, true);
        ref var firstChunkHeader = ref firstChunkHandle.AsRef<CompRevStorageHeader>();
        var dirtyFirstChunk = false;

        // Get the chunk storing the revision we want to commit as well as the index of the element
        var elementHandle = GetRevisionElement(compRevTableAccessor, firstChunkId, compRevInfo.CurRevisionIndex, out var curElement);

        // Clear the entry of the transaction component revision if it's a rollback
        if (isRollback)
        {
            // If any, free the chunk storing the content
            if (compRevInfo.CurCompContentChunkId != 0)
            {
                componentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
            }
            
            // If we roll back a created component, we must delete the revision table chunk
            if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
            {
                compRevTableAccessor.UnpinChunk(firstChunkId);
                revTableSegment.FreeChunk(firstChunkId);

                // WARNING: Normal early exit, I usually don't like it, but from this point the RevTable Start chunk is gone, going on into the rest of the
                //  code would be dangerous as we have pointers that would be bad.
                return true;
            }
            
            // In case of update, mark void the revision entry we added
            if ((compRevInfo.Operations & ComponentInfo.OperationType.Updated) == ComponentInfo.OperationType.Updated)
            {
                curElement[0].DateTime = PackedDateTime48.FromPackedDateTimeTicks(0);
                curElement[0].ComponentChunkId = 0;
            }
        }
        
        // Commit the revision
        else
        {
            // Get now the ChunkId of the component revision corresponding to unchanged data (the one before our transaction started), because
            //  PrevCompContentChunkId could be replaced by the committing revision if there is a conflict
            var readCompChunkId = compRevInfo.PrevCompContentChunkId;

            // BuildPhase: do we have a conflict that requires us to create a new revision?
            var hasConflict = (conflictSolver?.IsBuildPhase ?? true) && (firstChunkHeader.LastCommitRevisionIndex >= compRevInfo.CurRevisionIndex);
            if (hasConflict)
            {
                // Create a new revision
                AddCompRev(info, ref compRevInfo, context.CommitTime.Ticks, false);
                
                // Copy the revision we are dealing with to the new one (the whole data, indices + content)
                var dstChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, dirtyPage: true);
                var srcChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId);
                var sizeToCopy = info.ComponentTable.ComponentTotalSize;
                new Span<byte>(srcChunk, sizeToCopy).CopyTo(new Span<byte>(dstChunk, sizeToCopy));

                // Update the indexInChunk and curElements to point to the new revision
                // indexInChunk = GetRevisionLocation(compRevTableAccessor, firstChunkHeader, compRevInfo.CurRevisionIndex, out curElements);
                elementHandle.Dispose();
                elementHandle = GetRevisionElement(compRevTableAccessor, firstChunkId, compRevInfo.CurRevisionIndex, out curElement);
            }
            
            // Do we have a conflict to record ?
            if (hasConflict && conflictSolver != null)
            {
                using var lastCommitHandle = GetRevisionElement(compRevTableAccessor, firstChunkId, firstChunkHeader.LastCommitRevisionIndex, 
                    out var lastCommitElement);

                var overhead = info.ComponentTable.ComponentOverhead;
                var readChunk = info.CompContentAccessor.GetChunkAddress(readCompChunkId) + overhead;
                var committingChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId) + overhead;
                var toCommitChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead;
                var committedChunk = info.CompContentAccessor.GetChunkAddress(lastCommitElement[0].ComponentChunkId) + overhead;
                
                conflictSolver.AddEntry(pk, info, readChunk, committedChunk, committingChunk, toCommitChunk);
            }

            // We are either in build phase with no conflict (or latest wins) or in commit phase
            else
            {
                // Update the indices (PK and secondary), the revision will be indexed but as long as the CompRevTransactionIsolatedFlag flag is set, it won't be
                //  visible to queries
                UpdateIndices(pk, info, compRevInfo, readCompChunkId);

                // Set the DateTime of the revision to the commit time, removing the Isolation flag
                curElement[0].DateTime = (PackedDateTime48)context.CommitTime;
                curElement[0].IsolationFlag = false;
                
                // Update Last Commit Revision Index
                firstChunkHeader.LastCommitRevisionIndex = Math.Max(firstChunkHeader.LastCommitRevisionIndex, compRevInfo.CurRevisionIndex);
            }
        }

        elementHandle.Dispose();
        
        // If this transaction is the oldest (the tail), we can remove the previous revision (if any), it is also the right place and time to clean up void
        //  revisions (the entry of a rolled back commit)
        _dbe.TransactionChain.Control.EnterSharedAccess();
        var isTail = _dbe.TransactionChain.Tail == this;
        long nextMinTick = isTail ? _dbe.TransactionChain.Tail.Next?.TransactionTick ?? DateTime.UtcNow.Ticks : 0;
        _dbe.TransactionChain.Control.ExitSharedAccess();
        
        if (isTail)
        {
            CleanUpUnusedEntries(info, ref compRevInfo, compRevTableAccessor, nextMinTick);
            dirtyFirstChunk = true;
        }

        // As we committed/rolled back the current revision, we don't need to keep track of the previous one anymore
        compRevInfo.PrevCompContentChunkId = -1;
        compRevInfo.PrevRevisionIndex = 0;
        
        // Check if we can/have to delete the whole component revision, either:
        //  - All the items are from a tick older than the required one
        //  - All are older except the last one but this is a deleted component we're committing
        //  - All are older except the last one but this is a created component we're roll-backing
        // TOFIX
        /*
        if ((itemLeftCount < 0) ||
            ((itemLeftCount == 0) && ((compRevInfo.Operations & ComponentInfo.OperationType.Deleted) != 0) && (isRollback == false)) ||
            ((itemLeftCount == 0) && ((compRevInfo.Operations & ComponentInfo.OperationType.Created) != 0) && isRollback))
        {
            Debug.Assert(curElements[compRevInfo.CompRevIndexInChunk].ComponentChunkId == 0, "Current Component Revision point to an allocated Component, should be 0.");

            // Remove the index
            using var accessor = info.PrimaryKeyIndex.Segment.CreateChunkRandomAccessor(8, _changeSet);
            info.PrimaryKeyIndex.Remove(pk, out _, accessor);

            // Free the Component Revision chain chunks
            var curChunkId = compRevInfo.CompRevTableStartChunkId;
            do
            {
                curChunkHeader = (CompRevStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
                var nextChunkIdx = curChunkHeader->NextChunkId;
                versionTableAccessor.Segment.FreeChunk(curChunkId);
                curChunkId = nextChunkIdx;
            } while (curChunkId != 0);

            res = true;
        }
        

        else */ 
        
        if (dirtyFirstChunk)
        {
            compRevTableAccessor.DirtyChunk(firstChunkId);
        }

        // Cleanups (NOT THE ONLY EXIT POINT OF THE FUNCTION, LOOK FOR THE ROLLBACK SECTION ABOVE)
        compRevTableAccessor.UnpinChunk(firstChunkId);

        return false;
    }

    private void UpdateIndices(long pk, ComponentInfo info, ComponentInfo.CompRevInfo compRevInfo, int prevCompChunkId)
    {
        // If there's a previous revision, we need to update the indices if some indexed fields changed
        var startChunkId = compRevInfo.CompRevTableFirstChunkId;
        if (prevCompChunkId != 0)
        {
            var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId, pin: true);
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
                    using var accessor = ifi.Index.Segment.CreateChunkRandomAccessor(8, _changeSet);
                    if (ifi.Index.AllowMultiple)
                    {
                        ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId,
                            accessor);
                        *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, accessor);
                    }
                    else
                    {
                        ifi.Index.Remove(&prev[ifi.OffsetToField], out var val, accessor);
                        ifi.Index.Add(&cur[ifi.OffsetToField], val, accessor);
                    }
                }
            }

            info.CompContentAccessor.UnpinChunk(prevCompChunkId);
        }

        // No previous revision, it means we're adding the first component revision, add the indices
        else
        {
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId);

            // Update the index with this new entry
            {
                using var accessor = info.PrimaryKeyIndex.Segment.CreateChunkRandomAccessor(8, _changeSet);
                info.PrimaryKeyIndex.Add(pk, startChunkId, accessor);
            }

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                using var accessor = ifi.Index.Segment.CreateChunkRandomAccessor(8, _changeSet);
                if (ifi.Index.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, accessor);
                }
                else
                {
                    ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, accessor);
                }
            }
        }
    }

    internal ref struct RevisionWalker : IDisposable
    {
        private readonly ChunkRandomAccessor _accessor;
        private readonly int _firstChunkId;
        private ChunkHandle _firstChunkHandle;
        private ChunkHandle _curChunkHandle;
        private readonly ref CompRevStorageHeader _header;
        private Span<CompRevStorageElement> _elements;
        private ref int _nextChunkId;
        private int _curChunkId;
        
        public ref CompRevStorageHeader Header => ref _header;
        public int CurChunkId => _curChunkId;
        public ref int NextChunkId => ref _nextChunkId;
        public Span<CompRevStorageElement> Elements => _elements;

        public RevisionWalker(ChunkRandomAccessor accessor, int firstChunkId)
        {
            _accessor = accessor;
            _firstChunkId = firstChunkId;
            _firstChunkHandle = accessor.GetChunkHandle(firstChunkId, false);
            _header = ref _firstChunkHandle.AsRef<CompRevStorageHeader>();
            _curChunkId = firstChunkId;
            _curChunkHandle = accessor.GetChunkHandle(firstChunkId, false);
            var stream = _curChunkHandle.AsStream();
            _nextChunkId = ref stream.PopRef<int>();
            _elements = stream.PopSpan<CompRevStorageElement>(ComponentTable.CompRevCountInRoot);
        }

        public bool Step(int stepCount, bool loop, out bool hasLopped)
        {
            hasLopped = false;
            for (int i = 0; i < stepCount; i++)
            {
                if (_nextChunkId == 0 && !loop)
                {
                    return false;
                }
                var nextChunkId = _nextChunkId;
                if (_nextChunkId == 0)
                {
                    hasLopped = true;
                    nextChunkId = _firstChunkId;
                }

                _curChunkHandle.Dispose();
                _curChunkId = nextChunkId;
                _curChunkHandle = _accessor.GetChunkHandle(nextChunkId, false);
                var stream = _curChunkHandle.AsStream();
                _nextChunkId = ref stream.PopRef<int>();
                _elements = stream.PopSpan<CompRevStorageElement>(ComponentTable.CompRevCountInNext);
            }
            return true;
        }
        
        public void Dispose()
        {
            _firstChunkHandle.Dispose();
            _curChunkHandle.Dispose();
        }
    }

    /// <summary>
    /// Clean up the revisions of a component, removing all the entries older than <paramref name="nextMinTick"/>, releasing unused component chunks and
    ///  defragmenting the revisions still being used.
    /// </summary>
    /// <param name="info">ComponentInfo object</param>
    /// <param name="compRevInfo">Component Revision Info object</param>
    /// <param name="compRevTableAccessor">The accessor</param>
    /// <param name="nextMinTick">The minimal tick to keep revisions</param>
    /// <remarks>
    /// This method walks through the chain of revision chunks and builds a new one, only the first chunk is kept.
    /// </remarks>
    private void CleanUpUnusedEntries(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, ChunkRandomAccessor compRevTableAccessor,
        long nextMinTick)
    {
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;
        using var firstChunkHandle = compRevTableAccessor.GetChunkHandle(firstChunkId, false);
        ref var firstChunkHeader = ref firstChunkHandle.AsRef<CompRevStorageHeader>();
        
        // Create a temporary chunk to store the cleaned up content of the first chunk (we can't overwrite the first chunk right away)
        Span<byte> tempChunk = stackalloc byte[ComponentTable.CompRevChunkSize];
        tempChunk.Clear();
        tempChunk.Split(out Span<CompRevStorageHeader> tempFirstHeader, out Span<CompRevStorageElement> tempElements);
        tempFirstHeader[0].ChainLength = 1;
        var curNextChunkId = tempChunk.Slice(0, sizeof(int)).Cast<byte, int>();
        var curDestElements = tempElements;
        var curDestIndex = 0;
        var curDestIndexInChunk = 0;
        var skipCount = 0;
        var ct = info.ComponentTable;
        var hasCollections = ct.HasCollections;

        ChunkHandle newChunkHandle = default;
        {
            using var enumerator = new RevisionEnumerator(compRevTableAccessor, firstChunkId, false, true);
            var prevChunkId = enumerator.IndexInChunk == 0 ? enumerator.CurChunkId : 0;
            var maxSkipCount = firstChunkHeader.ItemCount;
            var skipping = true;
            while (enumerator.MoveNext())
            {
                bool changedChunk = (enumerator.CurChunkId != prevChunkId) && (prevChunkId != 0);
                if (changedChunk)
                {
                    // Remove the previous chunk if we can
                    if (prevChunkId != 0 && !enumerator.IsFirstChunk)
                    {
                        if (hasCollections)
                        {
                            foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                            {
                                var bufferId = info.CompContentAccessor.GetChunkAsReadOnlySpan(prevChunkId).Slice(kvp.Key).Cast<byte, int>()[0];
                                kvp.Value.Item1.BufferRelease(bufferId, kvp.Value.Item2);
                            }
                        }

                        info.CompContentSegment.FreeChunk(prevChunkId);
                    }
                    prevChunkId = enumerator.CurChunkId;
                }

                if (skipping)
                {
                    // If the entry is older than the minimum tick, or we reached the maximum number of entries we can skip,
                    //  we can remove it and skip to the next one
                    if ((--maxSkipCount > 0) && (enumerator.Current.DateTime.Ticks < nextMinTick))
                    {
                        // Check if there's a component chunk to free
                        var revChunkId = enumerator.Current.ComponentChunkId;
                        if (revChunkId != 0)
                        {
                            if (hasCollections)
                            {
                                foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                                {
                                    var bufferId = info.CompContentAccessor.GetChunkAsReadOnlySpan(revChunkId).Slice(kvp.Key).Cast<byte, int>()[0];
                                    kvp.Value.Item1.BufferRelease(bufferId, kvp.Value.Item2);
                                }
                            }

                            info.CompContentSegment.FreeChunk(revChunkId);
                        }
            
                        // Clear the entry
                        enumerator.CurrentAsSpan.Clear();
                    
                        skipCount++;
                        continue;
                    }
                
                    // We stop skipping at the first valid entry
                    skipping = false;
                }
            
                curDestElements[curDestIndexInChunk++] = enumerator.Current;            // Copy the revision to the destination
                tempFirstHeader[0].ItemCount++;                                         // Update the item count
                if (!enumerator.Current.IsolationFlag)                                  // Update the last committed revision index if this is not an isolated entry
                {
                    tempFirstHeader[0].LastCommitRevisionIndex = (short)curDestIndex;
                }
                curDestIndex++;                                                         // One more item in the destination
            
                // If the current chunk is full, allocate a new one
                if (curDestIndex == curDestElements.Length)
                {
                    curDestIndexInChunk = 0;                                            // Reset the index in chunk
                    tempFirstHeader[0].ChainLength++;                                   // One more chunk in the chain
                    var newChunkId = info.CompContentSegment.AllocateChunk(false);  // Allocate a new chunk
                    curNextChunkId[0] = newChunkId;                                     // Set the next chunk ID of the current chunk
                    if (!newChunkHandle.IsDefault)                                      // Release the handle on the previous chunk, if any
                    {
                        newChunkHandle.Dispose();
                    }
                    newChunkHandle = compRevTableAccessor.GetChunkHandle(newChunkId, true);     // Get the handle of the new chunk
                    newChunkHandle.AsSpan().Split(out curNextChunkId, out curDestElements);     // Update our "cur" variables
                }
            }
        }
        
        tempFirstHeader[0].FirstItemRevision = firstChunkHeader.FirstItemRevision + skipCount;
        if (!newChunkHandle.IsDefault)
        {
            newChunkHandle.Dispose();
        }
        var tempControl = firstChunkHeader.Control;
        tempChunk.CopyTo(firstChunkHandle.AsSpan());
        firstChunkHeader.Control = tempControl;

        compRevInfo.CurRevisionIndex = 0;   // As we defrag and move everything to the beginning of the chunk, the first revision is always at 0
    }

    private ChunkHandle GetRevisionElement(ChunkRandomAccessor accessor, int firstChunkId, short revisionIndex, out Span<CompRevStorageElement> element)
    {
        var firstHandle = accessor.GetChunkHandle(firstChunkId, false);
        ref var firstHeader = ref firstHandle.AsRef<CompRevStorageHeader>();
        if (revisionIndex < ComponentTable.CompRevCountInRoot)
        {
            element = firstHandle.AsSpan().Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>().Slice(revisionIndex, 1);
            return firstHandle;
        }

        var (chunkIndexInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var nextChunkId = firstHeader.NextChunkId;

        var curHandle = accessor.GetChunkHandle(nextChunkId, false);
        var useLock = !firstHeader.Control.IsLockedByCurrentThread;
        if (useLock)
        {
            firstHeader.Control.EnterSharedAccess();
        }
        while (--chunkIndexInChain >= 0)
        {
            curHandle.Dispose();
            curHandle = accessor.GetChunkHandle(nextChunkId, false);
            nextChunkId = curHandle.AsRef<int>();
        }
        element = curHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>().Slice(indexInChunk, 1);
        
        if (useLock)
        {
            firstHeader.Control.ExitSharedAccess();
        }

        firstHandle.Dispose();
        return curHandle;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private short GetRevisionLocation(ChunkRandomAccessor accessor, int firstChunkId, short revisionIndex, out int resChunkId)
    {
        if (revisionIndex < ComponentTable.CompRevCountInRoot)
        {
            resChunkId = firstChunkId;
            return revisionIndex;
        }

        var (chunkIndexInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var header = (CompRevStorageHeader*)accessor.GetChunkAddress(firstChunkId);
        resChunkId = header->NextChunkId;

        var first = header;
        var useLock = !first->Control.IsLockedByCurrentThread;
        if (useLock)
        {
            first->Control.EnterSharedAccess();
        }
        while (--chunkIndexInChain != 0)
        {
            resChunkId = *(int*)accessor.GetChunkAddress(resChunkId);
        }
        if (useLock)
        {
            first->Control.ExitSharedAccess();
        }

        return (short)indexInChunk;
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

        // Get the minimum tick of all transactions because we'll remove component version that are older
        var context = new CommitContext { IsRollback = true, CommitTime = DateTime.UtcNow};

        var deletedComponents = new List<long>();
        // Process every Component Type and their components
        foreach (var componentInfo in _componentInfos.Values)
        {
            context.Info = componentInfo;
            deletedComponents.Clear();

            foreach (var key in componentInfo.CompRevInfoCache.Keys)
            {
                context.PrimaryKey = key;
                context.CompRevInfo = ref CollectionsMarshal.GetValueRefOrNullRef(componentInfo.CompRevInfoCache, key);

                // Nothing to commit if we only read the component
                if (context.CompRevInfo.Operations == ComponentInfo.OperationType.Read)
                {
                    continue;
                }

                if (CommitComponent(ref context))
                {
                    deletedComponents.Add(context.PrimaryKey);
                }
            }

            foreach (var pk in deletedComponents)
            {
                componentInfo.CompRevInfoCache.Remove(pk);
                _deletedComponentCount++;
            }
        }

        // New state
        State = TransactionState.Rollbacked;
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
        
        internal void AddEntry(long pk, ComponentInfo info, byte* readData, byte* committedData, byte* committingData, byte* toCommitData) => 
            _entries.Add(new Entry(pk, info, readData, committedData, committingData, toCommitData));

        private List<Entry> _entries;
        
        [PublicAPI]
        public struct Entry
        {
            private byte* _readData;
            private byte* _committedData;
            private byte* _committingData;
            private byte* _toCommitData;
            private ComponentInfo _info;
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
            internal Entry(long pk, ComponentInfo info, byte* readData, byte* committedData, byte* committingData, byte* toCommitData)
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
        public ComponentInfo Info;
        public ref ComponentInfo.CompRevInfo CompRevInfo;
        public ConcurrencyConflictSolver Solver;
        public bool IsRollback;
        public DateTime CommitTime;
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

        var conflictSolver = handler != null ? GetConflictSolver() : null;
        var context = new CommitContext { IsRollback = false, CommitTime = DateTime.UtcNow, Solver = conflictSolver };
        
        // Process every Component Type and their components
        foreach (var componentInfo in _componentInfos.Values)
        {
            context.Info = componentInfo;
            
            foreach (var key in componentInfo.CompRevInfoCache.Keys)
            {
                context.PrimaryKey = key;
                context.CompRevInfo = ref CollectionsMarshal.GetValueRefOrNullRef(componentInfo.CompRevInfoCache, key);

                // Nothing to commit if we only read the component
                if (context.CompRevInfo.Operations == ComponentInfo.OperationType.Read)
                {
                    continue;
                }

                CommitComponent(ref context);
            }
        }

        // New state
        State = TransactionState.Committed;
        return true;
    }
}