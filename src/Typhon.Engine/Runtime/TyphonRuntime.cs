using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Top-level runtime for Typhon game servers. Wraps <see cref="DatabaseEngine"/> and <see cref="DagScheduler"/>, managing the per-tick UoW/Transaction
/// lifecycle so game developers never handle commits manually.
/// </summary>
/// <remarks>
/// <para>
/// Each tick: creates a UoW (Deferred) → for each CallbackSystem/QuerySystem, creates a Transaction on the executing worker
/// thread (respecting thread affinity) → commits after each system → flushes UoW at tick end.
/// </para>
/// <para>
/// Pipeline systems do not receive Transactions — their entity access goes through Gather/Scatter pipelines.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class TyphonRuntime : IDisposable
{
    private readonly RuntimeOptions _options;
    private readonly ILogger _logger;

    // Tick-level UoW (created at tick start, disposed at tick end)
    private UnitOfWork _currentUow;

    // Per-system transaction tracking. Only one worker processes a given system index at a time (CAS on _isReady ensures single claimer), so no contention
    // on these slots.
    private readonly Transaction[] _systemTransactions;

    private readonly ViewBase[] _systemViews;                      // Resolved View per system (null if no input)
    private readonly ComponentTable[][] _systemChangeFilterTables; // ComponentTables for changeFilter types (null if no filter)
    private readonly ArchetypeClusterState[] _systemClusterStates; // Cluster state for single-archetype cluster-eligible systems (null if not applicable)
    private readonly PooledEntityList[] _systemEntityLists;        // For returning ArrayPool buffers
    private readonly EventQueueBase[][] _systemConsumedQueues;     // Pre-allocated consumed queue refs per system (null if none)
    private readonly PooledEntityList[] _parallelEntityLists;      // Full entity set for parallel QuerySystem chunk slicing
    private readonly HashMap<long>[] _multiTableFilterSets;        // Cached dedup sets for multi-table BuildFilteredEntitySet (avoids per-tick alloc)
    private readonly PointInTimeAccessor[] _parallelAccessors;      // Per-system reusable PTAs — Attach()ed each tick (per-system to avoid race with DAG-concurrent systems)
    private readonly PartitionEntityView[][] _partitionViews;      // Per-system per-worker partition views [sysIdx][chunkIdx]

    // Cached delegate — avoids per-TickContext allocation from method group conversion
    private readonly Func<DurabilityMode, Transaction> _createSideTxDelegate;

    // First-tick flag
    private bool _firstTickExecuted;

    // DeltaTime tracking
    private long _previousTickTimestamp;
    private float _currentDeltaTime;

    // ═══════════════════════════════════════════════════════════════
    // Subscription server
    // ═══════════════════════════════════════════════════════════════

    private readonly PublishedViewRegistry _publishedViewRegistry = new();
    private readonly ClientConnectionManager _clientConnectionManager = new();
    private SubscriptionOutputPhase _subscriptionOutputPhase;
    private TcpSubscriptionServer _tcpServer;

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle events
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired once on the first tick. Use to rebuild transient state after crash recovery. The callback receives a valid Transaction for entity operations.
    /// </summary>
    public event Action<TickContext> OnFirstTick;

    /// <summary>
    /// Fired during <see cref="Shutdown"/>. Use for cleanup (save player state, etc.). The callback receives a dedicated Transaction (Immediate durability).
    /// </summary>
    public event Action<TickContext> OnShutdown;

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The underlying database engine.</summary>
    public DatabaseEngine Engine { get; }

    /// <summary>The DAG scheduler driving tick execution.</summary>
    public DagScheduler Scheduler { get; }

    /// <summary>Telemetry ring buffer for diagnostic inspection.</summary>
    public TickTelemetryRing Telemetry => Scheduler.Telemetry;

    /// <summary>Number of ticks executed so far.</summary>
    public long CurrentTickNumber => Scheduler.CurrentTickNumber;

    /// <summary>Current overload response level.</summary>
    public OverloadLevel CurrentOverloadLevel => Scheduler.CurrentOverloadLevel;

    /// <summary>Static DAG system definitions (name, type, priority, dependencies).</summary>
    public SystemDefinition[] Systems => Scheduler.Systems;

    /// <summary>Fires when overload reaches <see cref="OverloadLevel.PlayerShedding"/>. Game code decides what to do (migrate, disconnect, split).</summary>
    public event Action<TyphonRuntime> OnCriticalOverload;

    // ═══════════════════════════════════════════════════════════════
    // Factory
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new TyphonRuntime from a DatabaseEngine and a schedule configuration.
    /// </summary>
    /// <param name="engine">The database engine for entity storage.</param>
    /// <param name="configure">Action to register systems on the <see cref="RuntimeSchedule"/>.</param>
    /// <param name="options">Runtime options. If null, defaults are used.</param>
    /// <param name="parent">Parent resource node. If null, uses the registry's Runtime node.</param>
    /// <param name="logger">Optional logger.</param>
    public static TyphonRuntime Create(DatabaseEngine engine, Action<RuntimeSchedule> configure, RuntimeOptions options = null, IResource parent = null,
        ILogger logger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = options ?? new RuntimeOptions();
        var schedule = RuntimeSchedule.Create(opts);
        configure(schedule);

        var resourceParent = parent ?? engine.Parent; // DatabaseEngine registers under DataEngine node
        var scheduler = schedule.Build(resourceParent, logger);

        return new TyphonRuntime(engine, scheduler, opts, logger);
    }

    private TyphonRuntime(DatabaseEngine engine, DagScheduler scheduler, RuntimeOptions options, ILogger logger)
    {
        Engine = engine;
        Scheduler = scheduler;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _systemTransactions = new Transaction[scheduler.SystemCount];
        _systemViews = new ViewBase[scheduler.SystemCount];
        _systemChangeFilterTables = new ComponentTable[scheduler.SystemCount][];
        _systemClusterStates = new ArchetypeClusterState[scheduler.SystemCount];
        _systemEntityLists = new PooledEntityList[scheduler.SystemCount];
        _systemConsumedQueues = new EventQueueBase[scheduler.SystemCount][];
        _parallelEntityLists = new PooledEntityList[scheduler.SystemCount];
        _multiTableFilterSets = new HashMap<long>[scheduler.SystemCount];
        _parallelAccessors = new PointInTimeAccessor[scheduler.SystemCount];
        _partitionViews = new PartitionEntityView[scheduler.SystemCount][];
        _createSideTxDelegate = CreateSideTransactionInternal;

        ResolveChangeFilters(scheduler);

        // Initialize subscription infrastructure
        var subOptions = options.SubscriptionServer ?? new SubscriptionServerOptions();
        _subscriptionOutputPhase = new SubscriptionOutputPhase(engine, _publishedViewRegistry, _clientConnectionManager, subOptions, logger);

        // Wire tick lifecycle hooks
        Scheduler.TickStartCallback = OnTickStartInternal;
        Scheduler.TickEndCallback = OnTickEndInternal;
        Scheduler.SystemStartCallback = OnSystemStartInternal;
        Scheduler.SystemEndCallback = OnSystemEndInternal;
        Scheduler.ParallelQueryPrepareCallback = OnParallelQueryPrepare;
        Scheduler.ParallelQueryChunkCallback = OnParallelQueryChunk;
        Scheduler.ParallelQueryCleanupCallback = OnParallelQueryCleanup;

        // Wire subscription telemetry enrichment
        Scheduler.TelemetryEnrichCallback = (ref t) =>
        {
            if (_subscriptionOutputPhase != null)
            {
                t.OutputPhaseMs = _subscriptionOutputPhase.LastOutputPhaseMs;
                t.SubscriptionDeltasPushed = _subscriptionOutputPhase.LastDeltasPushed;
                t.SubscriptionOverflowCount = _subscriptionOutputPhase.LastOverflowCount;
            }
        };

        Scheduler.OnCriticalOverloadCallback = () => OnCriticalOverload?.Invoke(this);
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Starts the scheduler (worker threads + tick driver) and the subscription server (if configured).</summary>
    public void Start()
    {
        Scheduler.Start();

        // Start TCP subscription server if a port is configured
        var subOptions = _options.SubscriptionServer;
        if (subOptions != null && subOptions.Port > 0)
        {
            _tcpServer = new TcpSubscriptionServer(subOptions, _clientConnectionManager, _subscriptionOutputPhase, _logger);
            _tcpServer.Start();
        }
    }

    /// <summary>
    /// Gracefully shuts down the runtime. Stops the subscription server, fires <see cref="OnShutdown"/>, then stops the scheduler.
    /// </summary>
    public void Shutdown()
    {
        // Stop accepting new connections and flush remaining data
        _tcpServer?.Shutdown();

        // Execute OnShutdown callback with a dedicated transaction
        if (OnShutdown != null)
        {
            using var tx = Engine.CreateQuickTransaction(DurabilityMode.Immediate);
            var ctx = new TickContext
            {
                TickNumber = Scheduler.CurrentTickNumber,
                DeltaTime = 0f,
                Transaction = tx,
                CreateSideTransaction = _createSideTxDelegate,
                Entities = PooledEntityList.Empty
            };
            OnShutdown.Invoke(ctx);
            tx.Commit();
        }

        Scheduler.Shutdown();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tcpServer?.Dispose();
        Scheduler.Dispose();

        // Dispose per-system PTAs AFTER scheduler — workers must be fully stopped
        // before we flush their per-thread EntityAccessors' ChangeSets.
        for (var i = 0; i < _parallelAccessors.Length; i++)
        {
            _parallelAccessors[i]?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Subscription API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Publish a shared View for client subscriptions. All subscribers see the same data; the delta is serialized once and memcpy'd.
    /// </summary>
    /// <remarks>
    /// <para>The View must be a dedicated instance — it must NOT be used as a system input. Published Views are refreshed only during
    /// the Output phase; using the same View as system input would consume ring buffer entries needed by subscriptions.</para>
    /// </remarks>
    /// <param name="name">Human-readable name clients use to identify this subscription.</param>
    /// <param name="view">A dedicated ViewBase instance for subscriptions.</param>
    /// <param name="priority">Subscription priority for overload throttling.</param>
    /// <returns>The published View handle.</returns>
    public PublishedView PublishView(string name, ViewBase view, SubscriptionPriority priority = SubscriptionPriority.Normal) =>
        _publishedViewRegistry.RegisterShared(name, view, priority);

    /// <summary>
    /// Publish a per-client View factory. A new View is created per subscriber, parameterized by <see cref="ClientContext"/>.
    /// </summary>
    /// <param name="name">Human-readable name clients use to identify this subscription.</param>
    /// <param name="factory">Factory that creates a View for each subscribing client.</param>
    /// <param name="priority">Subscription priority for overload throttling.</param>
    /// <returns>The published View handle.</returns>
    public PublishedView PublishView(string name, Func<ClientContext, ViewBase> factory, SubscriptionPriority priority = SubscriptionPriority.Normal) =>
        _publishedViewRegistry.RegisterPerClient(name, factory, priority);

    /// <summary>
    /// Set a client's subscription set. Replaces the previous set atomically. The transition is applied during the next tick's Output phase.
    /// </summary>
    /// <remarks>If called multiple times within a tick, the last call wins.</remarks>
    public void SetSubscriptions(ClientConnection client, params PublishedView[] views) => client.SetSubscriptions(views);

    /// <summary>The published View registry (for diagnostics and testing).</summary>
    public PublishedViewRegistry PublishedViews => _publishedViewRegistry;

    /// <summary>The client connection manager (for diagnostics and testing).</summary>
    internal ClientConnectionManager ClientConnections => _clientConnectionManager;

    // ═══════════════════════════════════════════════════════════════
    // Side-transaction factory
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a side-transaction with the specified durability mode.
    /// The caller owns the returned Transaction and must Commit + Dispose it.
    /// Side-transactions are NOT visible to the current tick's main Transactions (snapshot isolation).
    /// </summary>
    public Transaction CreateSideTransaction(DurabilityMode durability = DurabilityMode.Immediate) => Engine.CreateQuickTransaction(durability);

    private Transaction CreateSideTransactionInternal(DurabilityMode durability) => Engine.CreateQuickTransaction(durability);

    // ═══════════════════════════════════════════════════════════════
    // #197: Change filter resolution
    // ═══════════════════════════════════════════════════════════════

    private void ResolveChangeFilters(DagScheduler scheduler)
    {
        for (var i = 0; i < scheduler.SystemCount; i++)
        {
            var sys = scheduler.Systems[i];

            // Resolve input View
            if (sys.InputFactory != null)
            {
                _systemViews[i] = sys.InputFactory();
                if (_systemViews[i] == null)
                {
                    throw new InvalidOperationException($"System '{sys.Name}': InputFactory returned null. The View must be created before the runtime starts.");
                }

                if (_systemViews[i].IsPublished)
                {
                    throw new InvalidOperationException(
                        $"System '{sys.Name}': Input View (ViewId={_systemViews[i].ViewId}) is already published for subscriptions. " +
                        "Published Views must be separate instances from system input Views. Create a new View with the same query.");
                }

                _systemViews[i].IsSystemInput = true;

                // Detect cluster-eligible archetype for parallel cluster dispatch.
                // Checks if any entity already in the view belongs to a cluster-eligible archetype.
                if (sys.IsParallelQuery && Engine != null)
                {
                    foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
                    {
                        if (meta.IsClusterEligible && meta.ArchetypeId < Engine._archetypeStates.Length)
                        {
                            var es = Engine._archetypeStates[meta.ArchetypeId];
                            if (es?.ClusterState is { ActiveClusterCount: > 0 })
                            {
                                _systemClusterStates[i] = es.ClusterState;
                                break;
                            }
                        }
                    }
                }
            }

            // Resolve changeFilter component types → ComponentTable references
            if (sys.ChangeFilterTypes is { Length: > 0 })
            {
                var tables = new ComponentTable[sys.ChangeFilterTypes.Length];
                for (var j = 0; j < sys.ChangeFilterTypes.Length; j++)
                {
                    var ct = Engine.GetComponentTable(sys.ChangeFilterTypes[j]);
                    if (ct == null)
                    {
                        throw new InvalidOperationException(
                            $"System '{sys.Name}': ChangeFilter type '{sys.ChangeFilterTypes[j].Name}' is not a registered component type.");
                    }

                    if (ct.StorageMode == Schema.Definition.StorageMode.Versioned)
                    {
                        throw new InvalidOperationException(
                            $"System '{sys.Name}': ChangeFilter type '{sys.ChangeFilterTypes[j].Name}' uses Versioned storage mode, " +
                            "which does not support dirty tracking. ChangeFilter requires SingleVersion or Transient storage.");
                    }

                    tables[j] = ct;
                }

                _systemChangeFilterTables[i] = tables;

                // Build ReactiveSkip closure: returns true when no dirty entities exist for this system's change filter.
                // Uses PreviousTickHadDirtyEntities (reliable, works regardless of EntityPK overhead).
                var filterTables = tables;
                sys.ReactiveSkip = () =>
                {
                    for (var t = 0; t < filterTables.Length; t++)
                    {
                        if (filterTables[t].PreviousTickHadDirtyEntities)
                        {
                            return false; // Dirty entities exist — don't skip
                        }
                    }

                    return true; // No dirty entities — skip
                };
            }

            // Pre-allocate consumed queue refs (zero allocation per tick)
            if (sys.ConsumesQueueIndices is { Length: > 0 })
            {
                var consumed = new EventQueueBase[sys.ConsumesQueueIndices.Length];
                for (var j = 0; j < sys.ConsumesQueueIndices.Length; j++)
                {
                    consumed[j] = scheduler.GetEventQueue(sys.ConsumesQueueIndices[j]);
                }

                _systemConsumedQueues[i] = consumed;

                // Extend ReactiveSkip: don't skip if any consumed queue has events
                var originalSkip = sys.ReactiveSkip;
                var queueRefs = consumed;
                sys.ReactiveSkip = () =>
                {
                    for (var q = 0; q < queueRefs.Length; q++)
                    {
                        if (!queueRefs[q].IsEmpty)
                        {
                            return false; // Events pending — don't skip
                        }
                    }

                    // No events — fall through to original skip check (dirty entities) or default skip
                    return originalSkip == null || originalSkip();
                };
            }
        }
    }

    /// <summary>
    /// Build the filtered entity set for a system with change filter.
    /// Iterates the raw dirty bitmap from the previous tick, reads entity PKs from chunk offset 0, and intersects with the View's entity set. OR logic
    /// across multiple changeFilter tables.
    /// Falls back to full View when PK resolution is unavailable (first tick, or SV without indexed fields).
    /// </summary>
    private PooledEntityList BuildFilteredEntitySet(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        var filterTables = _systemChangeFilterTables[sysIdx];

        // Single-table fast path: skip intermediate collection, write directly to result array.
        // Most systems filter on a single component type — this eliminates HashSet allocation + copy.
        if (filterTables.Length == 1)
        {
            return BuildFilteredSingleTable(sysIdx, view, filterTables[0]);
        }

        // Multi-table path: deduplicate across tables using cached HashMap<long> (zero alloc after first tick)
        var dirtyInView = _multiTableFilterSets[sysIdx] ??= new HashMap<long>();
        dirtyInView.Clear();

        for (var t = 0; t < filterTables.Length; t++)
        {
            if (!ScanDirtyBitmapIntoSet(sysIdx, view, filterTables[t], dirtyInView, out var fallback))
            {
                return fallback;
            }
        }

        if (dirtyInView.Count == 0)
        {
            return PooledEntityList.Empty;
        }

        var list = PooledEntityList.Rent(dirtyInView.Count);
        var span = list.AsSpan();
        var idx = 0;
        foreach (var pk in dirtyInView)
        {
            span[idx++] = EntityId.FromRaw(pk);
        }

        return list;
    }

    /// <summary>
    /// Single-table fast path: scan dirty bitmap → View intersection → result array.
    /// No intermediate collection, no dedup (only one table → no duplicates possible).
    /// Includes cluster entity scanning (Phase 4a): reads PreviousTickDirtySnapshot from each cluster archetype that references this table.
    /// </summary>
    private unsafe PooledEntityList BuildFilteredSingleTable(int sysIdx, ViewBase view, ComponentTable table)
    {
        if (!table.PreviousTickHadDirtyEntities)
        {
            return PooledEntityList.Empty;
        }

        var bitmap = table.PreviousTickDirtyBitmap;
        if (bitmap == null || table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
        {
            return BuildFullViewEntitySet(sysIdx);
        }

        // Estimate upper bound from non-cluster bitmap + cluster dirty snapshots
        int bitmapPopCount = 0;
        for (int i = 0; i < bitmap.Length; i++)
        {
            bitmapPopCount += BitOperations.PopCount((ulong)bitmap[i]);
        }

        // Upper bound: view.Count (dirty ∩ view can't exceed view size). Avoids separate cluster estimate scan.
        var list = PooledEntityList.Rent(view.Count);
        var span = list.AsSpan();
        int count = 0;

        // Non-cluster path: scan ComponentTable dirty bitmap
        if (bitmap.Length > 0)
        {
            var accessor = table.ComponentSegment.CreateChunkAccessor();
            try
            {
                for (var wordIdx = 0; wordIdx < bitmap.Length; wordIdx++)
                {
                    var word = bitmap[wordIdx];
                    while (word != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount((ulong)word);
                        var chunkId = wordIdx * 64 + bit;
                        word &= word - 1;

                        if (table.IsChunkDestroyed(chunkId))
                        {
                            continue;
                        }

                        var entityPK = *(long*)accessor.GetChunkAddress(chunkId);
                        if (view.Contains(entityPK))
                        {
                            span[count++] = EntityId.FromRaw(entityPK);
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        // Cluster path (Phase 4a): scan cluster dirty bitmaps for archetypes referencing this table
        count = ScanClusterDirtyEntities(table, view, span, count);

        if (count == 0)
        {
            list.Return();
            return PooledEntityList.Empty;
        }

        return new PooledEntityList(list.BackingArray, count);
    }

    /// <summary>
    /// Scan cluster dirty bitmaps for all archetypes referencing the given table, adding matching entities to the result span.
    /// Uses direct array loop over <see cref="ArchetypeRegistry"/> (no yield-return allocation).
    /// Returns the updated count.
    /// </summary>
    private unsafe int ScanClusterDirtyEntities(ComponentTable table, ViewBase view, Span<EntityId> span, int count)
    {
        int maxArchId = Math.Min(ArchetypeRegistry.MaxArchetypeId, Engine._archetypeStates.Length - 1);
        for (int archId = 0; archId <= maxArchId; archId++)
        {
            var es = Engine._archetypeStates[archId];
            var cs = es?.ClusterState;
            if (cs?.PreviousTickDirtySnapshot == null)
            {
                continue;
            }

            if (!ArchetypeReferencesTable(es, table))
            {
                continue;
            }

            var snapshot = cs.PreviousTickDirtySnapshot;
            var clusterAccessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                for (int wordIdx = 0; wordIdx < snapshot.Length; wordIdx++)
                {
                    long word = snapshot[wordIdx];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount((ulong)word);
                        word &= word - 1;

                        byte* clusterBase = clusterAccessor.GetChunkAddress(wordIdx);
                        long entityPK = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + bit * 8);
                        if (view.Contains(entityPK))
                        {
                            span[count++] = EntityId.FromRaw(entityPK);
                        }
                    }
                }
            }
            finally
            {
                clusterAccessor.Dispose();
            }
        }

        return count;
    }

    /// <summary>
    /// Scan a single table's dirty bitmap and add matching entities to the dedup set.
    /// Returns false if a fallback is needed (first tick, no indexed fields).
    /// </summary>
    private unsafe bool ScanDirtyBitmapIntoSet(int sysIdx, ViewBase view, ComponentTable table, HashMap<long> dirtyInView, out PooledEntityList fallback)
    {
        fallback = default;

        if (!table.PreviousTickHadDirtyEntities)
        {
            return true;
        }

        var bitmap = table.PreviousTickDirtyBitmap;
        if (bitmap == null || table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
        {
            fallback = BuildFullViewEntitySet(sysIdx);
            return false;
        }

        // Non-cluster path
        var accessor = table.ComponentSegment.CreateChunkAccessor();
        try
        {
            for (var wordIdx = 0; wordIdx < bitmap.Length; wordIdx++)
            {
                var word = bitmap[wordIdx];
                while (word != 0)
                {
                    var bit = BitOperations.TrailingZeroCount((ulong)word);
                    var chunkId = wordIdx * 64 + bit;
                    word &= word - 1;

                    if (table.IsChunkDestroyed(chunkId))
                    {
                        continue;
                    }

                    var entityPK = *(long*)accessor.GetChunkAddress(chunkId);
                    if (view.Contains(entityPK))
                    {
                        dirtyInView.TryAdd(entityPK);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        // Cluster path (Phase 4a): scan cluster dirty bitmaps for archetypes referencing this table
        ScanClusterDirtyEntitiesIntoSet(table, view, dirtyInView);

        return true;
    }

    /// <summary>
    /// Scan cluster dirty bitmaps for all archetypes referencing the given table, adding matching entities to the dedup set.
    /// Multi-table variant that adds to HashMap instead of Span.
    /// </summary>
    private unsafe void ScanClusterDirtyEntitiesIntoSet(ComponentTable table, ViewBase view, HashMap<long> dirtyInView)
    {
        int maxArchId = Math.Min(ArchetypeRegistry.MaxArchetypeId, Engine._archetypeStates.Length - 1);
        for (int archId = 0; archId <= maxArchId; archId++)
        {
            var es = Engine._archetypeStates[archId];
            var cs = es?.ClusterState;
            if (cs?.PreviousTickDirtySnapshot == null)
            {
                continue;
            }

            if (!ArchetypeReferencesTable(es, table))
            {
                continue;
            }

            var snapshot = cs.PreviousTickDirtySnapshot;
            var clusterAccessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                for (int wordIdx = 0; wordIdx < snapshot.Length; wordIdx++)
                {
                    long word = snapshot[wordIdx];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount((ulong)word);
                        word &= word - 1;

                        byte* clusterBase = clusterAccessor.GetChunkAddress(wordIdx);
                        long entityPK = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + bit * 8);
                        if (view.Contains(entityPK))
                        {
                            dirtyInView.TryAdd(entityPK);
                        }
                    }
                }
            }
            finally
            {
                clusterAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Check whether an archetype's component slots include the given ComponentTable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ArchetypeReferencesTable(ArchetypeEngineState es, ComponentTable table)
    {
        for (int slot = 0; slot < es.SlotToComponentTable.Length; slot++)
        {
            if (es.SlotToComponentTable[slot] == table)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build entity set from full View (no change filter — all entities).
    /// </summary>
    private PooledEntityList BuildFullViewEntitySet(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        if (view.Count == 0)
        {
            return PooledEntityList.Empty;
        }

        var list = PooledEntityList.Rent(view.Count);
        var span = list.AsSpan();
        var idx = 0;
        foreach (var pk in view)
        {
            span[idx++] = EntityId.FromRaw(pk);
        }

        return list;
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel QuerySystem callbacks (called by DagScheduler)
    //
    // Four dispatch paths based on (WritesVersioned × HasChangeFilter):
    //   Path 1: Full, Non-Versioned  — O(1) prepare, PTA + PartitionEntityView (zero-copy)
    //   Path 2: Filtered, Non-Versioned — O(dirty) prepare, PTA + PooledEntitySlice
    //   Path 3: Full, Versioned (fallback) — O(N) prepare, per-chunk Transaction
    //   Path 4: Filtered, Versioned (fallback) — O(dirty) prepare, per-chunk Transaction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Prepare phase: selects the dispatch path based on WritesVersioned and change filter presence.
    /// For non-Versioned systems, creates/advances a long-lived PointInTimeAccessor.
    /// For the full non-Versioned path (Path 1), NO entity list is materialized — O(1).
    /// </summary>
    private int OnParallelQueryPrepare(int sysIdx)
    {
        var sys = Scheduler.Systems[sysIdx];
        var hasView = _systemViews[sysIdx] != null;
        var hasChangeFilter = hasView && _systemChangeFilterTables[sysIdx] != null;

        if (sys.WritesVersioned)
        {
            // Paths 3 & 4: Versioned fallback — materialize entity list, per-chunk Transactions
            return PrepareVersionedFallback(sysIdx, hasChangeFilter);
        }

        // Paths 1 & 2: Non-Versioned — use PointInTimeAccessor
        if (hasChangeFilter)
        {
            return PrepareFilteredNonVersioned(sysIdx);
        }

        return PrepareFullNonVersioned(sysIdx);
    }

    /// <summary>Lazy-init per-system PTA and PartitionEntityViews on first use. Zero-alloc on subsequent ticks.</summary>
    private void EnsureParallelResources(int sysIdx)
    {
        if (_parallelAccessors[sysIdx] == null)
        {
            _parallelAccessors[sysIdx] = new PointInTimeAccessor();
            _partitionViews[sysIdx] = new PartitionEntityView[Scheduler.WorkerCount];
            for (var w = 0; w < Scheduler.WorkerCount; w++)
            {
                _partitionViews[sysIdx][w] = new PartitionEntityView();
            }
        }

        _parallelAccessors[sysIdx].Attach(Engine, Scheduler.WorkerCount);
    }

    /// <summary>Path 1: Full View, Non-Versioned. O(1) prepare — no entity list materialization.</summary>
    private int PrepareFullNonVersioned(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        if (view == null || view.Count == 0)
        {
            return 0;
        }

        EnsureParallelResources(sysIdx);

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        metrics.EntitiesProcessed = view.Count;

        return ComputeChunkCount(view.Count);
    }

    /// <summary>Path 2: Change-filtered, Non-Versioned. O(dirty) prepare — materialize dirty list, use PTA for access.</summary>
    private int PrepareFilteredNonVersioned(int sysIdx)
    {
        var entityList = BuildFilteredEntitySet(sysIdx);
        _parallelEntityLists[sysIdx] = entityList;

        EnsureParallelResources(sysIdx);

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        metrics.EntitiesProcessed = entityList.Count;
        if (_systemViews[sysIdx] != null)
        {
            metrics.EntitiesSkippedByChangeFilter = _systemViews[sysIdx].Count - entityList.Count;
        }

        if (entityList.Count == 0)
        {
            return 0;
        }

        return ComputeChunkCount(entityList.Count);
    }

    /// <summary>Paths 3 & 4: Versioned fallback — materialize entity list (same as original path).</summary>
    private int PrepareVersionedFallback(int sysIdx, bool hasChangeFilter)
    {
        PooledEntityList entityList;
        if (hasChangeFilter)
        {
            entityList = BuildFilteredEntitySet(sysIdx);
        }
        else if (_systemViews[sysIdx] != null)
        {
            entityList = BuildFullViewEntitySet(sysIdx);
        }
        else
        {
            entityList = PooledEntityList.Empty;
        }

        _parallelEntityLists[sysIdx] = entityList;

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        metrics.EntitiesProcessed = entityList.Count;
        if (hasChangeFilter && _systemViews[sysIdx] != null)
        {
            metrics.EntitiesSkippedByChangeFilter = _systemViews[sysIdx].Count - entityList.Count;
        }

        if (entityList.Count == 0)
        {
            return 0;
        }

        return ComputeChunkCount(entityList.Count);
    }

    private int ComputeChunkCount(int entityCount)
    {
        var workerCount = Scheduler.WorkerCount;
        var minChunkSize = _options.ParallelQueryMinChunkSize;
        var maxChunks = Math.Max(1, (entityCount + minChunkSize - 1) / minChunkSize);
        return Math.Min(workerCount, maxChunks);
    }

    /// <summary>
    /// Chunk execution: dispatches to the appropriate path based on WritesVersioned.
    /// Non-Versioned: uses shared PointInTimeAccessor (no per-chunk Transaction).
    /// Versioned: creates a per-chunk Transaction (original fallback path).
    /// </summary>
    private void OnParallelQueryChunk(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        if (Scheduler.Systems[sysIdx].WritesVersioned)
        {
            ExecuteChunkWithTransaction(sysIdx, chunkIndex, totalChunks);
            return;
        }

        ExecuteChunkWithAccessor(sysIdx, chunkIndex, totalChunks, workerId);
    }

    /// <summary>Paths 1 & 2: Non-Versioned chunk execution with per-worker EntityAccessor from per-system PTA.</summary>
    private void ExecuteChunkWithAccessor(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        var pta = _parallelAccessors[sysIdx];
        var hasChangeFilter = _systemChangeFilterTables[sysIdx] != null;

        IReadOnlyCollection<EntityId> entities;
        int clusterStart = 0, clusterEnd = 0;

        if (hasChangeFilter)
        {
            // Path 2: Filtered — slice the materialized dirty entity list
            var fullList = _parallelEntityLists[sysIdx];
            var totalEntities = fullList.Count;
            var baseSize = totalEntities / totalChunks;
            var remainder = totalEntities % totalChunks;
            var start = chunkIndex * baseSize + Math.Min(chunkIndex, remainder);
            var count = baseSize + (chunkIndex < remainder ? 1 : 0);
            entities = new PooledEntitySlice(fullList.BackingArray, start, count);
        }
        else
        {
            // Path 1: Full — zero-copy partition view over HashMap buckets (per-system views, safe for concurrent systems)
            var partView = _partitionViews[sysIdx][chunkIndex];
            partView.Reset(_systemViews[sysIdx].EntityIdsInternal, chunkIndex, totalChunks);
            entities = partView;

            // Cluster-aware parallel dispatch: partition ActiveClusterIds range for this chunk.
            // Systems that use GetClusterEnumerator(ctx.StartClusterIndex, ctx.EndClusterIndex) get
            // correct work partitioning without iterating the full cluster set on every worker.
            var cs = _systemClusterStates[sysIdx];
            if (cs != null)
            {
                var totalClusters = cs.ActiveClusterCount;
                var cBase = totalClusters / totalChunks;
                var cRemainder = totalClusters % totalChunks;
                clusterStart = chunkIndex * cBase + Math.Min(chunkIndex, cRemainder);
                clusterEnd = clusterStart + cBase + (chunkIndex < cRemainder ? 1 : 0);
            }
        }

        // Get this worker's EntityAccessor — direct array lookup, zero dictionary overhead
        var workerAccessor = pta.GetWorkerAccessor(workerId);

        var ctx = new TickContext
        {
            TickNumber = Scheduler.CurrentTickNumber,
            DeltaTime = _currentDeltaTime,
            Accessor = workerAccessor,
            CreateSideTransaction = _createSideTxDelegate,
            Entities = entities,
            ConsumedQueues = null,
            StartClusterIndex = clusterStart,
            EndClusterIndex = clusterEnd
        };

        Scheduler.Systems[sysIdx].CallbackAction(ctx);
    }

    /// <summary>Paths 3 & 4: Versioned fallback — per-chunk Transaction (original path).</summary>
    private void ExecuteChunkWithTransaction(int sysIdx, int chunkIndex, int totalChunks)
    {
        var fullList = _parallelEntityLists[sysIdx];
        var totalEntities = fullList.Count;

        // Balanced partitioning: first `remainder` chunks get one extra entity
        var baseSize = totalEntities / totalChunks;
        var remainder = totalEntities % totalChunks;
        var start = chunkIndex * baseSize + Math.Min(chunkIndex, remainder);
        var count = baseSize + (chunkIndex < remainder ? 1 : 0);

        // Create per-chunk Transaction on THIS worker thread (respects thread affinity)
        var tx = _currentUow.CreateTransaction();
        var success = true;
        try
        {
            var slice = new PooledEntitySlice(fullList.BackingArray, start, count);
            var ctx = new TickContext
            {
                TickNumber = Scheduler.CurrentTickNumber,
                DeltaTime = _currentDeltaTime,
                Transaction = tx,
                CreateSideTransaction = _createSideTxDelegate,
                Entities = slice,
                ConsumedQueues = null
            };

            Scheduler.Systems[sysIdx].CallbackAction(ctx);
        }
        catch
        {
            success = false;
            throw; // Re-throw — DagScheduler's ProcessParallelQuery handles logging + _systemFailed
        }
        finally
        {
            if (success)
            {
                tx.Commit();
            }
            else
            {
                tx.Rollback();
            }

            tx.Dispose();
        }
    }

    /// <summary>
    /// Cleanup: returns pooled entity lists (if any) and resets state.
    /// Long-lived PTAs are NOT disposed here — they persist across ticks.
    /// </summary>
    private void OnParallelQueryCleanup(int sysIdx)
    {
        // Batch epoch flush: flush all workers that participated (once per system, not per chunk).
        // This avoids N×chunks epoch refreshes and reduces global EpochManager contention.
        var pta = _parallelAccessors[sysIdx];
        if (pta != null)
        {
            for (int w = 0; w < Scheduler.WorkerCount; w++)
            {
                pta.FlushWorker(w);
            }
        }

        _parallelEntityLists[sysIdx].Return();
        _parallelEntityLists[sysIdx] = default;
    }

    // ═══════════════════════════════════════════════════════════════
    // Tick lifecycle hooks (called by DagScheduler)
    // ═══════════════════════════════════════════════════════════════

    private void OnTickStartInternal(DagScheduler scheduler)
    {
        var now = Stopwatch.GetTimestamp();
        _currentDeltaTime = _previousTickTimestamp > 0 ? (float)((now - _previousTickTimestamp) / (double)Stopwatch.Frequency) : 0f;
        _previousTickTimestamp = now;

        // Create UoW for this tick (Deferred — batch all system commits, single WAL flush at end)
        _currentUow = Engine.CreateUnitOfWork();

        // OnFirstTick: runs once, on the timer thread before workers wake
        if (!_firstTickExecuted && OnFirstTick != null)
        {
            var tx = _currentUow.CreateTransaction();
            var ctx = new TickContext
            {
                TickNumber = scheduler.CurrentTickNumber,
                DeltaTime = _currentDeltaTime,
                Transaction = tx,
                CreateSideTransaction = _createSideTxDelegate,
                Entities = PooledEntityList.Empty
            };

            try
            {
                OnFirstTick.Invoke(ctx);
                tx.Commit();
            }
            finally
            {
                tx.Dispose();
                _firstTickExecuted = true; // Set in finally — prevents infinite retry if handler throws
            }
        }
    }

    private void OnTickEndInternal(DagScheduler scheduler)
    {
        // All system transactions have been committed individually.
        //
        // Issue #229 Phase 3 ordering — WriteTickFence runs BEFORE UoW.Flush.
        // Reason: the cluster tick fence publishes WAL records (ClusterTickFence chunks) describing the tick's dirty cluster-content changes.
        // It is ALSO the point where the Phase 3 migration fence runs (DetectClusterMigrations + ExecuteMigrations), mutating cluster pages directly.
        // By running WriteTickFence first, the subsequent UoW.Flush waits for a currentLsn that includes those publishes, so all migration writes become
        // per-tick durable via the/ same fsync that covers normal system commits. Moving WriteTickFence after Flush (the pre-Phase-3 ordering) would make
        // migration writes durable only at the NEXT tick's flush — a one-tick lag that's acceptable for the original R-Tree maintenance use case but unsafe
        // for persistent cluster content mutation.
        //
        // See debate decision Q1 in the Phase 3 design notes, and claude/design/spatial-tiers/01-spatial-clusters.md §"Migration fence WAL atomicity".
        Engine.WriteTickFence(scheduler.CurrentTickNumber);

        // Flush the UoW to make all Deferred writes (including the tick fence publishes above) durable, then dispose. UoW.Flush in WAL mode calls
        // WalManager.RequestFlush + WaitForDurable(currentLsn), where currentLsn is captured at the moment of the call — so it includes every publish made
        // in WriteTickFence.
        try
        {
            _currentUow?.Flush();
        }
        finally
        {
            _currentUow?.Dispose();
            _currentUow = null;
        }

        // #199: Output phase — subscription deltas.
        // Runs AFTER WriteTickFence so that:
        //   1. Ring buffer has ALL entries (commit-time + shadow-time) for correct View membership
        //   2. PreviousTickDirtyBitmap has this tick's dirty chunks for Modified detection
        //   3. All state is quiescent (no concurrent writers)
        _subscriptionOutputPhase?.Execute(scheduler.CurrentTickNumber, Scheduler.CurrentOverloadLevel);
    }

    private TickContext OnSystemStartInternal(int sysIdx)
    {
        // Create a Transaction on the CALLING THREAD (worker thread).
        // This respects Transaction's single-thread affinity constraint.
        var tx = _currentUow.CreateTransaction();
        _systemTransactions[sysIdx] = tx;

        // #197: Build entity set based on input View and change filter
        IReadOnlyCollection<EntityId> entities;
        var hasChangeFilter = _systemViews[sysIdx] != null && _systemChangeFilterTables[sysIdx] != null;
        if (hasChangeFilter)
        {
            var list = BuildFilteredEntitySet(sysIdx);
            _systemEntityLists[sysIdx] = list;
            entities = list;
        }
        else if (_systemViews[sysIdx] != null)
        {
            var list = BuildFullViewEntitySet(sysIdx);
            _systemEntityLists[sysIdx] = list;
            entities = list;
        }
        else
        {
            entities = PooledEntityList.Empty;
        }

        // #198: Record entity counts into per-system telemetry
        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        var entityCount = entities is PooledEntityList pel ? pel.Count : 0;
        metrics.EntitiesProcessed = entityCount;
        if (hasChangeFilter && _systemViews[sysIdx] != null)
        {
            metrics.EntitiesSkippedByChangeFilter = _systemViews[sysIdx].Count - entityCount;
        }

        return new TickContext
        {
            TickNumber = Scheduler.CurrentTickNumber,
            DeltaTime = _currentDeltaTime,
            Transaction = tx,
            CreateSideTransaction = _createSideTxDelegate,
            Entities = entities,
            ConsumedQueues = _systemConsumedQueues[sysIdx]
        };
    }

    private void OnSystemEndInternal(int sysIdx, bool success)
    {
        var tx = _systemTransactions[sysIdx];
        if (tx == null)
        {
            return;
        }

        try
        {
            if (success)
            {
                tx.Commit();
            }
            else
            {
                tx.Rollback();
            }
        }
        finally
        {
            tx.Dispose();
            _systemTransactions[sysIdx] = null;

            // #197: Return pooled entity list to ArrayPool
            _systemEntityLists[sysIdx].Return();
            _systemEntityLists[sysIdx] = default;
        }
    }
}
