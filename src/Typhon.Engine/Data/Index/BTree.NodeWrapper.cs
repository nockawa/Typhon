// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Typhon.Engine;

public abstract partial class BTree<TKey>
{
    [DebuggerDisplay("ChunkId: {ChunkId}, IsValid: {IsValid}")]
    [DebuggerTypeProxy(typeof(BTree<>.NodeWrapper.DebugView))]
    public readonly struct NodeWrapper : IEquatable<NodeWrapper>
    {
        private readonly BaseNodeStorage _storage;
        public readonly int ChunkId;
        private readonly byte _flags; // bit 0: isLeaf, bit 1: valid (flag was set at construction)

        public NodeWrapper(BaseNodeStorage storage, int chunkId)
        {
            _storage = storage;
            ChunkId = chunkId;
            _flags = 0;
        }

        internal NodeWrapper(BaseNodeStorage storage, int chunkId, bool isLeaf)
        {
            _storage = storage;
            ChunkId = chunkId;
            _flags = (byte)(0x02 | (isLeaf ? 0x01 : 0));
        }

        #region Node Properties

        /// <summary>
        /// Creates an OlcLatch for this node by obtaining a ref to its OlcVersion field.
        /// </summary>
        internal OlcLatch GetLatch(ref ChunkAccessor accessor) => new OlcLatch(ref _storage.GetOlcVersionRef(ChunkId, ref accessor));

        public bool IsValid => _storage != null && ChunkId != 0;

        public bool GetIsLeaf(ref ChunkAccessor accessor)
        {
            if ((_flags & 0x02) != 0)
            {
                return (_flags & 0x01) != 0;
            }
            return (_storage.GetNodeStates(this, ref accessor) & NodeStates.IsLeaf) != 0;
        }
        public int GetCapacity() => _storage.GetNodeCapacity();
        public bool GetIsFull(ref ChunkAccessor accessor) => GetCount(ref accessor) == GetCapacity();
        public bool GetIsHalfFull(ref ChunkAccessor accessor) => GetCount(ref accessor) >= (GetCapacity() / 2);
        public int GetLength(ref ChunkAccessor accessor) => GetCount(ref accessor);

        public int GetCount(ref ChunkAccessor accessor) => _storage.GetCount(this, ref accessor);

        private void SetCount(int value, ref ChunkAccessor accessor) => _storage.SetCount(this, value, ref accessor);

        public int GetStart(ref ChunkAccessor accessor) => _storage.GetStart(this, ref accessor);

        private int GetEnd(ref ChunkAccessor accessor) => _storage.GetEnd(this, ref accessor);

        public KeyValueItem GetFirst(ref ChunkAccessor accessor) => _storage.GetItem(this, 0, true, ref accessor);

        public void SetFirst(KeyValueItem value, ref ChunkAccessor accessor) => _storage.SetItem(this, 0, value, true, ref accessor);

        public KeyValueItem GetLast(ref ChunkAccessor accessor) => _storage.GetItem(this, _storage.GetCount(this, ref accessor) - 1, true, ref accessor);

        public TKey GetHighKey(ref ChunkAccessor accessor) => _storage.GetHighKey(this, ref accessor);
        private void SetHighKey(TKey key, ref ChunkAccessor accessor) => _storage.SetHighKey(this, key, ref accessor);

        public int GetContentionHint(ref ChunkAccessor accessor) => _storage.GetContentionHint(this, ref accessor);
        internal void SetContentionHint(int value, ref ChunkAccessor accessor) => _storage.SetContentionHint(this, value, ref accessor);

        public void SetLast(KeyValueItem value, ref ChunkAccessor accessor) 
            => _storage.SetItem(this, _storage.GetCount(this, ref accessor) - 1, value, true, ref accessor);

        public NodeWrapper GetPrevious(ref ChunkAccessor accessor) => _storage.GetPreviousNode(this, ref accessor);

        private void SetPrevious(NodeWrapper value, ref ChunkAccessor accessor) => _storage.SetPreviousNode(this, value.ChunkId, ref accessor);

        public NodeWrapper GetNext(ref ChunkAccessor accessor) => _storage.GetNextNode(this, ref accessor);

        private void SetNext(NodeWrapper value, ref ChunkAccessor accessor) => _storage.SetNextNode(this, value.ChunkId, ref accessor);

        public NodeWrapper GetLeft(ref ChunkAccessor accessor) => _storage.GetLeftNode(this, ref accessor);

        public void SetLeft(NodeWrapper value, ref ChunkAccessor accessor) => _storage.SetLeftNode(this, value.ChunkId, ref accessor);

        public KeyValueItem GetItem(int index, ref ChunkAccessor accessor) => _storage.GetItem(this, index, true, ref accessor);
        private void SetItem(int index, KeyValueItem value, ref ChunkAccessor accessor) => _storage.SetItem(this, index, value, true, ref accessor);

        #endregion

        #region Node Operations

        public void PushFirst(KeyValueItem item, ref ChunkAccessor accessor) => _storage.PushFirst(this, item, ref accessor);
        public void PushLast(KeyValueItem item, ref ChunkAccessor accessor) => _storage.PushLast(this, item, ref accessor);

        private void MergeLeft(NodeWrapper right, ref ChunkAccessor accessor)
        {
            Activity activity = null;
            if (TelemetryConfig.BTreeActive)
            {
                activity = TyphonActivitySource.StartActivity("BTree.NodeMerge");
                activity?.SetTag(TyphonSpanAttributes.IndexNodeMerge, true);
            }

            try
            {
                _storage.MergeLeft(this, right, ref accessor);
            }
            finally
            {
                activity?.Dispose();
            }
        }

        public NodeWrapper GetChild(int index, ref ChunkAccessor accessor) => _storage.GetChild(this, index, ref accessor);

        public NodeWrapper GetLastChild(ref ChunkAccessor accessor) => _storage.GetLastChild(this, ref accessor);

        public NodeWrapper GetFirstChild(ref ChunkAccessor accessor) => _storage.GetFirstChild(this, ref accessor);

        // Insert/Remove dispatch removed — BTree.InsertIterative/RemoveIterative handle
        // the full root-to-leaf descent and upward propagation iteratively.

        /// <summary>
        /// Splits a leaf right and updates the doubly-linked list pointers.
        /// Used by both regular full-leaf splits and contention splits.
        /// </summary>
        internal NodeWrapper SplitLeafRight(ref ChunkAccessor accessor)
        {
            var right = SplitRight(NodeStates.IsLeaf, ref accessor);
            var next = GetNext(ref accessor);
            if (next.IsValid)
            {
                next.SetPrevious(right, ref accessor);
                right.SetNext(next, ref accessor);
            }
            right.SetPrevious(this, ref accessor);
            SetNext(right, ref accessor);
            return right;
        }

        internal KeyValueItem? InsertLeaf(ref InsertArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor, bool forceSplit = false)
        {
            KeyValueItem? rightLeaf = null;

            var index = Find(args.Key, args.KeyComparer, ref accessor);

            if (index < 0)
            {
                index = ~index;

                int value = args.GetValue();
                if (_storage.Owner.AllowMultiple)
                {
                    var bufferId = _storage.CreateBuffer(ref accessor);
                    args.ElementId = _storage.Append(bufferId, value, ref accessor);
                    args.BufferRootId = bufferId;
                    value = bufferId;
                }
                var item = new KeyValueItem(args.Key, value); // item to add

                if (!GetIsFull(ref accessor)) // if there is space, add and return.
                {
                    Insert(index, item, ref accessor); // insert value and return.
                }
                else // cant add, spill or split
                {
                    if (!forceSplit && CanSpillTo(GetPrevious(ref accessor), ref accessor))
                    {
                        var first = InsertPopFirst(index, item, ref accessor);
                        GetPrevious(ref accessor).PushLast(first, ref accessor); // move the smallest item to left sibling.

                        // update ancestors key.
                        var newSeparator = GetFirst(ref accessor).Key;
                        var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref pl, newSeparator);
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl, ref accessor);

                        // Left's HighKey must match the new separator (spill moved the boundary).
                        GetPrevious(ref accessor).SetHighKey(newSeparator, ref accessor);

                        Validate();
                        Validate();
                    }
                    else if (!forceSplit && CanSpillTo(GetNext(ref accessor), ref accessor))
                    {
                        var last = InsertPopLast(index, item, ref accessor);
                        GetNext(ref accessor).PushFirst(last, ref accessor);

                        // update ancestors key.
                        var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref pr, last.Key);
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr, ref accessor);

                        // Current's HighKey must match the new separator (spill moved the boundary).
                        SetHighKey(last.Key, ref accessor);

                        Validate();
                        Validate();
                    }
                    else // split, then promote middle item
                    {
                        var rightNode = SplitNodeRight(this, ref accessor);

                        // insert item and find middle value to promote
                        if (index <= GetCount(ref accessor))
                        {
                            // when adding item to this node, pop last item and give it to right node.
                            // this way, this and right split always have equal length or maximum 1 difference. (also avoids overflow when capacity = 1)
                            rightNode.PushFirst(InsertPopLast(index, item, ref accessor), ref accessor);
                        }
                        else if (index > GetCount(ref accessor))
                        {
                            rightNode.Insert(index - GetCount(ref accessor), item, ref accessor);
                        }

                        rightLeaf = new KeyValueItem(rightNode.GetFirst(ref accessor).Key, rightNode.ChunkId);

                        Validate();
                        Validate();
                    }
                }

                // splits right side to new node and keeps left side for current node.
                NodeWrapper SplitNodeRight(NodeWrapper left, ref ChunkAccessor ca) => left.SplitLeafRight(ref ca);

                bool CanSpillTo(NodeWrapper leaf, ref ChunkAccessor ca)
                {
                    return leaf.IsValid && !leaf.GetIsFull(ref ca);
                }
            }
            else
            {
                if (_storage.Owner.AllowMultiple)
                {
                    var curItem = GetItem(index, ref accessor);
                    args.ElementId = _storage.Append(curItem.Value, args.GetValue(), ref accessor);
                    args.BufferRootId = curItem.Value;
                }
                // Unique index: GetValue() not called, so Added stays false.
                // AddOrUpdateCore detects !Added && !AllowMultiple and throws UniqueConstraintViolationException.
            }

            return rightLeaf;
        }

        /// <summary>
        /// Handles a promoted key from a child split during insert. Either inserts the promoted key at this internal node, spills to a sibling, or splits
        /// this node (returning a new promoted key). Called iteratively during upward propagation from <see cref="BTree{TKey}.InsertIterative"/>.
        /// </summary>
        internal KeyValueItem? HandlePromotedInsert(int childIndex, KeyValueItem middle, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            // +1 because middle is always right side which is fresh node.
            // items at index already point to left node after split. so middle must go after index.
            int index = childIndex + 1;

            KeyValueItem? rightChild = null;
            if (!GetIsFull(ref accessor))
            {
                Insert(index, middle, ref accessor);
            }
            else
            {
                // if left sibling has space, spill left child of this item to left sibling.
                if (CanSpillTo(relatives.GetLeftSibling(ref accessor), ref accessor, out var leftSibling))
                {
                    #region Fix Pointers after share
                    // give first item to left sibling.
                    //
                    //        [x][x]       [F][x]
                    //       /   \  \     // \\\ \
                    //
                    //        [x][x][F]       [x]
                    //       /   \  \ \\     /// \
                    #endregion

                    var first = InsertPopFirst(index, middle, ref accessor);

                    // swap left and right nodes
                    var temp = GetLeft(ref accessor).ChunkId;
                    SetLeft(new NodeWrapper(_storage, first.Value), ref accessor);
                    first = new KeyValueItem(first.Key, temp);

                    var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                    KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                    relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl, ref accessor);

                    leftSibling.PushLast(first, ref accessor);

                    Validate();
                    Validate();
                }
                else if (CanSpillTo(relatives.GetRightSibling(ref accessor), ref accessor, out var rightSibling)) // if right sibling has space
                {
                    #region Fix Pointers after share
                    // give last item to right sibling.
                    //
                    //        [x][L]       [x][x]
                    //       /   \ \\     /// \  \
                    //
                    //        [x]        [L][x][x]
                    //       /   \      // \\\ \  \
                    #endregion

                    var last = InsertPopLast(index, middle, ref accessor);

                    // swap left and right node
                    var temp = rightSibling.GetLeft(ref accessor).ChunkId;
                    rightSibling.SetLeft(new NodeWrapper(_storage, last.Value), ref accessor);
                    last = new KeyValueItem(last.Key, temp);

                    var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                    KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                    relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr, ref accessor);

                    rightSibling.PushFirst(last, ref accessor);

                    Validate();
                    Validate();
                }
                else // split, then promote middle item
                {
                    #region Fix Pointers after split
                    // ==============================================================
                    //
                    // if [left] and [right] were leafs
                    //
                    //     [][]...[N]...[][]
                    //               \   <= if we were here,
                    //             [left][mid][right]
                    //
                    // for insertion, make new key-node item with [mid] as key and [right] as node.
                    // simply add this item next to [N].
                    //
                    //     [][]...[N][mid]..[][]
                    //               \    \
                    //            [left][right]
                    //
                    // ==============================================================
                    //
                    // if [left] and [right] were internal nodes.
                    //
                    //     [middle]        [rightNode]
                    //            \\       *         \     <= left pointer of [rightNode] is null
                    //
                    //  Becomes
                    //
                    //    [middle]
                    //           \
                    //         [rightNode]
                    //        //          \
                    //
                    // ==============================================================
                    #endregion

                    var rightNode = SplitRight(NodeStates.None, ref accessor);

                    // find middle key to promote
                    if (index < GetCount(ref accessor))
                    {
                        middle = InsertPopLast(index, middle, ref accessor);
                    }
                    else if (index > GetCount(ref accessor))
                    {
                        middle = rightNode.InsertPopFirst(index - GetCount(ref accessor), middle, ref accessor);
                    }

                    rightNode.SetLeft(new NodeWrapper(_storage, middle.Value), ref accessor);
                    middle = new KeyValueItem(middle.Key, rightNode.ChunkId);
                    rightChild = middle;

                    Validate();
                    Validate();
                }
            }

            return rightChild;

            bool CanSpillTo(NodeWrapper node, ref ChunkAccessor ca, out NodeWrapper iNode)
            {
                if (node.IsValid && !node.GetIsLeaf(ref ca))
                {
                    iNode = node;
                    return !iNode.GetIsFull(ref ca);
                }

                iNode = default;
                return false;
            }
        }

        public bool RemoveLeaf(ref RemoveArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            var merge = false;
            var index = Find(args.Key, args.Comparer, ref accessor);

            if (index >= 0)
            {
                args.SetRemovedValue(RemoveAtInternal(index, ref accessor).Value); // remove item

                if (!GetIsHalfFull(ref accessor)) // borrow or merge
                {
                    if (CanBorrowFrom(GetPrevious(ref accessor), ref accessor)) // left sibling
                    {
                        var last = GetPrevious(ref accessor).PopLastInternal(ref accessor);
                        PushFirst(last, ref accessor);

                        var p = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref p, last.Key);
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, p, ref accessor);

                        // Left's HighKey must match the new separator (borrow moved the boundary).
                        GetPrevious(ref accessor).SetHighKey(last.Key, ref accessor);

                        Validate();
                        Validate();
                    }
                    else if (CanBorrowFrom(GetNext(ref accessor), ref accessor)) // right sibling
                    {
                        var first = GetNext(ref accessor).PopFirstInternal(ref accessor);
                        PushLast(first, ref accessor);

                        var newSeparator = GetNext(ref accessor).GetFirst(ref accessor).Key;
                        var p = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref p, newSeparator);
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, p, ref accessor);

                        // Current's HighKey must match the new separator (borrow moved the boundary).
                        SetHighKey(newSeparator, ref accessor);

                        Validate();
                        Validate();
                    }
                    else // merge with either sibling.
                    {
                        if (relatives.HasTrueLeftSibling) // current node will be removed from parent.
                        {
                            merge = true;
                            GetPrevious(ref accessor).MergeLeft(this, ref accessor); // merge from left to keep items in order.
                            var p = GetPrevious(ref accessor);
                            p.SetNext(GetNext(ref accessor), ref accessor); // fix linked list
                            if (GetNext(ref accessor).IsValid)
                            {
                                var n = GetNext(ref accessor);
                                n.SetPrevious(GetPrevious(ref accessor), ref accessor);
                            }

                            Validate();
                            Validate();
                        }
                        else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                        {
                            merge = true;
                            MergeLeft(GetNext(ref accessor), ref accessor); // merge from right to keep items in order.
                            SetNext(GetNext(ref accessor).GetNext(ref accessor), ref accessor); // fix linked list
                            if (GetNext(ref accessor).IsValid)
                            {
                                var n = GetNext(ref accessor);
                                n.SetPrevious(this, ref accessor);
                            }

                            Validate();
                            Validate();
                        }
                        // else: root leaf — no siblings to merge with.
                        // The root is allowed to be below half-full per B-tree invariants.
                    }
                }

                bool CanBorrowFrom(NodeWrapper leaf, ref ChunkAccessor ca)
                {
                    if (!leaf.IsValid)
                    {
                        return false;
                    }

                    return leaf.GetCount(ref ca) > (leaf.GetCapacity() / 2);
                }
            }

            return merge; // true if merge happened.
        }
        
        /// <summary>
        /// Handles a child merge during remove. Removes the separator key from this internal node, then borrows from a sibling or merges with a sibling if
        /// below half-full. Called iteratively during upward propagation from <see cref="BTree{TKey}.RemoveIterative"/>.
        /// </summary>
        internal bool HandleChildMerge(int childIndex, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            bool merge = false;

            RemoveAtInternal(Math.Max(0, childIndex), ref accessor); // removes right sibling of child if left most child is merged, otherwise merged child is removed.

            if (!GetIsHalfFull(ref accessor)) // borrow or merge
            {
                if (CanBorrowFrom(relatives.GetLeftSibling(ref accessor), ref accessor, out NodeWrapper leftSibling))
                {
                    var last = leftSibling.PopLastInternal(ref accessor);

                    // swap left and right pointers.
                    var temp = GetLeft(ref accessor).ChunkId;
                    SetLeft(new NodeWrapper(_storage, last.Value), ref accessor);
                    last.Value = temp;

                    var pr = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                    KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                    relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pr, ref accessor);

                    PushFirst(last, ref accessor);

                    Validate();
                    Validate();
                }
                else if (CanBorrowFrom(relatives.GetRightSibling(ref accessor), ref accessor, out NodeWrapper rightSibling))
                {
                    var first = rightSibling.PopFirstInternal(ref accessor);

                    // swap left and right pointers.
                    var temp = rightSibling.GetLeft(ref accessor).ChunkId;
                    rightSibling.SetLeft(new NodeWrapper(_storage, first.Value), ref accessor);
                    first.Value = temp;

                    var pl = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                    KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                    relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pl, ref accessor);

                    PushLast(first, ref accessor);

                    Validate();
                    Validate();
                }
                else // merge
                {
                    merge = true;
                    if (relatives.HasTrueLeftSibling) // current node will be removed from parent
                    {
                        var pkey = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor).Key; // demote key
                        var mid = new KeyValueItem(pkey, GetLeft(ref accessor).ChunkId);
                        leftSibling.PushLast(mid, ref accessor);
                        leftSibling.MergeLeft(this, ref accessor); // merge from left to keep items in order.

                        Validate();
                    }
                    else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                    {
                        var pkey = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor).Key; // demote key
                        var mid = new KeyValueItem(pkey, rightSibling.GetLeft(ref accessor).ChunkId);
                        PushLast(mid, ref accessor);
                        MergeLeft(rightSibling, ref accessor); // merge from right to keep items in order.

                        Validate();
                    }
                }
            }

            return merge; // true if merge happened.

            bool CanBorrowFrom(NodeWrapper node, ref ChunkAccessor ca, out NodeWrapper iNode)
            {
                if (!node.IsValid || node.GetIsLeaf(ref ca))
                {
                    iNode = default;
                    return false;
                }

                iNode = node;
                return iNode.GetCount(ref ca) > iNode.GetCapacity() / 2;
            }
        }

        internal KeyValueItem RemoveAtInternal(int index, ref ChunkAccessor accessor) => _storage.RemoveAt(this, index, ref accessor);

        private NodeWrapper SplitRight(NodeStates states, ref ChunkAccessor accessor)
        {
            Activity activity = null;
            if (TelemetryConfig.BTreeActive)
            {
                activity = TyphonActivitySource.StartActivity("BTree.NodeSplit");
                activity?.SetTag(TyphonSpanAttributes.IndexNodeSplit, true);
            }

            try
            {
                var result = _storage.SplitRight(this, states, ref accessor);
                Interlocked.Increment(ref _storage.Owner._splitCount);
                return result;
            }
            finally
            {
                activity?.Dispose();
            }
        }

        /// <summary>
        /// Inline sanity check during mutations. Intentionally a no-op — assertions here fire while holding OLC write locks; an exception would leak the lock
        /// and permanently deadlock the tree.
        /// Use <see cref="CheckConsistency"/> post-mutation for structural validation.
        /// </summary>
        [Conditional("DEBUG")]
        [ExcludeFromCodeCoverage]
        private static void Validate()
        {
        }

        public int Find(TKey key, IComparer<TKey> comparer, ref ChunkAccessor accessor) => BinarySearch(key, comparer, ref accessor);

        private int BinarySearch(TKey key, IComparer<TKey> comparer, ref ChunkAccessor accessor) => _storage.BinarySearch(this, key, comparer, ref accessor);

        private KeyValueItem InsertPopFirst(int index, KeyValueItem item, ref ChunkAccessor accessor)
        {
            if (index == 0)
            {
                return item;
            }

            var value = PopFirstInternal(ref accessor);
            Insert(index - 1, item, ref accessor);

            return value;
        }

        private KeyValueItem InsertPopLast(int index, KeyValueItem item, ref ChunkAccessor accessor)
        {
            if (index == GetCount(ref accessor))
            {
                return item;
            }

            var value = PopLastInternal(ref accessor);
            Insert(index, item, ref accessor);

            return value;
        }

        public KeyValueItem PopFirstInternal(ref ChunkAccessor accessor)
        {
            if (GetCount(ref accessor) <= 0)
            {
                throw new InvalidOperationException("no items to remove.");
            }

            var temp = _storage.GetItem(this, GetStart(ref accessor), false, ref accessor);
            _storage.SetItem(this, GetStart(ref accessor), default, false, ref accessor);
            _storage.IncrementStart(this, ref accessor);
            SetCount(GetCount(ref accessor) - 1, ref accessor);
            return temp;
        }

        public KeyValueItem PopLastInternal(ref ChunkAccessor accessor)
        {
            if (GetCount(ref accessor) <= 0)
            {
                throw new InvalidOperationException("no items to remove.");
            }

            SetCount(GetCount(ref accessor) - 1, ref accessor);
            var end = GetEnd(ref accessor);
            var temp = _storage.GetItem(this, end, false, ref accessor);
            _storage.SetItem(this, end, default, false, ref accessor);
            return temp;
        }

        public void Insert(int index, KeyValueItem item, ref ChunkAccessor accessor) => _storage.Insert(this, index, item, ref accessor);

        public int Adjust(int index) => (index < 0 || index >= GetCapacity()) ? (index + GetCapacity() * (-index).Sign()) : index;

        #endregion

        #region Equatable

        public bool Equals(NodeWrapper other) => ChunkId == other.ChunkId;

        public override bool Equals(object obj) => obj is NodeWrapper other && Equals(other);

        public override int GetHashCode() => ChunkId;

        public static bool operator ==(NodeWrapper left, NodeWrapper right) => left.Equals(right);

        public static bool operator !=(NodeWrapper left, NodeWrapper right) => !left.Equals(right);

        public NodeWrapper GetNearestChild(TKey key, IComparer<TKey> comparer, ref ChunkAccessor accessor)
        {
            if (GetIsLeaf(ref accessor))
            {
                return default;
            }

            var index = Find(key, comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1; // get next nearest item.
            }

            return GetChild(index, ref accessor);
        }

        #endregion

        #region Debug / Check

        [ExcludeFromCodeCoverage]
        private sealed class DebugView
        {
            private readonly NodeWrapper _node;

            private DebugView(NodeWrapper node)
            {
                _node = node;
            }

            public NodeWrapper Previous
            {
                get
                {
                    var accessor = _node._storage.Segment.CreateChunkAccessor();
                    try
                    {
                        return _node.GetPrevious(ref accessor);
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            }

            public NodeWrapper Next
            {
                get
                {
                    var accessor = _node._storage.Segment.CreateChunkAccessor();
                    try
                    {
                        return _node.GetNext(ref accessor);
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            }

            public NodeWrapper Left
            {
                get
                {
                    var accessor = _node._storage.Segment.CreateChunkAccessor();
                    try
                    {
                        return _node.GetLeft(ref accessor);
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            }

            public KeyValueItem[] Items
            {
                get
                {
                    var accessor = _node._storage.Segment.CreateChunkAccessor();
                    try
                    {
                        var count = _node.GetCount(ref accessor);
                        var res = new KeyValueItem[count];
                        for (int i = 0; i < count; i++)
                        {
                            res[i] = _node.GetItem(i, ref accessor);
                        }

                        return res;
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            }

        }

        internal enum CheckConsistencyParent
        {
            Root,
            Left,
            Right
        }

        [ExcludeFromCodeCoverage]
        internal void CheckConsistency(TKey key, CheckConsistencyParent parent, IComparer<TKey> comparer, int height, ref ChunkAccessor accessor)
        {
            ConsistencyAssert(IsValid, "Root node should always be valid");

            ConsistencyAssert(((height == 1) && GetIsLeaf(ref accessor)) || ((height > 1) && !GetIsLeaf(ref accessor)), $"Mismatch node's Height {height} with {GetIsLeaf(ref accessor)}");

            var firstKey = GetFirst(ref accessor).Key;
            ConsistencyAssert(comparer.Compare(firstKey, GetLast(ref accessor).Key) <= 0, $"First Key '{firstKey}' should be less than Last's one '{GetLast(ref accessor).Key}'.");
            ConsistencyAssert(comparer.Compare(firstKey, GetItem(0, ref accessor).Key) == 0, $"First.Key '{firstKey}' should be equal to first item's key '{GetItem(0, ref accessor).Key}'.");
            var lastKey = GetItem(GetCount(ref accessor) - 1, ref accessor).Key;
            ConsistencyAssert(comparer.Compare(lastKey, GetLast(ref accessor).Key) == 0, $"Last.Key '{GetLast(ref accessor).Key}' should be equal to last item's key '{lastKey}'.");

            var count = GetCount(ref accessor);
            var left = GetLeft(ref accessor);
            ConsistencyAssert((count == 0 || GetIsLeaf(ref accessor)) || (left.IsValid && left == GetFirstChild(ref accessor)), "Invalid Left Node, should be the first child");

            for (int i = 0; i < count; i++)
            {
                var childItem = GetIsLeaf(ref accessor) ? default : GetChild(i, ref accessor);
                var item = GetItem(i, ref accessor);
                if (!GetIsLeaf(ref accessor))
                {
                    ConsistencyAssert(childItem.IsValid, "A Child Node should always be valid");
                    ConsistencyAssert(childItem.ChunkId == item.Value, "Node's Id doesn't match with item's Key");
                }

                if (parent == CheckConsistencyParent.Left)
                {
                    ConsistencyAssert(comparer.Compare(key, item.Key) > 0, $"{i} {height} Left Node's key '{item.Key}' should be less than parent's key '{key}'.");
                } else if (parent == CheckConsistencyParent.Right)
                {
                    ConsistencyAssert(comparer.Compare(key, item.Key) <= 0, $"Right Node's key '{item.Key}' should be greater than parent's key '{key}'.");
                }

                if (!GetIsLeaf(ref accessor))
                {
                    left.CheckConsistency(item.Key, CheckConsistencyParent.Left, comparer, height - 1, ref accessor);
                    childItem.CheckConsistency(item.Key, CheckConsistencyParent.Right, comparer, height - 1, ref accessor);
                }

                left = childItem;
            }
        }

        #endregion
    }
}