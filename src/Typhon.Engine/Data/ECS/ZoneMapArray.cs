using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Per-archetype per-indexed-field zone map for cluster-level query pruning.
/// Maintains min/max bounds per cluster; allows queries to skip clusters entirely when the query range doesn't overlap the cluster's [min, max] interval.
/// </summary>
/// <remarks>
/// <para>Zone maps are NOT persisted — rebuilt from cluster data on reopen/recovery.</para>
/// <para>Maintenance: lazy full recompute at tick fence for dirty clusters; eager widen on spawn.</para>
/// <para>Staleness: between tick fences, bounds may be wider than actual data (destroyed boundary entity lingers).
/// False positives acceptable (cluster checked but no match). False negatives impossible.</para>
/// </remarks>
internal sealed unsafe class ZoneMapArray
{
    private long[] _mins;       // [clusterChunkId] → min value (raw bits, sign-flipped for float ordering)
    private long[] _maxs;       // [clusterChunkId] → max value (raw bits, sign-flipped for float ordering)
    private bool[] _valid;      // [clusterChunkId] → true if min/max are initialized
    private int _capacity;
    private readonly int _fieldSize;
    private readonly bool _isFloat;
    private readonly bool _isDouble;

    internal ZoneMapArray(int initialCapacity, int fieldSize, bool isFloat, bool isDouble)
    {
        _capacity = Math.Max(16, initialCapacity);
        _mins = new long[_capacity];
        _maxs = new long[_capacity];
        _valid = new bool[_capacity];
        _fieldSize = fieldSize;
        _isFloat = isFloat;
        _isDouble = isDouble;
    }

    /// <summary>
    /// Recompute min/max for a single cluster by scanning all occupied entities.
    /// Called at tick fence for each dirty cluster.
    /// </summary>
    public void Recompute(int clusterChunkId, byte* clusterBase, ArchetypeClusterInfo layout, int compSlot, int fieldOffset)
    {
        EnsureCapacity(clusterChunkId);

        ulong occupancy = *(ulong*)clusterBase;
        if (occupancy == 0)
        {
            _valid[clusterChunkId] = false;
            return;
        }

        int compSize = layout.ComponentSize(compSlot);
        byte* compBase = clusterBase + layout.ComponentOffset(compSlot);

        long min = long.MaxValue;
        long max = long.MinValue;
        ulong bits = occupancy;

        while (bits != 0)
        {
            int slotIndex = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            byte* fieldPtr = compBase + slotIndex * compSize + fieldOffset;
            long val = ReadFieldAsOrderedLong(fieldPtr);
            if (val < min)
            {
                min = val;
            }
            if (val > max)
            {
                max = val;
            }
        }

        _mins[clusterChunkId] = min;
        _maxs[clusterChunkId] = max;
        _valid[clusterChunkId] = true;
    }

    /// <summary>
    /// Widen bounds to include a new value (eager, on spawn). Never narrows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Widen(int clusterChunkId, byte* fieldPtr)
    {
        EnsureCapacity(clusterChunkId);
        long val = ReadFieldAsOrderedLong(fieldPtr);

        if (!_valid[clusterChunkId])
        {
            _mins[clusterChunkId] = val;
            _maxs[clusterChunkId] = val;
            _valid[clusterChunkId] = true;
            return;
        }

        if (val < _mins[clusterChunkId])
        {
            _mins[clusterChunkId] = val;
        }
        if (val > _maxs[clusterChunkId])
        {
            _maxs[clusterChunkId] = val;
        }
    }

    /// <summary>
    /// Check if a cluster's zone map overlaps the query range [queryMin, queryMax].
    /// Returns true if the cluster MAY contain matching entities (or if the zone map is not initialized).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MayContain(int clusterChunkId, long queryMin, long queryMax)
    {
        if ((uint)clusterChunkId >= (uint)_capacity || !_valid[clusterChunkId])
        {
            return true; // Unknown → don't skip (conservative)
        }

        // Standard interval overlap: !(clusterMax < queryMin || clusterMin > queryMax)
        return _maxs[clusterChunkId] >= queryMin && _mins[clusterChunkId] <= queryMax;
    }

    /// <summary>
    /// Invalidate a cluster's zone map (e.g., when cluster is freed).
    /// </summary>
    public void Invalidate(int clusterChunkId)
    {
        if ((uint)clusterChunkId < (uint)_capacity)
        {
            _valid[clusterChunkId] = false;
        }
    }

    /// <summary>
    /// Read a field value as a long that preserves sort order across types.
    /// For floats: sign-flip so that negative floats sort before positive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadFieldAsOrderedLong(byte* ptr)
    {
        if (_isFloat)
        {
            return FloatToOrderedLong(*(float*)ptr);
        }
        if (_isDouble)
        {
            return DoubleToOrderedLong(*(double*)ptr);
        }
        return _fieldSize switch
        {
            1 => *(byte*)ptr,
            2 => *(short*)ptr,
            4 => *(int*)ptr,
            8 => *(long*)ptr,
            _ => *(int*)ptr, // fallback
        };
    }

    // Float ordering: flip all bits if negative (sign bit set), else flip only sign bit.
    // This converts IEEE 754 to a representation where memcmp order = numeric order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FloatToOrderedLong(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? ~bits : bits ^ int.MinValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DoubleToOrderedLong(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits < 0 ? ~bits : bits ^ long.MinValue;
    }

    private void EnsureCapacity(int index)
    {
        if (index >= _capacity)
        {
            int newCap = Math.Max(_capacity * 2, index + 1);
            Array.Resize(ref _mins, newCap);
            Array.Resize(ref _maxs, newCap);
            Array.Resize(ref _valid, newCap);
            _capacity = newCap;
        }
    }
}
