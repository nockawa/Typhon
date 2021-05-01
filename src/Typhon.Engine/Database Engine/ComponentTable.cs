// unset

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;

namespace Typhon.Engine
{
    internal struct ComponentRowHeader
    {
        public long Revision;       // Incremented every time a row content is changed
        public long Timestamp;      // Timestamp of the last change
    }

    public unsafe class ComponentTable : IDisposable
    {
        private const int ComponentSegmentStartingSize = 4;
        private const int MainIndexSegmentStartingSize = 4;

        private const int RowVersionCountPerChunk = 8;
        private static readonly int RowVersionDataChunkSize = sizeof(RowVersionStorageHeader) + (RowVersionCountPerChunk * sizeof(RowVersionStorageElement));

        private static readonly int IndexAccessPoolSize = 16;

        private const long RowVersionTransactionExclusiveFlag = 1L << 63;

        private DatabaseEngine _dbe;
        private ChunkRandomAccessor _versionTableAccessor;
        private ChunkRandomAccessor _mainIndexAccessor;

        public ChunkBasedSegment ComponentSegment { get; private set; }
        public ChunkBasedSegment VersionTableSegment { get; private set; }
        public ChunkBasedSegment MainIndexSegment { get; private set; }

        public int ComponentRowSize => _definition.RowSize;

        private BTree<long> _mainIndex;
        private DBComponentDefinition _definition;
        private int _rowOverhead => sizeof(ComponentRowHeader) + (_definition.IndicesCount * sizeof(int));
        private int _rowTotalSize => _definition.RowSize + _rowOverhead;

        public void Create(DatabaseEngine dbe, DBComponentDefinition definition)
        {
            _dbe = dbe;
            _definition = definition;

            var lsm = _dbe.LSM;

            ComponentSegment    = lsm.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, _rowTotalSize);
            VersionTableSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, RowVersionDataChunkSize);
            MainIndexSegment    = lsm.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));

            _versionTableAccessor = VersionTableSegment.CreateChunkRandomAccessor(IndexAccessPoolSize);
            _mainIndexAccessor = MainIndexSegment.CreateChunkRandomAccessor(IndexAccessPoolSize);
            _mainIndex = new LongSingleBTree(MainIndexSegment, _mainIndexAccessor);
        }

        public void Dispose()
        {
            if (ComponentSegment == null) return;

            _versionTableAccessor.Dispose();
            _mainIndexAccessor.Dispose();

            MainIndexSegment.Dispose();
            VersionTableSegment.Dispose();
            ComponentSegment.Dispose();

            ComponentSegment = null;
        }

        internal DatabaseEngine DBE => _dbe;

        internal struct SerializationData
        {
            public LogicalSegment.SerializationData ComponentSegment;
            public LogicalSegment.SerializationData VersionTableSegment;
            public LogicalSegment.SerializationData MainIndexSegment;
        }
        internal SerializationData SerializeSettings() =>
            new()
            {
                ComponentSegment    = ComponentSegment.SerializeSettings(),
                VersionTableSegment = VersionTableSegment.SerializeSettings(),
                MainIndexSegment    = MainIndexSegment.SerializeSettings()
            };

        internal int CreateComponent(long primaryKey, long tick, bool isExclusive, out int rowVersionStorageChunkId)
        {
            // Allocate the chunk that will store the component's row
            var componentChunkId = ComponentSegment.AllocateChunk(false);

            // Allocate the row version storage as it's a new component
            rowVersionStorageChunkId = AllocRowVersionStorage(tick, componentChunkId, isExclusive);

            // Update the index with this new entry
            _mainIndex.Add(primaryKey, rowVersionStorageChunkId);

            return componentChunkId;
        }

        internal struct RowVersionStorageHeader
        {
            public int NextChunkId;
            public AccessControlSmall Control;
            public short FirstItemIndex;
            public short ItemCount;
            public int ChainLength;
        }

        internal struct RowVersionStorageElement
        {
            public long Tick;
            public int RowChunkId;
        }

        internal int AllocRowVersionStorage(long tick, int firstRowChunkId, bool isExclusive)
        {
            var chunkId = VersionTableSegment.AllocateChunk(false);
            var chunkAddr = _versionTableAccessor.GetChunkAddress(chunkId);

            // Initialize the header
            var header = ((RowVersionStorageHeader*)chunkAddr);
            header->NextChunkId = 0;
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

        internal bool GetRowVersionTableFirstChunkId(long pk, out int firstChunkId) => _mainIndex.TryGet(pk, out firstChunkId);

        internal void AddRow(ref Transaction.ComponentInfo.RowInfo rowInfo, long tick, bool isDelete, bool isExclusive)
        {
            // Get the chunk of the header, pin it because we might access other chunk while walking the chain
            var chunkAddr = _versionTableAccessor.GetChunkAddress(rowInfo.RowVersionFirstChunkId, true, true);
            var header = ((RowVersionStorageHeader*)chunkAddr);

            // Enter exclusive access for the RVS
            header->Control.EnterExclusiveAccess();

            // Check if we need to add one more chunk to the chain
            if (header->ChainLength * RowVersionCountPerChunk == header->ItemCount)
            {
                header->ChainLength++;

                // Allocate a new chunk as everything is full
                var newChunkId = VersionTableSegment.AllocateChunk(false);

                // Walk through the chain to find the last chunk, to link it to the new one
                var curChunkHeader = (RowVersionStorageHeader*)chunkAddr;
                while (curChunkHeader->NextChunkId != 0)
                {
                    curChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkHeader->NextChunkId);
                }
                curChunkHeader->NextChunkId = newChunkId;

                // Setup our new chunk
                var newChunkAddr = _versionTableAccessor.GetChunkAddress(newChunkId, false, true);
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
                    curChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkId);
                    --chunkIndexInChain;
                }
                _versionTableAccessor.DirtyChunk(curChunkId);

                // Allocate a new row
                var rowChunkId = isDelete ? 0 : ComponentSegment.AllocateChunk(false);

                // Add our new entry
                header->ItemCount++;
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
                _versionTableAccessor.UnpinChunk(rowInfo.RowVersionFirstChunkId);
            }
        }

        internal bool GetComponentRowChunkIdFromIndex(long pk, long tick, out Transaction.ComponentInfo.RowInfo rowInfo)
        {
            if (!_mainIndex.TryGet(pk, out var rowVersionFirstChunkId))
            {
                rowInfo = default;
                return false;
            }

            var firstHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(rowVersionFirstChunkId, true);
            firstHeader->Control.EnterSharedAccess();       // Lock with shared to allow concurrent walk through the chain but wait on update

            // Walk through the chain to find the first Item as elements are stored in a circular buffer
            var itemLeftCount = firstHeader->ItemCount;
            var rowVersionIndex = (int)firstHeader->FirstItemIndex;
            var curHeader = firstHeader;
            var curChunkId = rowVersionFirstChunkId;
            while (rowVersionIndex >= RowVersionCountPerChunk)
            {
                curChunkId = curHeader->NextChunkId;
                curHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkId);
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
            while (--itemLeftCount>=0 && curElements[rowVersionIndex].Tick < tick)
            {
                // If tick is null, the entry is to be ignored because it's a rollbacked one
                if (curElements[rowVersionIndex].Tick != 0)
                {
                    rowChunkId = curElements[rowVersionIndex].RowChunkId;
                }

                if (++rowVersionIndex == RowVersionCountPerChunk)
                {
                    rowVersionIndex = 0;
                    curChunkId      = curHeader->NextChunkId != 0 ? curHeader->NextChunkId : rowVersionFirstChunkId;
                    curHeader       = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkId);
                    curElements     = (RowVersionStorageElement*)(curHeader + 1);
                }
            }

            if (rowChunkId == 0)
            {
                rowInfo = default;
                return false;
            }

            firstHeader->Control.ExitSharedAccess();
            _versionTableAccessor.UnpinChunk(rowVersionFirstChunkId);

            rowInfo = new Transaction.ComponentInfo.RowInfo
            {
                ComponentChunkId       = rowChunkId, 
                RowVersionFirstChunkId = rowVersionFirstChunkId, 
                RowVersionChunkId      = curChunkId, 
                RowVersionIndexInChunk = rowVersionIndex
            };

            return true;
        }

        internal void CommitRow(long pk, long minTick, ref Transaction.ComponentInfo.RowInfo rowInfo, bool isRollback, bool isExclusive)
        {
            // Get the chunk of the header, pin it because we might access other chunk while walking the chain
            var firstChunkAddr = _versionTableAccessor.GetChunkAddress(rowInfo.RowVersionFirstChunkId, true);
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
            int curChunkId = rowInfo.RowVersionFirstChunkId;
            var curChunkHeader = firstChunkHeader;
            while (chunkIndexInChain != 0)
            {
                curChunkId = curChunkHeader->NextChunkId;
                curChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkId);
                --chunkIndexInChain;
            }

            // Free all row versions that are older than minTick, except the last one when we rollback: we still need it
            var prevRowChunkId = 0;
            var prevRowFirstIndex = firstChunkHeader->FirstItemIndex;
            var itemLeftCount = firstChunkHeader->ItemCount;
            var curElements = (RowVersionStorageElement*)(firstChunkHeader + 1);
            while (--itemLeftCount >= 0 && (curElements[indexInChunk].Tick&~RowVersionTransactionExclusiveFlag) < minTick)
            {
                // Get the ChunkId of the component row and release the row
                var rowChunkId = curElements[indexInChunk].RowChunkId;
                if (rowChunkId != 0)                                        // A rollbacked transaction can lead us to a valid entry with no row
                {
                    if (prevRowChunkId != 0)
                    {
                        ComponentSegment.FreeChunk(prevRowChunkId);
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
                    curChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkId);
                    curElements = (RowVersionStorageElement*)(curChunkHeader + 1);
                }
            }

            // If we are roll-backing, re-correct the version to keep the last before minTick
            if (isRollback)
            {
                firstChunkHeader->FirstItemIndex = prevRowFirstIndex;
            }
            else if (prevRowChunkId != 0)
            {
                ComponentSegment.FreeChunk(prevRowChunkId);
            }

            curChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(rowInfo.RowVersionChunkId, dirtyPage: true);
            curElements = (RowVersionStorageElement*)(curChunkHeader + 1);

            // Clear the entry of the transaction row version if it's a rollback
            if (isRollback)
            {
                var rowChunkId = curElements[rowInfo.RowVersionIndexInChunk].RowChunkId;
                if (rowChunkId != 0)
                {
                    ComponentSegment.FreeChunk(rowChunkId);
                }
                curElements[rowInfo.RowVersionIndexInChunk].Tick = 0;
                curElements[rowInfo.RowVersionIndexInChunk].RowChunkId = 0;
            }
            // Remove the Exclusive flag on the row that belongs to the transaction in make it available to all accesses
            else
            {
                curElements[rowInfo.RowVersionIndexInChunk].Tick &= ~RowVersionTransactionExclusiveFlag;
            }

            // Check if we can/have to delete the whole row version, either:
            //  - All the items are from a tick older than the required one
            //  - All are older except the last one but this is is a deleted row we're committing
            //  - All are older except the last one but this is a created row we're roll-backing
            if ((itemLeftCount < 0) ||
                ((itemLeftCount == 0) && ((rowInfo.Operations & Transaction.ComponentInfo.OperationType.Deleted) != 0) && (isRollback == false)) ||
                ((itemLeftCount == 0) && ((rowInfo.Operations & Transaction.ComponentInfo.OperationType.Created) != 0) &&  isRollback)) 
            {
                Debug.Assert(curElements[rowInfo.RowVersionIndexInChunk].RowChunkId == 0, "Current Row Version point to an allocated Row Component, should be 0.");

                // Remove the index
                _mainIndex.Remove(pk, out _);

                // Free the Row Version chain chunks
                curChunkId = rowInfo.RowVersionFirstChunkId;
                do
                {
                    curChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(curChunkId);
                    var nextChunkIdx = curChunkHeader->NextChunkId;
                    _versionTableAccessor.Segment.FreeChunk(curChunkId);
                    curChunkId = nextChunkIdx;
                } while (curChunkId != 0);
            }

            else if (dirtyFirstChunk) _versionTableAccessor.DirtyChunk(rowInfo.RowVersionFirstChunkId);

            // Cleanups
            firstChunkHeader->Control.ExitExclusiveAccess();
            _versionTableAccessor.UnpinChunk(rowInfo.RowVersionFirstChunkId);
        }

        internal void UpdateRow(ref Transaction.ComponentInfo.RowInfo rowInfo, long rowTick, bool isDelete, bool exclusiveRow)
        {
            var rowChunkHeader = (RowVersionStorageHeader*)_versionTableAccessor.GetChunkAddress(rowInfo.RowVersionChunkId, dirtyPage: true);
            var elements = (RowVersionStorageElement*)(rowChunkHeader + 1);
            elements[rowInfo.RowVersionIndexInChunk].Tick = rowTick | (exclusiveRow ? RowVersionTransactionExclusiveFlag : 0);
            if (isDelete)
            {
                var rowChunkId = elements[rowInfo.RowVersionIndexInChunk].RowChunkId;
                if (rowChunkId != 0)
                {
                    ComponentSegment.FreeChunk(rowChunkId);
                }
                elements[rowInfo.RowVersionIndexInChunk].RowChunkId = 0;
            }
        }
    }
}