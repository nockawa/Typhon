using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Immutable definition of a system node in the system DAG.
/// Created by <see cref="DagBuilder"/> and consumed by <see cref="DagScheduler"/>.
/// </summary>
[PublicAPI]
public sealed class SystemDefinition
{
    /// <summary>Unique name identifying this system in the DAG.</summary>
    public string Name { get; init; }

    /// <summary>Execution model: PipelineSystem (multi-worker chunks), QuerySystem (single-worker entities), or CallbackSystem (inline).</summary>
    public SystemType Type { get; init; }

    /// <summary>Position in the systems array. Set by <see cref="DagBuilder.Build"/>.</summary>
    public int Index { get; internal set; }

    /// <summary>Priority for overload management. Defined but not enforced until #201.</summary>
    public SystemPriority Priority { get; init; } = SystemPriority.Normal;

    // ═══════════════════════════════════════════════════════════════
    // Execution delegates
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Delegate for CallbackSystem and QuerySystem systems. Invoked once per tick on a single worker.
    /// Receives <see cref="TickContext"/> with tick-level information.
    /// </summary>
    public Action<TickContext> CallbackAction { get; init; }

    /// <summary>
    /// Delegate for Pipeline systems. Called per chunk with (chunkIndex, totalChunks).
    /// Multiple workers call this concurrently with distinct chunk indices.
    /// No TickContext — Pipeline's entity access goes through Gather/Scatter pipelines.
    /// </summary>
    public Action<int, int> PipelineChunkAction { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // DAG structure (set by DagBuilder.Build)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Indices of successor systems in the DAG.</summary>
    public int[] Successors { get; internal set; } = [];

    /// <summary>Number of predecessor systems that must complete before this system can execute.</summary>
    public int PredecessorCount { get; internal set; }

    /// <summary>
    /// Number of work chunks for Pipeline systems. Determines parallelism granularity.
    /// For CallbackSystem/QuerySystem systems this is always 1.
    /// Mutable because Pipeline chunk count may change per tick based on query result set size.
    /// </summary>
    public int TotalChunks { get; set; } = 1;

    // ═══════════════════════════════════════════════════════════════
    // Run conditions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Optional predicate evaluated before dispatch. If it returns false, the system is skipped and its successors are dispatched immediately.
    /// Null means always run. Evaluated before input query to avoid View refresh cost on false predicate.
    /// </summary>
    public Func<bool> RunIf { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // Overload parameters (stored for #201, not enforced yet)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>System runs every N ticks under normal load. Default: 1 (every tick).</summary>
    public int TickDivisor { get; set; } = 1;

    /// <summary>System runs every M ticks under overload. Default: 1 (every tick).</summary>
    public int ThrottledTickDivisor { get; set; } = 1;

    /// <summary>Whether this system can be shed (dropped entirely) under severe overload.</summary>
    public bool CanShed { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Event queue references (indices into scheduler's queue array)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Indices of event queues this system produces (writes to).</summary>
    public int[] ProducesQueueIndices { get; internal set; } = [];

    /// <summary>Indices of event queues this system consumes (reads from).</summary>
    public int[] ConsumesQueueIndices { get; internal set; } = [];

    // ═══════════════════════════════════════════════════════════════
    // Change filter (stored for #197, not enforced yet)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Component indices for dirty-bitset filtering. Empty means no filter (process all).</summary>
    public int[] ChangeFilterComponentIndices { get; init; } = [];
}
