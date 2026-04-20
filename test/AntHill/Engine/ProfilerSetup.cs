using System;
using System.Collections.Generic;
using System.Diagnostics;
using Typhon.Engine;
using Typhon.Engine.Profiler;
using Typhon.Engine.Profiler.Exporters;
using Typhon.Profiler;

namespace AntHill;

/// <summary>
/// Shared profiler activation helper used by both the Godot entry point (Main.cs)
/// and the headless profile runner (profiling/Program.cs).
/// </summary>
/// <remarks>
/// Post-#243 the engine switched from the pull-based <c>IRuntimeInspector</c> to a push-based
/// typed-event pipeline: the scheduler emits <c>TyphonEvent.Emit*</c> internally, a single
/// consumer thread drains the producer rings, and fans the batches out to one or more
/// <see cref="IProfilerExporter"/> instances.
///
/// Activation requires two steps in strict order:
/// <list type="number">
/// <item>Before any engine type JITs, set <c>TYPHON__TELEMETRY__ENABLED</c> and
/// <c>TYPHON__TELEMETRY__PROFILER__ENABLED</c> env vars, then call
/// <see cref="TelemetryConfig.EnsureInitialized"/> so <see cref="TelemetryConfig.ProfilerActive"/>
/// is baked to <c>true</c>. See <see cref="PrepareProfiling"/>.</item>
/// <item>After <see cref="TyphonBridge.Initialize"/> builds the DI container, construct a
/// <see cref="FileExporter"/> or <see cref="TcpExporter"/> parented to <c>registry.Profiler</c>,
/// attach it to <see cref="TyphonProfiler"/>, then call <see cref="TyphonProfiler.Start"/> with
/// a <see cref="ProfilerSessionMetadata"/> describing the system DAG. See <see cref="CreateExporter"/>
/// and <see cref="BuildSessionMetadata"/>.</item>
/// </list>
/// Teardown: call <see cref="TyphonProfiler.Stop"/> — it flushes + disposes every attached
/// exporter. <see cref="TyphonProfiler.DetachExporter"/> afterwards leaves the static list empty
/// for the next run (relevant when the same process re-inits, e.g. hot-reload scenarios).
/// </remarks>
public static class ProfilerSetup
{
    public const int DefaultLivePort = 9100;

    /// <summary>
    /// Step 1 of profiler activation: env vars + <see cref="TelemetryConfig.EnsureInitialized"/>.
    /// Must run BEFORE constructing <see cref="TyphonBridge"/> so the JIT gate
    /// (<see cref="TelemetryConfig.ProfilerActive"/>) is open when DagScheduler's hot methods compile.
    /// Returns <c>true</c> if profiling was requested (either input is set), <c>false</c> otherwise.
    /// </summary>
    public static bool PrepareProfiling(string traceFile, int livePort)
    {
        if (traceFile == null && livePort < 0) return false;

        Environment.SetEnvironmentVariable("TYPHON__TELEMETRY__ENABLED", "true");
        Environment.SetEnvironmentVariable("TYPHON__TELEMETRY__PROFILER__ENABLED", "true");
        TelemetryConfig.EnsureInitialized();
        return true;
    }

    /// <summary>
    /// Step 2 of profiler activation: construct exporters parented to the engine's <c>registry.Profiler</c> resource. Must run AFTER
    /// <see cref="TyphonBridge.Initialize"/> has built the DI container (so the parent resource exists).
    /// </summary>
    /// <remarks>
    /// If both <paramref name="traceFile"/> and <paramref name="livePort"/> are supplied, BOTH exporters are returned (dual-attach):
    /// the session is simultaneously streamed to the live viewer AND archived to the file. <see cref="TyphonProfiler"/> fans each batch
    /// to every attached exporter, so there's no double-decode or double-network cost — the consumer thread pushes the same bytes to
    /// both. Passing only one yields just that exporter. Passing neither yields an empty list (profiling not requested).
    /// </remarks>
    /// <param name="profilerParent">The <see cref="IResource"/> from <c>registry.Profiler</c>. Use <see cref="TyphonBridge.ProfilerParent"/>
    /// post-<c>Initialize</c>.</param>
    public static List<IProfilerExporter> CreateExporters(string traceFile, int livePort, IResource profilerParent)
    {
        var exporters = new List<IProfilerExporter>(2);
        if (traceFile == null && livePort < 0) return exporters;
        ArgumentNullException.ThrowIfNull(profilerParent);

        if (traceFile != null) exporters.Add(new FileExporter(traceFile, profilerParent));
        if (livePort >= 0) exporters.Add(new TcpExporter(livePort, profilerParent));
        return exporters;
    }

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
        return new ProfilerSessionMetadata(BuildSystemRecords(systems), BuildArchetypeRecords(), BuildComponentTypeRecords(), workerCount, baseTickRate, 
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTime.UtcNow, 0L, currentEngineTickProvider);
    }

    // Pulled from ArchetypeRegistry.EnumerateArchetypes so the viewer can resolve ArchetypeId → "Ant"/"Food"/"Nest" names in spans like
    // EcsSpawn, ClusterMigration, EcsQueryExecute. Without this table, those records carry raw numeric IDs that the viewer can't label.
    private static ArchetypeRecord[] BuildArchetypeRecords()
    {
        var list = new List<ArchetypeRecord>();
        foreach (var (id, name) in ArchetypeRegistry.EnumerateArchetypes())
        {
            list.Add(new ArchetypeRecord { ArchetypeId = id, Name = name });
        }
        return list.ToArray();
    }

    // Same shape as archetype records but for component types — resolves ComponentTypeId → "AntHill.Position"/"AntHill.Genetics"/... in
    // TransactionCommitComponent spans. The name is the [Component(...)] attribute's schema name when present, falling back to the CLR type name.
    private static ComponentTypeRecord[] BuildComponentTypeRecords()
    {
        var list = new List<ComponentTypeRecord>();
        foreach (var (id, name) in ArchetypeRegistry.EnumerateComponentTypes())
        {
            list.Add(new ComponentTypeRecord { ComponentTypeId = id, Name = name });
        }
        return list.ToArray();
    }

    // SystemDefinition stores successors but not predecessors — invert the edge list once so the
    // record table is self-describing for the viewer (which renders both directions).
    private static SystemDefinitionRecord[] BuildSystemRecords(SystemDefinition[] systems)
    {
        if (systems == null || systems.Length == 0) return [];

        var predecessors = new List<ushort>[systems.Length];
        for (int i = 0; i < systems.Length; i++)
        {
            predecessors[i] = new List<ushort>();
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

    /// <summary>
    /// Reads TYPHON_PROFILER_TRACE and TYPHON_PROFILER_LIVE env vars (set by the user's shell
    /// before launching Godot) and returns parsed values. Used by the Godot entry point where
    /// CLI args aren't always the most convenient input channel.
    /// </summary>
    public static (string traceFile, int livePort) ReadEnvVars()
    {
        string traceFile = Environment.GetEnvironmentVariable("TYPHON_PROFILER_TRACE");
        if (string.IsNullOrWhiteSpace(traceFile)) traceFile = null;

        int livePort = -1;
        string liveEnv = Environment.GetEnvironmentVariable("TYPHON_PROFILER_LIVE");
        if (!string.IsNullOrWhiteSpace(liveEnv))
        {
            if (int.TryParse(liveEnv, out var p)) livePort = p;
            else livePort = DefaultLivePort;
        }

        return (traceFile, livePort);
    }

    /// <summary>
    /// Prints telemetry diagnostics to the provided logger delegate. Safe to call after
    /// <see cref="TyphonBridge"/>'s runtime has been started. Intended for startup troubleshooting
    /// when profiler events aren't reaching the viewer.
    /// </summary>
    /// <param name="log">Logger delegate — pass <c>Godot.GD.Print</c> from Godot code or
    /// <c>System.Console.WriteLine</c> from the headless runner.</param>
    /// <param name="exporter">The exporter returned by <see cref="CreateExporter"/>, or null if
    /// profiling wasn't requested.</param>
    public static void PrintDiagnostics(Action<string> log, IList<IProfilerExporter> exporters)
    {
        if (log == null) return;

        log("───────────────────────────────────────────────────────────");
        log(" Typhon Telemetry Diagnostics");
        log("───────────────────────────────────────────────────────────");
        log(TelemetryConfig.GetActiveComponentsSummary());
        log("");
        log(TelemetryConfig.GetConfigurationSummary());
        log("");
        string exporterSummary;
        if (exporters == null || exporters.Count == 0)
        {
            exporterSummary = "(none — profiling not requested)";
        }
        else
        {
            var names = new string[exporters.Count];
            for (int i = 0; i < exporters.Count; i++) names[i] = exporters[i].GetType().Name;
            exporterSummary = string.Join(", ", names);
        }
        log($" Exporters:                 {exporterSummary}");
        log($" ProfilerActive (JIT gate): {TelemetryConfig.ProfilerActive}");
        log($" TyphonProfiler.IsRunning:  {TyphonProfiler.IsRunning}");
        log("");
        log(" Interpretation:");
        log("   * ProfilerActive must be TRUE at JIT time for the scheduler to emit events.");
        log("     If FALSE: env vars weren't set or EnsureInitialized ran too late. Check");
        log("     PrepareProfiling is called BEFORE TyphonBridge construction.");
        log("   * TyphonProfiler.IsRunning must be TRUE for the consumer thread to drain");
        log("     events to the exporter. If FALSE: TyphonProfiler.Start was never called");
        log("     or already Stopped.");
        log("───────────────────────────────────────────────────────────");
    }

    /// <summary>
    /// Parses an argv-style string array looking for <c>--trace &lt;path&gt;</c> and
    /// <c>--live [port]</c>. Used by both Program.cs (full argv) and Main.cs (Godot user args
    /// after the <c>++</c> separator).
    /// </summary>
    public static (string traceFile, int livePort) ParseArgs(string[] args)
    {
        string traceFile = null;
        int livePort = -1;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trace" when i + 1 < args.Length:
                    traceFile = args[++i];
                    break;
                case "--live":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                    {
                        livePort = p;
                        i++;
                    }
                    else
                    {
                        livePort = DefaultLivePort;
                    }
                    break;
            }
        }

        return (traceFile, livePort);
    }
}
