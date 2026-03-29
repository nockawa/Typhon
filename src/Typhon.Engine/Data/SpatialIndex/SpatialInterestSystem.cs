using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-region observer configuration stored contiguously for cache-friendly iteration.
/// ~60 bytes — fits in one x86 cache line.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpatialObserverConfig
{
    public double MinX, MinY, MinZ;
    public double MaxX, MaxY, MaxZ;
    public uint CategoryMask;
    public byte Active;  // 0=destroyed/free, 1=active
    public byte _pad1, _pad2, _pad3;
    public int Generation;
}

/// <summary>Per-observer mutable state, separated from config for locality.</summary>
internal sealed class ObserverState
{
    internal long[] ChangeBuffer;
    internal int ChangeCount;
    internal long LastConsumedTick;
}

/// <summary>
/// Interest management system for a single ComponentTable's spatial index.
/// Uses inverted dirty-set processing: iterate dirty entities, test each against all observers.
/// O(dirty × observers) vs traditional O(totalInView × observers).
/// </summary>
internal sealed unsafe class SpatialInterestSystem
{
    // Observer storage — flat array with free-list
    private SpatialObserverConfig[] _configs;
    private ObserverState[] _states;
    private int _capacity;
    private int _activeCount;
    private int _freeHead;

    // Dirty bitmap ring buffer — archives tick fence snapshots
    private readonly DirtyBitmapRing _dirtyRing;

    // Shared scratch — accumulated dirty bitmap (reused across GetSpatialChanges calls)
    private long[] _accumScratch;

    // Owner references
    private readonly ComponentTable _table;
    private readonly SpatialIndexState _spatialState;

    private const int InitialCapacity = 8;
    private const int InitialChangeBufferSize = 64;

    internal SpatialInterestSystem(ComponentTable table, SpatialIndexState spatialState)
    {
        _table = table;
        _spatialState = spatialState;
        _configs = new SpatialObserverConfig[InitialCapacity];
        _states = new ObserverState[InitialCapacity];
        _capacity = InitialCapacity;
        _freeHead = -1;

        int initialWords = Math.Max(1, (table.ComponentSegment.AllocatedChunkCount + 63) >> 6);
        _dirtyRing = new DirtyBitmapRing(initialWords);
    }

    internal int ActiveObserverCount => _activeCount;
    internal DirtyBitmapRing DirtyRing => _dirtyRing;

    // ── Observer CRUD ────────────────────────────────────────────────────

    public SpatialObserverHandle RegisterObserver(ReadOnlySpan<double> bounds, uint categoryMask = 0, long initialTick = 0)
    {
        int index;
        if (_freeHead >= 0)
        {
            index = _freeHead;
            _freeHead = _configs[index].Generation;
        }
        else
        {
            if (_activeCount >= _capacity)
            {
                Grow();
            }
            index = _activeCount;
        }

        int coordCount = _spatialState.Descriptor.CoordCount;
        int halfCoord = coordCount >> 1;

        ref var config = ref _configs[index];
        config.MinX = bounds.Length > 0 ? bounds[0] : 0;
        config.MinY = bounds.Length > 1 ? bounds[1] : 0;
        config.MinZ = halfCoord == 3 && bounds.Length > 2 ? bounds[2] : 0;
        config.MaxX = bounds.Length > halfCoord ? bounds[halfCoord] : 0;
        config.MaxY = bounds.Length > halfCoord + 1 ? bounds[halfCoord + 1] : 0;
        config.MaxZ = halfCoord == 3 && bounds.Length > halfCoord + 2 ? bounds[halfCoord + 2] : 0;
        config.CategoryMask = categoryMask;
        config.Active = 1;
        config.Generation++;

        _states[index] = new ObserverState
        {
            ChangeBuffer = new long[InitialChangeBufferSize],
            LastConsumedTick = initialTick
        };
        _activeCount++;

        return new SpatialObserverHandle(index, config.Generation);
    }

    public void UnregisterObserver(SpatialObserverHandle handle)
    {
        ValidateHandle(handle);

        ref var config = ref _configs[handle.Index];
        config.Active = 0;
        _states[handle.Index] = null;

        config.Generation = _freeHead;
        _freeHead = handle.Index;
        _activeCount--;
    }

    public void UpdateObserverBounds(SpatialObserverHandle handle, ReadOnlySpan<double> newBounds)
    {
        ValidateHandle(handle);

        int coordCount = _spatialState.Descriptor.CoordCount;
        int halfCoord = coordCount >> 1;

        ref var config = ref _configs[handle.Index];
        config.MinX = newBounds.Length > 0 ? newBounds[0] : 0;
        config.MinY = newBounds.Length > 1 ? newBounds[1] : 0;
        config.MinZ = halfCoord == 3 && newBounds.Length > 2 ? newBounds[2] : 0;
        config.MaxX = newBounds.Length > halfCoord ? newBounds[halfCoord] : 0;
        config.MaxY = newBounds.Length > halfCoord + 1 ? newBounds[halfCoord + 1] : 0;
        config.MaxZ = halfCoord == 3 && newBounds.Length > halfCoord + 2 ? newBounds[halfCoord + 2] : 0;
    }

    // ── Delta Query ──────────────────────────────────────────────────────

    /// <summary>
    /// Get spatial changes visible to an observer since their last consumption tick.
    /// Uses inverted dirty-set iteration: enumerate dirty entities, test each against observer's interest region.
    /// Result spans are valid until the next GetSpatialChanges call for this observer.
    /// </summary>
    public SpatialChangeResult GetSpatialChanges(SpatialObserverHandle handle, long currentTick)
    {
        ValidateHandle(handle);

        var state = _states[handle.Index];
        ref var config = ref _configs[handle.Index];
        long lastTick = state.LastConsumedTick;

        // No new ticks since last consumption
        if (currentTick <= lastTick)
        {
            return SpatialChangeResult.Empty(currentTick);
        }

        // Stale check: observer too far behind → full sync (IM2)
        if (currentTick - lastTick > DirtyBitmapRing.RingSize)
        {
            return PerformFullSync(ref config, state, currentTick);
        }

        // If no new data in ring since last consumption → nothing changed
        long startTick = lastTick + 1;
        if (startTick > _dirtyRing.HeadTick)
        {
            return SpatialChangeResult.Empty(currentTick);
        }

        // If requested range fell off the ring → full sync
        if (_dirtyRing.HeadTick == 0 || !_dirtyRing.IsTickAvailable(startTick))
        {
            return PerformFullSync(ref config, state, currentTick);
        }

        // Clamp endTick to what's actually in the ring
        long endTick = Math.Min(currentTick, _dirtyRing.HeadTick);

        // Accumulate dirty bitmaps for the tick range
        int maxWords = _dirtyRing.MaxWordCount;
        if (maxWords == 0)
        {
            state.LastConsumedTick = currentTick;
            return SpatialChangeResult.Empty(currentTick);
        }

        EnsureAccumCapacity(maxWords);
        Array.Clear(_accumScratch, 0, maxWords);
        int accumWords = _dirtyRing.AccumulateDirty(startTick, endTick, _accumScratch);

        // Inverted iteration: for each dirty entity, test containment against this observer
        state.ChangeCount = 0;
        var desc = _spatialState.Descriptor;
        int coordCount = desc.CoordCount;
        var tree = _spatialState.ActiveTree;

        var guard = EpochGuard.Enter(_table.DBE.EpochManager);
        try
        {
            var bpAccessor = _spatialState.BackPointerSegment.CreateChunkAccessor();
            var treeAccessor = tree.Segment.CreateChunkAccessor();
            try
            {
                Span<double> coords = stackalloc double[coordCount];

                for (int wordIdx = 0; wordIdx < accumWords; wordIdx++)
                {
                    long word = _accumScratch[wordIdx];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount((ulong)word);
                        int chunkId = wordIdx * 64 + bit;
                        word &= word - 1;

                        // Read back-pointer
                        var bp = SpatialBackPointerHelper.Read(ref bpAccessor, chunkId);
                        if (bp.LeafChunkId == 0)
                        {
                            continue; // destroyed or never inserted
                        }

                        // Read leaf entry: coords + category mask + entityId (all from same page)
                        byte* leafBase = treeAccessor.GetChunkAddress(bp.LeafChunkId);
                        SpatialNodeHelper.ReadLeafEntryCoords(leafBase, bp.SlotIndex, coords, desc);
                        uint category = SpatialNodeHelper.ReadLeafCategoryMask(leafBase, bp.SlotIndex, desc);

                        // Category filter
                        if (config.CategoryMask != 0 && (category & config.CategoryMask) != config.CategoryMask)
                        {
                            continue;
                        }

                        // AABB overlap test
                        if (!OverlapsObserver(in config, coords, coordCount))
                        {
                            continue;
                        }

                        // Read EntityId directly from the leaf (stored alongside coords in SOA layout)
                        long entityId = SpatialNodeHelper.ReadLeafEntityId(leafBase, bp.SlotIndex, desc);

                        EnsureChangeBufferCapacity(state);
                        state.ChangeBuffer[state.ChangeCount++] = entityId;
                    }
                }
            }
            finally
            {
                treeAccessor.Dispose();
                bpAccessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }

        state.LastConsumedTick = currentTick;
        return new SpatialChangeResult(state.ChangeBuffer.AsSpan(0, state.ChangeCount), false, currentTick);
    }

    // ── Full-Sync Path ───────────────────────────────────────────────────

    private SpatialChangeResult PerformFullSync(ref SpatialObserverConfig config, ObserverState state, long currentTick)
    {
        state.ChangeCount = 0;
        var desc = _spatialState.Descriptor;
        int coordCount = desc.CoordCount;
        Span<double> queryCoords = stackalloc double[coordCount];
        BuildQueryCoords(in config, queryCoords, coordCount);

        var tree = _spatialState.ActiveTree;
        var guard = EpochGuard.Enter(_table.DBE.EpochManager);
        try
        {
            foreach (var hit in tree.QueryAABBOccupants(queryCoords, categoryMask: config.CategoryMask))
            {
                EnsureChangeBufferCapacity(state);
                state.ChangeBuffer[state.ChangeCount++] = hit.EntityId;
            }
        }
        finally
        {
            guard.Dispose();
        }

        state.LastConsumedTick = currentTick;
        return new SpatialChangeResult(state.ChangeBuffer.AsSpan(0, state.ChangeCount), true, currentTick);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateHandle(SpatialObserverHandle handle)
    {
        if ((uint)handle.Index >= (uint)_capacity || _configs[handle.Index].Generation != handle.Generation || _configs[handle.Index].Active == 0)
        {
            throw new ArgumentException($"Invalid or destroyed observer handle: {handle}");
        }
    }

    private void Grow()
    {
        int newCapacity = _capacity << 1;
        Array.Resize(ref _configs, newCapacity);
        Array.Resize(ref _states, newCapacity);
        _capacity = newCapacity;
    }

    private void EnsureAccumCapacity(int wordCount)
    {
        if (_accumScratch == null || _accumScratch.Length < wordCount)
        {
            _accumScratch = new long[wordCount];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureChangeBufferCapacity(ObserverState state)
    {
        if (state.ChangeCount >= state.ChangeBuffer.Length)
        {
            Array.Resize(ref state.ChangeBuffer, state.ChangeBuffer.Length << 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildQueryCoords(in SpatialObserverConfig config, Span<double> coords, int coordCount)
    {
        int halfCoord = coordCount >> 1;
        coords[0] = config.MinX;
        coords[1] = config.MinY;
        coords[halfCoord] = config.MaxX;
        coords[halfCoord + 1] = config.MaxY;
        if (halfCoord == 3)
        {
            coords[2] = config.MinZ;
            coords[halfCoord + 2] = config.MaxZ;
        }
    }

    /// <summary>Test if entity's fat AABB (from leaf coords) overlaps the observer's interest region.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OverlapsObserver(in SpatialObserverConfig obs, ReadOnlySpan<double> coords, int coordCount)
    {
        if (coordCount == 4)
        {
            // 2D: coords = [minX, minY, maxX, maxY]
            return coords[2] >= obs.MinX && coords[0] <= obs.MaxX && coords[3] >= obs.MinY && coords[1] <= obs.MaxY;
        }
        // 3D: coords = [minX, minY, minZ, maxX, maxY, maxZ]
        return coords[3] >= obs.MinX && coords[0] <= obs.MaxX && coords[4] >= obs.MinY && coords[1] <= obs.MaxY && coords[5] >= obs.MinZ && coords[2] <= obs.MaxZ;
    }
}
