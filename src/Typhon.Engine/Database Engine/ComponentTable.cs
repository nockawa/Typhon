// unset

using System;
using System.Runtime.CompilerServices;
using Typhon.Engine.BPTree;

namespace Typhon.Engine
{
    internal struct ComponentRowHeader
    {
        public long Revision;
        public long Timestamp;
    }

    public unsafe struct ComponentReadWriteAccessor<T> : IDisposable where T : unmanaged
    {
        public DatabaseEngine DBE => ComponentTable.DBE;
        public ComponentTable ComponentTable { get; private set; }
        
        private byte* _componentAddr;
        private int _rowVersionStorageChunkId;
        private PageAccessor _accessor;

        public ComponentReadWriteAccessor(DatabaseEngine dbe)
        {
            ComponentTable = dbe.GetComponentTable<T>();
            _componentAddr = null;
            _accessor = default;
            _rowVersionStorageChunkId = default;
        }

        public ref T Current => ref Unsafe.AsRef<T>(_componentAddr);

        public void Create()
        {
            _componentAddr = ComponentTable.CreateComponent(ref _accessor, out _rowVersionStorageChunkId);
        }

        public void Read(long pk)
        {
            _componentAddr = ComponentTable.ReadComponent(pk, ref _accessor);
        }

        public void Reset()
        {
            _componentAddr = null;
            _accessor.Dispose();
        }

        public void Dispose()
        {
            if (ComponentTable == null)
            {
                return;
            }
            _accessor.Dispose();

            ComponentTable = null;
        }
    }

    public class ComponentTable : IDisposable
    {
        private const int ComponentSegmentStartingSize = 4;
        private const int MainIndexSegmentStartingSize = 4;

        private static readonly int IndexAccessPoolSize = 16;

        private DatabaseEngine _dbe;
        private ChunkRandomAccessor _mainIndexAccessor;

        public ChunkBasedSegment ComponentSegment { get; private set; }
        public ChunkBasedSegment MainIndexSegment { get; private set; }

        private BTree<long> _mainIndex;
        private DBComponentDefinition _definition;
        private unsafe int _rowOverhead => sizeof(ComponentRowHeader) + (_definition.IndicesCount * sizeof(int));
        private int _rowTotalSize => _definition.RowSize + _rowOverhead;
        private BlockAllocator _tempRowsAllocator;

        unsafe public void Create(DatabaseEngine dbe, DBComponentDefinition definition)
        {
            _dbe = dbe;
            _definition = definition;

            var lsm = _dbe.LSM;

            ComponentSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, _rowTotalSize);
            MainIndexSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));

            _mainIndexAccessor = MainIndexSegment.CreateChunkRandomAccessor(IndexAccessPoolSize);
            _mainIndex = new LongSingleBTree(MainIndexSegment, _mainIndexAccessor);

            _tempRowsAllocator = new BlockAllocator(_rowTotalSize, 256);
        }

        public void Dispose()
        {
            if (ComponentSegment == null) return;

            _mainIndexAccessor.Dispose();

            MainIndexSegment.Dispose();
            ComponentSegment.Dispose();

            ComponentSegment = null;
        }

        internal DatabaseEngine DBE => _dbe;

        internal struct SerializationData
        {
            public LogicalSegment.SerializationData ComponentSegment;
            public LogicalSegment.SerializationData MainIndexSegment;
        }
        internal SerializationData SerializeSettings() =>
            new()
            {
                ComponentSegment = ComponentSegment.SerializeSettings(),
                MainIndexSegment = MainIndexSegment.SerializeSettings()
            };

        internal unsafe byte* CreateComponent(ref PageAccessor accessor, out int rowVersionStorageChunkId)
        {
            // Allocate the chunk that will store the component's row
            var componentChunkId = ComponentSegment.AllocateChunk(false);

            // Get its PK
            var pk = DBE.GetNewPrimaryKey();

            // Allocate the row version storage as it's a new component
            rowVersionStorageChunkId = AllocRowVersionStorage(componentChunkId);

            // Update the index with this new entry
            _mainIndex.Add(pk, rowVersionStorageChunkId);

            // Get the chunk location and update the accessor accordingly
            var chunkAddr = FetchChunk(ref accessor, componentChunkId);



            return chunkAddr;
        }

        internal struct RowVersionStorageHeader
        {
            public int NextChunkId;
            public short FirstItemIndex;
            public short ItemCount;
        }

        internal struct RowVersionStorageElement
        {
            public long Tick;
            public int RowChunkId;
        }

        internal unsafe int AllocRowVersionStorage(int firstRowChunkId)
        {
            var chunkId = MainIndexSegment.AllocateChunk(false);
            var chunkAddr = _mainIndexAccessor.GetChunkAddress(chunkId);

            // Initialize the header
            var header = ((RowVersionStorageHeader*)chunkAddr);
            header->NextChunkId = 0;
            header->FirstItemIndex = 0;
            header->ItemCount = 1;

            // Initialize the first element
            var chunkElements = (RowVersionStorageElement*)(chunkAddr + sizeof(RowVersionStorageHeader));
            // We don't want this version to be "valid" (retrievable by queries) so we set the max tick to make it immediately discarded
            chunkElements[0].Tick = long.MaxValue;
            chunkElements[0].RowChunkId = firstRowChunkId;

            return chunkId;
        }

        internal unsafe byte* ReadComponent(long pk, ref PageAccessor accessor)
        {
            return null;
        }

        private unsafe byte* FetchChunk(ref PageAccessor accessor, int chunkId)
        {
            var (si, off) = ComponentSegment.GetChunkLocation(chunkId);

            if (accessor.IsValid == false || accessor.PageId != ComponentSegment.Pages[si])
            {
                if (accessor.IsValid)   accessor.Dispose();
                accessor = ComponentSegment.GetPageExclusiveAccessor(si);
            }
            
            return accessor.GetElementAddr(off, ComponentSegment.Stride, si == 0);
        }
    }
}