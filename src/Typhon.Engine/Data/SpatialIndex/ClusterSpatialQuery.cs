using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation per-cell cluster AABB query for a single archetype (issue #230 Phase 1).
/// The query expands the requested AABB into the overlapping grid cells, iterates each cell's <see cref="CellSpatialIndex"/> as a linear broadphase, and for
/// each broadphase-hit cluster performs a narrowphase scan over its occupied entity slots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> Requires the game to have called <see cref="DatabaseEngine.ConfigureSpatialGrid"/> before <see cref="DatabaseEngine.InitializeArchetypes"/>.
/// Without it, the per-cell index is never populated and querying throws <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// <b>Phase 1 scope.</b> Dynamic-mode archetypes only; Static mode still uses the legacy per-entity
/// <c>SpatialQuery{T}</c>. 2D f32 bounds only. No overflow R-Tree — the broadphase is a linear scan over all clusters in each cell, which is optimal for
/// typical AntHill cell populations (≤80 clusters).
/// </para>
/// <para>
/// <b>Epoch scope.</b> The caller must be inside an <see cref="EpochGuard"/> scope; the enumerator creates a <see cref="ChunkAccessor{TStore}"/> on the
/// cluster segment to read entity bounds during the narrowphase pass.
/// </para>
/// </remarks>
public readonly ref struct ClusterSpatialQuery<TArch> where TArch : Archetype<TArch>, new()
{
    private readonly ArchetypeClusterState _state;
    private readonly SpatialGrid _grid;

    internal ClusterSpatialQuery(ArchetypeClusterState state, SpatialGrid grid)
    {
        _state = state;
        _grid = grid;
    }

    /// <summary>
    /// Query all entities in this archetype whose spatial bounds intersect the axis-aligned box
    /// <c>[minX..maxX] × [minY..maxY]</c>.
    /// </summary>
    /// <param name="minX">Query AABB minimum X in world units.</param>
    /// <param name="minY">Query AABB minimum Y in world units.</param>
    /// <param name="maxX">Query AABB maximum X in world units.</param>
    /// <param name="maxY">Query AABB maximum Y in world units.</param>
    /// <param name="categoryMask">
    /// Category bitmask; a cluster is skipped if its union mask does not intersect. Pass
    /// <see cref="uint.MaxValue"/> (default) to accept every cluster.
    /// </param>
    /// <returns>A zero-allocation enumerator suitable for <c>foreach</c>.</returns>
    public AABBEnumerator AABB(float minX, float minY, float maxX, float maxY, uint categoryMask = uint.MaxValue)
    {
        if (_state?.SpatialSlot.Tree == null)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no spatial index. " +
                "Ensure the archetype has a SpatialIndex field and that ConfigureSpatialGrid was called " +
                "on the engine before InitializeArchetypes.");
        }
        
        // PerCellIndex may be null when the archetype exists but no spatial entities have been spawned yet. That's a legitimate "empty query result"
        // state — the enumerator handles it gracefully by returning false on the first MoveNext() call.
        return new AABBEnumerator(_state, _grid, minX, minY, maxX, maxY, categoryMask);
    }

    /// <summary>
    /// Zero-allocation, stack-allocated enumerator over the entities matching a
    /// <see cref="ClusterSpatialQuery{TArch}.AABB"/> query. Advance via <see cref="MoveNext"/>; read the current result via <see cref="Current"/>.
    /// </summary>
    public unsafe ref struct AABBEnumerator
    {
        private readonly ArchetypeClusterState _state;
        private readonly SpatialGrid _grid;

        // Query bounds in world units (f32 — Phase 1 is 2D f32 only).
        private readonly float _queryMinX;
        private readonly float _queryMinY;
        private readonly float _queryMaxX;
        private readonly float _queryMaxY;
        private readonly uint _categoryMask;

        // Cell range the query AABB covers, inclusive. Clamped to the grid extent by SpatialGrid.
        private readonly int _cellMinX;
        private readonly int _cellMinY;
        private readonly int _cellMaxX;
        private readonly int _cellMaxY;

        // Cluster-SoA field offset for the spatial field within each cluster, precomputed.
        private readonly int _spatialCompOffset;
        private readonly int _spatialCompSize;
        private readonly int _spatialFieldOffset;
        private readonly SpatialFieldInfo _fieldInfo;
        private readonly SpatialNodeDescriptor _descriptor;

        // Cluster segment accessor for narrowphase entity reads. Disposed via Dispose().
        private ChunkAccessor<PersistentStore> _accessor;
        private bool _accessorCreated;

        // Iteration state.
        private int _currentCellX;
        private int _currentCellY;
        private CellSpatialIndex _currentCellIndex;    // null when we need to advance to the next cell
        private int _currentBroadphaseSlot;            // next index into _currentCellIndex.ClusterIds to scan
        private ulong _currentOccupancyBits;           // remaining occupied slots in the current cluster (bits cleared as we iterate)
        private int _currentClusterChunkId;            // chunk id of the cluster currently in narrowphase
        private byte* _currentClusterBase;      // base pointer of that cluster

        // Last-yielded result.
        private ClusterSpatialQueryResult _current;

        internal AABBEnumerator(ArchetypeClusterState state, SpatialGrid grid, float minX, float minY, float maxX, float maxY, uint categoryMask)
        {
            _state = state;
            _grid = grid;
            _queryMinX = minX;
            _queryMinY = minY;
            _queryMaxX = maxX;
            _queryMaxY = maxY;
            _categoryMask = categoryMask;

            // Expand query AABB to the overlapping cell range. Each overlapping cell's per-archetype
            // spatial slot may or may not exist — the iteration handles null slots.
            grid.WorldToCellRange(minX, minY, maxX, maxY, out _cellMinX, out _cellMinY, out _cellMaxX, out _cellMaxY);

            var ss = state.SpatialSlot;
            _spatialCompOffset = state.Layout.ComponentOffset(ss.Slot);
            _spatialCompSize = state.Layout.ComponentSize(ss.Slot);
            _spatialFieldOffset = ss.FieldOffset;
            _fieldInfo = ss.FieldInfo;
            _descriptor = ss.Descriptor;

            _accessor = default;
            _accessorCreated = false;
            _currentCellX = _cellMinX;
            _currentCellY = _cellMinY;
            _currentCellIndex = null;
            _currentBroadphaseSlot = 0;
            _currentOccupancyBits = 0UL;
            _currentClusterChunkId = 0;
            _currentClusterBase = null;
            _current = default;
        }

        /// <summary>The most recently yielded result. Valid only after <see cref="MoveNext"/> returns <c>true</c>.</summary>
        public ClusterSpatialQueryResult Current => _current;

        /// <summary>Advance to the next matching entity. Returns <c>false</c> when the query is exhausted.</summary>
        public bool MoveNext()
        {
            // Hoisted stackalloc scratch for narrowphase entity bound reads. Allocating ONCE per MoveNext
            // call (not per loop iteration) avoids accumulating stack pressure across iterations of the
            // state machine's while(true) — a query that scans thousands of clusters before finding the
            // first match would otherwise allocate 32 bytes per iteration that can't be released until
            // MoveNext returns. See CA2014 for the general guidance.
            Span<double> entityCoords = stackalloc double[4];

            // Lazy accessor creation: only opened when the first cluster is about to be scanned.
            // Avoids accessor construction cost for empty queries (no overlapping cells with clusters).
            while (true)
            {
                // 1. Drain the current cluster's occupancy bits (narrowphase).
                if (_currentOccupancyBits != 0UL && _currentClusterBase != null)
                {
                    int slot = BitOperations.TrailingZeroCount(_currentOccupancyBits);
                    _currentOccupancyBits &= _currentOccupancyBits - 1;

                    // Read entity's tight bounds and test against query AABB.
                    byte* fieldPtr = _currentClusterBase + _spatialCompOffset + slot * _spatialCompSize + _spatialFieldOffset;
                    if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, _fieldInfo, entityCoords, _descriptor))
                    {
                        continue; // degenerate — skip
                    }

                    float eMinX = (float)entityCoords[0];
                    float eMinY = (float)entityCoords[1];
                    float eMaxX = (float)entityCoords[2];
                    float eMaxY = (float)entityCoords[3];

                    // Standard AABB overlap: miss if separated along any axis.
                    if (eMaxX < _queryMinX || eMinX > _queryMaxX)
                    {
                        continue;
                    }

                    if (eMaxY < _queryMinY || eMinY > _queryMaxY)
                    {
                        continue;
                    }

                    long entityId = *(long*)(_currentClusterBase + _state.Layout.EntityIdsOffset + slot * 8);
                    _current = new ClusterSpatialQueryResult(entityId, _currentClusterChunkId, slot);
                    return true;
                }

                // 2. Advance to the next cluster in the current cell's broadphase (linear scan).
                if (_currentCellIndex != null && _currentBroadphaseSlot < _currentCellIndex.ClusterCount)
                {
                    int idx = _currentBroadphaseSlot++;
                    uint clusterMask = _currentCellIndex.CategoryMasks[idx];
                    if ((clusterMask & _categoryMask) == 0)
                    {
                        continue; // category miss
                    }

                    // AABB overlap against the cluster's stored bounds.
                    float cMinX = _currentCellIndex.MinX[idx];
                    float cMinY = _currentCellIndex.MinY[idx];
                    float cMaxX = _currentCellIndex.MaxX[idx];
                    float cMaxY = _currentCellIndex.MaxY[idx];
                    if (cMaxX < _queryMinX || cMinX > _queryMaxX)
                    {
                        continue;
                    }

                    if (cMaxY < _queryMinY || cMinY > _queryMaxY)
                    {
                        continue;
                    }

                    // Broadphase hit — open the cluster for narrowphase scanning.
                    int chunkId = _currentCellIndex.ClusterIds[idx];
                    EnsureAccessor();
                    _currentClusterBase = _accessor.GetChunkAddress(chunkId);
                    _currentClusterChunkId = chunkId;
                    _currentOccupancyBits = *(ulong*)_currentClusterBase;
                    continue; // next iteration will drain occupancy bits
                }

                // 3. Advance to the next cell in the range.
                _currentCellIndex = null;
                _currentBroadphaseSlot = 0;
                while (_currentCellY <= _cellMaxY)
                {
                    while (_currentCellX <= _cellMaxX)
                    {
                        int cellKey = _grid.ComputeCellKey(_currentCellX, _currentCellY);
                        _currentCellX++;
                        if (cellKey < 0 || _state.PerCellIndex == null || cellKey >= _state.PerCellIndex.Length)
                        {
                            continue;
                        }
                        var slot = _state.PerCellIndex[cellKey];
                        if (slot?.DynamicIndex == null || slot.DynamicIndex.ClusterCount == 0)
                        {
                            continue;
                        }
                        _currentCellIndex = slot.DynamicIndex;
                        break;
                    }
                    if (_currentCellIndex != null)
                    {
                        break;
                    }
                    _currentCellY++;
                    _currentCellX = _cellMinX;
                }

                // 4. Done — no more cells with clusters.
                if (_currentCellIndex == null)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Dispose the narrowphase accessor if one was opened. Called automatically by
        /// <c>foreach</c> on a <c>ref struct</c> that implements this method.
        /// </summary>
        public void Dispose()
        {
            if (_accessorCreated)
            {
                _accessor.Dispose();
                _accessorCreated = false;
            }
        }

        /// <summary>Enumerator pattern: a ref struct enumerator is its own source.</summary>
        public AABBEnumerator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureAccessor()
        {
            if (!_accessorCreated)
            {
                _accessor = _state.ClusterSegment.CreateChunkAccessor();
                _accessorCreated = true;
            }
        }
    }
}

/// <summary>
/// Result of a <see cref="ClusterSpatialQuery{TArch}"/> match. Holds the entity id plus its
/// location inside the cluster storage (chunk id and slot index).
/// </summary>
public readonly struct ClusterSpatialQueryResult
{
    public readonly long EntityId;
    public readonly int ClusterChunkId;
    public readonly int SlotIndex;

    internal ClusterSpatialQueryResult(long entityId, int clusterChunkId, int slotIndex)
    {
        EntityId = entityId;
        ClusterChunkId = clusterChunkId;
        SlotIndex = slotIndex;
    }
}

/// <summary>
/// Construction helpers for <see cref="ClusterSpatialQuery{TArch}"/>. Exposed on
/// <see cref="DatabaseEngine"/> so callers can write <c>dbe.ClusterSpatialQuery{TArch}().AABB(...)</c>.
/// </summary>
public static class ClusterSpatialQueryExtensions
{
    /// <summary>
    /// Create a per-cell cluster AABB query for the given archetype.
    /// </summary>
    /// <typeparam name="TArch">The archetype type.</typeparam>
    /// <param name="engine">The database engine.</param>
    /// <returns>A zero-allocation query handle.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the archetype is not cluster-eligible or has no spatial component.
    /// </exception>
    public static ClusterSpatialQuery<TArch> ClusterSpatialQuery<TArch>(this DatabaseEngine engine)
        where TArch : Archetype<TArch>, new()
    {
        var meta = Archetype<TArch>.Metadata;
        if (!meta.IsClusterEligible)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype is not cluster-eligible.");
        }
        var state = engine._archetypeStates[meta.ArchetypeId].ClusterState;
        if (state == null)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no cluster state.");
        }
        return new ClusterSpatialQuery<TArch>(state, engine.SpatialGrid);
    }
}
