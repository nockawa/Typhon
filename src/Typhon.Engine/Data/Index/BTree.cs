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
    unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor);
    unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor accessor);
    unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor accessor);
    unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor accessor);
    unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ref ChunkAccessor accessor);
    void CheckConsistency(ref ChunkAccessor accessor);
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

    public ref struct InsertArguments
    {
        public InsertArguments(TKey key, int value, IComparer<TKey> comparer, ref ChunkAccessor accessor)
        {
            _value = value;
            _keyComparer = comparer ?? Comparer<TKey>.Default;
            Key = key;
            Added = false;
            ElementId = default;
            Accessor = ref accessor;
        }
        public readonly TKey Key;
        public bool Added { get; private set; }

        public int ElementId;

        public ref ChunkAccessor Accessor;

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
        public ref ChunkAccessor Accessor;

        /// <summary>
        /// result is set once when the value is found at leaf node.
        /// </summary>
        public int Value { get; private set; }

        /// <summary>
        /// true if item is removed.
        /// </summary>
        public bool Removed { get; private set; }

        public RemoveArguments(in TKey key, in IComparer<TKey> comparer, ref ChunkAccessor accessor)
        {
            Key = key;
            Comparer = comparer;

            Value = default;
            Removed = false;
            Accessor = ref accessor;
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
            ref ChunkAccessor accessor)
        {
            Debug.Assert(index >= -1 && index < parent.GetLength(ref accessor));

            // assign nearest ancestors between child and siblings.
            NodeWrapper leftAncestor, rightAncestor;
            int leftAncestorIndex, rightAncestorIndex;
            NodeWrapper leftSibling, rightSibling;
            bool hasTrueLeftSibling, hasTrueRightSibling;

            if (index == -1) // if child is left most, use left cousin as left sibling.
            {
                leftAncestor = parentRelatives.LeftAncestor;
                leftAncestorIndex = parentRelatives.LeftAncestorIndex;
                leftSibling = parentRelatives.LeftSibling.IsValid ? parentRelatives.LeftSibling.GetLastChild(ref accessor) : default;
                hasTrueLeftSibling = false;

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                rightSibling = parent.GetChild(rightAncestorIndex, ref accessor);
                hasTrueRightSibling = true;
            }
            else if (index == parent.GetLength(ref accessor) - 1) // if child is right most, use right cousin as right sibling.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                leftSibling = parent.GetChild(leftAncestorIndex - 1, ref accessor);
                hasTrueLeftSibling = true;

                rightAncestor = parentRelatives.RightAncestor;
                rightAncestorIndex = parentRelatives.RightAncestorIndex;
                rightSibling = parentRelatives.RightSibling.IsValid ? parentRelatives.RightSibling.GetFirstChild(ref accessor) : default;
                hasTrueRightSibling = false;
            }
            else // child is not right most nor left most.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                leftSibling = parent.GetChild(leftAncestorIndex - 1, ref accessor);
                hasTrueLeftSibling = true;

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                rightSibling = parent.GetChild(rightAncestorIndex, ref accessor);
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

    public bool IsEmpty(ref ChunkAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false);
        var res = header.Count == 0;
        return res;
    }

    public int IncCount(ref ChunkAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false);
        var res = ++header.Count;
        return res;
    }

    public int DecCount(ref ChunkAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false);
        var res = --header.Count;
        return res;
    }

    public void SetRootChunkId(ref ChunkAccessor accessor, int rootChunkId)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false);
        header.RootChunkId = rootChunkId;
    }

    public int GetRootChunkId(ref ChunkAccessor accessor)
    {
        ref var header = ref accessor.GetChunkBasedSegmentHeader<BTreeHeader>(BTreeHeader.Offset, false);
        var res = header.RootChunkId;
        return res;
    }    

    private NodeWrapper Root;
    private NodeWrapper LinkList;
    private NodeWrapper ReverseLinkList;
    public int Height;

    protected KeyValueItem GetFirst(ref ChunkAccessor accessor) => LinkList.GetFirst(ref accessor);
    protected KeyValueItem GetLast(ref ChunkAccessor accessor) => ReverseLinkList.GetLast(ref accessor);

    #endregion

    #region Public API
    
    public ChunkBasedSegment Segment => _segment;

    protected BTree(ChunkBasedSegment segment, bool load)
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
            var ca = segment.CreateChunkAccessor();
            Root = _storage.LoadNode(GetRootChunkId(ref ca));
            ca.Dispose();
        }
    }

    public unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor) => Add(Unsafe.AsRef<TKey>(keyAddr), value, ref accessor);
    public unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor accessor) => Remove(Unsafe.AsRef<TKey>(keyAddr), out value, ref accessor);
    public unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor accessor) => TryGet(Unsafe.AsRef<TKey>(keyAddr), ref accessor);
    public unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor accessor) 
        => RemoveValue(Unsafe.AsRef<TKey>(keyAddr), elementId, value, ref accessor);
    public unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ref ChunkAccessor accessor)
        => TryGetMultiple(Unsafe.AsRef<TKey>(keyAddr), ref accessor);

    public int Add(TKey key, int value, ref ChunkAccessor accessor)
    {
        Activity activity = null;
        if (TelemetryConfig.BTreeActive)
        {
            activity = TyphonActivitySource.StartActivity("BTree.Insert");
        }

        var args = new InsertArguments(key, value, Comparer, ref accessor);
        _access.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            AddOrUpdateCore(ref args);
            activity?.SetTag(TyphonSpanAttributes.IndexOperation, "insert");
            return args.ElementId;
        }
        finally
        {
            _storage.CommitChanges(ref accessor);
            _access.ExitExclusiveAccess();
            activity?.Dispose();
        }
    }

    public bool Remove(TKey key, out int value, ref ChunkAccessor accessor)
    {
        Activity activity = null;
        if (TelemetryConfig.BTreeActive)
        {
            activity = TyphonActivitySource.StartActivity("BTree.Delete");
        }

        var args = new RemoveArguments(key, Comparer, ref accessor);
        _access.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            RemoveCore(ref args);
            value = args.Value;
            activity?.SetTag(TyphonSpanAttributes.IndexOperation, "delete");
            return args.Removed;
        }
        finally
        {
            _storage.CommitChanges(ref accessor);
            _access.ExitExclusiveAccess();
            activity?.Dispose();
        }
    }

    public void CheckConsistency(ref ChunkAccessor accessor)
    {
        // Recursive check from Root to leaf
        if (IsEmpty(ref accessor))
        {
            return;
        }

        _access.EnterSharedAccess(ref WaitContext.Null);
        try
        {
            Root.CheckConsistency(default, NodeWrapper.CheckConsistencyParent.Root, Comparer, Height, ref accessor);

            // Check the linked link of leaves in forward
            NodeWrapper prev = default;
            var cur = LinkList;
            TKey prevValue = default;

            while (cur.IsValid)
            {
                if (cur != LinkList)
                {
                    Trace.Assert(prev.GetNext(ref accessor) == cur, " Prev.Next doesn't link to current");
                    Trace.Assert(cur.GetPrevious(ref accessor) == prev, "Cur.Previous doesn't link to previous");

                    Trace.Assert(Comparer.Compare(prevValue, cur.GetFirst(ref accessor).Key) < 0, 
                        $"Previous Node's first key '{prevValue}' should be less than current node's first key'{cur.GetFirst(ref accessor).Key}'.");
                }

                prevValue = cur.GetLast(ref accessor).Key;
                prev = cur;
                cur = cur.GetNext(ref accessor);
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
                    Trace.Assert(next.GetPrevious(ref accessor) == cur, " Next.Previous doesn't link to current");
                    Trace.Assert(cur.GetNext(ref accessor) == next, "Cur.Next doesn't link to next");

                    Trace.Assert(Comparer.Compare(nextValue, cur.GetLast(ref accessor).Key) > 0, 
                        $"Next Node's last key '{nextValue}' should be greater than current node's last key'{cur.GetLast(ref accessor).Key}'.");
                }

                nextValue = cur.GetFirst(ref accessor).Key;
                next = cur;
                cur = cur.GetPrevious(ref accessor);
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
            var ca = this._segment.CreateChunkAccessor();
            try
            {
                var result = TryGet(key, ref ca);
                if (result.IsFailure)
                {
                    throw new KeyNotFoundException();
                }

                return result.Value;
            }
            finally
            {
                ca.Dispose();
            }
        }
    }

    public Result<int, BTreeLookupStatus> TryGet(TKey key, ref ChunkAccessor accessor)
    {
        _access.EnterSharedAccess(ref WaitContext.Null);
        try
        {
            var leaf = FindLeaf(key, out var index, ref accessor);
            if (index >= 0)
            {
                return new Result<int, BTreeLookupStatus>(leaf.GetItem(index, ref accessor).Value);
            }

            return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
        }
        finally
        {
            _access.ExitSharedAccess();
        }
    }

    public bool RemoveValue(TKey key, int elementId, int value, ref ChunkAccessor accessor)
    {
        var result = TryGet(key, ref accessor);
        if (result.IsFailure)
        {
            return false;
        }
        var bufferId = result.Value;

        Activity activity = null;
        if (TelemetryConfig.BTreeActive)
        {
            activity = TyphonActivitySource.StartActivity("BTree.Delete");
            activity?.SetTag(TyphonSpanAttributes.IndexOperation, "delete");
        }

        _access.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            var res = _storage.RemoveFromBuffer(bufferId, elementId, value, ref accessor);
            if (res == -1)
            {
                return false;
            }

            // Remove the key if we no longer have values stored there
            if (res == 0)
            {
                var args = new RemoveArguments(key, Comparer, ref accessor);
                RemoveCore(ref args);

                if (args.Removed)
                {
                    _storage.DeleteBuffer(args.Value, ref accessor);
                }
            }
        }
        finally
        {
            _storage.CommitChanges(ref accessor);
            _access.ExitExclusiveAccess();
            activity?.Dispose();
        }

        return true;
    }

    public VariableSizedBufferAccessor<int> TryGetMultiple(TKey key, ref ChunkAccessor accessor)
    {
        var result = TryGet(key, ref accessor);
        if (result.IsFailure)
        {
            return default;
        }
        return _storage.GetBufferReadOnlyAccessor(result.Value, ref accessor);
    }

    #endregion

    #region Private API

    protected internal NodeWrapper AllocNode(NodeStates states, ref ChunkAccessor accessor)
    {
        var node = new NodeWrapper(_storage, _segment.AllocateChunk(true));
        _storage.InitializeNode(node, states, ref accessor);
        return node;
    }

    private void RemoveCore(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;
        if (IsEmpty(ref accessor))
        {
            return;
        }

        // optimize for removing items from beginning
        int order = args.Comparer.Compare(args.Key, GetFirst(ref accessor).Key);
        if (order < 0)
        {
            return;
        }

        if (order == 0 && (Root == LinkList || LinkList.GetCount(ref accessor) > LinkList.GetCapacity() / 2))
        {
            args.SetRemovedValue(LinkList.PopFirstInternal(ref accessor).Value);
            Debug.Assert(Root == LinkList || LinkList.GetIsHalfFull(ref accessor));
            DecCount(ref accessor);
            if (IsEmpty(ref accessor))
            {
                Root = LinkList = ReverseLinkList = default;
                SetRootChunkId(ref args.Accessor, 0);
                Height--;
            }
            return;
        }
        // optimize for removing items from end
        order = args.Comparer.Compare(args.Key, GetLast(ref accessor).Key);
        if (order > 0)
        {
            return;
        }

        if (order == 0 && (Root == ReverseLinkList || ReverseLinkList.GetCount(ref accessor) > ReverseLinkList.GetCapacity() / 2))
        {
            args.SetRemovedValue(ReverseLinkList.PopLastInternal(ref accessor).Value);
            Debug.Assert(Root == ReverseLinkList || ReverseLinkList.GetIsHalfFull(ref accessor));
            DecCount(ref accessor); // here count never becomes zero.
            return;
        }

        var nodeRelatives = new NodeRelatives();
        var merge = Root.Remove(ref args, ref nodeRelatives, ref accessor);

        if (args.Removed)
        {
            DecCount(ref accessor);
        }

        if (merge && Root.GetLength(ref accessor) == 0)
        {
            Root = Root.GetChild(-1, ref accessor); // left most child becomes root. (returns null for leafs)
            if (Root.IsValid == false)
            {
                LinkList = default;
                ReverseLinkList = default;
            }
            SetRootChunkId(ref args.Accessor, Root.IsValid ? Root.ChunkId : 0);
            Height--;
        }

        if (ReverseLinkList.IsValid && ReverseLinkList.GetPrevious(ref accessor).IsValid && ReverseLinkList.GetPrevious(ref accessor).GetNext(ref accessor).IsValid==false) // true if last leaf is merged.
        {
            ReverseLinkList = ReverseLinkList.GetPrevious(ref accessor);
        }
    }

    private void AddOrUpdateCore(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;
        
        if (IsEmpty(ref accessor))
        {
            Root = AllocNode(NodeStates.IsLeaf, ref accessor);
            SetRootChunkId(ref accessor, Root.ChunkId);
            LinkList = Root;
            ReverseLinkList = LinkList;
            Height++;
        }

        // append optimization: if item key is in order, this may add item in O(1) operation.
        int order = IsEmpty(ref accessor) ? 1 : args.Compare(args.Key, GetLast(ref accessor).Key);
        if (order > 0 && !ReverseLinkList.GetIsFull(ref accessor))
        {
            int value;
            if (AllowMultiple)
            {
                var bufferId = _storage.CreateBuffer(ref accessor);
                args.ElementId = _storage.Append(bufferId, args.GetValue(), ref accessor);
                value = bufferId;
            }
            else
            {
                value = args.GetValue();
            }
            ReverseLinkList.PushLast(new KeyValueItem(args.Key, value), ref accessor);
            IncCount(ref accessor);
            return;
        }

        if (order == 0)
        {
            if (AllowMultiple)
            {
                args.ElementId = _storage.Append(GetLast(ref accessor).Value, args.GetValue(), ref accessor);
            }
            return;
        }

        // pre-append optimization: if item key is in order, this may add item in O(1) operation.
        order = args.Compare(args.Key, GetFirst(ref accessor).Key);
        if (order < 0 && !LinkList.GetIsFull(ref accessor))
        {
            int value;
            if (AllowMultiple)
            {
                var bufferId = _storage.CreateBuffer(ref accessor);
                args.ElementId = _storage.Append(bufferId, args.GetValue(), ref accessor);
                value = bufferId;
            }
            else
            {
                value = args.GetValue();
            }
            LinkList.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
            IncCount(ref accessor);
            return;
        }

        if (order == 0)
        {
            if (AllowMultiple)
            {
                args.ElementId = _storage.Append(GetFirst(ref accessor).Value, args.GetValue(), ref accessor);
            }
            return;
        }

        var nodeRelatives = new NodeRelatives();
        var rightSplit = Root.Insert(ref args, ref nodeRelatives, default, ref accessor);

        if (args.Added)
        {
            IncCount(ref accessor);
        }

        // if split occurred at root, make a new root and increase height.
        if (rightSplit != null)
        {
            var newRoot = AllocNode(NodeStates.None, ref accessor);
            newRoot.SetLeft(Root, ref accessor);

            newRoot.Insert(0, rightSplit.Value, ref accessor);
            Root = newRoot;
            SetRootChunkId(ref accessor, Root.ChunkId);
            Height++;
        }

        var next = ReverseLinkList.GetNext(ref accessor);
        if (next.IsValid)
        {
            ReverseLinkList = next;
        }
    }

    private NodeWrapper FindLeaf(TKey key, out int index, ref ChunkAccessor accessor)
    {
        index = -1;
        if (IsEmpty(ref accessor))
        {
            return default;
        }

        var node = Root;
        while (!node.GetIsLeaf(ref accessor))
        {
            node = node.GetNearestChild(key, Comparer, ref accessor);
        }
        index = node.Find(key, Comparer, ref accessor);
        return node;
    }

    #endregion
}

#endregion