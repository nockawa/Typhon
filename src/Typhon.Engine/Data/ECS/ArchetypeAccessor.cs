using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Fast-path entity accessor pre-bound to a specific archetype.
/// Bypasses epoch checks, archetype lookup, and null guards that are redundant for PTA workers.
/// <para>Created via <see cref="EntityAccessor.For{TArch}"/>. Must be disposed after use.</para>
/// </summary>
/// <remarks>
/// <para><b>What it skips per-entity vs <see cref="EntityAccessor.ResolveEntity"/>:</b></para>
/// <list type="bullet">
///   <item>EpochThreadRegistry.IsCurrentThreadInScope check (ThreadStatic access)</item>
///   <item>ArchetypeRegistry.GetMetadata lookup</item>
///   <item>Null guards on archetype state and entity map</item>
///   <item>EntityMap ChunkAccessor cache check (always same archetype)</item>
/// </list>
/// <para>Versioned components are supported — revision chain walk is performed only for Versioned slots.
/// SV/Transient slots skip the chain walk entirely (the common fast path for game systems).</para>
/// <para>Cluster storage: when the archetype uses cluster storage, Resolve reads ClusterEntityRecord from the EntityMap and populates EntityRef's cluster
/// fields for direct SoA access.</para>
/// </remarks>
[PublicAPI]
public unsafe ref struct ArchetypeAccessor<TArch> where TArch : class
{
    private readonly ArchetypeMetadata _archetype;
    private readonly ArchetypeEngineState _engineState;
    private readonly EntityAccessor _accessor;
    private readonly EnabledBitsOverrides _enabledBitsOverrides;
    private readonly long _tsn;
    private readonly int _recordSize;
    private readonly bool _hasVersionedSlots;
    private bool _mutationPrepared;
    private ChunkAccessor<PersistentStore> _entityMapAccessor;

    // ── Cluster storage fields ──────────────────────────────────────────
    private readonly bool _hasClusterStorage;
    private readonly ArchetypeClusterState _clusterState;
    private ChunkAccessor<PersistentStore> _clusterAccessor;

    internal ArchetypeAccessor(ArchetypeMetadata archetype, ArchetypeEngineState engineState, EntityAccessor accessor, DatabaseEngine dbe)
    {
        _archetype = archetype;
        _engineState = engineState;
        _accessor = accessor;
        _enabledBitsOverrides = dbe.EnabledBitsOverrides;
        _tsn = accessor.TSN;
        _recordSize = archetype._entityRecordSize;
        _entityMapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();

        // Detect if any component uses Versioned storage (needs revision chain walk)
        _hasVersionedSlots = false;
        for (int slot = 0; slot < archetype.ComponentCount; slot++)
        {
            if (engineState.SlotToComponentTable[slot].StorageMode == StorageMode.Versioned)
            {
                _hasVersionedSlots = true;
            }

            // Pre-warm ComponentInfo cache — ensures EntityRef.Read/Write hits the fast array path
            accessor.EnsureComponentInfoCached(archetype._slotToComponentType[slot]);
        }

        // Cluster storage setup
        _hasClusterStorage = archetype.IsClusterEligible && engineState.ClusterState != null;
        _clusterState = engineState.ClusterState;
        _clusterAccessor = _hasClusterStorage ? _clusterState.ClusterSegment.CreateChunkAccessor() : default;
    }

    /// <summary>Open an entity for read-only access.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef Open(EntityId id) => Resolve(id, writable: false);

    /// <summary>Open an entity for read-write access.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef OpenMut(EntityId id)
    {
        if (!_mutationPrepared)
        {
            _accessor.PrepareForMutation();
            _mutationPrepared = true;
        }
        return Resolve(id, writable: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EntityRef Resolve(EntityId id, bool writable)
    {
        byte* readBuf = stackalloc byte[_recordSize];
        if (!_engineState.EntityMap.TryGet(id.EntityKey, readBuf, ref _entityMapAccessor))
        {
            return default;
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);
        ushort enabledBits = _enabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, _tsn);

        var result = new EntityRef(id, _archetype, _engineState, _accessor, enabledBits, writable);

        if (_hasClusterStorage)
        {
            // Cluster path: read ClusterEntityRecord → resolve cluster base + slot
            int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
            byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);
            result._clusterBase = _clusterAccessor.GetChunkAddress(clusterChunkId, writable);
            result._clusterSlotIndex = slotIndex;
            result._clusterChunkId = clusterChunkId;
            result._clusterLayout = _clusterState.Layout;
        }
        else
        {
            // Legacy path: copy per-component locations
            result.CopyLocationsFrom(readBuf, _archetype.ComponentCount);

            // Versioned components: walk revision chain to find visible content chunk.
            // SV/Transient: location from EntityRecord is the direct content chunk — no walk needed.
            if (_hasVersionedSlots)
            {
                ResolveVersionedSlots(ref result);
            }
        }

        return result;
    }

    private void ResolveVersionedSlots(ref EntityRef result)
    {
        for (int slot = 0; slot < _archetype.ComponentCount; slot++)
        {
            var table = _engineState.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.Versioned)
            {
                continue;
            }

            int compRevFirstChunkId = result.GetLocation(slot);
            if (compRevFirstChunkId == 0)
            {
                continue;
            }

            var compTypeId = _archetype._componentTypeIds[slot];
            var info = _accessor.GetComponentInfoInternal(compTypeId, _archetype._slotToComponentType[slot]);

            var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, _tsn, skipTimeout: true);
            if (chainResult.IsFailure)
            {
                continue;
            }

            result.SetLocation(slot, chainResult.Value.CurCompContentChunkId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cluster iteration API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True if this archetype uses cluster storage.</summary>
    public bool HasClusterStorage => _hasClusterStorage;

    /// <summary>Number of active clusters (clusters with at least one live entity).</summary>
    public int ClusterCount => _hasClusterStorage ? _clusterState.ActiveClusterCount : 0;

    /// <summary>
    /// Get an enumerator over active clusters for direct SoA iteration.
    /// The enumerator owns its own ChunkAccessor and must be disposed.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator()
    {
        if (!_hasClusterStorage)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.Create(_clusterState, _archetype, _clusterState.ClusterSegment);
    }

    /// <summary>Release the cached EntityMap and cluster ChunkAccessors.</summary>
    public void Dispose()
    {
        _entityMapAccessor.Dispose();
        if (_hasClusterStorage)
        {
            _clusterAccessor.Dispose();
        }
    }
}
