using System;
using System.Diagnostics;
using Typhon.Engine;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace AntHill;

/// <summary>
/// AntHill-specific glue between the runtime's <see cref="SystemDefinition"/> array and the profiler's
/// <see cref="ProfilerSessionMetadata"/> shape. The CLI/env parsing and exporter construction now live in
/// the engine — see <see cref="Typhon.Engine.Profiler.ProfilerLaunchConfig"/> and
/// <see cref="Typhon.Engine.Profiler.ProfilerLauncher"/>.
/// </summary>
/// <remarks>
/// What stays AntHill-specific: knowing AntHill's system list to build <see cref="SystemDefinitionRecord"/>s
/// that the trace file / TCP stream embed for the viewer's system-index → display-name lookup. That's a host
/// concern (each host has a different DAG), so it doesn't belong in the engine.
/// </remarks>
public static class ProfilerSetup
{
    /// <summary>
    /// Build the <see cref="ProfilerSessionMetadata"/> passed to <see cref="TyphonProfiler.Start"/>.
    /// Converts the runtime's <see cref="SystemDefinition"/> array into the serialized
    /// <see cref="SystemDefinitionRecord"/> shape expected by the trace file / TCP stream, so the
    /// viewer can resolve system-index → display name.
    /// </summary>
    /// <remarks>
    /// Archetype + component-type tables are left empty: the engine currently emits typed events
    /// containing numeric IDs only; name resolution for those tables is a follow-up when the
    /// AntHill workload needs per-archetype flame-graph labels. Timestamps anchor the session —
    /// all subsequent events are measured against <c>startTimestamp</c>.
    /// </remarks>
    public static ProfilerSessionMetadata BuildSessionMetadata(SystemDefinition[] systems, int workerCount, float baseTickRate,
        Func<long> currentEngineTickProvider = null)
    {
        // currentEngineTickProvider is accepted for forward-compat with callers but the current
        // ProfilerSessionMetadata schema does not expose a per-event tick-stamp hook — the parameter
        // is intentionally ignored. Archetype/ComponentType records are left empty: ArchetypeRegistry
        // has no public enumeration API yet, and the engine emits typed events with numeric IDs only,
        // so the viewer renders them un-resolved for the moment.
        _ = currentEngineTickProvider;
        return new ProfilerSessionMetadata(BuildSystemRecords(systems), [], [], workerCount, baseTickRate,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTime.UtcNow);
    }

    // SystemDefinition stores successors but not predecessors — invert the edge list once so the
    // record table is self-describing for the viewer (which renders both directions).
    private static SystemDefinitionRecord[] BuildSystemRecords(SystemDefinition[] systems)
    {
        if (systems == null || systems.Length == 0) return [];

        var predecessors = new System.Collections.Generic.List<ushort>[systems.Length];
        for (int i = 0; i < systems.Length; i++)
        {
            predecessors[i] = new System.Collections.Generic.List<ushort>();
        }
        for (int i = 0; i < systems.Length; i++)
        {
            foreach (var succ in systems[i].Successors)
            {
                predecessors[succ].Add((ushort)i);
            }
        }

        var records = new SystemDefinitionRecord[systems.Length];
        for (int i = 0; i < systems.Length; i++)
        {
            var sys = systems[i];
            var succIndices = sys.Successors;
            var succUshort = new ushort[succIndices.Length];
            for (int s = 0; s < succIndices.Length; s++)
            {
                succUshort[s] = (ushort)succIndices[s];
            }

            records[i] = new SystemDefinitionRecord
            {
                Index = (ushort)sys.Index,
                Name = sys.Name,
                Type = (byte)sys.Type,
                Priority = (byte)sys.Priority,
                IsParallel = sys.IsParallelQuery,
                TierFilter = (byte)sys.TierFilter,
                Predecessors = predecessors[i].ToArray(),
                Successors = succUshort,
            };
        }
        return records;
    }
}
