// unset

using System.Collections.Generic;

namespace Typhon.Engine.BPTree
{
    public abstract partial class BTree<TKey, TChunk, TStorage>
    {
        public abstract class BaseNodeStorage
        {
            protected internal BTree<TKey, TChunk, TStorage> Owner;
            protected ChunkBasedSegment Segment;

            protected ChunkReadWriteRandomAccessor<TChunk> ChunkRWA;
            protected ChunkReadOnlyRandomAccessor<TChunk> ChunkROA;

            internal void Initialize(BTree<TKey, TChunk, TStorage> owner, ChunkBasedSegment segment)
            {
                Owner = owner;
                Segment = segment;
                ChunkRWA = Segment.GetChunkReadWriteRandomAccessor<TChunk>(4);
                ChunkROA = Segment.GetChunkReadOnlyRandomAccessor<TChunk>(4);
            }

            #region Chunk Properties Access

            public abstract void InitializeNode(NodeWrapper node, NodeStates states);
            public abstract int GetNodeCapacity();
            public abstract NodeWrapper GetLeftNode(NodeWrapper node);
            public abstract void SetLeftNode(NodeWrapper node, int leftNodeId);
            public abstract NodeWrapper GetPreviousNode(NodeWrapper node);
            public abstract void SetPreviousNode(NodeWrapper node, int previousNodeId);
            public abstract NodeWrapper GetNextNode(NodeWrapper node);
            public abstract void SetNextNode(NodeWrapper node, int nextNodeId);
            public abstract KeyValueItem GetItem(NodeWrapper node, int index, bool adjust);
            public abstract void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust);
            public abstract int GetCount(NodeWrapper node);
            public abstract void SetCount(NodeWrapper node, int value);
            public abstract int GetStart(NodeWrapper node);
            public abstract void SetStart(NodeWrapper node, int value);
            public abstract int GetEnd(NodeWrapper node);
            public abstract NodeStates GetNodeStates(NodeWrapper node);

            #endregion

            #region Chunk Operations

            public abstract void PushFirst(NodeWrapper node, KeyValueItem item);
            public abstract void PushLast(NodeWrapper node, KeyValueItem item);
            public abstract void AppendFirst(NodeWrapper node, KeyValueItem value);
            public abstract void AppendLast(NodeWrapper node, KeyValueItem item);
            public abstract NodeWrapper GetLastChild(NodeWrapper node);
            public abstract NodeWrapper GetFirstChild(NodeWrapper node);
            public abstract NodeWrapper GetChild(NodeWrapper node, int index);
            public abstract void IncrementStart(NodeWrapper node);
            public abstract void DecrementStart(NodeWrapper node);
            public abstract bool IsRotated(NodeWrapper node);
            public abstract int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer);
            public abstract void Insert(NodeWrapper node, int index, KeyValueItem item);

            #endregion

            public abstract NodeWrapper SplitRight(NodeWrapper node, NodeStates nodeStates);
            public abstract KeyValueItem RemoveAt(NodeWrapper node, int index);
            public abstract void MergeLeft(NodeWrapper left, NodeWrapper right);
        }
    }
}