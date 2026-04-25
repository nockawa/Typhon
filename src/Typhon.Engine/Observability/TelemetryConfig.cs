// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Global telemetry configuration for Typhon Engine.
///
/// <para>
/// This class provides <c>static readonly</c> fields that allow the JIT compiler to
/// eliminate disabled telemetry code paths entirely. When a readonly field is <c>false</c>,
/// the JIT can treat <c>if (TelemetryConfig.ProfilerActive)</c> as dead code and
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
///   <item>Environment variables (TYPHON__PROFILER__ENABLED, etc.)</item>
///   <item>typhon.telemetry.json in current directory</item>
///   <item>typhon.telemetry.json next to the assembly</item>
///   <item>Built-in defaults (all disabled)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Namespace migration (Phase 0):</b> the configuration namespace was flattened from
/// <c>Typhon:Telemetry:Profiler:*</c> to <c>Typhon:Profiler:*</c>. The legacy paths are still
/// read via a back-compat shim in this release; the shim emits a deprecation warning to
/// <c>Console.Error</c> when activated and will be removed in the next minor. See
/// <see cref="LegacyConfigDetected"/>.
/// </para>
/// </summary>
/// <remarks>
/// Environment variable naming uses double underscore (<c>__</c>) as hierarchy separator
/// for cross-platform compatibility:
/// <code>
/// TYPHON__PROFILER__ENABLED=true
/// TYPHON__PROFILER__GCTRACING__ENABLED=true
/// TYPHON__PROFILER__SCHEDULER__GAUGES__STRAGGLERGAP__ENABLED=true
/// </code>
/// The legacy paths (<c>TYPHON__TELEMETRY__PROFILER__*</c>) are also accepted for one release.
/// </remarks>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class TelemetryConfig
{
    // ═══════════════════════════════════════════════════════════════════════════
    // MASTER SWITCH
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Global profiler enable/disable. When false, all profiler-emitted telemetry is disabled
    /// regardless of individual component settings.
    /// </summary>
    /// <remarks>
    /// Reads from <c>Typhon:Profiler:Enabled</c>. The back-compat shim recognises
    /// <c>Typhon:Telemetry:Enabled AND Typhon:Telemetry:Profiler:Enabled</c> as the legacy
    /// equivalent (both must be <c>true</c>).
    /// </remarks>
    public static readonly bool Enabled;

    // ═══════════════════════════════════════════════════════════════════════════
    // SCHEDULER TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Whether Scheduler component telemetry is enabled in configuration. Reads from <c>Typhon:Profiler:Scheduler:Enabled</c>.</summary>
    public static readonly bool SchedulerEnabled;

    /// <summary>Whether to track per-system transition latency. Reads from <c>Typhon:Profiler:Scheduler:Gauges:TransitionLatency:Enabled</c>.</summary>
    public static readonly bool SchedulerTrackTransitionLatency;

    /// <summary>Whether to track per-worker active/idle time breakdown. Reads from <c>Typhon:Profiler:Scheduler:Gauges:WorkerUtilization:Enabled</c>.</summary>
    public static readonly bool SchedulerTrackWorkerUtilization;

    /// <summary>Whether to track straggler gap (parallel efficiency metric for Patate systems). Reads from <c>Typhon:Profiler:Scheduler:Gauges:StragglerGap:Enabled</c>.</summary>
    public static readonly bool SchedulerTrackStragglerGap;

    /// <summary>
    /// Combined flag: true only if the new master AND scheduler telemetry are enabled.
    /// Gates deep metrics (straggler gap, per-worker utilization). The ring buffer itself is always on.
    /// </summary>
    public static readonly bool SchedulerActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // PROFILER (#243)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether the general-purpose profiler is enabled in configuration.
    /// </summary>
    /// <remarks>
    /// In the new namespace, <c>ProfilerEnabled</c> is identical to <see cref="Enabled"/> — the master
    /// switch <i>is</i> the profiler enable. The two fields are kept distinct for source-compatibility
    /// with code that reads either name.
    /// </remarks>
    public static readonly bool ProfilerEnabled;

    /// <summary>
    /// Combined flag: true if the profiler master switch is on. Identical to <see cref="Enabled"/> in the new namespace.
    /// </summary>
    /// <remarks>
    /// This is the single hot-path gate on every <c>TyphonEvent.BeginSpan</c> producer call. When <c>false</c>, the JIT folds
    /// <c>if (!TelemetryConfig.ProfilerActive) return default;</c> into a no-op on Tier 1 compilation, so the entire profiler producer
    /// path costs zero CPU when disabled.
    /// <para>
    /// Note that <c>TyphonProfiler.Start/Stop</c> only controls consumer + exporter lifecycle — not this gate. The gate is set once at
    /// class load from the config file and never changes for the life of the process.
    /// </para>
    /// </remarks>
    public static readonly bool ProfilerActive;

    /// <summary>
    /// Whether opt-in .NET runtime GC-event tracing is requested by configuration. Reads from <c>Typhon:Profiler:GcTracing:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerGcTracingEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerGcTracingEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerGcTracingActive;

    /// <summary>
    /// Whether opt-in per-allocation tracking is requested by configuration. Reads from <c>Typhon:Profiler:MemoryAllocations:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerMemoryAllocationsEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerMemoryAllocationsEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerMemoryAllocationsActive;

    /// <summary>
    /// Whether opt-in per-tick gauge snapshots are requested by configuration. Reads from <c>Typhon:Profiler:Gauges:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerGaugesEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerGaugesEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerGaugesActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONCURRENCY (Phase 1 + Phase 2) — leaf flags for AccessControl, AccessControlSmall,
    // ResourceAccessControl, Epoch, AdaptiveWaiter, OlcLatch.
    //
    // All default false. Parent-implies-children semantics via TelemetryConfigResolver:
    // flipping Profiler:Concurrency:Enabled = true turns the whole subtree on;
    // per-leaf Enabled keys override.
    // ═══════════════════════════════════════════════════════════════════════════

    // ── Root + sub-tree parents ────────────────────────────────────────────────

    /// <summary>Combined Concurrency root gate: master <see cref="Enabled"/> AND <c>Typhon:Profiler:Concurrency:Enabled</c>.</summary>
    public static readonly bool ConcurrencyActive;

    /// <summary>Combined gate for the AccessControl subtree.</summary>
    public static readonly bool ConcurrencyAccessControlActive;

    /// <summary>Combined gate for the AccessControlSmall subtree.</summary>
    public static readonly bool ConcurrencyAccessControlSmallActive;

    /// <summary>Combined gate for the ResourceAccessControl subtree.</summary>
    public static readonly bool ConcurrencyResourceAccessControlActive;

    /// <summary>Combined gate for the Epoch subtree.</summary>
    public static readonly bool ConcurrencyEpochActive;

    /// <summary>Combined gate for the AdaptiveWaiter subtree.</summary>
    public static readonly bool ConcurrencyAdaptiveWaiterActive;

    /// <summary>Combined gate for the OlcLatch subtree.</summary>
    public static readonly bool ConcurrencyOlcLatchActive;

    // ── AccessControl leaves ───────────────────────────────────────────────────

    /// <summary>Combined gate for AccessControl shared-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlSharedAcquireActive;

    /// <summary>Combined gate for AccessControl shared-release events.</summary>
    public static readonly bool ConcurrencyAccessControlSharedReleaseActive;

    /// <summary>Combined gate for AccessControl exclusive-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlExclusiveAcquireActive;

    /// <summary>Combined gate for AccessControl exclusive-release events.</summary>
    public static readonly bool ConcurrencyAccessControlExclusiveReleaseActive;

    /// <summary>Combined gate for AccessControl shared↔exclusive promotion/demotion events.</summary>
    public static readonly bool ConcurrencyAccessControlPromotionActive;

    /// <summary>Combined gate for AccessControl contention markers.</summary>
    public static readonly bool ConcurrencyAccessControlContentionActive;

    // ── AccessControlSmall leaves ──────────────────────────────────────────────

    /// <summary>Combined gate for AccessControlSmall shared-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallSharedAcquireActive;

    /// <summary>Combined gate for AccessControlSmall shared-release events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallSharedReleaseActive;

    /// <summary>Combined gate for AccessControlSmall exclusive-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallExclusiveAcquireActive;

    /// <summary>Combined gate for AccessControlSmall exclusive-release events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallExclusiveReleaseActive;

    /// <summary>Combined gate for AccessControlSmall contention markers.</summary>
    public static readonly bool ConcurrencyAccessControlSmallContentionActive;

    // ── ResourceAccessControl leaves ───────────────────────────────────────────

    /// <summary>Combined gate for ResourceAccessControl Accessing-mode acquire events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlAccessingActive;

    /// <summary>Combined gate for ResourceAccessControl Modify-mode acquire events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlModifyActive;

    /// <summary>Combined gate for ResourceAccessControl Destroy-mode acquire events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlDestroyActive;

    /// <summary>Combined gate for ResourceAccessControl Modify-promotion slow-path events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlModifyPromotionActive;

    /// <summary>Combined gate for ResourceAccessControl contention markers.</summary>
    public static readonly bool ConcurrencyResourceAccessControlContentionActive;

    // ── Epoch leaves ───────────────────────────────────────────────────────────

    /// <summary>Combined gate for EpochGuard Enter (PinCurrentThread) events.</summary>
    public static readonly bool ConcurrencyEpochScopeEnterActive;

    /// <summary>Combined gate for EpochGuard Dispose events.</summary>
    public static readonly bool ConcurrencyEpochScopeExitActive;

    /// <summary>Combined gate for GlobalEpoch advance events.</summary>
    public static readonly bool ConcurrencyEpochAdvanceActive;

    /// <summary>Combined gate for RefreshScope events.</summary>
    public static readonly bool ConcurrencyEpochRefreshActive;

    /// <summary>Combined gate for EpochThreadRegistry slot-claim events.</summary>
    public static readonly bool ConcurrencyEpochSlotClaimActive;

    /// <summary>Combined gate for EpochThreadRegistry dead-thread slot-reclaim events.</summary>
    public static readonly bool ConcurrencyEpochSlotReclaimActive;

    // ── AdaptiveWaiter leaves ──────────────────────────────────────────────────

    /// <summary>Combined gate for AdaptiveWaiter yield-or-sleep transition events. Phase 2 design (#280) chose transitions only — NOT per-spin — to keep trace volume sane.</summary>
    public static readonly bool ConcurrencyAdaptiveWaiterYieldOrSleepActive;

    // ── OlcLatch leaves ────────────────────────────────────────────────────────

    /// <summary>Combined gate for OlcLatch TryWriteLock-failure events.</summary>
    public static readonly bool ConcurrencyOlcLatchWriteLockAttemptActive;

    /// <summary>Combined gate for OlcLatch WriteUnlock events.</summary>
    public static readonly bool ConcurrencyOlcLatchWriteUnlockActive;

    /// <summary>Combined gate for OlcLatch MarkObsolete events.</summary>
    public static readonly bool ConcurrencyOlcLatchMarkObsoleteActive;

    /// <summary>Combined gate for OlcLatch ValidateVersion-failure events.</summary>
    public static readonly bool ConcurrencyOlcLatchValidationFailActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION SOURCE TRACKING (for diagnostics)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The configuration file path that was loaded, or null if using defaults/env vars only.
    /// </summary>
    public static readonly string LoadedConfigurationFile;

    /// <summary>
    /// True if any value was read from the legacy <c>Typhon:Telemetry:*</c> namespace via the back-compat shim,
    /// or if any legacy key was present in the loaded configuration. A deprecation warning is emitted to
    /// <c>Console.Error</c> at static-class load when this flag is set.
    /// </summary>
    public static readonly bool LegacyConfigDetected;

    // ═══════════════════════════════════════════════════════════════════════════
    // STATIC CONSTRUCTOR - Runs once on first access to any static member
    // ═══════════════════════════════════════════════════════════════════════════

    static TelemetryConfig()
    {
        var (config, configPath) = BuildConfiguration();
        LoadedConfigurationFile = configPath;

        var legacyDetected = false;

        // ─── Master switch ─────────────────────────────────────────────────────
        // New: Typhon:Profiler:Enabled is the single master.
        // Legacy: required Typhon:Telemetry:Enabled AND Typhon:Telemetry:Profiler:Enabled (both must be true).
        Enabled = ReadMasterEnabled(config, ref legacyDetected);
        ProfilerEnabled = Enabled;
        ProfilerActive = Enabled;

        // ─── Profiler children (live) ──────────────────────────────────────────
        ProfilerGcTracingEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:GcTracing:Enabled",
            "Typhon:Telemetry:Profiler:GcTracing:Enabled",
            false, ref legacyDetected);
        ProfilerGcTracingActive = ProfilerActive && ProfilerGcTracingEnabled;

        ProfilerMemoryAllocationsEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:MemoryAllocations:Enabled",
            "Typhon:Telemetry:Profiler:MemoryAllocations:Enabled",
            false, ref legacyDetected);
        ProfilerMemoryAllocationsActive = ProfilerActive && ProfilerMemoryAllocationsEnabled;

        ProfilerGaugesEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:Gauges:Enabled",
            "Typhon:Telemetry:Profiler:Gauges:Enabled",
            false, ref legacyDetected);
        ProfilerGaugesActive = ProfilerActive && ProfilerGaugesEnabled;

        // ─── Scheduler (live) ──────────────────────────────────────────────────
        // Legacy keys had a structural shape (Telemetry:Scheduler:Track*) — the back-compat fallback
        // remaps them to the new Profiler:Scheduler:Gauges:* tree.
        SchedulerEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Enabled",
            "Typhon:Telemetry:Scheduler:Enabled",
            false, ref legacyDetected);
        SchedulerActive = Enabled && SchedulerEnabled;

        SchedulerTrackTransitionLatency = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Gauges:TransitionLatency:Enabled",
            "Typhon:Telemetry:Scheduler:TrackTransitionLatency",
            true, ref legacyDetected);
        SchedulerTrackWorkerUtilization = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Gauges:WorkerUtilization:Enabled",
            "Typhon:Telemetry:Scheduler:TrackWorkerUtilization",
            true, ref legacyDetected);
        SchedulerTrackStragglerGap = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Gauges:StragglerGap:Enabled",
            "Typhon:Telemetry:Scheduler:TrackStragglerGap",
            true, ref legacyDetected);

        // ─── Concurrency subtree (Phase 1 + Phase 2 final shape) ───────────────
        // Greenfield namespace — no legacy fallback (the dead Typhon:Telemetry:* tree had no Concurrency concept).
        // Resolver implements parent-implies-children: Concurrency:Enabled = false makes all leaves false even if
        // their own Enabled key is true; Concurrency:Enabled = true (with master Profiler on) flips everything on
        // by default, with per-leaf overrides.
        var concurrencyTree = new Node("Concurrency",
        [
            new Node("AccessControl",
            [
                new Node("SharedAcquire"),
                new Node("SharedRelease"),
                new Node("ExclusiveAcquire"),
                new Node("ExclusiveRelease"),
                new Node("Promotion"),
                new Node("Contention"),
            ]),
            new Node("AccessControlSmall",
            [
                new Node("SharedAcquire"),
                new Node("SharedRelease"),
                new Node("ExclusiveAcquire"),
                new Node("ExclusiveRelease"),
                new Node("Contention"),
            ]),
            new Node("ResourceAccessControl",
            [
                new Node("Accessing"),
                new Node("Modify"),
                new Node("Destroy"),
                new Node("ModifyPromotion"),
                new Node("Contention"),
            ]),
            new Node("Epoch",
            [
                new Node("ScopeEnter"),
                new Node("ScopeExit"),
                new Node("Advance"),
                new Node("Refresh"),
                new Node("SlotClaim"),
                new Node("SlotReclaim"),
            ]),
            new Node("AdaptiveWaiter",
            [
                new Node("YieldOrSleep"),
            ]),
            new Node("OlcLatch",
            [
                new Node("WriteLockAttempt"),
                new Node("WriteUnlock"),
                new Node("MarkObsolete"),
                new Node("ValidationFail"),
            ]),
        ]);
        var concurrencyRootExplicit = ReadBool(config, "Typhon:Profiler:Concurrency:Enabled", false);
        var concurrencyRootEffective = Enabled && concurrencyRootExplicit;
        var concurrencyMap = TelemetryConfigResolver.Resolve(
            concurrencyTree, concurrencyRootEffective, config, "Typhon:Profiler");

        // Root + sub-tree parents
        ConcurrencyActive                          = concurrencyMap["Concurrency"];
        ConcurrencyAccessControlActive             = concurrencyMap["Concurrency:AccessControl"];
        ConcurrencyAccessControlSmallActive        = concurrencyMap["Concurrency:AccessControlSmall"];
        ConcurrencyResourceAccessControlActive     = concurrencyMap["Concurrency:ResourceAccessControl"];
        ConcurrencyEpochActive                     = concurrencyMap["Concurrency:Epoch"];
        ConcurrencyAdaptiveWaiterActive            = concurrencyMap["Concurrency:AdaptiveWaiter"];
        ConcurrencyOlcLatchActive                  = concurrencyMap["Concurrency:OlcLatch"];

        // AccessControl leaves
        ConcurrencyAccessControlSharedAcquireActive    = concurrencyMap["Concurrency:AccessControl:SharedAcquire"];
        ConcurrencyAccessControlSharedReleaseActive    = concurrencyMap["Concurrency:AccessControl:SharedRelease"];
        ConcurrencyAccessControlExclusiveAcquireActive = concurrencyMap["Concurrency:AccessControl:ExclusiveAcquire"];
        ConcurrencyAccessControlExclusiveReleaseActive = concurrencyMap["Concurrency:AccessControl:ExclusiveRelease"];
        ConcurrencyAccessControlPromotionActive        = concurrencyMap["Concurrency:AccessControl:Promotion"];
        ConcurrencyAccessControlContentionActive       = concurrencyMap["Concurrency:AccessControl:Contention"];

        // AccessControlSmall leaves
        ConcurrencyAccessControlSmallSharedAcquireActive    = concurrencyMap["Concurrency:AccessControlSmall:SharedAcquire"];
        ConcurrencyAccessControlSmallSharedReleaseActive    = concurrencyMap["Concurrency:AccessControlSmall:SharedRelease"];
        ConcurrencyAccessControlSmallExclusiveAcquireActive = concurrencyMap["Concurrency:AccessControlSmall:ExclusiveAcquire"];
        ConcurrencyAccessControlSmallExclusiveReleaseActive = concurrencyMap["Concurrency:AccessControlSmall:ExclusiveRelease"];
        ConcurrencyAccessControlSmallContentionActive       = concurrencyMap["Concurrency:AccessControlSmall:Contention"];

        // ResourceAccessControl leaves
        ConcurrencyResourceAccessControlAccessingActive       = concurrencyMap["Concurrency:ResourceAccessControl:Accessing"];
        ConcurrencyResourceAccessControlModifyActive          = concurrencyMap["Concurrency:ResourceAccessControl:Modify"];
        ConcurrencyResourceAccessControlDestroyActive         = concurrencyMap["Concurrency:ResourceAccessControl:Destroy"];
        ConcurrencyResourceAccessControlModifyPromotionActive = concurrencyMap["Concurrency:ResourceAccessControl:ModifyPromotion"];
        ConcurrencyResourceAccessControlContentionActive      = concurrencyMap["Concurrency:ResourceAccessControl:Contention"];

        // Epoch leaves
        ConcurrencyEpochScopeEnterActive  = concurrencyMap["Concurrency:Epoch:ScopeEnter"];
        ConcurrencyEpochScopeExitActive   = concurrencyMap["Concurrency:Epoch:ScopeExit"];
        ConcurrencyEpochAdvanceActive     = concurrencyMap["Concurrency:Epoch:Advance"];
        ConcurrencyEpochRefreshActive     = concurrencyMap["Concurrency:Epoch:Refresh"];
        ConcurrencyEpochSlotClaimActive   = concurrencyMap["Concurrency:Epoch:SlotClaim"];
        ConcurrencyEpochSlotReclaimActive = concurrencyMap["Concurrency:Epoch:SlotReclaim"];

        // AdaptiveWaiter leaves
        ConcurrencyAdaptiveWaiterYieldOrSleepActive = concurrencyMap["Concurrency:AdaptiveWaiter:YieldOrSleep"];

        // OlcLatch leaves
        ConcurrencyOlcLatchWriteLockAttemptActive = concurrencyMap["Concurrency:OlcLatch:WriteLockAttempt"];
        ConcurrencyOlcLatchWriteUnlockActive      = concurrencyMap["Concurrency:OlcLatch:WriteUnlock"];
        ConcurrencyOlcLatchMarkObsoleteActive     = concurrencyMap["Concurrency:OlcLatch:MarkObsolete"];
        ConcurrencyOlcLatchValidationFailActive   = concurrencyMap["Concurrency:OlcLatch:ValidationFail"];

        // ─── Legacy-presence detection ─────────────────────────────────────────
        // Even if no fallback fired (e.g., user has only dead-family keys with no live consumers),
        // any populated Typhon:Telemetry:* subtree warrants the deprecation warning.
        if (!legacyDetected && config.GetSection("Typhon:Telemetry").GetChildren().Any())
        {
            legacyDetected = true;
        }

        LegacyConfigDetected = legacyDetected;

        if (legacyDetected)
        {
            EmitDeprecationWarning();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIG READING HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool ReadBool(IConfiguration config, string key, bool defaultValue)
    {
        var v = config[key];
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        return bool.TryParse(v, out var b) ? b : defaultValue;
    }

    private static bool ReadBoolFallback(
        IConfiguration config,
        string newKey,
        string oldKey,
        bool defaultValue,
        ref bool legacyDetected)
    {
        var newVal = config[newKey];
        if (!string.IsNullOrEmpty(newVal))
        {
            return bool.TryParse(newVal, out var b) ? b : defaultValue;
        }

        var oldVal = config[oldKey];
        if (!string.IsNullOrEmpty(oldVal))
        {
            legacyDetected = true;
            return bool.TryParse(oldVal, out var b) ? b : defaultValue;
        }

        return defaultValue;
    }

    private static bool ReadMasterEnabled(IConfiguration config, ref bool legacyDetected)
    {
        // Prefer the new namespace.
        var newMaster = config["Typhon:Profiler:Enabled"];
        if (!string.IsNullOrEmpty(newMaster))
        {
            return bool.TryParse(newMaster, out var b) && b;
        }

        // Legacy: required Typhon:Telemetry:Enabled AND Typhon:Telemetry:Profiler:Enabled (both must be true).
        var legacyOuter = config["Typhon:Telemetry:Enabled"];
        var legacyInner = config["Typhon:Telemetry:Profiler:Enabled"];
        if (!string.IsNullOrEmpty(legacyOuter) || !string.IsNullOrEmpty(legacyInner))
        {
            legacyDetected = true;
            var outerOn = !string.IsNullOrEmpty(legacyOuter) && bool.TryParse(legacyOuter, out var o) && o;
            var innerOn = !string.IsNullOrEmpty(legacyInner) && bool.TryParse(legacyInner, out var i) && i;
            return outerOn && innerOn;
        }

        return false;
    }

    private static void EmitDeprecationWarning()
    {
        try
        {
            Console.Error.WriteLine(
                "[Typhon.Profiler] Configuration paths under 'Typhon:Telemetry:*' are deprecated; use 'Typhon:Profiler:*' instead. " +
                "The legacy paths are still read via a back-compat shim in this release but will be removed in the next minor.");
        }
        catch
        {
            // Console may not be available in some hosting scenarios — suppress to avoid disrupting startup.
        }
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
        // Uses __ as hierarchy separator: TYPHON__PROFILER__ENABLED -> Typhon:Profiler:Enabled
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
         Typhon Profiler Configuration:
           Config File: {LoadedConfigurationFile ?? "(none - using defaults/env vars)"}
           Master Enabled: {Enabled}
           Legacy Config Detected: {LegacyConfigDetected}

           Profiler: Active={ProfilerActive}
             GcTracing={ProfilerGcTracingEnabled} (Active={ProfilerGcTracingActive}),
             MemoryAllocations={ProfilerMemoryAllocationsEnabled} (Active={ProfilerMemoryAllocationsActive}),
             Gauges={ProfilerGaugesEnabled} (Active={ProfilerGaugesActive})

           Scheduler: Active={SchedulerActive}
             Enabled={SchedulerEnabled}, TransitionLatency={SchedulerTrackTransitionLatency},
             WorkerUtilization={SchedulerTrackWorkerUtilization}, StragglerGap={SchedulerTrackStragglerGap}
         """;

    /// <summary>
    /// Returns a concise one-line summary of active telemetry components.
    /// </summary>
    public static string GetActiveComponentsSummary()
    {
        if (!Enabled)
        {
            return "Profiler: Disabled";
        }

        var active = new System.Collections.Generic.List<string>();

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

        return active.Count > 0 ? $"Profiler: Enabled [{string.Join(", ", active)}]" : "Profiler: Enabled (no components active)";
    }
}
