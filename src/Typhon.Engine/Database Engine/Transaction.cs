// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Engine.BPTree;

namespace Typhon.Engine;

public unsafe struct Transaction : IDisposable
{
    private const int RandomAccessCachedPagesCount = 16;
    private const long RowVersionTransactionExclusiveFlag = 1L << 63;
    private const int RowVersionCountPerChunk = ComponentTable.RowVersionCountPerChunk;

    internal struct ComponentData
    {
        public ComponentData(Type type, void* data)
        {
            Type = type;
            Data = data;
        }
        public Type Type;
        public void* Data;
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

        public struct RowInfo
        {
            public int ComponentChunkId;
            public int RowVersionFirstChunkId;
            public int RowRevisionBeforeTransaction;
            public int RowVersionChunkId;
            public int RowVersionIndexInChunk;
            public OperationType Operations;
        }

        public ComponentTable ComponentTable;
        public ChunkBasedSegment ComponentSegment;
        public ChunkBasedSegment VersionTableSegment;
        public LongSingleBTree PrimaryKeyIndex;
        public ChunkRandomAccessor RowAccessor;
        public ChunkRandomAccessor VersionTableAccessor;
        public Dictionary<long, RowInfo> RowInfoCache;
    }

    public enum TransactionState
    {
        Created = 0,        // New object, no operation done yet
        InProgress,         // At least one operation added to the transaction
        Rollbacked,         // Was rollbacked by the user or during dispose
        Committed           // Was committed by the user
    }

    internal struct TransactionChainNode
    {
        public long TransactionTick;
        public int PrevNodeId;
        public int NextNodeId;
        public int TransactionId;
    }

    internal class TransactionChain
    {
        public int HeadNodeId { get; private set; }

        public int TailNodeId { get; private set; }

        internal int GetNextNode(int nodeId) => _allocator.Get(nodeId).NextNodeId;
        internal int GetPrevNode(int nodeId) => _allocator.Get(nodeId).PrevNodeId;

        private readonly UnmanagedStructAllocator<TransactionChainNode> _allocator;
        private AccessControl _control;

        public TransactionChain()
        {
            _allocator = new UnmanagedStructAllocator<TransactionChainNode>(256);
            HeadNodeId = -1;
            TailNodeId = -1;
        }

        public int PushHead(long tick, int transactionId)
        {
            _control.EnterExclusiveAccess();
            ref var node = ref _allocator.Allocate(out var nodeId);

            node.TransactionTick = tick;
            node.TransactionId = transactionId;
            node.PrevNodeId = HeadNodeId;
            node.NextNodeId = -1;

            if (HeadNodeId != -1)
            {
                _allocator.Get(HeadNodeId).NextNodeId = nodeId;
            }

            HeadNodeId = nodeId;

            if (TailNodeId == -1)
            {
                TailNodeId = nodeId;
            }

            _control.ExitExclusiveAccess();
            return nodeId;
        }

        public void RemoveNode(int nodeId)
        {
            _control.EnterExclusiveAccess();

            ref var node = ref _allocator.Get(nodeId);
            if (node.NextNodeId != -1)
            {
                _allocator.Get(node.NextNodeId).PrevNodeId = node.PrevNodeId;
            }

            if (node.PrevNodeId != -1)
            {
                _allocator.Get(node.PrevNodeId).NextNodeId = node.NextNodeId;
            }

            if (TailNodeId == nodeId)
            {
                TailNodeId = node.NextNodeId;
            }

            if (HeadNodeId == nodeId)
            {
                HeadNodeId = node.PrevNodeId;
            }

            _control.ExitExclusiveAccess();
        }

        public long GetMinTick()
        {
            if (TailNodeId == -1) return 0;
            return _allocator.Get(TailNodeId).TransactionTick;
        }
    }

    internal static TransactionChain Transactions;

    static Transaction()
    {
        Transactions = new TransactionChain();
    }

    private static int TransactionIdCounter;

    public TransactionState State { get; private set; }
    public int TransactionId { get; }

    private bool _isDisposed;
    private readonly bool _isExclusive;
    private readonly DatabaseEngine _dbe;

    private readonly Dictionary<Type, ComponentInfo> _componentInfos;
    private readonly int _transactionNodeId;

    // Transaction acts as a single point in time for queries, this point in time is the construction datetime.
    private readonly long _transactionCreationTick;

    private int? _committedOperationCount;
    private int _deletedComponentCount;

    public int CommittedOperationCount
    {
        get
        {
            if (_committedOperationCount.HasValue == false)
            {
                var count = 0;
                foreach (var componentInfo in _componentInfos.Values)
                {
                    count += componentInfo.RowInfoCache.Count;
                }
                _committedOperationCount = count + _deletedComponentCount;
            }
            return _committedOperationCount.Value;
        }
    }

    public Transaction(DatabaseEngine dbe, bool exclusiveConcurrency)
    {
        _dbe = dbe;
        _isDisposed = false;
        _isExclusive = exclusiveConcurrency;
        _componentInfos = new Dictionary<Type, ComponentInfo>();
        _transactionCreationTick = DateTime.UtcNow.Ticks;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        TransactionId = Interlocked.Increment(ref TransactionIdCounter);
        State = TransactionState.Created;

        _transactionNodeId = Transactions.PushHead(_transactionCreationTick, TransactionId);
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        if (State != TransactionState.Committed)
        {
            Rollback();
        }

        Transactions.RemoveNode(_transactionNodeId);
        _isDisposed = true;
    }

    public long CreateEntity<T>(ref T t) where T : unmanaged 
        => CreateEntity(new ComponentData(typeof(T), Unsafe.AsPointer(ref t)));
    public long CreateEntity<T, U>(ref T t, ref U u) where T : unmanaged where U : unmanaged
        => CreateEntity(new ComponentData(typeof(T), Unsafe.AsPointer(ref t)), new ComponentData(typeof(U), Unsafe.AsPointer(ref u)));
    public long CreateEntity<T, U, V>(ref T t, ref U u, ref V v) where T : unmanaged where U : unmanaged where V : unmanaged
        => CreateEntity(new ComponentData(typeof(T), Unsafe.AsPointer(ref t)), new ComponentData(typeof(U), Unsafe.AsPointer(ref u)), new ComponentData(typeof(V), Unsafe.AsPointer(ref v)));

    public bool ReadEntity<T>(long pk, out T t) where T : unmanaged
    {
        t = default;
        return ReadEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)));
    }

    public bool ReadEntity<T, U>(long pk, out T t, out U u) where T : unmanaged where U : unmanaged
    {
        t = default;
        u = default;
        return ReadEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)), new ComponentData(typeof(U), Unsafe.AsPointer(ref u)));
    }

    public bool ReadEntity<T, U, V>(long pk, out T t, out U u, out V v) where T : unmanaged where U : unmanaged where V : unmanaged
    {
        t = default;
        u = default;
        v = default;
        return ReadEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)), new ComponentData(typeof(U), Unsafe.AsPointer(ref u)), new ComponentData(typeof(V), Unsafe.AsPointer(ref v)));
    }

    public bool UpdateEntity<T>(long pk, ref T t) where T : unmanaged 
        => UpdateEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)));

    public bool UpdateEntity<T, U>(long pk, ref T t, ref U u) where T : unmanaged where U : unmanaged 
        => UpdateEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)), new ComponentData(typeof(U), Unsafe.AsPointer(ref u)));

    public bool UpdateEntity<T, U, V>(long pk, ref T t, ref U u, ref V v) where T : unmanaged where U : unmanaged where V : unmanaged 
        => UpdateEntity(pk, new ComponentData(typeof(T), Unsafe.AsPointer(ref t)), new ComponentData(typeof(U), Unsafe.AsPointer(ref u)), new ComponentData(typeof(V), Unsafe.AsPointer(ref v)));

    public bool DeleteEntity<T>(long pk) where T : unmanaged
        => UpdateEntity(pk, new ComponentData(typeof(T), null));

    public bool DeleteEntity<T, U>(long pk) where T : unmanaged where U : unmanaged
        => UpdateEntity(pk, new ComponentData(typeof(T), null), new ComponentData(typeof(U), null));

    public bool DeleteEntity<T, U, V>(long pk) where T : unmanaged where U : unmanaged where V : unmanaged
        => UpdateEntity(pk, new ComponentData(typeof(T), null), new ComponentData(typeof(U), null), new ComponentData(typeof(V), null));

    public int GetComponentRevision<T>(long pk) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        if (!info.RowInfoCache.TryGetValue(pk, out var rowInfo))
        {
            if (!GetRowVersionTableFirstChunkId(pk, info, out rowInfo.RowVersionFirstChunkId))
            {
                return -1;
            }
        }

        ref var h = ref info.VersionTableAccessor.GetChunk<RowVersionStorageHeader>(rowInfo.RowVersionFirstChunkId);
        return h.Revision;
    }

    private ComponentInfo GetComponentInfo(Type componentType)
    {
        if (!_componentInfos.TryGetValue(componentType, out var info))
        {
            var ct = _dbe.GetComponentTable(componentType);
            if (ct == null) throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");

            info = new ComponentInfo
            {
                ComponentTable       = ct,
                ComponentSegment     = ct.ComponentSegment,
                VersionTableSegment  = ct.VersionTableSegment,
                PrimaryKeyIndex      = ct.PrimaryKeyIndex,
                RowAccessor          = ct.ComponentSegment.CreateChunkRandomAccessor(RandomAccessCachedPagesCount),
                VersionTableAccessor = ct.VersionTableSegment.CreateChunkRandomAccessor(RandomAccessCachedPagesCount),
                RowInfoCache         = new Dictionary<long, ComponentInfo.RowInfo>()
            };

            _componentInfos.Add(componentType, info);
        }

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
        var rowTick = DateTime.UtcNow.Ticks;

        for (int i = 0; i < data.Length; i++)
        {
            var componentType = data[i].Type;

            // Fetch the cached info or create it if it's the first time we operate on this Component type
            var info = GetComponentInfo(componentType);

            // Create the component, its version table and add an index entry
            var rowChunkId = CreateComponent(pk, info, rowTick, _isExclusive, out var rowVersionChunkId);

            // We might want to access this row again in the Transaction to let's cache the pk/RowVersion
            info.RowInfoCache.Add(pk, new ComponentInfo.RowInfo
            {
                ComponentChunkId       = rowChunkId, 
                RowVersionFirstChunkId = rowVersionChunkId, 
                Operations             = ComponentInfo.OperationType.Created, 
                RowVersionChunkId      = rowChunkId, 
                RowVersionIndexInChunk = 0
            });

            // Copy the row data
            var componentData = info.RowAccessor.GetChunkAddress(rowChunkId, dirtyPage: true);
            var rowData = componentData + info.ComponentTable.RowOverhead;
            int rowSize = info.ComponentTable.ComponentRowSize;
            new Span<byte>(data[i].Data, rowSize).CopyTo(new Span<byte>(rowData, rowSize));
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
            if (!info.RowInfoCache.TryGetValue(pk, out var rowInfo))
            {
                // Couldn't find in the cache, get it from the index
                if (!GetComponentRowInfoFromIndex(pk, info, _transactionCreationTick, out rowInfo))
                {
                    // No row for this PK/Tick
                    ++notFoundCount;
                    continue;
                }

                rowInfo.Operations |= ComponentInfo.OperationType.Read;
                info.RowInfoCache.Add(pk, rowInfo);
            }

            if (rowInfo.ComponentChunkId != 0)
            {
                int size = info.ComponentTable.ComponentRowSize;
                var compAddr = info.RowAccessor.GetChunkAddress(rowInfo.ComponentChunkId);
                new Span<byte>(compAddr + info.ComponentTable.RowOverhead, size).CopyTo(new Span<byte>(data[i].Data, size));
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

        var rowTick = DateTime.UtcNow.Ticks;
        for (int i = 0; i < data.Length; i++)
        {
            var componentType = data[i].Type;
            var isDelete = data[i].Data == null;

            // Fetch the cached info or create it if it's the first time we operate on this Component type
            var info = GetComponentInfo(componentType);

            // Check if the component is in the cache (meaning we already made an operation on it in this transaction)
            var rowCached = info.RowInfoCache.TryGetValue(pk, out var rowInfo);
            if (rowCached)
            {
                // Can't update a deleted row...
                if ((rowInfo.Operations & ComponentInfo.OperationType.Deleted) == ComponentInfo.OperationType.Deleted)
                {
                    return false;
                }

                // Check if we already made a valid operation (create or update) for this row in the transaction and just update the component row data and tick
                if ((rowInfo.Operations & (ComponentInfo.OperationType.Created | ComponentInfo.OperationType.Updated)) != 0)
                {
                    UpdateRow(info, ref rowInfo, rowTick, isDelete, true);
                    var newOp = rowInfo.Operations | (isDelete ? ComponentInfo.OperationType.Deleted : ComponentInfo.OperationType.Updated);
                    if (rowInfo.Operations != newOp)
                    {
                        rowInfo.Operations = newOp;
                        info.RowInfoCache[pk] = rowInfo;
                    }
                }
            }

            // First operation on this row in this transaction
            if (rowInfo.ComponentChunkId == 0)
            {
                // If the row is already cached, we can take advantage of it to avoid an index lookup, otherwise do the lookup
                if (!rowCached)
                {
                    if (!GetRowVersionTableFirstChunkId(pk, info, out rowInfo.RowVersionFirstChunkId))
                    {
                        return false;
                    }
                }

                // Add a new row version for the current component, if there is no data it means we are delete the component, we still
                //  need to add a new version with an empty ComponentChunkId
                AddRow(info, ref rowInfo, rowTick, isDelete, _isExclusive);
                rowInfo.Operations |= data[i].Data != null ? ComponentInfo.OperationType.Updated : ComponentInfo.OperationType.Deleted;
                info.RowInfoCache[pk] = rowInfo;
            }

            // Setup the row header
            if (!isDelete)
            {
                // Copy the row data
                var componentData = info.RowAccessor.GetChunkAddress(rowInfo.ComponentChunkId, dirtyPage: true);
                var rowData = componentData + info.ComponentTable.RowOverhead;
                int rowSize = info.ComponentTable.ComponentRowSize;
                new Span<byte>(data[i].Data, rowSize).CopyTo(new Span<byte>(rowData, rowSize));
            }
        }

        return true;
    }

    internal int CreateComponent(long primaryKey, ComponentInfo info, long tick, bool isExclusive, out int rowVersionStorageChunkId)
    {
        // Allocate the chunk that will store the component's row
        var componentChunkId = info.ComponentSegment.AllocateChunk(false);

        // Allocate the row version storage as it's a new component
        rowVersionStorageChunkId = AllocRowVersionStorage(info, tick, componentChunkId, isExclusive);

        // Update the index with this new entry
        info.PrimaryKeyIndex.Add(primaryKey, rowVersionStorageChunkId);

        return componentChunkId;
    }

    internal int AllocRowVersionStorage(ComponentInfo info, long tick, int firstRowChunkId, bool isExclusive)
    {
        var chunkId = info.VersionTableSegment.AllocateChunk(false);
        var chunkAddr = info.VersionTableAccessor.GetChunkAddress(chunkId);

        // Initialize the header
        var header = ((RowVersionStorageHeader*)chunkAddr);
        header->NextChunkId = 0;
        header->Revision = 1;
        header->Control = default;
        header->FirstItemIndex = 0;
        header->ItemCount = 1;
        header->ChainLength = 1;

        if (isExclusive) header->Control.EnterExclusiveAccess();

        // Initialize the first element
        var chunkElements = (RowVersionStorageElement*)(chunkAddr + sizeof(RowVersionStorageHeader));
        // We don't want this version to be "valid" (retrievable by queries) so we set the max tick to make it immediately discarded
        chunkElements[0].Tick = tick | RowVersionTransactionExclusiveFlag;
        chunkElements[0].RowChunkId = firstRowChunkId;

        return chunkId;
    }

    internal bool GetRowVersionTableFirstChunkId(long pk, ComponentInfo info, out int firstChunkId) => info.PrimaryKeyIndex.TryGet(pk, out firstChunkId);

    internal void AddRow(ComponentInfo info, ref ComponentInfo.RowInfo rowInfo, long tick, bool isDelete, bool isExclusive)
    {
        var versionTableAccessor = info.VersionTableAccessor;
        var versionTableSegment = info.VersionTableSegment;
        var componentSegment = info.ComponentSegment;

        // Get the chunk of the header, pin it because we might access other chunk while walking the chain
        var chunkAddr = versionTableAccessor.GetChunkAddress(rowInfo.RowVersionFirstChunkId, true, true);
        var header = ((RowVersionStorageHeader*)chunkAddr);

        // Enter exclusive access for the RVS
        header->Control.EnterExclusiveAccess();

        // Save the current revision, we'll need it to restore it in case of rollback
        rowInfo.RowRevisionBeforeTransaction = header->Revision;

        // Check if we need to add one more chunk to the chain
        if (header->ChainLength * RowVersionCountPerChunk == header->ItemCount)
        {
            header->ChainLength++;

            // Allocate a new chunk as everything is full
            var newChunkId = versionTableSegment.AllocateChunk(false);

            // Walk through the chain to find the last chunk, to link it to the new one
            var curChunkHeader = (RowVersionStorageHeader*)chunkAddr;
            while (curChunkHeader->NextChunkId != 0)
            {
                curChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkHeader->NextChunkId);
            }
            curChunkHeader->NextChunkId = newChunkId;

            // Setup our new chunk
            var newChunkAddr = versionTableAccessor.GetChunkAddress(newChunkId, false, true);
            var newChunkHeader = (RowVersionStorageHeader*)newChunkAddr;
            newChunkHeader->NextChunkId = 0;    // The rest of the header data is simply ignored, wasted...
        }

        // Add our new entry
        {
            // We store elements in a rotating buffer...that is stored in a forward linked list of RowVersionCountPerChunk elements
            // So a bit of computing must be made to find where to add our row
            var entryIndex = (header->FirstItemIndex + header->ItemCount) % (header->ChainLength * RowVersionCountPerChunk);
            var chunkIndexInChain = Math.DivRem(entryIndex, RowVersionCountPerChunk, out var indexInChunk);

            // Walk through the linked list until we spot our chunk
            var curChunkId = rowInfo.RowVersionFirstChunkId;
            var curChunkHeader = header;
            while (chunkIndexInChain != 0)
            {
                curChunkId = curChunkHeader->NextChunkId;
                curChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
                --chunkIndexInChain;
            }
            versionTableAccessor.DirtyChunk(curChunkId);

            // Allocate a new row
            var rowChunkId = isDelete ? 0 : componentSegment.AllocateChunk(false);

            // Add our new entry
            header->ItemCount++;
            header->Revision++;
            var curChunkElements = (RowVersionStorageElement*)(curChunkHeader + 1);
            curChunkElements[indexInChunk].Tick = tick | RowVersionTransactionExclusiveFlag;
            curChunkElements[indexInChunk].RowChunkId = rowChunkId;

            // Update the rowInfo
            rowInfo.ComponentChunkId = rowChunkId;
            rowInfo.RowVersionChunkId = curChunkId;
            rowInfo.RowVersionIndexInChunk = indexInChunk;

            // Keep the lock on the row if the transaction is in pessimistic concurrency mode
            if (!isExclusive)
            {
                header->Control.ExitExclusiveAccess();
            }

            // Cleanups
            versionTableAccessor.UnpinChunk(rowInfo.RowVersionFirstChunkId);
        }
    }

    internal bool GetComponentRowInfoFromIndex(long pk, ComponentInfo info, long tick, out ComponentInfo.RowInfo rowInfo)
    {
        var versionTableAccessor = info.VersionTableAccessor;

        if (!info.PrimaryKeyIndex.TryGet(pk, out var rowVersionFirstChunkId))
        {
            rowInfo = default;
            return false;
        }

        var firstHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(rowVersionFirstChunkId, true);
        firstHeader->Control.EnterSharedAccess();       // Lock with shared to allow concurrent walk through the chain but wait on update

        // Save the current revision, we'll need it to restore it in case of rollback
        rowInfo.RowRevisionBeforeTransaction = firstHeader->Revision;

        // Walk through the chain to find the first Item as elements are stored in a circular buffer
        var itemLeftCount = firstHeader->ItemCount;
        var rowVersionIndex = (int)firstHeader->FirstItemIndex;
        var curHeader = firstHeader;
        var curChunkId = rowVersionFirstChunkId;
        while (rowVersionIndex >= RowVersionCountPerChunk)
        {
            curChunkId = curHeader->NextChunkId;
            curHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
            rowVersionIndex -= RowVersionCountPerChunk;
        }

        // We've got the starting point, now we find the right row version, the one that is the closest to the tick we requested
        var curElements = (RowVersionStorageElement*)(curHeader + 1);

        // First check if we only have more recent version than the one we want, return nothing if it's the case
        if (curElements[rowVersionIndex].Tick > tick)
        {
            rowInfo = default;
            return false;
        }

        int rowChunkId = 0;
        while (--itemLeftCount >= 0 && curElements[rowVersionIndex].Tick < tick)
        {
            // If tick is null, the entry is to be ignored because it's a rollbacked one
            if (curElements[rowVersionIndex].Tick != 0)
            {
                rowChunkId = curElements[rowVersionIndex].RowChunkId;
            }

            if (++rowVersionIndex == RowVersionCountPerChunk)
            {
                rowVersionIndex = 0;
                curChunkId = curHeader->NextChunkId != 0 ? curHeader->NextChunkId : rowVersionFirstChunkId;
                curHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
                curElements = (RowVersionStorageElement*)(curHeader + 1);
            }
        }

        if (rowChunkId == 0)
        {
            rowInfo = default;
            return false;
        }

        firstHeader->Control.ExitSharedAccess();
        versionTableAccessor.UnpinChunk(rowVersionFirstChunkId);

        rowInfo = new ComponentInfo.RowInfo
        {
            ComponentChunkId = rowChunkId,
            RowVersionFirstChunkId = rowVersionFirstChunkId,
            RowVersionChunkId = curChunkId,
            RowVersionIndexInChunk = rowVersionIndex
        };

        return true;
    }

    // Return true if the whole component is deleted (all rows versions were delete/rollbacked)
    private bool CommitRow(long pk, ComponentInfo info, long minTick, ref ComponentInfo.RowInfo rowInfo, bool isRollback, bool isExclusive)
    {
        var versionTableAccessor = info.VersionTableAccessor;
        var componentSegment = info.ComponentSegment;

        // Get the chunk of the header, pin it because we might access other chunk while walking the chain
        var firstChunkAddr = versionTableAccessor.GetChunkAddress(rowInfo.RowVersionFirstChunkId, true);
        var firstChunkHeader = ((RowVersionStorageHeader*)firstChunkAddr);
        var dirtyFirstChunk = false;

        // Enter exclusive access for the RVS if the transaction is not in exclusive mode (otherwise we already own the lock)
        if (!isExclusive)
        {
            firstChunkHeader->Control.EnterExclusiveAccess();
        }
        else
        {
            Debug.Assert(firstChunkHeader->Control.IsLockedByCurrentThread, "Error the row should be locked by the Transaction but it's not.");
        }

        // We store elements in a rotating buffer...that is stored in a forward linked list of RowVersionCountPerChunk elements
        // So a bit of computing must be made to find the first item
        var rowVersionIndex = firstChunkHeader->FirstItemIndex % (firstChunkHeader->ChainLength * RowVersionCountPerChunk);
        var chunkIndexInChain = Math.DivRem(rowVersionIndex, RowVersionCountPerChunk, out var indexInChunk);

        // Walk through the linked list until we find the chunk that is our starting point
        int curChunkId;
        var curChunkHeader = firstChunkHeader;
        while (chunkIndexInChain != 0)
        {
            curChunkId = curChunkHeader->NextChunkId;
            curChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
            --chunkIndexInChain;
        }

        // Free all row versions that are older than minTick, except the last one when we rollback: we still need it
        var prevRowChunkId = 0;
        var prevRowFirstIndex = firstChunkHeader->FirstItemIndex;
        var itemLeftCount = firstChunkHeader->ItemCount;
        var curElements = (RowVersionStorageElement*)(firstChunkHeader + 1);
        while (--itemLeftCount >= 0 && (curElements[indexInChunk].Tick & ~RowVersionTransactionExclusiveFlag) < minTick)
        {
            // Get the ChunkId of the component row and release the row
            var rowChunkId = curElements[indexInChunk].RowChunkId;
            if (rowChunkId != 0)                                        // A rollbacked transaction can lead us to a valid entry with no row
            {
                if (prevRowChunkId != 0)
                {
                    componentSegment.FreeChunk(prevRowChunkId);
                }

                prevRowChunkId = rowChunkId;
                prevRowFirstIndex = firstChunkHeader->FirstItemIndex;
            }

            // Remove the row from the version
            --firstChunkHeader->ItemCount;
            ++firstChunkHeader->FirstItemIndex;
            dirtyFirstChunk = true;

            // Switch to next row
            if (++indexInChunk == RowVersionCountPerChunk)
            {
                indexInChunk = 0;
                curChunkId = curChunkHeader->NextChunkId != 0 ? curChunkHeader->NextChunkId : rowInfo.RowVersionFirstChunkId;
                curChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
                curElements = (RowVersionStorageElement*)(curChunkHeader + 1);
            }
        }

        curChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(rowInfo.RowVersionChunkId, dirtyPage: true);
        curElements = (RowVersionStorageElement*)(curChunkHeader + 1);

        // If we are roll-backing, re-correct the version to keep the last before minTick
        if (isRollback)
        {
            firstChunkHeader->FirstItemIndex = prevRowFirstIndex;
        }

        // If there's a previous row version, diff it against the new one on the field that are indexed to update them
        else if (prevRowChunkId != 0)
        {
            var prev = info.RowAccessor.GetChunkAddress(prevRowChunkId, pin: true);
            var cur  = info.RowAccessor.GetChunkAddress(curElements[rowInfo.RowVersionIndexInChunk].RowChunkId);
            var prevSpan  = new Span<byte>(prev, info.ComponentTable.RowTotalSize);
            var curSpan   = new Span<byte>(cur, info.ComponentTable.RowTotalSize);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                // The update changed the field?
                if (prevSpan.Slice(ifi.OffsetToField, ifi.Size).SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)) == false)
                {
                    if (ifi.Index.AllowMultiple)
                    {
                        ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], rowInfo.RowVersionFirstChunkId);
                        *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], rowInfo.RowVersionFirstChunkId);
                    }
                    else
                    {
                        ifi.Index.Remove(&prev[ifi.OffsetToField], out var val);
                        ifi.Index.Add(&cur[ifi.OffsetToField], val);
                    }
                }
            }

            info.RowAccessor.UnpinChunk(prevRowChunkId);

            // Free the previous row, we don't need it anymore as its replaced by the one we're committing
            componentSegment.FreeChunk(prevRowChunkId);
        }

        // No previous version, it means we're adding the first row version, add the indices
        else
        {
            var cur = info.RowAccessor.GetChunkAddress(curElements[rowInfo.RowVersionIndexInChunk].RowChunkId);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                if (ifi.Index.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], rowInfo.RowVersionFirstChunkId);
                }
                else
                {
                    ifi.Index.Add(&cur[ifi.OffsetToField], rowInfo.RowVersionFirstChunkId);
                }
            }
        }

        // Clear the entry of the transaction row version if it's a rollback
        if (isRollback)
        {
            var rowChunkId = curElements[rowInfo.RowVersionIndexInChunk].RowChunkId;
            if (rowChunkId != 0)
            {
                componentSegment.FreeChunk(rowChunkId);
            }
            curElements[rowInfo.RowVersionIndexInChunk].Tick = 0;
            curElements[rowInfo.RowVersionIndexInChunk].RowChunkId = 0;
            firstChunkHeader->Revision = rowInfo.RowRevisionBeforeTransaction;

            dirtyFirstChunk = true;
        }
        // Remove the Exclusive flag on the row that belongs to the transaction to make it available to all accesses
        else
        {
            curElements[rowInfo.RowVersionIndexInChunk].Tick &= ~RowVersionTransactionExclusiveFlag;
        }

        bool res = false;

        // Check if we can/have to delete the whole row version, either:
        //  - All the items are from a tick older than the required one
        //  - All are older except the last one but this is is a deleted row we're committing
        //  - All are older except the last one but this is a created row we're roll-backing
        if ((itemLeftCount < 0) ||
            ((itemLeftCount == 0) && ((rowInfo.Operations & ComponentInfo.OperationType.Deleted) != 0) && (isRollback == false)) ||
            ((itemLeftCount == 0) && ((rowInfo.Operations & ComponentInfo.OperationType.Created) != 0) && isRollback))
        {
            Debug.Assert(curElements[rowInfo.RowVersionIndexInChunk].RowChunkId == 0, "Current Row Version point to an allocated Row Component, should be 0.");

            // Remove the index
            info.PrimaryKeyIndex.Remove(pk, out _);

            // Free the Row Version chain chunks
            curChunkId = rowInfo.RowVersionFirstChunkId;
            do
            {
                curChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(curChunkId);
                var nextChunkIdx = curChunkHeader->NextChunkId;
                versionTableAccessor.Segment.FreeChunk(curChunkId);
                curChunkId = nextChunkIdx;
            } while (curChunkId != 0);

            res = true;
        }

        else if (dirtyFirstChunk)
        {
            versionTableAccessor.DirtyChunk(rowInfo.RowVersionFirstChunkId);
        }

        // Cleanups
        firstChunkHeader->Control.ExitExclusiveAccess();
        versionTableAccessor.UnpinChunk(rowInfo.RowVersionFirstChunkId);

        return res;
    }

    internal void UpdateRow(ComponentInfo info, ref ComponentInfo.RowInfo rowInfo, long rowTick, bool isDelete, bool exclusiveRow)
    {
        var versionTableAccessor = info.VersionTableAccessor;
        var componentSegment = info.ComponentSegment;

        var rowChunkHeader = (RowVersionStorageHeader*)versionTableAccessor.GetChunkAddress(rowInfo.RowVersionChunkId, dirtyPage: true);
        rowChunkHeader->Revision++;
        var elements = (RowVersionStorageElement*)(rowChunkHeader + 1);
        elements[rowInfo.RowVersionIndexInChunk].Tick = rowTick | (exclusiveRow ? RowVersionTransactionExclusiveFlag : 0);
        if (isDelete)
        {
            var rowChunkId = elements[rowInfo.RowVersionIndexInChunk].RowChunkId;
            if (rowChunkId != 0)
            {
                componentSegment.FreeChunk(rowChunkId);
            }
            elements[rowInfo.RowVersionIndexInChunk].RowChunkId = 0;
        }
    }

    public bool Rollback()
    {
        // Nothing to do if the transaction is empty
        if (State is TransactionState.Created) return true;

        // Can't rollback a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed) return false;

        // Get the minimum tick of all transactions because we'll remove component row version that are older
        var minTick = Transactions.GetMinTick();

        var deletedComponents = new List<long>();
        // Process every Component Type and their rows
        foreach (var componentInfo in _componentInfos.Values)
        {
            deletedComponents.Clear();

            foreach (var kvp in componentInfo.RowInfoCache)
            {
                var pk = kvp.Key;
                var rowInfo = kvp.Value;

                // Nothing to commit if we only read the component
                if (rowInfo.Operations == ComponentInfo.OperationType.Read) continue;

                if (CommitRow(pk, componentInfo, minTick, ref rowInfo, true, _isExclusive))
                {
                    deletedComponents.Add(pk);
                }
            }

            foreach (var pk in deletedComponents)
            {
                componentInfo.RowInfoCache.Remove(pk);
                _deletedComponentCount++;
            }
        }

        // New state
        State = TransactionState.Rollbacked;
        return true;
    }

    public bool Commit()
    {
        // Nothing to do if the transaction is empty
        if (State is TransactionState.Created) return true;

        // Can't commit a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed) return false;

        // Get the minimum tick of all transactions because we'll remove component row version that are older
        var minTick = Transactions.GetMinTick();

        // Process every Component Type and their rows
        foreach (var componentInfo in _componentInfos.Values)
        {
            foreach (var kvp in componentInfo.RowInfoCache)
            {
                var pk = kvp.Key;
                var rowInfo = kvp.Value;

                // Nothing to commit if we only read the component
                if (rowInfo.Operations == ComponentInfo.OperationType.Read)    continue;

                CommitRow(pk, componentInfo, minTick, ref rowInfo, false, _isExclusive);
            }
        }

        // New state
        State = TransactionState.Committed;
        return true;
    }
}