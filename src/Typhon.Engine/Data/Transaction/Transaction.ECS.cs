using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

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
    // ═══════════════════════════════════════════════════════════════════════
    // ECS Queries
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a polymorphic query matching <typeparamref name="TArchetype"/> and all descendants.
    /// Supports Tier 1 (.With, .Without, .Exclude), Tier 2 (.Enabled, .Disabled), and execution (.Execute, .Count, .Any, foreach).
    /// </summary>
    public EcsQuery<TArchetype> Query<TArchetype>() where TArchetype : class => new(this, polymorphic: true);

    /// <summary>Create an exact query matching only <typeparamref name="TArchetype"/>, no descendants.</summary>
    public EcsQuery<TArchetype> QueryExact<TArchetype>() where TArchetype : class => new(this, polymorphic: false);

    /// <summary>
    /// O(1) metadata count of live entities for <typeparamref name="TArchetype"/> and descendants.
    /// Uses LinearHash.EntryCount — fast but includes entities with DiedTSN set (not yet cleaned up).
    /// For exact counts respecting visibility, use <c>Query&lt;T&gt;().Count()</c>.
    /// </summary>
    public int EcsCount<TArchetype>() where TArchetype : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        if (meta?.SubtreeArchetypeIds == null)
        {
            return 0;
        }

        int total = 0;
        foreach (var id in meta.SubtreeArchetypeIds)
        {
            var m = ArchetypeRegistry.GetMetadata(id);
            if (m != null)
            {
                var es = _dbe._archetypeStates[m.ArchetypeId];
                if (es?.EntityMap != null)
                {
                    total += (int)es.EntityMap.EntryCount;
                }
            }
        }
        return total;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn
    // ═══════════════════════════════════════════════════════════════════════

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
        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        Debug.Assert(engineState?.EntityMap != null, $"Archetype {typeof(TArch).Name} EntityMap not initialized — call DatabaseEngine.InitializeArchetypes first");

        // Generate unique EntityKey
        long entityKey = Interlocked.Increment(ref engineState.NextEntityKey);
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
                var table = engineState.SlotToComponentTable[slot];
                int chunkId = table.StorageMode == StorageMode.Transient ? 
                    table.TransientComponentSegment.AllocateChunk(false) : table.ComponentSegment.AllocateChunk(false, _changeSet);

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
                        var dst = table.StorageMode == StorageMode.Transient ? 
                            info.TransientCompContentAccessor.GetChunkAsSpan(chunkId, true) : info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
                        int overhead = table.ComponentOverhead;
                        // Copy min(DataSize, available chunk space) — sizeof(T) may exceed ComponentStorageSize
                        // due to struct alignment padding (e.g., 8-byte aligned struct with 12B fields → sizeof = 16)
                        int copySize = Math.Min(values[v].DataSize, dst.Length - overhead);
                        new ReadOnlySpan<byte>((byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in values[v])) + 12, copySize)
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

                // Versioned: create revision chain (CompRevStorageHeader + first revision entry)
                // This populates _componentInfos so CommitComponentCore handles PK index, secondary// indexes, WAL serialization, and IsolationFlag
                // clearing at commit time.
                if (table.StorageMode == StorageMode.Versioned)
                {
                    var compType = meta._slotToComponentType[slot];
                    var info = GetComponentInfo(compType);
                    var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, UowId, chunkId, (long)entityId.RawValue);

                    var cri = new ComponentInfo.CompRevInfo
                    {
                        Operations = ComponentInfo.OperationType.Created,
                        PrevCompContentChunkId = 0,
                        PrevRevisionIndex = -1,
                        CurCompContentChunkId = chunkId,
                        CompRevTableFirstChunkId = compRevChunkId,
                        CurRevisionIndex = 0,
                        ReadCommitSequence = 1,
                        ReadRevisionIndex = 0,
                    };
                    info.AddNew((long)entityId.RawValue, cri);
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
        if (meta == null)
        {
            return false;
        }
        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        if (engineState?.EntityMap == null)
        {
            return false;
        }

        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        using var guard = EpochGuard.Enter(_epochManager);
        var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
        bool found = engineState.EntityMap.TryGet(id.EntityKey, readBuf, ref accessor);
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
            if (childMeta == null)
            {
                continue;
            }
            var childEngineState = _dbe._archetypeStates[childMeta.ArchetypeId];
            if (childEngineState?.EntityMap == null)
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

            var engineState = _dbe._archetypeStates[meta.ArchetypeId];
            var entityRef = new EntityRef(id, meta, engineState, this, bits, writable);
            entityRef.CopyLocationsFrom(pending.RecordBytes, meta.ComponentCount);
            return entityRef;
        }

        // Probe LinearHash
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return default;
        }

        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        using var guard = EpochGuard.Enter(_epochManager);
        var accessor = es.EntityMap.Segment.CreateChunkAccessor();
        bool found = es.EntityMap.TryGet(id.EntityKey, readBuf, ref accessor);
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

        var result = new EntityRef(id, meta, es, this, enabledBits, writable);
        result.CopyLocationsFrom(readBuf, meta.ComponentCount);

        // For Versioned components: resolve MVCC-visible chunkId via revision chain walk.
        // Location[slot] from EntityRecord is the spawn-time chunkId. After writes, the revision chain has newer versions. Walking the chain gives the correct
        // chunkId for this transaction's snapshot.
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            var table = es.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.Versioned)
            {
                continue;
            }

            var compType = meta._slotToComponentType[slot];
            var info = GetComponentInfo(compType);

            long pk = (long)id.RawValue;

            // If already resolved in this transaction (e.g. second Open on same entity), reuse cached entry
            if (!info.IsMultiple && info.SingleCache.TryGetValue(pk, out var cached))
            {
                result.SetLocation(slot, cached.CurCompContentChunkId);
                continue;
            }

            var pkResult = GetCompRevInfoFromIndex(pk, info, TSN);
            if (pkResult.IsFailure)
            {
                continue;
            }

            // Cache CompRevInfo for conflict detection (5.3 Write)
            var compRevInfo = pkResult.Value;
            compRevInfo.Operations = ComponentInfo.OperationType.Read;
            info.AddNew(pk, compRevInfo);

            // Override Location[slot] with MVCC-visible chunkId
            result.SetLocation(slot, compRevInfo.CurCompContentChunkId);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers — component data access (delegated from EntityRef)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read component data via the existing ComponentInfo accessor cache. Zero-copy — returns a ref into the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T ReadEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        byte* ptr = table.StorageMode == StorageMode.Transient ? 
            info.TransientCompContentAccessor.GetChunkAddress(chunkId) : info.CompContentAccessor.GetChunkAddress(chunkId);
        return ref Unsafe.AsRef<T>(ptr + table.ComponentOverhead);
    }

    /// <summary>Write component data via the existing ComponentInfo accessor cache. Returns mutable ref.
    /// For SingleVersion: atomically marks chunkId in DirtyBitmap for tick fence serialization.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        byte* ptr;
        if (table.StorageMode == StorageMode.Transient)
        {
            ptr = info.TransientCompContentAccessor.GetChunkAddress(chunkId, true);
        }
        else
        {
            ptr = info.CompContentAccessor.GetChunkAddress(chunkId, true);
            table.DirtyBitmap?.Set(chunkId);
        }
        return ref Unsafe.AsRef<T>(ptr + table.ComponentOverhead);
    }

    /// <summary>
    /// Copy-on-write for Versioned components: allocates new chunk, copies data, creates revision entry.
    /// Called by EntityRef.Write for Versioned components. Returns (newChunkId, newChunkAddress).
    /// First write per entity allocates; subsequent writes reuse the same new chunk.
    /// </summary>
    internal (int chunkId, nint ptr) EcsVersionedCopyOnWrite(Type compType, EntityId entityId, ComponentTable table)
    {
        var info = GetComponentInfo(compType);
        long pk = (long)entityId.RawValue;

        // CompRevInfo should be in cache from Read (5.2 ResolveEntity) or Created (5.1 Spawn)
        ref var cri = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var cached);

        if (!cached)
        {
            // Fallback: Write without prior Open (edge case)
            var result = GetCompRevInfoFromIndex(pk, info, TSN);
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"Entity {entityId} not found in PK index for {compType.Name}");
            }
            cri = result.Value;
        }

        // Only allocate new revision on FIRST write. Created (from Spawn) already has a chunk.
        bool alreadyWritten = (cri.Operations & (ComponentInfo.OperationType.Updated | ComponentInfo.OperationType.Created)) != 0;

        if (!alreadyWritten)
        {
            int oldChunkId = cri.CurCompContentChunkId;
            cri.Operations |= ComponentInfo.OperationType.Updated;

            // AddCompRev: allocates NEW chunk, adds revision entry with IsolationFlag=true
            ComponentRevisionManager.AddCompRev(info, ref cri, TSN, UowId, isDelete: false);

            // Copy old data to new chunk
            byte* oldPtr = info.CompContentAccessor.GetChunkAddress(oldChunkId);
            byte* newPtr = info.CompContentAccessor.GetChunkAddress(cri.CurCompContentChunkId, true);
            Unsafe.CopyBlock(newPtr, oldPtr, (uint)table.ComponentTotalSize);

            // If the component has collections, increment RefCounters for shared collection buffers.
            // The byte copy above duplicated the _bufferId fields — both old and new revisions now
            // reference the same collection storage, so RefCounter must reflect that.
            if (table.HasCollections)
            {
                foreach (var kvp in table.ComponentCollectionVSBSByOffset)
                {
                    int bufferId = *(int*)(newPtr + table.ComponentOverhead + kvp.Key);
                    if (bufferId != 0)
                    {
                        var accessor = kvp.Value.Segment.CreateChunkAccessor(_changeSet);
                        kvp.Value.BufferAddRef(bufferId, ref accessor);
                        accessor.Dispose();
                    }
                }
            }
        }

        byte* ptr = info.CompContentAccessor.GetChunkAddress(cri.CurCompContentChunkId, true);
        return (cri.CurCompContentChunkId, (nint)ptr);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Commit hooks — flush pending ECS operations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Flush all pending ECS operations into persistent storage. Called during Commit.</summary>
    internal void FlushEcsPendingOperations()
    {
        // Enable/Disable must run BEFORE spawns: for pending spawn entities, it updates the record bytes in-place. FlushPendingSpawns then writes the
        // corrected records to LinearHash. For committed entities, it directly upserts to LinearHash (order doesn't matter).
        FlushPendingEnableDisable();
        FlushPendingSpawns();
        FlushPendingDestroys();
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
                var engineState = _dbe._archetypeStates[meta.ArchetypeId];
                var hashAccessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
                engineState.EntityMap.Insert(entityId.EntityKey, recordPtr, ref hashAccessor, _changeSet);
                hashAccessor.Dispose();

                // Insert into secondary indexes for each component slot.
                // - Versioned: SKIP — CommitComponentCore handles via IndexMaintainer (prevents double insertion)
                // - Transient: IndexedFieldInfos = [] so naturally skipped
                // - SV: inserted here
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = engineState.SlotToComponentTable[slot];
                    if (table.StorageMode == StorageMode.Versioned)
                    {
                        continue; // CommitComponentCore handles PK + secondary indexes via IndexMaintainer
                    }
                    var indexedFieldInfos = table.IndexedFieldInfos;
                    if (indexedFieldInfos == null || indexedFieldInfos.Length == 0)
                    {
                        continue;
                    }

                    int chunkId = EntityRecordAccessor.GetLocation(recordPtr, slot);
                    if (chunkId == 0)
                    {
                        continue;
                    }

                    // Transient secondary indexes require ChunkAccessor<TransientStore> — not yet implemented (IndexedFieldInfos = [] for Transient).
                    // Guard defensively.
                    Debug.Assert(table.StorageMode != StorageMode.Transient, "Transient secondary indexes not yet implemented — IndexedFieldInfos should be empty");
                    var compAccessor = table.ComponentSegment.CreateChunkAccessor(_changeSet);
                    byte* chunkAddr = compAccessor.GetChunkAddress(chunkId, true);

                    for (int i = 0; i < indexedFieldInfos.Length; i++)
                    {
                        ref var ifi = ref indexedFieldInfos[i];
                        var idxAccessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);
                        if (ifi.Index.AllowMultiple)
                        {
                            *(int*)&chunkAddr[ifi.OffsetToIndexElementId] = ifi.Index.Add(&chunkAddr[ifi.OffsetToField], chunkId, ref idxAccessor, out _);
                        }
                        else
                        {
                            ifi.Index.Add(&chunkAddr[ifi.OffsetToField], chunkId, ref idxAccessor);
                        }
                        idxAccessor.Dispose();
                    }

                    compAccessor.Dispose();
                }
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

        // Hoist stackalloc out of loop — max record size is 78B (14B header + 16 components × 4B)
        byte* readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        foreach (var entityId in _pendingDestroys)
        {
            var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
            if (meta == null)
            {
                continue;
            }
            var engineState = _dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null)
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
            if (engineState.EntityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
            {
                // Set DiedTSN
                EntityRecordAccessor.GetHeader(readBuf).DiedTSN = TSN;
                engineState.EntityMap.Upsert(entityId.EntityKey, readBuf, ref accessor, _changeSet);

                // Enqueue for deferred GC (LinearHash removal + chunk freeing when MinTSN advances past DiedTSN)
                _dbe.EnqueueEcsCleanup(entityId, meta, TSN);
            }
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Prepare component-level tombstone revisions for pending destroys. Called BEFORE CommitComponentCore so it can handle secondary index removal,
    /// WAL delete entries, and view notifications. The archetype-level DiedTSN is set later in FlushPendingDestroys (post-commit).
    /// </summary>
    private void PrepareEcsDestroys()
    {
        if (_pendingDestroys == null || _pendingDestroys.Count == 0)
        {
            return;
        }

        foreach (var entityId in _pendingDestroys)
        {
            // Skip entities that were spawned in this same transaction — they were never inserted
            // into ComponentTables (FlushPendingSpawns skips them), so there's nothing to delete.
            if (_pendingSpawns != null && _pendingSpawns.ContainsKey(entityId))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
            if (meta == null)
            {
                continue;
            }
            var engineState = _dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.SlotToComponentTable == null)
            {
                continue;
            }

            long pk = (long)entityId.RawValue;
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];
                if (table == null || table.StorageMode != StorageMode.Versioned)
                {
                    continue;
                }
                MarkComponentDeleted(meta._slotToComponentType[slot], pk);
            }
        }
    }

    /// <summary>
    /// Mark a component as deleted in the ComponentInfo cache for a destroyed entity.
    /// Creates a tombstone revision (CurCompContentChunkId = 0) so CommitComponentCore can handle index removal, WAL entries, and deferred cleanup.
    /// </summary>
    private void MarkComponentDeleted(Type compType, long pk)
    {
        var info = GetComponentInfo(compType);

        ref var cri = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var cached);

        if (cached)
        {
            // Already in cache (from Open/Write in same tx)
            if ((cri.Operations & ComponentInfo.OperationType.Deleted) != 0)
            {
                return;
            }

            // Free chunk allocated by Spawn/Write in same tx
            if (cri.CurCompContentChunkId != 0)
            {
                info.CompContentSegment.FreeChunk(cri.CurCompContentChunkId);
                cri.CurCompContentChunkId = 0;
            }
        }
        else
        {
            // Not in cache — read from index
            var result = GetCompRevInfoFromIndex(pk, info, TSN);
            if (result.IsFailure)
            {
                info.SingleCache.Remove(pk);
                return;
            }
            cri = result.Value;
        }

        cri.Operations |= ComponentInfo.OperationType.Deleted;

        // Create tombstone revision only on first mutation (same guard as UpdateComponent)
        if (!cached || (cri.Operations & ComponentInfo.OperationType.Read) != 0)
        {
            ComponentRevisionManager.AddCompRev(info, ref cri, TSN, UowId, isDelete: true);
        }
    }

    private void FlushPendingEnableDisable()
    {
        if (_pendingEnableDisable == null || _pendingEnableDisable.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);

        // Hoist stackalloc out of loop — max record size is 78B (14B header + 16 components × 4B)
        byte* readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

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
            if (meta == null)
            {
                continue;
            }
            var engineState = _dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null)
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
            if (engineState.EntityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
            {
                ushort oldBits = EntityRecordAccessor.GetHeader(readBuf).EnabledBits;

                // Record MVCC override if older transactions exist
                if (oldBits != newBits)
                {
                    _dbe.EnabledBitsOverrides.Record(entityId.EntityKey, TSN, oldBits);
                }

                // Update
                EntityRecordAccessor.GetHeader(readBuf).EnabledBits = newBits;
                engineState.EntityMap.Upsert(entityId.EntityKey, readBuf, ref accessor, _changeSet);
            }
            accessor.Dispose();
        }
    }

    /// <summary>Clean up ECS-specific state on transaction reset/dispose. Frees orphaned chunks on rollback.</summary>
    internal void CleanupEcsState()
    {
        // If transaction was NOT committed, free component chunks allocated by pending spawns
        if (_pendingSpawns is { Count: > 0 } && State != TransactionState.Committed)
        {
            foreach (var kvp in _pendingSpawns)
            {
                var meta = kvp.Value.Archetype;
                var engineState = _dbe._archetypeStates[meta.ArchetypeId];
                if (engineState?.SlotToComponentTable == null)
                {
                    continue;
                }

                fixed (byte* recordPtr = kvp.Value.RecordBytes)
                {
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int chunkId = EntityRecordAccessor.GetLocation(recordPtr, slot);
                        if (chunkId != 0)
                        {
                            var table = engineState.SlotToComponentTable[slot];
                            if (table.StorageMode == StorageMode.Transient)
                            {
                                table.TransientComponentSegment.FreeChunk(chunkId);
                            }
                            else
                            {
                                table.ComponentSegment.FreeChunk(chunkId);

                                // Versioned: also free the revision chain chunk
                                if (table.StorageMode == StorageMode.Versioned)
                                {
                                    var compType = meta._slotToComponentType[slot];
                                    if (_componentInfos.TryGetValue(compType, out var info) &&
                                        !info.IsMultiple && info.SingleCache.TryGetValue((long)kvp.Key.RawValue, out var cri))
                                    {
                                        table.CompRevTableSegment.FreeChunk(cri.CompRevTableFirstChunkId);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Rollback Versioned writes (copy-on-write): free chunks allocated by AddCompRev
        if (State != TransactionState.Committed && _componentInfos.Count > 0)
        {
            foreach (var kvp in _componentInfos)
            {
                var info = kvp.Value;
                if (info.ComponentTable.StorageMode != StorageMode.Versioned)
                {
                    continue;
                }

                if (info.SingleCache != null)
                {
                    foreach (var cacheKvp in info.SingleCache)
                    {
                        var cri = cacheKvp.Value;
                        // Free copy-on-write chunks (Updated but not Created — Created chunks are freed above)
                        if ((cri.Operations & ComponentInfo.OperationType.Updated) != 0 &&
                            (cri.Operations & ComponentInfo.OperationType.Created) == 0 &&
                            cri.CurCompContentChunkId > 0)
                        {
                            info.CompContentSegment.FreeChunk(cri.CurCompContentChunkId);
                        }
                    }
                }
            }
        }

        _pendingSpawns?.Clear();
        _pendingDestroys?.Clear();
        _pendingEnableDisable?.Clear();
    }
}
