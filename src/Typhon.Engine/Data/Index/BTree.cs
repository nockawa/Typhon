// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

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
    None     = 0x00,
    IsLeaf   = 0x02
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
    int EntryCount { get; }
    unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor);
    unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor, out int bufferRootId);
    unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor accessor);
    unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor accessor);
    unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor accessor, bool preserveEmptyBuffer = false);
    unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ref ChunkAccessor accessor);

    /// <summary>
    /// Compound move: atomically removes <paramref name="value"/> from <paramref name="oldKeyAddr"/>
    /// and inserts it under <paramref name="newKeyAddr"/>. For unique indexes (!AllowMultiple).
    /// </summary>
    /// <returns>True if the old key was found and moved; false if old key not found.</returns>
    unsafe bool Move(void* oldKeyAddr, void* newKeyAddr, int value, ref ChunkAccessor accessor);

    /// <summary>
    /// Compound move for multi-value indexes (AllowMultiple): removes <paramref name="elementId"/>/<paramref name="value"/>
    /// from <paramref name="oldKeyAddr"/>'s buffer and appends <paramref name="value"/> under <paramref name="newKeyAddr"/>.
    /// Returns the new element ID and both HEAD buffer IDs for inline TAIL tracking.
    /// </summary>
    unsafe int MoveValue(void* oldKeyAddr, void* newKeyAddr, int elementId, int value, ref ChunkAccessor accessor, out int oldHeadBufferId, 
        out int newHeadBufferId, bool preserveEmptyBuffer = false);

    void CheckConsistency(ref ChunkAccessor accessor);
}

public abstract partial class BTree<TKey> : IBTree where TKey : unmanaged 
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
        public int BufferRootId;

        public ref ChunkAccessor Accessor;

        private readonly int _value;
        private readonly IComparer<TKey> _keyComparer;

        public int GetValue()
        {
            Added = true;
            return _value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TKey left, TKey right)
        {
            // typeof(TKey) is a JIT intrinsic for value types — dead branches are eliminated at JIT time,
            // turning this into a direct comparison instead of an IComparer interface dispatch.
            if (typeof(TKey) == typeof(long))
            {
                var l = (long)(object)left;
                var r = (long)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(int))
            {
                var l = (int)(object)left;
                var r = (int)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(short))
            {
                var l = (short)(object)left;
                var r = (short)(object)right;
                return l.CompareTo(r);
            }
            return _keyComparer.Compare(left, right);
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TKey left, TKey right)
        {
            if (typeof(TKey) == typeof(long))
            {
                var l = (long)(object)left;
                var r = (long)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(int))
            {
                var l = (int)(object)left;
                var r = (int)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(short))
            {
                var l = (short)(object)left;
                var r = (short)(object)right;
                return l.CompareTo(r);
            }
            return Comparer.Compare(left, right);
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
    /// Siblings are lazily resolved to avoid loading chunks that are only needed during
    /// split/merge/spill/borrow operations (~5% of inserts).
    /// </summary>
    public struct NodeRelatives
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

        // Context for lazy cousin edge resolution (only set for edge children)
        private readonly NodeWrapper _cousinLeftSource;
        private readonly NodeWrapper _cousinRightSource;

        // Lazy-cached siblings
        private NodeWrapper _leftSibling;
        private NodeWrapper _rightSibling;
        private bool _leftLoaded;
        private bool _rightLoaded;

        private NodeRelatives(NodeWrapper leftAncestor, int leftAncestorIndex, bool hasTrueLeftSibling, NodeWrapper rightAncestor, int rightAncestorIndex, 
            bool hasTrueRightSibling, NodeWrapper cousinLeftSource, NodeWrapper cousinRightSource)
        {
            LeftAncestor = leftAncestor;
            LeftAncestorIndex = leftAncestorIndex;
            HasTrueLeftSibling = hasTrueLeftSibling;

            RightAncestor = rightAncestor;
            RightAncestorIndex = rightAncestorIndex;
            HasTrueRightSibling = hasTrueRightSibling;

            _cousinLeftSource = cousinLeftSource;
            _cousinRightSource = cousinRightSource;
            _leftSibling = default;
            _rightSibling = default;
            _leftLoaded = false;
            _rightLoaded = false;
        }

        /// <summary>
        /// Lazily resolves and returns the left sibling. Caches result on first access.
        /// For true siblings, reads from the ancestor node. For cousin edges, traverses
        /// the parent's left sibling to find the rightmost child.
        /// </summary>
        public NodeWrapper GetLeftSibling(ref ChunkAccessor accessor)
        {
            if (!_leftLoaded)
            {
                _leftLoaded = true;
                _leftSibling = HasTrueLeftSibling ? 
                    LeftAncestor.GetChild(LeftAncestorIndex - 1, ref accessor) : _cousinLeftSource.IsValid ? _cousinLeftSource.GetLastChild(ref accessor) : default;
            }
            return _leftSibling;
        }

        /// <summary>
        /// Lazily resolves and returns the right sibling. Caches result on first access.
        /// For true siblings, reads from the ancestor node. For cousin edges, traverses
        /// the parent's right sibling to find the leftmost child.
        /// </summary>
        public NodeWrapper GetRightSibling(ref ChunkAccessor accessor)
        {
            if (!_rightLoaded)
            {
                _rightLoaded = true;
                _rightSibling = HasTrueRightSibling ?
                    RightAncestor.GetChild(RightAncestorIndex, ref accessor) : _cousinRightSource.IsValid ? _cousinRightSource.GetFirstChild(ref accessor) : default;
            }
            return _rightSibling;
        }

        /// <summary>
        /// creates new relatives for child node.
        /// Ancestor fields are set eagerly (no chunk reads — just copies).
        /// Sibling fields are lazily resolved on first access via GetLeftSibling/GetRightSibling.
        /// </summary>
        public static void Create(NodeWrapper child, int index, NodeWrapper parent, int parentCount, ref NodeRelatives parentRelatives, out NodeRelatives res, ref ChunkAccessor accessor)
        {

            // assign nearest ancestors between child and siblings.
            NodeWrapper leftAncestor, rightAncestor;
            int leftAncestorIndex, rightAncestorIndex;
            bool hasTrueLeftSibling, hasTrueRightSibling;
            NodeWrapper cousinLeftSource = default, cousinRightSource = default;

            if (index == -1) // if child is left most, use left cousin as left sibling.
            {
                leftAncestor = parentRelatives.LeftAncestor;
                leftAncestorIndex = parentRelatives.LeftAncestorIndex;
                hasTrueLeftSibling = false;
                cousinLeftSource = parentRelatives.GetLeftSibling(ref accessor);

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                hasTrueRightSibling = true;
            }
            else if (index == parentCount - 1) // if child is right most, use right cousin as right sibling.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                hasTrueLeftSibling = true;

                rightAncestor = parentRelatives.RightAncestor;
                rightAncestorIndex = parentRelatives.RightAncestorIndex;
                hasTrueRightSibling = false;
                cousinRightSource = parentRelatives.GetRightSibling(ref accessor);
            }
            else // child is not right most nor left most.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                hasTrueLeftSibling = true;

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                hasTrueRightSibling = true;
            }

            res = new NodeRelatives(leftAncestor, leftAncestorIndex, hasTrueLeftSibling, rightAncestor, rightAncestorIndex, hasTrueRightSibling, 
                cousinLeftSource, cousinRightSource);
        }
    }

    #region Private data

    public abstract bool AllowMultiple { get; }
    protected abstract BaseNodeStorage GetStorage();
    protected IComparer<TKey> Comparer;

    private readonly ChunkBasedSegment _segment;
    private readonly BaseNodeStorage _storage;
    // Lightweight mutex protecting DeferredNodeList, which is accessed by concurrent merge operations.
    private SpinLock _deferredLock = new(enableThreadOwnerTracking: false);

    // Per-instance count and root tracking used for ALL runtime operations.
    // Multiple BTrees can share the same ChunkBasedSegment (e.g., PK index and secondary
    // indexes share DefaultIndexSegment). Runtime code MUST use these per-instance fields
    // instead of reading from a single shared offset, which would cause cross-BTree corruption.
    // Each BTree has a unique entry in the chunk 0 directory, keyed by stableId.
    private int _count;

    // Cached last key for append fast-path: avoids reading ReverseLinkList chunk on sequential inserts.
    // TKey is unmanaged (value type), so no heap allocation.
    private TKey _cachedLastKey;
    private bool _hasCachedLastKey;

    // Epoch-deferred deallocation: nodes marked obsolete during merges are freed once all readers have exited.
    // Protected by _deferredLock for thread safety under concurrent merge operations.
    private DeferredNodeList _deferredNodes;

    // OLC diagnostics counters (always-on, only incremented on slow paths)
    internal long _optimisticRestarts;
    internal long _pessimisticFallbacks;
    internal long _writeLockFailures;
    internal long _splitCount;
    internal long _mergeCount;
    internal long _moveRightCount;

    internal const int MaxTreeDepth = 32;
    internal const int MaxOptimisticRestarts = 3;

    #region OLC Path Buffers

    /// <summary>Stack-allocated int buffer for tree traversal path (max 32 levels).</summary>
    [InlineArray(MaxTreeDepth)]
    internal struct PathIntBuffer
    {
        private int _element0;
    }

    /// <summary>Stack-allocated NodeWrapper buffer for tree traversal path.</summary>
    [InlineArray(MaxTreeDepth)]
    internal struct PathNodesBuffer
    {
        private NodeWrapper _element0;
    }

    /// <summary>Stack-allocated NodeRelatives buffer for tree traversal path.</summary>
    [InlineArray(MaxTreeDepth)]
    internal struct PathRelativesBuffer
    {
        private NodeRelatives _element0;
    }

    /// <summary>
    /// Stack-allocated traversal context for a single BTree operation.
    /// Replaces instance-level path arrays that were protected by the whole-tree lock.
    /// ~4KB on the stack per mutation. PathVersions adds 128 bytes (32 x 4) for OLC validation.
    /// </summary>
    internal ref struct MutationContext
    {
        public PathRelativesBuffer PathRelatives;
        public PathNodesBuffer PathNodes;
        public PathIntBuffer PathChildIndices;
        public PathIntBuffer PathVersions;     // OLC version snapshots for validation
        public int Depth;
    }

    #endregion

    #region Epoch-Deferred Deallocation

    /// <summary>
    /// Tracks nodes marked obsolete during merges for epoch-deferred deallocation.
    /// Inline buffer of 8 entries covers typical case; overflows to List for cascading merges.
    /// All access must be under _deferredLock (via DeferredAdd / DeferredReclaim).
    /// </summary>
    internal struct DeferredNodeList
    {
        private struct Entry
        {
            public int ChunkId;
            public long RetireEpoch;
        }

        [InlineArray(8)]
        private struct EntryBuffer
        {
            private Entry _element0;
        }

        private EntryBuffer _entries;
        private int _inlineCount;
        private List<Entry> _overflow;

        /// <summary>Record a chunk for deferred deallocation at the given epoch.</summary>
        public void Add(int chunkId, long retireEpoch)
        {
            if (_inlineCount < 8)
            {
                _entries[_inlineCount] = new Entry { ChunkId = chunkId, RetireEpoch = retireEpoch };
                _inlineCount++;
            }
            else
            {
                _overflow ??= new List<Entry>();
                _overflow.Add(new Entry { ChunkId = chunkId, RetireEpoch = retireEpoch });
            }
        }

        /// <summary>
        /// Free nodes whose retire epoch is strictly less than safeEpoch (meaning all threads
        /// that could have observed the node have since exited their epoch scope).
        /// </summary>
        public void Reclaim(ChunkBasedSegment segment, long safeEpoch)
        {
            // Reclaim from inline buffer (compact in-place)
            int write = 0;
            for (int read = 0; read < _inlineCount; read++)
            {
                if (_entries[read].RetireEpoch < safeEpoch)
                {
                    segment.FreeChunk(_entries[read].ChunkId);
                }
                else
                {
                    if (write != read)
                    {
                        _entries[write] = _entries[read];
                    }
                    write++;
                }
            }
            _inlineCount = write;

            // Reclaim from overflow list
            if (_overflow is { Count: > 0 })
            {
                for (int i = _overflow.Count - 1; i >= 0; i--)
                {
                    if (_overflow[i].RetireEpoch < safeEpoch)
                    {
                        segment.FreeChunk(_overflow[i].ChunkId);
                        _overflow.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Number of deferred entries pending reclamation.</summary>
        public readonly int Count => _inlineCount + (_overflow?.Count ?? 0);
    }

    #endregion

    // Optimization: warm ChunkAccessor for exclusive operations. Reused across exclusive-lock calls to avoid per-operation creation (~15ns) and keep the
    // Cached location of this BTree's entry in the chunk 0 directory.
    // Computed once at construction, used by SyncHeader for O(1) writes.
    private int _dirChunkId;
    private int _dirEntryOffset;

    /// <summary>Number of preallocated directory chunks (0-3). Provides up to 20 index slots for 64-byte chunks.</summary>
    internal const int DirectoryChunkCount = 4;

    /// <summary>Hard cap on secondary indexes per segment (could be raised later).</summary>
    internal const int MaxDirectoryEntries = 20;

    public bool IsEmpty() => _count == 0;

    public int EntryCount => _count;

    /// <summary>Number of deferred nodes pending reclamation (test visibility).</summary>
    internal int DeferredNodeCount => _deferredNodes.Count;

    /// <summary>Number of OLC optimistic read restarts (version validation failures).</summary>
    public long OptimisticRestarts => Interlocked.Read(ref _optimisticRestarts);

    /// <summary>Number of fallbacks from optimistic to pessimistic path.</summary>
    public long PessimisticFallbacks => Interlocked.Read(ref _pessimisticFallbacks);

    /// <summary>Number of SpinWriteLock spin iterations (contention on write locks).</summary>
    public long WriteLockFailures => Interlocked.Read(ref _writeLockFailures);

    /// <summary>Number of node splits (leaf + internal).</summary>
    public long SplitCount => Interlocked.Read(ref _splitCount);

    /// <summary>Number of node merges (leaf + internal).</summary>
    public long MergeCount => Interlocked.Read(ref _mergeCount);

    /// <summary>Number of move-right operations during insert (B-link following).</summary>
    public long MoveRightCount => Interlocked.Read(ref _moveRightCount);

    internal void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _optimisticRestarts, 0);
        Interlocked.Exchange(ref _pessimisticFallbacks, 0);
        Interlocked.Exchange(ref _writeLockFailures, 0);
        Interlocked.Exchange(ref _splitCount, 0);
        Interlocked.Exchange(ref _mergeCount, 0);
        Interlocked.Exchange(ref _moveRightCount, 0);
    }

    /// <summary>
    /// Returns an enumerator that walks the leaf-level linked list, yielding all entries in ascending key order.
    /// The caller must be inside an epoch scope. Uses per-leaf OLC validation (lock-free for readers).
    /// </summary>
    public LeafEnumerator EnumerateLeaves() => new LeafEnumerator(this);

    /// <summary>
    /// Returns the maximum key in the BTree. Single-threaded use only (engine init).
    /// </summary>
    public TKey GetMaxKey()
    {
        if (_count == 0)
        {
            return default;
        }

        using var guard = EpochGuard.Enter(_segment.Manager.EpochManager);
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            return GetLast(ref accessor).Key;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    public int IncCount() => Interlocked.Increment(ref _count);

    public int DecCount() => Interlocked.Decrement(ref _count);

    /// <summary>
    /// Writes <c>_count</c> and <c>Root.ChunkId</c> to this BTree's directory entry in chunk 0 (or chained chunks 1-3).
    /// Each BTree on a shared segment has a unique entry so they don't collide.
    /// </summary>
    private unsafe void SyncHeader(ref ChunkAccessor accessor)
    {
        var addr = accessor.GetChunkAddress(_dirChunkId, true);
        ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(addr + _dirEntryOffset);
        entry.Count = _count;
        entry.RootChunkId = _rootChunkId;
    }

    // Volatile root chunk ID: atomically readable by concurrent readers under OLC.
    // NodeWrapper is reconstructed on demand from _storage + _rootChunkId.
    private volatile int _rootChunkId;

    private NodeWrapper Root
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootChunkId == 0 ? default : new NodeWrapper(_storage, _rootChunkId);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _rootChunkId = value.IsValid ? value.ChunkId : 0;
    }

    private NodeWrapper LinkList;
    private NodeWrapper ReverseLinkList;

    // Volatile height: atomically readable by concurrent readers under OLC.
    // Only modified under exclusive lock; volatile prevents compiler reordering for readers.
    private volatile int _height;

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _height;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _height = value;
    }

    protected KeyValueItem GetFirst(ref ChunkAccessor accessor) => LinkList.GetFirst(ref accessor);
    protected KeyValueItem GetLast(ref ChunkAccessor accessor) => ReverseLinkList.GetLast(ref accessor);

    #endregion

    #region Public API
    
    public ChunkBasedSegment Segment => _segment;

    protected BTree(ChunkBasedSegment segment, bool load, short stableId = 0, ChangeSet changeSet = null)
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
            // Only clear content for chunks not yet allocated — subsequent BTrees sharing this
            // segment must NOT re-clear, as that would wipe existing directory entries.
            for (int i = 0; i < DirectoryChunkCount; i++)
            {
                if (!_segment.IsChunkAllocated(i))
                {
                    _segment.ReserveChunk(i, true, changeSet);
                }
            }

            // Register this BTree in the directory (append a new entry, cache its location)
            var accessor = _segment.CreateChunkAccessor(changeSet);
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

    public unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor) => Add(Unsafe.AsRef<TKey>(keyAddr), value, ref accessor, out _);
    public unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor, out int bufferRootId)
        => Add(Unsafe.AsRef<TKey>(keyAddr), value, ref accessor, out bufferRootId);
    public unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor accessor) => Remove(Unsafe.AsRef<TKey>(keyAddr), out value, ref accessor);
    public unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor accessor) => TryGet(Unsafe.AsRef<TKey>(keyAddr), ref accessor);
    public unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor accessor, bool preserveEmptyBuffer = false)
        => RemoveValue(Unsafe.AsRef<TKey>(keyAddr), elementId, value, ref accessor, preserveEmptyBuffer);
    public unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ref ChunkAccessor accessor)
        => TryGetMultiple(Unsafe.AsRef<TKey>(keyAddr), ref accessor);
    public unsafe bool Move(void* oldKeyAddr, void* newKeyAddr, int value, ref ChunkAccessor accessor)
        => Move(Unsafe.AsRef<TKey>(oldKeyAddr), Unsafe.AsRef<TKey>(newKeyAddr), value, ref accessor);
    public unsafe int MoveValue(void* oldKeyAddr, void* newKeyAddr, int elementId, int value,
        ref ChunkAccessor accessor, out int oldHeadBufferId, out int newHeadBufferId, bool preserveEmptyBuffer = false)
        => MoveValue(Unsafe.AsRef<TKey>(oldKeyAddr), Unsafe.AsRef<TKey>(newKeyAddr), elementId, value,
            ref accessor, out oldHeadBufferId, out newHeadBufferId, preserveEmptyBuffer);

    public int Add(TKey key, int value, ref ChunkAccessor accessor) => Add(key, value, ref accessor, out _);

    public int Add(TKey key, int value, ref ChunkAccessor accessor, out int bufferRootId)
    {
        Activity activity = null;
        if (TelemetryConfig.BTreeActive)
        {
            activity = TyphonActivitySource.StartActivity("BTree.Insert");
        }

        // Per-operation accessor for thread safety under OLC
        var opAccessor = _segment.CreateChunkAccessor(accessor.ChangeSet);
        try
        {
            var args = new InsertArguments(key, value, Comparer, ref opAccessor);
            AddOrUpdateCore(ref args);
            SyncHeader(ref opAccessor);
            activity?.SetTag(TyphonSpanAttributes.IndexOperation, "insert");
            bufferRootId = args.BufferRootId;
            return args.ElementId;
        }
        finally
        {
            opAccessor.CommitChanges();
            opAccessor.Dispose();
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

        // Per-operation accessor for thread safety under OLC
        var opAccessor = _segment.CreateChunkAccessor(accessor.ChangeSet);
        try
        {
            var args = new RemoveArguments(key, Comparer, ref opAccessor);
            RemoveCore(ref args);
            SyncHeader(ref opAccessor);
            value = args.Value;
            activity?.SetTag(TyphonSpanAttributes.IndexOperation, "delete");
            return args.Removed;
        }
        finally
        {
            opAccessor.CommitChanges();
            opAccessor.Dispose();
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

        // Debug/test-only: runs without locks (caller must ensure no concurrent modification)
        Root.CheckConsistency(default, NodeWrapper.CheckConsistencyParent.Root, Comparer, Height, ref accessor);

        // Check the linked link of leaves in forward
        NodeWrapper prev = default;
        var cur = LinkList;
        TKey prevValue = default;

        while (cur.IsValid)
        {
            if (cur != LinkList)
            {
                ConsistencyAssert(prev.GetNext(ref accessor) == cur, "Prev.Next doesn't link to current");
                ConsistencyAssert(cur.GetPrevious(ref accessor) == prev, "Cur.Previous doesn't link to previous");

                ConsistencyAssert(Comparer.Compare(prevValue, cur.GetFirst(ref accessor).Key) < 0,
                    $"Previous Node's first key '{prevValue}' should be less than current node's first key '{cur.GetFirst(ref accessor).Key}'.");
            }

            prevValue = cur.GetLast(ref accessor).Key;
            prev = cur;
            cur = cur.GetNext(ref accessor);
        }
        ConsistencyAssert(prev == ReverseLinkList, "Last Node of the forward chain doesn't match ReverseLinkList");

        // Check the linked link of leaves in reverse
        NodeWrapper next = default;
        cur = ReverseLinkList;
        TKey nextValue = default;

        while (cur.IsValid)
        {
            if (cur != ReverseLinkList)
            {
                ConsistencyAssert(next.GetPrevious(ref accessor) == cur, "Next.Previous doesn't link to current");
                ConsistencyAssert(cur.GetNext(ref accessor) == next, "Cur.Next doesn't link to next");

                ConsistencyAssert(Comparer.Compare(nextValue, cur.GetLast(ref accessor).Key) > 0,
                    $"Next Node's last key '{nextValue}' should be greater than current node's last key '{cur.GetLast(ref accessor).Key}'.");
            }

            nextValue = cur.GetFirst(ref accessor).Key;
            next = cur;
            cur = cur.GetPrevious(ref accessor);
        }
        ConsistencyAssert(next == LinkList, "Last Node of the reverse chain doesn't match LinkedList");
    }

    private static void ConsistencyAssert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Consistency check: {message}");
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
        // OLC optimistic path: zero locks, zero writes to shared state
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // restart
            }

            if (keyIndex < 0)
            {
                // Key not found — validate leaf version one more time
                var leaf = _storage.LoadNode(leafChunkId);
                var latch = leaf.GetLatch(ref accessor);
                if (latch.ValidateVersion(leafVersion))
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // leaf was modified — restart
            }

            // Key found — read value and validate
            var leafNode = _storage.LoadNode(leafChunkId);
            int value = leafNode.GetItem(keyIndex, ref accessor).Value;
            var leafLatch = leafNode.GetLatch(ref accessor);
            if (leafLatch.ValidateVersion(leafVersion))
            {
                return new Result<int, BTreeLookupStatus>(value);
            }
            // Value read may be stale — restart
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback after MaxOptimisticRestarts
        Interlocked.Increment(ref _pessimisticFallbacks);
        return TryGetPessimistic(key, ref accessor);
    }

    private Result<int, BTreeLookupStatus> TryGetPessimistic(TKey key, ref ChunkAccessor accessor)
    {
        // Unlimited OLC retries — guaranteed to complete as long as writers make progress
        while (true)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                Thread.SpinWait(1);
                continue;
            }

            if (keyIndex < 0)
            {
                var leaf = _storage.LoadNode(leafChunkId);
                if (leaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                continue;
            }

            var leafNode = _storage.LoadNode(leafChunkId);
            int value = leafNode.GetItem(keyIndex, ref accessor).Value;
            if (leafNode.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return new Result<int, BTreeLookupStatus>(value);
            }
        }
    }

    public bool RemoveValue(TKey key, int elementId, int value, ref ChunkAccessor accessor, bool preserveEmptyBuffer = false)
    {
        Activity activity = null;
        if (TelemetryConfig.BTreeActive)
        {
            activity = TyphonActivitySource.StartActivity("BTree.Delete");
            activity?.SetTag(TyphonSpanAttributes.IndexOperation, "delete");
        }

        // Per-operation accessor for thread safety under OLC
        var opAccessor = _segment.CreateChunkAccessor(accessor.ChangeSet);
        try
        {
            // FindLeaf traversal is safe under OLC: internal nodes are stable.
            var leaf = FindLeaf(key, out _, ref opAccessor);
            if (!leaf.IsValid)
            {
                return false;
            }

            // WriteLock leaf for consistent index and to prevent concurrent OLC modification
            SpinWriteLock(leaf.GetLatch(ref opAccessor));

            // Re-find under lock (index might have shifted due to concurrent OLC fast path remove)
            var index = leaf.Find(key, Comparer, ref opAccessor);
            if (index < 0)
            {
                leaf.GetLatch(ref opAccessor).WriteUnlock();
                return false;
            }

            var bufferId = leaf.GetItem(index, ref opAccessor).Value;
            var res = _storage.RemoveFromBuffer(bufferId, elementId, value, ref opAccessor);

            // WriteUnlock leaf — buffer manipulation is done, version bumped for OLC readers
            leaf.GetLatch(ref opAccessor).WriteUnlock();

            if (res == -1)
            {
                return false;
            }

            // Remove the key if we no longer have values stored there.
            // When preserveEmptyBuffer is true, keep the BTree key and empty HEAD buffer alive so that linked TAIL version-history buffers remain reachable
            // for temporal queries.
            if (res == 0 && !preserveEmptyBuffer)
            {
                var args = new RemoveArguments(key, Comparer, ref opAccessor);
                RemoveCorePessimistic(ref args);

                if (args.Removed)
                {
                    _storage.DeleteBuffer(args.Value, ref opAccessor);
                }

                SyncHeader(ref opAccessor);
            }
        }
        finally
        {
            opAccessor.CommitChanges();
            opAccessor.Dispose();
            activity?.Dispose();
        }

        return true;
    }

    public VariableSizedBufferAccessor<int> TryGetMultiple(TKey key, ref ChunkAccessor accessor)
    {
        // OLC optimistic path: zero locks, zero writes to shared state
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return default;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // restart
            }

            if (keyIndex < 0)
            {
                var leaf = _storage.LoadNode(leafChunkId);
                var latch = leaf.GetLatch(ref accessor);
                if (latch.ValidateVersion(leafVersion))
                {
                    return default;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // leaf was modified — restart
            }

            // Key found — read buffer ID and validate
            var leafNode = _storage.LoadNode(leafChunkId);
            int bufferId = leafNode.GetItem(keyIndex, ref accessor).Value;
            var leafLatch = leafNode.GetLatch(ref accessor);
            if (leafLatch.ValidateVersion(leafVersion))
            {
                return _storage.GetBufferReadOnlyAccessor(bufferId, ref accessor);
            }
            // Buffer ID read may be stale — restart
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback
        Interlocked.Increment(ref _pessimisticFallbacks);
        return TryGetMultiplePessimistic(key, ref accessor);
    }

    private VariableSizedBufferAccessor<int> TryGetMultiplePessimistic(TKey key, ref ChunkAccessor accessor)
    {
        // Unlimited OLC retries — guaranteed to complete as long as writers make progress
        while (true)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return default;
                }
                Thread.SpinWait(1);
                continue;
            }

            if (keyIndex < 0)
            {
                var leaf = _storage.LoadNode(leafChunkId);
                if (leaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
                {
                    return default;
                }
                continue;
            }

            var leafNode = _storage.LoadNode(leafChunkId);
            var bufferId = leafNode.GetItem(keyIndex, ref accessor).Value;
            if (leafNode.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return _storage.GetBufferReadOnlyAccessor(bufferId, ref accessor);
            }
        }
    }

    #endregion

    #region Private API

    protected internal NodeWrapper AllocNode(NodeStates states, ref ChunkAccessor accessor)
    {
        var node = new NodeWrapper(_storage, _segment.AllocateChunk(false), (states & NodeStates.IsLeaf) != 0);
        _storage.InitializeNode(node, states, ref accessor);
        return node;
    }

    /// <summary>Result of an OLC insert attempt.</summary>
    private enum OlcInsertResult
    {
        /// <summary>Insert completed successfully.</summary>
        Completed,
        /// <summary>OLC validation failed — caller should retry or fall back.</summary>
        Restart,
        /// <summary>Target leaf is full — needs pessimistic path for split/spill.</summary>
        LeafFull,
    }

    /// <summary>Result of an OLC remove attempt.</summary>
    private enum OlcRemoveResult
    {
        /// <summary>Remove completed successfully.</summary>
        Completed,
        /// <summary>OLC validation failed — caller should retry.</summary>
        Restart,
        /// <summary>Key not found in the tree (confirmed by OLC validation).</summary>
        NotFound,
        /// <summary>Remove requires merge/borrow or structural change — needs pessimistic path.</summary>
        NeedsPessimistic,
    }

    /// <summary>Spin-waits until the write lock is acquired. Counts contention spins for diagnostics.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SpinWriteLock(OlcLatch latch)
    {
        while (!latch.TryWriteLock())
        {
            Interlocked.Increment(ref _writeLockFailures);
            Thread.SpinWait(1);
        }
    }

    /// <summary>Thread-safe addition to the epoch-deferred node list (protected by _deferredLock).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DeferredAdd(int chunkId, long retireEpoch)
    {
        bool lockTaken = false;
        _deferredLock.Enter(ref lockTaken);
        try
        {
            _deferredNodes.Add(chunkId, retireEpoch);
        }
        finally
        {
            _deferredLock.Exit(useMemoryBarrier: false);
        }
    }

    /// <summary>Thread-safe reclamation of epoch-deferred nodes (protected by _deferredLock).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DeferredReclaim()
    {
        bool lockTaken = false;
        _deferredLock.Enter(ref lockTaken);
        try
        {
            _deferredNodes.Reclaim(_segment, _segment.Manager.EpochManager.MinActiveEpoch);
        }
        finally
        {
            _deferredLock.Exit(useMemoryBarrier: false);
        }
    }

    /// <summary>Creates the insert value, handling AllowMultiple buffer creation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CreateInsertValue(ref InsertArguments args, ref ChunkAccessor accessor)
    {
        if (AllowMultiple)
        {
            var bufferId = _storage.CreateBuffer(ref accessor);
            args.ElementId = _storage.Append(bufferId, args.GetValue(), ref accessor);
            args.BufferRootId = bufferId;
            return bufferId;
        }
        return args.GetValue();
    }

    /// <summary>
    /// OLC-dispatching remove: tries optimistic fast paths first, then falls back to pessimistic.
    /// Begin/end remove fast paths and general mid-leaf remove operate without exclusive lock when the leaf has enough items (no merge/borrow needed).
    /// All other cases use the pessimistic path.
    /// </summary>
    private void RemoveCore(ref RemoveArguments args)
    {
        if (IsEmpty())
        {
            return;
        }

        // OLC retry loop — handles begin/end fast paths + general non-merge removes
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var result = TryRemoveOlc(ref args);
            if (result == OlcRemoveResult.Completed || result == OlcRemoveResult.NotFound)
            {
                return;
            }
            if (result == OlcRemoveResult.NeedsPessimistic)
            {
                break;
            }
            // Restart: continue loop
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback
        Interlocked.Increment(ref _pessimisticFallbacks);
        RemoveCorePessimistic(ref args);
    }

    /// <summary>
    /// OLC remove attempt: tries begin/end fast paths (first/last key of first/last leaf) and general mid-leaf remove via optimistic descent.
    /// Only modifies a single leaf node (WriteLocked). Returns NeedsPessimistic when the leaf is too small (merge/borrow would be needed).
    /// </summary>
    private OlcRemoveResult TryRemoveOlc(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;

        // --- Begin-remove fast path: remove first key of leftmost leaf ---
        {
            var ll = LinkList;
            if (!ll.IsValid)
            {
                return OlcRemoveResult.Restart;
            }
            var llLatch = ll.GetLatch(ref accessor);
            int llVersion = llLatch.ReadVersion();
            if (llVersion == 0)
            {
                return OlcRemoveResult.Restart;
            }

            var firstKey = ll.GetFirst(ref accessor).Key;
            if (!llLatch.ValidateVersion(llVersion))
            {
                return OlcRemoveResult.Restart;
            }

            int order = args.Compare(args.Key, firstKey);
            if (order < 0)
            {
                return OlcRemoveResult.NotFound; // key < first key → definitely not in tree
            }

            if (order == 0)
            {
                int count = ll.GetCount(ref accessor);
                bool isRoot = _rootChunkId == ll.ChunkId;
                int capacity = ll.GetCapacity();
                if (!llLatch.ValidateVersion(llVersion))
                {
                    return OlcRemoveResult.Restart;
                }

                // Safe if: root leaf with count > 1, or non-root leaf above half-full
                if ((isRoot && count > 1) || (!isRoot && count > capacity / 2))
                {
                    if (!llLatch.TryWriteLock())
                    {
                        return OlcRemoveResult.Restart;
                    }
                    if (!llLatch.ValidateVersionLocked(llVersion))
                    {
                        llLatch.AbortWriteLock();
                        return OlcRemoveResult.Restart;
                    }
                    // Re-verify first key under lock (concurrent OLC writer might have removed it)
                    if (args.Compare(args.Key, ll.GetFirst(ref accessor).Key) != 0)
                    {
                        llLatch.WriteUnlock();
                        return OlcRemoveResult.Restart;
                    }

                    args.SetRemovedValue(ll.PopFirstInternal(ref accessor).Value);
                    llLatch.WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    return OlcRemoveResult.Completed;
                }

                return OlcRemoveResult.NeedsPessimistic; // merge/borrow possible or tree might become empty
            }
        }

        // --- End-remove fast path: remove last key of rightmost leaf ---
        {
            var rll = ReverseLinkList;
            if (!rll.IsValid)
            {
                return OlcRemoveResult.Restart;
            }
            var rllLatch = rll.GetLatch(ref accessor);
            int rllVersion = rllLatch.ReadVersion();
            if (rllVersion == 0)
            {
                return OlcRemoveResult.Restart;
            }

            int rllCount = rll.GetCount(ref accessor);
            if (rllCount == 0)
            {
                return OlcRemoveResult.Restart; // transient empty state during concurrent tree emptying
            }
            var lastKey = rll.GetItem(rllCount - 1, ref accessor).Key;
            if (!rllLatch.ValidateVersion(rllVersion))
            {
                return OlcRemoveResult.Restart;
            }

            int order = args.Compare(args.Key, lastKey);
            if (order > 0)
            {
                return OlcRemoveResult.NotFound; // key > last key → definitely not in tree
            }

            if (order == 0)
            {
                bool isRoot = _rootChunkId == rll.ChunkId;
                int capacity = rll.GetCapacity();
                if (!rllLatch.ValidateVersion(rllVersion))
                {
                    return OlcRemoveResult.Restart;
                }

                if ((isRoot && rllCount > 1) || (!isRoot && rllCount > capacity / 2))
                {
                    if (!rllLatch.TryWriteLock())
                    {
                        return OlcRemoveResult.Restart;
                    }
                    if (!rllLatch.ValidateVersionLocked(rllVersion))
                    {
                        rllLatch.AbortWriteLock();
                        return OlcRemoveResult.Restart;
                    }
                    // Re-verify last key under lock
                    if (args.Compare(args.Key, rll.GetLast(ref accessor).Key) != 0)
                    {
                        rllLatch.WriteUnlock();
                        return OlcRemoveResult.Restart;
                    }

                    args.SetRemovedValue(rll.PopLastInternal(ref accessor).Value);
                    rllLatch.WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    return OlcRemoveResult.Completed;
                }

                return OlcRemoveResult.NeedsPessimistic;
            }
        }

        // --- General path: optimistic descent to leaf, remove if safe (no merge/borrow) ---
        var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(args.Key, ref accessor);
        if (leafChunkId == 0)
        {
            return OlcRemoveResult.Restart;
        }

        if (keyIndex < 0)
        {
            // Key not found — validate version to confirm
            var nfLeaf = new NodeWrapper(_storage, leafChunkId);
            if (!nfLeaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return OlcRemoveResult.Restart;
            }
            return OlcRemoveResult.NotFound;
        }

        // Key found — check if safe to remove under OLC (no merge/borrow needed)
        {
            var leaf = new NodeWrapper(_storage, leafChunkId);
            var leafLatch = leaf.GetLatch(ref accessor);
            int count = leaf.GetCount(ref accessor);
            bool isRoot = _rootChunkId == leafChunkId;
            int capacity = leaf.GetCapacity();
            if (!leafLatch.ValidateVersion(leafVersion))
            {
                return OlcRemoveResult.Restart;
            }

            if ((isRoot && count > 1) || (!isRoot && count > capacity / 2))
            {
                if (!leafLatch.TryWriteLock())
                {
                    return OlcRemoveResult.Restart;
                }
                if (!leafLatch.ValidateVersionLocked(leafVersion))
                {
                    leafLatch.AbortWriteLock();
                    return OlcRemoveResult.Restart;
                }

                // Re-find key under lock (index might have shifted due to concurrent modification)
                var reIndex = leaf.Find(args.Key, args.Comparer, ref accessor);
                if (reIndex < 0)
                {
                    leafLatch.WriteUnlock();
                    return OlcRemoveResult.NotFound; // concurrent writer already removed it
                }

                args.SetRemovedValue(leaf.RemoveAtInternal(reIndex, ref accessor).Value);
                leafLatch.WriteUnlock();
                _hasCachedLastKey = false;
                DecCount();
                return OlcRemoveResult.Completed;
            }

            return OlcRemoveResult.NeedsPessimistic;
        }
    }

    /// <summary>
    /// Pessimistic remove fallback: uses WriteLock/WriteUnlock on individual nodes so concurrent OLC readers detect changes. No global lock — concurrency is
    /// handled by per-node OLC latches and latch-coupled SMO in RemoveIterative.
    /// </summary>
    private void RemoveCorePessimistic(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;
        try
        {
            if (IsEmpty())
            {
                return;
            }

            // Begin-remove fast path (WriteLock protects against concurrent OLC writers)
            {
                var ll = LinkList;
                SpinWriteLock(ll.GetLatch(ref accessor));
                int order = args.Compare(args.Key, ll.GetFirst(ref accessor).Key);
                if (order < 0)
                {
                    ll.GetLatch(ref accessor).AbortWriteLock(); // key not in tree — didn't modify node
                    return;
                }

                if (order == 0 && (Root == ll || ll.GetCount(ref accessor) > ll.GetCapacity() / 2))
                {
                    args.SetRemovedValue(ll.PopFirstInternal(ref accessor).Value);
                    ll.GetLatch(ref accessor).WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    if (IsEmpty())
                    {
                        Root = LinkList = ReverseLinkList = default;
                        Height--;
                    }
                    return;
                }
                ll.GetLatch(ref accessor).AbortWriteLock(); // condition failed — didn't modify node
            }

            // End-remove fast path
            {
                var rll = ReverseLinkList;
                SpinWriteLock(rll.GetLatch(ref accessor));

                // Safety: if rll was split concurrently, it's no longer the rightmost leaf.
                // Fall through to general path which handles stale pointers correctly.
                if (rll.GetNext(ref accessor).IsValid)
                {
                    rll.GetLatch(ref accessor).AbortWriteLock();
                }
                else
                {
                    int order = args.Compare(args.Key, rll.GetLast(ref accessor).Key);
                    if (order > 0)
                    {
                        rll.GetLatch(ref accessor).AbortWriteLock(); // key not in tree — didn't modify node
                        return;
                    }

                    if (order == 0 && (Root == rll || rll.GetCount(ref accessor) > rll.GetCapacity() / 2))
                    {
                        args.SetRemovedValue(rll.PopLastInternal(ref accessor).Value);
                        rll.GetLatch(ref accessor).WriteUnlock();
                        _hasCachedLastKey = false;
                        DecCount();
                        return;
                    }
                    rll.GetLatch(ref accessor).AbortWriteLock(); // condition failed — didn't modify node
                }
            }

            // General remove path with latch-coupled SMO — retry on lock contention
            _hasCachedLastKey = false;
            bool merge;
            int retries = 0;
            while (true)
            {
                merge = RemoveIterative(ref args, ref accessor, out bool removeCompleted);
                if (removeCompleted)
                {
                    break;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                if (++retries > 64)
                {
                    Thread.SpinWait(retries); // exponential back-off on high contention
                }
            }

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

            if (ReverseLinkList.IsValid && ReverseLinkList.GetPrevious(ref accessor).IsValid && ReverseLinkList.GetPrevious(ref accessor).GetNext(ref accessor).IsValid == false)
            {
                ReverseLinkList = ReverseLinkList.GetPrevious(ref accessor);
            }
        }
        finally
        {
            // Reclaim deferred nodes whose epoch is safe (all readers have exited).
            DeferredReclaim();
        }
    }

    private void AddOrUpdateCore(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;

        // 1. Empty tree initialization.
        //    An empty tree has no root node, so OLC readers/writers have nothing to latch on.
        //    CAS on _rootChunkId atomically races to claim the init slot; loser sees non-zero root and proceeds.
        if (IsEmpty())
        {
            var newRoot = AllocNode(NodeStates.IsLeaf, ref accessor);
            if (Interlocked.CompareExchange(ref _rootChunkId, newRoot.ChunkId, 0) == 0)
            {
                // We won the race — initialize root, LinkList, ReverseLinkList
                LinkList = newRoot;
                ReverseLinkList = newRoot;
                Height++;
                int value = CreateInsertValue(ref args, ref accessor);
                newRoot.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                IncCount();
                _cachedLastKey = args.Key;
                _hasCachedLastKey = true;
                return;
            }
            // Another thread initialized the root — free our unused node and fall through
            _segment.FreeChunk(newRoot.ChunkId);
        }

        // 2. OLC retry loop — handles append/prepend fast paths + non-full leaf inserts.
        //    Zero writes to shared state except the single leaf being modified (WriteLocked).
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var result = TryInsertOlc(ref args);
            if (result == OlcInsertResult.Completed)
            {
                return;
            }
            if (result == OlcInsertResult.LeafFull)
            {
                break; // Need pessimistic path for split/spill
            }
            // Restart: version validation failed
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // 3. Pessimistic fallback — exclusive lock + WriteLock all modified nodes for OLC readers
        Interlocked.Increment(ref _pessimisticFallbacks);
        AddOrUpdateCorePessimistic(ref args);
    }

    /// <summary>
    /// OLC insert attempt: tries append/prepend fast paths and non-full leaf insert.
    /// Only modifies a single leaf node (WriteLocked). Returns LeafFull when the target leaf is full and needs split/spill (which requires the pessimistic path).
    /// </summary>
    private OlcInsertResult TryInsertOlc(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;

        // --- Append fast path: insert at end of rightmost leaf ---
        var rl = ReverseLinkList;
        if (rl.IsValid)
        {
            var latch = rl.GetLatch(ref accessor);
            var version = latch.ReadVersion();
            if (version != 0)
            {
                int rlCount = rl.GetCount(ref accessor);
                if (rlCount > 0)
                {
                    var lastKey = rl.GetLast(ref accessor).Key;
                    if (!latch.ValidateVersion(version))
                    {
                        return OlcInsertResult.Restart;
                    }

                    int order = args.Compare(args.Key, lastKey);
                    if (order > 0)
                    {
                        if (!latch.TryWriteLock())
                        {
                            return OlcInsertResult.Restart;
                        }
                        if (!latch.ValidateVersionLocked(version))
                        {
                            latch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        // Safety: if rl was split concurrently, it's no longer the rightmost leaf.
                        // GetNext().IsValid means a new right sibling exists — abort and fall through.
                        if (rl.GetIsFull(ref accessor) || rl.GetNext(ref accessor).IsValid)
                        {
                            latch.AbortWriteLock();
                            return rl.GetIsFull(ref accessor) ? OlcInsertResult.LeafFull : OlcInsertResult.Restart;
                        }
                        int value = CreateInsertValue(ref args, ref accessor);
                        rl.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                        _cachedLastKey = args.Key;
                        _hasCachedLastKey = true;
                        latch.WriteUnlock();
                        IncCount();
                        return OlcInsertResult.Completed;
                    }

                    if (order == 0)
                    {
                        if (AllowMultiple)
                        {
                            if (!latch.TryWriteLock())
                            {
                                return OlcInsertResult.Restart;
                            }
                            if (!latch.ValidateVersionLocked(version))
                            {
                                latch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            var bufferRootId = rl.GetLast(ref accessor).Value;
                            args.ElementId = _storage.Append(bufferRootId, args.GetValue(), ref accessor);
                            args.BufferRootId = bufferRootId;
                            latch.WriteUnlock();
                            return OlcInsertResult.Completed;
                        }
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }
        }

        // --- Prepend fast path: insert at beginning of leftmost leaf ---
        var ll = LinkList;
        if (ll.IsValid)
        {
            var llLatch = ll.GetLatch(ref accessor);
            var llVersion = llLatch.ReadVersion();
            if (llVersion != 0)
            {
                int llCount = ll.GetCount(ref accessor);
                if (llCount > 0)
                {
                    var firstKey = ll.GetFirst(ref accessor).Key;
                    if (!llLatch.ValidateVersion(llVersion))
                    {
                        return OlcInsertResult.Restart;
                    }

                    int order = args.Compare(args.Key, firstKey);
                    if (order < 0)
                    {
                        if (!llLatch.TryWriteLock())
                        {
                            return OlcInsertResult.Restart;
                        }
                        if (!llLatch.ValidateVersionLocked(llVersion))
                        {
                            llLatch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        if (ll.GetIsFull(ref accessor))
                        {
                            llLatch.WriteUnlock();
                            return OlcInsertResult.LeafFull;
                        }
                        int value = CreateInsertValue(ref args, ref accessor);
                        ll.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
                        llLatch.WriteUnlock();
                        IncCount();
                        return OlcInsertResult.Completed;
                    }

                    if (order == 0)
                    {
                        if (AllowMultiple)
                        {
                            if (!llLatch.TryWriteLock())
                            {
                                return OlcInsertResult.Restart;
                            }
                            if (!llLatch.ValidateVersionLocked(llVersion))
                            {
                                llLatch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            var bufferRootId = ll.GetFirst(ref accessor).Value;
                            args.ElementId = _storage.Append(bufferRootId, args.GetValue(), ref accessor);
                            args.BufferRootId = bufferRootId;
                            llLatch.WriteUnlock();
                            return OlcInsertResult.Completed;
                        }
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }
        }

        // --- General path: optimistic descent to leaf, non-full insert ---
        // followRightLink: false — inserts must not follow B-link because inserting into the right sibling bypasses the spill/split path that updates parent
        // separators. If the leaf was split concurrently, version validation will trigger a restart.
        var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(args.Key, ref accessor, followRightLink: false);
        if (leafChunkId == 0)
        {
            return OlcInsertResult.Restart;
        }

        var leaf = new NodeWrapper(_storage, leafChunkId);
        var leafLatch = leaf.GetLatch(ref accessor);
        if (!leafLatch.TryWriteLock())
        {
            return OlcInsertResult.Restart;
        }
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock();
            return OlcInsertResult.Restart;
        }
        // Range check: stale separator may route to wrong leaf after a concurrent split.
        if (leaf.GetCount(ref accessor) > 0 && leaf.GetNext(ref accessor).IsValid &&
            args.Compare(args.Key, leaf.GetLast(ref accessor).Key) > 0)
        {
            leafLatch.WriteUnlock();
            return OlcInsertResult.Restart;
        }
        if (leaf.GetIsFull(ref accessor))
        {
            leafLatch.WriteUnlock();
            return OlcInsertResult.LeafFull;
        }

        // Re-search under lock (key positions may have shifted since optimistic read)
        keyIndex = leaf.Find(args.Key, args.KeyComparer, ref accessor);
        if (keyIndex < 0)
        {
            keyIndex = ~keyIndex;
            int value = CreateInsertValue(ref args, ref accessor);
            leaf.Insert(keyIndex, new KeyValueItem(args.Key, value), ref accessor);
            leafLatch.WriteUnlock();
            IncCount();
            return OlcInsertResult.Completed;
        }

        // Key already exists
        if (AllowMultiple)
        {
            var curItem = leaf.GetItem(keyIndex, ref accessor);
            args.ElementId = _storage.Append(curItem.Value, args.GetValue(), ref accessor);
            args.BufferRootId = curItem.Value;
            leafLatch.WriteUnlock();
            return OlcInsertResult.Completed;
        }
        leafLatch.WriteUnlock();
        ThrowHelper.ThrowUniqueConstraintViolation();
        return OlcInsertResult.Restart; // unreachable — ThrowHelper always throws
    }

    /// <summary>
    /// Pessimistic insert fallback: uses InsertIterative with latch-coupled SMO.
    /// No global lock — concurrency is handled by per-node OLC latches.
    /// </summary>
    private void AddOrUpdateCorePessimistic(ref InsertArguments args)
    {
        try
        {
            ref var accessor = ref args.Accessor;

            if (IsEmpty())
            {
                Root = AllocNode(NodeStates.IsLeaf, ref accessor);
                LinkList = Root;
                ReverseLinkList = LinkList;
                Height++;
            }

            // Append fast path: lock the last leaf and insert if key > lastKey and leaf not full.
            // Capture local refs to avoid races from concurrent ReverseLinkList/LinkList updates.
            {
                var rl = ReverseLinkList;
                int order = IsEmpty() ? 1 : args.Compare(args.Key, _hasCachedLastKey ? _cachedLastKey : rl.GetLast(ref accessor).Key);
                if (order > 0 && !rl.GetIsFull(ref accessor))
                {
                    var rlLatch = rl.GetLatch(ref accessor);
                    SpinWriteLock(rlLatch);
                    // Re-validate under lock: leaf may now be full, another writer inserted a larger key,
                    // or a concurrent split made this leaf no longer the rightmost (GetNext becomes valid).
                    if (!rl.GetIsFull(ref accessor) && !rl.GetNext(ref accessor).IsValid && args.Compare(args.Key, rl.GetLast(ref accessor).Key) > 0)
                    {
                        int value = CreateInsertValue(ref args, ref accessor);
                        rl.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                        _cachedLastKey = args.Key;
                        _hasCachedLastKey = true;
                        rlLatch.WriteUnlock();
                        IncCount();
                        return;
                    }
                    rlLatch.AbortWriteLock();
                    // Fall through to general path
                }
                else if (order == 0 && AllowMultiple)
                {
                    var rlLatch = rl.GetLatch(ref accessor);
                    SpinWriteLock(rlLatch);
                    var lastEntry = rl.GetLast(ref accessor);
                    if (args.Compare(args.Key, lastEntry.Key) == 0)
                    {
                        args.ElementId = _storage.Append(lastEntry.Value, args.GetValue(), ref accessor);
                        args.BufferRootId = lastEntry.Value;
                        rlLatch.WriteUnlock();
                        return;
                    }
                    rlLatch.AbortWriteLock();
                    // Fall through
                }
                else if (order == 0)
                {
                    ThrowHelper.ThrowUniqueConstraintViolation();
                }
            }

            // Prepend fast path: lock the first leaf and insert if key < firstKey and leaf not full.
            if (!IsEmpty())
            {
                var ll = LinkList;
                int order = args.Compare(args.Key, ll.GetFirst(ref accessor).Key);
                if (order < 0 && !ll.GetIsFull(ref accessor))
                {
                    var llLatch = ll.GetLatch(ref accessor);
                    SpinWriteLock(llLatch);
                    if (!ll.GetIsFull(ref accessor) && args.Compare(args.Key, ll.GetFirst(ref accessor).Key) < 0)
                    {
                        int value = CreateInsertValue(ref args, ref accessor);
                        ll.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
                        llLatch.WriteUnlock();
                        IncCount();
                        return;
                    }
                    llLatch.AbortWriteLock();
                    // Fall through
                }
                else if (order == 0 && AllowMultiple)
                {
                    var llLatch = ll.GetLatch(ref accessor);
                    SpinWriteLock(llLatch);
                    var firstEntry = ll.GetFirst(ref accessor);
                    if (args.Compare(args.Key, firstEntry.Key) == 0)
                    {
                        args.ElementId = _storage.Append(firstEntry.Value, args.GetValue(), ref accessor);
                        args.BufferRootId = firstEntry.Value;
                        llLatch.WriteUnlock();
                        return;
                    }
                    llLatch.AbortWriteLock();
                    // Fall through
                }
                else if (order == 0)
                {
                    ThrowHelper.ThrowUniqueConstraintViolation();
                }
            }

            // General path with latch-coupled SMO — retry on lock contention
            // InsertIterative handles root splits internally under the root's write lock.
            int retries = 0;
            while (true)
            {
                InsertIterative(ref args, ref accessor, out bool insertCompleted);
                if (insertCompleted)
                {
                    break;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                if (++retries > 64)
                {
                    Thread.SpinWait(retries); // exponential back-off on high contention
                }
            }

            if (args.Added)
            {
                IncCount();
            }
            else if (!AllowMultiple)
            {
                ThrowHelper.ThrowUniqueConstraintViolation();
            }

            var next = ReverseLinkList.GetNext(ref accessor);
            if (next.IsValid)
            {
                ReverseLinkList = next;
            }
            _cachedLastKey = GetLast(ref accessor).Key;
            _hasCachedLastKey = true;
        }
        finally
        {
            // Reclaim deferred nodes whose epoch is safe (all readers have exited).
            DeferredReclaim();
        }
    }

    /// <summary>
    /// Iterative insert with latch-coupled SMO: descends optimistically recording PathVersions, then locks bottom-up only as needed for structural modifications.
    /// Fast path (leaf not full): locks only the leaf node.
    /// Slow path (leaf full, new key): locks leaf + neighbors + path nodes with version validation.
    /// Returns null if no root split, non-null promoted key if root split needed.
    /// Sets <paramref name="completed"/> to false when lock acquisition fails and caller must retry.
    /// </summary>
    private KeyValueItem? InsertIterative(ref InsertArguments args, ref ChunkAccessor accessor, out bool completed)
    {
        completed = false;
        MutationContext ctx = default;
        var node = Root;
        var relatives = new NodeRelatives();

        // Phase 1: Descend from root to leaf, recording path + PathVersions for validation.
        // OLC protocol: read version BEFORE data, validate AFTER — ensures (index, version) are consistent.
        while (!node.GetIsLeaf(ref accessor))
        {
            var latch = node.GetLatch(ref accessor);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return null; // node locked or obsolete — restart
            }

            var index = node.Find(args.Key, args.KeyComparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            var child = node.GetChild(index, ref accessor);
            int parentCount = node.GetCount(ref accessor);

            // Validate: node wasn't modified during our unlocked read
            if (!latch.ValidateVersion(version))
            {
                return null; // node modified between version read and data read — restart
            }

            NodeRelatives.Create(child, index, node, parentCount, ref relatives, out var childRelatives, ref accessor);

            ctx.PathNodes[ctx.Depth] = node;
            ctx.PathChildIndices[ctx.Depth] = index;
            ctx.PathVersions[ctx.Depth] = version;

            // Store after Create so lazy-resolved siblings are cached in the stored copy
            ctx.PathRelatives[ctx.Depth] = relatives;

            node = child;
            relatives = childRelatives;
            ctx.Depth++;
        }

        // Phase 1.5A: Lock leaf with version validation.
        // Between Phase 1 descent and lock acquisition, a concurrent writer may have split/modified this leaf. Snapshot the version before locking,
        // then validate after.
        var leafLatch = node.GetLatch(ref accessor);
        int leafVersion = leafLatch.ReadVersion();
        if (leafVersion == 0)
        {
            // Leaf is locked or obsolete. SpinWriteLock to wait for the current holder to release, then restart — we can't validate without a baseline version.
            SpinWriteLock(leafLatch);
            leafLatch.AbortWriteLock(); // release without version bump (we didn't modify anything)
            return null;
        }
        SpinWriteLock(leafLatch);
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock(); // release without version bump — leaf was modified, not by us
            return null; // restart — leaf was modified between descent and lock
        }

        // B-link move_right (Lehman & Yao): if the key is beyond this leaf's range, a concurrent split moved some keys to a right sibling. Chain right using
        // lock coupling (lock next before releasing current) until we find the correct leaf. Forward progress is guaranteed:
        // all movement is strictly rightward with no cycle, and SpinWriteLock waits for busy siblings.
        bool movedRight = false;
        while (node.GetCount(ref accessor) > 0 && node.GetNext(ref accessor).IsValid &&
               args.Compare(args.Key, node.GetLast(ref accessor).Key) > 0)
        {
            Interlocked.Increment(ref _moveRightCount);
            var nextNode = node.GetNext(ref accessor);
            SpinWriteLock(nextNode.GetLatch(ref accessor));

            // Gap check: after locking next leaf, verify key belongs there.
            // Without this, move_right chains across subtree boundaries when the key space has gaps (e.g., leaves [14-26] → [201-213] with no intermediate leaves).
            // Key 27 would land in [201-213] where it doesn't belong → BST violation.
            if (nextNode.GetCount(ref accessor) > 0 &&
                args.Compare(args.Key, nextNode.GetFirst(ref accessor).Key) < 0)
            {
                nextNode.GetLatch(ref accessor).AbortWriteLock();
                break; // Key falls in a gap — belongs in current leaf (will split via slow path)
            }

            node.GetLatch(ref accessor).AbortWriteLock();    // release current
            node = nextNode;
            movedRight = true;
        }

        // Fast path: leaf not full → InsertLeaf only modifies this leaf (insert or duplicate append)
        if (!node.GetIsFull(ref accessor))
        {
            node.InsertLeaf(ref args, ref relatives, ref accessor);
            node.GetLatch(ref accessor).WriteUnlock();
            completed = true;
            return null; // no split when leaf has space
        }

        // Check if key already exists in full leaf (buffer append, no structural change)
        {
            var idx = node.Find(args.Key, args.KeyComparer, ref accessor);
            if (idx >= 0)
            {
                node.InsertLeaf(ref args, ref relatives, ref accessor);
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return null;
            }
        }

        // After move_right, PathVersions and relatives are stale (recorded for the original leaf's// path). The gap check above prevents wrong-subtree inserts.
        // Force split (skip spill which uses stale relatives) and don't propagate — B-link right-link chain provides correct routing until a future insert
        // naturally propagates the separator.
        // This avoids the restart livelock that occurs when the slow path repeatedly fails.
        if (movedRight)
        {
            // Lock the next neighbor for the split's linked list update (SetPrevious on next node)
            var mrNext = node.GetNext(ref accessor);
            if (mrNext.IsValid)
            {
                SpinWriteLock(mrNext.GetLatch(ref accessor));
            }

            node.InsertLeaf(ref args, ref relatives, ref accessor, forceSplit: true);

            if (mrNext.IsValid)
            {
                mrNext.GetLatch(ref accessor).WriteUnlock();
            }
            node.GetLatch(ref accessor).WriteUnlock();
            completed = true;
            return null; // promoted key not propagated — B-link covers the gap
        }

        // Slow path: leaf full, new key → structural modification (spill or split) needed.
        // Lock leaf neighbors for potential spill.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        var leafPrev = node.GetPrevious(ref accessor);
        var leafNext = node.GetNext(ref accessor);
        if (leafPrev.IsValid && !leafPrev.GetLatch(ref accessor).TryWriteLock())
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return null; // restart
        }
        if (leafNext.IsValid && !leafNext.GetLatch(ref accessor).TryWriteLock())
        {
            if (leafPrev.IsValid)
            {
                leafPrev.GetLatch(ref accessor).AbortWriteLock();
            }
            node.GetLatch(ref accessor).AbortWriteLock();
            return null; // restart
        }

        // Lock path nodes bottom-up with version validation.
        // Required for ancestor key updates during spill and split propagation.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        for (int i = ctx.Depth - 1; i >= 0; i--)
        {
            var pathLatch = ctx.PathNodes[i].GetLatch(ref accessor);
            if (!pathLatch.TryWriteLock())
            {
                // Unlock path nodes already acquired above this level
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref accessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return null; // restart
            }
            if (!pathLatch.ValidateVersionLocked(ctx.PathVersions[i]))
            {
                pathLatch.AbortWriteLock();
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref accessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return null; // restart
            }
        }

        // All needed nodes locked — Phase 2: Insert at leaf (may spill or split)
        var promoted = node.InsertLeaf(ref args, ref relatives, ref accessor);

        // Phase 2.5: Unlock leaf neighbors (version bumped by WriteUnlock)
        if (leafNext.IsValid)
        {
            leafNext.GetLatch(ref accessor).WriteUnlock();
        }
        if (leafPrev.IsValid)
        {
            leafPrev.GetLatch(ref accessor).WriteUnlock();
        }
        // Defer leaf unlock if this is a root-leaf that split (need to hold lock for atomic root creation)
        if (!(ctx.Depth == 0 && promoted != null))
        {
            node.GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 3: Propagate splits upward through internal nodes
        while (ctx.Depth > 0 && promoted != null)
        {
            ctx.Depth--;
            node = ctx.PathNodes[ctx.Depth];
            relatives = ctx.PathRelatives[ctx.Depth];

            // Lock siblings that HandlePromotedInsert might spill to (only when node is full)
            NodeWrapper leftSib = default, rightSib = default;
            if (node.GetIsFull(ref accessor))
            {
                leftSib = relatives.GetLeftSibling(ref accessor);
                rightSib = relatives.GetRightSibling(ref accessor);
                if (leftSib.IsValid)
                {
                    SpinWriteLock(leftSib.GetLatch(ref accessor));
                }
                if (rightSib.IsValid)
                {
                    SpinWriteLock(rightSib.GetLatch(ref accessor));
                }
            }

            promoted = node.HandlePromotedInsert(ctx.PathChildIndices[ctx.Depth], promoted.Value, ref relatives, ref accessor);

            // Unlock siblings
            if (rightSib.IsValid)
            {
                rightSib.GetLatch(ref accessor).WriteUnlock();
            }
            if (leftSib.IsValid)
            {
                leftSib.GetLatch(ref accessor).WriteUnlock();
            }
            // Defer root unlock if root split (need to hold lock for atomic root creation)
            if (!(ctx.Depth == 0 && promoted != null))
            {
                node.GetLatch(ref accessor).WriteUnlock();
            }
        }

        // Phase 3.5: Unlock remaining path nodes above propagation level
        while (ctx.Depth > 0)
        {
            ctx.Depth--;
            ctx.PathNodes[ctx.Depth].GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 4: Root split — create new root while holding old root's write lock.
        // This prevents concurrent InsertIterative calls from racing to create multiple roots.
        if (promoted != null)
        {
            var newRoot = AllocNode(NodeStates.None, ref accessor);
            newRoot.SetLeft(Root, ref accessor);
            newRoot.Insert(0, promoted.Value, ref accessor);
            Root = newRoot;
            Height++;
            node.GetLatch(ref accessor).WriteUnlock(); // release old root after publishing new root
        }

        completed = true;
        return null; // root splits handled internally — never return promoted to caller
    }

    /// <summary>
    /// Iterative remove with latch-coupled SMO: descends optimistically recording PathVersions, then locks bottom-up only as needed for structural modifications.
    /// Fast path (leaf stays half-full or root leaf): locks only the leaf node.
    /// Slow path (leaf underflows): locks leaf + neighbors + path nodes with version validation.
    /// Sets <paramref name="completed"/> to false when lock acquisition fails and caller must retry.
    /// </summary>
    private bool RemoveIterative(ref RemoveArguments args, ref ChunkAccessor accessor, out bool completed)
    {
        completed = false;
        MutationContext ctx = default;
        var node = Root;
        var relatives = new NodeRelatives();

        // Phase 1: Descend from root to leaf, recording path + PathVersions for validation.
        // OLC protocol: read version BEFORE data, validate AFTER — ensures (index, version) are consistent.
        while (!node.GetIsLeaf(ref accessor))
        {
            var latch = node.GetLatch(ref accessor);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return false; // node locked or obsolete — restart
            }

            var index = node.Find(args.Key, args.Comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            var child = node.GetChild(index, ref accessor);
            int parentCount = node.GetCount(ref accessor);

            // Validate: node wasn't modified during our unlocked read
            if (!latch.ValidateVersion(version))
            {
                return false; // node modified between version read and data read — restart
            }

            NodeRelatives.Create(child, index, node, parentCount, ref relatives, out var childRelatives, ref accessor);

            ctx.PathNodes[ctx.Depth] = node;
            ctx.PathChildIndices[ctx.Depth] = index;
            ctx.PathVersions[ctx.Depth] = version;

            // Store after Create so lazy-resolved siblings are cached in the stored copy
            ctx.PathRelatives[ctx.Depth] = relatives;

            node = child;
            relatives = childRelatives;
            ctx.Depth++;
        }

        // Phase 1.5A: Lock leaf with version validation.
        // Between Phase 1 descent and lock acquisition, a concurrent writer may have split/modified
        // this leaf. Snapshot the version before locking, then validate after.
        var leafLatch = node.GetLatch(ref accessor);
        int leafVersion = leafLatch.ReadVersion();
        if (leafVersion == 0)
        {
            // Leaf is locked or obsolete. SpinWriteLock to wait, then restart.
            SpinWriteLock(leafLatch);
            leafLatch.AbortWriteLock();
            return false;
        }
        SpinWriteLock(leafLatch);
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock();
            return false; // restart — leaf was modified between descent and lock
        }

        // Check if key exists in this leaf
        var keyIndex = node.Find(args.Key, args.Comparer, ref accessor);
        if (keyIndex < 0)
        {
            node.GetLatch(ref accessor).AbortWriteLock(); // key not found — didn't modify leaf
            completed = true;
            return false; // key not found — no merge
        }

        // Fast path: leaf won't underflow after remove (count > capacity/2) or root leaf (depth == 0).
        // RemoveLeaf only modifies the leaf in this case (no borrow/merge needed).
        int count = node.GetCount(ref accessor);
        if (count > node.GetCapacity() / 2 || ctx.Depth == 0)
        {
            bool fastMerged = node.RemoveLeaf(ref args, ref relatives, ref accessor);
            node.GetLatch(ref accessor).WriteUnlock();
            completed = true;
            return fastMerged;
        }

        // Slow path: leaf may underflow → need neighbors + path for borrow/merge.
        // Lock leaf neighbors for potential borrow or merge.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        var leafPrev = node.GetPrevious(ref accessor);
        var leafNext = node.GetNext(ref accessor);
        if (leafPrev.IsValid && !leafPrev.GetLatch(ref accessor).TryWriteLock())
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return false; // restart
        }
        if (leafNext.IsValid && !leafNext.GetLatch(ref accessor).TryWriteLock())
        {
            if (leafPrev.IsValid)
            {
                leafPrev.GetLatch(ref accessor).AbortWriteLock();
            }
            node.GetLatch(ref accessor).AbortWriteLock();
            return false; // restart
        }

        // Lock path nodes bottom-up with version validation.
        // Required for ancestor key updates during borrow and merge propagation.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        for (int i = ctx.Depth - 1; i >= 0; i--)
        {
            var pathLatch = ctx.PathNodes[i].GetLatch(ref accessor);
            if (!pathLatch.TryWriteLock())
            {
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref accessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return false; // restart
            }
            if (!pathLatch.ValidateVersionLocked(ctx.PathVersions[i]))
            {
                pathLatch.AbortWriteLock();
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref accessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return false; // restart
            }
        }

        // All needed nodes locked — Phase 2: Remove at leaf (may borrow/merge)
        var merged = node.RemoveLeaf(ref args, ref relatives, ref accessor);

        // Phase 2.5: Mark obsolete merged leaf + unlock leaf neighbors + leaf (version bumped by WriteUnlock)
        var retireEpoch = _segment.Manager.EpochManager.GlobalEpoch;
        if (merged)
        {
            Interlocked.Increment(ref _mergeCount);
            if (relatives.HasTrueLeftSibling)
            {
                // Current node was merged into its left sibling — mark current obsolete
                node.GetLatch(ref accessor).MarkObsolete();
                DeferredAdd(node.ChunkId, retireEpoch);
            }
            else if (relatives.HasTrueRightSibling && leafNext.IsValid)
            {
                // Right sibling was merged into current — mark right sibling obsolete
                leafNext.GetLatch(ref accessor).MarkObsolete();
                DeferredAdd(leafNext.ChunkId, retireEpoch);
            }
        }
        if (leafNext.IsValid)
        {
            leafNext.GetLatch(ref accessor).WriteUnlock();
        }
        if (leafPrev.IsValid)
        {
            leafPrev.GetLatch(ref accessor).WriteUnlock();
        }
        node.GetLatch(ref accessor).WriteUnlock();

        // Phase 3: Propagate merges upward through internal nodes
        while (ctx.Depth > 0 && merged)
        {
            ctx.Depth--;
            node = ctx.PathNodes[ctx.Depth];
            relatives = ctx.PathRelatives[ctx.Depth];

            // Lock siblings that HandleChildMerge might borrow from or merge with
            NodeWrapper leftSib = relatives.GetLeftSibling(ref accessor);
            NodeWrapper rightSib = relatives.GetRightSibling(ref accessor);
            if (leftSib.IsValid)
            {
                SpinWriteLock(leftSib.GetLatch(ref accessor));
            }
            if (rightSib.IsValid)
            {
                SpinWriteLock(rightSib.GetLatch(ref accessor));
            }

            merged = node.HandleChildMerge(ctx.PathChildIndices[ctx.Depth], ref relatives, ref accessor);

            // Mark obsolete internal node that was merged
            if (merged)
            {
                Interlocked.Increment(ref _mergeCount);
                if (relatives.HasTrueLeftSibling)
                {
                    // Current internal node merged into left sibling
                    node.GetLatch(ref accessor).MarkObsolete();
                    DeferredAdd(node.ChunkId, retireEpoch);
                }
                else if (relatives.HasTrueRightSibling && rightSib.IsValid)
                {
                    // Right sibling merged into current
                    rightSib.GetLatch(ref accessor).MarkObsolete();
                    DeferredAdd(rightSib.ChunkId, retireEpoch);
                }
            }

            // Unlock siblings + this path node
            if (rightSib.IsValid)
            {
                rightSib.GetLatch(ref accessor).WriteUnlock();
            }
            if (leftSib.IsValid)
            {
                leftSib.GetLatch(ref accessor).WriteUnlock();
            }
            node.GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 3.5: Unlock remaining path nodes above propagation level
        while (ctx.Depth > 0)
        {
            ctx.Depth--;
            ctx.PathNodes[ctx.Depth].GetLatch(ref accessor).WriteUnlock();
        }

        completed = true;
        return merged;
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

        // B-link: follow right links if key is beyond this leaf's range (stale parent separator).
        // Multiple hops may be needed when consecutive forceSplit operations left unpropagated separators.
        for (int hop = 0; hop < 16 && index < 0 && node.GetCount(ref accessor) > 0; hop++)
        {
            if (Comparer.Compare(key, node.GetLast(ref accessor).Key) <= 0)
            {
                break; // key is within this leaf's range
            }

            var next = node.GetNext(ref accessor);
            if (!next.IsValid)
            {
                break;
            }

            node = next;
            index = node.Find(key, Comparer, ref accessor);
        }

        return node;
    }

    /// <summary>
    /// Optimistic descent from root to leaf using OLC version validation.
    /// Returns (leafChunkId, leafVersion, keyIndex). leafChunkId=0 signals restart needed.
    /// Zero writes to shared state — readers never acquire any lock.
    /// </summary>
    private (int leafChunkId, int leafVersion, int keyIndex) OptimisticDescendToLeaf(TKey key, ref ChunkAccessor accessor, bool followRightLink = true)
    {
        var node = Root;
        if (!node.IsValid)
        {
            return (0, 0, -1);
        }

        var latch = node.GetLatch(ref accessor);
        int version = latch.ReadVersion();
        if (version == 0)
        {
            return (0, 0, -1); // locked or obsolete — restart
        }

        // Descend through internal nodes
        while (!node.GetIsLeaf(ref accessor))
        {
            var index = node.Find(key, Comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            // Read child pointer
            var child = node.GetChild(index, ref accessor);

            // Validate parent version after reading child pointer
            if (!latch.ValidateVersion(version))
            {
                return (0, 0, -1); // parent was modified — restart
            }

            // Move to child
            node = child;
            if (!node.IsValid)
            {
                return (0, 0, -1); // invalid child — restart
            }
            latch = node.GetLatch(ref accessor);
            version = latch.ReadVersion();
            if (version == 0)
            {
                return (0, 0, -1); // locked or obsolete — restart
            }
        }

        // At leaf: search for key
        var keyIndex = node.Find(key, Comparer, ref accessor);

        // B-link right-link following: if key not found and beyond this leaf's range, the leaf may have split and the parent separator is stale. Follow
        // right links until we find the leaf containing the key or exhaust the chain.
        // Multiple hops may be needed when consecutive forceSplit operations left several unpropagated separators in a row.
        // Disabled for inserts (followRightLink=false): version validation handles restarts.
        if (followRightLink && keyIndex < 0)
        {
            const int maxHops = 16;
            for (int hop = 0; hop < maxHops && node.GetCount(ref accessor) > 0; hop++)
            {
                if (Comparer.Compare(key, node.GetLast(ref accessor).Key) <= 0)
                {
                    break; // key is within this leaf's range (just not present)
                }

                if (!latch.ValidateVersion(version))
                {
                    return (0, 0, -1); // leaf modified — restart from root
                }

                var nextNode = node.GetNext(ref accessor);
                if (!nextNode.IsValid)
                {
                    break; // no right sibling — key doesn't exist
                }

                var nextLatch = nextNode.GetLatch(ref accessor);
                int nextVersion = nextLatch.ReadVersion();
                if (nextVersion == 0)
                {
                    return (0, 0, -1); // right sibling locked/obsolete — restart
                }

                node = nextNode;
                latch = nextLatch;
                version = nextVersion;
                keyIndex = node.Find(key, Comparer, ref accessor);

                if (keyIndex >= 0)
                {
                    break; // found the key
                }
            }
        }

        // Validate final leaf version after reading
        if (!latch.ValidateVersion(version))
        {
            return (0, 0, -1); // leaf was modified during search — restart
        }

        return (node.ChunkId, version, keyIndex);
    }

    /// <summary>
    /// Compound move for unique indexes: atomically removes the entry at <paramref name="oldKey"/> and inserts it under <paramref name="newKey"/>.
    /// Uses OLC: same-leaf fast path (single lock), different-leaf (dual lock by ChunkId order).
    /// Falls back to pessimistic after <see cref="MaxOptimisticRestarts"/>.
    /// </summary>
    /// <returns>True if the old key was found and moved; false if old key not found.</returns>
    public bool Move(TKey oldKey, TKey newKey, int value, ref ChunkAccessor accessor)
    {
        // Per-operation accessor for thread safety under OLC
        var opAccessor = _segment.CreateChunkAccessor(accessor.ChangeSet);
        try
        {
            for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
            {
                // Phase 1: Optimistic descent for both keys
                var (oldLeafId, oldVersion, oldKeyIndex) = OptimisticDescendToLeaf(oldKey, ref opAccessor);
                if (oldLeafId == 0)
                {
                    if (IsEmpty())
                    {
                        return false;
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (oldKeyIndex < 0)
                {
                    // oldKey not found — validate this is not a stale read
                    var checkLeaf = _storage.LoadNode(oldLeafId);
                    if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(oldVersion))
                    {
                        return false; // genuinely not found
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue; // stale read, restart
                }

                var (newLeafId, newVersion, newKeyIndex) = OptimisticDescendToLeaf(newKey, ref opAccessor);
                if (newLeafId == 0)
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue; // restart
                }

                // Phase 2: Lock and mutate
                if (oldLeafId == newLeafId)
                {
                    // Same-leaf fast path: single WriteLock, net count unchanged
                    var leaf = _storage.LoadNode(oldLeafId);
                    var latch = leaf.GetLatch(ref opAccessor);
                    if (!latch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue; // contended, restart
                    }

                    // Validate version (detects concurrent modification between our read and lock)
                    if (!latch.ValidateVersion(oldVersion | 1))
                    {
                        latch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Re-find under lock (indices may have shifted)
                    var oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        latch.WriteUnlock();
                        return false; // old key gone
                    }

                    // Check newKey doesn't already exist BEFORE modifying anything
                    var ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    if (ni >= 0)
                    {
                        latch.WriteUnlock();
                        return false; // newKey already exists — no modification
                    }

                    // Remove old entry and insert new entry
                    leaf.RemoveAtInternal(oi, ref opAccessor);
                    // Re-find insertion point after removal (indices shifted)
                    ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    ni = ~ni;
                    leaf.Insert(ni, new KeyValueItem(newKey, value), ref opAccessor);

                    latch.WriteUnlock();
                    return true;
                }
                else
                {
                    // Different-leaf path: lock in ChunkId order to prevent deadlocks
                    var firstId = Math.Min(oldLeafId, newLeafId);
                    var secondId = Math.Max(oldLeafId, newLeafId);
                    var firstVersion = oldLeafId == firstId ? oldVersion : newVersion;
                    var secondVersion = oldLeafId == firstId ? newVersion : oldVersion;

                    var firstLeaf = _storage.LoadNode(firstId);
                    var secondLeaf = _storage.LoadNode(secondId);

                    var firstLatch = firstLeaf.GetLatch(ref opAccessor);
                    if (!firstLatch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var secondLatch = secondLeaf.GetLatch(ref opAccessor);
                    if (!secondLatch.TryWriteLock())
                    {
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Validate both versions
                    if (!firstLatch.ValidateVersion(firstVersion | 1) || !secondLatch.ValidateVersion(secondVersion | 1))
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Identify which is old and which is new
                    var oldLeaf = oldLeafId == firstId ? firstLeaf : secondLeaf;
                    var newLeaf = oldLeafId == firstId ? secondLeaf : firstLeaf;

                    // Safety check: if newLeaf is full (insert would overflow) or oldLeaf would underflow, bail to pessimistic which handles structural
                    // modifications properly
                    if (newLeaf.GetIsFull(ref opAccessor) || !oldLeaf.GetIsHalfFull(ref opAccessor))
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        break; // fall to pessimistic
                    }

                    // Re-find under locks
                    var oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        return false;
                    }

                    var ni = newLeaf.Find(newKey, Comparer, ref opAccessor);
                    if (ni >= 0)
                    {
                        // newKey already exists — fail without modification
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        return false;
                    }
                    ni = ~ni;

                    // Remove from old, insert into new
                    oldLeaf.RemoveAtInternal(oi, ref opAccessor);
                    newLeaf.Insert(ni, new KeyValueItem(newKey, value), ref opAccessor);

                    secondLatch.WriteUnlock();
                    firstLatch.WriteUnlock();
                    return true;
                }
            }

            // Pessimistic fallback: full exclusive lock
            Interlocked.Increment(ref _pessimisticFallbacks);
            return MovePessimistic(oldKey, newKey, value, ref opAccessor);
        }
        finally
        {
            opAccessor.CommitChanges();
            opAccessor.Dispose();
        }
    }

    /// <summary>
    /// Pessimistic fallback for Move: traverses, removes oldKey, inserts newKey.
    /// No global lock — concurrency is handled by per-node OLC latches in Remove/Insert.
    /// </summary>
    private bool MovePessimistic(TKey oldKey, TKey newKey, int value, ref ChunkAccessor accessor)
    {
        try
        {
            var oldLeaf = FindLeaf(oldKey, out var oldIndex, ref accessor);
            if (!oldLeaf.IsValid || oldIndex < 0)
            {
                return false;
            }

            // Check that newKey doesn't already exist
            var newLeaf = FindLeaf(newKey, out var newIndex, ref accessor);
            if (newLeaf.IsValid && newIndex >= 0)
            {
                return false; // newKey already exists
            }

            // Remove old entry — use RemoveArguments/RemoveCore for proper structural handling
            var removeArgs = new RemoveArguments(oldKey, Comparer, ref accessor);
            RemoveCorePessimistic(ref removeArgs);
            if (!removeArgs.Removed)
            {
                return false;
            }

            // Insert new entry
            var insertArgs = new InsertArguments(newKey, value, Comparer, ref accessor);
            AddOrUpdateCorePessimistic(ref insertArgs);
            SyncHeader(ref accessor);
            return true;
        }
        finally
        {
            DeferredReclaim();
        }
    }

    /// <summary>
    /// Compound move for AllowMultiple indexes: removes <paramref name="elementId"/>/<paramref name="value"/> from <paramref name="oldKey"/>'s buffer and
    /// appends <paramref name="value"/> under <paramref name="newKey"/>.
    /// Returns the new element ID and both HEAD buffer IDs for inline TAIL tracking.
    /// </summary>
    public int MoveValue(TKey oldKey, TKey newKey, int elementId, int value,
        ref ChunkAccessor accessor, out int oldHeadBufferId, out int newHeadBufferId, bool preserveEmptyBuffer = false)
    {
        // Per-operation accessor for thread safety under OLC
        var opAccessor = _segment.CreateChunkAccessor(accessor.ChangeSet);
        try
        {
            for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
            {
                // Phase 1: Optimistic descent for both keys
                var (oldLeafId, oldVersion, oldKeyIndex) = OptimisticDescendToLeaf(oldKey, ref opAccessor);
                if (oldLeafId == 0)
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (oldKeyIndex < 0)
                {
                    var checkLeaf = _storage.LoadNode(oldLeafId);
                    if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(oldVersion))
                    {
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1; // old key genuinely not found
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                var (newLeafId, newVersion, newKeyIndex) = OptimisticDescendToLeaf(newKey, ref opAccessor);
                if (newLeafId == 0)
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // Phase 2: Lock and mutate
                if (oldLeafId == newLeafId)
                {
                    var leaf = _storage.LoadNode(oldLeafId);
                    var latch = leaf.GetLatch(ref opAccessor);
                    if (!latch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!latch.ValidateVersion(oldVersion | 1))
                    {
                        latch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Re-find oldKey under lock
                    var oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        latch.WriteUnlock();
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1;
                    }

                    // Remove element from old buffer
                    var oldBufferId = leaf.GetItem(oi, ref opAccessor).Value;
                    var res = _storage.RemoveFromBuffer(oldBufferId, elementId, value, ref opAccessor);
                    oldHeadBufferId = oldBufferId;

                    if (res == -1)
                    {
                        latch.WriteUnlock();
                        newHeadBufferId = -1;
                        return -1; // element not found in buffer
                    }

                    // Find or prepare newKey
                    var ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    int newBufferId;
                    int newElementId;
                    if (ni >= 0)
                    {
                        // newKey exists — append to its buffer
                        newBufferId = leaf.GetItem(ni, ref opAccessor).Value;
                        newElementId = _storage.Append(newBufferId, value, ref opAccessor);
                    }
                    else
                    {
                        // newKey doesn't exist — need to insert a new key entry
                        // If leaf is full and we won't reclaim a slot, bail to pessimistic.
                        // We can only reclaim when res==0 (old buffer empty) AND !preserveEmptyBuffer.
                        if (leaf.GetIsFull(ref opAccessor) && (res != 0 || preserveEmptyBuffer))
                        {
                            // Undo the buffer removal — re-add the element
                            _storage.Append(oldBufferId, value, ref opAccessor);
                            latch.WriteUnlock();
                            break; // fall to pessimistic
                        }

                        newBufferId = _storage.CreateBuffer(ref opAccessor);
                        newElementId = _storage.Append(newBufferId, value, ref opAccessor);
                        ni = ~ni;
                        // If old buffer empty (res==0) and not preserving, remove old key first to free a slot
                        if (res == 0 && !preserveEmptyBuffer)
                        {
                            oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                            if (oi >= 0)
                            {
                                leaf.RemoveAtInternal(oi, ref opAccessor);
                                _storage.DeleteBuffer(oldBufferId, ref opAccessor);
                                Interlocked.Decrement(ref _count);
                            }
                            // Re-find insertion point after removal
                            ni = leaf.Find(newKey, Comparer, ref opAccessor);
                            ni = ~ni;
                            res = -2; // sentinel: old key already cleaned up
                        }
                        leaf.Insert(ni, new KeyValueItem(newKey, newBufferId), ref opAccessor);
                        Interlocked.Increment(ref _count);
                    }
                    newHeadBufferId = newBufferId;

                    // If old buffer is now empty and not yet cleaned up, remove the BTree entry for oldKey
                    if (res == 0 && !preserveEmptyBuffer)
                    {
                        // Re-find oldKey (index may have shifted after insert)
                        oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                        if (oi >= 0)
                        {
                            leaf.RemoveAtInternal(oi, ref opAccessor);
                            _storage.DeleteBuffer(oldBufferId, ref opAccessor);
                            Interlocked.Decrement(ref _count);
                        }
                    }

                    latch.WriteUnlock();
                    SyncHeader(ref opAccessor);
                    return newElementId;
                }
                else
                {
                    // Different-leaf path: lock in ChunkId order
                    var firstId = Math.Min(oldLeafId, newLeafId);
                    var secondId = Math.Max(oldLeafId, newLeafId);
                    var firstVersion = oldLeafId == firstId ? oldVersion : newVersion;
                    var secondVersion = oldLeafId == firstId ? newVersion : oldVersion;

                    var firstLeaf = _storage.LoadNode(firstId);
                    var secondLeaf = _storage.LoadNode(secondId);

                    var firstLatch = firstLeaf.GetLatch(ref opAccessor);
                    if (!firstLatch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var secondLatch = secondLeaf.GetLatch(ref opAccessor);
                    if (!secondLatch.TryWriteLock())
                    {
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!firstLatch.ValidateVersion(firstVersion | 1) || !secondLatch.ValidateVersion(secondVersion | 1))
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var oldLeaf = oldLeafId == firstId ? firstLeaf : secondLeaf;
                    var newLeaf = oldLeafId == firstId ? secondLeaf : firstLeaf;

                    // Pre-check: if newLeaf is full and newKey doesn't exist, bail to pessimistic (we'd need to insert a new entry which could cause overflow)
                    if (newLeaf.GetIsFull(ref opAccessor))
                    {
                        var preNi = newLeaf.Find(newKey, Comparer, ref opAccessor);
                        if (preNi < 0)
                        {
                            secondLatch.WriteUnlock();
                            firstLatch.WriteUnlock();
                            break; // fall to pessimistic
                        }
                    }

                    // Remove element from old buffer
                    var oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1;
                    }

                    var oldBufferId = oldLeaf.GetItem(oi, ref opAccessor).Value;
                    var res = _storage.RemoveFromBuffer(oldBufferId, elementId, value, ref opAccessor);
                    oldHeadBufferId = oldBufferId;

                    if (res == -1)
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        newHeadBufferId = -1;
                        return -1;
                    }

                    // Append to new buffer
                    var ni = newLeaf.Find(newKey, Comparer, ref opAccessor);
                    int newBufferId;
                    int newElementId;
                    if (ni >= 0)
                    {
                        newBufferId = newLeaf.GetItem(ni, ref opAccessor).Value;
                        newElementId = _storage.Append(newBufferId, value, ref opAccessor);
                    }
                    else
                    {
                        newBufferId = _storage.CreateBuffer(ref opAccessor);
                        newElementId = _storage.Append(newBufferId, value, ref opAccessor);
                        ni = ~ni;
                        newLeaf.Insert(ni, new KeyValueItem(newKey, newBufferId), ref opAccessor);
                        Interlocked.Increment(ref _count);
                    }
                    newHeadBufferId = newBufferId;

                    // If old buffer is now empty, remove the BTree entry
                    if (res == 0 && !preserveEmptyBuffer)
                    {
                        oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                        if (oi >= 0)
                        {
                            oldLeaf.RemoveAtInternal(oi, ref opAccessor);
                            _storage.DeleteBuffer(oldBufferId, ref opAccessor);
                            Interlocked.Decrement(ref _count);
                        }
                    }

                    secondLatch.WriteUnlock();
                    firstLatch.WriteUnlock();
                    SyncHeader(ref opAccessor);
                    return newElementId;
                }
            }

            // Pessimistic fallback
            Interlocked.Increment(ref _pessimisticFallbacks);
            return MoveValuePessimistic(oldKey, newKey, elementId, value, ref opAccessor, out oldHeadBufferId, out newHeadBufferId, preserveEmptyBuffer);
        }
        finally
        {
            opAccessor.CommitChanges();
            opAccessor.Dispose();
        }
    }

    /// <summary>
    /// Pessimistic fallback for MoveValue: removes element from old buffer,
    /// appends to new buffer, handles empty-buffer cleanup.
    /// No global lock — concurrency is handled by per-node OLC latches in Remove/Insert.
    /// </summary>
    private int MoveValuePessimistic(TKey oldKey, TKey newKey, int elementId, int value, ref ChunkAccessor accessor, out int oldHeadBufferId, 
        out int newHeadBufferId, bool preserveEmptyBuffer = false)
    {
        try
        {
            var oldLeaf = FindLeaf(oldKey, out var oldIndex, ref accessor);
            if (!oldLeaf.IsValid || oldIndex < 0)
            {
                oldHeadBufferId = -1;
                newHeadBufferId = -1;
                return -1;
            }

            // Remove element from old buffer
            var oldBufferId = oldLeaf.GetItem(oldIndex, ref accessor).Value;
            var res = _storage.RemoveFromBuffer(oldBufferId, elementId, value, ref accessor);
            oldHeadBufferId = oldBufferId;

            if (res == -1)
            {
                newHeadBufferId = -1;
                return -1;
            }

            // Append to new key's buffer
            var newLeaf = FindLeaf(newKey, out var newIndex, ref accessor);
            int newBufferId;
            int newElementId;
            if (newLeaf.IsValid && newIndex >= 0)
            {
                // newKey exists — append to its buffer
                newBufferId = newLeaf.GetItem(newIndex, ref accessor).Value;
                newElementId = _storage.Append(newBufferId, value, ref accessor);
            }
            else
            {
                // newKey doesn't exist — create buffer and insert via AddOrUpdateCore
                newBufferId = _storage.CreateBuffer(ref accessor);
                newElementId = _storage.Append(newBufferId, value, ref accessor);
                var insertArgs = new InsertArguments(newKey, newBufferId, Comparer, ref accessor);
                AddOrUpdateCorePessimistic(ref insertArgs);
            }
            newHeadBufferId = newBufferId;

            // If old buffer is now empty, remove the BTree entry
            if (res == 0 && !preserveEmptyBuffer)
            {
                var removeArgs = new RemoveArguments(oldKey, Comparer, ref accessor);
                RemoveCorePessimistic(ref removeArgs);
                if (removeArgs.Removed)
                {
                    _storage.DeleteBuffer(oldBufferId, ref accessor);
                }
            }

            SyncHeader(ref accessor);
            return newElementId;
        }
        finally
        {
            DeferredReclaim();
        }
    }

    #endregion
}

#endregion