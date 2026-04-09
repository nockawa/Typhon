using System;

namespace Typhon.Engine;

/// <summary>
/// Flat-array pool holding per-cell cluster lists. Each cell gets a contiguous segment inside <see cref="_pool"/>; iteration is a single sequential read,
/// matching the layout the design doc describes as "Option B: Compact array per cell" in <c>claude/design/spatial-tiers/01-spatial-clusters.md</c>.
/// </summary>
/// <remarks>
/// <para>Growth strategy: each cell starts with zero capacity. On the first insert we allocate a small tail segment (capacity 4) at the
/// current <see cref="_tail"/> offset and record its head. When that segment fills up we allocate a new tail segment at 2× capacity, copy the old entries
/// across, and update <see cref="CellDescriptor.ClusterListHead"/>. The abandoned segment becomes dead space inside the pool — acceptable because cell cluster
/// counts change slowly, cell grids are small (a few hundred KB), and compacting would complicate lookups without any measurable benefit at our scales.</para>
/// <para>Removal uses swap-with-last — <see cref="CellDescriptor.ClusterCount"/> shrinks; the last entry in the segment moves into the vacated slot.
/// This means clusters attached to a cell have no stable index inside the pool; callers must not cache positions.</para>
/// </remarks>
internal sealed class CellClusterPool
{
    private int[] _pool;
    private int _tail;

    /// <summary>
    /// Parallel array holding the allocated capacity of each cell's segment. Indexed by the cell key, same indexing as <see cref="CellDescriptor.ClusterListHead"/>.
    /// </summary>
    private readonly int[] _cellCapacities;

    public CellClusterPool(int cellCount, int initialPoolCapacity = 256)
    {
        if (cellCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCount));
        }

        _pool = new int[Math.Max(initialPoolCapacity, 16)];
        _tail = 0;
        _cellCapacities = new int[cellCount];
    }

    /// <summary>Number of ints currently allocated inside the pool (including dead tail segments).</summary>
    public int PoolTail => _tail;

    /// <summary>Total allocated pool size, in ints. Used by tests.</summary>
    public int PoolCapacity => _pool.Length;

    /// <summary>
    /// Read-only span of the cluster chunk IDs currently attached to <paramref name="cell"/>. May be empty.
    /// </summary>
    public ReadOnlySpan<int> GetClusters(in CellDescriptor cell)
    {
        if (cell.ClusterCount == 0 || cell.ClusterListHead < 0)
        {
            return ReadOnlySpan<int>.Empty;
        }
        return _pool.AsSpan(cell.ClusterListHead, cell.ClusterCount);
    }

    /// <summary>
    /// Append <paramref name="clusterChunkId"/> to the list attached to <paramref name="cell"/>,
    /// growing the cell's segment if necessary.
    /// </summary>
    public void AddCluster(ref CellDescriptor cell, int cellKey, int clusterChunkId)
    {
        int capacity = _cellCapacities[cellKey];
        if (cell.ClusterListHead < 0 || cell.ClusterCount >= capacity)
        {
            GrowCellSegment(ref cell, cellKey, ref capacity);
        }

        _pool[cell.ClusterListHead + cell.ClusterCount] = clusterChunkId;
        cell.ClusterCount++;
    }

    /// <summary>
    /// Remove <paramref name="clusterChunkId"/> from the list attached to <paramref name="cell"/> using swap-with-last.
    /// Returns <c>false</c> if the cluster is not in the list.
    /// </summary>
    public bool RemoveCluster(ref CellDescriptor cell, int clusterChunkId)
    {
        if (cell.ClusterCount == 0 || cell.ClusterListHead < 0)
        {
            return false;
        }

        var span = _pool.AsSpan(cell.ClusterListHead, cell.ClusterCount);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != clusterChunkId)
            {
                continue;
            }

            // Swap-with-last (no-op when i is already the last entry)
            span[i] = span[^1];
            cell.ClusterCount--;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drop every cell's segment and reset the pool tail. Used by <c>SpatialGrid.ResetCellState</c> before <c>RebuildCellState</c>.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_cellCapacities);
        _tail = 0;
    }

    private void GrowCellSegment(ref CellDescriptor cell, int cellKey, ref int capacity)
    {
        int newCapacity = capacity == 0 ? 4 : capacity * 2;
        EnsurePoolCapacity(_tail + newCapacity);

        int newHead = _tail;
        if (cell.ClusterCount > 0)
        {
            // Copy the existing entries into the fresh tail segment. The old segment leaks as dead space — see class remarks.
            Array.Copy(_pool, cell.ClusterListHead, _pool, newHead, cell.ClusterCount);
        }

        cell.ClusterListHead = newHead;
        _tail += newCapacity;
        _cellCapacities[cellKey] = newCapacity;
        capacity = newCapacity;
    }

    private void EnsurePoolCapacity(int required)
    {
        if (required <= _pool.Length)
        {
            return;
        }
        int newSize = _pool.Length;
        while (newSize < required)
        {
            newSize *= 2;
        }
        Array.Resize(ref _pool, newSize);
    }
}
