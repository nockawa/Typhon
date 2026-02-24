// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine.BPTree;

public abstract partial class BTree<TKey>
{
    [DebuggerDisplay("ChunkId: {ChunkId}, IsValid: {IsValid}")]
    [DebuggerTypeProxy(typeof(BTree<>.NodeWrapper.DebugView))]
    public readonly struct NodeWrapper : IEquatable<NodeWrapper>
    {
        private readonly BaseNodeStorage _storage;
        public readonly int ChunkId;

        public NodeWrapper(BaseNodeStorage storage, int chunkId)
        {
            _storage = storage;
            ChunkId = chunkId;
        }

        #region Node Properties

        public bool IsValid => _storage != null && ChunkId != 0;
        public bool GetIsLeaf(ref ChunkAccessor accessor) => (_storage.GetNodeStates(this, ref accessor) & NodeStates.IsLeaf) != 0;
        public int GetCapacity() => _storage.GetNodeCapacity();
        public bool GetIsFull(ref ChunkAccessor accessor) => GetCount(ref accessor) == GetCapacity();
        public bool GetIsHalfFull(ref ChunkAccessor accessor) => GetCount(ref accessor) >= (GetCapacity() / 2);
        public int GetLength(ref ChunkAccessor accessor) => GetCount(ref accessor);

        public int GetCount(ref ChunkAccessor accessor) => _storage.GetCount(this, ref accessor);

        internal void SetCount(int value, ref ChunkAccessor accessor) => _storage.SetCount(this, value, ref accessor);

        public int GetStart(ref ChunkAccessor accessor) => _storage.GetStart(this, ref accessor);

        private void SetStart(int value, ref ChunkAccessor accessor) => _storage.SetStart(this, value, ref accessor);

        public int GetEnd(ref ChunkAccessor accessor) => _storage.GetEnd(this, ref accessor);

        public KeyValueItem GetFirst(ref ChunkAccessor accessor) => _storage.GetItem(this, 0, true, ref accessor);

        public void SetFirst(KeyValueItem value, ref ChunkAccessor accessor) => _storage.SetItem(this, 0, value, true, ref accessor);

        public KeyValueItem GetLast(ref ChunkAccessor accessor) => _storage.GetItem(this, _storage.GetCount(this, ref accessor) - 1, true, ref accessor);

        public void SetLast(KeyValueItem value, ref ChunkAccessor accessor) 
            => _storage.SetItem(this, _storage.GetCount(this, ref accessor) - 1, value, true, ref accessor);

        public NodeWrapper GetPrevious(ref ChunkAccessor accessor) => _storage.GetPreviousNode(this, ref accessor);

        public void SetPrevious(NodeWrapper value, ref ChunkAccessor accessor) => _storage.SetPreviousNode(this, value.ChunkId, ref accessor);

        public NodeWrapper GetNext(ref ChunkAccessor accessor) => _storage.GetNextNode(this, ref accessor);

        public void SetNext(NodeWrapper value, ref ChunkAccessor accessor) => _storage.SetNextNode(this, value.ChunkId, ref accessor);

        public NodeWrapper GetLeft(ref ChunkAccessor accessor) => _storage.GetLeftNode(this, ref accessor);

        public void SetLeft(NodeWrapper value, ref ChunkAccessor accessor) => _storage.SetLeftNode(this, value.ChunkId, ref accessor);

        public KeyValueItem GetItem(int index, ref ChunkAccessor accessor) => _storage.GetItem(this, index, true, ref accessor);
        public void SetItem(int index, KeyValueItem value, ref ChunkAccessor accessor) => _storage.SetItem(this, index, value, true, ref accessor);

        #endregion

        #region Node Operations

        public void PushFirst(KeyValueItem item, ref ChunkAccessor accessor) => _storage.PushFirst(this, item, ref accessor);
        public void PushLast(KeyValueItem item, ref ChunkAccessor accessor) => _storage.PushLast(this, item, ref accessor);
        public void MergeLeft(NodeWrapper right, ref ChunkAccessor accessor)
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

        public KeyValueItem? Insert(ref InsertArguments args, ref NodeRelatives relatives, NodeWrapper parent, ref ChunkAccessor accessor)
        {
            if (GetIsLeaf(ref accessor))
            {
                return InsertLeaf(ref args, ref relatives, ref accessor);
            }
            else
            {
                return InsertInternal(ref args, ref relatives, ref accessor);
            }
        }

        public bool Remove(ref RemoveArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            if (GetIsLeaf(ref accessor))
            {
                return RemoveLeaf(ref args, ref relatives, ref accessor);
            }
            else
            {
                return RemoveInternal(ref args, ref relatives, ref accessor);
            }
        }

        private KeyValueItem? InsertLeaf(ref InsertArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            KeyValueItem? rightLeaf = null;

            var index = Find(args.Key, args.KeyComparer, ref accessor);

            if (index < 0)
            {
                index = ~index;

                Debug.Assert(index >= 0 && index <= GetCount(ref accessor));

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
                    if (CanSpillTo(GetPrevious(ref accessor), ref accessor))
                    {
                        var first = InsertPopFirst(index, item, ref accessor);
                        GetPrevious(ref accessor).PushLast(first, ref accessor); // move the smallest item to left sibling.

                        // update ancestors key.
                        var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref pl, GetFirst(ref accessor).Key);
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl, ref accessor);

                        Validate(this, ref accessor);
                        Validate(GetPrevious(ref accessor), ref accessor);
                    }
                    else if (CanSpillTo(GetNext(ref accessor), ref accessor))
                    {
                        var last = InsertPopLast(index, item, ref accessor);
                        GetNext(ref accessor).PushFirst(last, ref accessor);

                        // update ancestors key.
                        var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref pr, last.Key);
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr, ref accessor);

                        Validate(this, ref accessor);
                        Validate(GetNext(ref accessor), ref accessor);
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

                        Validate(this, ref accessor);
                        Validate(rightNode, ref accessor);
                    }
                }

                // splits right side to new node and keeps left side for current node.
                NodeWrapper SplitNodeRight(NodeWrapper left, ref ChunkAccessor ca)
                {
                    var right = left.SplitRight(NodeStates.IsLeaf, ref ca);
                    var next = left.GetNext(ref ca);
                    if (next.IsValid)
                    {
                        next.SetPrevious(right, ref ca);
                        right.SetNext(left.GetNext(ref ca), ref ca); // to make linked list.
                    }
                    right.SetPrevious(left, ref ca);
                    left.SetNext(right, ref ca);
                    return right;
                }

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

        private KeyValueItem? InsertInternal(ref InsertArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            var index = Find(args.Key, args.KeyComparer, ref accessor);

            // -1 because items smaller than key have to go inside left child. 
            // since items at each index point to right child, index is decremented to get left child.
            if (index < 0)
            {
                index = ~index - 1;
            }

            Debug.Assert(index >= -1 && index < GetCount(ref accessor));

            // get child to traverse through.
            var child = GetChild(index, ref accessor);
            NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives, ref accessor);

            var rightChild = child.Insert(ref args, ref childRelatives, this, ref accessor);

            if (rightChild is KeyValueItem middle) // if split, add middle key to this node.
            {
                // +1 because middle is always right side which is fresh node. 
                // items at index already point to left node after split. so middle must go after index.
                index++;

                rightChild = null;
                if (!GetIsFull(ref accessor))
                {
                    Insert(index, middle, ref accessor);
                }
                else
                {
                    // if left sibling has space, spill left child of this item to left sibling.
                    if (CanSpillTo(relatives.LeftSibling, ref accessor, out var leftSibling))
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

                        //KeyValueItem.SwapRightWith(ref first, ref Left);
                        // swap left and right nodes
                        var temp = GetLeft(ref accessor).ChunkId;
                        SetLeft(new NodeWrapper(_storage, first.Value), ref accessor);
                        first = new KeyValueItem(first.Key, temp);

                        var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                        KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl, ref accessor);

                        leftSibling.PushLast(first, ref accessor);

                        Validate(this, ref accessor);
                        Validate(leftSibling, ref accessor);
                    }
                    else if (CanSpillTo(relatives.RightSibling, ref accessor, out var rightSibling)) // if right sibling has space
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

                        //KeyValueItem.SwapRightWith(ref last, ref rightSibling.Left);
                        // swap left and right node
                        var temp = rightSibling.GetLeft(ref accessor).ChunkId;
                        rightSibling.SetLeft(new NodeWrapper(_storage, last.Value), ref accessor);
                        last = new KeyValueItem(last.Key, temp);

                        var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                        KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr, ref accessor);

                        rightSibling.PushFirst(last, ref accessor);

                        Validate(this, ref accessor);
                        Validate(rightSibling, ref accessor);
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

                        Validate(this, ref accessor);
                        Validate(rightNode, ref accessor);
                    }
                }

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

            return rightChild;
        }

        public bool RemoveLeaf(ref RemoveArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            var merge = false;
            var index = Find(args.Key, args.Comparer, ref accessor);

            if (index >= 0)
            {
                Debug.Assert(index >= 0 && index <= GetCount(ref accessor));

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

                        Validate(this, ref accessor);
                        Validate(GetPrevious(ref accessor), ref accessor);
                    }
                    else if (CanBorrowFrom(GetNext(ref accessor), ref accessor)) // right sibling
                    {
                        var first = GetNext(ref accessor).PopFirstInternal(ref accessor);
                        PushLast(first, ref accessor);

                        var p = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                        KeyValueItem.ChangeKey(ref p, GetNext(ref accessor).GetFirst(ref accessor).Key);
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, p, ref accessor);

                        Validate(this, ref accessor);
                        Validate(GetNext(ref accessor), ref accessor);
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

                            Validate(GetPrevious(ref accessor), ref accessor);
                            Validate(GetNext(ref accessor), ref accessor);
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

                            Validate(this, ref accessor);
                            Validate(GetNext(ref accessor), ref accessor);
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
        
        public bool RemoveInternal(ref RemoveArguments args, ref NodeRelatives relatives, ref ChunkAccessor accessor)
        {
            var merge = false;
            var index = Find(args.Key, args.Comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            Debug.Assert(index >= -1 && index < GetCount(ref accessor));

            var child = GetChild(index, ref accessor);
            NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives, ref accessor);
            var childMerged = child.Remove(ref args, ref childRelatives, ref accessor);

            if (childMerged)
            {
                RemoveAtInternal(Math.Max(0, index), ref accessor); // removes right sibling of child if left most child is merged, otherwise merged child is removed.

                if (!GetIsHalfFull(ref accessor)) // borrow or merge
                {
                    if (CanBorrowFrom(relatives.LeftSibling, ref accessor, out NodeWrapper leftSibling))
                    {
                        var last = leftSibling.PopLastInternal(ref accessor);

                        //KeyValueItem.SwapRightWith(ref last, ref Left); // swap left and right pointers.
                        var temp = GetLeft(ref accessor).ChunkId;
                        SetLeft(new NodeWrapper(_storage, last.Value), ref accessor);
                        last.Value = temp;

                        var pr = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, ref accessor);
                        KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pr, ref accessor);

                        PushFirst(last, ref accessor);

                        Validate(this, ref accessor);
                        Validate(leftSibling, ref accessor);
                    }
                    else if (CanBorrowFrom(relatives.RightSibling, ref accessor, out NodeWrapper rightSibling))
                    {
                        var first = rightSibling.PopFirstInternal(ref accessor);

                        //KeyValueItem.SwapRightWith(ref first, ref rightSibling.Left); // swap left and right pointers.
                        var temp = rightSibling.GetLeft(ref accessor).ChunkId;
                        rightSibling.SetLeft(new NodeWrapper(_storage, first.Value), ref accessor);
                        first.Value = temp;

                        var pl = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor);
                        KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pl, ref accessor);

                        PushLast(first, ref accessor);

                        Validate(this, ref accessor);
                        Validate(rightSibling, ref accessor);
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

                            Validate(leftSibling, ref accessor);
                        }
                        else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                        {
                            var pkey = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, ref accessor).Key; // demote key
                            var mid = new KeyValueItem(pkey, rightSibling.GetLeft(ref accessor).ChunkId);
                            PushLast(mid, ref accessor);
                            MergeLeft(rightSibling, ref accessor); // merge from right to keep items in order.

                            Validate(this, ref accessor);
                        }
                    }
                }

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

            return merge; // true if merge happened.
        }

        private KeyValueItem RemoveAtInternal(int index, ref ChunkAccessor accessor) => _storage.RemoveAt(this, index, ref accessor);

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
                return _storage.SplitRight(this, states, ref accessor);
            }
            finally
            {
                activity?.Dispose();
            }
        }

        [Conditional("DEBUG")]
        private static void Validate(NodeWrapper node, ref ChunkAccessor accessor)
        {
            if (!node.IsValid)
            {
                return;
            }

            Debug.Assert(node.GetIsHalfFull(ref accessor));
            Debug.Assert(!node.GetPrevious(ref accessor).IsValid || node.GetPrevious(ref accessor).GetNext(ref accessor) == node);
            Debug.Assert(!node.GetNext(ref accessor).IsValid || node.GetNext(ref accessor).GetPrevious(ref accessor) == node);
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

        public KeyValueItem InsertPopLast(int index, KeyValueItem item, ref ChunkAccessor accessor)
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
            Trace.Assert(IsValid, "Root node should always be valid");

            Trace.Assert(((height == 1) && GetIsLeaf(ref accessor)) || ((height > 1) && !GetIsLeaf(ref accessor)), $"Mismatch node's Height {height} with {GetIsLeaf(ref accessor)}");

            var firstKey = GetFirst(ref accessor).Key;
            Trace.Assert(comparer.Compare(firstKey, GetLast(ref accessor).Key) <= 0, $"First Key '{firstKey}' should be less than Last's one '{GetLast(ref accessor).Key}'.");
            Trace.Assert(comparer.Compare(firstKey, GetItem(0, ref accessor).Key) == 0, $"First.Key '{firstKey}' should be equal to first item's key '{GetItem(0, ref accessor).Key}'.");
            var lastKey = GetItem(GetCount(ref accessor) - 1, ref accessor).Key;
            Trace.Assert(comparer.Compare(lastKey, GetLast(ref accessor).Key) == 0, $"Last.Key '{GetLast(ref accessor).Key}' should be equal to last item's key '{lastKey}'.");

            var count = GetCount(ref accessor);
            var left = GetLeft(ref accessor);
            Trace.Assert((count == 0 || GetIsLeaf(ref accessor)) || (left.IsValid && left == GetFirstChild(ref accessor)), "Invalid Left Node, should be the first child");

            for (int i = 0; i < count; i++)
            {
                var childItem = GetIsLeaf(ref accessor) ? default : GetChild(i, ref accessor);
                var item = GetItem(i, ref accessor);
                if (!GetIsLeaf(ref accessor))
                {
                    Trace.Assert(childItem.IsValid, "A Child Node should always be valid");
                    Trace.Assert(childItem.ChunkId == item.Value, "Node's Id doesn't match with item's Key");
                }

                if (parent == CheckConsistencyParent.Left)
                {
                    Trace.Assert(comparer.Compare(key, item.Key) > 0, $"{i} {height} Left Node's key '{item.Key}' should be less than parent's key '{key}'.");
                } else if (parent == CheckConsistencyParent.Right)
                {
                    Trace.Assert(comparer.Compare(key, item.Key) <= 0, $"Right Node's key '{item.Key}' should be greater than parent's key '{key}'.");
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