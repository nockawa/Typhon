using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One cell's R-Tree over cluster AABBs — the structure <c>C4</c> specifies in place of <see cref="CellSpatialIndex"/>'s linear scan (#872 step 9).
/// </summary>
/// <remarks>
/// <para><b>One <see cref="SpatialRTree{TStore}"/> instance per cell, over a segment shared by every cell of one archetype.</b> The design proposed detaching
/// the tree's metadata from chunk 0 and threading a <c>ref</c> root through twelve methods so a single instance could serve every cell. That was measured and
/// found unnecessary: <c>SharedSegmentRTreeHarnessTests.Claim2</c> drives two ordinary trees over one segment through 120 interleaved inserts, splits both,
/// and each returns exactly its own payloads. The four metadata values are already per-INSTANCE fields; chunk 0 is a write-through mirror nothing reads
/// unless the tree is constructed with <c>load: true</c>, which a transient tree never is.</para>
/// <para>What that measurement did NOT excuse is the shared chunk 0 itself: every tree on the segment writes it on every insert under the tree's own
/// <c>_metadataLock</c>, which is one contended line per archetype once the fence runs cells in parallel. That is a suppression inside
/// <see cref="SpatialRTree{TStore}"/> and is tracked separately — it is a throughput problem, not a correctness one.</para>
/// <para><b>Why one segment per cell was rejected, with the number.</b> A <see cref="ChunkBasedSegment{TStore}"/> spans at least two pages since the v4
/// directory-only root (the root page carries the page directory and holds zero chunks), so 16 KiB minimum. At the 128³ / 1 % baseline — 20 971 occupied
/// cells — that is ~328 MiB of mostly-empty segment per spatial archetype, against ~10 MiB for the whole VDB layer.</para>
/// <para><b>Payloads are cluster chunk ids and bounds are <c>C15</c> cell-relative.</b> Both follow from the structure being per-cell per-archetype: a cluster
/// chunk id is only meaningful inside one archetype's <c>ClusterSegment</c> (issue #229 Q10), and a cluster lives wholly inside one cell (<c>C13</c>), which
/// is what makes the frame unambiguous.</para>
/// </remarks>
internal sealed class CellClusterTree
{
    /// <summary>
    /// The variant every cell tree uses. 3D f32 because <c>C16</c> makes 2D a degenerate Z axis rather than a separate code path, and <c>C15</c> rules out
    /// f64 — it would more than halve fan-out (~4 entries per node against 11), attacking the <c>O(log C)</c> the tree exists for.
    /// </summary>
    internal const SpatialVariant Variant = SpatialVariant.R3Df32;

    private readonly SpatialRTree<TransientStore> _tree;
    private readonly ChunkBasedSegment<TransientStore> _segment;
    private int _clusterCount;

    /// <summary>Number of clusters currently indexed by this cell's tree.</summary>
    internal int ClusterCount => _clusterCount;

    /// <summary>The tree, for the differential harness and the validator. Not part of the index contract.</summary>
    internal SpatialRTree<TransientStore> Tree => _tree;

    internal CellClusterTree(ChunkBasedSegment<TransientStore> segment, int[] payloadBackPointers)
    {
        _segment = segment;
        _tree = new SpatialRTree<TransientStore>(segment, Variant) { PayloadBackPointers = payloadBackPointers };
    }

    /// <summary>
    /// Re-point the tree at the archetype's back-pointer array. Called before every mutation because the owner grows that array by reallocation, which leaves
    /// the tree holding the abandoned one.
    /// </summary>
    /// <remarks>
    /// One reference store on a path that is already allocating chunks — cheaper than the alternatives, which are to make the array non-growable or to have
    /// the owner walk every cell's tree on each resize. See <see cref="SpatialRTree{TStore}.PayloadBackPointers"/> for why a stale reference here is a silent
    /// stale handle rather than a lost <c>-1</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RebindBackPointers(int[] payloadBackPointers) => _tree.PayloadBackPointers = payloadBackPointers;

    /// <summary>Insert a cluster. Returns the packed handle, which the caller stores in <c>ClusterSpatialIndexSlot</c>.</summary>
    internal int Add(int clusterChunkId, in ClusterSpatialAabb aabb)
    {
        Span<double> coords = stackalloc double[6];
        ToCoords(in aabb, coords);

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            var (leafChunkId, slotIndex) = _tree.Insert(clusterChunkId, coords, ref accessor, null, aabb.CategoryMask);
            _clusterCount++;
            return SpatialRTree<TransientStore>.PackHandle(leafChunkId, slotIndex);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Update a cluster's bounds in place when they still fit the leaf that holds them, and reinsert only when they escape — <c>C5</c>.
    /// </summary>
    /// <returns>The handle to store: unchanged on the in-place path, new on the escape path.</returns>
    /// <remarks>
    /// <para>The in-place store is the whole economic argument: a cluster's box moves every tick for a dynamic archetype, and remove-and-reinsert on each is
    /// ~94-235 µs per cell per tick against ~14 µs escape-bound. It is also where a stale handle would do its damage, which is why
    /// <c>SpatialRTree.PayloadBackPointers</c> had to exist before this method could.</para>
    /// <para><b>This leaves the leaf's MBR loose, and that is deliberate.</b> Not refitting is what makes the fast path fast. <c>ST-01</c> states leaf MBR
    /// EQUALITY, so the looseness must not outlive the exclusive window — the caller refits the leaves it touched at the end of the pass. Too-loose is
    /// <c>ST-01</c>'s performance-only direction; the fatal direction is too-tight, which this path cannot produce because it only ever widens.</para>
    /// </remarks>
    internal int UpdateAt(int handle, int clusterChunkId, in ClusterSpatialAabb aabb, out bool escaped)
    {
        Span<double> coords = stackalloc double[6];
        ToCoords(in aabb, coords);

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            var (leafChunkId, slotIndex) = SpatialRTree<TransientStore>.UnpackHandle(handle);
            if (_tree.TryUpdateLeafEntryInPlace(leafChunkId, slotIndex, clusterChunkId, coords, aabb.CategoryMask, ref accessor))
            {
                escaped = false;
                return handle;
            }

            // Escaped its leaf: remove and reinsert. Remove retires the handle and repairs the swapped entry's; Insert issues the new one.
            escaped = true;
            _tree.Remove(leafChunkId, slotIndex, ref accessor);
            var (newLeaf, newSlot) = _tree.Insert(clusterChunkId, coords, ref accessor, null, aabb.CategoryMask);
            return SpatialRTree<TransientStore>.PackHandle(newLeaf, newSlot);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Remove a cluster by its handle.</summary>
    internal void RemoveAt(int handle)
    {
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            var (leafChunkId, slotIndex) = SpatialRTree<TransientStore>.UnpackHandle(handle);
            _tree.Remove(leafChunkId, slotIndex, ref accessor);
            _clusterCount--;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Cluster chunk ids whose bounds overlap <paramref name="queryCoords"/>, which must already be in this cell's frame.
    /// </summary>
    internal SpatialRTree<TransientStore>.AABBQueryEnumerator Query(ReadOnlySpan<double> queryCoords, uint categoryMask) =>
        _tree.QueryAABB(queryCoords, null, categoryMask);

    /// <summary>
    /// Expand a cluster AABB into the tree's <c>[min0..minN, max0..maxN]</c> coordinate layout.
    /// </summary>
    /// <remarks>
    /// The 2D sentinel is translated rather than passed through. A 2D archetype leaves Z at ±Infinity, and an R-Tree node MBR that unions an infinite extent
    /// becomes infinite on that axis for the whole subtree — which prunes nothing and turns every descent into a full scan. Collapsing 2D to a zero-thickness
    /// Z slab keeps the node bounds finite while remaining exactly as selective, because every 2D cluster shares the same slab.
    /// </remarks>
    private static void ToCoords(in ClusterSpatialAabb aabb, Span<double> coords)
    {
        bool flat = float.IsPositiveInfinity(aabb.MinZ) || float.IsNegativeInfinity(aabb.MaxZ);
        coords[0] = aabb.MinX;
        coords[1] = aabb.MinY;
        coords[2] = flat ? 0d : aabb.MinZ;
        coords[3] = aabb.MaxX;
        coords[4] = aabb.MaxY;
        coords[5] = flat ? 0d : aabb.MaxZ;
    }

    /// <summary>Expand a query box into the tree's coordinate layout, collapsing an infinite Z range onto the flat slab <see cref="ToCoords"/> writes.</summary>
    internal static void QueryToCoords(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, Span<double> coords)
    {
        bool infiniteZ = float.IsNegativeInfinity(minZ) || float.IsPositiveInfinity(maxZ);
        coords[0] = minX;
        coords[1] = minY;
        coords[2] = infiniteZ ? 0d : minZ;
        coords[3] = maxX;
        coords[4] = maxY;
        coords[5] = infiniteZ ? 0d : maxZ;
    }
}
