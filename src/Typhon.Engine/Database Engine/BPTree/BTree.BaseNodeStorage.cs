// unset

using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.BPTree;

public abstract partial class BTree<TKey>
{
    public abstract class BaseNodeStorage
    {
        protected internal BTree<TKey> Owner;

        protected internal ChunkBasedSegment Segment;
        // private ThreadLocal<ChunkRandomAccessor> _chunkAccessorThreadLocal;
        // protected ChunkRandomAccessor ChunkAccessor => _chunkAccessorThreadLocal.Value;

        internal virtual void Initialize(BTree<TKey> owner, ChunkBasedSegment segment)
        {
            Owner = owner;
            Segment = segment;
            // _chunkAccessorThreadLocal = new ThreadLocal<ChunkRandomAccessor>(() => segment.CreateChunkRandomAccessor(ChunkRandomAccessorPagedCount));
        }

        public void CommitChanges(ChunkRandomAccessor accessor) => accessor.CommitChanges();

        #region Chunk Properties Access

        public abstract void InitializeNode(NodeWrapper node, NodeStates states, ChunkRandomAccessor accessor);
        public abstract int GetNodeCapacity();
        public abstract NodeWrapper GetLeftNode(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract void SetLeftNode(NodeWrapper node, int leftNodeId, ChunkRandomAccessor accessor);
        public abstract NodeWrapper GetPreviousNode(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract void SetPreviousNode(NodeWrapper node, int previousNodeId, ChunkRandomAccessor accessor);
        public abstract NodeWrapper GetNextNode(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract void SetNextNode(NodeWrapper node, int nextNodeId, ChunkRandomAccessor accessor);
        public abstract KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ChunkRandomAccessor accessor);
        public abstract void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ChunkRandomAccessor accessor);
        public abstract int GetCount(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract void SetCount(NodeWrapper node, int value, ChunkRandomAccessor accessor);
        public abstract int GetStart(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract void SetStart(NodeWrapper node, int value, ChunkRandomAccessor accessor);
        public abstract int GetEnd(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract NodeStates GetNodeStates(NodeWrapper node, ChunkRandomAccessor accessor);

        #endregion

        #region Chunk Operations

        public abstract void PushFirst(NodeWrapper node, KeyValueItem item, ChunkRandomAccessor accessor);
        public abstract void PushLast(NodeWrapper node, KeyValueItem item, ChunkRandomAccessor accessor);
        public abstract int Append(int bufferId, int value, ChunkRandomAccessor accessor);
        public abstract void Insert(NodeWrapper node, int index, KeyValueItem item, ChunkRandomAccessor accessor);
        public abstract int CreateBuffer(ChunkRandomAccessor accessor);
        public abstract VariableSizedBufferAccessor<int> GetBufferReadOnlyAccessor(int bufferId, ChunkRandomAccessor accessor);
        public abstract int RemoveFromBuffer(int bufferId, int elementId, int value, ChunkRandomAccessor accessor);
        public abstract void DeleteBuffer(int bufferId, ChunkRandomAccessor accessor);
        public abstract NodeWrapper GetLastChild(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract NodeWrapper GetFirstChild(NodeWrapper node, ChunkRandomAccessor accessor);
        public virtual NodeWrapper GetChild(NodeWrapper node, int index, ChunkRandomAccessor accessor)
        {
            if (node.GetIsLeaf(accessor))
            {
                return default;
            }
            if (index < 0)
            {
                return GetLeftNode(node, accessor);
            }
            return new NodeWrapper(this, GetItem(node, index, true, accessor).Value);
        }
        public abstract void IncrementStart(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract void DecrementStart(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract bool IsRotated(NodeWrapper node, ChunkRandomAccessor accessor);
        public abstract int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer, ChunkRandomAccessor accessor);

        #endregion

        public abstract NodeWrapper SplitRight(NodeWrapper node, NodeStates nodeStates, ChunkRandomAccessor accessor);
        public abstract KeyValueItem RemoveAt(NodeWrapper node, int index, ChunkRandomAccessor accessor);
        public abstract void MergeLeft(NodeWrapper left, NodeWrapper right, ChunkRandomAccessor accessor);
    }
}