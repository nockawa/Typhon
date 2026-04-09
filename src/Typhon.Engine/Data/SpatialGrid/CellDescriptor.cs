using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-cell runtime state for the shared spatial grid. 16 bytes, one per cell.
/// </summary>
/// <remarks>
/// <para>All cell state is transient — it is rebuilt at startup from entity positions (Decisions Q2 and Q6 in <c>claude/design/spatial-tiers/01-spatial-clusters.md</c>).
/// Nothing here is persisted to disk.</para>
/// <para>For a 100x100 grid (10 000 cells) the descriptor array is 160 KB — fits in L2. For a Morton-padded 128x128 grid (16 384 cells) it's 256 KB.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct CellDescriptor
{
    /// <summary>SimTier value assigned by the game code each tick. Reserved — not used in Phase 1+2.</summary>
    public byte Tier;

    /// <summary>Flag byte for future use (checkerboard colour, dirty marker, etc.). Reserved in Phase 1+2.</summary>
    public byte Flags;

    /// <summary>Padding to keep the struct 16-byte aligned.</summary>
    public ushort Reserved;

    /// <summary>
    /// Start index of this cell's cluster list inside <see cref="CellClusterPool"/>. <c>-1</c> when the cell
    /// has no clusters (the pool has not allocated a segment for it yet).
    /// </summary>
    public int ClusterListHead;

    /// <summary>Number of clusters currently attached to this cell (length of the list span).</summary>
    public int ClusterCount;

    /// <summary>
    /// Sum of <c>PopCount(OccupancyBits)</c> across every cluster attached to this cell. Maintained incrementally
    /// by <see cref="ArchetypeClusterState.ClaimSlotInCell"/> / slot release, and by <c>RebuildCellState</c> at startup.
    /// </summary>
    public int EntityCount;
}
