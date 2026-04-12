namespace Typhon.Profiler;

/// <summary>
/// Identifies a phase within a tick's lifecycle.
/// </summary>
public enum TickPhase : byte
{
    /// <summary>System dispatch phase — the DAG scheduler is running systems.</summary>
    SystemDispatch = 0,

    /// <summary>UoW flush — deferred writes becoming durable (WAL write).</summary>
    UowFlush = 1,

    /// <summary>Write tick fence — dirty bitmap snapshot, shadow entry processing, spatial index update.</summary>
    WriteTickFence = 2,

    /// <summary>Subscription output — refresh published Views, compute deltas, push to clients.</summary>
    OutputPhase = 3,

    /// <summary>Tier index rebuild — rebuild per-archetype tier cluster indexes at tick start.</summary>
    TierIndexRebuild = 4,

    /// <summary>Dormancy sweep — advance sleep counters, transition idle clusters.</summary>
    DormancySweep = 5
}
