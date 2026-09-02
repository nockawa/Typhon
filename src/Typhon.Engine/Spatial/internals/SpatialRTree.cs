using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Maximum tree depth supported. 16 is generous — 100K entities in 2D-f32 (fanout 20) need depth 4-5.
/// </summary>
internal static class SpatialRTreeConstants
{
    internal const int MaxTreeDepth = 16;

    /// <summary>Inline capacity of the ray query's priority queue — the allocation-free fast path before it spills to pooled arrays.</summary>
    internal const int RayHeapInlineCapacity = 64;

    /// <summary>
    /// Hard ceiling on the ray priority queue after spilling (~192 KB of pooled backing at 12 B/entry).
    /// </summary>
    /// <remarks>
    /// Not derived from <see cref="MaxTreeDepth"/>: unlike a descent path, the ray frontier is not depth-bounded — a ray that grazes many subtrees at the same
    /// entry distance holds them all pending at once. This bound exists only so a degenerate or cyclic tree cannot grow the heap without limit; reaching it is
    /// recorded through <see cref="SpatialRTreeDiagnostics.RecordDfsStackOverflow"/> rather than silently dropping children (#589).
    /// </remarks>
    internal const int MaxRayHeapCapacity = 1 << 14;
}

/// <summary>Stack-allocated buffer for path recording during descent.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathChunkIdBuffer
{
    private int _element0;
}

/// <summary>Stack-allocated buffer for child indices along the descent path.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathChildIndexBuffer
{
    private int _element0;
}

/// <summary>Stack-allocated buffer for OLC versions along the descent path.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathVersionBuffer
{
    private int _element0;
}

/// <summary>
/// Stack-allocated traversal path for a single R-Tree mutation. Records (chunkId, childIndex, olcVersion) at each level during descent, enabling parent
/// access during split propagation and ancestor MBR refit.
/// </summary>
internal ref struct DescentPath
{
    public PathChunkIdBuffer ChunkIds;
    public PathChildIndexBuffer ChildIndices;
    public PathVersionBuffer Versions;
    public int Depth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int chunkId, int childIndex, int version)
    {
        ChunkIds[Depth] = chunkId;
        ChildIndices[Depth] = childIndex;
        Versions[Depth] = version;
        Depth++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => Depth = 0;
}

/// <summary>
/// Page-backed wide R-Tree for spatial indexing. Uses SOA node layout driven by <see cref="SpatialNodeDescriptor"/>. All four variants (2D/3D × f32/f64) are
/// served by a single implementation — descriptor fields are JIT-constant after readonly promotion.
/// </summary>
/// <remarks>
/// Coordinates flow through the tree as <c>double</c> arrays of length <c>CoordCount</c>, ordered as [min0, min1, ..., max0, max1, ...]
/// (e.g., [MinX, MinY, MaxX, MaxY] for 2D). The SOA read/write helpers in <see cref="SpatialNodeHelper"/> handle float↔double conversion at the storage boundary.
/// </remarks>
internal unsafe partial class SpatialRTree<TStore> where TStore : struct, IPageStore
{
    private readonly ChunkBasedSegment<TStore> _segment;
    private readonly SpatialNodeDescriptor _desc;
    private readonly SpatialVariant _variant;

    // Tree metadata (persisted in chunk 0)
    private int _rootChunkId;
    private int _nodeCount;
    private int _entityCount;
    private int _depth;

    /// <summary>Monotonic counter incremented on every Insert/Remove. Used by trigger system for static cache invalidation.</summary>
    private int _mutationVersion;

    /// <summary>Lock protecting SyncMetadata writes to chunk 0 against concurrent mutations.</summary>
    private readonly Lock _metadataLock = new();

    /// <summary>
    /// Back-pointer CBS for O(1) leaf lookup. When set, split scatter updates back-pointers directly
    /// using componentChunkIds stored in leaf entries. Null for standalone unit tests.
    /// </summary>
    internal ChunkBasedSegment<TStore> BackPointerSegment;

    /// <summary>
    /// Payload-indexed back-pointer array — the cluster-tree counterpart of <see cref="BackPointerSegment"/> (#872 step 9). Indexed by the payload id passed
    /// to <see cref="Insert(long,System.ReadOnlySpan{double},ref ChunkAccessor{TStore},ChangeSet,uint)"/>; each element is a packed
    /// <c>(leafChunkId, slotIndex)</c> made by <see cref="PackHandle"/>. Null for every tree that does not use handles.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all.</b> A handle returned by <c>Insert</c> is only valid until the leaf splits. <c>InsertWithSplit</c> scatters BOTH
    /// halves through the overlap-minimising permutation, so an entry that stays in its original leaf still moves slot — measured at
    /// <c>SharedSegmentRTreeHarnessTests.Claim1</c>: <b>43 of 60 handles wrong after splits, 20 of them pointing outside their leaf's live range</b>. A stale
    /// handle makes <c>C5</c>'s escape-bound update write one cluster's bound into another cluster's slot, which is <c>CA-01</c> violated in both directions
    /// and an <c>SQ-01</c> false negative with no exception anywhere near it.</para>
    /// <para><b>Why an array rather than <see cref="BackPointerSegment"/>.</b> That path opens a <see cref="ChunkAccessor{TStore}"/> and does a paged lookup
    /// and write per entry. The cluster payload IS a cluster chunk id and <c>ArchetypeClusterState.ClusterSpatialIndexSlot</c> is already an <c>int[]</c>
    /// indexed by exactly that, so the fix-up is one array store folded into a scatter loop that is already writing four fields per entry.</para>
    /// <para><b>The array must be large enough BEFORE any mutation, and must not be resized while one is in flight.</b> It is indexed by payload id, so it
    /// has to cover the largest live one. A resize is not merely unsynchronised — it cannot be made safe by a writer-side lock alone:
    /// <see cref="ScatterLeafEntries"/> reads the field once and writes through that reference, so an <c>Array.Resize</c> concurrent with a split publishes
    /// a NEW array while the scatter fills the abandoned one. The lost write is a stale handle, which is the exact defect this array exists to remove.
    /// <c>ArchetypeClusterState.EnsureClusterSpatialIndexSlotCapacity</c> is lock-free today, so the contract is the caller's to keep: size it, attach it,
    /// and do not grow it under a live fence. <see cref="ThrowPayloadOutOfRange"/> makes a violation loud rather than silent.</para>
    /// </remarks>
    internal int[] PayloadBackPointers;

    /// <summary>Pack a <c>(leafChunkId, slotIndex)</c> pair into one <see cref="int"/>.</summary>
    /// <remarks>
    /// <para>Packing into an <c>int</c> rather than widening to a <c>long</c> keeps <c>ClusterSpatialIndexSlot</c> the size it already is: one array per
    /// archetype sized by cluster count, so the width is paid per cluster forever. <c>-1</c> stays the "not in any tree" sentinel it already was, and is never
    /// a valid packed handle because leaf chunk 0 is the segment's reserved null chunk.</para>
    /// <para><b>Five bits, not four, and the difference is not comfort.</b> Leaf capacities computed from
    /// <see cref="SpatialNodeDescriptor"/>'s arithmetic are <c>R2Df32</c> 15, <c>R3Df32</c> 11, <c>R2Df64</c> 9, <c>R3Df64</c> 11 — so four bits fits the
    /// widest variant with <i>exactly zero</i> headroom. A node stride change, a dropped header field, or the cluster-specific descriptor already discussed
    /// as a follow-up (dropping the unused 8-byte entity id would take <c>R2Df32</c> to 20) each push it over, and the failure is a silently truncated slot
    /// index — a handle naming the wrong entry, which is precisely the defect this whole mechanism exists to remove. Five bits costs one bit of chunk id,
    /// leaving 67 M chunks per segment, and <see cref="AssertHandleCapacity"/> makes the remaining margin a startup failure rather than a silent wrap.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PackHandle(int leafChunkId, int slotIndex)
    {
        // Both fields are guarded because both can silently wrap. The slot is bounded by LeafCapacity, which AssertHandleCapacity pins at construction;
        // the chunk id is bounded only by how big the segment grew, and shifting it past bit 31 sets the sign bit, after which >>> returns garbage.
        if ((uint)slotIndex > HandleSlotMask || (uint)leafChunkId > MaxHandleChunkId)
        {
            ThrowHandleOutOfRange(leafChunkId, slotIndex);
        }
        return (leafChunkId << HandleSlotBits) | slotIndex;
    }

    /// <summary>Unpack a handle made by <see cref="PackHandle"/>. Passing <see cref="NullHandle"/> is a caller bug — use <see cref="IsNullHandle"/> first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int leafChunkId, int slotIndex) UnpackHandle(int handle) => (handle >>> HandleSlotBits, handle & HandleSlotMask);

    /// <summary>
    /// The "this payload is in no tree" handle. Deliberately <c>-1</c>, matching the sentinel <c>ArchetypeClusterState.ClusterSpatialIndexSlot</c> already
    /// used before it held handles — a second spelling of "absent" is how one of the two gets forgotten.
    /// </summary>
    /// <remarks>
    /// It is not a decodable handle: <c>UnpackHandle(-1)</c> yields <c>(0x07FFFFFF, 31)</c>, which is a perfectly plausible-looking leaf and slot. Test with
    /// <see cref="IsNullHandle"/> before unpacking; there is no in-band way to tell the two apart afterwards.
    /// </remarks>
    internal const int NullHandle = -1;

    /// <summary>Whether a stored handle means "not in any tree".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNullHandle(int handle) => handle < 0;

    /// <summary>Fail loudly if this variant's leaf capacity cannot be addressed by <see cref="PackHandle"/>'s slot field.</summary>
    /// <remarks>
    /// Always compiled, and called from the constructor rather than left to a test: the descriptor arithmetic is derived from the stride at runtime, so a
    /// change that overflows the field would otherwise produce corrupted handles in Release with nothing to point at.
    /// </remarks>
    private void AssertHandleCapacity()
    {
        if (_desc.LeafCapacity > HandleSlotMask)
        {
            ThrowHandleCapacityExceeded(_variant, _desc.LeafCapacity);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowHandleCapacityExceeded(SpatialVariant variant, int leafCapacity) =>
        throw new InvalidOperationException(
            $"Spatial variant {variant} has a leaf capacity of {leafCapacity}, which does not fit PackHandle's {HandleSlotBits}-bit slot field "
            + $"(max {HandleSlotMask}). Widen HandleSlotBits — the alternative is a silently truncated slot index in every payload back-pointer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowHandleOutOfRange(int leafChunkId, int slotIndex) =>
        throw new ArgumentOutOfRangeException(nameof(leafChunkId),
            $"Cannot pack handle (leaf {leafChunkId}, slot {slotIndex}): the slot field holds 0..{HandleSlotMask} and the chunk field 0..{MaxHandleChunkId}. "
            + "Packing anyway would produce a handle naming a different entry, which is silent.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPayloadOutOfRange(long payloadId, int length) =>
        throw new ArgumentOutOfRangeException(nameof(payloadId),
            $"Payload {payloadId} is outside PayloadBackPointers (length {length}). The array must cover every live payload id BEFORE the mutation that "
            + "relocates it; skipping the write would leave the payload's handle naming whatever entry now occupies its old slot.");

    private const int HandleSlotBits = 5;
    private const int HandleSlotMask = (1 << HandleSlotBits) - 1;

    /// <summary>Largest leaf chunk id <see cref="PackHandle"/> can hold without shifting into the sign bit.</summary>
    private const int MaxHandleChunkId = int.MaxValue >> HandleSlotBits;

    /// <summary>Record a payload's new position, or fail loudly if the array cannot hold it. See <see cref="PayloadBackPointers"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePayloadHandle(int[] payloadBackPointers, long payloadId, int handle)
    {
        if ((ulong)payloadId >= (ulong)payloadBackPointers.Length)
        {
            ThrowPayloadOutOfRange(payloadId, payloadBackPointers.Length);
        }
        payloadBackPointers[payloadId] = handle;
    }

    // Chunk 0 metadata layout
    private const int MetaRootOffset = 0;
    private const int MetaNodeCountOffset = 4;
    private const int MetaEntityCountOffset = 8;
    private const int MetaDepthOffset = 12;
    private const int MetaVariantOffset = 16;

    internal ChunkBasedSegment<TStore> Segment => _segment;
    internal SpatialNodeDescriptor Descriptor => _desc;
    internal SpatialVariant Variant => _variant;
    internal int RootChunkId => _rootChunkId;
    internal int NodeCount => _nodeCount;
    internal int EntityCount => _entityCount;
    internal int Depth => _depth;
    internal int MutationVersion => _mutationVersion;

    /// <summary>
    /// Create a new R-Tree or load an existing one from the segment.
    /// </summary>
    /// <param name="segment">Pre-allocated CBS with stride matching the descriptor's Stride</param>
    /// <param name="variant">Spatial variant (determines descriptor and node layout)</param>
    /// <param name="load">True to load existing tree from segment, false to create new</param>
    /// <param name="changeSet">ChangeSet for WAL participation (null for non-WAL)</param>
    internal SpatialRTree(ChunkBasedSegment<TStore> segment, SpatialVariant variant, bool load = false, ChangeSet changeSet = null)
    {
        _segment = segment;
        _variant = variant;
        _desc = SpatialNodeDescriptor.ForVariant(variant);
        AssertHandleCapacity();

        var guard = EpochGuard.Enter(_segment.Store.EpochManager);
        try
        {
            if (!load)
            {
                // Reserve chunk 0 for metadata BEFORE creating our accessor (ReserveChunk with clearContent creates its own internal accessor)
                if (!_segment.IsChunkAllocated(0))
                {
                    _segment.ReserveChunk(0, true, changeSet);
                }
            }

            var accessor = _segment.CreateChunkAccessor(changeSet);
            try
            {
                if (!load)
                {
                    _rootChunkId = AllocNode(true, 0, ref accessor, changeSet);
                    _nodeCount = 1;
                    _entityCount = 0;
                    _depth = 1;
                    SyncMetadata(ref accessor);
                }
                else
                {
                    LoadMetadata(ref accessor);
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>Allocate a new node, initialize its header fields.</summary>
    /// <remarks>
    /// Allocates WITHOUT clearContent to avoid creating a nested ChunkAccessor inside AllocateChunk (the caller already has an active accessor).
    /// We zero and initialize the header manually.
    /// </remarks>
    private int AllocNode(bool isLeaf, int parentChunkId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet = null)
    {
        int chunkId = _segment.AllocateChunk(false, changeSet);
        byte* nodeBase = accessor.GetChunkAddress(chunkId, true);

        // Zero the entire chunk (stride bytes)
        new Span<byte>(nodeBase, _desc.Stride).Clear();

        // Initialize header
        // OlcVersion must start at version >= 1 (not 0) because ReadVersion() returns 0 as "locked/obsolete"
        // Set to 0b100 = 4 (version=1, lock=0, obsolete=0)
        *(int*)nodeBase = 4;
        SpatialNodeHelper.SetCount(nodeBase, 0);
        SpatialNodeHelper.SetIsLeaf(nodeBase, isLeaf);
        SpatialNodeHelper.SetParentChunkId(nodeBase, parentChunkId);
        return chunkId;
    }

    /// <summary>Write tree metadata to chunk 0. Lock-protected against concurrent mutations.</summary>
    private void SyncMetadata(ref ChunkAccessor<TStore> accessor)
    {
        lock (_metadataLock)
        {
            byte* meta = accessor.GetChunkAddress(0, true);
            *(int*)(meta + MetaRootOffset) = _rootChunkId;
            *(int*)(meta + MetaNodeCountOffset) = _nodeCount;
            *(int*)(meta + MetaEntityCountOffset) = _entityCount;
            *(int*)(meta + MetaDepthOffset) = _depth;
            *(byte*)(meta + MetaVariantOffset) = (byte)_variant;
        }
    }

    /// <summary>Load tree metadata from chunk 0.</summary>
    private void LoadMetadata(ref ChunkAccessor<TStore> accessor)
    {
        byte* meta = accessor.GetChunkAddress(0);
        _rootChunkId = *(int*)(meta + MetaRootOffset);
        _nodeCount = *(int*)(meta + MetaNodeCountOffset);
        _entityCount = *(int*)(meta + MetaEntityCountOffset);
        _depth = *(int*)(meta + MetaDepthOffset);
    }

    // ── OLC helpers ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OlcLatch GetLatch(byte* nodeBase) => new(ref SpatialNodeHelper.OlcVersionRef(nodeBase));

    /// <summary>Spin-wait to acquire write lock on a node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SpinWriteLock(byte* nodeBase, out OlcLatch latch)
    {
        latch = GetLatch(nodeBase);
        SpinWait spin = default;
        while (!latch.TryWriteLock())
        {
            spin.SpinOnce();
        }
    }

    // ── Category mask helpers ────────────────────────────────────────────────

    /// <summary>
    /// Recompute an internal node's UnionCategoryMask as the bitwise OR of all children's UnionCategoryMasks.
    /// Must be called after RefitInternalMBR whenever category masks may have changed.
    /// </summary>
    private void RefitInternalUnionMask(byte* nodeBase, ref ChunkAccessor<TStore> accessor)
    {
        int count = SpatialNodeHelper.GetCount(nodeBase);
        uint unionMask = 0;
        for (int i = 0; i < count; i++)
        {
            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
            byte* childBase = accessor.GetChunkAddress(childId);
            unionMask |= SpatialNodeHelper.ReadUnionCategoryMask(childBase, _desc);
        }
        SpatialNodeHelper.WriteUnionCategoryMask(nodeBase, unionMask, _desc);
    }

    /// <summary>
    /// Update the category mask of a leaf entry in-place and refit union masks up the ancestor chain.
    /// Called via back-pointer for runtime category changes (e.g., entity dies → clear Alive bit).
    /// </summary>
    internal void SetEntryCategoryMask(int leafChunkId, int slotIndex, uint mask, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);
        SpinWriteLock(leafBase, out var latch);
        SpatialNodeHelper.WriteLeafCategoryMask(leafBase, slotIndex, mask, _desc);
        SpatialNodeHelper.RefitLeafMBR(leafBase, _desc); // recomputes leaf union mask
        latch.WriteUnlock();
        RefitAncestorsBottomUp(leafChunkId, ref accessor);
    }

    // ── Ancestor refit (bottom-up via ParentChunkId chain) ──────────────────

    /// <summary>
    /// Walk up from a node to the root via ParentChunkId, refitting each ancestor's MBR and UnionCategoryMask.
    /// Used after remove and other mutations that don't have a recorded descent path.
    /// </summary>
    private void RefitAncestorsBottomUp(int startChunkId, ref ChunkAccessor<TStore> accessor)
    {
        int currentChunkId = startChunkId;
        while (true)
        {
            byte* currentBase = accessor.GetChunkAddress(currentChunkId);

            // OLC-validate the child read to avoid chasing a stale parent pointer after concurrent split
            var childLatch = GetLatch(currentBase);
            int childVersion = childLatch.ReadVersion();
            int parentChunkId = SpatialNodeHelper.GetParentChunkId(currentBase);
            if (!childLatch.ValidateVersion(childVersion))
            {
                // Child was concurrently modified (split changed parent pointer) — re-read
                continue;
            }

            if (parentChunkId == 0)
            {
                break;
            }

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Refit the parent's internal entry for this child
            int parentCount = SpatialNodeHelper.GetCount(parentBase);
            for (int i = 0; i < parentCount; i++)
            {
                if (SpatialNodeHelper.ReadInternalChildId(parentBase, i, _desc) == currentChunkId)
                {
                    // Update this child's MBR in the parent
                    for (int c = 0; c < _desc.CoordCount; c++)
                    {
                        SpatialNodeHelper.WriteInternalCoord(parentBase, i, c, SpatialNodeHelper.ReadNodeMBRCoord(currentBase, c, _desc), _desc);
                    }
                    break;
                }
            }

            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
            parentLatch.WriteUnlock();

            currentChunkId = parentChunkId;
        }
    }

    /// <summary>
    /// Walk the recorded descent path upward, refitting each ancestor's internal entry
    /// for the child that was modified, then recomputing the ancestor's own NodeMBR and UnionCategoryMask.
    /// </summary>
    private void RefitAncestors(ref DescentPath path, ref ChunkAccessor<TStore> accessor)
    {
        for (int level = path.Depth - 1; level >= 0; level--)
        {
            int parentChunkId = path.ChunkIds[level];
            int childIdx = path.ChildIndices[level];

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Read child's current NodeMBR and update the parent's entry for that child
            int childChunkId = SpatialNodeHelper.ReadInternalChildId(parentBase, childIdx, _desc);
            byte* childBase = accessor.GetChunkAddress(childChunkId);
            for (int c = 0; c < _desc.CoordCount; c++)
            {
                SpatialNodeHelper.WriteInternalCoord(parentBase, childIdx, c, SpatialNodeHelper.ReadNodeMBRCoord(childBase, c, _desc), _desc);
            }

            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
            parentLatch.WriteUnlock();
        }
    }

    /// <summary>
    /// Read the fat AABB coordinates of a leaf entry at a known position. Used by SpatialMaintainer for containment check.
    /// </summary>
    internal void ReadLeafCoords(int leafChunkId, int slotIndex, Span<double> coords, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId);
        SpatialNodeHelper.ReadLeafEntryCoords(leafBase, slotIndex, coords, _desc);
    }
}
