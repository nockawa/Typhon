// unset

using JetBrains.Annotations;
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

namespace Typhon.Engine.BPTree;

#region Chunk definitions

[Flags]
public enum NodeStates
{
    None       = 0x00,
    Ownership  = 0x01,
    IsLeaf     = 0x02
}

#endregion

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BTreeHeader
{
    unsafe public static readonly int Size = sizeof(BTreeHeader);
    public static readonly int TotalSize =  ChunkBasedSegmentHeader.TotalSize + Size;
    public static readonly int Offset = ChunkBasedSegmentHeader.TotalSize;
    
    public int Count;
    public int RootChunkId;
}

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
    ChunkBasedSegment Segment { get; }
    bool AllowMultiple { get; }
    // int Count { get; }
    unsafe int Add(void* keyAddr, int value, ChunkRandomAccessor accessor);
    unsafe bool Remove(void* keyAddr, out int value, ChunkRandomAccessor accessor);
    unsafe bool TryGet(void* keyAddr, out int value, ChunkRandomAccessor accessor);
    unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ChunkRandomAccessor accessor);
    unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ChunkRandomAccessor accessor);
    void CheckConsistency(ChunkRandomAccessor accessor);
}

public abstract partial class BTree<TKey> : IBTree where TKey : unmanaged 
{
    const int ChunkRandomAccessorPagedCount = 8;

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
        public InsertArguments(TKey key, int value, IComparer<TKey> comparer=null, ChunkRandomAccessor accessor = null)
        {
            _value = value;
            _keyComparer = comparer ?? Comparer<TKey>.Default;
            Key = key;
            Added = false;
            ElementId = default;
            Accessor = accessor;
        }
        public readonly TKey Key;
        public bool Added { get; private set; }

        public int ElementId;

        public ChunkRandomAccessor Accessor;

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
        public readonly ChunkRandomAccessor Accessor;

        /// <summary>
        /// result is set once when the value is found at leaf node.
        /// </summary>
        public int Value { get; private set; }

        /// <summary>
        /// true if item is removed.
        /// </summary>
        public bool Removed { get; private set; }

        public RemoveArguments(in TKey key, in IComparer<TKey> comparer, ChunkRandomAccessor accessor)
        {
            Key = key;
            Comparer = comparer;

            Value = default;
            Removed = false;
            Accessor = accessor;
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
    public readonly struct NodeRelatives
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
        public static void Create(NodeWrapper child, int index, NodeWrapper parent, ref NodeRelatives parentRelatives, out NodeRelatives res,
            ChunkRandomAccessor accessor)
        {
            Debug.Assert(index >= -1 && index < parent.GetLength(accessor));

            // assign nearest ancestors between child and siblings.
            NodeWrapper leftAncestor, rightAncestor;
            int leftAncestorIndex, rightAncestorIndex;
            NodeWrapper leftSibling, rightSibling;
            bool hasTrueLeftSibling, hasTrueRightSibling;

            if (index == -1) // if child is left most, use left cousin as left sibling.
            {
                leftAncestor = parentRelatives.LeftAncestor;
                leftAncestorIndex = parentRelatives.LeftAncestorIndex;
                leftSibling = parentRelatives.LeftSibling.IsValid ? parentRelatives.LeftSibling.GetLastChild(accessor) : default;
                hasTrueLeftSibling = false;

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                rightSibling = parent.GetChild(rightAncestorIndex, accessor);
                hasTrueRightSibling = true;
            }
            else if (index == parent.GetLength(accessor) - 1) // if child is right most, use right cousin as right sibling.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                leftSibling = parent.GetChild(leftAncestorIndex - 1, accessor);
                hasTrueLeftSibling = true;

                rightAncestor = parentRelatives.RightAncestor;
                rightAncestorIndex = parentRelatives.RightAncestorIndex;
                rightSibling = parentRelatives.RightSibling.IsValid ? parentRelatives.RightSibling.GetFirstChild(accessor) : default;
                hasTrueRightSibling = false;
            }
            else // child is not right most nor left most.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                leftSibling = parent.GetChild(leftAncestorIndex - 1, accessor);
                hasTrueLeftSibling = true;

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                rightSibling = parent.GetChild(rightAncestorIndex, accessor);
                hasTrueRightSibling = true;
            }

            res = new NodeRelatives(leftAncestor, leftAncestorIndex, leftSibling, hasTrueLeftSibling,
                rightAncestor, rightAncestorIndex, rightSibling, hasTrueRightSibling);
        }
    }

    #region Private data

    public abstract bool AllowMultiple { get; }
    protected abstract BaseNodeStorage GetStorage();
    protected IComparer<TKey> Comparer;

    private AccessControl _access;
    private readonly ChunkBasedSegment _segment;
    private readonly BaseNodeStorage _storage;

    public bool IsEmpty(ChunkRandomAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false, out var entry);
        var res = header.Count == 0;
        accessor.UnpinChunkBasedSegmentHeader(entry);
        return res;
    }    

    public int IncCount(ChunkRandomAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false, out var entry);
        var res = ++header.Count;
        accessor.UnpinChunkBasedSegmentHeader(entry);
        return res;
    }    

    public int DecCount(ChunkRandomAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false, out var entry);
        var res = --header.Count;
        accessor.UnpinChunkBasedSegmentHeader(entry);
        return res;
    }    

    public void SetRootChunkId(ChunkRandomAccessor accessor, int rootChunkId)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false, out var entry);
        header.RootChunkId = rootChunkId;
        accessor.UnpinChunkBasedSegmentHeader(entry);
    }    

    public int GetRootChunkId(ChunkRandomAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false, out var entry);
        var res = header.RootChunkId;
        accessor.UnpinChunkBasedSegmentHeader(entry);
        return res;
    }    

    private NodeWrapper Root;
    private NodeWrapper LinkList;
    private NodeWrapper ReverseLinkList;
    public int Height;

    protected KeyValueItem GetFirst(ChunkRandomAccessor accessor) => LinkList.GetFirst(accessor);
    protected KeyValueItem GetLast(ChunkRandomAccessor accessor) => ReverseLinkList.GetLast(accessor);

    #endregion

    #region Public API
    
    public ChunkBasedSegment Segment => _segment;

    protected BTree(ChunkBasedSegment segment, ChunkRandomAccessor accessor, bool load)
    {
        Comparer = Comparer<TKey>.Default;
        _segment = segment;
        // ReSharper disable once VirtualMemberCallInConstructor
        _storage = GetStorage();
        _storage.Initialize(this, _segment);

        if (!load)
        {
            // We make sure the chunk 0 is reserved so we can consider any ChunkId == 0 as a "null pointer".
            // So any default constructed type declaring ChunkId fields can have this "null" by default.
            _segment.ReserveChunk(0);
        }
        else
        {
            Root = _storage.LoadNode(GetRootChunkId(accessor));
        }
    }

    public unsafe int Add(void* keyAddr, int value, ChunkRandomAccessor accessor) => Add(Unsafe.AsRef<TKey>(keyAddr), value, accessor);
    public unsafe bool Remove(void* keyAddr, out int value, ChunkRandomAccessor accessor) => Remove(Unsafe.AsRef<TKey>(keyAddr), out value, accessor);
    public unsafe bool TryGet(void* keyAddr, out int value, ChunkRandomAccessor accessor) => TryGet(Unsafe.AsRef<TKey>(keyAddr), out value, accessor);
    public unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ChunkRandomAccessor accessor) 
        => RemoveValue(Unsafe.AsRef<TKey>(keyAddr), elementId, value, accessor);
    public unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ChunkRandomAccessor accessor) => TryGetMultiple(Unsafe.AsRef<TKey>(keyAddr), accessor);

    public int Add(TKey key, int value, ChunkRandomAccessor accessor)
    {
        var args = new InsertArguments(key, value, Comparer, accessor);
        _access.EnterExclusiveAccess();
        try
        {
            AddOrUpdateCore(ref args);
            return args.ElementId;
        }
        finally
        {
            _storage.CommitChanges(accessor);
            _access.ExitExclusiveAccess();
        }
    }

    public bool Remove(TKey key, out int value, ChunkRandomAccessor accessor)
    {
        var args = new RemoveArguments(key, Comparer, accessor);
        _access.EnterExclusiveAccess();
        try
        {
            RemoveCore(ref args);
            value = args.Value;
            return args.Removed;
        }
        finally
        {
            _storage.CommitChanges(accessor);
            _access.ExitExclusiveAccess();
        }
    }

    public void CheckConsistency(ChunkRandomAccessor accessor)
    {
        // Recursive check from Root to leaf
        if (IsEmpty(accessor))
        {
            return;
        }

        _access.EnterSharedAccess();
        try
        {
            Root.CheckConsistency(default, NodeWrapper.CheckConsistencyParent.Root, Comparer, Height, accessor);

            // Check the linked link of leaves in forward
            NodeWrapper prev = default;
            var cur = LinkList;
            TKey prevValue = default;

            while (cur.IsValid)
            {
                if (cur != LinkList)
                {
                    Trace.Assert(prev.GetNext(accessor) == cur, " Prev.Next doesn't link to current");
                    Trace.Assert(cur.GetPrevious(accessor) == prev, "Cur.Previous doesn't link to previous");

                    Trace.Assert(Comparer.Compare(prevValue, cur.GetFirst(accessor).Key) < 0, $"Previous Node's first key '{prevValue}' should be less than current node's first key'{cur.GetFirst(accessor).Key}'.");
                }

                prevValue = cur.GetLast(accessor).Key;
                prev = cur;
                cur = cur.GetNext(accessor);
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
                    Trace.Assert(next.GetPrevious(accessor) == cur, " Next.Previous doesn't link to current");
                    Trace.Assert(cur.GetNext(accessor) == next, "Cur.Next doesn't link to next");

                    Trace.Assert(Comparer.Compare(nextValue, cur.GetLast(accessor).Key) > 0, $"Next Node's last key '{nextValue}' should be greater than current node's last key'{cur.GetLast(accessor).Key}'.");
                }

                nextValue = cur.GetFirst(accessor).Key;
                next = cur;
                cur = cur.GetPrevious(accessor);
            }
            Trace.Assert(next == LinkList, "Last Node of the reverse chain doesn't match LinkedList");
        }
        finally
        {
            _access.ExitSharedAccess();
        }
    }

    public int this[TKey key]
    {
        get
        {
            // TODO use a thread-local ChunkRandomAccessor to avoid creating a new one every time.
            var cra = this._segment.CreateChunkRandomAccessor(8);
            if (!TryGet(key, out var value, cra)) throw new KeyNotFoundException();
            return value;
        }
    }

    public bool TryGet(TKey key, out int value, ChunkRandomAccessor accessor)
    {
        value = default;
        _access.EnterSharedAccess();
        try
        {
            var leaf = FindLeaf(key, out var index, accessor);
            if (index >= 0) value = leaf.GetItem(index, accessor).Value;
            return index >= 0;
        }
        finally
        {
            _access.ExitSharedAccess();
        }
    }

    public bool RemoveValue(TKey key, int elementId, int value, ChunkRandomAccessor accessor)
    {
        if (TryGet(key, out var bufferId, accessor) == false)
        {
            return false;
        }
        _access.EnterExclusiveAccess();
        try
        {
            var res = _storage.RemoveFromBuffer(bufferId, elementId, value, accessor);
            if (res == -1) return false;

            // Remove the key if we no longer have values stored there
            if (res == 0)
            {
                var args = new RemoveArguments(key, Comparer, accessor);
                RemoveCore(ref args);

                if (args.Removed)
                {
                    _storage.DeleteBuffer(args.Value, accessor);
                }
            }
        }
        finally
        {
            _storage.CommitChanges(accessor);
            _access.ExitExclusiveAccess();
        }

        return true;
    }

    public VariableSizedBufferAccessor<int> TryGetMultiple(TKey key, ChunkRandomAccessor accessor)
    {
        if (TryGet(key, out var bufferId, accessor) == false)
        {
            return default;
        }
        return _storage.GetBufferReadOnlyAccessor(bufferId, accessor);
    }

    #endregion

    #region Private API

    protected internal NodeWrapper AllocNode(NodeStates states, ChunkRandomAccessor accessor)
    {
        var node = new NodeWrapper(_storage, _segment.AllocateChunk(true));
        _storage.InitializeNode(node, states, accessor);
        return node;
    }

    private void RemoveCore(ref RemoveArguments args)
    {
        var accessor = args.Accessor;
        if (IsEmpty(accessor))
        {
            return;
        }

        // optimize for removing items from beginning
        int order = args.Comparer.Compare(args.Key, GetFirst(accessor).Key);
        if (order < 0)
        {
            return;
        }

        if (order == 0 && (Root == LinkList || LinkList.GetCount(accessor) > LinkList.GetCapacity() / 2))
        {
            args.SetRemovedValue(LinkList.PopFirstInternal(accessor).Value);
            Debug.Assert(Root == LinkList || LinkList.GetIsHalfFull(accessor));
            DecCount(accessor);
            if (IsEmpty(accessor))
            {
                Root = LinkList = ReverseLinkList = default;
                SetRootChunkId(args.Accessor, 0);
                Height--;
            }
            return;
        }
        // optimize for removing items from end
        order = args.Comparer.Compare(args.Key, GetLast(accessor).Key);
        if (order > 0)
        {
            return;
        }

        if (order == 0 && (Root == ReverseLinkList || ReverseLinkList.GetCount(accessor) > ReverseLinkList.GetCapacity() / 2))
        {
            args.SetRemovedValue(ReverseLinkList.PopLastInternal(accessor).Value);
            Debug.Assert(Root == ReverseLinkList || ReverseLinkList.GetIsHalfFull(accessor));
            DecCount(accessor); // here count never becomes zero.
            return;
        }

        var nodeRelatives = new NodeRelatives();
        var merge = Root.Remove(ref args, ref nodeRelatives, accessor);

        if (args.Removed)
        {
            DecCount(accessor);
        }

        if (merge && Root.GetLength(accessor) == 0)
        {
            Root = Root.GetChild(-1, accessor); // left most child becomes root. (returns null for leafs)
            if (Root.IsValid == false)
            {
                LinkList = default;
                ReverseLinkList = default;
            }
            SetRootChunkId(args.Accessor, Root.IsValid ? Root.ChunkId : 0);
            Height--;
        }

        if (ReverseLinkList.IsValid && ReverseLinkList.GetPrevious(accessor).IsValid && ReverseLinkList.GetPrevious(accessor).GetNext(accessor).IsValid==false) // true if last leaf is merged.
        {
            ReverseLinkList = ReverseLinkList.GetPrevious(accessor);
        }
    }


    private void AddOrUpdateCore(ref InsertArguments args)
    {
        var accessor = args.Accessor;
        
        if (IsEmpty(accessor))
        {
            Root = AllocNode(NodeStates.IsLeaf, args.Accessor);
            SetRootChunkId(args.Accessor, Root.ChunkId);
            LinkList = Root;
            ReverseLinkList = LinkList;
            Height++;
        }

        int CreateBufferAndAddValue(ref InsertArguments iargs)
        {
            var bufferId = _storage.CreateBuffer(accessor);
            iargs.ElementId =_storage.Append(bufferId, iargs.GetValue(), accessor);
            return bufferId;
        }

        // append optimization: if item key is in order, this may add item in O(1) operation.
        int order = IsEmpty(accessor) ? 1 : args.Compare(args.Key, GetLast(accessor).Key);
        if (order > 0 && !ReverseLinkList.GetIsFull(accessor))
        {
            var value = AllowMultiple ? CreateBufferAndAddValue(ref args) : args.GetValue();
            ReverseLinkList.PushLast(new KeyValueItem(args.Key, value), accessor);
            IncCount(accessor);
            return;
        }
        else if (order == 0)
        {
            if (AllowMultiple)
            {
                args.ElementId = _storage.Append(GetLast(accessor).Value, args.GetValue(), accessor);
            }
            return;
        }

        // pre-append optimization: if item key is in order, this may add item in O(1) operation.
        order = args.Compare(args.Key, GetFirst(accessor).Key);
        if (order < 0 && !LinkList.GetIsFull(accessor))
        {
            var value = AllowMultiple ? CreateBufferAndAddValue(ref args) : args.GetValue();
            LinkList.PushFirst(new KeyValueItem(args.Key, value), accessor);
            IncCount(accessor);
            return;
        }
        else if (order == 0)
        {
            if (AllowMultiple)
            {
                args.ElementId = _storage.Append(GetFirst(accessor).Value, args.GetValue(), accessor);
            }
            return;
        }

        var nodeRelatives = new NodeRelatives();
        var rightSplit = Root.Insert(ref args, ref nodeRelatives, default, accessor);

        if (args.Added)
        {
            IncCount(accessor);
        }

        // if split occurred at root, make a new root and increase height.
        if (rightSplit != null)
        {
            var newRoot = AllocNode(NodeStates.None, args.Accessor);
            newRoot.SetLeft(Root, accessor);

            newRoot.Insert(0, rightSplit.Value, accessor);
            Root = newRoot;
            SetRootChunkId(args.Accessor, Root.ChunkId);
            Height++;
        }

        var next = ReverseLinkList.GetNext(accessor);
        if (next.IsValid)
        {
            ReverseLinkList = next;
        }
    }

    private NodeWrapper FindLeaf(TKey key, out int index, ChunkRandomAccessor accessor)
    {
        index = -1;
        if (IsEmpty(accessor))
        {
            return default;
        }

        var node = Root;
        while (!node.GetIsLeaf(accessor))
        {
            node = node.GetNearestChild(key, Comparer, accessor);
        }
        index = node.Find(key, Comparer, accessor);
        return node;
    }

    #endregion
}

#endregion