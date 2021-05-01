// unset

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine
{
    public unsafe struct Transaction : IDisposable
    {
        private const int RandomAccessCachedPagesCount = 16;

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
                public int RowVersionChunkId;
                public int RowVersionIndexInChunk;
                public OperationType Operations;
            }

            public ComponentTable ComponentTable;
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
        private bool _isExclusive;
        private readonly DatabaseEngine _dbe;

        private readonly Dictionary<Type, ComponentInfo> _componentInfos;
        private int _transactionNodeId;

        // Transaction acts as a single point in time where we make changes (or rollback them), this point in time is the construction datetime.
        private readonly long _transactionCreationTick;

        private int? _committedOperationCount;
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
                    _committedOperationCount = count;
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

        private ComponentInfo GetComponentInfo(Type componentType)
        {
            if (!_componentInfos.TryGetValue(componentType, out var info))
            {
                info = new ComponentInfo();
                info.ComponentTable = _dbe.GetComponentTable(componentType);
                if (info.ComponentTable == null) throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");

                info.RowAccessor = info.ComponentTable.ComponentSegment.CreateChunkRandomAccessor(RandomAccessCachedPagesCount);
                info.VersionTableAccessor = info.ComponentTable.VersionTableSegment.CreateChunkRandomAccessor(RandomAccessCachedPagesCount);
                info.RowInfoCache = new Dictionary<long, ComponentInfo.RowInfo>();

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
                var rowChunkId = info.ComponentTable.CreateComponent(pk, rowTick, _isExclusive, out var rowVersionChunkId);

                // We might want to access this row again in the Transaction to let's cache the pk/RowVersion
                info.RowInfoCache.Add(pk, new ComponentInfo.RowInfo { ComponentChunkId = rowChunkId, RowVersionFirstChunkId = rowVersionChunkId, Operations = ComponentInfo.OperationType.Created, RowVersionChunkId = rowChunkId, RowVersionIndexInChunk = 0 });

                // Setup the row header
                var componentData = info.RowAccessor.GetChunkAddress(rowChunkId, dirtyPage: true);
                var header = (ComponentRowHeader*)componentData;
                header->Revision = 1;
                header->Timestamp = rowTick;

                // Copy the row data
                var rowData = componentData + sizeof(ComponentRowHeader);
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
                    if (!info.ComponentTable.GetComponentRowChunkIdFromIndex(pk, _transactionCreationTick, out rowInfo))
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
                    new Span<byte>(compAddr + sizeof(ComponentRowHeader), size).CopyTo(new Span<byte>(data[i].Data, size));
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
                        info.ComponentTable.UpdateRow(ref rowInfo, rowTick, isDelete, true);
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
                        if (!info.ComponentTable.GetRowVersionTableFirstChunkId(pk, out rowInfo.RowVersionFirstChunkId))
                        {
                            return false;
                        }
                    }

                    // Add a new row version for the current component, if there is no data it means we are delete the component, we still
                    //  need to add a new version with an empty ComponentChunkId
                    info.ComponentTable.AddRow(ref rowInfo, rowTick, isDelete, _isExclusive);
                    rowInfo.Operations |= data[i].Data != null ? ComponentInfo.OperationType.Updated : ComponentInfo.OperationType.Deleted;
                    info.RowInfoCache[pk] = rowInfo;
                }

                // Setup the row header
                if (!isDelete)
                {
                    var componentData = info.RowAccessor.GetChunkAddress(rowInfo.ComponentChunkId, dirtyPage: true);
                    var header = (ComponentRowHeader*)componentData;
                    header->Revision = 1;
                    header->Timestamp = _transactionCreationTick;

                    // Copy the row data
                    var rowData = componentData + sizeof(ComponentRowHeader);
                    int rowSize = info.ComponentTable.ComponentRowSize;
                    new Span<byte>(data[i].Data, rowSize).CopyTo(new Span<byte>(rowData, rowSize));
                }
            }

            return true;
        }
        
        public bool Rollback()
        {
            // Nothing to do if the transaction is empty
            if (State is TransactionState.Created) return true;

            // Can't rollback a transaction already processed
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
                    if (rowInfo.Operations == ComponentInfo.OperationType.Read) continue;

                    componentInfo.ComponentTable.CommitRow(pk, minTick, ref rowInfo, true, _isExclusive);
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

                    componentInfo.ComponentTable.CommitRow(pk, minTick, ref rowInfo, false, _isExclusive);
                }
            }

            // New state
            State = TransactionState.Committed;
            return true;
        }
    }
}