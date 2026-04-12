namespace Typhon.Profiler;

/// <summary>
/// Describes a system in the DAG, stored in the system definition table of a <c>.typhon-trace</c> file.
/// Variable-length record (name is UTF-8 encoded).
/// </summary>
public sealed class SystemDefinitionRecord
{
    /// <summary>System index in the DAG array.</summary>
    public ushort Index { get; init; }

    /// <summary>Display name of the system.</summary>
    public string Name { get; init; }

    /// <summary>Execution model (Pipeline = 0, Query = 1, Callback = 2).</summary>
    public byte Type { get; init; }

    /// <summary>Priority for overload management (Normal = 0, Low = 1, High = 2, Critical = 3).</summary>
    public byte Priority { get; init; }

    /// <summary>Whether this system uses parallel chunk dispatch.</summary>
    public bool IsParallel { get; init; }

    /// <summary>Simulation tier filter (SimTier flags byte). 0x0F = All.</summary>
    public byte TierFilter { get; init; }

    /// <summary>Indices of predecessor systems in the DAG.</summary>
    public ushort[] Predecessors { get; init; } = [];

    /// <summary>Indices of successor systems in the DAG.</summary>
    public ushort[] Successors { get; init; } = [];
}
