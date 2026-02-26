// unset

namespace Typhon.Engine.BPTree;

public abstract partial class BTree<TKey>
{
    /// <summary>
    /// Enumerates all key-value entries in the BTree by walking the leaf-level linked list
    /// from left to right. Items are yielded in ascending key order.
    /// </summary>
    /// <remarks>
    /// <para>The enumerator holds a shared lock on the BTree (preventing structural modifications)
    /// and owns a <see cref="ChunkAccessor"/>. Both are released on <see cref="Dispose"/>.</para>
    /// <para>The caller must be inside an epoch scope (e.g., via a Transaction).</para>
    /// <para>Supports <c>foreach</c> via duck-typing (GetEnumerator/MoveNext/Current/Dispose).</para>
    /// </remarks>
    public ref struct LeafEnumerator
    {
        private readonly BTree<TKey> _tree;
        private ChunkAccessor _accessor;
        private NodeWrapper _currentNode;
        private int _currentIndex;
        private int _nodeItemCount;
        private bool _disposed;

        internal LeafEnumerator(BTree<TKey> tree)
        {
            _tree = tree;
            _accessor = tree._segment.CreateChunkAccessor();
            _currentNode = tree.LinkList;
            _currentIndex = -1;
            _nodeItemCount = _currentNode.IsValid ? _currentNode.GetCount(ref _accessor) : 0;
            _disposed = false;
        }

        /// <summary>Returns this enumerator (required for foreach pattern).</summary>
        public LeafEnumerator GetEnumerator() => this;

        /// <summary>Gets the current key-value item.</summary>
        public KeyValueItem Current => _currentNode.GetItem(_currentIndex, ref _accessor);

        /// <summary>Advances to the next entry, traversing leaf nodes as needed.</summary>
        public bool MoveNext()
        {
            if (!_currentNode.IsValid)
            {
                return false;
            }

            _currentIndex++;

            if (_currentIndex < _nodeItemCount)
            {
                return true;
            }

            // Move to next leaf node in the linked list
            _currentNode = _currentNode.GetNext(ref _accessor);
            if (!_currentNode.IsValid)
            {
                return false;
            }

            _currentIndex = 0;
            _nodeItemCount = _currentNode.GetCount(ref _accessor);
            return _nodeItemCount > 0;
        }

        /// <summary>Releases the chunk accessor and shared lock.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _accessor.Dispose();
                _tree._access.ExitSharedAccess();
            }
        }
    }
}
