// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Defines the level of span instrumentation for PagedMMF/PageCache operations.
/// Higher levels include all spans from lower levels.
/// </summary>
[PublicAPI]
public enum PageCacheSpanLevel
{
    /// <summary>
    /// No spans for page cache operations (default, zero overhead).
    /// </summary>
    None = 0,

    /// <summary>
    /// Spans for disk I/O operations only (reads and writes).
    /// Suitable for production - disk ops are already slow.
    /// </summary>
    IOOnly = 1,

    /// <summary>
    /// Adds spans for cache allocation and eviction events.
    /// Useful for investigating cache behavior.
    /// </summary>
    CacheMiss = 2,

    /// <summary>
    /// Spans for every RequestPage call (dangerous for performance!).
    /// Use only for deep diagnostics.
    /// </summary>
    All = 3
}

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

    /// <summary>
    /// The configured span instrumentation level for PagedMMF operations.
    /// </summary>
    public static readonly PageCacheSpanLevel PagedMMFSpanLevel;

    /// <summary>
    /// True if span level is IOOnly or higher (Level >= 1).
    /// Use for disk read/write span guards.
    /// </summary>
    public static readonly bool PagedMMFSpanIOOnly;

    /// <summary>
    /// True if span level is CacheMiss or higher (Level >= 2).
    /// Use for allocation/eviction span guards.
    /// </summary>
    public static readonly bool PagedMMFSpanCacheMiss;

    /// <summary>
    /// True if span level is All (Level == 3).
    /// Use for every RequestPage span guard (dangerous!).
    /// </summary>
    public static readonly bool PagedMMFSpanAll;

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

    /// <summary>
    /// Combined flag: true only if global AND component telemetry are enabled.
    /// </summary>
    public static readonly bool BTreeActive;

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
    // ECS TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether ECS component telemetry is enabled in configuration.
    /// </summary>
    public static readonly bool EcsEnabled;

    /// <summary>
    /// Combined flag: true only if global AND ECS telemetry are enabled.
    /// Use this single check in ECS hot paths (Spawn, Destroy, Open, Query).
    /// </summary>
    public static readonly bool EcsActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // SPATIAL INDEX TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Whether spatial index telemetry is enabled in configuration.</summary>
    public static readonly bool SpatialEnabled;

    /// <summary>Combined flag: true only if global AND spatial telemetry are enabled. Use in spatial hot paths.</summary>
    public static readonly bool SpatialActive;

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

        // PagedMMF span level (for distributed tracing)
        var spanLevelStr = mmfSection.GetValue("SpanLevel", "None");
        PagedMMFSpanLevel = System.Enum.TryParse<PageCacheSpanLevel>(spanLevelStr, true, out var parsedLevel)
            ? parsedLevel
            : PageCacheSpanLevel.None;
        PagedMMFSpanIOOnly = Enabled && PagedMMFSpanLevel >= PageCacheSpanLevel.IOOnly;
        PagedMMFSpanCacheMiss = Enabled && PagedMMFSpanLevel >= PageCacheSpanLevel.CacheMiss;
        PagedMMFSpanAll = Enabled && PagedMMFSpanLevel >= PageCacheSpanLevel.All;

        // BTree
        var btreeSection = section.GetSection("BTree");
        BTreeEnabled = btreeSection.GetValue("Enabled", false);
        BTreeTrackNodeSplits = btreeSection.GetValue("TrackNodeSplits", true);
        BTreeTrackNodeMerges = btreeSection.GetValue("TrackNodeMerges", true);
        BTreeTrackSearchDepth = btreeSection.GetValue("TrackSearchDepth", true);
        BTreeTrackKeyComparisons = btreeSection.GetValue("TrackKeyComparisons", false);
        BTreeActive = Enabled && BTreeEnabled;

        // Transaction
        var txSection = section.GetSection("Transaction");
        TransactionEnabled = txSection.GetValue("Enabled", false);
        TransactionTrackCommitRollback = txSection.GetValue("TrackCommitRollback", true);
        TransactionTrackConflicts = txSection.GetValue("TrackConflicts", true);
        TransactionTrackDuration = txSection.GetValue("TrackDuration", true);
        TransactionActive = Enabled && TransactionEnabled;

        // ECS
        var ecsSection = section.GetSection("ECS");
        EcsEnabled = ecsSection.GetValue("Enabled", false);
        EcsActive = Enabled && EcsEnabled;

        // Spatial Index
        var spatialSection = section.GetSection("Spatial");
        SpatialEnabled = spatialSection.GetValue("Enabled", false);
        SpatialActive = Enabled && SpatialEnabled;
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
             SpanLevel={PagedMMFSpanLevel} (IOOnly={PagedMMFSpanIOOnly}, CacheMiss={PagedMMFSpanCacheMiss}, All={PagedMMFSpanAll})

           BTree: Active={BTreeActive}
             Enabled={BTreeEnabled}, Splits={BTreeTrackNodeSplits}, Merges={BTreeTrackNodeMerges},
             Depth={BTreeTrackSearchDepth}, Comparisons={BTreeTrackKeyComparisons}

           Transaction: Active={TransactionActive}
             Enabled={TransactionEnabled}, CommitRollback={TransactionTrackCommitRollback},
             Conflicts={TransactionTrackConflicts}, Duration={TransactionTrackDuration}

           ECS: Active={EcsActive}
             Enabled={EcsEnabled}
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

        if (BTreeActive)
        {
            active.Add("BTree");
        }

        if (TransactionActive)
        {
            active.Add("Transaction");
        }

        if (EcsActive)
        {
            active.Add("ECS");
        }

        return active.Count > 0
            ? $"Telemetry: Enabled [{string.Join(", ", active)}]"
            : "Telemetry: Enabled (no components active)";
    }
}
