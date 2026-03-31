using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

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
    private readonly PooledEntityList[] _systemEntityLists;        // For returning ArrayPool buffers

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
        _systemEntityLists = new PooledEntityList[scheduler.SystemCount];

        ResolveChangeFilters(scheduler);

        // Initialize subscription infrastructure
        var subOptions = options.SubscriptionServer ?? new SubscriptionServerOptions();
        _subscriptionOutputPhase = new SubscriptionOutputPhase(engine, _publishedViewRegistry, _clientConnectionManager, subOptions, logger);

        // Wire tick lifecycle hooks
        Scheduler.TickStartCallback = OnTickStartInternal;
        Scheduler.TickEndCallback = OnTickEndInternal;
        Scheduler.SystemStartCallback = OnSystemStartInternal;
        Scheduler.SystemEndCallback = OnSystemEndInternal;

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
                CreateSideTransaction = CreateSideTransactionInternal,
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
        }
    }

    /// <summary>
    /// Build the filtered entity set for a system with change filter.
    /// Iterates the raw dirty bitmap from the previous tick, reads entity PKs from chunk offset 0, and intersects with the View's entity set. OR logic
    /// across multiple changeFilter tables.
    /// Falls back to full View when PK resolution is unavailable (first tick, or SV without indexed fields).
    /// </summary>
    private unsafe PooledEntityList BuildFilteredEntitySet(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        var filterTables = _systemChangeFilterTables[sysIdx];

        // Collect dirty entity PKs that are in the View (OR across all changeFilter tables)
        var dirtyInView = new HashSet<long>();

        for (var t = 0; t < filterTables.Length; t++)
        {
            var table = filterTables[t];
            if (!table.PreviousTickHadDirtyEntities)
            {
                continue;
            }

            var bitmap = table.PreviousTickDirtyBitmap;
            if (bitmap == null)
            {
                // First tick — fall back to full View
                return BuildFullViewEntitySet(sysIdx);
            }

            // PK at chunk offset 0 is only written during spawn for SV components with indexed fields.
            if (table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
            {
                // Cannot resolve chunkId → PK — fall back to full View
                return BuildFullViewEntitySet(sysIdx);
            }

            // Iterate bitmap → chunkId → PK → View intersection (same pattern as ProcessSpatialEntries)
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

                        var chunkPtr = accessor.GetChunkAddress(chunkId);
                        var entityPK = *(long*)chunkPtr;

                        if (view.Contains(entityPK))
                        {
                            dirtyInView.Add(entityPK);
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
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
                CreateSideTransaction = CreateSideTransactionInternal,
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
            }

            _firstTickExecuted = true;
        }
    }

    private void OnTickEndInternal(DagScheduler scheduler)
    {
        // All system transactions have been committed individually.
        // Flush the UoW to make all Deferred writes durable, then dispose.
        try
        {
            _currentUow?.Flush();
        }
        finally
        {
            _currentUow?.Dispose();
            _currentUow = null;
        }

        // #197: Tick fence — snapshot SV DirtyBitmaps, process shadow entries, update spatial indexes.
        // This captures which entities were written this tick for next tick's change-filtered system inputs.
        // Also fires NotifyViews for indexed field changes via ProcessShadowEntries, populating
        // published View ring buffers with the complete set of change entries.
        Engine.WriteTickFence(scheduler.CurrentTickNumber);

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
            CreateSideTransaction = CreateSideTransactionInternal,
            Entities = entities
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
