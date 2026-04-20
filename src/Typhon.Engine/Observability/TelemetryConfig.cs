// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Global telemetry configuration for Typhon Engine.
///
/// <para>
/// This class provides <c>static readonly</c> fields that allow the JIT compiler to
/// eliminate disabled telemetry code paths entirely. When a readonly field is <c>false</c>,
/// the JIT can treat <c>if (TelemetryConfig.AccessControlActive)</c> as dead code and
/// remove it completely in Tier 1 compilation.
/// </para>
///
/// <para>
/// <b>IMPORTANT:</b> Call <see cref="EnsureInitialized"/> once at application startup,
/// BEFORE any hot paths are JIT compiled. This ensures the static constructor runs
/// early and the JIT sees the final values when compiling performance-critical methods.
/// </para>
///
/// <para>
/// Configuration precedence (highest to lowest):
/// <list type="number">
///   <item>Environment variables (TYPHON__TELEMETRY__ENABLED, etc.)</item>
///   <item>typhon.telemetry.json in current directory</item>
///   <item>typhon.telemetry.json next to the assembly</item>
///   <item>Built-in defaults (all disabled)</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Environment variable naming uses double underscore (<c>__</c>) as hierarchy separator
/// for cross-platform compatibility:
/// <code>
/// TYPHON__TELEMETRY__ENABLED=true
/// TYPHON__TELEMETRY__ACCESSCONTROL__ENABLED=true
/// TYPHON__TELEMETRY__PAGEDMMF__TRACKIOOPERATIONS=false
/// </code>
/// </remarks>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class TelemetryConfig
{
    // ═══════════════════════════════════════════════════════════════════════════
    // MASTER SWITCH
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Global telemetry enable/disable. When false, all telemetry is disabled
    /// regardless of individual component settings.
    /// </summary>
    public static readonly bool Enabled;

    // ═══════════════════════════════════════════════════════════════════════════
    // ACCESS CONTROL TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether AccessControl component telemetry is enabled in configuration.
    /// </summary>
    public static readonly bool AccessControlEnabled;

    /// <summary>
    /// Whether to track contention events (thread waiting for lock).
    /// </summary>
    public static readonly bool AccessControlTrackContention;

    /// <summary>
    /// Whether to track contention wait duration (requires Stopwatch overhead).
    /// </summary>
    public static readonly bool AccessControlTrackContentionDuration;

    /// <summary>
    /// Whether to track shared vs exclusive access patterns.
    /// </summary>
    public static readonly bool AccessControlTrackAccessPatterns;

    /// <summary>
    /// Combined flag: true only if global AND component telemetry are enabled.
    /// Use this single check in hot paths for minimal branching.
    /// </summary>
    public static readonly bool AccessControlActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // PAGED MMF TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether PagedMMF component telemetry is enabled in configuration.
    /// </summary>
    public static readonly bool PagedMMFEnabled;

    /// <summary>
    /// Whether to track page allocation events.
    /// </summary>
    public static readonly bool PagedMMFTrackPageAllocations;

    /// <summary>
    /// Whether to track page eviction events.
    /// </summary>
    public static readonly bool PagedMMFTrackPageEvictions;

    /// <summary>
    /// Whether to track I/O read/write operations.
    /// </summary>
    public static readonly bool PagedMMFTrackIOOperations;

    /// <summary>
    /// Whether to track cache hit/miss ratios.
    /// </summary>
    public static readonly bool PagedMMFTrackCacheHitRatio;

    /// <summary>
    /// Combined flag: true only if global AND component telemetry are enabled.
    /// </summary>
    public static readonly bool PagedMMFActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // BTREE TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether BTree component telemetry is enabled in configuration.
    /// </summary>
    public static readonly bool BTreeEnabled;

    /// <summary>
    /// Whether to track node split operations.
    /// </summary>
    public static readonly bool BTreeTrackNodeSplits;

    /// <summary>
    /// Whether to track node merge operations.
    /// </summary>
    public static readonly bool BTreeTrackNodeMerges;

    /// <summary>
    /// Whether to track search depth statistics.
    /// </summary>
    public static readonly bool BTreeTrackSearchDepth;

    /// <summary>
    /// Whether to track key comparison counts (high overhead).
    /// </summary>
    public static readonly bool BTreeTrackKeyComparisons;

    // ═══════════════════════════════════════════════════════════════════════════
    // TRANSACTION TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether Transaction component telemetry is enabled in configuration.
    /// </summary>
    public static readonly bool TransactionEnabled;

    /// <summary>
    /// Whether to track commit/rollback operations.
    /// </summary>
    public static readonly bool TransactionTrackCommitRollback;

    /// <summary>
    /// Whether to track concurrency conflicts.
    /// </summary>
    public static readonly bool TransactionTrackConflicts;

    /// <summary>
    /// Whether to track transaction duration.
    /// </summary>
    public static readonly bool TransactionTrackDuration;

    /// <summary>
    /// Combined flag: true only if global AND component telemetry are enabled.
    /// </summary>
    public static readonly bool TransactionActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // SPATIAL INDEX TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Whether spatial index telemetry is enabled in configuration.</summary>
    public static readonly bool SpatialEnabled;

    /// <summary>Combined flag: true only if global AND spatial telemetry are enabled. Use in spatial hot paths.</summary>
    public static readonly bool SpatialActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // SCHEDULER TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Whether Scheduler component telemetry is enabled in configuration.</summary>
    public static readonly bool SchedulerEnabled;

    /// <summary>Whether to track per-worker active/idle time breakdown.</summary>
    public static readonly bool SchedulerTrackWorkerUtilization;

    /// <summary>Whether to track straggler gap (parallel efficiency metric for Patate systems).</summary>
    public static readonly bool SchedulerTrackStragglerGap;

    /// <summary>
    /// Combined flag: true only if global AND scheduler telemetry are enabled.
    /// Gates deep metrics (straggler gap, per-worker utilization). The ring buffer itself is always on.
    /// </summary>
    public static readonly bool SchedulerActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // PROFILER (#243)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether the general-purpose profiler is enabled in configuration.
    /// </summary>
    public static readonly bool ProfilerEnabled;

    /// <summary>
    /// Combined flag: true only if global telemetry AND the profiler are enabled.
    /// </summary>
    /// <remarks>
    /// This is the single hot-path gate on every <c>TyphonEvent.BeginSpan</c> producer call. When <c>false</c>, the JIT folds
    /// <c>if (!TelemetryConfig.ProfilerActive) return default;</c> into a no-op on Tier 1 compilation, so the entire profiler producer
    /// path costs zero CPU when disabled. Same JIT-elimination pattern as <see cref="EcsActive"/>/<see cref="BTreeActive"/>.
    /// <para>
    /// Note that <c>TyphonProfiler.Start/Stop</c> only controls consumer + exporter lifecycle — not this gate. The gate is set once at
    /// class load from the config file and never changes for the life of the process.
    /// </para>
    /// </remarks>
    public static readonly bool ProfilerActive;

    /// <summary>
    /// Whether opt-in .NET runtime GC-event tracing is requested by configuration. Raw flag — use <see cref="ProfilerGcTracingActive"/>
    /// for the combined gate that also requires the global and profiler master switches.
    /// </summary>
    public static readonly bool ProfilerGcTracingEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="Enabled"/>, <see cref="ProfilerEnabled"/>, AND <see cref="ProfilerGcTracingEnabled"/> are all set.
    /// Read once by <c>TyphonProfiler.Start</c> to decide whether to subscribe the <see cref="System.Diagnostics.Tracing.EventListener"/> and
    /// spin up the GC ingestion thread. When <c>false</c>, none of the GC-tracing machinery is constructed.
    /// </summary>
    public static readonly bool ProfilerGcTracingActive;

    /// <summary>
    /// Whether opt-in per-allocation tracking is requested by configuration. Raw flag — use <see cref="ProfilerMemoryAllocationsActive"/>
    /// for the combined gate.
    /// </summary>
    public static readonly bool ProfilerMemoryAllocationsEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="Enabled"/>, <see cref="ProfilerEnabled"/>, AND <see cref="ProfilerMemoryAllocationsEnabled"/> are set.
    /// Gates emission of <c>TraceEventKind.MemoryAllocEvent</c> from <c>PinnedMemoryBlock</c> construct/dispose. When <c>false</c>, the JIT
    /// folds the emission branch into dead code on Tier 1 compilation — zero cost at the call site.
    /// </summary>
    public static readonly bool ProfilerMemoryAllocationsActive;

    /// <summary>
    /// Whether opt-in per-tick gauge snapshots are requested by configuration. Raw flag — use <see cref="ProfilerGaugesActive"/> for the
    /// combined gate.
    /// </summary>
    public static readonly bool ProfilerGaugesEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="Enabled"/>, <see cref="ProfilerEnabled"/>, AND <see cref="ProfilerGaugesEnabled"/> are set.
    /// Gates emission of <c>TraceEventKind.PerTickSnapshot</c> from the scheduler's end-of-tick telemetry phase and all gauge-reading work
    /// (page cache bucketing walk, GC memory-info sampling, counter reads). When <c>false</c>, the entire chain is dead code at Tier 1.
    /// </summary>
    public static readonly bool ProfilerGaugesActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION SOURCE TRACKING (for diagnostics)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The configuration file path that was loaded, or null if using defaults/env vars only.
    /// </summary>
    public static readonly string LoadedConfigurationFile;

    // ═══════════════════════════════════════════════════════════════════════════
    // STATIC CONSTRUCTOR - Runs once on first access to any static member
    // ═══════════════════════════════════════════════════════════════════════════

    static TelemetryConfig()
    {
        var (config, configPath) = BuildConfiguration();
        LoadedConfigurationFile = configPath;

        var section = config.GetSection("Typhon:Telemetry");

        // Master switch
        Enabled = section.GetValue("Enabled", false);

        // AccessControl
        var acSection = section.GetSection("AccessControl");
        AccessControlEnabled = acSection.GetValue("Enabled", false);
        AccessControlTrackContention = acSection.GetValue("TrackContention", true);
        AccessControlTrackContentionDuration = acSection.GetValue("TrackContentionDuration", true);
        AccessControlTrackAccessPatterns = acSection.GetValue("TrackAccessPatterns", true);
        AccessControlActive = Enabled && AccessControlEnabled;

        // PagedMMF
        var mmfSection = section.GetSection("PagedMMF");
        PagedMMFEnabled = mmfSection.GetValue("Enabled", false);
        PagedMMFTrackPageAllocations = mmfSection.GetValue("TrackPageAllocations", true);
        PagedMMFTrackPageEvictions = mmfSection.GetValue("TrackPageEvictions", true);
        PagedMMFTrackIOOperations = mmfSection.GetValue("TrackIOOperations", true);
        PagedMMFTrackCacheHitRatio = mmfSection.GetValue("TrackCacheHitRatio", true);
        PagedMMFActive = Enabled && PagedMMFEnabled;

        // BTree
        var btreeSection = section.GetSection("BTree");
        BTreeTrackNodeSplits = btreeSection.GetValue("TrackNodeSplits", true);
        BTreeTrackNodeMerges = btreeSection.GetValue("TrackNodeMerges", true);
        BTreeTrackSearchDepth = btreeSection.GetValue("TrackSearchDepth", true);
        BTreeTrackKeyComparisons = btreeSection.GetValue("TrackKeyComparisons", false);

        // Transaction
        var txSection = section.GetSection("Transaction");
        TransactionEnabled = txSection.GetValue("Enabled", false);
        TransactionTrackCommitRollback = txSection.GetValue("TrackCommitRollback", true);
        TransactionTrackConflicts = txSection.GetValue("TrackConflicts", true);
        TransactionTrackDuration = txSection.GetValue("TrackDuration", true);
        TransactionActive = Enabled && TransactionEnabled;

        // Spatial Index
        var spatialSection = section.GetSection("Spatial");
        SpatialEnabled = spatialSection.GetValue("Enabled", false);
        SpatialActive = Enabled && SpatialEnabled;

        // Scheduler
        var schedSection = section.GetSection("Scheduler");
        SchedulerEnabled = schedSection.GetValue("Enabled", false);
        SchedulerTrackWorkerUtilization = schedSection.GetValue("TrackWorkerUtilization", true);
        SchedulerTrackStragglerGap = schedSection.GetValue("TrackStragglerGap", true);
        SchedulerActive = Enabled && SchedulerEnabled;

        // Profiler (#243)
        var profilerSection = section.GetSection("Profiler");
        ProfilerEnabled = profilerSection.GetValue("Enabled", false);
        ProfilerActive = Enabled && ProfilerEnabled;

        // Profiler — opt-in GC event tracing (.NET runtime GC events → typed-event pipeline)
        var gcTracingSection = profilerSection.GetSection("GcTracing");
        ProfilerGcTracingEnabled = gcTracingSection.GetValue("Enabled", false);
        ProfilerGcTracingActive = ProfilerActive && ProfilerGcTracingEnabled;

        // Profiler — opt-in per-allocation tracking (PinnedMemoryBlock alloc/free → MemoryAllocEvent)
        var memAllocSection = profilerSection.GetSection("MemoryAllocations");
        ProfilerMemoryAllocationsEnabled = memAllocSection.GetValue("Enabled", false);
        ProfilerMemoryAllocationsActive = ProfilerActive && ProfilerMemoryAllocationsEnabled;

        // Profiler — opt-in per-tick gauge snapshots (scheduler end-of-tick → PerTickSnapshot)
        var gaugesSection = profilerSection.GetSection("Gauges");
        ProfilerGaugesEnabled = gaugesSection.GetValue("Enabled", false);
        ProfilerGaugesActive = ProfilerActive && ProfilerGaugesEnabled;
    }

    private static (IConfiguration config, string loadedPath) BuildConfiguration()
    {
        var builder = new ConfigurationBuilder();
        string loadedPath = null;

        // 1. Look for config file in current directory
        var currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), "typhon.telemetry.json");
        if (File.Exists(currentDirPath))
        {
            builder.AddJsonFile(currentDirPath, optional: true, reloadOnChange: false);
            loadedPath = currentDirPath;
        }

        // 2. Look for config file next to the assembly (fallback)
        var assemblyLocation = typeof(TelemetryConfig).Assembly.Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var assemblyConfigPath = Path.Combine(assemblyDir, "typhon.telemetry.json");
                if (File.Exists(assemblyConfigPath) && assemblyConfigPath != currentDirPath)
                {
                    builder.AddJsonFile(assemblyConfigPath, optional: true, reloadOnChange: false);
                    loadedPath ??= assemblyConfigPath;
                }
            }
        }

        // 3. Environment variables override everything
        // Uses __ as hierarchy separator: TYPHON__TELEMETRY__ENABLED -> Typhon:Telemetry:Enabled
        builder.AddEnvironmentVariables();

        return (builder.Build(), loadedPath);
    }

    /// <summary>
    /// Forces early initialization of telemetry configuration.
    /// Call this at application startup to ensure the JIT compiler sees the
    /// readonly field values before compiling hot paths.
    /// </summary>
    /// <remarks>
    /// The <see cref="MethodImplOptions.NoInlining"/> attribute ensures this method
    /// is actually called and not optimized away, guaranteeing the static constructor runs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureInitialized() =>
        // Accessing any static field triggers the static constructor.
        // The NoInlining attribute ensures this method call isn't optimized away.
        _ = Enabled;

    /// <summary>
    /// Returns a human-readable summary of the current telemetry configuration.
    /// Useful for logging at application startup.
    /// </summary>
    /// <returns>A multi-line string describing all telemetry settings.</returns>
    public static string GetConfigurationSummary() =>
        $"""
         Typhon Telemetry Configuration:
           Config File: {LoadedConfigurationFile ?? "(none - using defaults/env vars)"}
           Master Enabled: {Enabled}

           AccessControl: Active={AccessControlActive}
             Enabled={AccessControlEnabled}, Contention={AccessControlTrackContention},
             Duration={AccessControlTrackContentionDuration}, Patterns={AccessControlTrackAccessPatterns}

           PagedMMF: Active={PagedMMFActive}
             Enabled={PagedMMFEnabled}, Allocations={PagedMMFTrackPageAllocations},
             Evictions={PagedMMFTrackPageEvictions}, IO={PagedMMFTrackIOOperations}, CacheRatio={PagedMMFTrackCacheHitRatio}

           BTree:
             Splits={BTreeTrackNodeSplits}, Merges={BTreeTrackNodeMerges},
             Depth={BTreeTrackSearchDepth}, Comparisons={BTreeTrackKeyComparisons}

           Transaction: Active={TransactionActive}
             Enabled={TransactionEnabled}, CommitRollback={TransactionTrackCommitRollback},
             Conflicts={TransactionTrackConflicts}, Duration={TransactionTrackDuration}

           Spatial: Active={SpatialActive}
             Enabled={SpatialEnabled}

           Scheduler: Active={SchedulerActive}
             Enabled={SchedulerEnabled}, WorkerUtilization={SchedulerTrackWorkerUtilization},
             StragglerGap={SchedulerTrackStragglerGap}

           Profiler: Active={ProfilerActive}
             Enabled={ProfilerEnabled}, GcTracing={ProfilerGcTracingEnabled} (Active={ProfilerGcTracingActive}),
             MemoryAllocations={ProfilerMemoryAllocationsEnabled} (Active={ProfilerMemoryAllocationsActive}),
             Gauges={ProfilerGaugesEnabled} (Active={ProfilerGaugesActive})
         """;

    /// <summary>
    /// Returns a concise one-line summary of active telemetry components.
    /// </summary>
    public static string GetActiveComponentsSummary()
    {
        if (!Enabled)
        {
            return "Telemetry: Disabled";
        }

        var active = new System.Collections.Generic.List<string>();
        if (AccessControlActive)
        {
            active.Add("AccessControl");
        }

        if (PagedMMFActive)
        {
            active.Add("PagedMMF");
        }

        if (TransactionActive)
        {
            active.Add("Transaction");
        }

        if (SpatialActive)
        {
            active.Add("Spatial");
        }

        if (SchedulerActive)
        {
            active.Add("Scheduler");
        }

        if (ProfilerActive)
        {
            var suffix = new System.Collections.Generic.List<string>();
            if (ProfilerGcTracingActive)
            {
                suffix.Add("GcTracing");
            }
            if (ProfilerMemoryAllocationsActive)
            {
                suffix.Add("MemoryAllocations");
            }
            if (ProfilerGaugesActive)
            {
                suffix.Add("Gauges");
            }
            active.Add(suffix.Count > 0 ? $"Profiler+{string.Join("+", suffix)}" : "Profiler");
        }

        return active.Count > 0 ? $"Telemetry: Enabled [{string.Join(", ", active)}]" : "Telemetry: Enabled (no components active)";
    }
}
