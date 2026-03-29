using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Immutable definition of a system node in the meta-DAG.
/// Created by <see cref="DagBuilder"/> and consumed by <see cref="DagScheduler"/>.
/// </summary>
[PublicAPI]
public sealed class SystemDefinition
{
    /// <summary>Unique name identifying this system in the DAG.</summary>
    public string Name { get; init; }

    /// <summary>Execution model: Patate (multi-worker chunks) or Callback (single-worker inline).</summary>
    public SystemType Type { get; init; }

    /// <summary>Position in the systems array. Set by <see cref="DagBuilder.Build"/>.</summary>
    public int Index { get; internal set; }

    /// <summary>Priority for overload management. Defined but not enforced until #201.</summary>
    public SystemPriority Priority { get; init; } = SystemPriority.Normal;

    /// <summary>
    /// Delegate for Callback systems. Invoked once on a single worker.
    /// </summary>
    public Action CallbackAction { get; init; }

    /// <summary>
    /// Delegate for Patate systems. Called per chunk with (chunkIndex, totalChunks).
    /// Multiple workers call this concurrently with distinct chunk indices.
    /// </summary>
    public Action<int, int> PatateChunkAction { get; init; }

    /// <summary>
    /// Indices of successor systems in the DAG. Set by <see cref="DagBuilder.Build"/>.
    /// </summary>
    public int[] Successors { get; internal set; } = [];

    /// <summary>
    /// Number of predecessor systems that must complete before this system can execute.
    /// Set by <see cref="DagBuilder.Build"/>.
    /// </summary>
    public int PredecessorCount { get; internal set; }

    /// <summary>
    /// Number of work chunks for Patate systems. Determines parallelism granularity.
    /// For Callback systems this is always 1.
    /// Mutable because Patate chunk count may change per tick based on query result set size.
    /// </summary>
    public int TotalChunks { get; set; } = 1;
}
