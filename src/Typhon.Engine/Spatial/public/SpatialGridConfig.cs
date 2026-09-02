using System;
using System.Numerics;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Immutable configuration for the engine-wide spatial grid. Set once via <see cref="DatabaseEngine.ConfigureSpatialGrid"/> before archetypes are initialized.
/// </summary>
/// <remarks>
/// <para>All spatial archetypes share a single coarse grid with one cell size. Per-archetype differences are expressed at the system level, through tier
/// filters, rather than at the grid level.</para>
/// <para><b>The grid is three-dimensional, and a flat world is simply a grid one cell deep.</b> There is deliberately no 2D overload set: a 2D/3D pair
/// would give you a call site where the Z coordinate is silently dropped, collapsing every entity onto the z = 0 plane. That does not raise — it returns
/// spatial query results that are quietly wrong. Use <see cref="Flat"/> to build a one-cell-deep world in a single call, and keep one code path.</para>
/// <para>Grid dimensions are derived per axis from (WorldMax - WorldMin) / CellSize, rounded up, and cell keys are plain row-major
/// <c>(z * GridHeight + y) * GridWidth + x</c>. There is no Morton encoding and no power-of-two padding: a 32-bit 3D Morton key would cap the world at 1 024
/// cells per axis, and the square key space its 2D predecessor needed would have made the descriptor count <c>KeySpaceDim³</c> — over a billion cells for a
/// 1024 x 1024 x 1 world.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialGridConfig
{
    /// <summary>World-space minimum corner (inclusive).</summary>
    public readonly Vector3 WorldMin;

    /// <summary>World-space maximum corner (exclusive — the grid excludes the max edge).</summary>
    public readonly Vector3 WorldMax;

    /// <summary>Size of a single grid cell, in world units. Cells are cubic. Must be &gt; 0.</summary>
    public readonly float CellSize;

    /// <summary>
    /// Fractional dead zone applied per axis during entity migration, as a fraction of cell size.
    /// Default 0.05 (5 % of cell size).
    /// </summary>
    public readonly float MigrationHysteresisRatio;

    // ── Derived values, computed in the constructor ────────────────────────

    /// <summary>
    /// Number of cells along the X axis — derived from (WorldMax.X - WorldMin.X) / CellSize, rounded up.
    /// </summary>
    public readonly int GridWidth;

    /// <summary>Number of cells along the Y axis.</summary>
    public readonly int GridHeight;

    /// <summary>Number of cells along the Z axis. <c>1</c> for a flat world built with <see cref="Flat(Vector2,Vector2,float,float)"/>.</summary>
    public readonly int GridDepth;

    /// <summary>Precomputed 1 / <see cref="CellSize"/>.</summary>
    public readonly float InverseCellSize;

    /// <summary>Total number of cell descriptor slots: <see cref="GridWidth"/> × <see cref="GridHeight"/> × <see cref="GridDepth"/>.</summary>
    public readonly int CellCount;

    /// <summary>
    /// Build a grid configuration and precompute the derived cell dimensions. World bounds are half-open: <paramref name="worldMin"/> is inclusive,
    /// <paramref name="worldMax"/> is exclusive.
    /// </summary>
    /// <param name="worldMin">World-space minimum corner (inclusive).</param>
    /// <param name="worldMax">World-space maximum corner (exclusive); must be strictly greater than <paramref name="worldMin"/> on all three axes.</param>
    /// <param name="cellSize">Cell size in world units; must be &gt; 0.</param>
    /// <param name="migrationHysteresisRatio">Per-axis dead zone as a fraction of cell size (default 0.05).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cellSize"/> is not positive, or the derived cell count does not fit a 32-bit cell key.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="worldMax"/> is not strictly greater than <paramref name="worldMin"/> on all three axes.</exception>
    public SpatialGridConfig(Vector3 worldMin, Vector3 worldMax, float cellSize, float migrationHysteresisRatio = 0.05f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        if (worldMax.X <= worldMin.X || worldMax.Y <= worldMin.Y || worldMax.Z <= worldMin.Z)
        {
            throw new ArgumentException("WorldMax must be strictly greater than WorldMin on all three axes.", nameof(worldMax));
        }

        WorldMin = worldMin;
        WorldMax = worldMax;
        CellSize = cellSize;
        MigrationHysteresisRatio = migrationHysteresisRatio;
        InverseCellSize = 1.0f / cellSize;

        GridWidth  = (int)MathF.Ceiling((worldMax.X - worldMin.X) * InverseCellSize);
        GridHeight = (int)MathF.Ceiling((worldMax.Y - worldMin.Y) * InverseCellSize);
        GridDepth  = (int)MathF.Ceiling((worldMax.Z - worldMin.Z) * InverseCellSize);

        // Computed in long deliberately: three axes multiply, and a silent int overflow here would produce a negative CellCount, a negative-length descriptor
        // array and an exception a long way from the configuration that caused it. The bound is the cell-key type, not memory — a 32-bit key is what every
        // consumer stores (ClusterCellMap, the profiler payloads, CellState lookups).
        long cellCount = (long)GridWidth * GridHeight * GridDepth;
        if (cellCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize),
                $"Grid dimensions {GridWidth} x {GridHeight} x {GridDepth} produce {cellCount} cells, which does not fit a 32-bit cell key. " +
                $"Use a larger cell size or a smaller world.");
        }

        CellCount = (int)cellCount;
    }

    /// <summary>
    /// Build a configuration for a <b>flat</b> world — one cell deep on Z, which is how a 2D game expresses itself to a 3D grid (C16). Z coordinates outside
    /// the single cell clamp into it, which is exactly what the grid did for every entity before it gained a third axis.
    /// </summary>
    /// <param name="worldMin">World-space minimum corner on X and Y (inclusive). Z is taken as 0.</param>
    /// <param name="worldMax">World-space maximum corner on X and Y (exclusive).</param>
    /// <param name="cellSize">Cell size in world units; must be &gt; 0.</param>
    /// <param name="migrationHysteresisRatio">Per-axis dead zone as a fraction of cell size (default 0.05).</param>
    public static SpatialGridConfig Flat(Vector2 worldMin, Vector2 worldMax, float cellSize, float migrationHysteresisRatio = 0.05f) =>
        new(new Vector3(worldMin, 0f), new Vector3(worldMax, cellSize), cellSize, migrationHysteresisRatio);
}
