// unset

using System.Threading;

namespace Typhon.Engine.BPTree;

public abstract partial class BTree<TKey>
{
    /// <summary>
    /// Enumerates all key-value entries in the BTree by walking the leaf-level linked list
    /// from left to right. Items are yielded in ascending key order.
    /// </summary>
    /// <remarks>
    /// <para>Uses per-leaf OLC validation: reads a leaf's version before reading its entries, then validates after. If the leaf was concurrently modified,
    /// the enumerator re-reads that leaf from the beginning (not the whole tree).</para>
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
        private int _leafVersion;
        private bool _disposed;

        internal LeafEnumerator(BTree<TKey> tree)
        {
            _tree = tree;
            _accessor = tree._segment.CreateChunkAccessor();
            _currentNode = tree.LinkList;
            _currentIndex = -1;
            _disposed = false;

            if (_currentNode.IsValid)
            {
                ReadLeafState();
            }
            else
            {
                _nodeItemCount = 0;
                _leafVersion = 0;
            }
        }

        /// <summary>Reads the current leaf's version and item count under OLC.</summary>
        private void ReadLeafState()
        {
            while (true)
            {
                var latch = _currentNode.GetLatch(ref _accessor);
                var version = latch.ReadVersion();
                if (version == 0)
                {
                    // Locked or obsolete — spin-wait and retry
                    Thread.SpinWait(1);
                    continue;
                }

                _nodeItemCount = _currentNode.GetCount(ref _accessor);

                if (latch.ValidateVersion(version))
                {
                    _leafVersion = version;
                    return;
                }
                // Version changed — retry this leaf
            }
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

            // Before following the Next pointer, validate the leaf version.
            // If the leaf was modified during our scan, re-read from the beginning of this leaf.
            var latch = _currentNode.GetLatch(ref _accessor);
            if (!latch.ValidateVersion(_leafVersion))
            {
                // Leaf was modified — re-read from beginning
                _currentIndex = -1;
                ReadLeafState();
                return MoveNext();
            }

            // Move to next leaf node in the linked list
            _currentNode = _currentNode.GetNext(ref _accessor);
            if (!_currentNode.IsValid)
            {
                return false;
            }

            _currentIndex = 0;
            ReadLeafState();
            return _nodeItemCount > 0;
        }

        /// <summary>Releases the chunk accessor.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _accessor.Dispose();
            }
        }
    }
}
