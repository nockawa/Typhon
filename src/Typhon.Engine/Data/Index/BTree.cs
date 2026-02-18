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

/// <summary>
/// Header of the BTree directory stored at the start of chunk 0.
/// Tracks how many BTree entries are registered in this segment.
/// Directory chunks are zeroed on first reservation (<see cref="ChunkBasedSegment.ReserveChunk(int,bool)"/>),
/// so <see cref="EntryCount"/> == 0 reliably means "empty directory".
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct BTreeDirectoryHeader
{
    unsafe public static readonly int Size = sizeof(BTreeDirectoryHeader);

    public ushort EntryCount;
}

/// <summary>
/// One entry in the BTree directory (chunk 0). Each BTree on the segment gets a unique entry,
/// keyed by <see cref="StableId"/> (FieldId for secondary indexes, -1 for PK, 0 for standalone).
/// </summary>
/// <remarks>12 bytes: short StableId + short Reserved + int RootChunkId + int Count.</remarks>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct BTreeDirectoryEntry
{
    unsafe public static readonly int Size = sizeof(BTreeDirectoryEntry);

    /// <summary>Stable key: -1 for PK, Field.FieldId for secondary indexes, 0 for standalone/test BTrees.</summary>
    public short StableId;
    public short Reserved;
    public int RootChunkId;
    public int Count;
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
        public static void Create(NodeWrapper child, int index, NodeWrapper parent, ref NodeRelatives parentRelatives, out NodeRelatives res, ref ChunkAccessor accessor)
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

    // Per-instance count and root tracking used for ALL runtime operations.
    // Multiple BTrees can share the same ChunkBasedSegment (e.g., PK index and secondary
    // indexes share DefaultIndexSegment). Runtime code MUST use these per-instance fields
    // instead of reading from a single shared offset, which would cause cross-BTree corruption.
    // Each BTree has a unique entry in the chunk 0 directory, keyed by stableId.
    private int _count;

    // Cached location of this BTree's entry in the chunk 0 directory.
    // Computed once at construction, used by SyncHeader for O(1) writes.
    private int _dirChunkId;
    private int _dirEntryOffset;

    /// <summary>Number of preallocated directory chunks (0-3). Provides up to 20 index slots for 64-byte chunks.</summary>
    internal const int DirectoryChunkCount = 4;

    /// <summary>Hard cap on secondary indexes per segment (could be raised later).</summary>
    internal const int MaxDirectoryEntries = 20;

    public bool IsEmpty() => _count == 0;

    public int IncCount() => ++_count;

    public int DecCount() => --_count;

    /// <summary>
    /// Writes <c>_count</c> and <c>Root.ChunkId</c> to this BTree's directory entry in chunk 0 (or chained chunks 1-3).
    /// Each BTree on a shared segment has a unique entry so they don't collide.
    /// </summary>
    private unsafe void SyncHeader(ref ChunkAccessor accessor)
    {
        var addr = accessor.GetChunkAddress(_dirChunkId, true);
        ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(addr + _dirEntryOffset);
        entry.Count = _count;
        entry.RootChunkId = Root.ChunkId;
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

    protected BTree(ChunkBasedSegment segment, bool load, short stableId = 0)
    {
        Comparer = Comparer<TKey>.Default;
        _segment = segment;

        // ReSharper disable once VirtualMemberCallInConstructor
        _storage = GetStorage();
        _storage.Initialize(this, _segment);

        // Both create and load paths need a ChunkAccessor, which requires an active epoch scope.
        // The BTree constructor may be called during DatabaseEngine init (outside any epoch scope),
        // so we enter one here. EpochGuard supports nesting, so this is a no-op if already in scope.
        using var guard = EpochGuard.Enter(_segment.Manager.EpochManager);

        if (!load)
        {
            // Reserve chunks 0-3 for the BTree directory overflow entries.
            // Chunk 0 is already reserved by CBS but call to clear its content anyway.
            // clearContent: true ensures first reservation zeros the chunk; subsequent BTrees are no-ops.
            for (int i = 0; i < DirectoryChunkCount; i++)
            {
                _segment.ReserveChunk(i, true);
            }

            // Register this BTree in the directory (append a new entry, cache its location)
            var accessor = _segment.CreateChunkAccessor();
            try
            {
                RegisterInDirectory(stableId, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else
        {
            // Load path: find our entry in the directory by stableId, reconstruct per-instance state.
            var accessor = _segment.CreateChunkAccessor();
            try
            {
                FindInDirectory(stableId, ref accessor);

                if (_count > 0)
                {
                    // Traverse the leftmost path to find Height and LinkList (leftmost leaf)
                    Height = 1;
                    var node = Root;
                    while (!node.GetIsLeaf(ref accessor))
                    {
                        node = node.GetLeft(ref accessor);
                        Height++;
                    }
                    LinkList = node;

                    // Traverse the rightmost path to find ReverseLinkList (rightmost leaf)
                    node = Root;
                    while (!node.GetIsLeaf(ref accessor))
                    {
                        node = node.GetLastChild(ref accessor);
                    }
                    ReverseLinkList = node;
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Appends a new entry to the chunk 0 directory for this BTree.
    /// Sets <c>_dirChunkId</c> and <c>_dirEntryOffset</c> for subsequent <see cref="SyncHeader"/> calls.
    /// </summary>
    private unsafe void RegisterInDirectory(short stableId, ref ChunkAccessor accessor)
    {
        var chunk0Addr = accessor.GetChunkAddress(0, true);
        ref var header = ref Unsafe.AsRef<BTreeDirectoryHeader>(chunk0Addr);

        // Directory chunks are zeroed on first reservation, so EntryCount is reliably 0 for a fresh segment.
        int entryIndex = header.EntryCount;
        if (entryIndex >= MaxDirectoryEntries)
        {
            throw new InvalidOperationException($"Maximum number of BTree indexes per segment exceeded ({MaxDirectoryEntries})");
        }

        (_dirChunkId, _dirEntryOffset) = ComputeEntryLocation(entryIndex, _segment.Stride);

        var entryChunkAddr = accessor.GetChunkAddress(_dirChunkId, true);
        ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(entryChunkAddr + _dirEntryOffset);
        entry.StableId = stableId;
        entry.Reserved = 0;
        entry.RootChunkId = 0;
        entry.Count = 0;

        header.EntryCount = (ushort)(entryIndex + 1);
    }

    /// <summary>
    /// Scans the chunk 0 directory for the entry matching <paramref name="stableId"/>.
    /// Populates <c>_dirChunkId</c>, <c>_dirEntryOffset</c>, <c>_count</c>, and <c>Root</c>.
    /// </summary>
    private unsafe void FindInDirectory(short stableId, ref ChunkAccessor accessor)
    {
        var chunk0Addr = accessor.GetChunkAddress(0, false);
        ref var header = ref Unsafe.AsRef<BTreeDirectoryHeader>(chunk0Addr);

        int totalEntries = header.EntryCount;
        int stride = _segment.Stride;

        for (var i = 0; i < totalEntries; i++)
        {
            var (chunkId, offset) = ComputeEntryLocation(i, stride);
            var entryChunkAddr = accessor.GetChunkAddress(chunkId, false);
            ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(entryChunkAddr + offset);

            if (entry.StableId == stableId)
            {
                _dirChunkId = chunkId;
                _dirEntryOffset = offset;
                _count = entry.Count;

                var rootChunkId = entry.RootChunkId;
                if (_count > 0 && rootChunkId > 0)
                {
                    Root = new NodeWrapper(_storage, rootChunkId);
                }
                return;
            }
        }

        throw new InvalidOperationException($"BTree with stableId {stableId} not found in directory (entries: {totalEntries})");
    }

    /// <summary>
    /// Computes which directory chunk and byte offset a given entry index maps to.
    /// Chunk 0 has a 4-byte header; chunks 1-3 are pure entry storage.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int chunkId, int offsetInChunk) ComputeEntryLocation(int entryIndex, int stride)
    {
        int entriesInChunk0 = (stride - BTreeDirectoryHeader.Size) / BTreeDirectoryEntry.Size;
        if (entryIndex < entriesInChunk0)
        {
            return (0, BTreeDirectoryHeader.Size + entryIndex * BTreeDirectoryEntry.Size);
        }

        int entriesPerChunk = stride / BTreeDirectoryEntry.Size;
        int adjusted = entryIndex - entriesInChunk0;
        return (1 + adjusted / entriesPerChunk, (adjusted % entriesPerChunk) * BTreeDirectoryEntry.Size);
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
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout);
        if (!_access.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("BTree/Insert", TimeoutOptions.Current.BTreeLockTimeout);
        }
        try
        {
            AddOrUpdateCore(ref args);
            SyncHeader(ref accessor);
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
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout);
        if (!_access.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("BTree/Delete", TimeoutOptions.Current.BTreeLockTimeout);
        }
        try
        {
            RemoveCore(ref args);
            SyncHeader(ref accessor);
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
        if (IsEmpty())
        {
            return;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout);
        if (!_access.EnterSharedAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("BTree/CheckConsistency", TimeoutOptions.Current.BTreeLockTimeout);
        }
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
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout);
        if (!_access.EnterSharedAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("BTree/TryGet", TimeoutOptions.Current.BTreeLockTimeout);
        }
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

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout);
        if (!_access.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("BTree/DeleteValue", TimeoutOptions.Current.BTreeLockTimeout);
        }
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

                SyncHeader(ref accessor);
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
        if (IsEmpty())
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
            DecCount();
            if (IsEmpty())
            {
                Root = LinkList = ReverseLinkList = default;
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
            DecCount(); // here count never becomes zero.
            return;
        }

        var nodeRelatives = new NodeRelatives();
        var merge = Root.Remove(ref args, ref nodeRelatives, ref accessor);

        if (args.Removed)
        {
            DecCount();
        }

        if (merge && Root.GetLength(ref accessor) == 0)
        {
            Root = Root.GetChild(-1, ref accessor); // left most child becomes root. (returns null for leafs)
            if (Root.IsValid == false)
            {
                LinkList = default;
                ReverseLinkList = default;
            }
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

        if (IsEmpty())
        {
            Root = AllocNode(NodeStates.IsLeaf, ref accessor);
            LinkList = Root;
            ReverseLinkList = LinkList;
            Height++;
        }

        // append optimization: if item key is in order, this may add item in O(1) operation.
        int order = IsEmpty() ? 1 : args.Compare(args.Key, GetLast(ref accessor).Key);
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
            IncCount();
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
            IncCount();
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
            IncCount();
        }

        // if split occurred at root, make a new root and increase height.
        if (rightSplit != null)
        {
            var newRoot = AllocNode(NodeStates.None, ref accessor);
            newRoot.SetLeft(Root, ref accessor);

            newRoot.Insert(0, rightSplit.Value, ref accessor);
            Root = newRoot;
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
        if (IsEmpty())
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