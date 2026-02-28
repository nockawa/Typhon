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

        public void CommitChanges(ref ChunkAccessor accessor) => accessor.CommitChanges();

        #region Chunk Properties Access

        public abstract void InitializeNode(NodeWrapper node, NodeStates states, ref ChunkAccessor accessor);
        public NodeWrapper LoadNode(int nodeId) => new(this, nodeId);
        public abstract int GetNodeCapacity();
        public abstract NodeWrapper GetLeftNode(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void SetLeftNode(NodeWrapper node, int leftNodeId, ref ChunkAccessor accessor);
        public abstract NodeWrapper GetPreviousNode(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void SetPreviousNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor accessor);
        public abstract NodeWrapper GetNextNode(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void SetNextNode(NodeWrapper node, int nextNodeId, ref ChunkAccessor accessor);
        public abstract KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ref ChunkAccessor accessor);
        public abstract void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ref ChunkAccessor accessor);
        public abstract int GetCount(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void SetCount(NodeWrapper node, int value, ref ChunkAccessor accessor);
        public abstract int GetStart(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void SetStart(NodeWrapper node, int value, ref ChunkAccessor accessor);
        public abstract int GetEnd(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract NodeStates GetNodeStates(NodeWrapper node, ref ChunkAccessor accessor);

        public abstract int GetContentionHint(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void SetContentionHint(NodeWrapper node, int value, ref ChunkAccessor accessor);

        /// <summary>
        /// Returns a ref to the node's OlcVersion field for optimistic lock coupling.
        /// Uses dirty=false because optimistic readers never dirty pages; writers must separately call GetChunk(id, true) before mutating data.
        /// </summary>
        public abstract ref int GetOlcVersionRef(int chunkId, ref ChunkAccessor accessor);

        #endregion

        #region Chunk Operations

        public abstract void PushFirst(NodeWrapper node, KeyValueItem item, ref ChunkAccessor accessor);
        public abstract void PushLast(NodeWrapper node, KeyValueItem item, ref ChunkAccessor accessor);
        public abstract int Append(int bufferId, int value, ref ChunkAccessor accessor);
        public abstract void Insert(NodeWrapper node, int index, KeyValueItem item, ref ChunkAccessor accessor);
        public abstract int CreateBuffer(ref ChunkAccessor accessor);
        public abstract VariableSizedBufferAccessor<int> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor accessor);
        public abstract int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor accessor);
        public abstract void DeleteBuffer(int bufferId, ref ChunkAccessor accessor);
        public abstract NodeWrapper GetLastChild(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract NodeWrapper GetFirstChild(NodeWrapper node, ref ChunkAccessor accessor);
        public NodeWrapper GetChild(NodeWrapper node, int index, ref ChunkAccessor accessor)
        {
            if (node.GetIsLeaf(ref accessor))
            {
                return default;
            }

            var child = index < 0 ? GetLeftNode(node, ref accessor) : new NodeWrapper(this, GetItem(node, index, true, ref accessor).Value);
            
            // Read child's StateFlags to cache IsLeaf — the child chunk will be accessed
            // immediately after (by BinarySearch/Find), so this read is essentially free.
            bool isLeaf = (GetNodeStates(child, ref accessor) & NodeStates.IsLeaf) != 0;
            return new NodeWrapper(this, child.ChunkId, isLeaf);
        }
        public abstract void IncrementStart(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract void DecrementStart(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract bool IsRotated(NodeWrapper node, ref ChunkAccessor accessor);
        public abstract int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer, ref ChunkAccessor accessor);

        #endregion

        public abstract NodeWrapper SplitRight(NodeWrapper node, NodeStates nodeStates, ref ChunkAccessor accessor);
        public abstract KeyValueItem RemoveAt(NodeWrapper node, int index, ref ChunkAccessor accessor);
        public abstract void MergeLeft(NodeWrapper left, NodeWrapper right, ref ChunkAccessor accessor);

        /// <summary>
        /// Returns the high key (upper bound) for B-link tree range checks.
        /// Default returns the last key in the node. Overridden by L16/L32/L64 to read the explicit HighKey field.
        /// </summary>
        public virtual TKey GetHighKey(NodeWrapper node, ref ChunkAccessor accessor) => GetItem(node, GetCount(node, ref accessor) - 1, true, ref accessor).Key;

        /// <summary>
        /// Sets the high key for the node. Default is a no-op (for types without explicit HighKey like String64).
        /// </summary>
        public virtual void SetHighKey(NodeWrapper node, TKey key, ref ChunkAccessor accessor) { }
    }
}