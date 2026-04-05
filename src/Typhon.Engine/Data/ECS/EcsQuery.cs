using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>Type of spatial predicate attached to an EcsQuery.</summary>
internal enum SpatialQueryType : byte
{
    None = 0,
    AABB = 1,
    Radius = 2,
    Ray = 3,
}

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

    // Expression-based WHERE state (for incremental views)
    private FieldPredicate[][] _fieldPredicateBranches;
    private ComponentTable _whereComponentTable;
    private EcsViewFieldReader _whereFieldReader;

    // OrderBy/Skip/Take state
    private OrderByField? _orderBy;
    private int _skip;
    private int _take;
    private int _enabledTypeId0, _enabledTypeId1, _enabledTypeId2, _enabledTypeId3;
    private int _disabledTypeId0, _disabledTypeId1, _disabledTypeId2, _disabledTypeId3;
    private Func<EntityId, Transaction, bool> _whereFilter;
    private Func<EntityId, Transaction, bool> _pendingSpawnFieldFilter;

    // Spatial query predicate (at most one per query)
    private ComponentTable _spatialTable;
    private SpatialQueryType _spatialQueryType;
    // Inline query parameters: meaning depends on _spatialQueryType
    // AABB: [min0..max0..] in [0]..[5]. Radius: center in [0]..[2], radius in [3]. Ray: origin in [0]..[2], dir in [3]..[5], maxDist in [6].
    private fixed double _spatialParams[7];

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
            default: throw new InvalidOperationException("Max 4 Enabled<T> constraints per query. Use archetype hierarchy or component composition to reduce filter count.");
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
            default: throw new InvalidOperationException("Max 4 Disabled<T> constraints per query. Use archetype hierarchy or component composition to reduce filter count.");
        }
        _disabledTypeIdCount++;
    }

    private readonly bool HasT2 => _enabledTypeIdCount > 0 || _disabledTypeIdCount > 0;

    private readonly bool MaskIsEmpty => _useLargeMask ? _maskLarge.IsEmpty : _mask256.IsEmpty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool MaskTest(ushort archetypeId) => _useLargeMask ? _maskLarge.Test(archetypeId) : _mask256.Test(archetypeId);

    private readonly int MaskMaxId => _useLargeMask ? _maskLarge.MaxId : _mask256.MaxId;

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

    /// <summary>
    /// Filter entities by an indexed-field predicate, enabling incremental view refresh via <see cref="ViewDeltaRingBuffer"/>.
    /// The expression is parsed into <see cref="FieldEvaluator"/> for boundary crossing detection. Requires indexed fields.
    /// </summary>
    public EcsQuery<TArchetype> WhereField<T>(Expression<Func<T, bool>> predicate) where T : unmanaged
    {
        var ct = _tx.DBE.GetComponentTable<T>();
        if (ct == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T).Name} is not registered.");
        }

        var branches = ExpressionParser.ParseDnf(predicate);

        if (_fieldPredicateBranches != null)
        {
            // Multiple WhereField calls: cross-product (AND of ORs)
            var combined = new FieldPredicate[_fieldPredicateBranches.Length * branches.Length][];
            var idx = 0;
            for (var l = 0; l < _fieldPredicateBranches.Length; l++)
            {
                for (var r = 0; r < branches.Length; r++)
                {
                    var merged = new FieldPredicate[_fieldPredicateBranches[l].Length + branches[r].Length];
                    Array.Copy(_fieldPredicateBranches[l], merged, _fieldPredicateBranches[l].Length);
                    Array.Copy(branches[r], 0, merged, _fieldPredicateBranches[l].Length, branches[r].Length);
                    combined[idx++] = merged;
                }
            }
            _fieldPredicateBranches = combined;
        }
        else
        {
            _fieldPredicateBranches = branches;
        }

        _whereComponentTable = ct;
        _whereFieldReader = EcsViewFieldReader<T>.Instance;

        // Compile the expression as a fallback filter for pending spawns (read-your-own-writes).
        // Pending spawns have no secondary index entries — they can't be found by the targeted scan.
        // This compiled predicate is evaluated via tx.Open() + TryRead() for pending spawn entities only.
        // Kept separate from _whereFilter to avoid re-evaluating committed entities that the index already filtered.
        var compiledPredicate = predicate.Compile();
        var prevPendingFilter = _pendingSpawnFieldFilter;
        _pendingSpawnFieldFilter = prevPendingFilter == null 
            ? (id, tx) =>
            {
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && compiledPredicate(value);
            } 
            : (id, tx) =>
            {
                if (!prevPendingFilter(id, tx))
                {
                    return false;
                }
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && compiledPredicate(value);
            };

        return this;
    }

    /// <summary>True if this query has Expression-based field predicates (enabling incremental views).</summary>
    internal readonly bool HasFieldPredicates => _fieldPredicateBranches != null;

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial predicates
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Filter by radius (sphere) around a center point. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereNearby<T>(double centerX, double centerY, double centerZ, double radius) where T : unmanaged
    {
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        Debug.Assert(_spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
        _spatialQueryType = SpatialQueryType.Radius;
        _spatialParams[0] = centerX; _spatialParams[1] = centerY; _spatialParams[2] = centerZ; _spatialParams[3] = radius;
        return this;
    }

    /// <summary>Filter by AABB overlap. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereInAABB<T>(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) where T : unmanaged
    {
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        Debug.Assert(_spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
        _spatialQueryType = SpatialQueryType.AABB;
        _spatialParams[0] = minX; _spatialParams[1] = minY; _spatialParams[2] = minZ;
        _spatialParams[3] = maxX; _spatialParams[4] = maxY; _spatialParams[5] = maxZ;
        return this;
    }

    /// <summary>Filter by ray intersection. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereRay<T>(double originX, double originY, double originZ, double dirX, double dirY, double dirZ, double maxDist)
        where T : unmanaged
    {
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        Debug.Assert(_spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
        _spatialQueryType = SpatialQueryType.Ray;
        _spatialParams[0] = originX; _spatialParams[1] = originY; _spatialParams[2] = originZ;
        _spatialParams[3] = dirX; _spatialParams[4] = dirY; _spatialParams[5] = dirZ; _spatialParams[6] = maxDist;
        return this;
    }

    /// <summary>
    /// Start a navigation (FK join) query from the source archetype to a target component type.
    /// The FK field selector identifies the long FK field on the source component.
    /// </summary>
    public readonly EcsNavigationQueryBuilder<TArchetype, TSource, TTarget> NavigateField<TSource, TTarget>(Expression<Func<TSource, long>> fkSelector)
        where TSource : unmanaged where TTarget : unmanaged
    {
        var fkFieldName = ExpressionParser.ExtractFieldName(fkSelector);
        return new EcsNavigationQueryBuilder<TArchetype, TSource, TTarget>(this, _tx, fkFieldName);
    }

    /// <summary>Test if an archetype ID matches the query mask. Used by EcsView to filter delta entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool MaskTestPublic(ushort archetypeId) => MaskTest(archetypeId);

    // ═══════════════════════════════════════════════════════════════════════
    // OrderBy / Skip / Take
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Order results by an indexed field. Requires <see cref="WhereField{T}"/> to identify the component.</summary>
    public EcsQuery<TArchetype> OrderByField<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        _orderBy = new OrderByField(ResolveOrderByFieldIndex(keySelector));
        return this;
    }

    /// <summary>Order results descending by an indexed field.</summary>
    public EcsQuery<TArchetype> OrderByFieldDescending<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        _orderBy = new OrderByField(ResolveOrderByFieldIndex(keySelector), descending: true);
        return this;
    }

    /// <summary>Skip the first <paramref name="count"/> results. Requires OrderBy.</summary>
    public EcsQuery<TArchetype> Skip(int count)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("Skip requires OrderByField.");
        }
        _skip = count;
        return this;
    }

    /// <summary>Take at most <paramref name="count"/> results. Requires OrderBy.</summary>
    public EcsQuery<TArchetype> Take(int count)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("Take requires OrderByField.");
        }
        _take = count;
        return this;
    }

    private int ResolveOrderByFieldIndex<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        if (_whereComponentTable == null)
        {
            throw new InvalidOperationException("OrderByField requires WhereField to be called first to identify the component table.");
        }
        var fieldName = ExpressionParser.ExtractFieldName(keySelector);
        if (!_whereComponentTable.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on component '{_whereComponentTable.Definition.Name}'.");
        }
        if (!field.HasIndex)
        {
            throw new InvalidOperationException($"Field '{fieldName}' must be indexed to use as OrderBy.");
        }
        return QueryResolverHelper.FindFieldIndex(_whereComponentTable.Definition, field);
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

    /// <summary>
    /// Create a persistent, refreshable View from this query.
    /// If Expression-based WHERE (WhereField) was used, creates an incremental view with ring buffer delta notifications.
    /// Otherwise, creates a pull-model view (full re-query on each Refresh).
    /// </summary>
    public EcsView<TArchetype> ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (HasFieldPredicates)
        {
            return ToIncrementalView(bufferCapacity);
        }

        // Pull mode: no field evaluators
        return ToPullView(bufferCapacity);
    }

    private EcsView<TArchetype> ToPullView(int bufferCapacity)
    {
        var initialSet = Execute();
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
        var firstTable = engineState.SlotToComponentTable[0];

        var view = new EcsView<TArchetype>(this, firstTable.DBE.MemoryAllocator, firstTable, bufferCapacity, _tx.TSN);

        // Populate initial entity set
        foreach (var id in initialSet)
        {
            view.AddEntityDirect((long)id.RawValue);
        }

        return view;
    }

    private EcsView<TArchetype> ToIncrementalView(int bufferCapacity)
    {
        var ct = _whereComponentTable;
        var branches = _fieldPredicateBranches;

        if (branches.Length > 1)
        {
            // OR path: create EcsOrView
            return ToOrView(ct, branches, bufferCapacity);
        }

        // Single AND branch
        var evaluators = QueryResolverHelper.ResolveEvaluators(branches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);

        var view = new EcsView<TArchetype>(this, evaluators, ct, _whereFieldReader, plan, bufferCapacity, _tx.TSN);

        // Register with ViewRegistry for delta notifications
        ct.ViewRegistry.RegisterView(view);

        // Initial population via PipelineExecutor (uses secondary index if plan selects one)
        _whereFieldReader.ExecuteFullScan(plan, plan.OrderedEvaluators, ct, _tx, view.EntityIdsInternal);

        // Process any deltas that arrived during population
        view.Refresh(_tx);
        view.ClearDelta();

        return view;
    }

    private EcsView<TArchetype> ToOrView(ComponentTable ct, FieldPredicate[][] branches, int bufferCapacity)
    {
        var branchEvaluators = new FieldEvaluator[branches.Length][];
        var plans = new ExecutionPlan[branches.Length];
        for (var b = 0; b < branches.Length; b++)
        {
            branchEvaluators[b] = QueryResolverHelper.ResolveEvaluators(branches[b], ct, 0, (byte)b);
            plans[b] = PlanBuilder.Instance.BuildPlan(branchEvaluators[b], ct, AdvancedSelectivityEstimator.Instance);
        }

        var view = new EcsView<TArchetype>(this, branchEvaluators, plans, ct, _whereFieldReader, bufferCapacity, _tx.TSN);
        ct.ViewRegistry.RegisterView(view);

        view.PopulateInitialOr(_tx);
        view.Refresh(_tx);
        view.ClearDelta();

        return view;
    }

    /// <summary>Rebind this query to a different transaction (different TSN → different visibility).</summary>
    internal void UpdateTransaction(Transaction tx) => _tx = tx;

    /// <summary>Execute the query and collect matching entity IDs into a HashSet.</summary>
    public HashSet<EntityId> Execute()
    {
        Activity activity = null;
        if (TelemetryConfig.EcsActive)
        {
            activity = TyphonActivitySource.StartActivity("ECS.Query.Execute");
            activity?.SetTag(TyphonSpanAttributes.EcsArchetype, typeof(TArchetype).Name);
        }

        var result = new HashSet<EntityId>();
        if (MaskIsEmpty)
        {
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, 0);
            activity?.Dispose();
            return result;
        }

        // Targeted scan via PipelineExecutor when field predicates are present
        if (HasFieldPredicates)
        {
            var targeted = ExecuteTargeted();
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, targeted.Count);
            activity?.SetTag(TyphonSpanAttributes.EcsQueryScanMode, "targeted");
            activity?.Dispose();
            return targeted;
        }

        // Spatial-driven scan: spatial index produces candidates, filtered by archetype mask + visibility
        if (_spatialQueryType != SpatialQueryType.None)
        {
            var spatial = ExecuteSpatial();
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, spatial.Count);
            activity?.SetTag(TyphonSpanAttributes.EcsQueryScanMode, "spatial");
            activity?.Dispose();
            return spatial;
        }

        CollectMatching((id, _) => result.Add(id));

        // T3 post-filter: evaluate WHERE predicate per entity via Transaction.Open
        var filter = _whereFilter;
        var tx = _tx;
        if (filter != null)
        {
            result.RemoveWhere(id => !filter(id, tx));
        }

        activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, result.Count);
        activity?.SetTag(TyphonSpanAttributes.EcsQueryScanMode, "broad");
        activity?.Dispose();

        return result;
    }

    /// <summary>Execute the query with ordering support. Requires <see cref="OrderByField{T,TKey}"/>.</summary>
    public List<EntityId> ExecuteOrdered()
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("ExecuteOrdered requires OrderByField.");
        }
        if (!HasFieldPredicates)
        {
            throw new InvalidOperationException("ExecuteOrdered requires WhereField to identify the component table.");
        }
        if (MaskIsEmpty)
        {
            return [];
        }

        var ct = _whereComponentTable;
        var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance, _orderBy.Value);

        // PipelineExecutor handles secondary index order; we post-filter by archetype mask
        var pkResult = new List<long>();
        _whereFieldReader.ExecuteOrderedScan(plan, plan.OrderedEvaluators, ct, _tx, pkResult);

        // Post-filter by archetype mask and convert to EntityId, applying Skip/Take
        var result = new List<EntityId>();
        int skipped = 0;
        int taken = 0;
        int take = _take > 0 ? _take : int.MaxValue;

        for (var i = 0; i < pkResult.Count; i++)
        {
            var entityId = EntityId.FromRaw(pkResult[i]);
            if (!MaskTest(entityId.ArchetypeId))
            {
                continue;
            }
            if (skipped < _skip)
            {
                skipped++;
                continue;
            }
            result.Add(entityId);
            taken++;
            if (taken >= take)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Execute targeted scan via PipelineExecutor with archetype mask post-filter.</summary>
    private HashSet<EntityId> ExecuteTargeted()
    {
        var ct = _whereComponentTable;

        var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);

        var pkResult = new HashMap<long>();
        _whereFieldReader.ExecuteFullScan(plan, plan.OrderedEvaluators, ct, _tx, pkResult);

        // Convert PKs to EntityIds — pre-size result to avoid rehashing
        var result = new HashSet<EntityId>(pkResult.Count);
        foreach (var pk in pkResult)
        {
            var entityId = EntityId.FromRaw(pk);
            if (MaskTest(entityId.ArchetypeId))
            {
                result.Add(entityId);
            }
        }

        // Read-your-own-writes: pending spawns have no secondary index entries, so the targeted scan above can't find them. Evaluate them via compiled
        // predicate fallback.
        CollectPendingSpawnsWithFieldFilter(result);

        // Opaque WHERE post-filter (from .Where<T>(Func), separate from WhereField)
        var filter = _whereFilter;
        if (filter != null)
        {
            var tx = _tx;
            result.RemoveWhere(id => !filter(id, tx));
        }

        return result;
    }

    /// <summary>
    /// Execute a spatial-driven query: spatial index produces candidate EntityIds, filtered by archetype mask, visibility, and WHERE.
    /// </summary>
    private HashSet<EntityId> ExecuteSpatial()
    {
        var state = _spatialTable.SpatialIndex;
        var result = new HashSet<EntityId>();
        var tx = _tx;

        // Fan out to both trees (SD1 guarantees no overlap). With per-component-type mode, only one is non-null.
        if (state.StaticTree != null)
        {
            QuerySingleTree(state.StaticTree, state, result);
        }
        if (state.DynamicTree != null)
        {
            QuerySingleTree(state.DynamicTree, state, result);
        }

        // Opaque WHERE post-filter
        var filter = _whereFilter;
        if (filter != null)
        {
            result.RemoveWhere(id => !filter(id, tx));
        }

        return result;
    }

    /// <summary>Query a single R-Tree and collect matching EntityIds into the result set.</summary>
    private void QuerySingleTree(SpatialRTree<PersistentStore> tree, SpatialIndexState state, HashSet<EntityId> result)
    {
        var tx = _tx;
        switch (_spatialQueryType)
        {
            case SpatialQueryType.AABB:
            {
                Span<double> coords = stackalloc double[6];
                for (int i = 0; i < 6; i++) coords[i] = _spatialParams[i];
                var coordSlice = coords[..state.Descriptor.CoordCount];

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryAABB(coordSlice))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTest(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
            case SpatialQueryType.Radius:
            {
                int halfCoord = state.Descriptor.CoordCount / 2;
                Span<double> center = stackalloc double[halfCoord];
                for (int i = 0; i < halfCoord; i++) center[i] = _spatialParams[i];

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryRadius(center, _spatialParams[3]))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTest(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
            case SpatialQueryType.Ray:
            {
                int halfCoord = state.Descriptor.CoordCount / 2;
                Span<double> origin = stackalloc double[halfCoord];
                Span<double> dir = stackalloc double[halfCoord];
                for (int i = 0; i < halfCoord; i++) { origin[i] = _spatialParams[i]; dir[i] = _spatialParams[3 + i]; }

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryRay(origin, dir, _spatialParams[6]))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTest(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
        }
    }

    /// <summary>Evaluate pending spawns against the compiled WhereField predicate.</summary>
    private void CollectPendingSpawnsWithFieldFilter(HashSet<EntityId> result)
    {
        var tx = _tx;
        var pendingFieldFilter = _pendingSpawnFieldFilter;
        if (pendingFieldFilter == null)
        {
            return;
        }

        var pending = tx.PendingSpawns;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var destroys = tx.PendingDestroys;
        for (int i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];
            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }
            if (!MaskTest(entry.Id.ArchetypeId))
            {
                continue;
            }
            if (pendingFieldFilter(entry.Id, tx))
            {
                result.Add(entry.Id);
            }
        }
    }

    /// <summary>Count matching entities.</summary>
    public int Count()
    {
        Activity activity = null;
        if (TelemetryConfig.EcsActive)
        {
            activity = TyphonActivitySource.StartActivity("ECS.Query.Count");
            activity?.SetTag(TyphonSpanAttributes.EcsArchetype, typeof(TArchetype).Name);
        }

        if (MaskIsEmpty)
        {
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, 0);
            activity?.Dispose();
            return 0;
        }

        // Targeted count via PipelineExecutor — avoids allocating result collections
        if (HasFieldPredicates)
        {
            var ct = _whereComponentTable;
            var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
            var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
            int targeted = _whereFieldReader.CountScan(plan, plan.OrderedEvaluators, ct, _tx);
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, targeted);
            activity?.SetTag(TyphonSpanAttributes.EcsQueryScanMode, "targeted");
            activity?.Dispose();
            return targeted;
        }

        // If WHERE filter, use Execute (which applies post-filter) then count
        if (_whereFilter != null)
        {
            int filtered = Execute().Count;
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, filtered);
            activity?.Dispose();
            return filtered;
        }

        int count = 0;
        CollectMatching((_, _) => count++);
        activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, count);
        activity?.SetTag(TyphonSpanAttributes.EcsQueryScanMode, "broad");
        activity?.Dispose();
        return count;
    }

    /// <summary>Test if any entity matches. Short-circuits on first match.</summary>
    public bool Any()
    {
        Activity activity = null;
        if (TelemetryConfig.EcsActive)
        {
            activity = TyphonActivitySource.StartActivity("ECS.Query.Any");
            activity?.SetTag(TyphonSpanAttributes.EcsArchetype, typeof(TArchetype).Name);
        }

        if (MaskIsEmpty)
        {
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, 0);
            activity?.Dispose();
            return false;
        }

        if (HasFieldPredicates)
        {
            var ct = _whereComponentTable;
            var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
            var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
            bool any = _whereFieldReader.CountScan(plan, plan.OrderedEvaluators, ct, _tx) > 0;
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, any ? 1 : 0);
            activity?.Dispose();
            return any;
        }

        if (_whereFilter != null)
        {
            bool any = Execute().Count > 0;
            activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, any ? 1 : 0);
            activity?.Dispose();
            return any;
        }

        bool found = false;
        CollectMatching((_, _) => found = true, stopOnFirst: true);
        activity?.SetTag(TyphonSpanAttributes.EcsQueryResultCount, found ? 1 : 0);
        activity?.Dispose();
        return found;
    }

    /// <summary>Get an enumerator for foreach support. Pre-collects matching entities then iterates.</summary>
    public EcsQueryEnumerator GetEnumerator()
    {
        var entities = new List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, EntityLocations Locations)>();
        if (!MaskIsEmpty)
        {
            CollectMatchingFull(entities);
        }
        return new EcsQueryEnumerator(_tx, entities, _whereFilter);
    }

    /// <summary>
    /// Core broad scan: iterate matching archetypes, then all entities in each LinearHash.
    /// Dispatches to the generic core once — the JIT fully specializes per TMask type.
    /// Also includes pending spawns for read-your-own-writes support.
    /// </summary>
    private void CollectMatching(Action<EntityId, ushort> onMatch, bool stopOnFirst = false)
    {
        if (_useLargeMask)
        {
            CollectMatchingCore(_maskLarge, onMatch, stopOnFirst);
            CollectPendingSpawns(_maskLarge, onMatch, stopOnFirst);
        }
        else
        {
            CollectMatchingCore(_mask256, onMatch, stopOnFirst);
            CollectPendingSpawns(_mask256, onMatch, stopOnFirst);
        }
    }

    /// <summary>Collect full entity data for foreach enumeration. Dispatches to generic core.</summary>
    private void CollectMatchingFull(List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results)
    {
        if (_useLargeMask)
        {
            CollectMatchingFullCore(_maskLarge, results);
            CollectPendingSpawnsFull(_maskLarge, results);
        }
        else
        {
            CollectMatchingFullCore(_mask256, results);
            CollectPendingSpawnsFull(_mask256, results);
        }
    }

    /// <summary>
    /// Scan the transaction's pending spawns for entities matching the query (read-your-own-writes).
    /// Pending spawns are not yet in the EntityMap — without this, Query().Execute() would miss them.
    /// </summary>
    private void CollectPendingSpawns<TMask>(TMask mask, Action<EntityId, ushort> onMatch, bool stopOnFirst) where TMask : struct, IArchetypeMask<TMask>
    {
        var pending = _tx.PendingSpawns;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var destroys = _tx.PendingDestroys;
        var enableDisable = _tx.PendingEnableDisable;
        bool hasT2 = HasT2;

        for (int i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];

            // Skip if pending destroy
            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }

            // T1: archetype mask
            if (!mask.Test(entry.Id.ArchetypeId))
            {
                continue;
            }

            // Resolve EnabledBits (may have been overridden by Enable/Disable in same tx)
            ushort enabledBits = entry.EnabledBits;
            if (enableDisable != null && enableDisable.TryGetValue(entry.Id, out ushort overrideBits))
            {
                enabledBits = overrideBits;
            }

            // T2: check enabled/disabled constraints
            if (hasT2)
            {
                var meta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
                if (meta == null || !ResolveT2Masks(meta, out ushort reqEnabled, out ushort reqDisabled))
                {
                    continue;
                }
                if ((enabledBits & reqEnabled) != reqEnabled)
                {
                    continue;
                }
                if ((enabledBits & reqDisabled) != 0)
                {
                    continue;
                }
            }

            onMatch(entry.Id, enabledBits);

            if (stopOnFirst)
            {
                return;
            }
        }
    }

    /// <summary>Pending spawn collection for foreach enumeration (includes EntityLocations).</summary>
    private void CollectPendingSpawnsFull<TMask>(TMask mask, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results) where TMask : struct, IArchetypeMask<TMask>
    {
        var pending = _tx.PendingSpawns;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var destroys = _tx.PendingDestroys;
        var enableDisable = _tx.PendingEnableDisable;
        bool hasT2 = HasT2;

        for (int i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];

            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }

            if (!mask.Test(entry.Id.ArchetypeId))
            {
                continue;
            }

            ushort enabledBits = entry.EnabledBits;
            if (enableDisable != null && enableDisable.TryGetValue(entry.Id, out ushort overrideBits))
            {
                enabledBits = overrideBits;
            }

            var meta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
            if (meta == null)
            {
                continue;
            }

            if (hasT2)
            {
                if (!ResolveT2Masks(meta, out ushort reqEnabled, out ushort reqDisabled))
                {
                    continue;
                }
                if ((enabledBits & reqEnabled) != reqEnabled)
                {
                    continue;
                }
                if ((enabledBits & reqDisabled) != 0)
                {
                    continue;
                }
            }

            // Copy locations from SpawnEntry into EntityLocations
            var locs = new EntityLocations();
            for (int s = 0; s < meta.ComponentCount; s++)
            {
                locs.Values[s] = entry.Loc[s];
            }

            results.Add((entry.Id, meta, enabledBits, locs));
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
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
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
    private void CollectMatchingFullCore<TMask>(TMask mask, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results) where TMask : struct, IArchetypeMask<TMask>
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
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref action);
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Broad scan action structs (JIT-specialized callbacks for ForEachEntry)
    // ═══════════════════════════════════════════════════════════════════════

    private struct BroadScanAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
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
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

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

            var entityId = new EntityId(key, Meta.ArchetypeId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            ushort bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out ushort pendingBits))
            {
                bits = pendingBits;
            }

            // T2 check
            if (HasT2)
            {
                if ((bits & RequiredEnabled) != RequiredEnabled)
                {
                    return true;
                }
                if ((bits & RequiredDisabled) != 0)
                {
                    return true;
                }
            }

            OnMatch(entityId, bits);

            if (StopOnFirst)
            {
                Found = true;
                return false; // Stop iteration
            }
            return true;
        }
    }

    private struct BroadScanCollectAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ArchetypeMetadata Meta;
        public long TxTsn;
        public EnabledBitsOverrides EnabledBitsOverrides;
        public bool HasT2;
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> Results;
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

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

            var entityId = new EntityId(key, Meta.ArchetypeId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            ushort bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out ushort pendingBits))
            {
                bits = pendingBits;
            }

            if (HasT2)
            {
                if ((bits & RequiredEnabled) != RequiredEnabled)
                {
                    return true;
                }
                if ((bits & RequiredDisabled) != 0)
                {
                    return true;
                }
            }

            // Copy component locations inline — no heap allocation.
            // For cluster archetypes, locations are meaningless (record has ClusterChunkId+SlotIndex, not per-component ChunkIds).
            // Store a zeroed EntityLocations — the enumerator will resolve via Transaction.Open for cluster archetypes.
            var locs = new EntityLocations();
            if (!Meta.IsClusterEligible)
            {
                EntityRecordAccessor.CopyLocationsTo(value, ref locs, Meta.ComponentCount);
            }

            Results.Add((entityId, Meta, bits, locs));
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumerator (iterates pre-collected results)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Iterates pre-collected query results, yielding read-only EntityRefs with zero-copy component access.
    /// Entities returned by query enumeration are opened as read-only — use <see cref="Transaction.OpenMut"/> for writes.
    /// </summary>
    [PublicAPI]
    public ref struct EcsQueryEnumerator
    {
        private readonly Transaction _tx;
        private readonly List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, EntityLocations Locations)> _entities;
        private readonly Func<EntityId, Transaction, bool> _whereFilter;
        private int _index;
        private EntityRef _current;

        internal EcsQueryEnumerator(Transaction tx, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> entities, Func<EntityId, Transaction, bool> whereFilter)
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

                var (id, meta, enabledBits, locations) = _entities[_index];

                // T3 post-filter: evaluate WHERE via Transaction.Open
                if (_whereFilter != null && !_whereFilter(id, _tx))
                {
                    continue;
                }

                if (meta.IsClusterEligible)
                {
                    // Cluster archetype: resolve via Transaction.Open which handles cluster path correctly
                    _current = _tx.Open(id);
                }
                else
                {
                    var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
                    _current = new EntityRef(id, meta, engineState, _tx, enabledBits, false);
                    _current.CopyLocationsFrom(in locations, meta.ComponentCount);
                }
                return true;
            }
        }

        public void Dispose() { }
    }
}
#pragma warning restore TYPHON005
