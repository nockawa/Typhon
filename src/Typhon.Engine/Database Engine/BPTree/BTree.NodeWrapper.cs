// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine.BPTree;

public abstract partial class BTree<TKey>
{
    [DebuggerDisplay("ChunkId: {ChunkId}, IsValid: {IsValid}, IsLeaf: {IsLeaf}, Count: {Count}")]
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
        public bool GetIsLeaf(ChunkRandomAccessor accessor) => (_storage.GetNodeStates(this, accessor) & NodeStates.IsLeaf) != 0;
        public int GetCapacity() => _storage.GetNodeCapacity();
        public bool GetIsFull(ChunkRandomAccessor accessor) => GetCount(accessor) == GetCapacity();
        public bool GetIsHalfFull(ChunkRandomAccessor accessor) => GetCount(accessor) >= (GetCapacity() / 2);
        public int GetLength(ChunkRandomAccessor accessor) => GetCount(accessor);

        public int GetCount(ChunkRandomAccessor accessor) => _storage.GetCount(this, accessor);

        internal void SetCount(int value, ChunkRandomAccessor accessor) => _storage.SetCount(this, value, accessor);

        public int GetStart(ChunkRandomAccessor accessor) => _storage.GetStart(this, accessor);

        private void SetStart(int value, ChunkRandomAccessor accessor) => _storage.SetStart(this, value, accessor);

        public int GetEnd(ChunkRandomAccessor accessor) => _storage.GetEnd(this, accessor);

        public KeyValueItem GetFirst(ChunkRandomAccessor accessor) => _storage.GetItem(this, 0, true, accessor);

        public void SetFirst(KeyValueItem value, ChunkRandomAccessor accessor) => _storage.SetItem(this, 0, value, true, accessor);

        public KeyValueItem GetLast(ChunkRandomAccessor accessor) => _storage.GetItem(this, _storage.GetCount(this, accessor) - 1, true, accessor);

        public void SetLast(KeyValueItem value, ChunkRandomAccessor accessor) 
            => _storage.SetItem(this, _storage.GetCount(this, accessor) - 1, value, true, accessor);

        public NodeWrapper GetPrevious(ChunkRandomAccessor accessor) => _storage.GetPreviousNode(this, accessor);

        public void SetPrevious(NodeWrapper value, ChunkRandomAccessor accessor) => _storage.SetPreviousNode(this, value.ChunkId, accessor);

        public NodeWrapper GetNext(ChunkRandomAccessor accessor) => _storage.GetNextNode(this, accessor);

        public void SetNext(NodeWrapper value, ChunkRandomAccessor accessor) => _storage.SetNextNode(this, value.ChunkId, accessor);

        public NodeWrapper GetLeft(ChunkRandomAccessor accessor) => _storage.GetLeftNode(this, accessor);

        public void SetLeft(NodeWrapper value, ChunkRandomAccessor accessor) => _storage.SetLeftNode(this, value.ChunkId, accessor);

        public KeyValueItem GetItem(int index, ChunkRandomAccessor accessor) => _storage.GetItem(this, index, true, accessor);
        public void SetItem(int index, KeyValueItem value, ChunkRandomAccessor accessor) => _storage.SetItem(this, index, value, true, accessor);

        #endregion

        #region Node Operations

        public void PushFirst(KeyValueItem item, ChunkRandomAccessor accessor) => _storage.PushFirst(this, item, accessor);
        public void PushLast(KeyValueItem item, ChunkRandomAccessor accessor) => _storage.PushLast(this, item, accessor);
        public void MergeLeft(NodeWrapper right, ChunkRandomAccessor accessor) => _storage.MergeLeft(this, right, accessor);

        public NodeWrapper GetChild(int index, ChunkRandomAccessor accessor) => _storage.GetChild(this, index, accessor);

        public NodeWrapper GetLastChild(ChunkRandomAccessor accessor) => _storage.GetLastChild(this, accessor);

        public NodeWrapper GetFirstChild(ChunkRandomAccessor accessor) => _storage.GetFirstChild(this, accessor);

        public KeyValueItem? Insert(ref InsertArguments args, ref NodeRelatives relatives, NodeWrapper parent, ChunkRandomAccessor accessor)
        {
            if (GetIsLeaf(accessor))
            {
                return InsertLeaf(ref args, ref relatives, accessor);
            }
            else
            {
                return InsertInternal(ref args, ref relatives, accessor);
            }
        }

        public bool Remove(ref RemoveArguments args, ref NodeRelatives relatives, ChunkRandomAccessor accessor)
        {
            if (GetIsLeaf(accessor))
            {
                return RemoveLeaf(ref args, ref relatives, accessor);
            }
            else
            {
                return RemoveInternal(ref args, ref relatives, accessor);
            }
        }

        private KeyValueItem? InsertLeaf(ref InsertArguments args, ref NodeRelatives relatives, ChunkRandomAccessor accessor)
        {
            KeyValueItem? rightLeaf = null;

            var index = Find(args.Key, args.KeyComparer, accessor);

            if (index < 0)
            {
                index = ~index;

                Debug.Assert(index >= 0 && index <= GetCount(accessor));

                int value = args.GetValue();
                if (_storage.Owner.AllowMultiple)
                {
                    var bufferId = _storage.CreateBuffer(accessor);
                    args.ElementId = _storage.Append(bufferId, value, accessor);
                    value = bufferId;
                }
                var item = new KeyValueItem(args.Key, value); // item to add

                if (!GetIsFull(accessor)) // if there is space, add and return.
                {
                    Insert(index, item, accessor); // insert value and return.
                }
                else // cant add, spill or split
                {
                    if (CanSpillTo(GetPrevious(accessor)))
                    {
                        var first = InsertPopFirst(index, item, accessor);
                        GetPrevious(accessor).PushLast(first, accessor); // move the smallest item to left sibling.

                        // update ancestors key.
                        var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, accessor);
                        KeyValueItem.ChangeKey(ref pl, GetFirst(accessor).Key);
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl, accessor);

                        Validate(this, accessor);
                        Validate(GetPrevious(accessor), accessor);
                    }
                    else if (CanSpillTo(GetNext(accessor)))
                    {
                        var last = InsertPopLast(index, item, accessor);
                        GetNext(accessor).PushFirst(last, accessor);

                        // update ancestors key.
                        var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, accessor);
                        KeyValueItem.ChangeKey(ref pr, last.Key);
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr, accessor);

                        Validate(this, accessor);
                        Validate(GetNext(accessor), accessor);
                    }
                    else // split, then promote middle item
                    {
                        var rightNode = SplitNodeRight(this);

                        // insert item and find middle value to promote
                        if (index <= GetCount(accessor))
                        {
                            // when adding item to this node, pop last item and give it to right node.
                            // this way, this and right split always have equal length or maximum 1 difference. (also avoids overflow when capacity = 1)
                            rightNode.PushFirst(InsertPopLast(index, item, accessor), accessor);
                        }
                        else if (index > GetCount(accessor))
                        {
                            rightNode.Insert(index - GetCount(accessor), item, accessor);
                        }

                        rightLeaf = new KeyValueItem(rightNode.GetFirst(accessor).Key, rightNode.ChunkId);

                        Validate(this, accessor);
                        Validate(rightNode, accessor);
                    }
                }

                // splits right side to new node and keeps left side for current node.
                NodeWrapper SplitNodeRight(NodeWrapper left)
                {
                    var right = left.SplitRight(NodeStates.IsLeaf, accessor);
                    var next = left.GetNext(accessor);
                    if (next.IsValid)
                    {
                        next.SetPrevious(right, accessor);
                        right.SetNext(left.GetNext(accessor), accessor); // to make linked list.
                    }
                    right.SetPrevious(left, accessor);
                    left.SetNext(right, accessor);
                    return right;
                }

                bool CanSpillTo(NodeWrapper leaf)
                {
                    return leaf.IsValid && leaf.GetIsFull(accessor) == false;
                }
            }
            else
            {
                var curItem = GetItem(index, accessor);
                args.ElementId = _storage.Append(curItem.Value, args.GetValue(), accessor);
                //KeyValueItem.ChangeValue(ref item, args.GetUpdateValue(item.Value)); // update item value
                //Items[index] = item; // set new item
            }

            return rightLeaf;
        }

        private KeyValueItem? InsertInternal(ref InsertArguments args, ref NodeRelatives relatives, ChunkRandomAccessor accessor)
        {
            var index = Find(args.Key, args.KeyComparer, accessor);

            // -1 because items smaller than key have to go inside left child. 
            // since items at each index point to right child, index is decremented to get left child.
            if (index < 0) index = ~index - 1;

            Debug.Assert(index >= -1 && index < GetCount(accessor));

            // get child to traverse through.
            var child = GetChild(index, accessor);
            NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives, accessor);

            var rightChild = child.Insert(ref args, ref childRelatives, this, accessor);

            if (rightChild is KeyValueItem middle) // if split, add middle key to this node.
            {
                // +1 because middle is always right side which is fresh node. 
                // items at index already point to left node after split. so middle must go after index.
                index++;

                rightChild = null;
                if (!GetIsFull(accessor))
                {
                    Insert(index, middle, accessor);
                }
                else
                {
                    // if left sibling has space, spill left child of this item to left sibling.
                    if (CanSpillTo(relatives.LeftSibling, out var leftSibling))
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

                        var first = InsertPopFirst(index, middle, accessor);

                        //KeyValueItem.SwapRightWith(ref first, ref Left);
                        // swap left and right nodes
                        var temp = GetLeft(accessor).ChunkId;
                        SetLeft(new NodeWrapper(_storage, first.Value), accessor);
                        first = new KeyValueItem(first.Key, temp);

                        var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, accessor);
                        KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl, accessor);

                        leftSibling.PushLast(first, accessor);

                        Validate(this, accessor);
                        Validate(leftSibling, accessor);
                    }
                    else if (CanSpillTo(relatives.RightSibling, out var rightSibling)) // if right sibling has space
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

                        var last = InsertPopLast(index, middle, accessor);

                        //KeyValueItem.SwapRightWith(ref last, ref rightSibling.Left);
                        // swap left and right node
                        var temp = rightSibling.GetLeft(accessor).ChunkId;
                        rightSibling.SetLeft(new NodeWrapper(_storage, last.Value), accessor);
                        last = new KeyValueItem(last.Key, temp);

                        var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, accessor);
                        KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr, accessor);

                        rightSibling.PushFirst(last, accessor);

                        Validate(this, accessor);
                        Validate(rightSibling, accessor);
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

                        var rightNode = SplitRight(NodeStates.None, accessor);

                        // find middle key to promote
                        if (index < GetCount(accessor))
                        {
                            middle = InsertPopLast(index, middle, accessor);
                        }
                        else if (index > GetCount(accessor))
                        {
                            middle = rightNode.InsertPopFirst(index - GetCount(accessor), middle, accessor);
                        }

                        rightNode.SetLeft(new NodeWrapper(_storage, middle.Value), accessor);
                        middle = new KeyValueItem(middle.Key, rightNode.ChunkId);
                        rightChild = middle;

                        Validate(this, accessor);
                        Validate(rightNode, accessor);
                    }
                }

                bool CanSpillTo(NodeWrapper node, out NodeWrapper iNode)
                {
                    if (node.IsValid && node.GetIsLeaf(accessor) == false)
                    {
                        iNode = node;
                        return iNode.GetIsFull(accessor) == false;
                    }

                    iNode = default;
                    return false;
                }
            }

            return rightChild;
        }

        public bool RemoveLeaf(ref RemoveArguments args, ref NodeRelatives relatives, ChunkRandomAccessor accessor)
        {
            var merge = false;
            var index = Find(args.Key, args.Comparer, accessor);

            if (index >= 0)
            {
                Debug.Assert(index >= 0 && index <= GetCount(accessor));

                args.SetRemovedValue(RemoveAtInternal(index, accessor).Value); // remove item

                if (!GetIsHalfFull(accessor)) // borrow or merge
                {
                    if (CanBorrowFrom(GetPrevious(accessor))) // left sibling
                    {
                        var last = GetPrevious(accessor).PopLastInternal(accessor);
                        PushFirst(last, accessor);

                        var p = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, accessor);
                        KeyValueItem.ChangeKey(ref p, last.Key);
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, p, accessor);

                        Validate(this, accessor);
                        Validate(GetPrevious(accessor), accessor);
                    }
                    else if (CanBorrowFrom(GetNext(accessor))) // right sibling
                    {
                        var first = GetNext(accessor).PopFirstInternal(accessor);
                        PushLast(first, accessor);

                        var p = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, accessor);
                        KeyValueItem.ChangeKey(ref p, GetNext(accessor).GetFirst(accessor).Key);
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, p, accessor);

                        Validate(this, accessor);
                        Validate(GetNext(accessor), accessor);
                    }
                    else // merge with either sibling.
                    {
                        merge = true; // set merge
                        if (relatives.HasTrueLeftSibling) // current node will be removed from parent.
                        {
                            GetPrevious(accessor).MergeLeft(this, accessor); // merge from left to keep items in order.
                            var p = GetPrevious(accessor);
                            p.SetNext(GetNext(accessor), accessor); // fix linked list
                            if (GetNext(accessor).IsValid)
                            {
                                var n = GetNext(accessor);
                                n.SetPrevious(GetPrevious(accessor), accessor);
                            }

                            Validate(GetPrevious(accessor), accessor);
                            Validate(GetNext(accessor), accessor);
                        }
                        else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                        {
                            MergeLeft(GetNext(accessor), accessor); // merge from right to keep items in order. 
                            SetNext(GetNext(accessor).GetNext(accessor), accessor); // fix linked list
                            if (GetNext(accessor).IsValid)
                            {
                                var n = GetNext(accessor);
                                n.SetPrevious(this, accessor);
                            }

                            Validate(this, accessor);
                            Validate(GetNext(accessor), accessor);
                        }
                        else Debug.Fail("leaf must either have true left or true right sibling.");
                    }
                }

                bool CanBorrowFrom(NodeWrapper leaf)
                {
                    if (leaf.IsValid == false) return false;
                    return leaf.GetCount(accessor) > (leaf.GetCapacity() / 2);
                }
            }

            return merge; // true if merge happened.
        }
        
        public bool RemoveInternal(ref RemoveArguments args, ref NodeRelatives relatives, ChunkRandomAccessor accessor)
        {
            var merge = false;
            var index = Find(args.Key, args.Comparer, accessor);
            if (index < 0) index = ~index - 1;

            Debug.Assert(index >= -1 && index < GetCount(accessor));

            var child = GetChild(index, accessor);
            NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives, accessor);
            var childMerged = child.Remove(ref args, ref childRelatives, accessor);

            if (childMerged)
            {
                RemoveAtInternal(Math.Max(0, index), accessor); // removes right sibling of child if left most child is merged, otherwise merged child is removed.

                if (!GetIsHalfFull(accessor)) // borrow or merge
                {
                    if (CanBorrowFrom(relatives.LeftSibling, out NodeWrapper leftSibling))
                    {
                        var last = leftSibling.PopLastInternal(accessor);

                        //KeyValueItem.SwapRightWith(ref last, ref Left); // swap left and right pointers.
                        var temp = GetLeft(accessor).ChunkId;
                        SetLeft(new NodeWrapper(_storage, last.Value), accessor);
                        last.Value = temp;

                        var pr = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, accessor);
                        KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                        relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pr, accessor);

                        PushFirst(last, accessor);

                        Validate(this, accessor);
                        Validate(leftSibling, accessor);
                    }
                    else if (CanBorrowFrom(relatives.RightSibling, out NodeWrapper rightSibling))
                    {
                        var first = rightSibling.PopFirstInternal(accessor);

                        //KeyValueItem.SwapRightWith(ref first, ref rightSibling.Left); // swap left and right pointers.
                        var temp = rightSibling.GetLeft(accessor).ChunkId;
                        rightSibling.SetLeft(new NodeWrapper(_storage, first.Value), accessor);
                        first.Value = temp;

                        var pl = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, accessor);
                        KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                        relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pl, accessor);

                        PushLast(first, accessor);

                        Validate(this, accessor);
                        Validate(rightSibling, accessor);
                    }
                    else // merge
                    {
                        merge = true;
                        if (relatives.HasTrueLeftSibling) // current node will be removed from parent
                        {
                            var pkey = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex, accessor).Key; // demote key
                            var mid = new KeyValueItem(pkey, GetLeft(accessor).ChunkId);
                            leftSibling.PushLast(mid, accessor);
                            leftSibling.MergeLeft(this, accessor); // merge from left to keep items in order.

                            Validate(leftSibling, accessor);
                        }
                        else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                        {
                            var pkey = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex, accessor).Key; // demote key
                            var mid = new KeyValueItem(pkey, rightSibling.GetLeft(accessor).ChunkId);
                            PushLast(mid, accessor);
                            MergeLeft(rightSibling, accessor); // merge from right to keep items in order.

                            Validate(this, accessor);
                        }
                    }
                }

                bool CanBorrowFrom(NodeWrapper node, out NodeWrapper iNode)
                {
                    if (node.IsValid == false || node.GetIsLeaf(accessor))
                    {
                        iNode = default;
                        return false;
                    }

                    iNode = node;
                    return iNode.GetCount(accessor) > iNode.GetCapacity() / 2;
                }
            }

            return merge; // true if merge happened.
        }

        private KeyValueItem RemoveAtInternal(int index, ChunkRandomAccessor accessor) => _storage.RemoveAt(this, index, accessor);

        private NodeWrapper SplitRight(NodeStates states, ChunkRandomAccessor accessor) => _storage.SplitRight(this, states, accessor);

        [Conditional("DEBUG")]
        private static void Validate(NodeWrapper node, ChunkRandomAccessor accessor)
        {
            if (node.IsValid == false) return;
            Debug.Assert(node.GetIsHalfFull(accessor));
            Debug.Assert(node.GetPrevious(accessor).IsValid == false || node.GetPrevious(accessor).GetNext(accessor) == node);
            Debug.Assert(node.GetNext(accessor).IsValid == false || node.GetNext(accessor).GetPrevious(accessor) == node);
        }

        public int Find(TKey key, IComparer<TKey> comparer, ChunkRandomAccessor accessor) => BinarySearch(key, comparer, accessor);

        private int BinarySearch(TKey key, IComparer<TKey> comparer, ChunkRandomAccessor accessor) => _storage.BinarySearch(this, key, comparer, accessor);

        private KeyValueItem InsertPopFirst(int index, KeyValueItem item, ChunkRandomAccessor accessor)
        {
            if (index == 0) return item;
            var value = PopFirstInternal(accessor);
            Insert(index - 1, item, accessor);

            return value;
        }

        public KeyValueItem InsertPopLast(int index, KeyValueItem item, ChunkRandomAccessor accessor)
        {
            if (index == GetCount(accessor)) return item;
            var value = PopLastInternal(accessor);
            Insert(index, item, accessor);

            return value;
        }

        public KeyValueItem PopFirstInternal(ChunkRandomAccessor accessor)
        {
            if (GetCount(accessor) <= 0) throw new InvalidOperationException("no items to remove.");

            var temp = _storage.GetItem(this, GetStart(accessor), false, accessor);
            _storage.SetItem(this, GetStart(accessor), default, false, accessor);
            _storage.IncrementStart(this, accessor);
            SetCount(GetCount(accessor) - 1, accessor);
            return temp;
        }

        public KeyValueItem PopLastInternal(ChunkRandomAccessor accessor)
        {
            if (GetCount(accessor) <= 0) throw new InvalidOperationException("no items to remove.");

            SetCount(GetCount(accessor) - 1, accessor);
            var end = GetEnd(accessor);
            var temp = _storage.GetItem(this, end, false, accessor);
            _storage.SetItem(this, end, default, false, accessor);
            return temp;
        }

        public void Insert(int index, KeyValueItem item, ChunkRandomAccessor accessor) => _storage.Insert(this, index, item, accessor);

        public int Adjust(int index) => (index < 0 || index >= GetCapacity()) ? (index + GetCapacity() * (-index).Sign()) : index;

        #endregion

        #region Equatable

        public bool Equals(NodeWrapper other) => ChunkId == other.ChunkId;

        public override bool Equals(object obj) => obj is NodeWrapper other && Equals(other);

        public override int GetHashCode() => ChunkId;

        public static bool operator ==(NodeWrapper left, NodeWrapper right) => left.Equals(right);

        public static bool operator !=(NodeWrapper left, NodeWrapper right) => !left.Equals(right);

        public NodeWrapper GetNearestChild(TKey key, IComparer<TKey> comparer, ChunkRandomAccessor accessor)
        {
            if (GetIsLeaf(accessor)) return default;

            var index = Find(key, comparer, accessor);
            if (index < 0) index = ~index - 1; // get next nearest item.
            return GetChild(index, accessor);
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
                get => _node.GetPrevious(_node._storage.Segment.CreateChunkRandomAccessor());
            }

            public NodeWrapper Next => _node.GetNext(_node._storage.Segment.CreateChunkRandomAccessor());
            public NodeWrapper Left => _node.GetLeft(_node._storage.Segment.CreateChunkRandomAccessor());
            public KeyValueItem[] Items
            {
                get
                {
                    var accessor = _node._storage.Segment.CreateChunkRandomAccessor();
                    var count = _node.GetCount(accessor);
                    var res = new KeyValueItem[count];
                    for (int i = 0; i < count; i++)
                    {
                        res[i] = _node.GetItem(i, accessor);
                    }

                    return res;
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
        internal void CheckConsistency(TKey key, CheckConsistencyParent parent, IComparer<TKey> comparer, int height, ChunkRandomAccessor accessor)
        {
            Trace.Assert(IsValid, "Root node should always be valid");

            Trace.Assert(((height == 1) && GetIsLeaf(accessor)) || ((height > 1) && (GetIsLeaf(accessor) == false)), $"Mismatch node's Height {height} with {GetIsLeaf(accessor)}");

            var firstKey = GetFirst(accessor).Key;
            Trace.Assert(comparer.Compare(firstKey, GetLast(accessor).Key) <= 0, $"First Key '{firstKey}' should be less than Last's one '{GetLast(accessor).Key}'.");
            Trace.Assert(comparer.Compare(firstKey, GetItem(0, accessor).Key) == 0, $"First.Key '{firstKey}' should be equal to first item's key '{GetItem(0, accessor).Key}'.");
            var lastKey = GetItem(GetCount(accessor) - 1, accessor).Key;
            Trace.Assert(comparer.Compare(lastKey, GetLast(accessor).Key) == 0, $"Last.Key '{GetLast(accessor).Key}' should be equal to last item's key '{lastKey}'.");

            var count = GetCount(accessor);
            var left = GetLeft(accessor);
            Trace.Assert((count == 0 || GetIsLeaf(accessor)) || (left.IsValid && left == GetFirstChild(accessor)), "Invalid Left Node, should be the first child");

            for (int i = 0; i < count; i++)
            {
                var childItem = GetIsLeaf(accessor) ? default : GetChild(i, accessor);
                var item = GetItem(i, accessor);
                if (GetIsLeaf(accessor) == false)
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

                if (GetIsLeaf(accessor) == false)
                {
                    left.CheckConsistency(item.Key, CheckConsistencyParent.Left, comparer, height - 1, accessor);
                    childItem.CheckConsistency(item.Key, CheckConsistencyParent.Right, comparer, height - 1, accessor);
                }

                left = childItem;
            }
        }

        #endregion
    }
}