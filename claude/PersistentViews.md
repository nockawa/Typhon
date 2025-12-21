# Persistent Views Design for Typhon

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Design Approach 1: Entity Set Caching with Delta Tracking](#design-approach-1-entity-set-caching-with-delta-tracking)
3. [Design Approach 2: Multi-Level Cached Query Plan](#design-approach-2-multi-level-cached-query-plan)
4. [Design Approach 3: Materialized Component Snapshots](#design-approach-3-materialized-component-snapshots)
5. [Design Approach 4: Dependency Graph with Fine-Grained Invalidation](#design-approach-4-dependency-graph-with-fine-grained-invalidation)
6. [Design Approach 5: Incremental Bitmap Maintenance](#design-approach-5-incremental-bitmap-maintenance)
7. [Disk Persistence Analysis](#disk-persistence-analysis)
8. [Comprehensive Comparison](#comprehensive-comparison)
9. [Recommended Hybrid Approach](#recommended-hybrid-approach)

---

## Problem Statement

### Game Simulation Query Patterns

In game simulations, certain queries are executed repeatedly:

```csharp
// Executed every frame (60 FPS = every 16ms)
var nearbyEnemies = views.GetEnemiesInRadius(playerPosition, 50.0f);

// Executed every tick (20 TPS = every 50ms)
var playersInRegion = views.GetPlayersInRegion(regionId);

// Executed on demand, but frequently (UI updates, leaderboards)
var topPlayers = views.GetTopPlayersByScore(limit: 100);

// Monitored continuously (alerts, triggers)
var lowHealthPlayers = views.GetPlayersWithHealthBelow(threshold: 20);
```

### Requirements

1. **Sub-millisecond refresh**: View results must update quickly (<<16ms for 60 FPS)
2. **Memory efficiency**: Can't cache unlimited queries
3. **Correctness**: Cached results must reflect committed changes
4. **Incremental updates**: Avoid re-executing entire query on small changes
5. **Configurable caching**: User controls cache strategy per view
6. **Persistence (optional)**: Survive process restarts for critical views

### Challenges

1. **When to invalidate**: Which commits affect which views?
2. **What to cache**: Full results? Intermediate steps? Just IDs?
3. **How to update**: Re-execute? Delta computation? Incremental maintenance?
4. **Memory vs speed**: More caching = faster queries but more memory
5. **Consistency with MVCC**: Cached results must respect transaction snapshots

---

## Design Approach 1: Entity Set Caching with Delta Tracking

### Core Concept

Cache the entity ID result set for each view. On commit, track which entities changed and incrementally update affected views.

### Architecture

```csharp
// Cached view state
public class CachedEntitySetView<TC1, TC2, TC3>
    where TC1 : unmanaged
    where TC2 : unmanaged
    where TC3 : unmanaged
{
    // Cached result: just entity IDs
    private HashSet<long> _cachedEntityIds;

    // Compiled predicate for delta checking
    private Func<TC1, TC2, TC3, bool> _predicate;

    // Which component types this view depends on
    private HashSet<Type> _dependentComponentTypes;

    // Invalidation strategy
    private ViewCacheStrategy _strategy;

    // Version/timestamp of cached data
    private long _cacheVersion;

    // Statistics
    private ViewStatistics _stats;
}

public enum ViewCacheStrategy
{
    None,               // No caching, always re-execute
    Invalidate,         // Cache but invalidate on any change
    IncrementalDelta,   // Incrementally update with deltas
    PeriodicRefresh,    // Cache with periodic full refresh
    Smart               // Adaptive based on usage patterns
}

public class ViewStatistics
{
    public long ExecutionCount;
    public long CacheHits;
    public long CacheMisses;
    public long IncrementalUpdates;
    public TimeSpan TotalUpdateTime;
    public TimeSpan AverageUpdateTime;
}
```

### Implementation

```csharp
// View creation with caching
var playersInRegion = dbe
    .CreateView<Player, Stats, Location>()
    .Where((p, s, l) =>
        l.RegionId == 5 &&
        p.IsActive &&
        s.Level > 50)
    .WithCaching(ViewCacheStrategy.IncrementalDelta)
    .Build();

// View state tracking
internal class ViewRegistry
{
    // All active views
    private ConcurrentDictionary<Guid, IView> _views = new();

    // Index: ComponentType → Views that depend on it
    private ConcurrentDictionary<Type, HashSet<Guid>> _componentDependencies = new();

    public void RegisterView(IView view)
    {
        _views[view.Id] = view;

        foreach (var componentType in view.DependentComponentTypes)
        {
            _componentDependencies.GetOrAdd(componentType, _ => new()).Add(view.Id);
        }
    }
}

// On transaction commit
internal class ViewUpdateManager
{
    public void OnTransactionCommit(Transaction t)
    {
        // Get all component types modified in this transaction
        var modifiedComponentTypes = t.GetModifiedComponents().Keys;

        // Find all views that depend on these components
        var affectedViews = new HashSet<IView>();
        foreach (var componentType in modifiedComponentTypes)
        {
            if (_registry.GetViewsForComponent(componentType, out var views))
            {
                affectedViews.UnionWith(views);
            }
        }

        // Update each affected view
        foreach (var view in affectedViews)
        {
            UpdateView(view, t);
        }
    }

    private void UpdateView(IView view, Transaction t)
    {
        switch (view.Strategy)
        {
            case ViewCacheStrategy.Invalidate:
                view.Invalidate();
                break;

            case ViewCacheStrategy.IncrementalDelta:
                UpdateViewIncrementally(view, t);
                break;

            case ViewCacheStrategy.PeriodicRefresh:
                // Mark stale, will refresh on next access if needed
                view.MarkStale();
                break;
        }
    }

    private void UpdateViewIncrementally<TC1, TC2, TC3>(
        CachedEntitySetView<TC1, TC2, TC3> view,
        Transaction t)
        where TC1 : unmanaged
        where TC2 : unmanaged
        where TC3 : unmanaged
    {
        var startTime = Stopwatch.GetTimestamp();

        // Get modified entities for relevant component types
        var modifiedEntities = new HashSet<long>();
        foreach (var componentType in view.DependentComponentTypes)
        {
            if (t.GetModifiedComponentInfo(componentType, out var compInfo))
            {
                modifiedEntities.UnionWith(compInfo.CompRevInfoCache.Keys);
            }
        }

        // For each modified entity, check if it should be in the view
        foreach (var entityId in modifiedEntities)
        {
            bool wasInView = view.CachedEntityIds.Contains(entityId);

            // Read current state
            bool hasComponents =
                t.ReadEntity<TC1>(entityId, out var c1) &&
                t.ReadEntity<TC2>(entityId, out var c2) &&
                t.ReadEntity<TC3>(entityId, out var c3);

            bool shouldBeInView = hasComponents && view.Predicate(c1, c2, c3);

            // Update cached set based on state transition
            if (wasInView && !shouldBeInView)
            {
                // Entity no longer matches - remove
                view.CachedEntityIds.Remove(entityId);
                view.Stats.RemovedCount++;
            }
            else if (!wasInView && shouldBeInView)
            {
                // Entity now matches - add
                view.CachedEntityIds.Add(entityId);
                view.Stats.AddedCount++;
            }
            // else: no change to view
        }

        view.CacheVersion = t.TransactionTick;
        view.Stats.IncrementalUpdates++;

        var elapsed = Stopwatch.GetElapsedTime(startTime);
        view.Stats.TotalUpdateTime += elapsed;
    }
}

// View execution
public IEnumerable<(long entityId, TC1, TC2, TC3)> Execute(Transaction t)
{
    if (_strategy == ViewCacheStrategy.None || NeedsRefresh(t))
    {
        // Full re-execution
        return ExecuteFullQuery(t);
    }
    else
    {
        // Use cached entity IDs, read current component state
        _stats.CacheHits++;

        foreach (var entityId in _cachedEntityIds)
        {
            // Read fresh component data (respects transaction snapshot)
            if (t.ReadEntity<TC1>(entityId, out var c1) &&
                t.ReadEntity<TC2>(entityId, out var c2) &&
                t.ReadEntity<TC3>(entityId, out var c3))
            {
                yield return (entityId, c1, c2, c3);
            }
        }
    }
}
```

### Performance Analysis

**Scenario: 1M entities, view matches 1K entities, 100 entities change per commit**

```
Initial Query Execution:
├─ Pipeline execution: 833µs (as before)
└─ Cache entity IDs: 8KB (1K × 8 bytes)

Incremental Update per Commit:
├─ Find affected views: ~5µs (hash table lookup)
├─ Check 100 modified entities:
│  ├─ Read components: 100 × 3 = 300 reads = 300µs
│  ├─ Evaluate predicate: 100 × 10ns = 1µs
│  └─ Update hash set: 100 × 20ns = 2µs
├─ Total: ~308µs per commit

View Execution (cached):
├─ Iterate 1K entity IDs: ~1µs
├─ Read components: 1K × 3 = 3K reads = 3ms
└─ Total: ~3ms

Comparison to full re-execution:
├─ Full query: 833µs (pipeline) + 3ms (reads) = 3.83ms
├─ Cached query: 3ms
└─ Speedup: 1.28x

Wait, that's not great! Let me reconsider...
```

**Insight: Just caching entity IDs doesn't help much because we still need to read all components.**

### Optimization: Cache Components Too

```csharp
// Enhanced cached view with component snapshots
public class MaterializedView<TC1, TC2, TC3>
{
    // Cache both IDs and component data
    private Dictionary<long, (TC1, TC2, TC3)> _cachedResults;

    // Track which entities have stale component data
    private HashSet<long> _staleEntities;
}

// Incremental update with component caching
private void UpdateViewWithComponentCache(MaterializedView<TC1, TC2, TC3> view, Transaction t)
{
    var modifiedEntities = GetModifiedEntities(t, view.DependentComponentTypes);

    foreach (var entityId in modifiedEntities)
    {
        // Mark as stale
        view.StaleEntities.Add(entityId);

        // Option 1: Eagerly update
        if (view.Strategy.IsEager)
        {
            UpdateEntityInView(view, entityId, t);
        }
    }
}

// View execution with component cache
public IEnumerable<(long, TC1, TC2, TC3)> Execute(Transaction t)
{
    foreach (var (entityId, cachedComponents) in _cachedResults)
    {
        if (_staleEntities.Contains(entityId))
        {
            // Refresh stale component
            if (t.ReadEntity<TC1>(entityId, out var c1) &&
                t.ReadEntity<TC2>(entityId, out var c2) &&
                t.ReadEntity<TC3>(entityId, out var c3))
            {
                // Check if still matches predicate
                if (_predicate(c1, c2, c3))
                {
                    // Update cache
                    _cachedResults[entityId] = (c1, c2, c3);
                    yield return (entityId, c1, c2, c3);
                }
                else
                {
                    // No longer matches - remove from view
                    _cachedResults.Remove(entityId);
                }
            }

            _staleEntities.Remove(entityId);
        }
        else
        {
            // Use cached components
            yield return (entityId, cachedComponents.Item1, cachedComponents.Item2, cachedComponents.Item3);
        }
    }
}
```

**Revised Performance:**

```
Incremental Update per Commit (eager):
├─ Find affected views: ~5µs
├─ Update 100 modified entities:
│  ├─ Read components: 100 × 3 = 300µs
│  ├─ Evaluate predicate: 100 × 10ns = 1µs
│  ├─ Update cache: 100 × 50ns = 5µs
├─ Total: ~311µs per commit

View Execution (cached):
├─ Iterate 1K cached entities: ~10µs
├─ Refresh 100 stale entries: 300µs (or 0 if eager)
└─ Total: 10µs (eager) or 310µs (lazy)

Speedup vs full re-execution:
├─ Full: 3.83ms
├─ Cached (eager): 10µs
└─ Speedup: 383x !!
```

### Pros & Cons

**Pros:**
- ✅ Massive speedup for cached queries (383x)
- ✅ Simple conceptual model (entity set + components)
- ✅ Configurable strategies (eager vs lazy, invalidate vs incremental)
- ✅ Low memory overhead (~24-48 bytes per result entity)
- ✅ Easy to implement

**Cons:**
- ❌ Memory usage scales with result size (1K results = 48KB)
- ❌ Update cost on every commit (even if view not used)
- ❌ Doesn't help with views that have dynamic parameters
- ❌ Cache invalidation logic can be complex
- ❌ MVCC interaction: cached data represents single point in time

---

## Design Approach 2: Multi-Level Cached Query Plan

### Core Concept

Cache results at multiple levels of the query execution plan: index scans, intermediate filters, and final results. Incrementally update each level.

### Architecture

```csharp
public class MultiLevelCachedView<TC1, TC2, TC3>
{
    // Level 1: Cached index scan results (per predicate)
    private Dictionary<string, CachedIndexScan> _indexScanCache;

    // Level 2: Cached intermediate filter results (per pipeline stage)
    private Dictionary<int, HashSet<long>> _stageResultCache;

    // Level 3: Cached final results (full materialization)
    private Dictionary<long, (TC1, TC2, TC3)> _finalResultCache;

    // Change tracking per level
    private HashSet<string> _invalidatedIndexScans;
    private HashSet<int> _invalidatedStages;
    private HashSet<long> _invalidatedResults;
}

// Cached index scan
public class CachedIndexScan
{
    public Type ComponentType;
    public string FieldName;
    public ComparisonOp Op;
    public object Value;

    // Cached result: entity IDs matching this index scan
    public RoaringBitmap MatchingEntities;

    // Last update timestamp
    public long CacheVersion;

    // Statistics
    public long HitCount;
    public long UpdateCount;
}

// Multi-level execution
public class MultiLevelExecutor
{
    public IEnumerable<(long, TC1, TC2, TC3)> Execute(Transaction t, MultiLevelCachedView view)
    {
        // Level 1: Get/refresh index scans
        var scanResults = new List<RoaringBitmap>();

        foreach (var scanKey in view.QueryPlan.RequiredIndexScans)
        {
            RoaringBitmap scanResult;

            if (view.IndexScanCache.TryGetValue(scanKey, out var cached) &&
                !view.InvalidatedIndexScans.Contains(scanKey))
            {
                // Use cached index scan
                scanResult = cached.MatchingEntities;
                cached.HitCount++;
            }
            else
            {
                // Re-execute index scan
                scanResult = ExecuteIndexScan(t, scanKey);

                // Cache result
                view.IndexScanCache[scanKey] = new CachedIndexScan
                {
                    MatchingEntities = scanResult,
                    CacheVersion = t.TransactionTick
                };

                view.InvalidatedIndexScans.Remove(scanKey);
            }

            scanResults.Add(scanResult);
        }

        // Level 2: Intersect index scan results
        var candidateEntities = IntersectBitmaps(scanResults);

        // Level 3: Check final result cache or materialize
        foreach (var entityId in candidateEntities)
        {
            if (view.FinalResultCache.TryGetValue(entityId, out var cached) &&
                !view.InvalidatedResults.Contains(entityId))
            {
                // Use cached final result
                yield return (entityId, cached.Item1, cached.Item2, cached.Item3);
            }
            else
            {
                // Materialize and cache
                if (t.ReadEntity<TC1>(entityId, out var c1) &&
                    t.ReadEntity<TC2>(entityId, out var c2) &&
                    t.ReadEntity<TC3>(entityId, out var c3))
                {
                    view.FinalResultCache[entityId] = (c1, c2, c3);
                    view.InvalidatedResults.Remove(entityId);

                    yield return (entityId, c1, c2, c3);
                }
            }
        }
    }
}

// Incremental update on commit
public void OnTransactionCommit(Transaction t, MultiLevelCachedView view)
{
    var modifiedComponents = t.GetModifiedComponents();

    foreach (var (componentType, compInfo) in modifiedComponents)
    {
        // Invalidate relevant index scans
        foreach (var indexScan in view.IndexScanCache.Values)
        {
            if (indexScan.ComponentType == componentType)
            {
                // Check if any modified entity affects this scan
                foreach (var entityId in compInfo.CompRevInfoCache.Keys)
                {
                    if (ShouldInvalidateScan(entityId, indexScan, t))
                    {
                        view.InvalidatedIndexScans.Add(indexScan.Key);
                        break;
                    }
                }
            }
        }

        // Invalidate final results for modified entities
        foreach (var entityId in compInfo.CompRevInfoCache.Keys)
        {
            view.InvalidatedResults.Add(entityId);
        }
    }
}

// Smart scan invalidation
private bool ShouldInvalidateScan(long entityId, CachedIndexScan scan, Transaction t)
{
    // Read old and new field value
    var oldValue = GetOldFieldValue(entityId, scan.FieldName);
    var newValue = GetNewFieldValue(entityId, scan.FieldName, t);

    // Check if entity transitioned in/out of scan range
    bool wasInRange = EvaluateComparison(oldValue, scan.Op, scan.Value);
    bool isInRange = EvaluateComparison(newValue, scan.Op, scan.Value);

    return wasInRange != isInRange;  // Invalidate if changed
}
```

### Granularity Control

```csharp
public class CacheGranularityConfig
{
    // What to cache at each level
    public bool CacheIndexScans { get; set; } = true;
    public bool CacheIntermediateStages { get; set; } = true;
    public bool CacheFinalResults { get; set; } = true;

    // Eviction policies
    public int MaxCachedIndexScans { get; set; } = 100;
    public int MaxCachedStages { get; set; } = 50;
    public int MaxCachedResults { get; set; } = 10_000;

    // Update strategies
    public UpdateStrategy IndexScanStrategy { get; set; } = UpdateStrategy.Lazy;
    public UpdateStrategy StageStrategy { get; set; } = UpdateStrategy.Lazy;
    public UpdateStrategy ResultStrategy { get; set; } = UpdateStrategy.Eager;
}

public enum UpdateStrategy
{
    Lazy,      // Refresh on next access
    Eager,     // Refresh immediately on commit
    Periodic   // Refresh on timer
}
```

### Example Usage

```csharp
// Create view with multi-level caching
var nearbyEnemies = dbe
    .CreateView<Enemy, Transform, Health>()
    .Where((enemy, transform, health) =>
        transform.Position.DistanceTo(playerPos) < 50.0f &&
        enemy.Faction == Faction.Hostile &&
        health.CurrentHealth > 0)
    .WithMultiLevelCache(config =>
    {
        config.CacheIndexScans = true;        // Cache "Faction == Hostile"
        config.CacheFinalResults = true;      // Cache materialized enemies
        config.ResultStrategy = UpdateStrategy.Eager;  // Update immediately
    })
    .Build();

// First execution: cold cache
using (var t = dbe.CreateTransaction())
{
    var enemies = nearbyEnemies.Execute(t).ToList();
    // Time: 833µs (initial query) + caching overhead
}

// Subsequent executions: warm cache
using (var t = dbe.CreateTransaction())
{
    var enemies = nearbyEnemies.Execute(t).ToList();
    // Time: ~5µs (cached results, assuming no invalidations)
}

// After enemy takes damage (Health component changed)
// → Only invalidates final result cache for that enemy
// → Index scan cache (Faction == Hostile) still valid

// After enemy spawns (new entity)
// → Index scan cache invalidated and refreshed
// → Adds new enemy to final result cache
```

### Performance Analysis

**Scenario: 100K enemies total, 500 hostile nearby, 50 take damage per frame**

```
Initial Execution (cold cache):
├─ Index scan "Faction == Hostile": 200µs → 20K results (cached)
├─ Filter "Distance < 50": 20K checks @ 100ns = 2ms
├─ Filter "Health > 0": 500 checks @ 50ns = 25µs
├─ Materialize 500 enemies: 500 × 3 components = 1.5ms
└─ Total: ~3.73ms

Subsequent Executions (warm cache, no changes):
├─ Check index scan cache: HIT (~1µs)
├─ Check intermediate stage cache: HIT (~5µs)
├─ Check final result cache: 500 HITs (~5µs)
└─ Total: ~11µs (339x speedup!)

Update on Commit (50 enemies damaged):
├─ Check if index scans affected: 50 × 10ns = 500ns (NONE)
├─ Invalidate 50 final results: 50 × 20ns = 1µs
├─ (Eager) Refresh 50 results: 50 × 3 reads = 150µs
└─ Total: ~152µs

Next Execution (warm cache, 50 invalidated):
├─ Index scan cache: HIT (~1µs)
├─ Intermediate cache: HIT (~5µs)
├─ Final results: 450 HIT + 50 refresh = 5µs + 150µs
└─ Total: ~156µs (24x speedup vs full re-execution)
```

### Pros & Cons

**Pros:**
- ✅ Extreme speedup for warm cache (339x)
- ✅ Granular invalidation (only affected levels)
- ✅ Flexible configuration (tune per view)
- ✅ Reusable index scans across views
- ✅ Excellent for high-frequency queries

**Cons:**
- ❌ Complex implementation (three cache levels)
- ❌ Higher memory usage (caching at multiple levels)
- ❌ Cache coherence challenges
- ❌ Overhead for views executed once
- ❌ Difficult to determine optimal granularity

---

## Design Approach 3: Materialized Component Snapshots

### Core Concept

Store view results as actual component data in a special "materialized view component". Use Typhon's own MVCC system to manage versions.

### Architecture

```csharp
// Materialized view stored as a component
[Component("MaterializedView_NearbyEnemies", 1)]
public struct MaterializedViewEntry_NearbyEnemies
{
    [Field] public long SourceEntityId;  // Reference to original entity

    // Snapshot of source components at materialization time
    [Field] public Enemy EnemySnapshot;
    [Field] public Transform TransformSnapshot;
    [Field] public Health HealthSnapshot;

    // Metadata
    [Field] public long MaterializedAtTick;
    [Field] public bool IsStale;
}

// View definition with materialized storage
public class MaterializedComponentView<TC1, TC2, TC3, TMaterialized>
    where TC1 : unmanaged
    where TC2 : unmanaged
    where TC3 : unmanaged
    where TMaterialized : unmanaged
{
    // View definition
    private Expression<Func<TC1, TC2, TC3, bool>> _predicate;

    // Materialized component type
    private Type _materializedType;

    // Mapping function: source components → materialized entry
    private Func<long, TC1, TC2, TC3, TMaterialized> _materializeFunc;

    // Reverse mapping: materialized entry → original entity ID
    private Func<TMaterialized, long> _getSourceEntityId;
}

// View execution
public IEnumerable<(TC1, TC2, TC3)> Execute(Transaction t)
{
    // Query materialized view component
    var materializedEntities = dbe
        .Select<TMaterialized>()
        .Where(m => !m.IsStale)  // Only return fresh entries
        .Execute(t);

    foreach (var (_, materialized) in materializedEntities)
    {
        // Extract snapshots from materialized entry
        var (c1, c2, c3) = ExtractSnapshots(materialized);
        yield return (c1, c2, c3);
    }
}

// Incremental maintenance on commit
public void OnTransactionCommit(Transaction t)
{
    var modifiedEntities = GetModifiedEntities(t, _dependentComponentTypes);

    using var maintenanceTx = dbe.CreateTransaction();

    foreach (var entityId in modifiedEntities)
    {
        // Check if entity currently in materialized view
        var materializedId = FindMaterializedEntry(entityId, maintenanceTx);

        // Read current state
        bool hasComponents =
            t.ReadEntity<TC1>(entityId, out var c1) &&
            t.ReadEntity<TC2>(entityId, out var c2) &&
            t.ReadEntity<TC3>(entityId, out var c3);

        bool matchesPredicate = hasComponents && _predicate.Compile()(c1, c2, c3);

        if (materializedId.HasValue && !matchesPredicate)
        {
            // Entity no longer matches - remove from materialized view
            maintenanceTx.DeleteEntity<TMaterialized>(materializedId.Value);
        }
        else if (!materializedId.HasValue && matchesPredicate)
        {
            // Entity now matches - add to materialized view
            var materialized = _materializeFunc(entityId, c1, c2, c3);
            maintenanceTx.CreateEntity(ref materialized);
        }
        else if (materializedId.HasValue && matchesPredicate)
        {
            // Entity still matches but data changed - update snapshot
            var materialized = _materializeFunc(entityId, c1, c2, c3);
            maintenanceTx.UpdateEntity(materializedId.Value, ref materialized);
        }
    }

    maintenanceTx.Commit();
}
```

### Example: Leaderboard View

```csharp
// Define materialized view component
[Component("LeaderboardEntry", 1)]
public struct LeaderboardEntry
{
    [Field] [Index] public long PlayerId;  // Reference to Player entity

    // Snapshot of player data
    [Field] public String64 PlayerName;
    [Field] [Index] public int Score;      // Indexed for ranking queries
    [Field] public int Level;

    // Metadata
    [Field] public long UpdatedAtTick;
}

// Create materialized leaderboard view
var leaderboard = dbe
    .CreateMaterializedView<Player, Stats, LeaderboardEntry>()
    .Where((player, stats) => stats.Score > 1000)
    .Materialize((playerId, player, stats) => new LeaderboardEntry
    {
        PlayerId = playerId,
        PlayerName = player.Name,
        Score = stats.Score,
        Level = stats.Level,
        UpdatedAtTick = DateTime.UtcNow.Ticks
    })
    .WithRefreshStrategy(RefreshStrategy.Eager)
    .Build();

// Query leaderboard (uses materialized component)
using var t = dbe.CreateTransaction();

var topPlayers = dbe
    .Select<LeaderboardEntry>()
    .OrderByDescending(e => e.Score)  // Uses Score index!
    .Take(100)
    .Execute(t);

foreach (var (_, entry) in topPlayers)
{
    Console.WriteLine($"{entry.PlayerName}: {entry.Score}");
}

// Performance:
// First time: Full materialization of all players with Score > 1000
// Subsequent: Just index scan on LeaderboardEntry.Score (very fast!)
// Updates: Only when player scores change
```

### MVCC Integration

The beauty of this approach is that materialized views are just regular components, so they benefit from Typhon's MVCC:

```csharp
// Transaction T1: Reads leaderboard
using var t1 = dbe.CreateTransaction();  // Tick = 1000
var leaderboard1 = QueryLeaderboard(t1);
// Sees leaderboard as of tick 1000

// Transaction T2: Player scores change, updates materialized view
using var t2 = dbe.CreateTransaction();  // Tick = 1001
UpdatePlayerScore(player123, newScore: 5000, t2);
t2.Commit();
// Materialized view updated with new revision at tick 1001

// T1 continues reading (still at tick 1000)
var leaderboard2 = QueryLeaderboard(t1);
// Still sees OLD leaderboard (snapshot isolation!)

// New transaction sees updated view
using var t3 = dbe.CreateTransaction();  // Tick = 1002
var leaderboard3 = QueryLeaderboard(t3);
// Sees NEW leaderboard with updated scores
```

### Pros & Cons

**Pros:**
- ✅ Leverages existing MVCC infrastructure
- ✅ Natural integration with Typhon's component model
- ✅ Automatic versioning and snapshot isolation
- ✅ Can index materialized components for fast queries
- ✅ Persistent by default (survives restarts)
- ✅ Simpler invalidation logic (just update component)

**Cons:**
- ❌ Storage overhead (duplicates source data)
- ❌ Maintenance cost (creates/updates/deletes components)
- ❌ Harder to express complex transformations
- ❌ Materialized components count toward total entity limit
- ❌ MVCC overhead (revision tracking, GC of old versions)

---

## Design Approach 4: Dependency Graph with Fine-Grained Invalidation

### Core Concept

Build a dependency graph tracking which view results depend on which component fields. Only invalidate affected results when specific fields change.

### Architecture

```csharp
// Dependency tracking
public class ViewDependencyGraph
{
    // Node in the dependency graph
    public class DependencyNode
    {
        public Type ComponentType;
        public string FieldName;
        public HashSet<ViewSubscription> DependentViews;
    }

    // Edge: View → Field dependency
    public class ViewSubscription
    {
        public Guid ViewId;
        public DependencyType Type;  // Read, Filter, Project
        public PredicateInfo Predicate;  // For filter dependencies
    }

    // Graph structure
    private Dictionary<(Type, string), DependencyNode> _nodes = new();

    public void RegisterDependency(
        Guid viewId,
        Type componentType,
        string fieldName,
        DependencyType type,
        PredicateInfo predicate = null)
    {
        var key = (componentType, fieldName);
        var node = _nodes.GetOrAdd(key, _ => new DependencyNode
        {
            ComponentType = componentType,
            FieldName = fieldName,
            DependentViews = new()
        });

        node.DependentViews.Add(new ViewSubscription
        {
            ViewId = viewId,
            Type = type,
            Predicate = predicate
        });
    }
}

// Track field-level changes
public class FieldLevelChangeTracker
{
    public class FieldChange
    {
        public Type ComponentType;
        public long EntityId;
        public string FieldName;
        public object OldValue;
        public object NewValue;
    }

    public List<FieldChange> GetFieldChanges(Transaction t)
    {
        var changes = new List<FieldChange>();

        foreach (var (componentType, compInfo) in t.GetModifiedComponents())
        {
            foreach (var entityId in compInfo.CompRevInfoCache.Keys)
            {
                var oldComponent = ReadOldComponent(entityId, componentType);
                var newComponent = ReadNewComponent(entityId, componentType, t);

                // Compare field by field
                foreach (var field in GetFields(componentType))
                {
                    var oldValue = field.GetValue(oldComponent);
                    var newValue = field.GetValue(newComponent);

                    if (!Equals(oldValue, newValue))
                    {
                        changes.Add(new FieldChange
                        {
                            ComponentType = componentType,
                            EntityId = entityId,
                            FieldName = field.Name,
                            OldValue = oldValue,
                            NewValue = newValue
                        });
                    }
                }
            }
        }

        return changes;
    }
}

// Fine-grained invalidation
public class FineGrainedViewInvalidator
{
    public void OnTransactionCommit(Transaction t, ViewDependencyGraph graph)
    {
        var fieldChanges = _changeTracker.GetFieldChanges(t);

        var affectedViews = new Dictionary<Guid, HashSet<long>>();

        foreach (var change in fieldChanges)
        {
            var key = (change.ComponentType, change.FieldName);

            if (graph.TryGetNode(key, out var node))
            {
                foreach (var subscription in node.DependentViews)
                {
                    // Check if this change affects the view
                    if (ShouldInvalidate(subscription, change))
                    {
                        affectedViews
                            .GetOrAdd(subscription.ViewId, _ => new())
                            .Add(change.EntityId);
                    }
                }
            }
        }

        // Invalidate affected views
        foreach (var (viewId, entityIds) in affectedViews)
        {
            var view = _viewRegistry.GetView(viewId);
            view.InvalidateEntities(entityIds);
        }
    }

    private bool ShouldInvalidate(ViewSubscription subscription, FieldChange change)
    {
        if (subscription.Type == DependencyType.Read)
        {
            // Any change to a read field invalidates
            return true;
        }
        else if (subscription.Type == DependencyType.Filter)
        {
            // Check if change affects filter outcome
            var predicate = subscription.Predicate;

            bool oldResult = EvaluatePredicate(predicate, change.OldValue);
            bool newResult = EvaluatePredicate(predicate, change.NewValue);

            // Only invalidate if entity transitions in/out of view
            return oldResult != newResult;
        }

        return false;
    }
}
```

### Example: Smart Invalidation

```csharp
// View: High-level active players
var elitePlayers = dbe
    .CreateView<Player, Stats>()
    .Where((player, stats) =>
        player.IsActive &&          // Dependency: Player.IsActive (filter)
        stats.Level >= 50 &&        // Dependency: Stats.Level (filter)
        stats.Score > 10000)        // Dependency: Stats.Score (filter)
    .Build();

// Dependency graph for this view:
Player.IsActive → elitePlayers (Filter: value == true)
Stats.Level → elitePlayers (Filter: value >= 50)
Stats.Score → elitePlayers (Filter: value > 10000)

// Scenario 1: Player logs in (IsActive: false → true)
// Change: Player.IsActive changed
// Action: Re-evaluate predicate for this entity (might enter view)

// Scenario 2: Player's name changes
// Change: Player.Name changed
// Action: NO invalidation (Name not in dependency graph)

// Scenario 3: Player gains XP (Level: 49 → 50)
// Change: Stats.Level changed
// Old value (49): Doesn't match filter (49 < 50)
// New value (50): Matches filter (50 >= 50)
// Action: Add entity to view

// Scenario 4: Player gains more XP (Level: 50 → 51)
// Change: Stats.Level changed
// Old value (50): Matches filter
// New value (51): Matches filter
// Action: NO invalidation (still matches, just update cached value)

// Scenario 5: Player loses points (Score: 12000 → 9000)
// Change: Stats.Score changed
// Old value (12000): Matches filter
// New value (9000): Doesn't match filter (9000 < 10000)
// Action: Remove entity from view
```

### Performance Analysis

```
Dependency Graph Build (one-time per view):
├─ Parse predicate AST: ~50µs
├─ Extract field dependencies: ~20µs
├─ Register in graph: ~10µs
└─ Total: ~80µs

Field-Level Change Tracking per Commit (100 entities modified, 3 components each):
├─ Read old components: 100 × 3 = 300 reads = 300µs
├─ Read new components: 100 × 3 = 300 reads = 300µs
├─ Compare fields: 100 × 3 components × 5 fields × 10ns = 15µs
└─ Total: ~615µs

Invalidation Check (100 field changes, 10 affected views):
├─ Look up dependencies: 100 × 20ns = 2µs
├─ Evaluate predicate transitions: 100 × 100ns = 10µs
├─ Update affected views: 10 × 50µs = 500µs
└─ Total: ~512µs

Combined Per-Commit Cost:
├─ Change tracking: 615µs
├─ Invalidation: 512µs
└─ Total: ~1.13ms

Memory Overhead:
├─ Dependency graph: ~100 bytes per field dependency
├─ Change tracking: None (transient during commit)
└─ For 100 views × 10 field deps: ~100KB
```

### Pros & Cons

**Pros:**
- ✅ Minimal false invalidations (field-level precision)
- ✅ Scales well with number of views (shared dependency graph)
- ✅ Efficient for views with disjoint field sets
- ✅ Enables sophisticated caching strategies
- ✅ Reusable infrastructure across all views

**Cons:**
- ❌ High per-commit overhead (field comparison)
- ❌ Complex implementation (graph management, predicate evaluation)
- ❌ Memory overhead for dependency graph
- ❌ Doesn't reduce query execution time (only invalidation)
- ❌ May over-invalidate for complex predicates

---

## Design Approach 5: Incremental Bitmap Maintenance

### Core Concept

Represent view results as bitmaps (entity ID bit sets). Maintain bitmaps incrementally using set operations (union, intersection, difference).

### Architecture

```csharp
public class IncrementalBitmapView<TC1, TC2, TC3>
{
    // View result as bitmap
    private RoaringBitmap _resultBitmap;

    // Per-predicate bitmaps
    private Dictionary<string, RoaringBitmap> _predicateBitmaps;

    // Predicate dependency tracking
    private Expression<Func<TC1, TC2, TC3, bool>> _predicate;
    private List<PredicateClause> _predicateClauses;

    // Change deltas (since last refresh)
    private RoaringBitmap _addedEntities;
    private RoaringBitmap _removedEntities;
    private RoaringBitmap _modifiedEntities;
}

// Incremental maintenance using set operations
public class IncrementalBitmapMaintenance
{
    public void OnTransactionCommit(Transaction t, IncrementalBitmapView view)
    {
        var modifiedEntities = GetModifiedEntities(t, view.DependentComponentTypes);

        // Categorize each modified entity
        foreach (var entityId in modifiedEntities)
        {
            bool wasInView = view.ResultBitmap.Contains(entityId);

            // Check if entity should be in view now
            bool shouldBeInView = EvaluatePredicate(entityId, view, t);

            if (!wasInView && shouldBeInView)
            {
                // Add to view
                view.ResultBitmap.Add(entityId);
                view.AddedEntities.Add(entityId);
            }
            else if (wasInView && !shouldBeInView)
            {
                // Remove from view
                view.ResultBitmap.Remove(entityId);
                view.RemovedEntities.Add(entityId);
            }
            else if (wasInView && shouldBeInView)
            {
                // Still in view but data changed
                view.ModifiedEntities.Add(entityId);
            }
        }

        // Update per-predicate bitmaps if needed
        UpdatePredicateBitmaps(t, view, modifiedEntities);
    }

    // Smart incremental update for complex predicates
    private void UpdatePredicateBitmaps(
        Transaction t,
        IncrementalBitmapView view,
        IEnumerable<long> modifiedEntities)
    {
        // For each predicate clause: player.Age < 18
        foreach (var clause in view.PredicateClauses)
        {
            var predicateBitmap = view.PredicateBitmaps[clause.Key];

            foreach (var entityId in modifiedEntities)
            {
                // Check if entity matches this specific predicate
                bool matches = EvaluateClause(entityId, clause, t);

                if (matches)
                {
                    predicateBitmap.Add(entityId);
                }
                else
                {
                    predicateBitmap.Remove(entityId);
                }
            }
        }

        // Recompute view result bitmap from predicate bitmaps
        // For AND predicates: Intersect all bitmaps
        view.ResultBitmap = view.PredicateBitmaps.Values
            .Aggregate((a, b) => a.And(b));
    }
}

// Delta subscription API
public class BitmapViewDeltaSubscriber
{
    // Get changes since last subscription check
    public ViewDelta GetDelta(IncrementalBitmapView view)
    {
        var delta = new ViewDelta
        {
            AddedEntities = view.AddedEntities.Clone(),
            RemovedEntities = view.RemovedEntities.Clone(),
            ModifiedEntities = view.ModifiedEntities.Clone()
        };

        // Clear deltas after consumption
        view.AddedEntities.Clear();
        view.RemovedEntities.Clear();
        view.ModifiedEntities.Clear();

        return delta;
    }
}

public class ViewDelta
{
    public RoaringBitmap AddedEntities;
    public RoaringBitmap RemovedEntities;
    public RoaringBitmap ModifiedEntities;

    public bool IsEmpty =>
        AddedEntities.IsEmpty &&
        RemovedEntities.IsEmpty &&
        ModifiedEntities.IsEmpty;
}
```

### Example: Real-Time Map View

```csharp
// View: All entities in visible map region
var visibleEntities = dbe
    .CreateBitmapView<Transform, Renderable>()
    .Where((transform, renderable) =>
        transform.X >= viewport.MinX &&
        transform.X <= viewport.MaxX &&
        transform.Y >= viewport.MinY &&
        transform.Y <= viewport.MaxY &&
        renderable.IsVisible)
    .Build();

// Game loop
while (running)
{
    // Get delta since last frame
    var delta = visibleEntities.GetDelta();

    if (!delta.IsEmpty)
    {
        // Add newly visible entities to render list
        foreach (var entityId in delta.AddedEntities)
        {
            AddToRenderList(entityId);
        }

        // Remove no-longer-visible entities
        foreach (var entityId in delta.RemovedEntities)
        {
            RemoveFromRenderList(entityId);
        }

        // Update moved/changed entities
        foreach (var entityId in delta.ModifiedEntities)
        {
            UpdateInRenderList(entityId);
        }
    }

    Render();
}

// Performance:
// Full view: 5,000 entities in viewport
// Per frame: ~50 entities move, ~5 enter/exit viewport
//
// Without deltas: Re-execute view (833µs) + read 5K entities (15ms)
// With deltas: Check 55 modified entities (55µs) + read 55 (165µs)
// Speedup: 72x !
```

### Bitmap Compression

Roaring Bitmaps provide excellent compression for sparse sets:

```
Scenario: 1M entities, 1K match view (0.1% selectivity)

Uncompressed bitmap:
├─ 1M bits = 125KB

Roaring bitmap:
├─ 1K set bits represented as ~1KB containers
├─ Total: ~2KB
└─ Compression ratio: 62.5x

Operations:
├─ Add/Remove: O(1) amortized
├─ Intersection: O(n + m) where n, m = container counts
├─ Union: O(n + m)
└─ Iteration: O(k) where k = set bits
```

### Pros & Cons

**Pros:**
- ✅ Memory-efficient (compressed bitmaps)
- ✅ Fast set operations (intersection, union)
- ✅ Natural delta tracking (added/removed/modified)
- ✅ Excellent for spatial queries (range queries on coordinates)
- ✅ Incremental updates very fast (just set bits)

**Cons:**
- ❌ Doesn't cache component data (only entity IDs)
- ❌ Still need to read components for final results
- ❌ Bitmap operations have overhead for dense sets
- ❌ Predicate re-evaluation needed for modifications
- ❌ Bitmap memory can grow large for large result sets

---

## Disk Persistence Analysis

### Should We Persist Views to Disk?

Let's analyze the trade-offs:

#### Approach A: In-Memory Only (Transient)

```csharp
// Views exist only in RAM
public class TransientViewStorage
{
    private Dictionary<Guid, IView> _views = new();

    // Fast access, but lost on restart
}
```

**Pros:**
- ✅ Zero I/O overhead
- ✅ Simple implementation
- ✅ Maximum performance (memory-speed access)
- ✅ No disk space usage

**Cons:**
- ❌ Lost on process restart
- ❌ Rebuild cost on startup (could be significant)
- ❌ Crash recovery requires full re-execution

**Use Cases:**
- Short-lived views (per-session queries)
- Rapidly changing data (real-time game state)
- Data that's cheap to recompute

#### Approach B: Persistent to Disk (Durable)

```csharp
// Views stored in Typhon database as components
public class PersistentViewStorage
{
    // View metadata component
    [Component("ViewMetadata", 1)]
    public struct ViewMetadata
    {
        [Field] public String64 ViewName;
        [Field] public int Version;
        [Field] public long LastRefreshTick;
        [Field] public VariableSizedArray<byte> SerializedPredicate;
    }

    // View result component (one per result entity)
    [Component("ViewResult_{ViewName}", 1)]
    public struct ViewResult
    {
        [Field] public long SourceEntityId;
        [Field] public VariableSizedArray<byte> MaterializedData;
    }
}
```

**Pros:**
- ✅ Survives restarts (no rebuild cost)
- ✅ Crash recovery (consistent with database)
- ✅ Can leverage MVCC for versioning
- ✅ Natural integration with Typhon's persistence

**Cons:**
- ❌ I/O overhead on every update
- ❌ Disk space usage (can be significant)
- ❌ Slower updates (write to PagedMMF)
- ❌ Complicates view lifecycle management

**Use Cases:**
- Long-lived views (leaderboards, analytics)
- Expensive-to-recompute views (complex aggregations)
- Critical views needed immediately on startup

#### Hybrid Approach: Selective Persistence

```csharp
public class HybridViewStorage
{
    public enum PersistenceStrategy
    {
        Transient,           // Never persist
        Checkpointed,        // Persist periodically
        WriteThrough,        // Persist every update
        WriteBehind          // Async persist
    }

    public class ViewConfig
    {
        public PersistenceStrategy Persistence { get; set; } = PersistenceStrategy.Transient;
        public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(5);
        public int DirtyThreshold { get; set; } = 1000;  // Persist after N changes
    }
}

// Usage
var leaderboard = dbe
    .CreateView<Player, Stats>()
    .Where((p, s) => s.Score > 10000)
    .WithPersistence(config =>
    {
        config.Persistence = PersistenceStrategy.Checkpointed;
        config.CheckpointInterval = TimeSpan.FromMinutes(1);
    })
    .Build();
```

**Recommended Strategy:**

```
View Type                  | Persistence       | Rationale
---------------------------|-------------------|---------------------------
Real-time (every frame)    | Transient         | Too frequent, I/O prohibitive
Session-scoped             | Transient         | Lost on logout anyway
Leaderboards               | Checkpointed      | Important but not critical
Analytics/Dashboards       | Checkpointed      | Can tolerate slight staleness
Critical metrics           | WriteThrough      | Must be durable
Background aggregations    | WriteBehind       | Async, don't block commits
```

### Persistence Implementation

```csharp
// Checkpointed persistence
public class CheckpointedViewPersistence
{
    private readonly TimeSpan _interval;
    private DateTime _lastCheckpoint;

    public async Task CheckpointLoop(IView view)
    {
        while (true)
        {
            await Task.Delay(_interval);

            if (view.IsDirty)
            {
                await PersistView(view);
                view.MarkClean();
            }
        }
    }

    private async Task PersistView(IView view)
    {
        using var tx = _dbe.CreateTransaction();

        // Write view metadata
        var metadata = new ViewMetadata
        {
            ViewName = view.Name,
            Version = view.Version,
            LastRefreshTick = view.LastRefreshTick,
            SerializedPredicate = SerializePredicate(view.Predicate)
        };
        tx.CreateOrUpdateEntity(ref metadata);

        // Write view results (batched)
        foreach (var batch in view.GetResults().Chunk(1000))
        {
            foreach (var result in batch)
            {
                var resultEntry = new ViewResult
                {
                    SourceEntityId = result.EntityId,
                    MaterializedData = SerializeResult(result)
                };
                tx.CreateOrUpdateEntity(ref resultEntry);
            }

            // Commit batch to avoid large transactions
            tx.Commit();
            tx = _dbe.CreateTransaction();
        }

        tx.Commit();
    }
}

// Load persisted view on startup
public IView LoadPersistedView(string viewName)
{
    using var tx = _dbe.CreateTransaction();

    // Load metadata
    var metadata = tx.Query<ViewMetadata>()
        .Where(m => m.ViewName == viewName)
        .FirstOrDefault();

    if (metadata == default)
    {
        return null;  // View not persisted
    }

    // Reconstruct predicate
    var predicate = DeserializePredicate(metadata.SerializedPredicate);

    // Load results
    var results = tx.Query<ViewResult>()
        .Where(r => r.ViewName == viewName)
        .Select(r => DeserializeResult(r.MaterializedData))
        .ToList();

    // Create view with pre-populated cache
    var view = CreateView(predicate);
    view.PopulateCache(results);
    view.LastRefreshTick = metadata.LastRefreshTick;

    return view;
}
```

### Performance Impact of Persistence

```
Scenario: Leaderboard view with 10K entries

Transient (in-memory only):
├─ Initial build: 5ms
├─ Per-update: 50µs (in-memory only)
├─ Restart cost: 5ms (rebuild)
└─ Memory: 480KB (10K × 48 bytes)

Checkpointed (persist every 1 min):
├─ Initial build: 5ms
├─ Per-update: 50µs (in-memory)
├─ Checkpoint write: 50ms (10K component writes)
├─ Restart cost: 20ms (read from disk)
└─ Disk: 480KB + index overhead

WriteThrough (persist every update):
├─ Initial build: 5ms
├─ Per-update: 50µs (memory) + 5ms (disk write) = 5.05ms
├─ Restart cost: 20ms
└─ Disk: 480KB + MVCC revisions

Analysis:
- WriteThrough: 100x slower updates (5.05ms vs 50µs)
- Checkpointed: Best balance (fast updates, durable eventually)
- Transient: Fastest but vulnerable to crashes
```

### Recommendation: Don't Persist to Disk (Initially)

**Rationale:**

1. **Performance critical**: Typhon targets microsecond-level operations. Disk I/O kills this.

2. **MVCC already provides durability**: Source data is persisted. Views are derived and can be rebuilt.

3. **Fast rebuild on startup**: With index-centric queries, rebuilding views on startup is acceptable:
   ```
   100 views × 1ms avg build time = 100ms startup cost
   This is acceptable for most games.
   ```

4. **Memory-first design**: Keep views in RAM for maximum speed. Users who need persistence can implement it themselves using materialized components.

5. **Simpler implementation**: No serialization, no disk format versioning, no migration logic.

**Exception: User-Defined Persistence**

Allow users to opt-in to persistence for critical views:

```csharp
// Explicit persistence via materialized components
var criticalLeaderboard = dbe
    .CreateMaterializedView<Player, Stats, LeaderboardEntry>()
    .Where((p, s) => s.Score > 10000)
    .Materialize((id, p, s) => new LeaderboardEntry { ... })
    .Build();

// This leverages Typhon's own persistence (PagedMMF)
// User gets durability but pays the cost explicitly
```

---

## Comprehensive Comparison

| Criterion | Approach 1<br/>Entity Set | Approach 2<br/>Multi-Level | Approach 3<br/>Materialized | Approach 4<br/>Dependency | Approach 5<br/>Bitmap |
|-----------|------------------------|--------------------------|---------------------------|-------------------------|---------------------|
| **Memory Usage** | ⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Update Speed** | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Query Speed** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| **Implementation Complexity** | ⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐ |
| **False Invalidations** | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **MVCC Integration** | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ |
| **Scalability** | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Delta Support** | ⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Persistence Ready** | ⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐ |

---

## Recommended Hybrid Approach

After analyzing all options, I recommend a **hybrid approach** combining the best elements:

### Foundation: Approach 1 (Entity Set) + Approach 5 (Bitmap)

```csharp
public class HybridPersistentView<TC1, TC2, TC3>
    where TC1 : unmanaged
    where TC2 : unmanaged
    where TC3 : unmanaged
{
    // Core: Bitmap for entity ID storage (memory-efficient)
    private RoaringBitmap _resultBitmap;

    // Optional: Component cache for frequently accessed views
    private Dictionary<long, (TC1, TC2, TC3)> _componentCache;
    private bool _cacheComponents;

    // Delta tracking (Approach 5)
    private RoaringBitmap _addedSinceLastQuery;
    private RoaringBitmap _removedSinceLastQuery;
    private RoaringBitmap _modifiedSinceLastQuery;

    // Statistics and adaptive behavior
    private ViewStatistics _stats;
    private ViewConfig _config;
}

public class ViewConfig
{
    // Caching strategy
    public bool CacheComponents { get; set; } = true;
    public int MaxCachedComponents { get; set; } = 10_000;

    // Update strategy
    public UpdateTiming UpdateTiming { get; set; } = UpdateTiming.Eager;

    // Eviction policy
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.LRU;

    // Optional: Materialize to component for persistence
    public bool MaterializeToComponent { get; set; } = false;
    public Type MaterializedComponentType { get; set; }

    // Performance tuning
    public int DeltaBatchSize { get; set; } = 256;
    public bool EnableDeltaTracking { get; set; } = true;
}

public enum UpdateTiming
{
    Eager,      // Update immediately on commit
    Lazy,       // Update on next query
    Deferred    // Update on background thread
}
```

### Implementation

```csharp
// View creation
var nearbyEnemies = dbe
    .CreateView<Enemy, Transform, Health>()
    .Where((e, t, h) =>
        t.Position.DistanceTo(playerPos) < 50.0f &&
        e.Faction == Faction.Hostile &&
        h.CurrentHP > 0)
    .Configure(config =>
    {
        config.CacheComponents = true;       // Cache for fast access
        config.UpdateTiming = UpdateTiming.Eager;  // Update on commit
        config.EnableDeltaTracking = true;   // Track changes
    })
    .Build();

// Query with delta support
var delta = nearbyEnemies.GetDeltaSinceLastQuery();

foreach (var entityId in delta.Added)
{
    var enemy = nearbyEnemies.GetCached(entityId);  // From cache
    SpawnEnemyVisual(enemy);
}

foreach (var entityId in delta.Removed)
{
    DespawnEnemyVisual(entityId);
}

foreach (var entityId in delta.Modified)
{
    var enemy = nearbyEnemies.GetCached(entityId);
    UpdateEnemyVisual(enemy);
}

// For persistence-critical views, materialize to component
var leaderboard = dbe
    .CreateView<Player, Stats>()
    .Where((p, s) => s.Score > 10000)
    .Configure(config =>
    {
        config.MaterializeToComponent = true;  // Persist to disk
        config.MaterializedComponentType = typeof(LeaderboardEntry);
    })
    .Build();
```

### Adaptive Optimization

```csharp
// Views automatically optimize based on usage patterns
public class AdaptiveViewOptimizer
{
    public void OptimizeView(HybridPersistentView view)
    {
        var stats = view.Stats;

        // Decision: Should we cache components?
        if (stats.AverageQueryFrequency > 10)  // >10 queries/second
        {
            if (!view.Config.CacheComponents)
            {
                _logger.LogInformation("Enabling component cache for frequently queried view");
                view.Config.CacheComponents = true;
            }
        }
        else if (stats.AverageQueryFrequency < 0.1)  // <1 query/10 seconds
        {
            if (view.Config.CacheComponents)
            {
                _logger.LogInformation("Disabling component cache for infrequently queried view");
                view.Config.CacheComponents = false;
                view.ClearComponentCache();
            }
        }

        // Decision: Update timing
        var updateCost = stats.AverageUpdateTime;
        var queryCost = stats.AverageQueryTime;

        if (updateCost.TotalMicroseconds > 1000 && queryCost.TotalMicroseconds < 100)
        {
            // Expensive to update, cheap to query → use lazy updates
            view.Config.UpdateTiming = UpdateTiming.Lazy;
        }
        else if (updateCost.TotalMicroseconds < 100)
        {
            // Cheap to update → use eager updates
            view.Config.UpdateTiming = UpdateTiming.Eager;
        }

        // Decision: Should we materialize to component?
        if (stats.RebuildCostOnRestart.TotalSeconds > 10)
        {
            _logger.LogWarning("View rebuild cost is high, consider persistence");
            // Suggest to user to enable materialization
        }
    }
}
```

### Summary

**For Typhon, implement:**

1. ✅ **Hybrid Entity Set + Bitmap** (Approaches 1 + 5)
   - Bitmap for entity IDs (memory-efficient)
   - Optional component cache (performance)
   - Delta tracking (game loops)

2. ✅ **Configurable strategies** per view
   - User chooses eager/lazy updates
   - User chooses to cache components or not
   - User chooses to materialize or not

3. ✅ **No default disk persistence**
   - Keep views in RAM for speed
   - Users can opt-in via materialized components

4. ✅ **Adaptive optimization**
   - Views self-tune based on usage
   - Metrics guide optimization decisions

5. ✅ **Delta API for game loops**
   - Essential for real-time simulations
   - Avoids re-processing unchanged data

**This gives you maximum flexibility with excellent performance for game simulations.**
