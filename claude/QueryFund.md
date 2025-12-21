# Fundamental Query Engine Architecture: WHERE Clause Decomposition and Execution

This document analyzes the fundamental design decisions for Typhon's query engine, focusing on WHERE clause decomposition, index utilization, caching strategies, and incremental updates. The analysis prioritizes performance through bandwidth minimization, leverages index statistics for optimization, and emphasizes pipeline execution over bitmap-based approaches.

## Part 1: WHERE Clause Decomposition Strategies

### Strategy A: Unified Expression with Automatic Decomposition

**API Usage:**
```csharp
// Single unified lambda - library decomposes internally
var query = db.Select<Player, Inventory, Guild>()
    .Where((player, inventory, guild) =>
        player.Age > 18 &&
        inventory.Gold > 1000 &&
        guild.Level >= 5 &&
        player.GuildId == guild.Id);  // Cross-component join predicate
```

**Internal Mechanics - Expression Tree Partitioning Algorithm:**

1. **Parse Phase**: Traverse expression tree to identify all Boolean operators (AND, OR, NOT)
2. **Partition Phase**: Build dependency graph where each predicate node tracks which component types it references
3. **Classification Phase**: Categorize into three buckets:
   - **Single-component predicates**: `player.Age > 18` (only touches Player)
   - **Multi-component predicates**: `player.GuildId == guild.Id` (touches Player AND Guild)
   - **Computed predicates**: `player.Strength + inventory.BonusStrength > 100` (requires reading both components)

4. **Optimization Phase**: Build execution plan based on predicate characteristics

**Pros:**
- **Natural C# syntax** - developers write queries like LINQ
- **Complete visibility** - library sees entire WHERE clause, can optimize globally
- **Cross-component optimization** - can detect join conditions and reorder execution
- **Statistics-driven** - can estimate selectivity of entire query before execution

**Cons:**
- **Expression tree complexity** - must handle arbitrary C# expressions
- **Parsing overhead** - happens on first query execution (can be cached)
- **Ambiguous optimization** - library chooses execution order, may surprise users
- **Debugging difficulty** - users don't see how query executes

**Data Bandwidth Characteristics:**
- **Best case**: Library correctly identifies most selective predicate, scans minimal index entries
- **Worst case**: Bad selectivity estimation leads to scanning many entities before filtering

**Solves:** Developer ergonomics, automatic optimization, supports complex cross-component predicates

---

### Strategy B: Explicit Component-Scoped Predicates

**API Usage:**
```csharp
// Explicit decomposition - developer controls execution order
var query = db.Select<Player, Inventory, Guild>()
    .Where<Player>(p => p.Age > 18)          // Executes first
    .Where<Inventory>(i => i.Gold > 1000)    // Executes second on survivors
    .Where<Guild>(g => g.Level >= 5)         // Executes third on survivors
    .WhereJoin((p, g) => p.GuildId == g.Id); // Cross-component predicate
```

**Internal Mechanics - Sequential Filter Pipeline Algorithm:**

1. **Build Phase**: Each `.Where<T>()` call appends a filter stage to pipeline
2. **Compilation Phase**: Each predicate compiles to delegate independently
3. **Execution Phase**:
   - Stage 1 yields entity IDs from most selective component's index
   - Stage 2+ filter survivors by checking their components against predicates
   - Final stage materializes component data only for complete survivors

**Pros:**
- **Explicit execution order** - developer sees exactly what runs when
- **Simple decomposition** - already partitioned by component type
- **Easy to reason about** - linear pipeline model
- **Debugging transparency** - can measure each stage independently
- **Natural caching boundaries** - each stage is independently cacheable

**Cons:**
- **Verbose API** - multiple method calls for complex queries
- **Developer burden** - user must understand which predicate is most selective
- **No automatic optimization** - library can't reorder without breaking API contract
- **Cross-component predicates awkward** - requires separate `.WhereJoin()` method

**Data Bandwidth Characteristics:**
- **Best case**: Developer orders correctly, minimal data read at each stage
- **Worst case**: Developer orders poorly, reads many components before filtering
- **Predictable**: Bandwidth is deterministic based on API call order

**Solves:** Predictability, debuggability, explicit control, natural cache boundaries

---

### Strategy C: Hybrid with Optimization Hints

**API Usage:**
```csharp
// Unified syntax with optional optimization hints
var query = db.Select<Player, Inventory, Guild>()
    .Where((p, i, g) =>
        p.Age > 18 &&
        i.Gold > 1000 &&
        g.Level >= 5 &&
        p.GuildId == g.Id)
    .OptimizeUsing(opt => opt
        .StartWith<Guild>(g => g.Level)     // Hint: Guild.Level is most selective
        .ThenFilter<Player>()                // Then filter players
        .ThenFilter<Inventory>());           // Then filter inventory
```

**Internal Mechanics - Hinted Execution Plan Algorithm:**

1. **Parse Phase**: Decompose unified WHERE into component sub-predicates (like Strategy A)
2. **Hint Processing Phase**: If hints provided, use them to order execution; otherwise use statistics
3. **Plan Building Phase**: Construct execution plan respecting hints but validating against statistics
4. **Fallback Phase**: If hints lead to poor plan (detected by cost estimation), warn and use auto-optimized plan

**Pros:**
- **Best of both worlds** - natural syntax with control when needed
- **Graceful degradation** - hints are optional, library can auto-optimize
- **Learning opportunity** - library can suggest better hints based on actual execution
- **Performance transparency** - hints make execution plan visible

**Cons:**
- **API complexity** - two ways to do same thing (with/without hints)
- **Hint validation overhead** - must check hints don't conflict with query semantics
- **Potential hint staleness** - data distribution changes, hints become outdated
- **Learning curve** - developers must learn when/how to provide hints

**Data Bandwidth Characteristics:**
- **Best case**: Correct hints or good statistics, minimal bandwidth
- **Average case**: Hints slightly off but acceptable, reasonable bandwidth
- **Worst case**: Wrong hints override good statistics, excessive bandwidth (can be detected and warned)

**Solves:** Flexibility, performance control, automatic optimization with escape hatch

---

## Part 2: Index Utilization Strategies (No Bitmaps, Pipeline-Focused)

### Approach A: Sorted Index Merge-Scan Pipeline

**Algorithm Description:**

**Phase 1: Selectivity Estimation**
- For each single-component predicate, query index statistics
- Estimate cardinality: `EstimatedResults = TotalEntities × Selectivity`
- For range queries on indexed fields: use histogram to estimate matching rows
- For equality queries: use MCV (Most Common Values) or assume uniform distribution

**Phase 2: Pipeline Construction**
- Select most selective single-component predicate as **Primary Stream Source**
- Order remaining predicates by increasing estimated cardinality
- Build pipeline stages: Primary Stream → Secondary Filters → Component Materialization

**Phase 3: Execution - Sorted Merge Algorithm**

*Primary Stream Generation:*
- Open index scan on most selective predicate's field
- Use index's natural sort order (e.g., B+Tree in-order traversal)
- Yield entity IDs in sorted order

*Secondary Filter Stages:*
- For each entity ID from primary stream
- For each secondary predicate on different component:
  - Perform index lookup (B+Tree search: O(log N))
  - If indexed field: binary search in sorted index, O(log N)
  - If not indexed: must read component and evaluate, O(1) per component
- Short-circuit on first failed predicate (don't check remaining)

*Materialization Stage:*
- Only for entities passing all filters
- Read actual component data from storage
- Assemble result tuple

**Example Execution:**

Query: `Select<Player, Inventory>().Where((p, i) => p.Age > 18 && i.Gold > 1000)`

Statistics show:
- `Player.Age > 18`: 60% selectivity (60K of 100K players)
- `Inventory.Gold > 1000`: 15% selectivity (15K of 100K inventories)

Execution:
1. Primary Stream: Scan `Inventory.Gold` index starting at value 1001, yields entity IDs in ascending order
2. For each entity ID (15K estimated):
   - Check `Player.Age` index: does this entity have Player.Age > 18?
   - If yes: read both components and yield result
   - If no: skip to next entity ID
3. Expected reads: 15K index lookups + ~9K component reads (15K × 60%)

**Pros:**
- **Minimal materialization** - read component data only for final survivors
- **Index-native** - leverages sorted nature of B+Trees
- **No intermediate sets** - true streaming pipeline
- **Short-circuit friendly** - stops checking predicates on first failure
- **Memory-efficient** - no bitmap allocation, just iterator state

**Cons:**
- **Sequential dependency** - can't parallelize filter stages
- **Index lookup overhead** - O(log N) for each secondary filter check
- **Poor for high-cardinality primary** - if primary stream yields 90% of entities, wastes index lookups

**Data Bandwidth Analysis:**
- **Index bandwidth**: `PrimaryStreamSize × log(IndexSize) × SecondaryPredicateCount` index node reads
- **Component bandwidth**: `ResultSetSize` component reads
- **Optimal when**: Primary stream is highly selective (< 10% of entities)

**Solves:** Streaming execution without materializing intermediate sets, natural fit for indexed fields

---

### Approach B: Multi-Index Intersection via Sorted Merge-Join

**Algorithm Description:**

Instead of primary/secondary distinction, treat all indexed predicates as **equal peers** and merge their sorted streams.

**Phase 1: Stream Initialization**
- For each indexed predicate, open index scan
- Each scan produces entity IDs in sorted ascending order
- Example:
  - Stream A (Player.Age > 18): [5, 12, 18, 23, 45, 67, ...]
  - Stream B (Inventory.Gold > 1000): [12, 18, 34, 45, 91, ...]

**Phase 2: Sorted Merge-Join**
- Maintain cursor for each stream
- Advance all cursors to find entity IDs present in ALL streams
- Classic multi-way merge algorithm:
  1. Read current entity ID from all streams
  2. Find minimum ID across streams
  3. If all streams show same ID: emit as match, advance all cursors
  4. Else: advance cursor(s) showing minimum ID
  5. Repeat until any stream exhausted

**Phase 3: Component Materialization**
- For matched entity IDs, read component data
- Apply any non-indexed predicates
- Yield results

**Example Execution:**

Query: `Select<Player, Inventory>().Where((p, i) => p.Age > 18 && i.Gold > 1000)`

Stream A (Player.Age > 18): [5, 12, 18, 23, 45, 67, 89, 91, ...]
Stream B (Inventory.Gold > 1000): [12, 18, 34, 45, 91, 102, ...]

Merge process:
```
Step 1: A=5,  B=12  → min=5,  not match, advance A
Step 2: A=12, B=12  → match! emit 12, advance both
Step 3: A=18, B=18  → match! emit 18, advance both
Step 4: A=23, B=34  → min=23, not match, advance A
Step 5: A=45, B=34  → min=34, not match, advance B
Step 6: A=45, B=45  → match! emit 45, advance both
...
```

**Pros:**
- **Symmetric treatment** - no artificial primary/secondary distinction
- **Minimal index reads** - each index scanned once sequentially (cache-friendly)
- **Early termination** - if any stream exhausts, entire query completes
- **Scalable to many predicates** - merge algorithm handles N streams efficiently
- **Predictable performance** - cost is sum of stream lengths, not product

**Cons:**
- **Requires all predicates indexed** - can't handle non-indexed fields in merge
- **Sorted order requirement** - all indexes must use same sort order (entity ID)
- **Stream synchronization overhead** - managing multiple cursors
- **Wasted work on sparse matches** - if result set is 1% of smallest stream, scanned 99% unnecessarily

**Data Bandwidth Analysis:**
- **Best case**: Streams are highly correlated, many matches, bandwidth = `sum(StreamLengths)`
- **Worst case**: Streams are anti-correlated, few matches, still bandwidth = `sum(StreamLengths)` but low yield
- **Component bandwidth**: `MatchCount` component reads

**Optimal when**: Multiple predicates with similar selectivity (20-40% each), result set is intersection

**Solves:** Symmetric multi-predicate queries, avoids primary/secondary bias, cache-friendly sequential scans

---

### Approach C: Adaptive Pipeline with Statistics-Driven Reordering

**Algorithm Description:**

Dynamic execution where pipeline stages can **reorder mid-execution** based on observed cardinalities.

**Phase 1: Initial Plan**
- Use statistics to build initial execution order
- Mark plan as "provisional" - can be revised

**Phase 2: Probe Execution**
- Execute first K entities (e.g., K=100) through full pipeline
- Measure actual cardinality at each stage
- Record: `ActualSelectivity[stage] = OutputCount / InputCount`

**Phase 3: Validation**
- Compare actual vs estimated selectivity for each stage
- If deviation > threshold (e.g., 2x difference):
  - Recompute execution plan with actual measurements
  - If new plan differs, switch to it
  - Discard probe results, restart query with new plan

**Phase 4: Full Execution**
- Execute with finalized plan
- Continue monitoring for drastic changes (concept drift)

**Example Execution:**

Query: `Select<Player, Inventory, Guild>().Where((p, i, g) => p.Age > 18 && i.Gold > 1000 && g.Level >= 5)`

Statistics predict:
- Guild.Level >= 5: 20% (most selective)
- Player.Age > 18: 60%
- Inventory.Gold > 1000: 15% (ACTUALLY most selective, but stats outdated)

Initial plan: Guild → Player → Inventory

After probing 100 entities:
- Guild filter: 100 → 22 entities (22% actual, close to 20% estimate)
- Player filter: 22 → 14 entities (64% actual, close to 60% estimate)
- Inventory filter: 14 → 2 entities (14% actual, but wait... this is 14% of input to THIS stage)

Realization: Inventory should have been primary stream!

Revised plan: Inventory → Guild → Player

Restart execution with correct plan.

**Pros:**
- **Self-correcting** - adapts to stale statistics
- **Handles changing data** - detects distribution drift
- **Optimal long-term** - converges to best plan
- **Resilient to estimation errors** - doesn't commit to bad plan

**Cons:**
- **Probe overhead** - first K entities processed twice if plan changes
- **Complexity** - must implement plan switching mid-execution
- **Thrashing risk** - data distribution changes during execution, plan flip-flops
- **Latency spike** - plan revision adds latency to first query execution

**Data Bandwidth Analysis:**
- **With plan revision**: Bandwidth = `ProbeSize × FullPipeline + FullDataset × OptimalPipeline`
- **Without revision**: Bandwidth = `FullDataset × OptimalPipeline`
- **Worthwhile when**: Probe overhead << savings from optimal plan, i.e., large datasets

**Solves:** Stale statistics problem, dynamic data distributions, long-running queries on changing data

---

## Part 3: Library-Level Sub-Query Caching

### The Core Question: Should `Player.Age > 18` Be Cached Globally?

Let's analyze the mechanics and trade-offs of caching at different granularities.

### Granularity Option 1: View-Level Caching (Current Baseline)

**Mechanics:**
- Each View instance caches its own complete result set
- Query: `Select<Player>().Where(p => p.Age > 18)` creates View A
- Another query: `Select<Player, Inventory>().Where((p, i) => p.Age > 18 && i.Gold > 1000)` creates View B
- Views A and B both evaluate `Player.Age > 18` independently
- No sharing between views

**Cache Invalidation Algorithm:**
- Track which component types each view depends on
- When Player component modified: invalidate all views depending on Player
- Coarse-grained: even if Age didn't change, view invalidated

**Pros:**
- **Simple isolation** - views don't interfere with each other
- **Clear ownership** - each view manages its own cache
- **Predictable memory** - user controls view lifetime
- **No coordination overhead** - no global cache locks

**Cons:**
- **Massive duplication** - `Player.Age > 18` result recomputed for every view using it
- **Wasted invalidations** - changing Player.Name invalidates views filtering on Player.Age
- **Missed optimization opportunities** - library can't leverage common sub-expressions

**Data Bandwidth:**
- Each view performs full index scan on creation
- Total bandwidth = `NumberOfViews × IndexScanCost`

**Solves:** Simplicity, isolation, predictable behavior

---

### Granularity Option 2: Predicate-Level Caching (Library-Managed)

**Mechanics:**

**Cache Structure:**
- Global cache maps `PredicateSignature → EntityIDSet`
- Signature includes: Component type, field, operator, value
- Example: `{ComponentType: Player, Field: Age, Operator: GreaterThan, Value: 18} → {5, 12, 18, 23, ...}`

**Cache Population Algorithm:**
- On first query using `Player.Age > 18`:
  1. Check global cache for matching signature
  2. If miss: execute index scan, populate cache
  3. If hit: return cached entity ID set
- Subsequent queries using same predicate: instant cache hit

**Cache Invalidation Algorithm - Field-Level Granularity:**
- Track which fields each cached predicate depends on
- When component updated:
  1. Identify which fields changed (compare old vs new values)
  2. Invalidate only predicates depending on changed fields
  3. Example: Updating Player.Name doesn't invalidate `Player.Age > 18` cache

**Incremental Update Algorithm:**
- When Player entity ID=123 has Age change from 17 → 19:
  1. Check if entity 123 currently in `Player.Age > 18` cache
  2. Old value (17) didn't match, new value (19) does match
  3. **Add** entity 123 to cached set
  4. Update is O(1), no full re-scan needed!

**Cache Key Generation:**
- Constant value predicates: `Player.Age > 18` → cache key includes literal "18"
- Parameterized queries: `Player.Age > @minAge` → separate cache entry per parameter value
- Range queries: `Player.Age BETWEEN 18 AND 65` → single cache key

**Pros:**
- **Massive deduplication** - `Player.Age > 18` cached once, used by all views
- **Incremental updates** - O(1) to update cache when single entity changes
- **Field-level invalidation** - changing unrelated fields doesn't invalidate
- **Automatic optimization** - all queries benefit from caching without opt-in

**Cons:**
- **Unbounded memory growth** - every unique predicate creates cache entry
- **Cache key complexity** - must canonicalize expressions (Age > 18 == 18 < Age?)
- **Eviction policy complexity** - which predicates to evict when memory constrained?
- **Concurrency overhead** - global cache needs thread synchronization
- **Stale cache risk** - if invalidation has bugs, queries return wrong results

**Data Bandwidth:**
- First query: Full index scan
- Subsequent queries: Zero index bandwidth (cache hit)
- Updates: Single entity lookup O(log N) to determine add/remove from cache

**Solves:** Deduplication across views, incremental maintenance, field-level precision

---

### Granularity Option 3: Hot Predicate Adaptive Caching

**Mechanics:**

**Observation Phase:**
- Library tracks predicate execution frequency without caching
- Count: How many times has `Player.Age > 18` been evaluated in last N minutes?
- Cost: How expensive is this predicate? (index size, selectivity)

**Promotion Algorithm:**
- Predicate becomes "hot" if: `Frequency × Cost > Threshold`
- Example:
  - `Player.Age > 18` executed 100 times/minute, cost 50ms → score 5000
  - Threshold is 1000 → PROMOTE to library cache
  - `Guild.Founder == "Alice"` executed 2 times/hour, cost 10ms → score 0.33 → DON'T cache

**Cache Management:**
- Only hot predicates get library-level cache entries
- Cold predicates evaluated normally (view-level caching only)
- Continuous monitoring: if hot predicate becomes cold, evict from library cache

**Demotion Algorithm:**
- If cached predicate's access frequency drops below threshold for sustained period:
  - Mark for eviction
  - After grace period with no access: remove from library cache
  - Memory freed for other hot predicates

**Pros:**
- **Bounded memory** - only hot predicates cached, automatic eviction
- **Self-tuning** - adapts to workload patterns
- **No manual configuration** - library decides what to cache
- **Focused benefit** - optimization effort spent on high-impact predicates

**Cons:**
- **Cold start** - first queries don't benefit, must warm up
- **Oscillation risk** - predicates bouncing between hot/cold waste effort
- **Tuning complexity** - must set appropriate thresholds
- **Monitoring overhead** - tracking frequency adds CPU cost

**Data Bandwidth:**
- Hot predicates: Zero bandwidth after warming
- Cold predicates: Full index scan every time
- Adaptive: Bandwidth decreases as workload stabilizes

**Solves:** Memory efficiency, workload adaptation, automatic optimization

---

## Part 4: Cross-Component Predicate Handling

### The Join Problem in Multi-Component Queries

Query: `Select<Player, Guild>().Where((p, g) => p.Age > 18 && p.GuildId == g.Id && g.Level >= 5)`

This contains:
- **Single-component predicates**: `p.Age > 18`, `g.Level >= 5`
- **Cross-component join predicate**: `p.GuildId == g.Id`

### Approach A: Navigation-First (ECS Entity Reference Pattern)

**Algorithm:**

**Phase 1: Identify Navigation Predicate**
- Detect `p.GuildId == g.Id` is entity reference navigation
- GuildId is stored in Player component as foreign key
- This creates Parent→Child relationship: Player navigates to Guild

**Phase 2: Choose Navigation Direction**
- **Forward navigation**: Start with Player, lookup Guild by Player.GuildId
- **Reverse navigation**: Start with Guild, find all Players with matching GuildId

Direction chosen by cardinality:
- If few Players, many Guilds → forward (start with Player)
- If few Guilds, many Players → reverse (start with Guild)

**Phase 3: Execution**

*Forward Navigation Example* (assuming Player is selective):
1. Execute `Player.Age > 18` → yields entity IDs [5, 12, 23, 45, ...]
2. For each Player entity:
   - Read Player component to get GuildId value
   - Navigation step: lookup Guild entity by GuildId (O(1) direct access, not index scan!)
   - Check `Guild.Level >= 5` predicate
   - If passes: yield (Player, Guild) tuple

*Reverse Navigation Example* (assuming Guild is selective):
1. Execute `Guild.Level >= 5` → yields Guild entity IDs [101, 205, 312, ...]
2. For each Guild entity:
   - Reverse lookup: scan Player.GuildId index for entities where GuildId = 101
   - Check `Player.Age > 18` on each matched Player
   - Yield all (Player, Guild) tuples that pass

**Pros:**
- **Leverages ECS design** - entity IDs are direct references, O(1) lookup
- **Minimal overhead** - navigation is pointer dereference, not join
- **Natural for hierarchical data** - player→guild→alliance chains
- **No temporary storage** - streaming pipeline

**Cons:**
- **Requires foreign key semantics** - must know GuildId references Guild entity
- **Direction matters** - wrong choice wastes component reads
- **Broken references** - if GuildId points to non-existent guild, must handle gracefully

**Data Bandwidth:**
- Forward: `SelectiveComponentResultCount` component reads + `ResultCount` navigated component reads
- Reverse: `NavigatedComponentResultCount` index scans + filtered component reads
- Optimal: Start with most selective component, navigate to related components

**Solves:** ECS-native joins, leverages entity reference design, minimal overhead

---

### Approach B: Two-Phase Filtering with Deferred Join

**Algorithm:**

**Phase 1: Independent Filtering**
- Execute all single-component predicates independently
- `Player.Age > 18` → EntitySet A (60K entities)
- `Guild.Level >= 5` → EntitySet B (500 entities)
- Do NOT materialize sets, keep as index iterators

**Phase 2: Join Predicate Evaluation**
- Choose smaller set as "probe side" (Guild with 500 entities)
- For each Guild entity in set B:
  - Read Guild.Id value
  - Scan Player.GuildId index for all Players with GuildId = Guild.Id
  - Check if those Players exist in set A (Age > 18 filter)
  - Yield matching (Player, Guild) pairs

**Optimization: Probe-Side Index Lookup**
- Instead of scanning Player.GuildId index linearly:
- Perform index seek: `PlayerGuildIdIndex.Seek(guildId)` → O(log N)
- Returns all Players with that GuildId directly

**Pros:**
- **Decouples filtering from joining** - can optimize each phase independently
- **Exploits small intermediate results** - uses smaller set as probe
- **Index-friendly** - seeks rather than scans
- **Parallelizable** - can filter components concurrently before join

**Cons:**
- **Two-pass algorithm** - filtering then joining, not single pipeline
- **Materialization risk** - may need to store intermediate sets if not careful
- **Duplication** - reads Guild.Id multiple times (once per matching Player)

**Data Bandwidth:**
- Phase 1: Two independent index scans
- Phase 2: `SmallerSetSize × log(LargerIndex)` index lookups + component reads
- Total: Higher than navigation-first but more flexible

**Solves:** Complex joins with multiple filtered components, exploits index seeks

---

## Part 5: Incremental Update Mechanics for Cached Queries

### The Fundamental Challenge

When `Player entity ID=123` updates `Age: 17 → 19`, how do we maintain cached results efficiently?

### Update Strategy A: Fine-Grained Delta Tracking

**Algorithm:**

**Phase 1: Change Detection**
- Transaction commits with modified Player ID=123
- Compare old vs new component values:
  - `OldAge = 17`, `NewAge = 19` → Age changed
  - `OldName = "Alice"`, `NewName = "Alice"` → Name unchanged
- Generate field-level change event: `{EntityId: 123, ComponentType: Player, ChangedFields: [Age], OldValues: {Age: 17}, NewValues: {Age: 19}}`

**Phase 2: Affected Predicate Identification**
- Query predicate cache/view registry: which predicates depend on Player.Age?
- Find: `Player.Age > 18` used by Views X, Y, Z and library cache entry L

**Phase 3: Incremental Update**
For each affected predicate cache:
1. Evaluate old value against predicate: `17 > 18` → FALSE (wasn't in result set)
2. Evaluate new value against predicate: `19 > 18` → TRUE (should be in result set)
3. Determination: ADD entity 123 to cached result set
4. Update operation: O(1) set insertion

**All Possible Cases:**
- Old FALSE, New FALSE → no change (ignore update)
- Old FALSE, New TRUE → ADD to cache
- Old TRUE, New FALSE → REMOVE from cache
- Old TRUE, New TRUE → no change (entity stays in result)

**Phase 4: View Notification**
- Views subscribing to this predicate receive delta notification:
  - `{Added: [123], Removed: [], Modified: []}`
- View updates its own cache accordingly

**Pros:**
- **O(1) update cost** - single entity evaluation
- **Precise invalidation** - only affected predicates updated
- **Delta propagation** - views get incremental changes, not full recompute
- **Scalable** - cost independent of result set size

**Cons:**
- **Field-level tracking overhead** - must diff old vs new for every update
- **Complex dependency graph** - must maintain predicate→field mappings
- **Memory for old values** - must keep old component snapshot for comparison
- **Cascading updates** - one entity change triggers multiple cache updates

**Data Bandwidth:**
- Per update: Zero index bandwidth (predicate re-evaluation on single entity)
- Aggregate: `UpdateCount × AffectedPredicateCount` predicate evaluations

**Solves:** True incremental maintenance, minimal recomputation, delta-driven UI updates

---

### Update Strategy B: Lazy Invalidation with Version Tagging

**Algorithm:**

**Phase 1: Version Assignment**
- Database maintains global version counter: `CurrentVersion = 12,543`
- Each component update increments version
- Each cached predicate result tagged with version: `{Predicate: "Player.Age > 18", ResultSet: [...], ValidAsOfVersion: 12,540}`

**Phase 2: Invalidation on Write**
- When Player component updates:
  - Increment `CurrentVersion → 12,544`
  - Mark all caches depending on Player component as "potentially stale"
  - Do NOT recompute immediately (lazy)

**Phase 3: Lazy Revalidation on Read**
- View reads predicate cache: `GetCachedResult("Player.Age > 18")`
- Check: `Cache.ValidAsOfVersion (12,540) < CurrentVersion (12,544)`
- Determination: Cache is stale
- Action: Re-execute query, update cache, tag with new version

**Optimization: Partial Revalidation**
- Instead of full re-execution, can scan updates since version 12,540
- Read transaction log entries for Player component changes
- Apply incremental updates (similar to Strategy A) but only when cache accessed

**Pros:**
- **Zero write-time cost** - updates just increment version, no cache maintenance
- **Simple implementation** - version tagging is straightforward
- **Natural consistency** - version numbers provide total ordering
- **Defers work** - only recomputes caches that are actually accessed

**Cons:**
- **Read latency spikes** - first read after updates pays full recomputation cost
- **Stale reads** - if version not checked, could serve stale data
- **Transaction log dependency** - requires keeping log of changes
- **Wasted recomputation** - if view never accessed, update effort wasted (actually a pro!)

**Data Bandwidth:**
- Writes: Zero bandwidth
- Reads: Full index scan on first access after staleness
- Amortized: Low if views accessed infrequently, high if accessed constantly

**Solves:** Write-optimized workloads, reduces write amplification, eventual consistency

---

## Part 6: Recommended Hybrid Architecture

After analyzing all approaches, here's the architecture that best addresses the requirements for performance, clean API, and incremental updates:

### Foundation: Explicit Decomposition with Auto-Optimization

**API Design:**
```csharp
// Developer writes natural unified lambda
var query = db.Select<Player, Inventory, Guild>()
    .Where((p, i, g) =>
        p.Age > 18 &&
        i.Gold > 1000 &&
        g.Level >= 5 &&
        p.GuildId == g.Id);

// Library automatically decomposes into execution plan
// But provides transparent inspection API
var plan = query.GetExecutionPlan();
// Shows: "Guild.Level (500 est) → Player.Age (300 est) → Navigation(GuildId) → Inventory.Gold (45 est final)"
```

**Why:** Natural C# syntax for developers, but library has full visibility for optimization.

---

### Execution: Sorted Index Merge Pipeline (No Bitmaps)

**Algorithm:**
1. **Plan Construction**: Use index statistics to order single-component predicates by selectivity
2. **Primary Stream**: Most selective indexed predicate generates sorted entity ID stream
3. **Secondary Filters**: Pipeline stages perform index seeks (O(log N)) for each entity
4. **Navigation Joins**: When detecting FK relationship, switch to O(1) entity navigation
5. **Materialization**: Final stage reads components only for complete survivors

**Why:** Avoids bitmap materialization, minimizes data bandwidth, leverages index sort order.

---

### Caching: Two-Tier with Hot Predicate Promotion

**Tier 1: View-Level (Always Active)**
- Each view caches its complete result set
- Managed by view lifecycle (developer controls)
- Simple, predictable, isolated

**Tier 2: Library Predicate Cache (Adaptive)**
- Monitor predicate execution frequency
- Promote to library cache only if: `Frequency × Cost > Threshold`
- Field-level granular invalidation
- Incremental delta tracking for hot predicates
- Automatic eviction when cold

**Why:** Balances memory efficiency (bounded growth) with performance (caches what matters), no configuration needed.

---

### Invalidation: Fine-Grained with Field-Level Deltas

**Algorithm:**
1. On component update: diff old vs new values, identify changed fields
2. Lookup predicate dependency graph: which predicates depend on changed fields?
3. For each affected predicate:
   - Evaluate old value against predicate → was it in result?
   - Evaluate new value against predicate → should it be in result?
   - Generate delta: ADD, REMOVE, or NO-CHANGE
4. Propagate deltas to all views subscribing to that predicate

**Why:** O(1) update cost, precise invalidation, enables true incremental maintenance for game loops.

---

### Performance Characteristics

**Query Execution:**
- First execution: `SelectivityBest × log(N) × PredicateCount` index operations
- Subsequent (cached): Zero index operations for hot predicates
- Component reads: `ResultSetSize` only (minimal bandwidth)

**Updates:**
- Write cost: `O(1)` per affected predicate (delta computation)
- No full re-scan needed
- Hot predicates maintained incrementally

**Memory:**
- View caches: User-controlled lifetime
- Library predicate cache: Bounded by hot predicate threshold, auto-eviction
- No unbounded growth

---

## Summary

This architecture delivers on all requirements:

✅ **Safe C# API** - Natural lambda syntax with fluent builder pattern
✅ **Pipeline execution** - No bitmap materialization, streaming index scans
✅ **Minimal bandwidth** - Read only necessary index nodes and components
✅ **Multi-level caching** - View-level + adaptive library-level predicate cache
✅ **Incremental updates** - Field-level change detection with O(1) delta propagation
✅ **Index-driven performance** - Statistics-based selectivity estimation and execution ordering
✅ **Game loop friendly** - Delta tracking for added/removed/modified entities between queries

The design prioritizes data bandwidth minimization through intelligent index utilization, avoids full table/index scans via statistics-driven planning, and supports incremental maintenance for the repetitive query patterns common in game simulations.
