// unset

using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.BPTree
{
    public abstract partial class BTree<TKey>
    {
        public abstract class BaseNodeStorage
        {
            protected internal BTree<TKey> Owner;
            private ThreadLocal<ChunkRandomAccessor> _chunkAccessorThreadLocal;
            protected ChunkRandomAccessor ChunkAccessor => _chunkAccessorThreadLocal.Value;

            internal virtual void Initialize(BTree<TKey> owner, ChunkBasedSegment segment)
            {
                Owner = owner;
                _chunkAccessorThreadLocal = new ThreadLocal<ChunkRandomAccessor>(() => segment.CreateChunkRandomAccessor(ChunkRandomAccessorPagedCount));
            }

            public void CommitChanges() => ChunkAccessor.CommitChanges();

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
            public abstract int Append(int bufferId, int value);
            public abstract void Insert(NodeWrapper node, int index, KeyValueItem item);
            public abstract int CreateBuffer();
            public abstract VariableSizedBufferAccessor<int> GetBufferReadOnlyAccessor(int bufferId);
            public abstract int RemoveFromBuffer(int bufferId, int elementId, int value);
            public abstract void DeleteBuffer(int bufferId);
            public abstract NodeWrapper GetLastChild(NodeWrapper node);
            public abstract NodeWrapper GetFirstChild(NodeWrapper node);
            public virtual NodeWrapper GetChild(NodeWrapper node, int index)
            {
                if (node.IsLeaf) return default;
                if (index < 0)
                {
                    return GetLeftNode(node);
                }
                return new NodeWrapper(this, GetItem(node, index, true).Value);
            }
            public abstract void IncrementStart(NodeWrapper node);
            public abstract void DecrementStart(NodeWrapper node);
            public abstract bool IsRotated(NodeWrapper node);
            public abstract int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer);

            #endregion

            public abstract NodeWrapper SplitRight(NodeWrapper node, NodeStates nodeStates);
            public abstract KeyValueItem RemoveAt(NodeWrapper node, int index);
            public abstract void MergeLeft(NodeWrapper left, NodeWrapper right);
        }
    }
}