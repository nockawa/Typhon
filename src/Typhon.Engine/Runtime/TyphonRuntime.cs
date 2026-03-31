using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;

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

    // First-tick flag
    private bool _firstTickExecuted;

    // DeltaTime tracking
    private long _previousTickTimestamp;
    private float _currentDeltaTime;

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

        // Wire tick lifecycle hooks
        Scheduler.TickStartCallback = OnTickStartInternal;
        Scheduler.TickEndCallback = OnTickEndInternal;
        Scheduler.SystemStartCallback = OnSystemStartInternal;
        Scheduler.SystemEndCallback = OnSystemEndInternal;
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Starts the scheduler (worker threads + tick driver).</summary>
    public void Start() => Scheduler.Start();

    /// <summary>
    /// Gracefully shuts down the runtime. Fires <see cref="OnShutdown"/>, then stops the scheduler.
    /// </summary>
    public void Shutdown()
    {
        // Execute OnShutdown callback with a dedicated transaction
        if (OnShutdown != null)
        {
            using var tx = Engine.CreateQuickTransaction(DurabilityMode.Immediate);
            var ctx = new TickContext
            {
                TickNumber = Scheduler.CurrentTickNumber,
                DeltaTime = 0f,
                Transaction = tx,
                CreateSideTransaction = CreateSideTransactionInternal
            };
            OnShutdown.Invoke(ctx);
            tx.Commit();
        }

        Scheduler.Shutdown();
    }

    /// <inheritdoc />
    public void Dispose() => Scheduler.Dispose();

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
                CreateSideTransaction = CreateSideTransactionInternal
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
    }

    private TickContext OnSystemStartInternal(int sysIdx)
    {
        // Create a Transaction on the CALLING THREAD (worker thread).
        // This respects Transaction's single-thread affinity constraint.
        var tx = _currentUow.CreateTransaction();
        _systemTransactions[sysIdx] = tx;

        return new TickContext
        {
            TickNumber = Scheduler.CurrentTickNumber,
            DeltaTime = _currentDeltaTime,
            Transaction = tx,
            CreateSideTransaction = CreateSideTransactionInternal
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
        }
    }
}
