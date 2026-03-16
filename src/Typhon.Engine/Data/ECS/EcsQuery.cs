using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// ECS query builder with three-tier evaluation: T1 (ArchetypeMask), T2 (EnabledBits), T3 (WHERE — future).
/// Supports polymorphic queries (archetype + descendants) and exact queries (single archetype).
/// </summary>
[PublicAPI]
#pragma warning disable TYPHON005 // EcsQuery borrows Transaction, doesn't own it
public unsafe struct EcsQuery<TArchetype> where TArchetype : class
{
    private Transaction _tx;
    private ArchetypeMask256 _mask256;          // used when _useLargeMask == false
    private ArchetypeMaskLarge _maskLarge;       // used when _useLargeMask == true
    private bool _useLargeMask;
    private int _enabledTypeIdCount;
    private int _disabledTypeIdCount;
    private int _enabledTypeId0, _enabledTypeId1, _enabledTypeId2, _enabledTypeId3;
    private int _disabledTypeId0, _disabledTypeId1, _disabledTypeId2, _disabledTypeId3;
    private Func<EntityId, Transaction, bool> _whereFilter;

    internal EcsQuery(Transaction tx, bool polymorphic)
    {
        _tx = tx;
        _useLargeMask = !ArchetypeRegistry.UseSmallMask;

        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        if (meta == null)
        {
            return;
        }

        if (_useLargeMask)
        {
            _maskLarge = polymorphic && meta.SubtreeArchetypeIds != null ? 
                ArchetypeMaskLarge.FromSubtree(meta.SubtreeArchetypeIds, ArchetypeRegistry.MaxArchetypeId) : 
                ArchetypeMaskLarge.FromArchetype(meta.ArchetypeId, ArchetypeRegistry.MaxArchetypeId);
        }
        else
        {
            _mask256 = polymorphic && meta.SubtreeArchetypeIds != null ? 
                ArchetypeMask256.FromSubtree(meta.SubtreeArchetypeIds) : ArchetypeMask256.FromArchetype(meta.ArchetypeId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 1 constraints — ArchetypeMask
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Include only archetypes that declare <typeparamref name="T"/>. Mask AND.</summary>
    public EcsQuery<TArchetype> With<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (typeId < 0)
        {
            _mask256 = default;
            _maskLarge = default;
            return this;
        }
        if (_useLargeMask)
        {
            _maskLarge = _maskLarge.And(ArchetypeRegistry.GetComponentMaskLarge(typeId));
        }
        else
        {
            _mask256 = _mask256.And(ArchetypeRegistry.GetComponentMask(typeId));
        }
        return this;
    }

    /// <summary>Exclude archetypes that declare <typeparamref name="T"/>. Mask AND NOT.</summary>
    public EcsQuery<TArchetype> Without<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (typeId < 0)
        {
            return this;
        }
        if (_useLargeMask)
        {
            _maskLarge = _maskLarge.AndNot(ArchetypeRegistry.GetComponentMaskLarge(typeId));
        }
        else
        {
            _mask256 = _mask256.AndNot(ArchetypeRegistry.GetComponentMask(typeId));
        }
        return this;
    }

    /// <summary>Remove an archetype subtree. Mask AND NOT subtree.</summary>
    public EcsQuery<TArchetype> Exclude<TExcluded>() where TExcluded : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TExcluded>();
        if (meta == null)
        {
            return this;
        }

        if (_useLargeMask)
        {
            var excludeMask = meta.SubtreeArchetypeIds != null ? 
                ArchetypeMaskLarge.FromSubtree(meta.SubtreeArchetypeIds, ArchetypeRegistry.MaxArchetypeId) :
                ArchetypeMaskLarge.FromArchetype(meta.ArchetypeId, ArchetypeRegistry.MaxArchetypeId);
            _maskLarge = _maskLarge.AndNot(excludeMask);
        }
        else
        {
            var excludeMask = meta.SubtreeArchetypeIds != null ? 
                ArchetypeMask256.FromSubtree(meta.SubtreeArchetypeIds) : ArchetypeMask256.FromArchetype(meta.ArchetypeId);
            _mask256 = _mask256.AndNot(excludeMask);
        }
        return this;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 2 constraints — EnabledBits
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Include only entities where <typeparamref name="T"/> is enabled.</summary>
    public EcsQuery<TArchetype> Enabled<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component {typeof(T).Name} not registered");
        AddEnabledTypeId(typeId);
        return this;
    }

    /// <summary>Include only entities where <typeparamref name="T"/> is disabled.</summary>
    public EcsQuery<TArchetype> Disabled<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component {typeof(T).Name} not registered");
        AddDisabledTypeId(typeId);
        return this;
    }

    private void AddEnabledTypeId(int typeId)
    {
        switch (_enabledTypeIdCount)
        {
            case 0: _enabledTypeId0 = typeId; break;
            case 1: _enabledTypeId1 = typeId; break;
            case 2: _enabledTypeId2 = typeId; break;
            case 3: _enabledTypeId3 = typeId; break;
            default: throw new InvalidOperationException("Max 4 Enabled<T> constraints per query");
        }
        _enabledTypeIdCount++;
    }

    private void AddDisabledTypeId(int typeId)
    {
        switch (_disabledTypeIdCount)
        {
            case 0: _disabledTypeId0 = typeId; break;
            case 1: _disabledTypeId1 = typeId; break;
            case 2: _disabledTypeId2 = typeId; break;
            case 3: _disabledTypeId3 = typeId; break;
            default: throw new InvalidOperationException("Max 4 Disabled<T> constraints per query");
        }
        _disabledTypeIdCount++;
    }

    private bool HasT2 => _enabledTypeIdCount > 0 || _disabledTypeIdCount > 0;

    private bool MaskIsEmpty => _useLargeMask ? _maskLarge.IsEmpty : _mask256.IsEmpty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaskTest(ushort archetypeId) => _useLargeMask ? _maskLarge.Test(archetypeId) : _mask256.Test(archetypeId);

    private int MaskMaxId => _useLargeMask ? _maskLarge.MaxId : _mask256.MaxId;

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 3 constraints — WHERE predicates (broad scan evaluation)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Filter entities by a component field predicate. Evaluated per-entity during broad scan via <see cref="Transaction.Open"/> + <see cref="EntityRef.TryRead{T}"/>.
    /// Multiple Where calls chain as AND (each must pass).
    /// </summary>
    /// <remarks>Targeted scan (index-first) is not yet available — always uses broad scan.</remarks>
    public EcsQuery<TArchetype> Where<T>(Func<T, bool> predicate) where T : unmanaged
    {
        var prevFilter = _whereFilter;
        _whereFilter = prevFilter == null ? (id, tx) =>
            {
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && predicate(value);
            } : (id, tx) =>
            {
                if (!prevFilter(id, tx))
                {
                    return false;
                }
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && predicate(value);
            };
        return this;
    }

    /// <summary>Resolve T2 masks for a specific archetype.</summary>
    private bool ResolveT2Masks(ArchetypeMetadata meta, out ushort requiredEnabled, out ushort requiredDisabled)
    {
        requiredEnabled = 0;
        requiredDisabled = 0;

        for (int i = 0; i < _enabledTypeIdCount; i++)
        {
            int typeId = i switch { 0 => _enabledTypeId0, 1 => _enabledTypeId1, 2 => _enabledTypeId2, _ => _enabledTypeId3 };
            if (!meta.TryGetSlot(typeId, out byte slot))
            {
                return false;
            }
            requiredEnabled |= (ushort)(1 << slot);
        }

        for (int i = 0; i < _disabledTypeIdCount; i++)
        {
            int typeId = i switch { 0 => _disabledTypeId0, 1 => _disabledTypeId1, 2 => _disabledTypeId2, _ => _disabledTypeId3 };
            if (!meta.TryGetSlot(typeId, out byte slot))
            {
                continue;
            }
            requiredDisabled |= (ushort)(1 << slot);
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Execution — broad scan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Create a persistent, refreshable View from this query. Initial population via Execute().</summary>
    public EcsView<TArchetype> ToView()
    {
        var initialSet = Execute();
        return new EcsView<TArchetype>(this, initialSet, _tx.TSN);
    }

    /// <summary>Rebind this query to a different transaction (different TSN → different visibility).</summary>
    internal void UpdateTransaction(Transaction tx) => _tx = tx;

    /// <summary>Execute the query and collect matching entity IDs into a HashSet.</summary>
    public HashSet<EntityId> Execute()
    {
        var result = new HashSet<EntityId>();
        if (MaskIsEmpty)
        {
            return result;
        }

        CollectMatching((id, _) => result.Add(id));

        // T3 post-filter: evaluate WHERE predicate per entity via Transaction.Open
        var filter = _whereFilter;
        var tx = _tx;
        if (filter != null)
        {
            result.RemoveWhere(id => !filter(id, tx));
        }

        return result;
    }

    /// <summary>Count matching entities.</summary>
    public int Count()
    {
        if (MaskIsEmpty)
        {
            return 0;
        }

        // If WHERE filter, use Execute (which applies post-filter) then count
        if (_whereFilter != null)
        {
            return Execute().Count;
        }

        int count = 0;
        CollectMatching((_, _) => count++);
        return count;
    }

    /// <summary>Test if any entity matches. Short-circuits on first match.</summary>
    public bool Any()
    {
        if (MaskIsEmpty)
        {
            return false;
        }

        if (_whereFilter != null)
        {
            return Execute().Count > 0;
        }

        bool found = false;
        CollectMatching((_, _) => found = true, stopOnFirst: true);
        return found;
    }

    /// <summary>Get an enumerator for foreach support. Pre-collects matching entities then iterates.</summary>
    public EcsQueryEnumerator GetEnumerator()
    {
        var entities = new List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, byte[] RecordBytes)>();
        if (!MaskIsEmpty)
        {
            CollectMatchingFull(entities);
        }
        return new EcsQueryEnumerator(_tx, entities, _whereFilter);
    }

    /// <summary>
    /// Core broad scan: iterate matching archetypes, then all entities in each LinearHash.
    /// Dispatches to the generic core once — the JIT fully specializes per TMask type.
    /// </summary>
    private void CollectMatching(Action<EntityId, ushort> onMatch, bool stopOnFirst = false)
    {
        if (_useLargeMask)
        {
            CollectMatchingCore(_maskLarge, onMatch, stopOnFirst);
        }
        else
        {
            CollectMatchingCore(_mask256, onMatch, stopOnFirst);
        }
    }

    /// <summary>Collect full entity data for foreach enumeration. Dispatches to generic core.</summary>
    private void CollectMatchingFull(List<(EntityId, ArchetypeMetadata, ushort, byte[])> results)
    {
        if (_useLargeMask)
        {
            CollectMatchingFullCore(_maskLarge, results);
        }
        else
        {
            CollectMatchingFullCore(_mask256, results);
        }
    }

    /// <summary>
    /// JIT-specialized broad scan. TMask.Test() is inlined — zero virtual dispatch, zero branch per entity.
    /// Two native code paths emitted: one for ArchetypeMask256 (fixed ulong[4]), one for ArchetypeMaskLarge (ulong[]).
    /// </summary>
    private void CollectMatchingCore<TMask>(TMask mask, Action<EntityId, ushort> onMatch, bool stopOnFirst) where TMask : struct, IArchetypeMask<TMask>
    {
        long txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        bool hasT2 = HasT2;

        for (int archBit = 0; archBit <= mask.MaxId; archBit++)
        {
            if (!mask.Test((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null || engineState.SlotToComponentTable == null)
            {
                continue;
            }

            ushort reqEnabled = 0, reqDisabled = 0;
            if (hasT2 && !ResolveT2Masks(meta, out reqEnabled, out reqDisabled))
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var action = new BroadScanAction
            {
                Meta = meta,
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                OnMatch = onMatch,
                StopOnFirst = stopOnFirst,
                Found = false,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref action);
            accessor.Dispose();

            if (stopOnFirst && action.Found)
            {
                return;
            }
        }
    }

    /// <summary>JIT-specialized variant for full entity data collection (foreach enumeration).</summary>
    private void CollectMatchingFullCore<TMask>(TMask mask, List<(EntityId, ArchetypeMetadata, ushort, byte[])> results) where TMask : struct, IArchetypeMask<TMask>
    {
        long txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        bool hasT2 = HasT2;

        for (int archBit = 0; archBit <= mask.MaxId; archBit++)
        {
            if (!mask.Test((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null || engineState.SlotToComponentTable == null)
            {
                continue;
            }

            ushort reqEnabled = 0, reqDisabled = 0;
            if (hasT2 && !ResolveT2Masks(meta, out reqEnabled, out reqDisabled))
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var action = new BroadScanCollectAction
            {
                Meta = meta,
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                Results = results,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref action);
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Broad scan action structs (JIT-specialized callbacks for ForEachEntry)
    // ═══════════════════════════════════════════════════════════════════════

    private struct BroadScanAction : RawValueHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ArchetypeMetadata Meta;
        public long TxTsn;
        public EnabledBitsOverrides EnabledBitsOverrides;
        public bool HasT2;
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public Action<EntityId, ushort> OnMatch;
        public bool StopOnFirst;
        public bool Found;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            // Visibility check
            if (header.BornTSN != 0 && header.BornTSN > TxTsn)
            {
                return true; // Not yet born — skip, continue
            }
            if (header.DiedTSN != 0 && header.DiedTSN <= TxTsn)
            {
                return true; // Dead — skip, continue
            }

            // T2 check
            if (HasT2)
            {
                ushort enabledBits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
                if ((enabledBits & RequiredEnabled) != RequiredEnabled)
                {
                    return true;
                }
                if ((enabledBits & RequiredDisabled) != 0)
                {
                    return true;
                }
            }

            var entityId = new EntityId(key, Meta.ArchetypeId);
            ushort bits = HasT2 ? EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn) : header.EnabledBits;
            OnMatch(entityId, bits);

            if (StopOnFirst)
            {
                Found = true;
                return false; // Stop iteration
            }
            return true;
        }
    }

    private struct BroadScanCollectAction : RawValueHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ArchetypeMetadata Meta;
        public long TxTsn;
        public EnabledBitsOverrides EnabledBitsOverrides;
        public bool HasT2;
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public List<(EntityId, ArchetypeMetadata, ushort, byte[])> Results;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            if (header.BornTSN != 0 && header.BornTSN > TxTsn)
            {
                return true;
            }
            if (header.DiedTSN != 0 && header.DiedTSN <= TxTsn)
            {
                return true;
            }

            if (HasT2)
            {
                ushort enabledBits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
                if ((enabledBits & RequiredEnabled) != RequiredEnabled)
                {
                    return true;
                }
                if ((enabledBits & RequiredDisabled) != 0)
                {
                    return true;
                }
            }

            ushort bits = HasT2 ? EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn) : header.EnabledBits;

            // Copy entity record for EntityRef construction
            int recordSize = EntityRecordAccessor.RecordSize(Meta.ComponentCount);
            var recordBytes = new byte[recordSize];
            fixed (byte* dst = recordBytes)
            {
                Unsafe.CopyBlock(dst, value, (uint)recordSize);
            }

            Results.Add((new EntityId(key, Meta.ArchetypeId), Meta, bits, recordBytes));
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumerator (iterates pre-collected results)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Iterates pre-collected query results, yielding EntityRefs with zero-copy component access.</summary>
    [PublicAPI]
    public ref struct EcsQueryEnumerator
    {
        private readonly Transaction _tx;
        private readonly List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, byte[] RecordBytes)> _entities;
        private readonly Func<EntityId, Transaction, bool> _whereFilter;
        private int _index;
        private EntityRef _current;

        internal EcsQueryEnumerator(Transaction tx, List<(EntityId, ArchetypeMetadata, ushort, byte[])> entities, Func<EntityId, Transaction, bool> whereFilter)
        {
            _tx = tx;
            _entities = entities;
            _whereFilter = whereFilter;
            _index = -1;
        }

        public EntityRef Current => _current;

        public bool MoveNext()
        {
            while (true)
            {
                _index++;
                if (_index >= _entities.Count)
                {
                    return false;
                }

                var (id, meta, enabledBits, recordBytes) = _entities[_index];

                // T3 post-filter: evaluate WHERE via Transaction.Open
                if (_whereFilter != null && !_whereFilter(id, _tx))
                {
                    continue;
                }

                var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
                _current = new EntityRef(id, meta, engineState, _tx, enabledBits, false);
                _current.CopyLocationsFrom(recordBytes, meta.ComponentCount);
                return true;
            }
        }

        public void Dispose() { }
    }
}
#pragma warning restore TYPHON005
