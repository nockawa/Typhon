// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine.BPTree
{
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
            public bool IsLeaf => (_storage.GetNodeStates(this) & NodeStates.IsLeaf) != 0;
            public int Capacity => _storage.GetNodeCapacity();
            public bool IsFull => Count == Capacity;
            public bool IsHalfFull => Count >= (Capacity / 2);
            public int Length => Count;

            public int Count
            {
                get => _storage.GetCount(this);
                internal set => _storage.SetCount(this, value);
            }
            public int Start
            {
                get => _storage.GetStart(this);
                private set => _storage.SetStart(this, value);
            }
            public int End
            {
                get => _storage.GetEnd(this);
            }

            public KeyValueItem First
            {
                get => _storage.GetItem(this, 0, true);
                set => _storage.SetItem(this, 0, value, true);
            }

            public KeyValueItem Last
            {
                get => _storage.GetItem(this, _storage.GetCount(this) - 1, true);
                set => _storage.SetItem(this, _storage.GetCount(this) - 1, value, true);
            }

            public NodeWrapper Previous
            {
                get => _storage.GetPreviousNode(this);
                set => _storage.SetPreviousNode(this, value.ChunkId);
            }

            public NodeWrapper Next
            {
                get => _storage.GetNextNode(this);
                set => _storage.SetNextNode(this, value.ChunkId);
            }

            public NodeWrapper Left
            {
                get => _storage.GetLeftNode(this);
                set => _storage.SetLeftNode(this, value.ChunkId);
            }

            public KeyValueItem GetItem(int index) => _storage.GetItem(this, index, true);
            public void SetItem(int index, KeyValueItem value) => _storage.SetItem(this, index, value, true);

            #endregion

            #region Node Operations

            public void PushFirst(KeyValueItem item) => _storage.PushFirst(this, item);
            public void PushLast(KeyValueItem item) => _storage.PushLast(this, item);
            public void MergeLeft(NodeWrapper right) => _storage.MergeLeft(this, right);

            public NodeWrapper GetChild(int index) => _storage.GetChild(this, index);

            public NodeWrapper GetLastChild() => _storage.GetLastChild(this);

            public NodeWrapper GetFirstChild() => _storage.GetFirstChild(this);

            public KeyValueItem? Insert(ref InsertArguments args, ref NodeRelatives relatives, NodeWrapper parent)
            {
                if (IsLeaf)
                {
                    return InsertLeaf(ref args, ref relatives);
                }
                else
                {
                    return InsertInternal(ref args, ref relatives);
                }
            }

            public bool Remove(ref RemoveArguments args, ref NodeRelatives relatives)
            {
                if (IsLeaf)
                {
                    return RemoveLeaf(ref args, ref relatives);
                }
                else
                {
                    return RemoveInternal(ref args, ref relatives);
                }
            }

            private KeyValueItem? InsertLeaf(ref InsertArguments args, ref NodeRelatives relatives)
            {
                KeyValueItem? rightLeaf = null;

                var index = Find(args.Key, args.KeyComparer);

                if (index < 0)
                {
                    index = ~index;

                    Debug.Assert(index >= 0 && index <= Count);

                    int value = args.GetValue();
                    if (_storage.Owner.AllowMultiple)
                    {
                        var bufferId = _storage.CreateBuffer();
                        args.ElementId = _storage.Append(bufferId, value);
                        value = bufferId;
                    }
                    var item = new KeyValueItem(args.Key, value); // item to add

                    if (!IsFull) // if there is space, add and return.
                    {
                        Insert(index, item); // insert value and return.
                    }
                    else // cant add, spill or split
                    {
                        if (CanSpillTo(Previous))
                        {
                            var first = InsertPopFirst(index, item);
                            Previous.PushLast(first); // move smallest item to left sibling.

                            // update ancestors key.
                            var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex);
                            KeyValueItem.ChangeKey(ref pl, First.Key);
                            relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl);

                            Validate(this);
                            Validate(Previous);
                        }
                        else if (CanSpillTo(Next))
                        {
                            var last = InsertPopLast(index, item);
                            Next.PushFirst(last);

                            // update ancestors key.
                            var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex);
                            KeyValueItem.ChangeKey(ref pr, last.Key);
                            relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr);

                            Validate(this);
                            Validate(Next);
                        }
                        else // split, then promote middle item
                        {
                            var rightNode = SplitNodeRight(this);

                            // insert item and find middle value to promote
                            if (index <= Count)
                            {
                                // when adding item to this node, pop last item and give it to right node.
                                // this way, this and right split always have equal length or maximum 1 difference. (also avoids overflow when capacity = 1)
                                rightNode.PushFirst(InsertPopLast(index, item));
                            }
                            else if (index > Count)
                            {
                                rightNode.Insert(index - Count, item);
                            }

                            rightLeaf = new KeyValueItem(rightNode.First.Key, rightNode.ChunkId);

                            Validate(this);
                            Validate(rightNode);
                        }
                    }

                    // splits right side to new node and keeps left side for current node.
                    NodeWrapper SplitNodeRight(NodeWrapper left)
                    {
                        var right = left.SplitRight(NodeStates.IsLeaf);
                        var next = left.Next;
                        if (next.IsValid)
                        {
                            next.Previous = right;
                            right.Next = left.Next; // to make linked list.
                        }
                        right.Previous = left;
                        left.Next = right;
                        return right;
                    }

                    bool CanSpillTo(NodeWrapper leaf)
                    {
                        return leaf.IsValid && leaf.IsFull == false;
                    }
                }
                else
                {
                    var curItem = GetItem(index);
                    args.ElementId = _storage.Append(curItem.Value, args.GetValue());
                    //KeyValueItem.ChangeValue(ref item, args.GetUpdateValue(item.Value)); // update item value
                    //Items[index] = item; // set new item
                }

                return rightLeaf;
            }

            private KeyValueItem? InsertInternal(ref InsertArguments args, ref NodeRelatives relatives)
            {
                var index = Find(args.Key, args.KeyComparer);

                // -1 because items smaller than key have to go inside left child. 
                // since items at each index point to right child, index is decremented to get left child.
                if (index < 0) index = ~index - 1;

                Debug.Assert(index >= -1 && index < Count);

                // get child to traverse through.
                var child = GetChild(index);
                NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives);

                var rightChild = child.Insert(ref args, ref childRelatives, this);

                if (rightChild is KeyValueItem middle) // if split, add middle key to this node.
                {
                    // +1 because middle is always right side which is fresh node. 
                    // items at index already point to left node after split. so middle must go after index.
                    index++;

                    rightChild = null;
                    if (!IsFull)
                    {
                        Insert(index, middle);
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

                            var first = InsertPopFirst(index, middle);

                            //KeyValueItem.SwapRightWith(ref first, ref Left);
                            // swap left and right nodes
                            var temp = Left.ChunkId;
                            Left = new NodeWrapper(_storage, first.Value);
                            first = new KeyValueItem(first.Key, temp);

                            var pl = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex);
                            KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                            relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pl);

                            leftSibling.PushLast(first);

                            Validate(this);
                            Validate(leftSibling);
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

                            var last = InsertPopLast(index, middle);

                            //KeyValueItem.SwapRightWith(ref last, ref rightSibling.Left);
                            // swap left and right node
                            var temp = rightSibling.Left.ChunkId;
                            rightSibling.Left = new NodeWrapper(_storage, last.Value);
                            last = new KeyValueItem(last.Key, temp);

                            var pr = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex);
                            KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                            relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pr);

                            rightSibling.PushFirst(last);

                            Validate(this);
                            Validate(rightSibling);
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

                            var rightNode = SplitRight(NodeStates.None);

                            // find middle key to promote
                            if (index < Count)
                            {
                                middle = InsertPopLast(index, middle);
                            }
                            else if (index > Count)
                            {
                                middle = rightNode.InsertPopFirst(index - Count, middle);
                            }

                            rightNode.Left = new NodeWrapper(_storage, middle.Value);
                            middle = new KeyValueItem(middle.Key, rightNode.ChunkId);
                            rightChild = middle;

                            Validate(this);
                            Validate(rightNode);
                        }
                    }

                    bool CanSpillTo(NodeWrapper node, out NodeWrapper iNode)
                    {
                        if (node.IsValid && node.IsLeaf == false)
                        {
                            iNode = node;
                            return iNode.IsFull == false;
                        }

                        iNode = default;
                        return false;
                    }
                }

                return rightChild;
            }

            public bool RemoveLeaf(ref RemoveArguments args, ref NodeRelatives relatives)
            {
                var merge = false;
                var index = Find(args.Key, args.Comparer);

                if (index >= 0)
                {
                    Debug.Assert(index >= 0 && index <= Count);

                    args.SetRemovedValue(RemoveAtInternal(index).Value); // remove item

                    if (!IsHalfFull) // borrow or merge
                    {
                        if (CanBorrowFrom(Previous)) // left sibling
                        {
                            var last = Previous.PopLastInternal();
                            PushFirst(last);

                            var p = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex);
                            KeyValueItem.ChangeKey(ref p, last.Key);
                            relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, p);

                            Validate(this);
                            Validate(Previous);
                        }
                        else if (CanBorrowFrom(Next)) // right sibling
                        {
                            var first = Next.PopFirstInternal();
                            PushLast(first);

                            var p = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex);
                            KeyValueItem.ChangeKey(ref p, Next.First.Key);
                            relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, p);

                            Validate(this);
                            Validate(Next);
                        }
                        else // merge with either sibling.
                        {
                            merge = true; // set merge
                            if (relatives.HasTrueLeftSibling) // current node will be removed from parent.
                            {
                                Previous.MergeLeft(this); // merge from left to keep items in order.
                                var p = Previous;
                                p.Next = Next; // fix linked list
                                if (Next.IsValid)
                                {
                                    var n = Next;
                                    n.Previous = Previous;
                                }

                                Validate(Previous);
                                Validate(Next);
                            }
                            else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                            {
                                MergeLeft(Next); // merge from right to keep items in order. 
                                Next = Next.Next; // fix linked list
                                if (Next.IsValid)
                                {
                                    var n = Next;
                                    n.Previous = this;
                                }

                                Validate(this);
                                Validate(Next);
                            }
                            else Debug.Fail("leaf must either have true left or true right sibling.");
                        }
                    }

                    bool CanBorrowFrom(NodeWrapper leaf)
                    {
                        if (leaf.IsValid == false) return false;
                        return leaf.Count > (leaf.Capacity / 2);
                    }
                }

                return merge; // true if merge happened.
            }
            public bool RemoveInternal(ref RemoveArguments args, ref NodeRelatives relatives)
            {
                var merge = false;
                var index = Find(args.Key, args.Comparer);
                if (index < 0) index = ~index - 1;

                Debug.Assert(index >= -1 && index < Count);

                var child = GetChild(index);
                NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives);
                var childMerged = child.Remove(ref args, ref childRelatives);

                if (childMerged)
                {
                    RemoveAtInternal(Math.Max(0, index)); // removes right sibling of child if left most child is merged, other wise merged child is removed.

                    if (!IsHalfFull) // borrow or merge
                    {
                        if (CanBorrowFrom(relatives.LeftSibling, out NodeWrapper leftSibling))
                        {
                            var last = leftSibling.PopLastInternal();

                            //KeyValueItem.SwapRightWith(ref last, ref Left); // swap left and right pointers.
                            var temp = Left.ChunkId;
                            Left = new NodeWrapper(_storage, last.Value);
                            last.Value = temp;

                            var pr = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex);
                            KeyValueItem.SwapKeys(ref pr, ref last); // swap ancestor key with item.
                            relatives.LeftAncestor.SetItem(relatives.LeftAncestorIndex, pr);

                            PushFirst(last);

                            Validate(this);
                            Validate(leftSibling);
                        }
                        else if (CanBorrowFrom(relatives.RightSibling, out NodeWrapper rightSibling))
                        {
                            var first = rightSibling.PopFirstInternal();

                            //KeyValueItem.SwapRightWith(ref first, ref rightSibling.Left); // swap left and right pointers.
                            var temp = rightSibling.Left.ChunkId;
                            rightSibling.Left = new NodeWrapper(_storage, first.Value);
                            first.Value = temp;

                            var pl = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex);
                            KeyValueItem.SwapKeys(ref pl, ref first); // swap ancestor key with item.
                            relatives.RightAncestor.SetItem(relatives.RightAncestorIndex, pl);

                            PushLast(first);

                            Validate(this);
                            Validate(rightSibling);
                        }
                        else // merge
                        {
                            merge = true;
                            if (relatives.HasTrueLeftSibling) // current node will be removed from parent
                            {
                                var pkey = relatives.LeftAncestor.GetItem(relatives.LeftAncestorIndex).Key; // demote key
                                var mid = new KeyValueItem(pkey, Left.ChunkId);
                                leftSibling.PushLast(mid);
                                leftSibling.MergeLeft(this); // merge from left to keep items in order.

                                Validate(leftSibling);
                            }
                            else if (relatives.HasTrueRightSibling) // right sibling will be removed from parent
                            {
                                var pkey = relatives.RightAncestor.GetItem(relatives.RightAncestorIndex).Key; // demote key
                                var mid = new KeyValueItem(pkey, rightSibling.Left.ChunkId);
                                PushLast(mid);
                                MergeLeft(rightSibling); // merge from right to keep items in order.

                                Validate(this);
                            }
                        }
                    }

                    bool CanBorrowFrom(NodeWrapper node, out NodeWrapper iNode)
                    {
                        if (node.IsValid == false || node.IsLeaf)
                        {
                            iNode = default;
                            return false;
                        }

                        iNode = node;
                        return iNode.Count > iNode.Capacity / 2;
                    }
                }

                return merge; // true if merge happened.
            }

            private KeyValueItem RemoveAtInternal(int index) => _storage.RemoveAt(this, index);

            private NodeWrapper SplitRight(NodeStates states) => _storage.SplitRight(this, states);

            [Conditional("DEBUG")]
            private static void Validate(NodeWrapper node)
            {
                if (node.IsValid == false) return;
                Debug.Assert(node.IsHalfFull);
                Debug.Assert(node.Previous.IsValid == false || node.Previous.Next == node);
                Debug.Assert(node.Next.IsValid == false || node.Next.Previous == node);
            }

            public int Find(TKey key, IComparer<TKey> comparer) => BinarySearch(key, comparer);

            private int BinarySearch(TKey key, IComparer<TKey> comparer) => _storage.BinarySearch(this, key, comparer);

            private KeyValueItem InsertPopFirst(int index, KeyValueItem item)
            {
                if (index == 0) return item;
                var value = PopFirstInternal();
                Insert(index - 1, item);

                return value;
            }

            public KeyValueItem InsertPopLast(int index, KeyValueItem item)
            {
                if (index == Count) return item;
                var value = PopLastInternal();
                Insert(index, item);

                return value;
            }

            public KeyValueItem PopFirstInternal()
            {
                if (Count <= 0) throw new InvalidOperationException("no items to remove.");

                var temp = _storage.GetItem(this, Start, false);
                _storage.SetItem(this, Start, default, false);
                _storage.IncrementStart(this);
                Count--;
                return temp;
            }

            public KeyValueItem PopLastInternal()
            {
                if (Count <= 0) throw new InvalidOperationException("no items to remove.");

                Count--;
                var end = End;
                var temp = _storage.GetItem(this, end, false);
                _storage.SetItem(this, end, default, false);
                return temp;
            }

            public void Insert(int index, KeyValueItem item) => _storage.Insert(this, index, item);

            public int Adjust(int index) => (index < 0 || index >= Capacity) ? (index + Capacity * (-index).Sign()) : index;

            #endregion

            #region Equatable

            public bool Equals(NodeWrapper other) => ChunkId == other.ChunkId;

            public override bool Equals(object obj) => obj is NodeWrapper other && Equals(other);

            public override int GetHashCode() => ChunkId;

            public static bool operator ==(NodeWrapper left, NodeWrapper right) => left.Equals(right);

            public static bool operator !=(NodeWrapper left, NodeWrapper right) => !left.Equals(right);

            public NodeWrapper GetNearestChild(TKey key, IComparer<TKey> comparer)
            {
                if (IsLeaf) return default;

                var index = Find(key, comparer);
                if (index < 0) index = ~index - 1; // get next nearest item.
                return GetChild(index);
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

                public NodeWrapper Previous => _node.Previous;
                public NodeWrapper Next => _node.Next;
                public NodeWrapper Left => _node.Left;
                public KeyValueItem[] Items
                {
                    get
                    {
                        var count = _node.Count;
                        var res = new KeyValueItem[count];
                        for (int i = 0; i < count; i++)
                        {
                            res[i] = _node.GetItem(i);
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
            internal void CheckConsistency(TKey key, CheckConsistencyParent parent, IComparer<TKey> comparer, int height)
            {
                Trace.Assert(IsValid, "Root node should always be valid");

                Trace.Assert(((height == 1) && IsLeaf) || ((height > 1) && (IsLeaf == false)), $"Mismatch node's Height {height} with {IsLeaf}");

                var firstKey = First.Key;
                Trace.Assert(comparer.Compare(firstKey, Last.Key) <= 0, $"First Key '{firstKey}' should be less than Last's one '{Last.Key}'.");
                Trace.Assert(comparer.Compare(firstKey, GetItem(0).Key) == 0, $"First.Key '{firstKey}' should be equal to first item's key '{GetItem(0).Key}'.");
                var lastKey = GetItem(Count - 1).Key;
                Trace.Assert(comparer.Compare(lastKey, Last.Key) == 0, $"Last.Key '{Last.Key}' should be equal to last item's key '{lastKey}'.");

                var count = Count;
                var left = Left;
                Trace.Assert((count == 0 || IsLeaf) || (left.IsValid && left == GetFirstChild()), "Invalid Left Node, should be the first child");

                for (int i = 0; i < count; i++)
                {
                    var childItem = IsLeaf ? default : GetChild(i);
                    var item = GetItem(i);
                    if (IsLeaf == false)
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

                    if (IsLeaf == false)
                    {
                        left.CheckConsistency(item.Key, CheckConsistencyParent.Left, comparer, height - 1);
                        childItem.CheckConsistency(item.Key, CheckConsistencyParent.Right, comparer, height - 1);
                    }

                    left = childItem;
                }
            }

            #endregion
        }
    }
}