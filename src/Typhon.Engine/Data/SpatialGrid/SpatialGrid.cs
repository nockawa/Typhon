using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Engine-wide coarse spatial grid. Owns the per-cell descriptor array and the pool holding each cell's cluster list.
/// One instance per <see cref="DatabaseEngine"/>, configured once at startup via <see cref="DatabaseEngine.ConfigureSpatialGrid"/>.
/// </summary>
/// <remarks>
/// <para>Phase 1+2 scope of issue #229: this grid exists, is wired into the spawn path for spatial archetypes, and is rebuilt at startup.
/// It does <em>not</em> yet participate in migration (Phase 3) or per-cell R-Tree queries (#230).</para>
/// <para>The grid stores only transient state. Nothing in <see cref="CellDescriptor"/> is persisted;
/// <c>RebuildCellState</c> reconstructs everything from entity positions after a reopen.</para>
/// </remarks>
[PublicAPI]
internal sealed unsafe class SpatialGrid
{
    private readonly SpatialGridConfig _config;
    private readonly CellDescriptor[] _cells;
    private readonly CellClusterPool _clusterPool;

    public SpatialGrid(SpatialGridConfig config)
    {
        _config = config;
        _cells = new CellDescriptor[config.CellCount];
        // Mark all cells' head as "no segment yet". Zero would be a valid head, so we use -1.
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i].ClusterListHead = -1;
        }
        _clusterPool = new CellClusterPool(config.CellCount);
    }

    public ref readonly SpatialGridConfig Config => ref _config;

    public int CellCount => _cells.Length;

    internal CellClusterPool CellClusterPool => _clusterPool;

    /// <summary>
    /// Access a cell descriptor by cell key for read + write (callers bump <see cref="CellDescriptor.EntityCount"/>
    /// and <see cref="CellDescriptor.ClusterCount"/> directly).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref CellDescriptor GetCell(int cellKey) => ref _cells[cellKey];

    /// <summary>
    /// Convert a world-space 2D point to a grid cell key. Points outside the configured bounds are clamped to the nearest valid cell — callers that care
    /// about "out of bounds" should test bounds themselves before calling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WorldToCellKey(float worldX, float worldY)
    {
        // Guard against NaN / ±Infinity: relational comparisons with NaN return false on both sides,
        // so the clamp below wouldn't catch a NaN — it would slip through as cellX=0 (or whatever the
        // implementation-defined (int)NaN returns on the current runtime). Rather than produce a
        // silently wrong cell key, throw so the caller fixes the upstream bug.
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY))
        {
            throw new ArgumentException(
                $"WorldToCellKey received a non-finite coordinate: ({worldX}, {worldY}). " +
                $"Position data is corrupted upstream — spatial grid cannot place a NaN/Infinity entity.");
        }

        // Convert to cell coordinates
        int cellX = (int)MathF.Floor((worldX - _config.WorldMin.X) * _config.InverseCellSize);
        int cellY = (int)MathF.Floor((worldY - _config.WorldMin.Y) * _config.InverseCellSize);

        // Clamp to valid grid range
        if (cellX < 0)
        {
            cellX = 0;
        }
        else if (cellX >= _config.GridWidth)
        {
            cellX = _config.GridWidth - 1;
        }

        if (cellY < 0)
        {
            cellY = 0;
        }
        else if (cellY >= _config.GridHeight)
        {
            cellY = _config.GridHeight - 1;
        }

        return ComputeCellKey(cellX, cellY);
    }

    /// <summary>
    /// Convert a world-space 2D AABB to the inclusive cell-coordinate range it overlaps. Used by query
    /// paths that iterate all cells touched by a query rectangle (issue #230). Out-of-bounds inputs are
    /// clamped to the grid extent; <see cref="float.NaN"/> / <see cref="float.PositiveInfinity"/> inputs
    /// throw because they would produce meaningless cell indices.
    /// </summary>
    /// <param name="minX">Query AABB minimum X in world units.</param>
    /// <param name="minY">Query AABB minimum Y in world units.</param>
    /// <param name="maxX">Query AABB maximum X in world units.</param>
    /// <param name="maxY">Query AABB maximum Y in world units.</param>
    /// <param name="cellMinX">Inclusive minimum cell X coordinate.</param>
    /// <param name="cellMinY">Inclusive minimum cell Y coordinate.</param>
    /// <param name="cellMaxX">Inclusive maximum cell X coordinate.</param>
    /// <param name="cellMaxY">Inclusive maximum cell Y coordinate.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WorldToCellRange(float minX, float minY, float maxX, float maxY, out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
        {
            throw new ArgumentException(
                $"WorldToCellRange received non-finite coordinates: ({minX}, {minY}, {maxX}, {maxY}). " +
                $"Query data is corrupted upstream — spatial grid cannot compute a cell range for a NaN/Infinity AABB.");
        }

        int rawMinX = (int)MathF.Floor((minX - _config.WorldMin.X) * _config.InverseCellSize);
        int rawMinY = (int)MathF.Floor((minY - _config.WorldMin.Y) * _config.InverseCellSize);
        int rawMaxX = (int)MathF.Floor((maxX - _config.WorldMin.X) * _config.InverseCellSize);
        int rawMaxY = (int)MathF.Floor((maxY - _config.WorldMin.Y) * _config.InverseCellSize);

        cellMinX = Math.Clamp(rawMinX, 0, _config.GridWidth - 1);
        cellMinY = Math.Clamp(rawMinY, 0, _config.GridHeight - 1);
        cellMaxX = Math.Clamp(rawMaxX, 0, _config.GridWidth - 1);
        cellMaxY = Math.Clamp(rawMaxY, 0, _config.GridHeight - 1);
    }

    /// <summary>
    /// Extract a 2D centre point from a spatial field pointer. Supports <see cref="SpatialFieldType.AABB2F"/>
    /// (centre of the AABB) and <see cref="SpatialFieldType.BSphere2F"/> (sphere centre). Other field types
    /// are unsupported in Phase 1+2 and will throw at config time, so this method does not re-validate.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="WorldToCellKeyFromSpatialField"/> and the cell-crossing detection loop in
    /// <c>DatabaseEngine.ProcessClusterSpatialEntries</c> (issue #229 Phase 3). The detection path reuses the
    /// extracted center for both the hysteresis bounds check and the fallback <see cref="WorldToCellKey"/> call,
    /// avoiding a double read of the field memory.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadSpatialCenter2D(byte* fieldPtr, SpatialFieldType fieldType, out float posX, out float posY)
    {
        switch (fieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                float minX = *(float*)fieldPtr;
                float minY = *(float*)(fieldPtr + sizeof(float));
                float maxX = *(float*)(fieldPtr + 2 * sizeof(float));
                float maxY = *(float*)(fieldPtr + 3 * sizeof(float));
                posX = (minX + maxX) * 0.5f;
                posY = (minY + maxY) * 0.5f;
                return;
            }
            case SpatialFieldType.AABB3F:
            {
                // 3D AABB layout is [minX, minY, minZ, maxX, maxY, maxZ]. For 2D cell bucketing we use only the XY center — Z is used at narrowphase.
                // Issue #230 Phase 3.
                float minX = *(float*)fieldPtr;
                float minY = *(float*)(fieldPtr + sizeof(float));
                float maxX = *(float*)(fieldPtr + 3 * sizeof(float));
                float maxY = *(float*)(fieldPtr + 4 * sizeof(float));
                posX = (minX + maxX) * 0.5f;
                posY = (minY + maxY) * 0.5f;
                return;
            }
            case SpatialFieldType.BSphere2F:
            {
                // BSphere2F — CenterX, CenterY, Radius
                posX = *(float*)fieldPtr;
                posY = *(float*)(fieldPtr + sizeof(float));
                return;
            }
            case SpatialFieldType.BSphere3F:
            {
                // BSphere3F — CenterX, CenterY, CenterZ, Radius. Same 2D bucketing approach as AABB3F.
                posX = *(float*)fieldPtr;
                posY = *(float*)(fieldPtr + sizeof(float));
                return;
            }
            default:
                // ValidateSupportedFieldType rejects f64 tiers at ConfigureSpatialGrid time, so this path should not be reachable. Defensive fallback
                // to help diagnose any future field-type addition that forgot to update this dispatch.
                throw new System.NotSupportedException(
                    $"ReadSpatialCenter2D: field type '{fieldType}' is not supported. f32 tiers (2D and 3D) only.");
        }
    }

    /// <summary>
    /// Extract a 2D centre point from a spatial field pointer and convert it to a cell key. Supports <see cref="SpatialFieldType.AABB2F"/> (centre of the AABB)
    /// and <see cref="SpatialFieldType.BSphere2F"/> (sphere centre). Other field types are unsupported in Phase 1+2 and will throw at config time, so this
    /// method does not re-validate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WorldToCellKeyFromSpatialField(byte* fieldPtr, SpatialFieldType fieldType)
    {
        ReadSpatialCenter2D(fieldPtr, fieldType, out float posX, out float posY);
        return WorldToCellKey(posX, posY);
    }

    /// <summary>
    /// Throws if <paramref name="fieldType"/> is not supported by the spatial grid. Issue #230 Phase 3 extended support from 2D-only to both 2D and 3D f32
    /// tiers — cells are always 2D (XY) and 3D archetypes bucket entities by their XY center, ignoring Z. Z-axis filtering happens at the query narrowphase.
    /// f64 tiers remain deferred to a follow-up sub-issue of #228.
    /// </summary>
    public static void ValidateSupportedFieldType(SpatialFieldType fieldType, string archetypeName)
    {
        if (fieldType is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F or SpatialFieldType.AABB3F or SpatialFieldType.BSphere3F)
        {
            return;
        }
        throw new NotSupportedException(
            $"Spatial archetype '{archetypeName}' uses field type '{fieldType}'. " +
            $"The spatial grid currently supports f32 spatial fields only (AABB2F, BSphere2F, AABB3F, BSphere3F). " +
            $"f64 variants are a planned follow-up.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeCellKey(int cellX, int cellY)
    {
        if (SpatialConfig.UseMortonCellKeys)
        {
            return MortonKeys.Encode2D(cellX, cellY);
        }
#pragma warning disable CS0162 // Unreachable code — deliberate const-bool feature flag
        // ReSharper disable once HeuristicUnreachableCode
        return cellY * _config.GridWidth + cellX;
#pragma warning restore CS0162
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int x, int y) CellKeyToCoords(int cellKey)
    {
        if (SpatialConfig.UseMortonCellKeys)
        {
            return MortonKeys.Decode2D(cellKey);
        }
#pragma warning disable CS0162 // Unreachable code — deliberate const-bool feature flag
        // ReSharper disable once HeuristicUnreachableCode
        return (cellKey % _config.GridWidth, cellKey / _config.GridWidth);
#pragma warning restore CS0162
    }

    /// <summary>
    /// Drop all cell state and reset the pool. Called by <c>RebuildCellState</c> before reconstructing
    /// the mapping from entity positions.
    /// </summary>
    public void ResetCellState()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = default;
            _cells[i].ClusterListHead = -1;
        }
        _clusterPool.Reset();
    }
}
