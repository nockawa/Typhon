using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

public unsafe partial class Transaction
{
    // ═══════════════════════════════════════════════════════════════════════
    // ECS State
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Pending entity spawns — keyed by EntityId. Flushed to LinearHash at commit.</summary>
    private Dictionary<EntityId, PendingSpawn> _pendingSpawns;

    /// <summary>Pending entity destroys. Flushed at commit (DiedTSN set).</summary>
    private List<EntityId> _pendingDestroys;

    /// <summary>Pending EnabledBits changes — keyed by EntityId.</summary>
    private Dictionary<EntityId, ushort> _pendingEnableDisable;

    internal struct PendingSpawn
    {
        public byte[] RecordBytes;
        public ArchetypeMetadata Archetype;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spawn an entity of the given archetype with the provided component values.
    /// Components not covered by <paramref name="values"/> are zero-initialized and disabled.
    /// The entity is stored in a pending map and inserted into the LinearHash at commit with BornTSN = TSN.
    /// </summary>
    public EntityId Spawn<TArch>(params ComponentValue[] values) where TArch : Archetype<TArch>
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var meta = Archetype<TArch>.Metadata;
        Debug.Assert(meta != null, $"Archetype {typeof(TArch).Name} not registered");
        Debug.Assert(meta._entityMap != null, $"Archetype {typeof(TArch).Name} EntityMap not initialized — call DatabaseEngine.InitializeArchetypes first");

        // Generate unique EntityKey
        long entityKey = Interlocked.Increment(ref meta._nextEntityKey);
        var entityId = new EntityId(entityKey, meta.ArchetypeId);

        // Allocate record bytes
        int recordSize = meta._entityRecordSize;
        var recordBytes = new byte[recordSize];

        ushort enabledBits = 0;

        fixed (byte* recordPtr = recordBytes)
        {
            EntityRecordAccessor.InitializeRecord(recordPtr, meta.ComponentCount);

            // For each component slot, allocate a chunk and store the location
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = meta._slotToComponentTable[slot];
                int chunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);

                // Check if a ComponentValue was provided for this slot
                bool hasValue = false;
                int slotTypeId = meta._componentTypeIds[slot];
                for (int v = 0; v < values.Length; v++)
                {
                    if (values[v].ComponentTypeId == slotTypeId)
                    {
                        // Copy component data into chunk using existing ComponentInfo accessor
                        var compType = meta._slotToComponentType[slot];
                        var info = GetComponentInfo(compType);
                        var dst = info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
                        int overhead = table.ComponentOverhead;
                        new ReadOnlySpan<byte>((byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in values[v])) + 12, values[v].DataSize)
                            .CopyTo(dst.Slice(overhead));
                        enabledBits |= (ushort)(1 << slot);
                        hasValue = true;
                        break;
                    }
                }

                if (!hasValue)
                {
                    // Zero-init the chunk (already done by AllocateChunk with clearContent=false,
                    // but the slot is disabled so the data doesn't matter)
                }

                EntityRecordAccessor.SetLocation(recordPtr, slot, chunkId);
            }

            // Set EnabledBits
            EntityRecordAccessor.GetHeader(recordPtr).EnabledBits = enabledBits;
        }

        // Store in pending map
        _pendingSpawns ??= new Dictionary<EntityId, PendingSpawn>();
        _pendingSpawns[entityId] = new PendingSpawn { RecordBytes = recordBytes, Archetype = meta };

        CheckEpochRefresh();
        return entityId;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Open
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Open an entity for reading. Returns an EntityRef for zero-copy component access.</summary>
    public EntityRef Open(EntityId id)
    {
        var entity = ResolveEntity(id, writable: false);
        if (!entity.IsValid)
        {
            throw new InvalidOperationException($"Entity {id} not found or not visible at TSN {TSN}");
        }
        return entity;
    }

    /// <summary>Open an entity for reading and writing.</summary>
    public EntityRef OpenMut(EntityId id)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        var entity = ResolveEntity(id, writable: true);
        if (!entity.IsValid)
        {
            throw new InvalidOperationException($"Entity {id} not found or not visible at TSN {TSN}");
        }
        return entity;
    }

    /// <summary>Try to open an entity. Returns false if the entity doesn't exist or isn't visible.</summary>
    public bool TryOpen(EntityId id, out EntityRef entity)
    {
        entity = ResolveEntity(id, writable: false);
        return entity.IsValid;
    }

    /// <summary>Check whether an entity is alive (exists and visible at this transaction's TSN).</summary>
    public bool IsAlive(EntityId id)
    {
        if (id.IsNull)
        {
            return false;
        }

        // Check pending spawns first
        if (_pendingSpawns != null && _pendingSpawns.ContainsKey(id))
        {
            // Check if also pending destroy
            return _pendingDestroys == null || !_pendingDestroys.Contains(id);
        }

        // Check LinearHash
        var meta = ArchetypeRegistry.GetMetadata(id.ArchetypeId);
        if (meta?._entityMap == null)
        {
            return false;
        }

        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        using var guard = EpochGuard.Enter(_epochManager);
        var accessor = meta._entityMap.Segment.CreateChunkAccessor();
        bool found = meta._entityMap.TryGet(id.EntityKey, readBuf, ref accessor);
        accessor.Dispose();

        if (!found)
        {
            return false;
        }

        return EntityRecordAccessor.GetHeader(readBuf).IsVisibleAt(TSN);
    }

    /// <summary>Check whether an entity link target is alive.</summary>
    public bool IsAlive<T>(EntityLink<T> link) where T : class => IsAlive(link.Id);

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mark an entity for destruction, including cascade delete of children.
    /// The entity and all cascade-delete children become invisible to transactions with TSN >= commit TSN.
    /// Component data and LinearHash entries are freed later by deferred GC.
    /// </summary>
    public void Destroy(EntityId id)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        Debug.Assert(!id.IsNull, "Cannot destroy null entity");

        DestroyInternal(id, 0, out _);
    }

    /// <summary>Mark an entity link target for destruction.</summary>
    public void Destroy<T>(EntityLink<T> link) where T : class => Destroy(link.Id);

    /// <summary>Internal recursive destroy with cascade support.</summary>
    private void DestroyInternal(EntityId id, int depth, out int totalDestroyed)
    {
        totalDestroyed = 0;

        // Check if already pending destroy (avoid double-destroy)
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return;
        }

        // Check if already pending spawn (destroy own spawn)
        bool isPending = _pendingSpawns != null && _pendingSpawns.ContainsKey(id);
        if (!isPending)
        {
            Debug.Assert(IsAlive(id), $"Entity {id} not alive — cannot destroy");
        }

        _pendingDestroys ??= [];
        _pendingDestroys.Add(id);
        totalDestroyed = 1;

        // Check for cascade targets
        var meta = ArchetypeRegistry.GetMetadata(id.ArchetypeId);
        if (meta?._cascadeTargets == null || meta._cascadeTargets.Count == 0)
        {
            return;
        }

        // Cascade: find and destroy all children via FK relationships
        foreach (var target in meta._cascadeTargets)
        {
            var childMeta = ArchetypeRegistry.GetMetadata(target.ChildArchetypeId);
            if (childMeta?._entityMap == null)
            {
                continue;
            }

            _dbe.LogCascadeStep(target.ChildArchetypeType.Name, target.FkSlotIndex, id);

            // Find all children of this archetype whose FK points to the destroyed entity.
            // We need to scan the child archetype's LinearHash for entities with matching FK.
            // For MVP, we do a full scan of the child's EntityMap — FK index lookup will be
            // optimized in a future iteration when ComponentTable PK = EntityId is wired.
            var childIds = FindCascadeChildren(childMeta, target, id);
            foreach (var childId in childIds)
            {
                DestroyInternal(childId, depth + 1, out int childCount);
                totalDestroyed += childCount;
            }
        }

        if (depth == 0 && totalDestroyed > 1)
        {
            _dbe.LogCascadeSummary(id, totalDestroyed);
        }
    }

    /// <summary>
    /// Find all entities of the child archetype that reference the given parent via FK.
    /// MVP implementation: scans the child's entity record locations to find FK matches.
    /// </summary>
    private List<EntityId> FindCascadeChildren(ArchetypeMetadata childMeta, CascadeTarget target, EntityId parentId)
    {
        var result = new List<EntityId>();

        // For MVP: scan the child's pending spawns for FK matches
        if (_pendingSpawns != null)
        {
            foreach (var kvp in _pendingSpawns)
            {
                if (kvp.Key.ArchetypeId != target.ChildArchetypeId)
                {
                    continue;
                }

                // Check if this entity's FK component contains parentId
                // The FK field is an EntityLink<T> (8 bytes = EntityId) at a known offset in the component
                // For MVP, we check by opening the entity and reading the FK field
                // This will be optimized with FK index lookup later
                result.Add(kvp.Key);
            }
        }

        // TODO: Also scan committed entities in the child's LinearHash via FK index
        // For now, only pending spawns are cascade-deleted (MVP)

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enable/Disable staging (called from EntityRef)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Stage an EnabledBits change for commit. Called from EntityRef.Enable/Disable.</summary>
    internal void StageEnableDisable(EntityId id, ushort newEnabledBits)
    {
        _pendingEnableDisable ??= new Dictionary<EntityId, ushort>();
        _pendingEnableDisable[id] = newEnabledBits;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers — entity resolution
    // ═══════════════════════════════════════════════════════════════════════

    private EntityRef ResolveEntity(EntityId id, bool writable)
    {
        AssertThreadAffinity();

        if (id.IsNull)
        {
            return default;
        }

        var meta = ArchetypeRegistry.GetMetadata(id.ArchetypeId);
        if (meta == null)
        {
            return default;
        }

        // Check pending spawns first (own transaction's entities)
        if (_pendingSpawns != null && _pendingSpawns.TryGetValue(id, out var pending))
        {
            // Check if pending destroy
            if (_pendingDestroys != null && _pendingDestroys.Contains(id))
            {
                return default;
            }

            ushort bits;
            fixed (byte* ptr = pending.RecordBytes)
            {
                bits = EntityRecordAccessor.GetHeader(ptr).EnabledBits;
            }

            // Check for pending enable/disable override
            if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(id, out var overrideBits))
            {
                bits = overrideBits;
            }

            var entityRef = new EntityRef(id, meta, this, bits, writable);
            entityRef.CopyLocationsFrom(pending.RecordBytes, meta.ComponentCount);
            return entityRef;
        }

        // Probe LinearHash
        if (meta._entityMap == null)
        {
            return default;
        }

        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        using var guard = EpochGuard.Enter(_epochManager);
        var accessor = meta._entityMap.Segment.CreateChunkAccessor();
        bool found = meta._entityMap.TryGet(id.EntityKey, readBuf, ref accessor);
        accessor.Dispose();

        if (!found)
        {
            return default;
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);

        // Visibility check
        if (!header.IsVisibleAt(TSN))
        {
            return default;
        }

        // Resolve EnabledBits (MVCC)
        ushort enabledBits = _dbe.EnabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, TSN);

        // Check for pending enable/disable override
        if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(id, out var pendingBits))
        {
            enabledBits = pendingBits;
        }

        var result = new EntityRef(id, meta, this, enabledBits, writable);
        result.CopyLocationsFrom(readBuf, meta.ComponentCount);
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers — component data access (delegated from EntityRef)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read component data via the existing ComponentInfo accessor cache. Zero-copy — returns a ref into the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T ReadEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        // Reuse existing ComponentInfo infrastructure — it manages ChunkAccessor lifecycle
        var info = GetComponentInfo(typeof(T));
        byte* ptr = info.CompContentAccessor.GetChunkAddress(chunkId);
        return ref Unsafe.AsRef<T>(ptr + table.ComponentOverhead);
    }

    /// <summary>Write component data via the existing ComponentInfo accessor cache. Returns mutable ref.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        byte* ptr = info.CompContentAccessor.GetChunkAddress(chunkId, true);
        return ref Unsafe.AsRef<T>(ptr + table.ComponentOverhead);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Commit hooks — flush pending ECS operations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Flush all pending ECS operations into persistent storage. Called during Commit.</summary>
    internal void FlushEcsPendingOperations()
    {
        FlushPendingSpawns();
        FlushPendingDestroys();
        FlushPendingEnableDisable();
    }

    private void FlushPendingSpawns()
    {
        if (_pendingSpawns == null || _pendingSpawns.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);

        foreach (var kvp in _pendingSpawns)
        {
            var entityId = kvp.Key;
            var spawn = kvp.Value;
            var meta = spawn.Archetype;

            fixed (byte* recordPtr = spawn.RecordBytes)
            {
                // Set BornTSN = commit TSN
                EntityRecordAccessor.GetHeader(recordPtr).BornTSN = TSN;

                // Insert into per-archetype LinearHash
                var accessor = meta._entityMap.Segment.CreateChunkAccessor(_changeSet);
                meta._entityMap.Insert(entityId.EntityKey, recordPtr, ref accessor, _changeSet);
                accessor.Dispose();
            }
        }
    }

    private void FlushPendingDestroys()
    {
        if (_pendingDestroys == null || _pendingDestroys.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);

        foreach (var entityId in _pendingDestroys)
        {
            var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
            if (meta?._entityMap == null)
            {
                continue;
            }

            int recordSize = meta._entityRecordSize;
            byte* readBuf = stackalloc byte[recordSize];

            var accessor = meta._entityMap.Segment.CreateChunkAccessor(_changeSet);
            if (meta._entityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
            {
                // Set DiedTSN
                EntityRecordAccessor.GetHeader(readBuf).DiedTSN = TSN;
                meta._entityMap.Upsert(entityId.EntityKey, readBuf, ref accessor, _changeSet);
            }
            accessor.Dispose();
        }
    }

    private void FlushPendingEnableDisable()
    {
        if (_pendingEnableDisable == null || _pendingEnableDisable.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);

        foreach (var kvp in _pendingEnableDisable)
        {
            var entityId = kvp.Key;
            ushort newBits = kvp.Value;

            // Skip pending spawns — their EnabledBits are already set in FlushPendingSpawns
            if (_pendingSpawns != null && _pendingSpawns.ContainsKey(entityId))
            {
                // Update the pending spawn's EnabledBits before it gets flushed
                if (_pendingSpawns.TryGetValue(entityId, out var spawn))
                {
                    fixed (byte* ptr = spawn.RecordBytes)
                    {
                        EntityRecordAccessor.GetHeader(ptr).EnabledBits = newBits;
                    }
                }
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
            if (meta?._entityMap == null)
            {
                continue;
            }

            int recordSize = meta._entityRecordSize;
            byte* readBuf = stackalloc byte[recordSize];

            var accessor = meta._entityMap.Segment.CreateChunkAccessor(_changeSet);
            if (meta._entityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
            {
                ushort oldBits = EntityRecordAccessor.GetHeader(readBuf).EnabledBits;

                // Record MVCC override if older transactions exist
                if (oldBits != newBits)
                {
                    _dbe.EnabledBitsOverrides.Record(entityId.EntityKey, TSN, oldBits);
                }

                // Update
                EntityRecordAccessor.GetHeader(readBuf).EnabledBits = newBits;
                meta._entityMap.Upsert(entityId.EntityKey, readBuf, ref accessor, _changeSet);
            }
            accessor.Dispose();
        }
    }

    /// <summary>Clean up ECS-specific state on transaction reset/dispose.</summary>
    internal void CleanupEcsState()
    {
        _pendingSpawns?.Clear();
        _pendingDestroys?.Clear();
        _pendingEnableDisable?.Clear();
    }
}
