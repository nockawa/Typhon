namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Defines a simulation scenario that generates load on Typhon.
/// </summary>
public interface IScenario
{
    /// <summary>
    /// Display name for the scenario.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description of what this scenario simulates.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Runs the scenario until cancellation.
    /// </summary>
    /// <param name="context">Typhon database context.</param>
    /// <param name="config">Scenario configuration.</param>
    /// <param name="stats">Statistics to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken cancellationToken);
}

/// <summary>
/// Configuration for a scenario run.
/// </summary>
public class ScenarioConfig
{
    /// <summary>
    /// How long to run the scenario.
    /// </summary>
    public int DurationSeconds { get; set; } = 30;

    /// <summary>
    /// Target operations per second (int.MaxValue = unlimited).
    /// </summary>
    public int TargetOpsPerSecond { get; set; } = 1000;

    /// <summary>
    /// Number of concurrent workers.
    /// </summary>
    public int WorkerCount { get; set; } = 4;
}

/// <summary>
/// Statistics collected during scenario execution.
/// </summary>
public class ScenarioStats
{
    private long _totalOps;
    private long _successOps;
    private long _failedOps;
    private long _txCommitted;
    private long _txRolledBack;
    private long _totalLatencyUs;
    private long _latencyCount;
    private Exception _firstException;

    public long TotalOperations => Interlocked.Read(ref _totalOps);
    public long SuccessfulOperations => Interlocked.Read(ref _successOps);
    public long FailedOperations => Interlocked.Read(ref _failedOps);
    public long TransactionsCommitted => Interlocked.Read(ref _txCommitted);
    public long TransactionsRolledBack => Interlocked.Read(ref _txRolledBack);
    public Exception FirstException => _firstException;

    public double AverageLatencyUs
    {
        get
        {
            var count = Interlocked.Read(ref _latencyCount);
            return count > 0 ? (double)Interlocked.Read(ref _totalLatencyUs) / count : 0;
        }
    }

    public void RecordSuccess(long latencyUs = 0)
    {
        Interlocked.Increment(ref _totalOps);
        Interlocked.Increment(ref _successOps);
        if (latencyUs > 0)
        {
            Interlocked.Add(ref _totalLatencyUs, latencyUs);
            Interlocked.Increment(ref _latencyCount);
        }
    }

    public void RecordFailure(Exception ex = null)
    {
        Interlocked.Increment(ref _totalOps);
        var failCount = Interlocked.Increment(ref _failedOps);
        // Capture the first exception for debugging
        if (failCount == 1 && ex != null)
        {
            Interlocked.CompareExchange(ref _firstException, ex, null);
        }
    }

    public void RecordCommit() => Interlocked.Increment(ref _txCommitted);
    public void RecordRollback() => Interlocked.Increment(ref _txRolledBack);
}

/// <summary>
/// Registry of available scenarios.
/// </summary>
public static class ScenarioRegistry
{
    /// <summary>
    /// Gets metadata about available scenarios for display purposes.
    /// </summary>
    public static IReadOnlyList<(string Name, string Description, Func<IScenario> Factory)> GetScenarioFactories() =>
    [
        ("Baseline (Unit Test Style)", "Single-threaded, synchronous CRUD. Mirrors unit test patterns to verify engine works.", () => new BaselineScenario()),
        ("Factory Bootstrap", "Creates buildings, conveyor belts, and resource nodes. Heavy CREATE load.", () => new FactoryBootstrapScenario()),
        ("Factory Production", "Updates production progress, item quantities. Heavy UPDATE load.", () => new FactoryProductionScenario()),
        ("Factory Supply Chain", "Simulates belts, item movement, and logistics queries. Mixed READ/UPDATE.", () => new FactorySupplyChainScenario()),
        ("RPG World Simulation", "Simulates NPC movement, player interactions. Balanced CRUD operations.", () => new RpgWorldSimulationScenario()),
        ("RPG Combat", "Intense battle simulation: damage, healing, skill cooldowns. High-frequency UPDATEs.", () => new RpgCombatScenario()),
        ("RPG Questing", "Quest acceptance, progress tracking, inventory rewards. Mixed CRUD.", () => new RpgQuestingScenario()),
        ("Mixed Workload", "Factory + RPG simultaneously. Tests mixed component types and isolation.", () => new MixedWorkloadScenario()),
        ("High Contention", "Multiple workers updating same entities. Stress tests locking and MVCC.", () => new HighContentionScenario())
    ];

    /// <summary>
    /// Gets fresh scenario instances (for backward compatibility).
    /// Each call creates new instances to avoid stale state between runs.
    /// </summary>
    public static IReadOnlyList<IScenario> GetScenarios() =>
        GetScenarioFactories().Select(f => f.Factory()).ToList();
}
