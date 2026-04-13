using System;
using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Shared profiler activation helper used by both the Godot entry point (Main.cs)
/// and the headless profile runner (profiling/Program.cs).
/// </summary>
/// <remarks>
/// Centralizes the JIT-gate ordering rule so neither entry point can silently
/// break it. The sequence must be:
/// <list type="number">
/// <item>Set Typhon telemetry env vars</item>
/// <item>Call <see cref="TelemetryConfig.EnsureInitialized"/></item>
/// <item>Only then construct <see cref="TyphonBridge"/> / <see cref="TyphonRuntime"/></item>
/// </list>
/// If env vars are set AFTER the TelemetryConfig static constructor has run, or
/// EnsureInitialized is called AFTER DagScheduler has JIT'd its hot methods,
/// the DeepTrace flag is silently ignored and the inspector receives zero events.
/// </remarks>
public static class ProfilerSetup
{
    public const int DefaultLivePort = 9001;

    /// <summary>
    /// If profiling was requested, enables telemetry, forces TelemetryConfig initialization,
    /// and constructs the appropriate inspector. Returns null if neither traceFile nor
    /// livePort was provided.
    /// </summary>
    /// <param name="traceFile">Path to write a .typhon-trace file, or null for no file mode.</param>
    /// <param name="livePort">TCP port for live mode, or -1 for no live mode.</param>
    /// <remarks>
    /// Call BEFORE constructing <see cref="TyphonBridge"/>. If both traceFile and livePort
    /// are provided, traceFile wins (file mode).
    /// </remarks>
    public static IRuntimeInspector TryCreateInspector(string traceFile, int livePort)
    {
        if (traceFile == null && livePort < 0) return null;

        // Step 1: env vars. Must happen before TelemetryConfig static ctor reads them.
        Environment.SetEnvironmentVariable("TYPHON__TELEMETRY__ENABLED", "true");
        Environment.SetEnvironmentVariable("TYPHON__TELEMETRY__SCHEDULER__ENABLED", "true");
        Environment.SetEnvironmentVariable("TYPHON__TELEMETRY__SCHEDULER__DEEPTRACE", "true");

        // Step 2: force the static ctor to run now, with env vars in place.
        // Must happen before DagScheduler's hot methods JIT, or the guard is baked as false.
        TelemetryConfig.EnsureInitialized();

        // Step 3: construct the inspector. Safe to call TyphonBridge.Initialize(inspector) after.
        return traceFile != null
            ? new TraceFileInspector(traceFile)
            : new TcpStreamInspector(livePort);
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
            else livePort = DefaultLivePort; // presence without a number → default port
        }

        return (traceFile, livePort);
    }

    /// <summary>
    /// Prints telemetry diagnostics to the provided logger delegate. Safe to call after
    /// <see cref="TyphonBridge"/>'s runtime has been started. Intended for startup troubleshooting
    /// when OTel spans aren't appearing in the profiler viewer.
    /// </summary>
    /// <param name="log">Logger delegate — pass <c>Godot.GD.Print</c> from Godot code or
    /// <c>System.Console.WriteLine</c> from the headless runner.</param>
    /// <param name="inspector">The inspector that was passed to <c>TyphonBridge.Initialize</c>,
    /// or null if profiling wasn't requested.</param>
    /// <remarks>
    /// The key diagnostic value is <c>TyphonActivitySource.Instance.HasListeners()</c>. If it
    /// returns <c>true</c>, the profiler's <c>ActivityListener</c> is registered and spans
    /// emitted by the engine will be captured. If <c>false</c>, the listener was never attached
    /// — either because profiling wasn't requested (inspector is null), the scheduler hasn't
    /// started yet, or <c>TelemetryConfig.SchedulerDeepTrace</c> was false at JIT time.
    /// </remarks>
    public static void PrintDiagnostics(Action<string> log, IRuntimeInspector inspector)
    {
        if (log == null) return;

        log("───────────────────────────────────────────────────────────");
        log(" Typhon Telemetry Diagnostics");
        log("───────────────────────────────────────────────────────────");
        log(TelemetryConfig.GetActiveComponentsSummary());
        log("");
        log(TelemetryConfig.GetConfigurationSummary());
        log("");
        log($" Inspector:                       {(inspector != null ? inspector.GetType().Name : "(none — profiling not requested)")}");
        log($" OTel ActivityListener attached:  {TyphonActivitySource.Instance.HasListeners()}");
        log("");
        log(" Interpretation:");
        log("   * If 'OTel ActivityListener attached' is TRUE, the capture path is live.");
        log("     Spans fired by the engine (Transaction/ECS/Spatial/...) will flow to the viewer");
        log("     provided their TelemetryConfig.XxxActive flag is also TRUE above.");
        log("   * If FALSE, no spans will be captured. Most common causes:");
        log("     - Inspector is null (profiling wasn't requested this run)");
        log("     - Scheduler hasn't started yet (call this AFTER TyphonBridge.Start)");
        log("     - TelemetryConfig.SchedulerDeepTrace was false at JIT time");
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
