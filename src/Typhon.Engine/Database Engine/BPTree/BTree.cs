// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//
//                                                   BASED ON https://github.com/MkazemAkhgary/BPlusTree
// Adapted :
//  - To allow multiple values per key
//  - Thread-safe
//  - Storage of data in a ChunkBasedSegment
//
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

namespace Typhon.Engine.BPTree
{
    #region Chunk definitions

    [Flags]
    public enum NodeStates
    {
        None       = 0x00,
        Ownership  = 0x01,
        IsLeaf     = 0x02
    }

    #endregion

    #region Misc Helpers

    /// <summary>
    /// provides some mathematical and numeric extensions.
    /// </summary>
    internal static class BTreeExtensions
    {
        /// <summary>
        /// fast sign function that uses bitwise operations instead of branches.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
        public static int Sign(this int x) => (x >> 31) | 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        unsafe internal static int BinarySearch<T>(T* array, int index, int length, T value, IComparer<T> comparer, int arrayStride) where T : unmanaged
        {
            int num1 = index;
            int num2 = index + length - 1;
            while (num1 <= num2)
            {
                int index1 = num1 + (num2 - num1 >> 1);
                int num3 = comparer.Compare(*(T*)((byte*)array + (arrayStride*index1)), value);
                if (num3 == 0)
                {
                    return index1;
                }

                if (num3 < 0)
                {
                    num1 = index1 + 1;
                }
                else
                {
                    num2 = index1 - 1;
                }
            }
            return ~num1;
        }
    }

    #endregion

    #region BTree+ main class

    public interface IBTree
    {
        bool AllowMultiple { get; }
        int Count { get; }
        unsafe int Add(void* keyAddr, int value);
        unsafe bool Remove(void* keyAddr, out int value);
        unsafe bool TryGet(void* keyAddr, out int value);
        unsafe bool RemoveValue(void* keyAddr, int elementId, int value);
        unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr);
        void CheckConsistency();
    }

    public abstract partial class BTree<TKey> : IBTree
        where TKey : unmanaged 
    {
        [DebuggerDisplay("Key: {Key}, Value: {Value}")]
        [StructLayout(LayoutKind.Sequential)]
        public struct KeyValueItem
        {
            public KeyValueItem(TKey key, int value)
            {
                Key = key;
                Value = value;
            }
            public TKey Key;
            public int Value;

            public static void ChangeKey(ref KeyValueItem item, TKey newKey)
            {
                item = new KeyValueItem(newKey, item.Value);
            }

            public static void SwapKeys(ref KeyValueItem x, ref KeyValueItem y)
            {
                var xKey = x.Key;
                ChangeKey(ref x, y.Key);
                ChangeKey(ref y, xKey);
            }
        }

        public struct InsertArguments
        {
            public InsertArguments(TKey key, int value, IComparer<TKey> comparer=null)
            {
                _value = value;
                _keyComparer = comparer ?? Comparer<TKey>.Default;
                Key = key;
                Added = false;
                ElementId = default;
            }
            public readonly TKey Key;
            public bool Added { get; private set; }

            public int ElementId;

            private readonly int _value;
            private readonly IComparer<TKey> _keyComparer;

            public int GetValue()
            {
                Added = true;
                return _value;
            }

            public int Compare(TKey left, TKey right) => _keyComparer.Compare(left, right);

            public IComparer<TKey> KeyComparer => _keyComparer;
        }

        public ref struct RemoveArguments
        {
            public readonly TKey Key;
            public readonly IComparer<TKey> Comparer;

            /// <summary>
            /// result is set once when the value is found at leaf node.
            /// </summary>
            public int Value { get; private set; }

            /// <summary>
            /// true if item is removed.
            /// </summary>
            public bool Removed { get; private set; }

            public RemoveArguments(in TKey key, in IComparer<TKey> comparer)
            {
                Key = key;
                Comparer = comparer;

                Value = default;
                Removed = false;
            }

            public void SetRemovedValue(int value)
            {
                Value = value;
                Removed = true;
            }
        }

        /// <summary>
        /// contains information about relatives of each node, such as ancestors and siblings.
        /// this information is used for borrow and spill operations.
        /// </summary>
        public readonly ref struct NodeRelatives
        {
            /*  Note: "/" is left pointer. "\" is right pointer.
             * 
             *               [LeftAncestor][RightAncestor]
             *              /              \              \
             *       [LeftSibling]       [Node]     [RightSibling]
             *      
             *      
             *                    [LeftAncestor][...]
             *                   /              \    ...
             *                [X]       [RightAncestor]  ...
             *                   \     /               \
             *         [LeftSibling][Node]       [RightSibling]
             *        
             *                      [RightAncestor][...]
             *                     /        \         ...
             *          [LeftAncestor]      [X][...]     ...
             *         /              \    /    
             *   [LeftSibling]     [Node][RightSibling]  
            */

            public readonly NodeWrapper LeftSibling;
            public readonly NodeWrapper RightSibling;

            /// <summary>
            /// nearest ancestor of node and its left sibling.
            /// </summary>
            public readonly NodeWrapper LeftAncestor;

            /// <summary>
            /// parent or ancestor used to get right sibling.
            /// </summary>
            public readonly NodeWrapper RightAncestor;

            /// <summary>
            /// index of item in ancestor that shares left sibling.
            /// </summary>
            public readonly int LeftAncestorIndex;

            /// <summary>
            /// index of item in ancestor that shares right sibling.
            /// </summary>
            public readonly int RightAncestorIndex;

            /// <summary>
            /// if left sibling is sibling and not cousin
            /// </summary>
            public readonly bool HasTrueLeftSibling;

            /// <summary>
            /// if right sibling is sibling and not cousin
            /// </summary>
            public readonly bool HasTrueRightSibling;

            private NodeRelatives(NodeWrapper leftAncestor, int leftAncestorIndex, NodeWrapper leftSibling, bool hasTrueLeftSibling,
                NodeWrapper rightAncestor, int rightAncestorIndex, NodeWrapper rightSibling, bool hasTrueRightSibling)
            {
                LeftAncestor = leftAncestor;
                LeftAncestorIndex = leftAncestorIndex;
                LeftSibling = leftSibling;
                HasTrueLeftSibling = hasTrueLeftSibling;

                RightAncestor = rightAncestor;
                RightAncestorIndex = rightAncestorIndex;
                RightSibling = rightSibling;
                HasTrueRightSibling = hasTrueRightSibling;
            }

            /// <summary>
            /// creates new relatives for child node.
            /// </summary>
            public static NodeRelatives Create(NodeWrapper child, int index, NodeWrapper parent, ref NodeRelatives parentRelatives)
            {
                Debug.Assert(index >= -1 && index < parent.Length);

                // assign nearest ancestors between child and siblings.
                NodeWrapper leftAncestor, rightAncestor;
                int leftAncestorIndex, rightAncestorIndex;
                NodeWrapper leftSibling, rightSibling;
                bool hasTrueLeftSibling, hasTrueRightSibling;

                if (index == -1) // if child is left most, use left cousin as left sibling.
                {
                    leftAncestor = parentRelatives.LeftAncestor;
                    leftAncestorIndex = parentRelatives.LeftAncestorIndex;
                    leftSibling = parentRelatives.LeftSibling.IsValid ? parentRelatives.LeftSibling.GetLastChild() : default;
                    hasTrueLeftSibling = false;

                    rightAncestor = parent;
                    rightAncestorIndex = index + 1;
                    rightSibling = parent.GetChild(rightAncestorIndex);
                    hasTrueRightSibling = true;
                }
                else if (index == parent.Length - 1) // if child is right most, use right cousin as right sibling.
                {
                    leftAncestor = parent;
                    leftAncestorIndex = index;
                    leftSibling = parent.GetChild(leftAncestorIndex - 1);
                    hasTrueLeftSibling = true;

                    rightAncestor = parentRelatives.RightAncestor;
                    rightAncestorIndex = parentRelatives.RightAncestorIndex;
                    rightSibling = parentRelatives.RightSibling.IsValid ? parentRelatives.RightSibling.GetFirstChild() : default;
                    hasTrueRightSibling = false;
                }
                else // child is not right most nor left most.
                {
                    leftAncestor = parent;
                    leftAncestorIndex = index;
                    leftSibling = parent.GetChild(leftAncestorIndex - 1);
                    hasTrueLeftSibling = true;

                    rightAncestor = parent;
                    rightAncestorIndex = index + 1;
                    rightSibling = parent.GetChild(rightAncestorIndex);
                    hasTrueRightSibling = true;
                }

                return new NodeRelatives(leftAncestor, leftAncestorIndex, leftSibling, hasTrueLeftSibling,
                    rightAncestor, rightAncestorIndex, rightSibling, hasTrueRightSibling);
            }
        }

        #region Private data

        public abstract bool AllowMultiple { get; }
        protected abstract BaseNodeStorage GetStorage();
        protected IComparer<TKey> Comparer;

        private readonly ChunkBasedSegment _segment;
        private readonly BaseNodeStorage _storage;

        public int Count { get; private set; }

        private NodeWrapper Root;
        private NodeWrapper LinkList;
        private NodeWrapper ReverseLinkList;
        public int Height;

        protected KeyValueItem First => LinkList.First;
        protected KeyValueItem Last => ReverseLinkList.Last;

        #endregion

        #region Public API

        protected BTree(ChunkBasedSegment segment, ChunkRandomAccessor accessor)
        {
            Comparer = Comparer<TKey>.Default;
            _segment = segment;
            _storage = GetStorage();
            _storage.Initialize(this, _segment, accessor);
            // We make sure the chunk 0 is reserved so we can consider any ChunkId == 0 as a "null pointer".
            // So any default constructed type declaring ChunkId fields can have this "null" by default.
            _segment.ReserveChunk(0);
        }

        public unsafe int Add(void* keyAddr, int value) => Add(Unsafe.AsRef<TKey>(keyAddr), value);
        public unsafe bool Remove(void* keyAddr, out int value) => Remove(Unsafe.AsRef<TKey>(keyAddr), out value);
        public unsafe bool TryGet(void* keyAddr, out int value) => TryGet(Unsafe.AsRef<TKey>(keyAddr), out value);
        public unsafe bool RemoveValue(void* keyAddr, int elementId, int value) => RemoveValue(Unsafe.AsRef<TKey>(keyAddr), elementId, value);
        public unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr) => TryGetMultiple(Unsafe.AsRef<TKey>(keyAddr));

        public int Add(TKey key, int value)
        {
            var args = new InsertArguments(key, value, Comparer);
            AddOrUpdateCore(ref args);
            return args.ElementId;
        }

        public bool Remove(TKey key, out int value)
        {
            var args = new RemoveArguments(key, Comparer);
            RemoveCore(ref args);
            value = args.Value;
            return args.Removed;
        }

        public void CheckConsistency()
        {
            // Recursive check from Root to leaf
            if (Count == 0)
            {
                return;
            }

            Root.CheckConsistency(default, NodeWrapper.CheckConsistencyParent.Root, Comparer, Height);

            // Check the linked link of leaves in forward
            NodeWrapper prev = default;
            var cur = LinkList;
            TKey prevValue = default;

            while (cur.IsValid)
            {
                if (cur != LinkList)
                {
                    Trace.Assert(prev.Next == cur, " Prev.Next doesn't link to current");
                    Trace.Assert(cur.Previous == prev, "Cur.Previous doesn't link to previous");

                    Trace.Assert(Comparer.Compare(prevValue, cur.First.Key) < 0, $"Previous Node's first key '{prevValue}' should be less than current node's first key'{cur.First.Key}'.");
                }

                prevValue = cur.Last.Key;
                prev = cur;
                cur = cur.Next;
            }
            Trace.Assert(prev == ReverseLinkList, "Last Node of the forward chain doesn't match ReverseLinkList");

            // Check the linked link of leaves in reverse
            NodeWrapper next = default;
            cur = ReverseLinkList;
            TKey nextValue = default;

            while (cur.IsValid)
            {
                if (cur != ReverseLinkList)
                {
                    Trace.Assert(next.Previous == cur, " Next.Previous doesn't link to current");
                    Trace.Assert(cur.Next == next, "Cur.Next doesn't link to next");

                    Trace.Assert(Comparer.Compare(nextValue, cur.Last.Key) > 0, $"Next Node's last key '{nextValue}' should be greater than current node's last key'{cur.Last.Key}'.");
                }

                nextValue = cur.First.Key;
                next = cur;
                cur = cur.Previous;
            }
            Trace.Assert(next == LinkList, "Last Node of the reverse chain doesn't match LinkedList");
        }

        public int this[TKey key]
        {
            get
            {
                if (!TryGet(key, out var value)) throw new KeyNotFoundException();
                return value;
            }
        }

        public bool TryGet(TKey key, out int value)
        {
            value = default;
            var leaf = FindLeaf(key, out var index);
            if (index >= 0) value = leaf.GetItem(index).Value;
            return index >= 0;
        }

        public bool RemoveValue(TKey key, int elementId, int value)
        {
            if (TryGet(key, out var bufferId) == false)
            {
                return false;
            }
            var res = _storage.RemoveFromBuffer(bufferId, elementId, value);
            if (res == -1) return false;

            // Remove the key if we no longer have values stored there
            if (res == 0)
            {
                var args = new RemoveArguments(key, Comparer);
                RemoveCore(ref args);

                if (args.Removed)
                {
                    _storage.DeleteBuffer(args.Value);
                }
            }

            return true;
        }

        public VariableSizedBufferAccessor<int> TryGetMultiple(TKey key)
        {
            if (TryGet(key, out var bufferId) == false)
            {
                return default;
            }
            return _storage.GetBufferReadOnlyAccessor(bufferId);
        }

        #endregion

        #region Private API

        protected internal NodeWrapper AllocNode(NodeStates states)
        {
            var node = new NodeWrapper(_storage, _segment.AllocateChunk(true));
            _storage.InitializeNode(node, states);
            return node;
        }

        private void RemoveCore(ref RemoveArguments args)
        {
            if (Count == 0) return;

            // optimize for removing items from beginning
            int order = args.Comparer.Compare(args.Key, First.Key);
            if (order < 0) return;
            if (order == 0 && (Root == LinkList || LinkList.Count > LinkList.Capacity / 2))
            {
                args.SetRemovedValue(LinkList.PopFirstInternal().Value);
                Debug.Assert(Root == LinkList || LinkList.IsHalfFull);
                Count--;
                if (Count == 0)
                {
                    Root = LinkList = ReverseLinkList = default;
                    Height--;
                }
                return;
            }
            // optimize for removing items from end
            order = args.Comparer.Compare(args.Key, Last.Key);
            if (order > 0) return;
            if (order == 0 && (Root == ReverseLinkList || ReverseLinkList.Count > ReverseLinkList.Capacity / 2))
            {
                args.SetRemovedValue(ReverseLinkList.PopLastInternal().Value);
                Debug.Assert(Root == ReverseLinkList || ReverseLinkList.IsHalfFull);
                Count--; // here count never becomes zero.
                return;
            }

            var nodeRelatives = new NodeRelatives();
            var merge = Root.Remove(ref args, ref nodeRelatives);

            if (args.Removed)
            {
                Count--;
            }

            if (merge && Root.Length == 0)
            {
                Root = Root.GetChild(-1); // left most child becomes root. (returns null for leafs)
                if (Root.IsValid == false)
                {
                    LinkList = default;
                    ReverseLinkList = default;
                }
                Height--;
            }

            if (ReverseLinkList.IsValid && ReverseLinkList.Previous.IsValid && ReverseLinkList.Previous.Next.IsValid==false) // true if last leaf is merged.
            {
                ReverseLinkList = ReverseLinkList.Previous;
            }
        }


        private void AddOrUpdateCore(ref InsertArguments args)
        {
            if (Count == 0)
            {
                Root = AllocNode(NodeStates.IsLeaf);
                LinkList = Root;
                ReverseLinkList = LinkList;
                Height++;
            }

            int CreateBufferAndAddValue(ref InsertArguments iargs)
            {
                var bufferId = _storage.CreateBuffer();
                iargs.ElementId =_storage.Append(bufferId, iargs.GetValue());
                return bufferId;
            }

            // append optimization: if item key is in order, this may add item in O(1) operation.
            int order = Count == 0 ? 1 : args.Compare(args.Key, Last.Key);
            if (order > 0 && !ReverseLinkList.IsFull)
            {
                var value = AllowMultiple ? CreateBufferAndAddValue(ref args) : args.GetValue();
                ReverseLinkList.PushLast(new KeyValueItem(args.Key, value));
                Count++;
                return;
            }
            else if (order == 0)
            {
                //var item = ReverseLinkList.Last;
                //KeyValueItem.ChangeValue(ref item, args.GetUpdateValue(item.Value));
                //ReverseLinkList.Last = item;
                if (AllowMultiple)
                {
                    args.ElementId = _storage.Append(Last.Value, args.GetValue());
                }
                return;
            }

            // pre-append optimization: if item key is in order, this may add item in O(1) operation.
            order = args.Compare(args.Key, First.Key);
            if (order < 0 && !LinkList.IsFull)
            {
                var value = AllowMultiple ? CreateBufferAndAddValue(ref args) : args.GetValue();
                LinkList.PushFirst(new KeyValueItem(args.Key, value));
                Count++;
                return;
            }
            else if (order == 0)
            {
                //var item = LinkList.Items.First;
                //KeyValueItem.ChangeValue(ref item, args.GetUpdateValue(item.Value));
                //LinkList.Items.First = item;
                if (AllowMultiple)
                {
                    args.ElementId = _storage.Append(First.Value, args.GetValue());
                }
                return;
            }

            var nodeRelatives = new NodeRelatives();
            var rightSplit = Root.Insert(ref args, ref nodeRelatives, default);

            if (args.Added)
            {
                Count++;
            }

            // if split occurred at root, make a new root and increase height.
            if (rightSplit != null)
            {
                var newRoot = AllocNode(NodeStates.None);
                newRoot.Left = Root;

                newRoot.Insert(0, rightSplit.Value);
                Root = newRoot;
                Height++;
            }

            var next = ReverseLinkList.Next;
            if (next.IsValid)
            {
                ReverseLinkList = next;
            }
        }

        private NodeWrapper FindLeaf(TKey key, out int index)
        {
            index = -1;
            if (Count == 0) return default;

            var node = Root;
            while (!node.IsLeaf) node = node.GetNearestChild(key, Comparer);
            index = node.Find(key, Comparer);
            return node;
        }

        #endregion
    }

    #endregion
}