// unset

using System.Collections.Generic;

namespace Typhon.Engine.BPTree;

public abstract partial class BTree<TKey>
{
    public abstract class BaseNodeStorage
    {
        protected internal BTree<TKey> Owner;

        protected internal ChunkBasedSegment Segment;

        internal virtual void Initialize(BTree<TKey> owner, ChunkBasedSegment segment)
        {
            Owner = owner;
            Segment = segment;
        }

        public void CommitChanges(ref EpochChunkAccessor accessor) => accessor.CommitChanges();

        #region Chunk Properties Access

        public abstract void InitializeNode(NodeWrapper node, NodeStates states, ref EpochChunkAccessor accessor);
        public NodeWrapper LoadNode(int nodeId) => new(this, nodeId);
        public abstract int GetNodeCapacity();
        public abstract NodeWrapper GetLeftNode(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract void SetLeftNode(NodeWrapper node, int leftNodeId, ref EpochChunkAccessor accessor);
        public abstract NodeWrapper GetPreviousNode(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract void SetPreviousNode(NodeWrapper node, int previousNodeId, ref EpochChunkAccessor accessor);
        public abstract NodeWrapper GetNextNode(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract void SetNextNode(NodeWrapper node, int nextNodeId, ref EpochChunkAccessor accessor);
        public abstract KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ref EpochChunkAccessor accessor);
        public abstract void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ref EpochChunkAccessor accessor);
        public abstract int GetCount(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract void SetCount(NodeWrapper node, int value, ref EpochChunkAccessor accessor);
        public abstract int GetStart(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract void SetStart(NodeWrapper node, int value, ref EpochChunkAccessor accessor);
        public abstract int GetEnd(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract NodeStates GetNodeStates(NodeWrapper node, ref EpochChunkAccessor accessor);

        #endregion

        #region Chunk Operations

        public abstract void PushFirst(NodeWrapper node, KeyValueItem item, ref EpochChunkAccessor accessor);
        public abstract void PushLast(NodeWrapper node, KeyValueItem item, ref EpochChunkAccessor accessor);
        public abstract int Append(int bufferId, int value, ref EpochChunkAccessor accessor);
        public abstract void Insert(NodeWrapper node, int index, KeyValueItem item, ref EpochChunkAccessor accessor);
        public abstract int CreateBuffer(ref EpochChunkAccessor accessor);
        public abstract VariableSizedBufferAccessor<int> GetBufferReadOnlyAccessor(int bufferId, ref EpochChunkAccessor accessor);
        public abstract int RemoveFromBuffer(int bufferId, int elementId, int value, ref EpochChunkAccessor accessor);
        public abstract void DeleteBuffer(int bufferId, ref EpochChunkAccessor accessor);
        public abstract NodeWrapper GetLastChild(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract NodeWrapper GetFirstChild(NodeWrapper node, ref EpochChunkAccessor accessor);
        public virtual NodeWrapper GetChild(NodeWrapper node, int index, ref EpochChunkAccessor accessor)
        {
            if (node.GetIsLeaf(ref accessor))
            {
                return default;
            }
            if (index < 0)
            {
                return GetLeftNode(node, ref accessor);
            }
            return new NodeWrapper(this, GetItem(node, index, true, ref accessor).Value);
        }
        public abstract void IncrementStart(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract void DecrementStart(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract bool IsRotated(NodeWrapper node, ref EpochChunkAccessor accessor);
        public abstract int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer, ref EpochChunkAccessor accessor);

        #endregion

        public abstract NodeWrapper SplitRight(NodeWrapper node, NodeStates nodeStates, ref EpochChunkAccessor accessor);
        public abstract KeyValueItem RemoveAt(NodeWrapper node, int index, ref EpochChunkAccessor accessor);
        public abstract void MergeLeft(NodeWrapper left, NodeWrapper right, ref EpochChunkAccessor accessor);
    }
}