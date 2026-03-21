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

    /// <summary>Spawned entity data — flat list for sequential iteration at commit time.</summary>
    private List<SpawnEntry> _spawnedEntities;

    /// <summary>O(1) lookup: EntityId → index into <see cref="_spawnedEntities"/>. Built lazily on first Contains/IndexOf call.</summary>
    private Dictionary<EntityId, int> _spawnedEntityIndex;
    private bool _spawnedEntityIndexStale;

    /// <summary>Lightweight spawn record: EntityId + EnabledBits + per-slot chunk IDs. No heap allocation.</summary>
    internal struct SpawnEntry
    {
        public EntityId Id;
        public ushort EnabledBits;
        /// <summary>Per-slot component content chunk IDs (for same-tx reads and rollback).</summary>
        public fixed int Loc[16];
        /// <summary>Per-slot compRevFirstChunkIds for Versioned components (used at commit for EntityRecord).</summary>
        public fixed int Rev[16];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SpawnedContains(EntityId id)
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return false;
        }
        RebuildSpawnedIndex();
        return _spawnedEntityIndex.ContainsKey(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SpawnedIndexOf(EntityId id)
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return -1;
        }
        RebuildSpawnedIndex();
        return _spawnedEntityIndex.TryGetValue(id, out int idx) ? idx : -1;
    }

    private void RebuildSpawnedIndex()
    {
        if (!_spawnedEntityIndexStale)
        {
            return;
        }
        _spawnedEntityIndex ??= new Dictionary<EntityId, int>(_spawnedEntities.Count);
        _spawnedEntityIndex.Clear();
        for (int i = 0; i < _spawnedEntities.Count; i++)
        {
            _spawnedEntityIndex[_spawnedEntities[i].Id] = i;
        }
        _spawnedEntityIndexStale = false;
    }

    /// <summary>Pending entity destroys. Flushed at commit (DiedTSN set). HashSet for O(1) Contains.</summary>
    private HashSet<EntityId> _pendingDestroys;

    /// <summary>Pending EnabledBits changes — keyed by EntityId.</summary>
    private Dictionary<EntityId, ushort> _pendingEnableDisable;

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
    public long EcsCount<TArchetype>() where TArchetype : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        if (meta?.SubtreeArchetypeIds == null)
        {
            return 0;
        }

        long total = 0;
        foreach (var id in meta.SubtreeArchetypeIds)
        {
            var m = ArchetypeRegistry.GetMetadata(id);
            if (m != null)
            {
                var es = _dbe._archetypeStates[m.ArchetypeId];
                if (es?.EntityMap != null)
                {
                    total += es.EntityMap.EntryCount;
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
    public EntityId Spawn<TArch>(params ReadOnlySpan<ComponentValue> values) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        Debug.Assert(meta != null, $"Archetype {typeof(TArch).Name} not registered");
        Debug.Assert(_dbe._archetypeStates[meta.ArchetypeId]?.EntityMap != null,
            $"Archetype {typeof(TArch).Name} EntityMap not initialized — call DatabaseEngine.InitializeArchetypes first");

        Activity activity = null;
        if (TelemetryConfig.EcsActive)
        {
            activity = TyphonActivitySource.StartActivity("ECS.Spawn");
            activity?.SetTag(TyphonSpanAttributes.EcsArchetype, typeof(TArch).Name);
        }

        var id = SpawnInternal(meta, values);

        activity?.SetTag(TyphonSpanAttributes.EntityId, (long)id.RawValue);
        activity?.Dispose();

        return id;
    }

    /// <summary>
    /// Spawn a batch of entities. Amortizes per-call overhead: single EnsureMutable check, single Interlocked.Add for all entity keys, single epoch
    /// refresh at the end.
    /// All entities are initialized with the same component values (or zero if none provided).
    /// </summary>
    public void SpawnBatch<TArch>(Span<EntityId> ids, params ComponentValue[] sharedValues) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        Debug.Assert(meta != null, $"Archetype {typeof(TArch).Name} not registered");
        Debug.Assert(_dbe._archetypeStates[meta.ArchetypeId]?.EntityMap != null,
            $"Archetype {typeof(TArch).Name} EntityMap not initialized");

        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        int count = ids.Length;

        // Allocate N entity keys in one atomic operation
        long baseKey = Interlocked.Add(ref engineState.NextEntityKey, count) - count + 1;

        _spawnedEntities ??= new List<SpawnEntry>(count);
        _spawnedEntityIndexStale = true;

        for (int n = 0; n < count; n++)
        {
            var entityId = new EntityId(baseKey + n, meta.ArchetypeId);
            ids[n] = entityId;

            var entry = new SpawnEntry { Id = entityId, EnabledBits = 0 };

            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];
                int chunkId = table.StorageMode == StorageMode.Transient
                    ? table.TransientComponentSegment.AllocateChunk(false)
                    : table.ComponentSegment.AllocateChunk(false, _changeSet);

                // Copy shared component value if provided for this slot
                int slotTypeId = meta._componentTypeIds[slot];
                for (int v = 0; v < sharedValues.Length; v++)
                {
                    if (sharedValues[v].ComponentTypeId == slotTypeId)
                    {
                        var compType = meta._slotToComponentType[slot];
                        var info = GetComponentInfo(compType);
                        var dst = table.StorageMode == StorageMode.Transient
                            ? info.TransientCompContentAccessor.GetChunkAsSpan(chunkId, true)
                            : info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
                        int overhead = table.ComponentOverhead;
                        int copySize = Math.Min(sharedValues[v].DataSize, dst.Length - overhead);
                        new ReadOnlySpan<byte>((byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in sharedValues[v])) + 12, copySize)
                            .CopyTo(dst.Slice(overhead));
                        entry.EnabledBits |= (ushort)(1 << slot);
                        break;
                    }
                }

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
                    entry.Rev[slot] = compRevChunkId;
                }

                entry.Loc[slot] = chunkId;
            }

            _spawnedEntityIndexStale = true;
            _spawnedEntities.Add(entry);

            // Epoch refresh every 128 entities to avoid holding epoch too long
            if ((n & 127) == 127)
            {
                _epochManager.RefreshScope();
            }
        }

        CheckEpochRefresh();
    }

    /// <summary>
    /// Destroy a batch of entities. Single EnsureMutable check, pre-sized pending list.
    /// Cascade delete is applied per entity.
    /// </summary>
    public void DestroyBatch(ReadOnlySpan<EntityId> ids)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        _pendingDestroys ??= new HashSet<EntityId>(ids.Length);

        for (int i = 0; i < ids.Length; i++)
        {
            Debug.Assert(!ids[i].IsNull, "Cannot destroy null entity");
            DestroyInternal(ids[i], 0, out _);
        }
    }

    /// <summary>Core Spawn implementation shared by Spawn&lt;TArch&gt; and SpawnByArchetypeId.</summary>
    private EntityId SpawnInternal(ArchetypeMetadata meta, ReadOnlySpan<ComponentValue> values)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];

        // Generate unique EntityKey
        long entityKey = Interlocked.Increment(ref engineState.NextEntityKey);
        var entityId = new EntityId(entityKey, meta.ArchetypeId);

        // Pre-build slot-indexed lookup — O(values.Length) once, then O(1) per slot
        Span<int> valueBySlot = stackalloc int[meta.ComponentCount];
        valueBySlot.Fill(-1);
        for (int v = 0; v < values.Length; v++)
        {
            if (meta.TryGetSlot(values[v].ComponentTypeId, out byte targetSlot))
            {
                valueBySlot[targetSlot] = v;
            }
        }

        var entry = new SpawnEntry { Id = entityId, EnabledBits = 0 };

        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            var table = engineState.SlotToComponentTable[slot];
            int chunkId = table.StorageMode == StorageMode.Transient ? 
                table.TransientComponentSegment.AllocateChunk(false) : table.ComponentSegment.AllocateChunk(false, _changeSet);

            // Copy component value data if provided for this slot
            int vi = valueBySlot[slot];
            if (vi >= 0)
            {
                var compType = meta._slotToComponentType[slot];
                var info = GetComponentInfo(compType);
                var dst = table.StorageMode == StorageMode.Transient ? 
                    info.TransientCompContentAccessor.GetChunkAsSpan(chunkId, true) : info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
                int overhead = table.ComponentOverhead;
                int copySize = Math.Min(values[vi].DataSize, dst.Length - overhead);
                new ReadOnlySpan<byte>((byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in values[vi])) + 12, copySize)
                    .CopyTo(dst.Slice(overhead));
                entry.EnabledBits |= (ushort)(1 << slot);
            }

            // Versioned: create revision chain (CompRevStorageHeader + first revision entry).
            // This populates _componentInfos so CommitComponentCore handles secondary indexes, WAL, and IsolationFlag clearing.
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
                entry.Rev[slot] = compRevChunkId;
            }

            entry.Loc[slot] = chunkId;
        }

        // Store in flat list — index rebuilt lazily on first Contains/IndexOf call
        _spawnedEntities ??= [];
        _spawnedEntityIndexStale = true;
        _spawnedEntities.Add(entry);

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

        // Check spawned entities first (not yet in EntityMap)
        if (SpawnedContains(id))
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

        // Check if pending destroy (committed entity marked for destruction in this transaction)
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
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

        Activity activity = null;
        if (TelemetryConfig.EcsActive)
        {
            activity = TyphonActivitySource.StartActivity("ECS.Destroy");
            activity?.SetTag(TyphonSpanAttributes.EntityId, (long)id.RawValue);
        }

        DestroyInternal(id, 0, out int totalDestroyed);

        if (activity != null)
        {
            if (totalDestroyed > 1)
            {
                activity.SetTag(TyphonSpanAttributes.EcsCascadeCount, totalDestroyed - 1);
            }
            activity.Dispose();
        }
    }

    /// <summary>Mark an entity link target for destruction.</summary>
    public void Destroy<T>(EntityLink<T> link) where T : class => Destroy(link.Id);

    /// <summary>Maximum cascade depth. DAG validation prevents cycles, but this guards against bugs.</summary>
    private const int MaxCascadeDepth = 32;

    /// <summary>Internal recursive destroy with cascade support.</summary>
    private void DestroyInternal(EntityId id, int depth, out int totalDestroyed)
    {
        totalDestroyed = 0;

        if (depth >= MaxCascadeDepth)
        {
            throw new InvalidOperationException(
                $"Cascade delete exceeded max depth {MaxCascadeDepth} at entity {id}. " +
                "This indicates a bug in cascade graph validation — cycles should be caught at registration time.");
        }

        // Check if already pending destroy (avoid double-destroy)
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return;
        }

        // Check if already pending spawn (destroy own spawn)
        bool isPending = SpawnedContains(id);
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
    /// Scans spawned entities (via EntityMap) and committed entities (via FK index).
    /// </summary>
    private List<EntityId> FindCascadeChildren(ArchetypeMetadata childMeta, CascadeTarget target, EntityId parentId)
    {
        var result = new List<EntityId>();

        // 1. Scan spawned entities for FK matches (read component data from SpawnEntry locations directly)
        if (_spawnedEntities != null)
        {
            for (int i = 0; i < _spawnedEntities.Count; i++)
            {
                var entry = _spawnedEntities[i];
                if (entry.Id.ArchetypeId != target.ChildArchetypeId)
                {
                    continue;
                }

                int chunkId = entry.Loc[target.FkSlotIndex];
                if (chunkId == 0)
                {
                    continue;
                }

                // For Versioned FK slot: chunkId is compContentChunkId in GetLoc, but need to check SingleCache for COW
                var spawnMeta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
                var spawnES = _dbe._archetypeStates[spawnMeta.ArchetypeId];
                var table = spawnES.SlotToComponentTable[target.FkSlotIndex];
                var compType = spawnMeta._slotToComponentType[target.FkSlotIndex];
                var info = GetComponentInfo(compType);

                int dataChunkId = chunkId;
                if (table.StorageMode == StorageMode.Versioned &&
                    !info.IsMultiple && info.SingleCache.TryGetValue((long)entry.Id.RawValue, out var cri))
                {
                    dataChunkId = cri.CurCompContentChunkId;
                }

                byte* ptr = table.StorageMode == StorageMode.Transient ? 
                    info.TransientCompContentAccessor.GetChunkAddress(dataChunkId) : info.CompContentAccessor.GetChunkAddress(dataChunkId);

                var fkEntityId = *(EntityId*)(ptr + table.ComponentOverhead + target.FkFieldOffset);
                if (fkEntityId == parentId)
                {
                    result.Add(entry.Id);
                }
            }
        }

        // 2. Find committed children via FK index lookup (O(log n + k) instead of O(n) EntityMap scan)
        var childEngineState = _dbe._archetypeStates[target.ChildArchetypeId];
        if (childEngineState?.SlotToComponentTable != null)
        {
            var table = childEngineState.SlotToComponentTable[target.FkSlotIndex];
            var fkIndexInfo = PipelineExecutor.FindFKIndex(table, target.FkFieldOffset);
            var fkIndex = (BTree<long, PersistentStore>)fkIndexInfo.Index;
            long parentPK = (long)parentId.RawValue;

            using var guard = EpochGuard.Enter(_epochManager);
            var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();

            var enumerator = fkIndex.EnumerateRangeMultiple(parentPK, parentPK);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (int j = 0; j < values.Length; j++)
                        {
                            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                            long childPK = header.EntityPK;
                            var childId = Unsafe.As<long, EntityId>(ref childPK);
                            if (childId.ArchetypeId == target.ChildArchetypeId)
                            {
                                result.Add(childId);
                            }
                        }
                    } while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
                compRevAccessor.Dispose();
            }
        }

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
    // Pending spawn query support (read-your-own-writes)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Pending spawns — exposed for EcsQuery read-your-own-writes support.</summary>
    internal List<SpawnEntry> PendingSpawns => _spawnedEntities;

    /// <summary>Pending destroys — exposed for EcsQuery read-your-own-writes support.</summary>
    internal HashSet<EntityId> PendingDestroys => _pendingDestroys;

    /// <summary>Pending EnabledBits overrides — exposed for EcsQuery read-your-own-writes support.</summary>
    internal Dictionary<EntityId, ushort> PendingEnableDisable => _pendingEnableDisable;

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

        // Check if this entity was spawned in this transaction (not yet in EntityMap)
        int spawnIdx = SpawnedIndexOf(id);
        bool isOwnSpawn = spawnIdx >= 0;

        // Early destroy check for own spawns
        if (isOwnSpawn && _pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return default;
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return default;
        }

        if (isOwnSpawn)
        {
            // Own spawn: build EntityRef directly from SpawnEntry (entity not in EntityMap yet)
            var entry = _spawnedEntities[spawnIdx];

            ushort enabledBits = entry.EnabledBits;
            if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(id, out var pendingBits))
            {
                enabledBits = pendingBits;
            }

            var result = new EntityRef(id, meta, es, this, enabledBits, writable);
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                result.SetLocation(slot, entry.Loc[slot]);
            }

            // For Versioned: override from SingleCache (same as before — Spawn already populated it)
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

                if (!info.IsMultiple && info.SingleCache.TryGetValue(pk, out var cached))
                {
                    result.SetLocation(slot, cached.CurCompContentChunkId);
                }
            }

            return result;
        }

        // Committed entity: read from EntityMap
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

        // Check pending destroy for committed entities
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return default;
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);

        // Visibility check
        if (!header.IsVisibleAt(TSN))
        {
            return default;
        }

        // Resolve EnabledBits: committed entities check MVCC overrides
        {
            ushort enabledBits = _dbe.EnabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, TSN);

            // Check for pending enable/disable override
            if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(id, out var pendingBits))
            {
                enabledBits = pendingBits;
            }

            var result = new EntityRef(id, meta, es, this, enabledBits, writable);
            result.CopyLocationsFrom(readBuf, meta.ComponentCount);

            // For Versioned components: resolve MVCC-visible chunkId via SingleCache or revision chain walk.
            // Location[slot] from EntityMap is compRevFirstChunkId.
            // For committed entities, walk the revision chain to find the visible version.
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

                // If already resolved in this transaction (prior Open or Write), reuse cached entry
                if (!info.IsMultiple && info.SingleCache.TryGetValue(pk, out var cached))
                {
                    result.SetLocation(slot, cached.CurCompContentChunkId);
                    continue;
                }

                // Walk revision chain from EntityMap's compRevFirstChunkId
                int compRevFirstChunkId = result.GetLocation(slot);
                if (compRevFirstChunkId == 0)
                {
                    continue;
                }

                var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN);
                if (chainResult.IsFailure)
                {
                    continue;
                }

                // Cache CompRevInfo for conflict detection
                var compRevInfo = chainResult.Value;
                compRevInfo.Operations = ComponentInfo.OperationType.Read;
                info.AddNew(pk, compRevInfo);
                result.SetLocation(slot, compRevInfo.CurCompContentChunkId);
            }

            return result;
        }
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
        // Enable/Disable for committed entities: directly upserts to EntityMap.
        // For spawned entities: skip here, FinalizeSpawns applies the override.
        FlushPendingEnableDisable();
        // Finalize spawned entities: set BornTSN from sentinel to actual TSN, insert SV secondary indexes.
        FinalizeSpawns();
        FlushPendingDestroys();
    }

    /// <summary>
    /// Finalize spawned entities: set BornTSN from sentinel (MaxValue) to actual TSN, making them visible.
    /// Also inserts SV secondary indexes (Versioned secondary indexes are handled by CommitComponentCore).
    /// </summary>
    private void FinalizeSpawns()
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return;
        }

        // Pre-size EntityMaps to avoid per-insert splits
        if (_spawnedEntities.Count >= 64)
        {
            Span<ushort> seenArchetypes = stackalloc ushort[16];
            int seenCount = 0;
            foreach (var entry in _spawnedEntities)
            {
                var archId = entry.Id.ArchetypeId;
                bool alreadySeen = false;
                for (int i = 0; i < seenCount; i++)
                {
                    if (seenArchetypes[i] == archId) { alreadySeen = true; break; }
                }
                if (alreadySeen) continue;
                if (seenCount < 16) seenArchetypes[seenCount++] = archId;

                var es = _dbe._archetypeStates[archId];
                if (es?.EntityMap != null)
                {
                    es.EntityMap.EnsureCapacity((int)es.EntityMap.EntryCount + _spawnedEntities.Count, _changeSet);
                }
            }
        }

        using var guard = EpochGuard.Enter(_epochManager);

        // Hoist stackalloc outside the loop — max record size is 78B (14B header + 16 components × 4B)
        byte* recordPtr = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        // Hoist all accessors outside the per-entity loop.
        // Track last-used archetype — covers the dominant case (single archetype per TX).
        // When archetype changes, dispose old accessors and create new ones.
        ushort lastArchId = 0;
        var mapAccessor = default(ChunkAccessor<PersistentStore>);
        bool hasMapAccessor = false;

        // Hoisted SV index accessors — one compAccessor per SV indexed slot, one idxAccessor per indexed field.
        // Allocated lazily with exact sizes when archetype changes (typically 1-3 slots, 2-8 indexes).
        Span<int> svSlots = stackalloc int[16];
        int svSlotCount = 0;
        ChunkAccessor<PersistentStore>[] svCompAccessors = null;
        ChunkAccessor<PersistentStore>[] svIdxAccessors = null;
        Span<int> svIdxAccessorBase = stackalloc int[16]; // offset into svIdxAccessors for each slot
        int svIdxAccessorTotal = 0;

        // Per-archetype cached state — avoids per-entity metadata lookups
        ArchetypeMetadata meta = null;
        ArchetypeEngineState engineState = null;
        int componentCount = 0;
        ushort versionedMask = 0; // bit set for Versioned slots — eliminates per-slot table dereference

        try
        {
            foreach (var entry in _spawnedEntities)
            {
                // Skip entities that were also destroyed in this transaction — no EntityMap insert needed
                if (_pendingDestroys != null && _pendingDestroys.Contains(entry.Id))
                {
                    continue;
                }

                // Build EntityRecord on stack from SpawnEntry
                ref var header = ref *(EntityRecordHeader*)recordPtr;
                header = default;
                header.BornTSN = TSN;

                ushort enabledBits = entry.EnabledBits;
                if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(entry.Id, out var newBits))
                {
                    enabledBits = newBits;
                }
                header.EnabledBits = enabledBits;

                // Hoist all per-archetype state — recycle when archetype changes
                if (!hasMapAccessor || entry.Id.ArchetypeId != lastArchId)
                {
                    // Dispose previous archetype's accessors
                    if (hasMapAccessor)
                    {
                        mapAccessor.Dispose();
                        for (int si = 0; si < svSlotCount; si++)
                        {
                            svCompAccessors[si].Dispose();
                        }
                        for (int ai = 0; ai < svIdxAccessorTotal; ai++)
                        {
                            svIdxAccessors[ai].Dispose();
                        }
                    }

                    // Cache archetype metadata + compute versioned slot mask
                    meta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
                    engineState = _dbe._archetypeStates[meta.ArchetypeId];
                    componentCount = meta.ComponentCount;
                    versionedMask = 0;
                    for (int slot = 0; slot < componentCount; slot++)
                    {
                        if (engineState.SlotToComponentTable[slot].StorageMode == StorageMode.Versioned)
                        {
                            versionedMask |= (ushort)(1 << slot);
                        }
                    }

                    mapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
                    lastArchId = entry.Id.ArchetypeId;
                    hasMapAccessor = true;

                    // Build SV indexed slot accessors for this archetype.
                    // First pass: count, then allocate exact sizes.
                    svSlotCount = 0;
                    svIdxAccessorTotal = 0;
                    int idxCount = 0;
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        var table = engineState.SlotToComponentTable[slot];
                        if (table.StorageMode == StorageMode.Versioned)
                        {
                            continue;
                        }
                        var ifi = table.IndexedFieldInfos;
                        if (ifi == null || ifi.Length == 0)
                        {
                            continue;
                        }
                        svSlotCount++;
                        idxCount += ifi.Length;
                    }

                    if (svSlotCount > 0)
                    {
                        // Reuse arrays if large enough, otherwise allocate exact size
                        if (svCompAccessors == null || svCompAccessors.Length < svSlotCount)
                        {
                            svCompAccessors = new ChunkAccessor<PersistentStore>[svSlotCount];
                        }
                        if (svIdxAccessors == null || svIdxAccessors.Length < idxCount)
                        {
                            svIdxAccessors = new ChunkAccessor<PersistentStore>[idxCount];
                        }
                    }

                    svSlotCount = 0;
                    svIdxAccessorTotal = 0;
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        var table = engineState.SlotToComponentTable[slot];
                        if (table.StorageMode == StorageMode.Versioned)
                        {
                            continue;
                        }
                        var indexedFieldInfos = table.IndexedFieldInfos;
                        if (indexedFieldInfos == null || indexedFieldInfos.Length == 0)
                        {
                            continue;
                        }
                        Debug.Assert(table.StorageMode != StorageMode.Transient, "Transient secondary indexes not yet implemented");

                        svSlots[svSlotCount] = slot;
                        svCompAccessors[svSlotCount] = table.ComponentSegment.CreateChunkAccessor(_changeSet);
                        svIdxAccessorBase[svSlotCount] = svIdxAccessorTotal;
                        for (int i = 0; i < indexedFieldInfos.Length; i++)
                        {
                            svIdxAccessors[svIdxAccessorTotal++] = indexedFieldInfos[i].Index.Segment.CreateChunkAccessor(_changeSet);
                        }
                        svSlotCount++;
                    }
                }

                // Build location array from SpawnEntry using pre-computed versioned mask
                var locDest = (int*)(recordPtr + EntityRecordAccessor.HeaderSize);
                for (int slot = 0; slot < componentCount; slot++)
                {
                    locDest[slot] = (versionedMask & (1 << slot)) != 0 ? entry.Rev[slot] : entry.Loc[slot];
                }

                // Insert into EntityMap — skip duplicate check (EntityKey is freshly generated, guaranteed unique)
                engineState.EntityMap.InsertNew(entry.Id.EntityKey, recordPtr, ref mapAccessor, _changeSet);

                // Insert SV secondary indexes (Versioned is handled by CommitComponentCore).
                // Accessors are hoisted: created once when archetype changes (alongside mapAccessor),
                // reused across all entities of the same archetype.
                for (int si = 0; si < svSlotCount; si++)
                {
                    int slot = svSlots[si];
                    var table = engineState.SlotToComponentTable[slot];
                    int chunkId = entry.Loc[slot];
                    if (chunkId == 0)
                    {
                        continue;
                    }

                    byte* chunkAddr = svCompAccessors[si].GetChunkAddress(chunkId, true);
                    var indexedFieldInfos = table.IndexedFieldInfos;

                    for (int i = 0; i < indexedFieldInfos.Length; i++)
                    {
                        ref var ifi = ref indexedFieldInfos[i];
                        if (ifi.Index.AllowMultiple)
                        {
                            *(int*)&chunkAddr[ifi.OffsetToIndexElementId] =
                                ifi.Index.Add(&chunkAddr[ifi.OffsetToField], chunkId, ref svIdxAccessors[svIdxAccessorBase[si] + i], out _);
                        }
                        else
                        {
                            ifi.Index.Add(&chunkAddr[ifi.OffsetToField], chunkId, ref svIdxAccessors[svIdxAccessorBase[si] + i]);
                        }
                    }
                }
            }
        }
        finally
        {
            if (hasMapAccessor)
            {
                mapAccessor.Dispose();
                for (int si = 0; si < svSlotCount; si++)
                {
                    svCompAccessors[si].Dispose();
                }
                for (int ai = 0; ai < svIdxAccessorTotal; ai++)
                {
                    svIdxAccessors[ai].Dispose();
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

        // Hoist EntityMap accessor — reuse when archetype matches (same pattern as FinalizeSpawns)
        ushort lastArchId = 0;
        var accessor = default(ChunkAccessor<PersistentStore>);
        bool hasAccessor = false;

        try
        {
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

                if (!hasAccessor || entityId.ArchetypeId != lastArchId)
                {
                    if (hasAccessor)
                    {
                        accessor.Dispose();
                    }
                    accessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
                    lastArchId = entityId.ArchetypeId;
                    hasAccessor = true;
                }

                if (engineState.EntityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
                {
                    // Set DiedTSN
                    EntityRecordAccessor.GetHeader(readBuf).DiedTSN = TSN;
                    engineState.EntityMap.Upsert(entityId.EntityKey, readBuf, ref accessor, _changeSet);

                    // Enqueue for deferred GC (LinearHash removal + chunk freeing when MinTSN advances past DiedTSN)
                    _dbe.EnqueueEcsCleanup(entityId, meta, TSN);
                }
            }
        }
        finally
        {
            if (hasAccessor)
            {
                accessor.Dispose();
            }
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
            // Skip entities that were spawned in this same transaction — they have no committed component data to delete
            // (FinalizeSpawns skips spawn+destroy entities).
            if (SpawnedContains(entityId))
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

            // Skip spawned entities — FinalizeSpawns applies the enable/disable override
            if (SpawnedContains(entityId))
            {
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
        // If transaction was NOT committed, free component chunks for spawned entities.
        // Entity was never inserted into EntityMap, so no EntityMap.Remove needed.
        if (_spawnedEntities is { Count: > 0 } && State != TransactionState.Committed)
        {
            foreach (var entry in _spawnedEntities)
            {
                var meta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
                if (meta == null)
                {
                    continue;
                }
                var engineState = _dbe._archetypeStates[meta.ArchetypeId];
                if (engineState?.SlotToComponentTable == null)
                {
                    continue;
                }

                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = engineState.SlotToComponentTable[slot];

                    if (table.StorageMode == StorageMode.Versioned)
                    {
                        // Versioned: free componentChunkId from SpawnEntry + compRev chain from SingleCache
                        int chunkId = entry.Loc[slot];
                        if (chunkId > 0)
                        {
                            table.ComponentSegment.FreeChunk(chunkId);
                        }

                        var compType = meta._slotToComponentType[slot];
                        if (_componentInfos.TryGetValue(compType, out var info) &&
                            !info.IsMultiple && info.SingleCache.TryGetValue((long)entry.Id.RawValue, out var cri))
                        {
                            if (cri.CompRevTableFirstChunkId > 0)
                            {
                                table.CompRevTableSegment.FreeChunk(cri.CompRevTableFirstChunkId);
                            }
                        }
                    }
                    else
                    {
                        // SV/Transient: free componentChunkId from SpawnEntry directly
                        int chunkId = entry.Loc[slot];
                        if (chunkId != 0)
                        {
                            if (table.StorageMode == StorageMode.Transient)
                            {
                                table.TransientComponentSegment.FreeChunk(chunkId);
                            }
                            else
                            {
                                table.ComponentSegment.FreeChunk(chunkId);
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

        _spawnedEntities?.Clear();
        _spawnedEntityIndex?.Clear();
        _pendingDestroys?.Clear();
        _pendingEnableDisable?.Clear();
    }
}
