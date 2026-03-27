using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>AABB query result: the EntityId of an entity whose fat AABB overlaps the query box.</summary>
internal readonly struct SpatialQueryResult
{
    public readonly long EntityId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpatialQueryResult(long entityId) => EntityId = entityId;
}

/// <summary>Stack buffer for DFS traversal of the R-Tree during AABB queries.</summary>
[InlineArray(256)]
internal struct QueryStackBuffer
{
    private int _element0;
}

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Query all entities whose fat AABB overlaps the given query box.
    /// Returns a ref struct enumerator suitable for foreach.
    /// </summary>
    /// <param name="queryCoords">CoordCount doubles: [min0, min1, ..., max0, max1, ...]</param>
    /// <param name="changeSet">ChangeSet for page access tracking</param>
    internal AABBQueryEnumerator QueryAABB(ReadOnlySpan<double> queryCoords, ChangeSet changeSet = null) => new(this, queryCoords, changeSet);

    /// <summary>
    /// Ref struct enumerator for AABB overlap queries. Uses stack-based DFS with OLC read validation per node. Zero heap allocations.
    /// </summary>
    internal ref struct AABBQueryEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;

        // Query bounds stored inline (max 6 doubles for 3D)
        private readonly double _q0, _q1, _q2, _q3, _q4, _q5;
        private readonly int _coordCount;

        // DFS stack of chunk IDs to visit
        private QueryStackBuffer _stack;
        private int _stackTop;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;

        private SpatialQueryResult _current;
        private bool _disposed;

        internal AABBQueryEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> queryCoords, ChangeSet changeSet)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;

            _q0 = queryCoords.Length > 0 ? queryCoords[0] : 0;
            _q1 = queryCoords.Length > 1 ? queryCoords[1] : 0;
            _q2 = queryCoords.Length > 2 ? queryCoords[2] : 0;
            _q3 = queryCoords.Length > 3 ? queryCoords[3] : 0;
            _q4 = queryCoords.Length > 4 ? queryCoords[4] : 0;
            _q5 = queryCoords.Length > 5 ? queryCoords[5] : 0;

            // Push root
            if (tree._rootChunkId != 0)
            {
                _stack[0] = tree._rootChunkId;
                _stackTop = 1;
            }
        }

        public SpatialQueryResult Current => _current;

        public AABBQueryEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Resume leaf scan if in progress
            while (_currentLeafChunkId != 0)
            {
                _currentLeafIndex++;
                if (_currentLeafIndex >= _currentLeafCount)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                if (EntryOverlapsQuery(leafBase, _currentLeafIndex, true))
                {
                    _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc));
                    return true;
                }
            }

            // DFS traversal
            while (_stackTop > 0)
            {
                int chunkId = _stack[--_stackTop];
                byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    // Locked or obsolete: restart from root
                    RestartFromRoot();
                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int count = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    continue;
                }

                if (isLeaf)
                {
                    // Start scanning this leaf
                    _currentLeafChunkId = chunkId;
                    _currentLeafIndex = -1;
                    _currentLeafCount = count;
                    return MoveNext(); // Re-enter to scan leaf entries
                }

                // Internal node: push overlapping children (reverse order for DFS)
                for (int i = count - 1; i >= 0; i--)
                {
                    if (EntryOverlapsQuery(nodeBase, i, false))
                    {
                        int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                        if (_stackTop < 256)
                        {
                            _stack[_stackTop++] = childId;
                        }
                    }
                }

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                }
            }

            return false;
        }

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        /// <summary>
        /// Separating-axis AABB overlap test: for each dimension, check entry.max &ge; query.min
        /// and entry.min &le; query.max.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool EntryOverlapsQuery(byte* nodeBase, int index, bool isLeaf)
        {
            int halfCoord = _coordCount / 2;

            for (int d = 0; d < halfCoord; d++)
            {
                double entryMin = isLeaf ? 
                    SpatialNodeHelper.ReadLeafCoord(nodeBase, index, d, _desc) : SpatialNodeHelper.ReadInternalCoord(nodeBase, index, d, _desc);
                double entryMax = isLeaf ? 
                    SpatialNodeHelper.ReadLeafCoord(nodeBase, index, d + halfCoord, _desc) : SpatialNodeHelper.ReadInternalCoord(nodeBase, index, d + halfCoord, _desc);

                double queryMin = GetQueryCoord(d);
                double queryMax = GetQueryCoord(d + halfCoord);

                if (entryMax < queryMin || entryMin > queryMax)
                {
                    return false;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double GetQueryCoord(int index) => index switch
        {
            0 => _q0,
            1 => _q1,
            2 => _q2,
            3 => _q3,
            4 => _q4,
            5 => _q5,
            _ => 0
        };

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _accessor.Dispose();
            }
        }
    }

    // ── Radius Query ─────────────────────────────────────────────────────

    /// <summary>
    /// Query all entities whose fat AABB overlaps a sphere defined by center + radius.
    /// Converts to AABB query internally. False positive rate: ~21% (2D), ~48% (3D) — caller post-filters.
    /// </summary>
    internal RadiusEnumerator QueryRadius(ReadOnlySpan<double> center, double radius, ChangeSet changeSet = null) => new(this, center, radius, changeSet);

    internal ref struct RadiusEnumerator
    {
        private AABBQueryEnumerator _inner;

        internal RadiusEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> center, double radius, ChangeSet changeSet)
        {
            int halfCoord = tree._desc.CoordCount / 2;
            Span<double> aabb = stackalloc double[tree._desc.CoordCount];
            for (int d = 0; d < halfCoord; d++)
            {
                aabb[d] = center[d] - radius;
                aabb[d + halfCoord] = center[d] + radius;
            }
            _inner = new AABBQueryEnumerator(tree, aabb, changeSet);
        }

        public SpatialQueryResult Current => _inner.Current;
        public RadiusEnumerator GetEnumerator() => this;
        public bool MoveNext() => _inner.MoveNext();
        public void Dispose() => _inner.Dispose();
    }

    // ── Ray Query ────────────────────────────────────────────────────────

    /// <summary>
    /// Query entities whose fat AABB intersects a ray, yielding results in front-to-back order.
    /// Uses a min-heap sorted by ray entry distance for priority traversal.
    /// </summary>
    internal RayEnumerator QueryRay(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist, ChangeSet changeSet = null)
        => new(this, origin, direction, maxDist, changeSet);

    /// <summary>Inline min-heap buffer for ray query priority queue (64 entries).</summary>
    [InlineArray(64)]
    internal struct RayHeapChunkIds { private int _element0; }

    [InlineArray(64)]
    internal struct RayHeapDistances { private double _element0; }

    internal ref struct RayEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private readonly double _maxDist;

        // Ray parameters (inline)
        private readonly double _ox, _oy, _oz, _idx, _idy, _idz;
        private readonly int _coordCount;

        // Min-heap of (chunkId, tEntry)
        private RayHeapChunkIds _heapChunkIds;
        private RayHeapDistances _heapDists;
        private int _heapSize;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;

        private SpatialQueryResult _current;
        private bool _disposed;

        internal RayEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist, ChangeSet changeSet)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _maxDist = maxDist;
            _heapSize = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;

            _ox = origin.Length > 0 ? origin[0] : 0;
            _oy = origin.Length > 1 ? origin[1] : 0;
            _oz = origin.Length > 2 ? origin[2] : 0;
            _idx = direction.Length > 0 && direction[0] != 0 ? 1.0 / direction[0] : double.MaxValue;
            _idy = direction.Length > 1 && direction[1] != 0 ? 1.0 / direction[1] : double.MaxValue;
            _idz = direction.Length > 2 && direction[2] != 0 ? 1.0 / direction[2] : double.MaxValue;

            if (tree._rootChunkId != 0)
            {
                HeapPush(tree._rootChunkId, 0.0);
            }
        }

        public SpatialQueryResult Current => _current;
        public RayEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Hoist stackalloc buffers outside all loops — reused across leaf scan and internal node processing
            int halfCoord = _coordCount / 2;
            Span<double> coords = stackalloc double[_coordCount];
            Span<double> origin = stackalloc double[halfCoord];
            Span<double> invDir = stackalloc double[halfCoord];
            origin[0] = _ox; invDir[0] = _idx;
            if (halfCoord > 1) { origin[1] = _oy; invDir[1] = _idy; }
            if (halfCoord > 2) { origin[2] = _oz; invDir[2] = _idz; }

            // Resume leaf scan if in progress
            while (_currentLeafChunkId != 0)
            {
                _currentLeafIndex++;
                if (_currentLeafIndex >= _currentLeafCount)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                SpatialNodeHelper.ReadLeafEntryCoords(leafBase, _currentLeafIndex, coords, _desc);

                var (hit, t) = SpatialGeometry.RayAABBIntersect(origin, invDir, coords, _coordCount);
                if (hit && t <= _maxDist)
                {
                    _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc));
                    return true;
                }
            }

            // Priority queue traversal
            while (_heapSize > 0)
            {
                double nextDist = _heapDists[0];
                if (nextDist > _maxDist)
                {
                    break; // Early termination
                }

                int chunkId = HeapPop();
                byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    RestartFromRoot();
                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int count = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    continue;
                }

                if (isLeaf)
                {
                    _currentLeafChunkId = chunkId;
                    _currentLeafIndex = -1;
                    _currentLeafCount = count;
                    return MoveNext();
                }

                // Internal node: push children with their ray entry distances
                for (int i = 0; i < count; i++)
                {
                    SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, coords, _desc);
                    var (hit, t) = SpatialGeometry.RayAABBIntersect(origin, invDir, coords, _coordCount);
                    if (hit && t <= _maxDist && _heapSize < 64)
                    {
                        int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                        HeapPush(childId, t);
                    }
                }

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                }
            }

            return false;
        }

        private void RestartFromRoot()
        {
            _heapSize = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                HeapPush(_tree._rootChunkId, 0.0);
            }
        }

        private void HeapPush(int chunkId, double dist)
        {
            int i = _heapSize++;
            _heapChunkIds[i] = chunkId;
            _heapDists[i] = dist;
            // Sift up
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heapDists[parent] <= _heapDists[i])
                {
                    break;
                }
                (_heapChunkIds[parent], _heapChunkIds[i]) = (_heapChunkIds[i], _heapChunkIds[parent]);
                (_heapDists[parent], _heapDists[i]) = (_heapDists[i], _heapDists[parent]);
                i = parent;
            }
        }

        private int HeapPop()
        {
            int result = _heapChunkIds[0];
            _heapSize--;
            if (_heapSize > 0)
            {
                _heapChunkIds[0] = _heapChunkIds[_heapSize];
                _heapDists[0] = _heapDists[_heapSize];
                // Sift down
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    int right = 2 * i + 2;
                    int smallest = i;
                    if (left < _heapSize && _heapDists[left] < _heapDists[smallest])
                    {
                        smallest = left;
                    }
                    if (right < _heapSize && _heapDists[right] < _heapDists[smallest])
                    {
                        smallest = right;
                    }
                    if (smallest == i)
                    {
                        break;
                    }
                    (_heapChunkIds[i], _heapChunkIds[smallest]) = (_heapChunkIds[smallest], _heapChunkIds[i]);
                    (_heapDists[i], _heapDists[smallest]) = (_heapDists[smallest], _heapDists[i]);
                    i = smallest;
                }
            }
            return result;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _accessor.Dispose();
            }
        }
    }

    // ── Frustum Query ────────────────────────────────────────────────────

    /// <summary>
    /// Query entities whose fat AABB intersects a frustum defined by a set of half-space planes.
    /// Optimizes with INSIDE subtree yields (entire subtree visible → skip per-entry plane tests).
    /// Planes packed as (normalX, normalY, [normalZ,] distance), dimCount+1 doubles per plane.
    /// </summary>
    internal FrustumEnumerator QueryFrustum(ReadOnlySpan<double> planes, int planeCount, ChangeSet changeSet = null)
        => new(this, planes, planeCount, changeSet);

    /// <summary>Stack buffer for frustum DFS — encodes (chunkId, fullyInside) via sign bit.</summary>
    [InlineArray(256)]
    internal struct FrustumStackBuffer { private int _element0; }

    internal ref struct FrustumEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private readonly int _planeCount;
        private readonly int _dimCount;
        private readonly int _planeDataLen; // _planeCount * (_dimCount + 1)

        // Planes stored inline: max 6 planes × 4 doubles = 24 doubles
        private fixed double _planes[24];

        // DFS stack — sign bit encodes fullyInside flag
        private FrustumStackBuffer _stack;
        private int _stackTop;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;
        private bool _currentLeafFullyInside;

        private SpatialQueryResult _current;
        private bool _disposed;

        internal FrustumEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> planes, int planeCount, ChangeSet changeSet)
        {
            _tree = tree;
            _desc = tree._desc;
            _dimCount = _desc.CoordCount / 2;
            _planeCount = planeCount;
            _planeDataLen = planeCount * (_dimCount + 1);
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _currentLeafFullyInside = false;
            _current = default;
            _disposed = false;

            int len = Math.Min(planes.Length, 24);
            for (int i = 0; i < len; i++)
            {
                _planes[i] = planes[i];
            }

            if (tree._rootChunkId != 0)
            {
                _stack[0] = tree._rootChunkId; // positive = needs testing
                _stackTop = 1;
            }
        }

        public SpatialQueryResult Current => _current;
        public FrustumEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Hoist reusable buffers outside all loops
            Span<double> coords = stackalloc double[_desc.CoordCount];
            Span<double> planeSpan = stackalloc double[_planeDataLen];
            for (int i = 0; i < _planeDataLen; i++)
            {
                planeSpan[i] = _planes[i];
            }

            // Resume leaf scan
            while (_currentLeafChunkId != 0)
            {
                _currentLeafIndex++;
                if (_currentLeafIndex >= _currentLeafCount)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                if (_currentLeafFullyInside)
                {
                    // INSIDE optimization: yield without plane tests
                    byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                    _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc));
                    return true;
                }

                // Test individual entry against frustum
                byte* lb = _accessor.GetChunkAddress(_currentLeafChunkId);
                SpatialNodeHelper.ReadLeafEntryCoords(lb, _currentLeafIndex, coords, _desc);

                int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(coords, planeSpan, _planeCount, _dimCount);
                if (cls != SpatialGeometry.FrustumOutside)
                {
                    _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(lb, _currentLeafIndex, _desc));
                    return true;
                }
            }

            // DFS traversal
            while (_stackTop > 0)
            {
                int encoded = _stack[--_stackTop];
                bool fullyInside = encoded < 0;
                int chunkId = fullyInside ? -encoded : encoded;

                byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    RestartFromRoot();
                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int count = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    continue;
                }

                if (isLeaf)
                {
                    _currentLeafChunkId = chunkId;
                    _currentLeafIndex = -1;
                    _currentLeafCount = count;
                    _currentLeafFullyInside = fullyInside;
                    return MoveNext();
                }

                if (fullyInside)
                {
                    // All children are fully inside — push with fullyInside flag
                    for (int i = count - 1; i >= 0; i--)
                    {
                        int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                        if (_stackTop < 256)
                        {
                            _stack[_stackTop++] = -childId; // negative = fully inside
                        }
                    }
                }
                else
                {
                    // Classify each child
                    for (int i = count - 1; i >= 0; i--)
                    {
                        SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, coords, _desc);
                        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(coords, planeSpan, _planeCount, _dimCount);
                        if (cls == SpatialGeometry.FrustumOutside)
                        {
                            continue;
                        }
                        int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                        if (_stackTop < 256)
                        {
                            _stack[_stackTop++] = cls == SpatialGeometry.FrustumInside ? -childId : childId;
                        }
                    }
                }

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                }
            }

            return false;
        }

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _accessor.Dispose();
            }
        }
    }

    // ── kNN Query ────────────────────────────────────────────────────────

    /// <summary>
    /// Find the k nearest entities to a point. Results written to caller-provided buffer, sorted by ascending squared distance.
    /// Uses iterative radius expansion — converges in 1–2 iterations for k &lt; 20.
    /// </summary>
    /// <returns>Number of results written (may be less than k if fewer entities exist).</returns>
    internal int QueryKNN(ReadOnlySpan<double> center, int k, Span<(long entityId, double distSq)> results, ChangeSet changeSet = null)
    {
        if (k <= 0 || _entityCount == 0)
        {
            return 0;
        }

        int halfCoord = _desc.CoordCount / 2;

        // Estimate initial radius from entity density
        double worldVolume = 1.0;
        if (_entityCount > 1)
        {
            // Read root node MBR to estimate world extent
            var accessor = _segment.CreateChunkAccessor(changeSet);
            try
            {
                byte* rootBase = accessor.GetChunkAddress(_rootChunkId);
                for (int d = 0; d < halfCoord; d++)
                {
                    double extent = SpatialNodeHelper.ReadNodeMBRCoord(rootBase, d + halfCoord, _desc) -
                                    SpatialNodeHelper.ReadNodeMBRCoord(rootBase, d, _desc);
                    if (extent > 0)
                    {
                        worldVolume *= extent;
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        double entityDensity = _entityCount / Math.Max(worldVolume, 1e-10);
        double volumeForK = k / Math.Max(entityDensity, 1e-10);
        double radius = Math.Pow(volumeForK, 1.0 / halfCoord) * 1.5; // 1.5x safety factor
        radius = Math.Max(radius, 1.0); // Minimum radius

        // Iterative expansion
        int maxCandidates = Math.Min(k * 4, 256);
        Span<(long entityId, double distSq)> candidates = stackalloc (long, double)[maxCandidates];
        int lastCount = 0;

        for (int iteration = 0; iteration < 8; iteration++)
        {
            int count = 0;
            foreach (var result in QueryRadius(center, radius, changeSet))
            {
                if (count >= candidates.Length)
                {
                    break;
                }
                candidates[count++] = (result.EntityId, 0);
            }

            if (count >= k || count == lastCount || radius > 1e15)
            {
                // Found enough, or no new results (all entities captured), or radius exceeded
                int resultCount = Math.Min(count, k);
                resultCount = Math.Min(resultCount, results.Length);
                for (int i = 0; i < resultCount; i++)
                {
                    results[i] = candidates[i];
                }
                return resultCount;
            }

            lastCount = count;
            radius *= 2.0;
        }

        return 0;
    }
}
