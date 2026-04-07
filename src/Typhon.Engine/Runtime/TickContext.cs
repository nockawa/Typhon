using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Context passed to CallbackSystem and QuerySystem delegates during tick execution.
/// Provides a valid <see cref="Transaction"/> for entity operations and a factory for side-transactions.
/// </summary>
/// <remarks>
/// <para>
/// Each CallbackSystem/QuerySystem receives its own TickContext with a dedicated <see cref="Transaction"/>
/// created on the worker thread (respecting Transaction's single-thread affinity).
/// The Transaction is committed automatically after the system completes — systems must NOT commit or dispose it.
/// </para>
/// <para>
/// Pipeline systems do NOT receive TickContext — they use <c>Action&lt;int, int&gt;</c> and access entity data
/// through Gather/Scatter pipelines (separate mechanism).
/// </para>
/// </remarks>
[PublicAPI]
public struct TickContext
{
    /// <summary>Monotonically increasing tick number (0-based).</summary>
    public long TickNumber { get; init; }

    /// <summary>Elapsed time in seconds since the previous tick. Zero on the first tick.</summary>
    public float DeltaTime { get; init; }

    /// <summary>
    /// Transaction for this system's entity operations (Spawn, Open, OpenMut, Query, etc.).
    /// Created on the current worker thread. Valid only during this system's execution.
    /// Do NOT Commit or Dispose — the scheduler manages the Transaction lifecycle.
    /// Null when running without a DatabaseEngine (standalone scheduler tests).
    /// </summary>
    public Transaction Transaction { get; init; }

    /// <summary>
    /// Per-worker EntityAccessor for parallel QuerySystems that do NOT write Versioned components.
    /// Provides Open/OpenMut with warm ChunkAccessor caches, zero per-entity dictionary overhead.
    /// Null when the system uses Transaction-based access (WritesVersioned=true or non-parallel systems).
    /// </summary>
    public EntityAccessor Accessor { get; init; }

    /// <summary>
    /// Filtered entity set for this system's execution.
    /// <list type="bullet">
    /// <item><description>CallbackSystem: empty (no entity input)</description></item>
    /// <item><description>QuerySystem/PipelineSystem without changeFilter: full View entity set</description></item>
    /// <item><description>QuerySystem/PipelineSystem with changeFilter: dirty entities ∪ Added (only entities whose filtered components were written since last tick)</description></item>
    /// </list>
    /// The backing array is pooled — do not hold references beyond the system's Execute scope.
    /// </summary>
    public IReadOnlyCollection<EntityId> Entities { get; init; }

    /// <summary>
    /// Event queues this system consumes. Null if the system has no consumed queues.
    /// Cast to <c>EventQueue&lt;T&gt;</c> and call <c>Drain(span)</c> or <c>AsSpan()</c> to read events.
    /// </summary>
    public EventQueueBase[] ConsumedQueues { get; init; }

    /// <summary>
    /// Creates a side-transaction with the specified durability mode.
    /// Side-transactions commit independently and are NOT visible to the main tick Transaction (snapshot isolation — the main Transaction's TSN is fixed at
    /// creation).
    /// The caller owns the returned Transaction and must Dispose it.
    /// </summary>
    /// <remarks>
    /// Use for economy-critical operations (trades, purchases, progression) that must be durable immediately, independent of the main tick's commit.
    /// Null when running without a DatabaseEngine.
    /// </remarks>
    public Func<DurabilityMode, Transaction> CreateSideTransaction { get; init; }

    /// <summary>
    /// Inclusive start index into <see cref="ArchetypeClusterState.ActiveClusterIds"/> for this worker's assigned cluster range.
    /// Used by cluster-native systems that iterate via <c>ClusterEnumerator.CreateScoped</c> for 2-3 ns/entity performance.
    /// -1 when not applicable (non-parallel, non-cluster, or entity-level dispatch).
    /// </summary>
    /// <remarks>Default 0 (not -1) due to struct constraint. Check <c>EndClusterIndex > StartClusterIndex</c> for validity.</remarks>
    public int StartClusterIndex { get; init; }

    /// <summary>
    /// Exclusive end index into <see cref="ArchetypeClusterState.ActiveClusterIds"/> for this worker's assigned cluster range.
    /// </summary>
    /// <remarks>Default 0. Check <c>EndClusterIndex > StartClusterIndex</c> for validity — a zero range means not applicable.</remarks>
    public int EndClusterIndex { get; init; }
}
