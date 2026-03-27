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
}
