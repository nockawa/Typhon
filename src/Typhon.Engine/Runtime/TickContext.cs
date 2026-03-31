using JetBrains.Annotations;
using System;

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
}
