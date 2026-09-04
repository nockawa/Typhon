using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster accessor providing typed, zero-copy access to component SoA arrays.
/// Created by <see cref="ClusterEnumerator{TArch}"/>. Must not outlive the enumerator.
/// </summary>
/// <remarks>
/// <para>Component data is laid out in Structure-of-Arrays format within the cluster:
/// <c>Component₀[N], Component₁[N], ...</c> where N is the cluster size (8..64).</para>
/// <para>Iteration pattern using OccupancyBits TZCNT loop:</para>
/// <code>
/// ulong bits = cluster.OccupancyBits;
/// while (bits != 0)
/// {
///     int idx = BitOperations.TrailingZeroCount(bits);
///     bits &amp;= bits - 1;
///     ref var pos = ref cluster.Get(Ant.Position, idx);
///     // ...
/// }
/// </code>
/// </remarks>
[PublicAPI]
public unsafe ref struct ClusterRef<TArch> where TArch : class
{
    private readonly byte* _base;
    private readonly byte* _transientBase;  // TransientStore cluster base; null for pure-SV/V or pure-Transient (where _base IS TS)
    private readonly ArchetypeClusterInfo _layout;
    private readonly ArchetypeMetadata _meta;
    private readonly int _chunkId;
    private readonly ArchetypeClusterState _state; // null only on synthetic test refs; carries spatial bookkeeping + grid

    // ── Cached cell frame (#872 step 9) ────────────────────────────────────
    //
    // The cluster's cell origin is a per-CLUSTER constant, and WriteSpatial is called per ENTITY. Resolving it inside the write meant two array loads, a
    // bounds check and a CellKeyToCoords — itself a volatile load plus two derefs into the cell's CellState line — for every ant, every tick, inlined into
    // the simulation barrier. A ClusterRef is created once per cluster and then written through many times (AntHill: `foreach (var cluster in clusters)`
    // with a slot loop inside), so caching on the ref moves that work from O(entities) to O(clusters).
    //
    // Lazy rather than resolved in the constructor: the constructor runs for every cluster access including the read-only query paths, which never need an
    // origin. Cheap to keep valid — the cluster's cell cannot change while the ref is alive (a cluster is assigned a cell at creation and only ever released
    // to -1; migration moves ENTITIES between clusters, never a cluster between cells).
    private const int CellFrameUnresolved = -2;
    private int _cachedCellKey;
    private float _cachedOriginX;
    private float _cachedOriginY;
    private float _cachedOriginZ;

    internal ClusterRef(byte* basePtr, byte* transientBasePtr, ArchetypeClusterInfo layout, ArchetypeMetadata meta, int chunkId, ArchetypeClusterState state)
    {
        _base = basePtr;
        _transientBase = transientBasePtr;
        _layout = layout;
        _meta = meta;
        _chunkId = chunkId;
        _state = state;
        _cachedCellKey = CellFrameUnresolved;
        _cachedOriginX = 0f;
        _cachedOriginY = 0f;
        _cachedOriginZ = 0f;
    }

    /// <summary>Bitmask of occupied slots. Bit i = 1 means slot i contains a live entity.</summary>
    public ulong OccupancyBits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => *(ulong*)_base;
    }

    /// <summary>Bitmask of entities with component at <paramref name="slot"/> enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong EnabledBits(int slot) => *(ulong*)(_base + _layout.EnabledBitsOffset(slot));

    /// <summary>Combined mask: alive AND component at slot enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ActiveBits(int slot) => OccupancyBits & EnabledBits(slot);

    /// <summary>Number of live entities in this cluster (PopCount of OccupancyBits).</summary>
    public int LiveCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitOperations.PopCount(OccupancyBits);
    }

    /// <summary>True when all slots are occupied.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OccupancyBits == _layout.FullMask;
    }

    /// <summary>Cluster size N (number of slots, 8..64).</summary>
    public int ClusterSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _layout.ClusterSize;
    }

    /// <summary>Full mask with lower N bits set.</summary>
    public ulong FullMask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _layout.FullMask;
    }

    /// <summary>Resolve the correct base pointer for a component slot (Transient → _transientBase, else → _base).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ResolveBase(byte slot) => (_transientBase != null && (_meta.TransientSlotMask & (1 << slot)) != 0) ? _transientBase : _base;

    /// <summary>
    /// Assert that <typeparamref name="T"/> strides the column exactly. Every accessor below hands out a <c>Span&lt;T&gt;</c> or a <c>ref T</c>, both of which
    /// step by <c>sizeof(T)</c>; if the column was laid out at a different stride, slot <c>i</c> is addressed at the wrong offset and a write spills into the
    /// neighbouring slot — silently, in Release (#816). The layout has matched <c>sizeof(T)</c> since <c>DBComponentDefinition.Build</c> started taking the CLR
    /// size, so in practice this fires only when the <see cref="Comp{T}"/> handle names a component that is not <typeparamref name="T"/>.
    /// <para>Inline-guard form: <see cref="CheckConfig.Enabled"/> is a <c>static readonly bool</c> that defaults to <see langword="false"/> and is set from
    /// configuration, not from the build flavour — so the JIT folds the whole check away in any build that leaves strict mode off, and the interpolated
    /// message is built only on the throw path.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckStride<T>(byte slot) where T : unmanaged
    {
        if (CheckConfig.Enabled && sizeof(T) != _layout.ComponentSize(slot))
        {
            ThrowHelper.ThrowInvalidOp(
                $"Component at slot {slot} has a column stride of {_layout.ComponentSize(slot)} bytes but {typeof(T).Name} is {sizeof(T)} bytes. "
              + $"The Comp<T> handle most likely names a different component.");
        }
    }

    /// <summary>
    /// Get a mutable span of the component's data across all N slots (its SoA array). For Versioned components use <see cref="GetReadOnlySpan{T}"/> instead —
    /// writing directly to the cluster slot bypasses the revision chain and breaks MVCC snapshot isolation.
    /// </summary>
    /// <typeparam name="T">Component value type.</typeparam>
    /// <param name="comp">Handle identifying the component within the archetype.</param>
    /// <returns>A mutable span of length <see cref="ClusterSize"/> over the component's SoA array.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown, when strict checks are enabled (<see cref="CheckConfig.Enabled"/>), if <typeparamref name="T"/> is a Versioned component.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetSpan<T>(Comp<T> comp) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        if (CheckConfig.Enabled && (_meta.VersionedSlotMask & (1 << slot)) != 0)
        {
            ThrowHelper.ThrowInvalidOp(
                $"GetSpan on Versioned component bypasses revision chain. Use GetReadOnlySpan for reads, OpenMut+Write for writes.");
        }
        CheckStride<T>(slot);

        // ── Handing out a mutable span over the SPATIAL column is a promise that positions may move ─────────────────
        //
        // 🔴 This is the one write path that signals nothing. WriteSpatial sets the process bit when the bound grew or a
        // crossing fired; OpenMut sets the dirty bit; a destroy now flags a shrink. GetSpan sets NONE of them — its own
        // contract is that the caller opts in via MarkDirty, and ClusterSpatialTests'
        // TickFence_DirectSpanWrite_NoMarkSlotDirty_AABB_StillRefreshed exists precisely because real callers do not
        // (AntHill's chase loop: a 1 000-radius query found the ant, an 80-radius kill missed it, because the cluster's
        // AABB was frozen at spawn while the entities walked away — a silent zero-hit query, not a crash).
        //
        // The engine's answer used to be that the fence re-derived EVERY active cluster's bound every tick, which made
        // the missing signal free and invisible. That walk is what the dirty gate removes, so the signal has to become
        // real. One Interlocked.Or per GetSpan call — per CLUSTER, not per entity, on a call that is already resolving
        // a slot and computing a base pointer — buys back exactly the coverage the full rescan was providing.
        //
        // 🔴 A PER-ARCHETYPE flag, not a per-cluster process bit, and the difference is the whole design.
        //
        // GetSpan returns a MUTABLE span; it does not observe whether the caller writes through it, and plenty of callers
        // do not — ClusterRepairTests.ReadAll enumerates every cluster and reads positions through exactly this method.
        // Setting the process bit here therefore marks a cluster "visit and republish" on a pure READ, which takes it past
        // the !boundsMoved skip into the outlier guard and drift detection. Measured: doing that made a read-only helper
        // relocate entities, reddening ARepairIsNeverBegunWithoutTheBudgetToFinishIt with "a refused repair still moved
        // entities". A read must not perturb the partition.
        //
        // So the claim recorded here is the weakest one that is actually true: "somebody was handed the ability to move
        // this archetype's positions without telling us which cluster". The refresh answers it by doing what it did before
        // the dirty gate existed — walking every active cluster once, this tick. An archetype that uses GetSpan on its
        // spatial column keeps exactly today's cost and today's behaviour; one that does not gets the gate. Nothing
        // regresses, and the AntHill case (TickFence_DirectSpanWrite_NoMarkSlotDirty_AABB_StillRefreshed) stays covered
        // for the reason it always was.
        //
        // Cleared in Finalize, after the refresh has consumed it — see ClearAabbRefreshBookkeeping.
        if (_state.SpatialSlot.HasSpatialIndex && _state.SpatialSlot.Slot == slot)
        {
            Volatile.Write(ref _state.SpatialSpanHandedOut, 1);
        }

        return new Span<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>
    /// Mark every occupied slot in this cluster dirty for ONE component column — the columnar counterpart to what <c>EntityRef.Write</c> does per entity.
    /// <para>
    /// <b>Required after writing through <see cref="GetSpan{T}"/> for any durable (SingleVersion) component.</b> The direct cluster path sets no dirty bits,
    /// so without this the tick fence never serialises the change and it is lost on reopen. Transient components need no call — they are never persisted.
    /// </para>
    /// <para>
    /// Prefer this over <c>ClusterEnumerator.MarkCurrentDirty()</c>, which cannot name a component and therefore widens the archetype's written-slot union to
    /// "all slots" — that union is what narrows the columns a FenceBlock emits (#559), so losing it inflates WAL volume for every entity in the archetype.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Component value type.</typeparam>
    /// <param name="comp">Handle identifying the component column that was written.</param>
    public void MarkDirty<T>(Comp<T> comp) where T : unmanaged
    {
        var componentSlot = _meta.GetSlot(comp._componentTypeId);
        var bits = OccupancyBits;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            _state.SetDirty(_chunkId, slot, componentSlot);
        }
    }

    /// <summary>Get a read-only span of component data for all N slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetReadOnlySpan<T>(Comp<T> comp) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        CheckStride<T>(slot);
        return new ReadOnlySpan<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>Get a mutable reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        if (CheckConfig.Enabled && (_meta.VersionedSlotMask & (1 << slot)) != 0)
        {
            ThrowHelper.ThrowInvalidOp($"Get on Versioned component bypasses revision chain. Use OpenMut+Write for writes.");
        }
        CheckStride<T>(slot);
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Get a read-only reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetReadOnly<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        CheckStride<T>(slot);
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Entity keys for all N slots. Use with slot index to reconstruct EntityId.</summary>
    public ReadOnlySpan<long> EntityIds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_base + _layout.EntityIdsOffset, _layout.ClusterSize);
    }

    /// <summary>Read EntityId for the entity at the given slot (stored as full packed EntityId).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityId GetEntityId(int slotIndex) =>
        EntityId.FromRaw(*(long*)(_base + _layout.EntityIdsOffset + slotIndex * 8));

    /// <summary>The chunk ID of this cluster within the archetype's segment.</summary>
    public int ChunkId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _chunkId;
    }

    /// <summary>
    /// Tight AABB of all entities in this cluster, in <b>world</b> coordinates. Returns the empty sentinel (min = +inf, max = -inf) when the archetype has
    /// no spatial index, or when the cluster is not attached to a cell. For 2D archetypes, MinZ/MaxZ are ±infinity sentinels — use MinX/MinY/MaxX/MaxY only.
    /// </summary>
    /// <remarks>
    /// <para><b>Returns a value, not a <c>ref readonly</c>, since #872 step 9.</b> The engine stores these bounds <c>C15</c> cell-relative, and this property
    /// is the boundary where they become world coordinates again. A reference cannot convert, and leaving it as one would have silently changed what every
    /// existing caller receives: the AntHill rock gather (<c>TyphonBridge</c>) and the SpaceBattle renderer and camera cull all compare this box against
    /// world positions, so they would have kept compiling and started answering wrongly by exactly the distance to the cell's origin.</para>
    /// <para>The cost is a 28-byte copy plus two dependent loads for the cell origin, against returning a reference. That is the right trade for a public
    /// property: callers reading it per cluster per frame can afford it, and a caller that wants the raw stored frame is inside the engine and can use
    /// <see cref="CellRelativeBounds"/>.</para>
    /// </remarks>
    public ClusterSpatialAabb SpatialBounds
    {
        get
        {
            if (_state?.ClusterAabbs == null || (uint)_chunkId >= (uint)_state.ClusterAabbs.Length)
            {
                return ClusterSpatialAabb.Empty;
            }

            var box = _state.ClusterAabbs[_chunkId];
            if (!TryGetCellOrigin(out float originX, out float originY, out float originZ))
            {
                // No cell means the stored value is the Empty sentinel rather than a bound in some other frame — see the spawn union in
                // Transaction.ECS, which leaves it untouched when a cluster has no cell. Returning it unconverted is correct, not a fallback.
                return box;
            }

            box.MinX = ClusterSpatialAabb.ToWorld(box.MinX, originX);
            box.MinY = ClusterSpatialAabb.ToWorld(box.MinY, originY);
            box.MinZ = ClusterSpatialAabb.ToWorld(box.MinZ, originZ);
            box.MaxX = ClusterSpatialAabb.ToWorld(box.MaxX, originX);
            box.MaxY = ClusterSpatialAabb.ToWorld(box.MaxY, originY);
            box.MaxZ = ClusterSpatialAabb.ToWorld(box.MaxZ, originZ);
            return box;
        }
    }

    /// <summary>
    /// The cluster's bounds in the <c>C15</c> CELL-RELATIVE frame they are stored in — engine-internal. Use <see cref="SpatialBounds"/> outside.
    /// </summary>
    internal ref readonly ClusterSpatialAabb CellRelativeBounds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref (_state?.ClusterAabbs != null ? ref _state.ClusterAabbs[_chunkId] : ref ClusterSpatialAabb.s_empty);
    }

    /// <summary>
    /// Write-barrier API for spatial components — the canonical replacement for <c>cluster.GetSpan&lt;T&gt;()[slotIndex] = ...</c> when <c>T</c> contains the
    /// archetype's <see cref="SpatialIndexAttribute"/>-marked field. Performs (in order):
    /// <list type="number">
    /// <item>Reads the OLD spatial-field bytes at <paramref name="slotIndex"/></item>
    /// <item>Writes <paramref name="newValue"/> to the slot</item>
    /// <item>Updates <see cref="ArchetypeClusterState.ClusterAabbs"/> inline on AABB grow (O(1) CAS per axis)</item>
    /// <item>Flags <see cref="ArchetypeClusterState.ClusterShrinkPendingAxes"/> for axes where this slot was at an extreme and moved inward — fence rescans
    ///       only this cluster on those axes</item>
    /// <item>Flags <see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/> when the new position crosses the cell+hysteresis boundary — fence drains
    ///       the migration without any full scan</item>
    /// <item>Sets the cluster's bit in <see cref="ArchetypeClusterState.ClusterProcessBitmap"/> so the fence loop visits this cluster</item>
    /// </list>
    /// <para>
    /// V1 supports <see cref="SpatialFieldType.AABB2F"/> only (AntHill's <c>WorldBounds</c>). Other field types throw <see cref="NotSupportedException"/>.
    /// </para>
    /// <para>
    /// <b>WriteSpatial does NOT mark the slot dirty</b> (via <see cref="ArchetypeClusterState.SetDirty(int, int, int)"/>).
    /// The dirty bitmap drives WAL serialization and change-filtered dispatch — for high-frequency  simulation state (e.g., AntHill's ant positions), marking
    /// every slot dirty floods the WAL writer with one frame per entity per tick → backpressure that stalls TickDriver. The fence-time spatial maintenance does
    /// not need the dirty bit; it consumes <see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/> /
    /// <see cref="ArchetypeClusterState.ClusterProcessBitmap"/> directly. If your workload genuinely needs WAL persistence of the spatial field (e.g.,
    /// resumable autosave), either write through the MVCC <c>Transaction.OpenMut + Write</c> path (which marks dirty), or
    /// call <see cref="ArchetypeClusterState.SetDirty(int, int, int)"/> explicitly after <c>WriteSpatial</c>.
    /// </para>
    /// <para>
    /// Thread safety: safe to call concurrently from multiple workers operating on different slots of any cluster (including the same cluster). All
    /// bookkeeping writes use <see cref="Interlocked"/> primitives.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpatial<T>(Comp<T> comp, int slotIndex, in T newValue) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        // Hottest path (AntHill per-entity-per-tick spatial write): inline-guard form so the gate JIT-folds to nothing when strict mode is off (#422 AC#6).
        if (CheckConfig.Enabled && (_meta.VersionedSlotMask & (1 << slot)) != 0)
        {
            ThrowHelper.ThrowInvalidOp($"WriteSpatial on Versioned component bypasses revision chain.");
        }
        if (CheckConfig.Enabled && (_state == null || !_state.SpatialSlot.HasSpatialIndex || _state.SpatialSlot.Slot != slot))
        {
            ThrowHelper.ThrowInvalidOp(
                $"WriteSpatial requires the archetype's spatial-indexed component (marked [SpatialIndex]). For non-spatial fields, use GetSpan or Get.");
        }

        CheckStride<T>(slot);

        var spatialSlot = _state.SpatialSlot;
        var slotBytes = ResolveBase(slot) + _layout.ComponentOffset(slot) + slotIndex * sizeof(T);
        var fieldPtr = slotBytes + spatialSlot.FieldOffset;

        var fieldType = spatialSlot.FieldInfo.FieldType;
        if (fieldType == SpatialFieldType.AABB2F)
        {
            WriteSpatialAabb2F(slotIndex, slotBytes, fieldPtr, in newValue);
        }
        else
        {
            // TODO: specialize AABB3F / BSphere2F / BSphere3F / double variants.
            throw new NotSupportedException($"WriteSpatial: spatial field type {fieldType} not yet supported. V1 supports AABB2F only.");
        }
    }

    /// <summary>AABB2F specialization of <see cref="WriteSpatial{T}"/>. Inlined into the barrier on the AntHill hot path (WorldBounds.Bounds is AABB2F,
    /// point-form-encoded with MinX==MaxX, MinY==MaxY).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialAabb2F<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        // Read old AABB before overwriting (fieldPtr points at the AABB2F inside the component).
        ref var oldAabb = ref *(AABB2F*)fieldPtr;
        var oldMinX = oldAabb.MinX;
        var oldMinY = oldAabb.MinY;
        var oldMaxX = oldAabb.MaxX;
        var oldMaxY = oldAabb.MaxY;

        // Write the new value (full T struct, may include non-spatial fields).
        *(T*)slotBytes = newValue;

        // Re-read the AABB2F from the freshly-written value (handles offset within T).
        ref var newAabb = ref *(AABB2F*)fieldPtr;
        var newMinX = newAabb.MinX;
        var newMinY = newAabb.MinY;
        var newMaxX = newAabb.MaxX;
        var newMaxY = newAabb.MaxY;

        // NOTE: WriteSpatial deliberately does NOT call _state.SetDirty for the spatial slot. The dirty bitmap drives WAL serialization and change-filtered
        // dispatch; for cluster archetypes where the spatial component is high-frequency simulation state (e.g., AntHill's WorldBounds), marking every slot
        // dirty floods the WAL with 100k frames/tick → backpressure. The fence-time spatial maintenance does NOT need the dirty bit — it consumes
        // ClusterMigrationPendingSlots / ClusterProcessBitmap directly. Callers that genuinely need WAL persistence of the spatial field should mutate it via
        // the MVCC Transaction path (which marks dirty), or call _state.SetDirty explicitly after WriteSpatial. See claude/design/spatial/write-time-spatial.md.

        // Step 4 + 5: AABB grow inline (CAS) and shrink flag.
        //
        // C15 (#872 step 9): ClusterAabbs holds CELL-RELATIVE bounds, so the entity's world coordinates have to be rebased before they can be compared with
        // — let alone CAS'd into — the stored extremes. The origin costs two dependent loads (ClusterCellMap, then the cell's own coordinates out of its
        // CellState) and is needed by the migration check below regardless, so it is resolved once here and handed to both.
        bool haveOrigin = TryGetCellOrigin(out int cellKey, out float originX, out float originY, out float originZ);
        var aabbChanged = false;
        if (haveOrigin)
        {
            ref var stored = ref _state.ClusterAabbs[_chunkId];
            aabbChanged = MaybeGrowAndFlagShrink(ref stored,
                ClusterSpatialAabb.ToCellRelativeMin(oldMinX, originX), ClusterSpatialAabb.ToCellRelativeMin(oldMinY, originY),
                ClusterSpatialAabb.ToCellRelativeMax(oldMaxX, originX), ClusterSpatialAabb.ToCellRelativeMax(oldMaxY, originY),
                ClusterSpatialAabb.ToCellRelativeMin(newMinX, originX), ClusterSpatialAabb.ToCellRelativeMin(newMinY, originY),
                ClusterSpatialAabb.ToCellRelativeMax(newMaxX, originX), ClusterSpatialAabb.ToCellRelativeMax(newMaxY, originY));
        }

        // Step 6: migration check.
        // centerZ is 0 because this specialization handles AABB2F and WriteSpatial supports nothing else yet. It matches ReadSpatialCenter3D, which reports
        // posZ = 0 for both 2D field types — so a write-time check and a fence-time check place the same entity in the same Z plane. When AABB3F lands here,
        // this must pass the real centre or the two will disagree.
        var migrationFlagged = haveOrigin && MaybeFlagMigration(slotIndex, cellKey, originX, originY, originZ, newMinX, newMinY, newMaxX, newMaxY, 0f);

        // Step 6b: bump the fence work-planner's migration cost hint. Non-atomic: an order-of-magnitude approximation is enough for chunk bucketing; lost
        // increments under contention are tolerable.
        if (migrationFlagged)
        {
            _state.MigrationHint++;
        }

        // Step 7: visibility for the fence loop.
        if (aabbChanged || migrationFlagged)
        {
            SetClusterProcessBit();
        }
    }

    /// <summary>
    /// Inline AABB-grow (CAS per axis) + shrink-pending-axes flag. Returns true when either axis-extreme moved (in which case the cluster needs a
    /// fence-time <c>PerCellIndex.UpdateAt</c> with the fresh AABB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeGrowAndFlagShrink(ref ClusterSpatialAabb stored, float oldMinX, float oldMinY, float oldMaxX, float oldMaxY, float newMinX, float newMinY, 
        float newMaxX, float newMaxY)
    {
        var changed = false;

        // GROW path (CAS loop per axis). Note: AABB2F has min/max as separate fields, so we CAS each independently.
        // The 2D AABB is stored on ClusterSpatialAabb (which has 3D fields; we touch only X/Y here).
        if (newMinX < stored.MinX) { CasMin(ref stored.MinX, newMinX); changed = true; }
        if (newMinY < stored.MinY) { CasMin(ref stored.MinY, newMinY); changed = true; }
        if (newMaxX > stored.MaxX) { CasMax(ref stored.MaxX, newMaxX); changed = true; }
        if (newMaxY > stored.MaxY) { CasMax(ref stored.MaxY, newMaxY); changed = true; }

        // SHRINK flag (only set when this slot WAS at an extreme AND moved inward). Bit layout:
        // 0x01=MinX, 0x02=MaxX, 0x04=MinY, 0x08=MaxY (matches ClusterShrinkPendingAxes doc).
        byte shrinkMask = 0;
        if (oldMinX == stored.MinX && newMinX > oldMinX)
        {
            shrinkMask |= 0x01;
        }

        if (oldMaxX == stored.MaxX && newMaxX < oldMaxX)
        {
            shrinkMask |= 0x02;
        }

        if (oldMinY == stored.MinY && newMinY > oldMinY)
        {
            shrinkMask |= 0x04;
        }

        if (oldMaxY == stored.MaxY && newMaxY < oldMaxY)
        {
            shrinkMask |= 0x08;
        }

        if (shrinkMask != 0)
        {
            // byte[] doesn't support Interlocked.Or directly; widen to int[] view at the chunk index. Cluster count is at most a few thousand → bool array
            // would also work, but byte[] keeps the mask compact. We use Interlocked.Or on int slice — see ClusterShrinkPendingAxesOr below.
            InterlockedOrByteArrayElement(_state.ClusterShrinkPendingAxes, _chunkId, shrinkMask);
            changed = true;
        }

        return changed;
    }

    /// <summary>CAS-loop float min update: write <paramref name="candidate"/> if it's still less than the stored value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CasMin(ref float storedRef, float candidate)
    {
        while (true)
        {
            var current = storedRef;
            if (candidate >= current)
            {
                return;
            }

            var currentBits = BitConverter.SingleToInt32Bits(current);
            var candidateBits = BitConverter.SingleToInt32Bits(candidate);
            ref var storedAsInt = ref Unsafe.As<float, int>(ref storedRef);
            if (Interlocked.CompareExchange(ref storedAsInt, candidateBits, currentBits) == currentBits)
            {
                return;
            }
            // Another thread updated; retry the comparison against the new value.
        }
    }

    /// <summary>CAS-loop float max update: write <paramref name="candidate"/> if it's still greater than the stored value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CasMax(ref float storedRef, float candidate)
    {
        while (true)
        {
            var current = storedRef;
            if (candidate <= current)
            {
                return;
            }

            var currentBits = BitConverter.SingleToInt32Bits(current);
            var candidateBits = BitConverter.SingleToInt32Bits(candidate);
            ref var storedAsInt = ref Unsafe.As<float, int>(ref storedRef);
            if (Interlocked.CompareExchange(ref storedAsInt, candidateBits, currentBits) == currentBits)
            {
                return;
            }
        }
    }

    /// <summary>Atomic OR of a small mask into a single byte of a byte[]. Implemented via CAS on the byte's aligned int word slice; safe across writers
    /// targeting different bytes within the same word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterlockedOrByteArrayElement(byte[] array, int index, byte mask)
    {
        // CAS loop on the byte directly: read, OR, CompareExchange byte's int-aligned word. Since `byte` is 1-byte and Interlocked operates on int+, we widen
        // to a per-element approach: each cluster index gets its own array slot, so within-byte word collisions only happen across nearby cluster indices.
        // A simple CAS loop on the single byte slot suffices.
        while (true)
        {
            var current = array[index];
            var updated = (byte)(current | mask);
            if (current == updated)
            {
                return; // mask already set
            }

            // Use Interlocked.CompareExchange on the byte directly via Unsafe.As<byte, int>. Since the byte is part of a larger int chunk, we operate on
            // a 1-byte CAS via a small helper. .NET 7+ has Interlocked.CompareExchange(ref byte, byte, byte) — use it.
            if (Interlocked.CompareExchange(ref array[index], updated, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// World-space minimum corner of the cell this cluster belongs to — the origin its <c>C15</c> cell-relative bounds are measured from.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> when the archetype has no grid or the cluster has no cell yet, in which case there is no frame to express a bound in and the
    /// caller must leave <c>ClusterAabbs</c> alone. Writing a world-space bound as a fallback would be worse than writing nothing: it would be indistinguishable
    /// from a cell-relative one on read, and wrong by the whole distance to the origin.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCellOrigin(out float originX, out float originY, out float originZ) =>
        TryGetCellOrigin(out _, out originX, out originY, out originZ);

    /// <summary>
    /// The cluster's cell key and the world-space origin its <c>C15</c> bounds are measured from, resolved once per <see cref="ClusterRef{TArch}"/> and
    /// cached. Also yields the key, so a caller needing both does not resolve the cluster's cell twice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCellOrigin(out int cellKey, out float originX, out float originY, out float originZ)
    {
        if (_cachedCellKey != CellFrameUnresolved)
        {
            cellKey = _cachedCellKey;
            originX = _cachedOriginX;
            originY = _cachedOriginY;
            originZ = _cachedOriginZ;
            return cellKey >= 0;
        }

        return ResolveCellOrigin(out cellKey, out originX, out originY, out originZ);
    }

    /// <summary>The cold half of <see cref="TryGetCellOrigin(out int, out float, out float, out float)"/> — taken once per cluster, never inlined into the
    /// per-entity write.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ResolveCellOrigin(out int cellKey, out float originX, out float originY, out float originZ)
    {
        cellKey = -1;
        originX = 0f;
        originY = 0f;
        originZ = 0f;

        var grid = _state?.Grid;
        var clusterCellMap = _state?.ClusterCellMap;
        if (grid != null && clusterCellMap != null && (uint)_chunkId < (uint)clusterCellMap.Length)
        {
            cellKey = clusterCellMap[_chunkId];
            if (cellKey >= 0)
            {
                grid.CellOrigin(cellKey, out originX, out originY, out originZ);
            }
        }

        // Only a SUCCESSFUL resolution is cached. Caching the miss would be faster and is not safe: a cluster acquires its cell during creation, so a ref
        // taken before that assignment and used after it would answer "no cell" for the rest of its life — and the caller's response to "no cell" is to skip
        // the CA-01 grow entirely, silently. A miss therefore re-resolves, which costs nothing that matters: a live cluster always has a cell, so the miss
        // path is not a path the hot loop takes.
        if (cellKey >= 0)
        {
            _cachedCellKey = cellKey;
            _cachedOriginX = originX;
            _cachedOriginY = originY;
            _cachedOriginZ = originZ;
            return true;
        }

        return false;
    }

    /// <summary>Migration cell-boundary check. Returns true when a migration was flagged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeFlagMigration(int slotIndex, int currentCellKey, float cellMinX, float cellMinY, float cellMinZ,
        float newMinX, float newMinY, float newMaxX, float newMaxY, float centerZ)
    {
        // The cell key and origin are PASSED IN rather than re-derived. The caller resolved both to convert the entity's bounds into the cluster's frame,
        // and this method used to repeat all of it — two array loads, a bounds check, CellKeyToCoords (itself two dependent loads into the cell's CellState)
        // and three multiplies — per entity per tick, inlined into the AntHill simulation barrier.
        var grid = _state.Grid;
        var centerX = 0.5f * (newMinX + newMaxX);
        var centerY = 0.5f * (newMinY + newMaxY);

        ref readonly var cfg = ref grid.Config;
        var cellSize = cfg.CellSize;
        var hyster = cellSize * cfg.MigrationHysteresisRatio;
        var cellMaxX = cellMinX + cellSize;
        var cellMaxY = cellMinY + cellSize;
        var cellMaxZ = cellMinZ + cellSize;

        var exited = centerX < cellMinX - hyster || centerX > cellMaxX + hyster
                     || centerY < cellMinY - hyster || centerY > cellMaxY + hyster
                     || centerZ < cellMinZ - hyster || centerZ > cellMaxZ + hyster;
        if (!exited)
        {
            // Count the crossings the margin swallowed (#872). Without this the SpatialBarrierOnly path reports zero absorbed crossings forever:
            // DetectClusterMigrations only increments inside its legacy dirty-bits scan, and the barrier-only branch returns before reaching it — so the
            // ratio that tunes MigrationHysteresisRatio was structurally 0/N on the path both demos use. The decision is made here, so the count belongs here.
            //
            // The extra test is the SAME comparison without the margin: "left the cell" minus "left the cell plus margin" is exactly the absorbed set.
            //
            // Ungated on purpose, matching MigrationHint below and EntityMap's split counter: a static readonly profiler gate resolves to false by default, so
            // gating it would leave the number a structural zero in exactly the tool built to read it — trading a defect nobody can see for four float
            // compares in a method that already dereferences the grid, loads two arrays and calls CellKeyToCoords.
            //
            // Interlocked, unlike MigrationHint's plain ++ below: this value is PUBLISHED as an exact count through
            // SpatialMigrationTelemetry, where MigrationHint is documented as an order-of-magnitude work estimate. The atomic is affordable precisely because
            // it is rare — it fires only for a write that lands inside the margin band, not on every spatial write.
            var rawExited = centerX < cellMinX || centerX > cellMaxX
                            || centerY < cellMinY || centerY > cellMaxY
                            || centerZ < cellMinZ || centerZ > cellMaxZ;
            if (rawExited)
            {
                Interlocked.Increment(ref _state.HysteresisAbsorbedLive);
            }

            return false;
        }

        var newCellKey = grid.WorldToCellKey(centerX, centerY, centerZ);
        if (newCellKey == currentCellKey)
        {
            return false;
        }

        // Set bit in per-cluster migration bitmap (atomic OR), and stomp dest cell key. By cluster-coherence invariant, two simultaneous writers to the same
        // cluster end up with the same dest key (modulo racing reads of WorldToCellKey on truly racing positions — fence re-reads positions when draining,
        // so stale dest keys self-correct).
        var slotBit = 1UL << slotIndex;
        Interlocked.Or(ref _state.ClusterMigrationPendingSlots[_chunkId], slotBit);
        _state.ClusterMigrationDestCellKeys[_chunkId] = newCellKey;
        return true;
    }

    /// <summary>Atomically set this cluster's bit in <see cref="ArchetypeClusterState.ClusterProcessBitmap"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetClusterProcessBit()
    {
        var wordIdx = _chunkId >> 6;
        var bit = 1L << (_chunkId & 63);
        Interlocked.Or(ref _state.ClusterProcessBitmap[wordIdx], bit);
    }
}

/// <summary>
/// Iterates active clusters for an archetype. Owns a <see cref="ChunkAccessor{TStore}"/> — must be disposed.
/// </summary>
/// <remarks>
/// <para>Supports <c>foreach</c> via <see cref="GetEnumerator"/>.</para>
/// <para>
/// <b>Non-empty guarantee:</b> the enumerator always yields clusters with <c>OccupancyBits != 0</c> (i.e. <c>LiveCount &gt;= 1</c>). Empty clusters can exist
/// in <c>ActiveClusterIds</c> during the fence's deferred-drain window — between Migrate (last slot released) and Finalize (chunk freed) — but
/// <see cref="MoveNext"/> filters them out so callers never observe a drained cluster.
/// </para>
/// <para>Usage:</para>
/// <code>
/// foreach (var cluster in ants.GetClusterEnumerator())
/// {
///     var positions = cluster.GetSpan&lt;Position&gt;(Ant.Position);
///     ulong bits = cluster.OccupancyBits; // guaranteed non-zero
///     while (bits != 0)
///     {
///         int slot = BitOperations.TrailingZeroCount(bits);
///         bits &amp;= bits - 1;
///         // ...
///     }
/// }
/// </code>
/// </remarks>
[PublicAPI]
public unsafe ref struct ClusterEnumerator<TArch> where TArch : class
{
    private ArchetypeClusterState _state;
    private ArchetypeMetadata _meta;
    private ChunkAccessor<PersistentStore> _accessor;
    private ChunkAccessor<TransientStore> _transientAccessor;
    private bool _hasTransientAccessor;
    private bool _hasPersistentAccessor;
    // Issue #231: source array for cluster chunk ids. Defaults to state.ActiveClusterIds but can point at a tier-filtered partition supplied
    // by TickContext.ClusterIds when a system declares a tier filter.
    private int[] _clusterIds;
    private int _index;
    private int _endIndex;

    [AllowCopy]
    internal static ClusterEnumerator<TArch> Create(ArchetypeClusterState state, ArchetypeMetadata meta,
        ChunkBasedSegment<PersistentStore> segment, ChunkBasedSegment<TransientStore> transientSegment = null)
    {
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = state.ActiveClusterIds;
        result._index = -1;
        result._endIndex = state.ActiveClusterCount;
        return result;
    }

    /// <summary>
    /// Create a scoped enumerator that iterates a range of <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// Used by non-tier-filtered parallel dispatch to partition cluster work across workers.
    /// </summary>
    [AllowCopy]
    internal static ClusterEnumerator<TArch> CreateScoped(ArchetypeClusterState state, ArchetypeMetadata meta,
        ChunkBasedSegment<PersistentStore> segment, ChunkBasedSegment<TransientStore> transientSegment,
        int startIndex, int endIndex)
    {
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = state.ActiveClusterIds;
        result._index = startIndex - 1;
        result._endIndex = endIndex;
        return result;
    }

    /// <summary>
    /// Create a scoped enumerator over an explicit cluster-id source array (issue #231). The source is typically a per-tier cluster list returned
    /// by <see cref="TierClusterIndex.GetClusters"/>. The range <c>[startIndex, endIndex)</c> indexes into <paramref name="clusterIds"/>, not
    /// into <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// </summary>
    [AllowCopy]
    internal static ClusterEnumerator<TArch> CreateScoped(ArchetypeClusterState state, ArchetypeMetadata meta, ChunkBasedSegment<PersistentStore> segment, 
        ChunkBasedSegment<TransientStore> transientSegment, int[] clusterIds, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(clusterIds);
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = clusterIds;
        result._index = startIndex - 1;
        result._endIndex = endIndex;
        return result;
    }

    /// <summary>The chunk ID of the current cluster. Available after <see cref="MoveNext"/> returns true.</summary>
    public int CurrentChunkId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _clusterIds[_index];
    }

    /// <summary>
    /// Mark all occupied slots in the current cluster as dirty. Call this after writing to component data via
    /// <see cref="ClusterRef{TArch}.GetSpan{T}"/> — the direct cluster path does not set dirty bits automatically.
    /// Without this call, <c>DetectClusterMigrations</c> and the WAL tick fence will not see the changes.
    /// </summary>
    public void MarkCurrentDirty()
    {
        var chunkId = _clusterIds[_index];
        var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
        var occupancy = *(ulong*)basePtr;
        while (occupancy != 0)
        {
            var slot = BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;
            _state.SetDirty(chunkId, slot);
        }
    }

    /// <summary>
    /// Mark a single slot in the current cluster as dirty. More precise than <see cref="MarkCurrentDirty"/> —
    /// use when only specific entities changed (e.g., after a cell-boundary crossing check). The slot index
    /// is the bit position from the <see cref="ClusterRef{TArch}.OccupancyBits"/> TZCNT loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkSlotDirty(int slotIndex) => _state.SetDirty(_clusterIds[_index], slotIndex);

    /// <summary>
    /// Advance to the next active cluster in the range, skipping drained clusters (<see cref="ClusterRef{TArch}.OccupancyBits"/> == 0) left in
    /// <c>ActiveClusterIds</c> by the fence's deferred-drain window. Guarantees <c>Current.OccupancyBits != 0</c> when returning true.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (++_index < _endIndex)
        {
            var chunkId = _clusterIds[_index];
            var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            if (*(ulong*)basePtr != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Get the current cluster ref.</summary>
    public ClusterRef<TArch> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var chunkId = _clusterIds[_index];
            // Primary base: PersistentStore for mixed/SV, TransientStore for pure-Transient
            var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            // TransientStore base for mixed archetypes (null for pure-SV/V and pure-Transient)
            var transientPtr = (_hasTransientAccessor && _hasPersistentAccessor) ? _transientAccessor.GetChunkAddress(chunkId) : null;
            return new ClusterRef<TArch>(basePtr, transientPtr, _state.Layout, _meta, chunkId, _state);
        }
    }

    /// <summary>Release the ChunkAccessors.</summary>
    public void Dispose()
    {
        if (_hasPersistentAccessor)
        {
            _accessor.Dispose();
        }
        if (_hasTransientAccessor)
        {
            _transientAccessor.Dispose();
        }
    }

    /// <summary>Enable foreach.</summary>
    public ClusterEnumerator<TArch> GetEnumerator() => this;
}
