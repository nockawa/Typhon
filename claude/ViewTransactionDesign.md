# View and Transaction Integration Design

## The Core Problem

Typhon uses MVCC (Multi-Version Concurrency Control) where every transaction has a timestamp (tick) that defines its snapshot point-in-time:

```csharp
using var t = db.CreateTransaction();  // Gets tick = 1000
// Transaction sees database as of tick 1000
// Newer commits (tick > 1000) are invisible to this transaction
```

**The Tension:**
- **Queries need transaction context** to establish a consistent snapshot
- **Views need to persist** beyond transaction lifetime and update incrementally as new commits happen
- **How do we reconcile these?**

---

## Design Option 1: Views Operate on "Latest Committed" State (No Transaction Context)

### API Design

```csharp
// Create view - no transaction needed
var youngPlayers = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// View always reads latest committed versions
foreach (var (entityId, player) in youngPlayers)
{
    // player reflects the most recent committed state
    Console.WriteLine($"Player {entityId}: Age = {player.Age}");
}

// When a transaction commits, view updates automatically
using (var t = db.CreateTransaction())  // tick = 1001
{
    t.UpdateEntity(123, ref somePlayer);
    t.Commit();  // Triggers incremental view update
}

// View now reflects the commit at tick 1001
```

### Internal Mechanics

**View Snapshot Model:**
```csharp
public class View<TC1, TC2>
{
    // View tracks its own "effective snapshot" timestamp
    private long _currentSnapshotTick;

    // View cache stores components from that snapshot
    private Dictionary<long, (TC1, TC2)> _components;

    public IEnumerable<(long, TC1, TC2)> GetAll()
    {
        // Reads from cache without transaction context
        // Cache reflects latest committed state
        foreach (var kvp in _components)
        {
            yield return (kvp.Key, kvp.Value.Item1, kvp.Value.Item2);
        }
    }
}
```

**Incremental Update Trigger:**
```csharp
// In Transaction.Commit()
public bool Commit()
{
    // ... MVCC commit logic ...

    if (commitSuccessful)
    {
        var committedTick = this.Tick;  // e.g., 1001

        // Update all affected views to reflect this commit
        foreach (var view in affectedViews)
        {
            view.IncrementalUpdate(this, committedTick);
        }
    }
}

// In View
internal void IncrementalUpdate(Transaction committedTxn, long newTick)
{
    // Process field changes from the committed transaction
    foreach (var change in committedTxn.GetChanges())
    {
        // Re-evaluate predicates with new committed values
        // Update cache to reflect new snapshot at 'newTick'
    }

    _currentSnapshotTick = newTick;  // Advance view's snapshot
}
```

### Pros

**1. Simple Mental Model**
- Views always show "current reality" (latest committed state)
- No need to think about transaction lifetimes for views
- Natural for game simulations: UI always displays current game state

**2. No Transaction Overhead for Reads**
- Reading from view doesn't require transaction creation
- Zero cost for repeated view queries
- Perfect for high-frequency reads (e.g., rendering at 60 FPS)

**3. Automatic Staleness Prevention**
- View is always up-to-date after any commit
- No manual refresh needed
- Incremental updates happen automatically

**4. Clean API**
```csharp
// Create view once
var enemies = db.Query<Enemy, Position>()
    .Where((e, pos) => IsNear(pos, player))
    .ToView();

// Use throughout game loop - always current
void OnRenderFrame()
{
    foreach (var (id, enemy, pos) in enemies)
    {
        RenderEnemy(id, enemy, pos);
    }
}
```

### Cons

**1. No Consistent Multi-View Snapshots**
```csharp
// Problem: Two views see different snapshots!
var view1 = db.Query<Player>().Where(p => p.Age < 18).ToView();
var view2 = db.Query<Inventory>().Where(i => i.Gold > 1000).ToView();

// Transaction commits between view reads
foreach (var p in view1) { /* ... */ }
// ← Another transaction commits here, changing inventory
foreach (var i in view2) { /* ... */ }  // Different snapshot!
```

User sees inconsistent data: Player age from tick 1000, Inventory from tick 1001.

**2. Lost Snapshot Isolation Guarantees**
- MVCC's core benefit is repeatable reads within a transaction
- Views don't have this guarantee
- Can't perform complex multi-step analysis on consistent snapshot

**3. Phantom Reads in Loops**
```csharp
// First iteration reads view
foreach (var (id, player) in youngPlayers)
{
    if (player.Age < 10)
    {
        // Commit happens during loop!
        // Next iteration sees different data
    }
}
```

**4. View Modification During Enumeration**
```csharp
foreach (var (id, player) in youngPlayers)
{
    // User commits a change inside loop
    using (var t = db.CreateTransaction())
    {
        t.UpdateEntity(999, ref newPlayer);
        t.Commit();  // Updates 'youngPlayers' view NOW!
    }
    // Enumeration may throw: "Collection was modified"
}
```

Must implement copy-on-write or defensive copying.

### Best For

- **Game rendering loops**: Always want latest visual state
- **Dashboards/UI**: Display current metrics
- **High-frequency queries**: 60+ FPS rendering where transaction overhead is unacceptable
- **Fire-and-forget queries**: Don't need multi-view consistency

---

## Design Option 2: Views Pin to Transaction Snapshot (Transaction-Bound Views)

### API Design

```csharp
// Views are created WITHIN a transaction
using var t = db.CreateTransaction();  // tick = 1000

// View sees database as of tick 1000
var youngPlayers = t.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// View reads consistent snapshot
foreach (var (id, player) in youngPlayers)
{
    // player reflects state at tick 1000
    // Even if other commits happen, view stays at tick 1000
}

// View is tied to transaction lifetime
// When transaction disposed, view becomes invalid
```

### Internal Mechanics

**View as Transaction Child:**
```csharp
public class View<TC1, TC2>
{
    private readonly Transaction _owningTransaction;
    private readonly long _snapshotTick;

    public View(Transaction txn)
    {
        _owningTransaction = txn;
        _snapshotTick = txn.Tick;  // Pin to transaction's snapshot
    }

    public IEnumerable<(long, TC1, TC2)> GetAll()
    {
        if (_owningTransaction.IsDisposed)
            throw new InvalidOperationException("View's transaction is disposed");

        // Read components as of _snapshotTick
        foreach (var entityId in _cachedEntityIds)
        {
            _owningTransaction.ReadEntityAtSnapshot(entityId, _snapshotTick, out TC1 c1);
            _owningTransaction.ReadEntityAtSnapshot(entityId, _snapshotTick, out TC2 c2);
            yield return (entityId, c1, c2);
        }
    }
}
```

**No Incremental Updates:**
- Views are immutable snapshots
- When new transaction created, must create new view
- Old views don't update (they're frozen at their snapshot tick)

### Pros

**1. Perfect Snapshot Isolation**
- View guarantees repeatable reads throughout its lifetime
- Multiple views in same transaction see identical state
- True MVCC semantics

**2. Consistent Multi-View Queries**
```csharp
using var t = db.CreateTransaction();  // tick = 1000

var view1 = t.Query<Player>().Where(p => p.Age < 18).ToView();
var view2 = t.Query<Inventory>().Where(i => i.Gold > 1000).ToView();

// Both views see identical snapshot at tick 1000
// Can perform complex analysis across views
var richKids = view1.Join(view2, p => p.EntityId, i => i.EntityId);
```

**3. No Collection Modification Issues**
- View is read-only snapshot
- Commits don't affect view during iteration
- Safe concurrent enumeration

**4. Explicit Refresh Model**
```csharp
// User controls when to see new data
void OnGameTick()
{
    using var t = db.CreateTransaction();  // New snapshot
    var enemies = t.Query<Enemy>().Where(e => e.IsAlive).ToView();

    // Process this snapshot
    foreach (var enemy in enemies)
    {
        UpdateAI(enemy);
    }

    // Next tick, create new transaction and new view
}
```

### Cons

**1. NO Incremental Updates**
- Every refresh requires full query re-execution
- Can't leverage cached results from previous snapshots
- Performance: ~50K-100K operations per refresh

**2. Transaction Lifetime Management Complexity**
```csharp
// Problem: View becomes invalid when transaction disposed
View<Player> youngPlayers;

using (var t = db.CreateTransaction())
{
    youngPlayers = t.Query<Player>().Where(p => p.Age < 18).ToView();
}
// Transaction disposed here

foreach (var player in youngPlayers)  // THROWS! Transaction disposed
{
    // Can't use view outside transaction scope
}
```

**3. Persistent View Pattern Impossible**
- Can't have long-lived views that update automatically
- Must manually recreate view every frame/tick
- High overhead for high-frequency queries

**4. Memory Pressure**
```csharp
// Game loop at 60 FPS
void OnRenderFrame()
{
    using var t = db.CreateTransaction();  // 60 transactions/sec
    var enemies = t.Query<Enemy>().Where(...).ToView();  // Full re-query 60x/sec

    // Re-executes entire pipeline 60 times per second!
}
```

### Best For

- **Analytical queries**: Complex multi-view analysis requiring consistent snapshot
- **Reporting**: Generate reports from fixed point-in-time
- **Batch processing**: Process entities in consistent snapshot
- **Low-frequency queries**: Occasional queries where re-execution cost is acceptable

---

## Design Option 3: Views Use "Advancing Snapshot" with Lazy Transaction Context

### API Design

```csharp
// Create view without transaction (like Option 1)
var youngPlayers = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// View internally creates short-lived transactions for reads
foreach (var (id, player) in youngPlayers)
{
    // Internally: view creates micro-transaction for this read
    // Sees consistent snapshot for THIS enumeration
}

// Next enumeration may see newer snapshot
foreach (var (id, player) in youngPlayers)
{
    // New micro-transaction, potentially newer snapshot
}
```

### Internal Mechanics

**Micro-Transaction per Enumeration:**
```csharp
public class View<TC1, TC2>
{
    private readonly DatabaseEngine _db;
    private HashSet<long> _cachedEntityIds;

    public IEnumerable<(long, TC1, TC2)> GetAll()
    {
        // Create micro-transaction for this enumeration
        using var microTxn = _db.CreateTransaction();

        // Read all components within this consistent snapshot
        foreach (var entityId in _cachedEntityIds)
        {
            microTxn.ReadEntity(entityId, out TC1 c1);
            microTxn.ReadEntity(entityId, out TC2 c2);
            yield return (entityId, c1, c2);
        }

        // Transaction disposed at end of enumeration
    }
}
```

**Incremental Updates Still Work:**
```csharp
internal void IncrementalUpdate(Transaction committedTxn)
{
    // Update cached entity IDs (which entities are in view)
    foreach (var change in committedTxn.GetChanges())
    {
        if (ShouldAddEntity(change))
            _cachedEntityIds.Add(change.EntityId);
        else if (ShouldRemoveEntity(change))
            _cachedEntityIds.Remove(change.EntityId);
    }

    // But DON'T cache component data
    // Component data is always read fresh via micro-transaction
}
```

### Pros

**1. Consistent Per-Enumeration**
```csharp
// Each loop sees consistent snapshot
foreach (var (id, player) in youngPlayers)
{
    // All reads in THIS loop see same tick
    // No phantom reads within this enumeration
}
```

**2. Incremental Updates for Entity IDs**
- View cache tracks which entities match (entity ID set)
- Doesn't cache component data (read fresh each time)
- Incremental updates are O(1) for entity ID tracking

**3. Automatic Snapshot Advancement**
- Each enumeration sees latest committed state
- No manual refresh needed
- Natural for game loops

**4. Safe Enumeration**
```csharp
foreach (var player in youngPlayers)
{
    // Commits during loop don't affect THIS enumeration
    // (Micro-transaction locks in snapshot at start of loop)
}
```

### Cons

**1. Component Read Overhead**
- Every enumeration reads components fresh from MVCC storage
- Can't leverage cached component data
- Cost: ~10K-50K component reads per enumeration (for 10K entity view)

**2. Still No Multi-View Consistency**
```csharp
// Each view creates its own micro-transaction
foreach (var player in playerView) { /* tick 1000 */ }
foreach (var inv in inventoryView) { /* tick 1001 - different! */ }
```

**3. Transaction Creation Overhead**
```csharp
// Game loop at 60 FPS, 3 views
void OnRenderFrame()
{
    foreach (var enemy in enemyView) { }      // Micro-txn 1
    foreach (var player in playerView) { }    // Micro-txn 2
    foreach (var item in itemView) { }        // Micro-txn 3

    // Creates 180 transactions per second!
}
```

Transaction creation cost: ~1-5 microseconds each
- 180 txns/sec × 3µs = 540µs per frame = 3% frame budget at 60 FPS

**4. Can't Cache Component Data**
- Cached components would go stale
- Must read fresh every time
- Loses major performance benefit of views

### Best For

- **Moderate-frequency queries**: ~1-10 times per second
- **Consistency-sensitive but not multi-view**: Need snapshot isolation within each query
- **Memory-constrained**: Can't cache component data

---

## Design Option 4: Dual-Mode Views with Explicit Transaction Binding

### API Design

```csharp
// Mode 1: Auto-updating persistent view (no transaction)
var persistentView = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView(ViewMode.Persistent);

// View updates automatically, reads latest committed state
foreach (var player in persistentView)
{
    // Sees latest committed state, no transaction context
}

// Mode 2: Transaction-bound snapshot view
using var t = db.CreateTransaction();
var snapshotView = t.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView(ViewMode.Snapshot);

// View is frozen at transaction's tick, consistent snapshot
foreach (var player in snapshotView)
{
    // Sees fixed snapshot at transaction's tick
}

// Mode 3: Bind persistent view to transaction for consistent read
using (var t = db.CreateTransaction())
{
    var boundView = persistentView.BindToTransaction(t);

    foreach (var player in boundView)
    {
        // Reads from persistent view's cache
        // But enforces snapshot at transaction's tick
        // If cached data is from newer tick, reads from MVCC storage
    }
}
```

### Internal Mechanics

**View Supports Multiple Access Patterns:**
```csharp
public class View<TC1, TC2>
{
    // Cache always reflects latest committed state
    private Dictionary<long, (TC1, TC2, long tick)> _componentCache;

    // Mode 1: Direct enumeration (latest committed)
    public IEnumerable<(long, TC1, TC2)> GetAll()
    {
        foreach (var kvp in _componentCache)
        {
            yield return (kvp.Key, kvp.Value.Item1, kvp.Value.Item2);
        }
    }

    // Mode 2: Transaction-bound enumeration (snapshot)
    public IEnumerable<(long, TC1, TC2)> GetAll(Transaction txn)
    {
        var snapshotTick = txn.Tick;

        foreach (var (entityId, (c1, c2, cachedTick)) in _componentCache)
        {
            if (cachedTick <= snapshotTick)
            {
                // Cached data is from before snapshot, can use it
                yield return (entityId, c1, c2);
            }
            else
            {
                // Cached data is newer than snapshot, read from MVCC
                txn.ReadEntityAtSnapshot(entityId, snapshotTick, out TC1 c1Old);
                txn.ReadEntityAtSnapshot(entityId, snapshotTick, out TC2 c2Old);
                yield return (entityId, c1Old, c2Old);
            }
        }
    }
}

// Fluent API for binding
public class BoundView<TC1, TC2>
{
    private readonly View<TC1, TC2> _view;
    private readonly Transaction _txn;

    public IEnumerable<(long, TC1, TC2)> GetAll()
    {
        return _view.GetAll(_txn);  // Delegates to transaction-bound read
    }
}
```

**Incremental Updates:**
```csharp
internal void IncrementalUpdate(Transaction committedTxn)
{
    var newTick = committedTxn.Tick;

    foreach (var change in committedTxn.GetChanges())
    {
        // Update cache with new committed state
        if (ShouldBeInView(change.NewComponent))
        {
            _componentCache[change.EntityId] = (c1, c2, newTick);
        }
        else
        {
            _componentCache.Remove(change.EntityId);
        }
    }
}
```

### Pros

**1. Flexibility**
- Choose consistency model per use case
- Persistent views for real-time rendering
- Transaction-bound for analytical queries
- Mix both in same application

**2. Cache Reuse Across Modes**
```csharp
// Cache built once, used in multiple ways
var view = db.Query<Player>().Where(p => p.Age < 18).ToView();

// High-frequency: Use persistent mode (fast)
void OnRenderFrame()
{
    foreach (var player in view) { /* Latest state */ }
}

// Low-frequency: Use transaction-bound (consistent)
void OnAnalyzeState()
{
    using var t = db.CreateTransaction();
    var bound = view.BindToTransaction(t);

    // Multi-view consistent analysis
    foreach (var player in bound) { /* Snapshot */ }
}
```

**3. Incremental Updates Work**
- View cache updates automatically
- Both modes benefit from cached entity ID set
- Transaction-bound mode reads older versions when needed via MVCC

**4. Explicit Consistency Control**
```csharp
// Developer chooses when consistency matters
using (var t = db.CreateTransaction())
{
    var view1 = persistentView1.BindToTransaction(t);
    var view2 = persistentView2.BindToTransaction(t);

    // Now both views see identical snapshot
}
```

### Cons

**1. API Complexity**
- Two ways to read views (direct vs bound)
- Must understand when to use each mode
- Learning curve for developers

**2. Cache Complexity**
- Must store tick with each cached component
- Transaction-bound reads may need MVCC fallback
- Complexity in determining if cached data is valid for given tick

**3. Potential Performance Pitfall**
```csharp
// If cache is frequently newer than transaction tick
using var t = db.CreateTransaction();  // tick 1000
var bound = veryActiveView.BindToTransaction(t);

foreach (var item in bound)
{
    // Cache has data from tick 1005, 1010, 1015...
    // Must read from MVCC for EVERY item!
    // Defeats caching entirely
}
```

**4. Memory Overhead**
- Must store tick per cached component: +8 bytes per entity
- For 10K entity view: +80KB overhead

### Best For

- **Hybrid applications**: Need both real-time and analytical queries
- **Complex simulations**: Want control over consistency vs performance trade-offs
- **Long-lived views**: Persistent views that occasionally need snapshot reads

---

## Design Option 5: Views as Materialized MVCC Components

### API Design

```csharp
// Define view as a special component type
[Component]
[MaterializedView]
public struct YoungPlayerView
{
    [Field] public long EntityId;  // References Player entity
    [Field] public int Age;        // Denormalized from Player
    [Field] public String64 Name;  // Denormalized from Player
}

// Register view with query definition
db.RegisterMaterializedView<YoungPlayerView>(
    query: db.Query<Player>().Where(p => p.Age < 18),
    mapper: (player) => new YoungPlayerView
    {
        EntityId = player.EntityId,
        Age = player.Age,
        Name = player.Name
    }
);

// Use view like any component - within transaction!
using var t = db.CreateTransaction();  // tick = 1000

// Query the materialized view (just like querying a component)
foreach (var viewEntity in t.QueryComponentTable<YoungPlayerView>())
{
    t.ReadEntity(viewEntity, out YoungPlayerView view);

    // 'view' reflects database state at tick 1000
    Console.WriteLine($"Young player: {view.Name}, Age: {view.Age}");
}

// View updates happen within transaction commit
using (var t2 = db.CreateTransaction())
{
    // Update source data
    t2.UpdateEntity(123, ref somePlayer);

    // On commit, view component automatically updated
    t2.Commit();  // Creates/updates/deletes YoungPlayerView entities
}
```

### Internal Mechanics

**View as Component Table:**
```csharp
// View data stored in regular ComponentTable
var viewTable = db.GetComponentTable<YoungPlayerView>();

// View entities are real entities with entity IDs
// Can query them like any component
```

**Incremental View Maintenance:**
```csharp
// In Transaction.Commit()
public bool Commit()
{
    // ... MVCC commit for normal components ...

    if (commitSuccessful)
    {
        // Update materialized views
        foreach (var viewDef in db.GetMaterializedViews())
        {
            UpdateMaterializedView(viewDef, this);
        }
    }
}

void UpdateMaterializedView(MaterializedViewDefinition viewDef, Transaction sourceTxn)
{
    foreach (var change in sourceTxn.GetChanges())
    {
        var sourceEntityId = change.EntityId;

        // Check if entity should be in view
        if (viewDef.Predicate.Evaluate(change.NewComponent))
        {
            // Create or update view entity
            var viewComponent = viewDef.Mapper(change.NewComponent);

            // Store as regular component (uses MVCC!)
            CreateOrUpdateViewEntity(sourceEntityId, viewComponent);
        }
        else
        {
            // Delete view entity if it exists
            DeleteViewEntityIfExists(sourceEntityId);
        }
    }
}
```

**View Queries Use MVCC:**
```csharp
// Transaction at tick 1000 sees view as of tick 1000
using var t = db.CreateTransaction();  // tick 1000

// Reads YoungPlayerView components as of tick 1000
// Automatically gets correct MVCC version
foreach (var viewEntityId in t.QueryComponentTable<YoungPlayerView>())
{
    t.ReadEntity(viewEntityId, out YoungPlayerView view);
    // 'view' is from tick 1000
}
```

### Pros

**1. Perfect MVCC Integration**
- Views are just components, get all MVCC benefits
- Snapshot isolation works naturally
- Time-travel queries work (read view at historical tick)

**2. Consistent Multi-View Queries**
```csharp
using var t = db.CreateTransaction();

// All views see identical snapshot at transaction's tick
var youngPlayers = t.QueryComponentTable<YoungPlayerView>();
var richPlayers = t.QueryComponentTable<RichPlayerView>();

// Can join, analyze across views consistently
```

**3. Automatic Persistence**
- Views are stored on disk like regular components
- Survive database restarts
- Can configure durability per view (WriteThrough, WriteAhead, etc.)

**4. Index Support**
```csharp
[Component]
[MaterializedView]
public struct YoungPlayerView
{
    [Field][Index] public int Age;  // Can index view fields!
}

// Fast queries on view data
var teenagersView = db.Query<YoungPlayerView>()
    .Where(v => v.Age >= 13 && v.Age <= 19)
    .ToView();
```

**5. Incremental Maintenance is MVCC-Aware**
- View updates create new MVCC revisions
- Old transactions still see old view state
- No interference between concurrent transactions

### Cons

**1. Storage Overhead**
- Every view entity is a full component with MVCC revisions
- Disk space: ~100 bytes per view entity × 10K entities = 1MB per view
- Memory overhead for component cache

**2. Write Amplification**
```csharp
// Update affects multiple views
using var t = db.CreateTransaction();
t.UpdateEntity(123, ref player);  // Update Player

t.Commit();  // Also updates:
             // - YoungPlayerView
             // - AdultPlayerView
             // - HighScorePlayerView
             // Each creates MVCC revision!
```

One source update → N view updates × MVCC overhead

**3. View Definition Complexity**
```csharp
// Must define view as struct component
// More boilerplate than lambda-based queries

// Can't easily do multi-component views
// Would need flattened struct with all denormalized fields
[Component]
public struct PlayerInventoryView  // Flattened Player + Inventory
{
    [Field] public long PlayerId;
    [Field] public int Age;           // From Player
    [Field] public int Gold;          // From Inventory
    [Field] public bool HasPremium;   // From Inventory
}
```

**4. View Entities Need Entity IDs**
- Each view entry consumes an entity ID
- View queries return view entity IDs, not source entity IDs
- Need mapping back to source: `ViewEntity → SourceEntity`

**5. Can't Use Lambda Syntax**
```csharp
// This beautiful syntax doesn't work:
var view = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// Must pre-define struct and register:
db.RegisterMaterializedView<YoungPlayerView>(...);
```

### Best For

- **Critical persistent views**: Must survive database restart
- **Time-travel analysis**: Need to query historical view state
- **Highly durable views**: Write-through persistence required
- **Views used in transactions**: Analytical queries needing snapshot isolation

---

## Design Option 6: Hybrid "Smart View" with Context Detection

### API Design

```csharp
// Context-aware API: View behaves differently based on how it's created

// Scenario 1: Created outside transaction → Persistent mode
var persistentView = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// Reads latest committed state, updates automatically
foreach (var player in persistentView)
{
    // No transaction context, sees latest
}

// Scenario 2: Created inside transaction → Snapshot mode
using var t = db.CreateTransaction();
var snapshotView = t.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// Reads snapshot at transaction's tick, immutable
foreach (var player in snapshotView)
{
    // Fixed snapshot at t.Tick
}

// Scenario 3: Bind persistent view to transaction for reading
using (var t = db.CreateTransaction())
{
    foreach (var player in persistentView.WithTransaction(t))
    {
        // Reads from cache where possible, MVCC fallback when needed
    }
}
```

### Internal Mechanics

**Single View Class, Multiple Behaviors:**
```csharp
public class View<TC1, TC2>
{
    private readonly DatabaseEngine _db;
    private readonly Transaction _boundTransaction;  // Null for persistent mode
    private readonly long _snapshotTick;

    private Dictionary<long, (TC1, TC2, long tick)> _cache;

    // Factory methods determine mode
    public static View<TC1, TC2> CreatePersistent(DatabaseEngine db, QueryPlan plan)
    {
        var view = new View<TC1, TC2>
        {
            _db = db,
            _boundTransaction = null,  // No transaction binding
            _snapshotTick = long.MaxValue  // Always read latest
        };

        view.ExecuteAndCache(plan);
        db.RegisterViewForUpdates(view);  // Subscribe to incremental updates

        return view;
    }

    public static View<TC1, TC2> CreateSnapshot(Transaction txn, QueryPlan plan)
    {
        var view = new View<TC1, TC2>
        {
            _db = txn.DatabaseEngine,
            _boundTransaction = txn,
            _snapshotTick = txn.Tick  // Pin to transaction tick
        };

        view.ExecuteAndCache(plan);
        // Don't register for updates (snapshot is immutable)

        return view;
    }

    public IEnumerable<(long, TC1, TC2)> GetAll()
    {
        if (_boundTransaction != null)
        {
            // Snapshot mode: read from cache or MVCC at pinned tick
            return GetAllSnapshot();
        }
        else
        {
            // Persistent mode: read latest from cache
            return GetAllLatest();
        }
    }

    public ViewEnumerator WithTransaction(Transaction txn)
    {
        // Bind to transaction for this enumeration only
        return new ViewEnumerator(this, txn);
    }
}
```

**Smart Update Registration:**
```csharp
// In DatabaseEngine
private List<View> _persistentViews;
private Dictionary<Transaction, List<View>> _snapshotViews;

internal void RegisterViewForUpdates(View view)
{
    // Only persistent views get incremental updates
    _persistentViews.Add(view);
}

public Transaction CreateTransaction()
{
    var txn = new Transaction(this, currentTick++);
    _snapshotViews[txn] = new List<View>();  // Track this txn's views

    return txn;
}

// On commit
internal void OnTransactionCommit(Transaction txn)
{
    // Update persistent views
    foreach (var view in _persistentViews)
    {
        view.IncrementalUpdate(txn);
    }

    // Snapshot views don't update
    // Clean up snapshot views for this transaction
    _snapshotViews.Remove(txn);
}
```

### Pros

**1. Intuitive API**
- Creation context determines behavior
- No explicit mode selection needed
- Reads naturally:
  ```csharp
  var view = db.Query(...).ToView();        // "db" → persistent
  var view = transaction.Query(...).ToView(); // "transaction" → snapshot
  ```

**2. Combines Best of Both Worlds**
- Persistent views: fast, auto-updating, good for rendering
- Snapshot views: consistent, good for analysis
- Same API surface, different semantics

**3. Escape Hatch Available**
```csharp
// Persistent view but need snapshot read
var view = db.Query(...).ToView();  // Persistent

using (var t = db.CreateTransaction())
{
    // Temporarily bind for consistent read
    foreach (var item in view.WithTransaction(t))
    {
        // Snapshot semantics
    }
}
// Back to persistent semantics
```

**4. Minimal Overhead**
- Persistent views benefit from incremental updates
- Snapshot views are lightweight (no update subscription)

### Cons

**1. Implicit Behavior Can Surprise**
```csharp
// User might not realize these behave differently!
var view1 = db.Query(...).ToView();        // Auto-updating
var view2 = transaction.Query(...).ToView(); // Immutable snapshot

// view1 updates over time, view2 doesn't
// Not obvious from API alone
```

**2. Lifecycle Confusion**
```csharp
View<Player> view;

using (var t = db.CreateTransaction())
{
    view = t.Query(...).ToView();  // Snapshot view
}
// Transaction disposed

// Can we still use view?
// It's a snapshot, so maybe yes?
// But transaction is disposed...
```

Must decide: Keep view valid (cache component data) or invalidate?

**3. Mixed Mode Complexity**
```csharp
public IEnumerable<(long, TC1, TC2)> GetAll()
{
    if (_boundTransaction != null)
        return GetAllSnapshot();
    else
        return GetAllLatest();
}
```

Single class handling two different behaviors → complexity

### Best For

- **General-purpose applications**: Want both modes without boilerplate
- **Developer-friendly APIs**: Hide complexity behind intuitive interface
- **Gradual learning**: Simple cases work without understanding modes, advanced users can leverage both

---

## Recommended Design: Hybrid Smart View (Option 6) with Enhancements

After analyzing all options, I recommend **Option 6 (Hybrid Smart View)** with the following enhancements:

### Enhanced API Design

```csharp
// 1. Persistent view (created from DatabaseEngine)
var persistentView = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView(cacheComponents: true);  // Explicit: cache component data

// Always sees latest committed state
foreach (var (id, player) in persistentView)
{
    // Fast: reads from cache
}

// 2. Snapshot view (created from Transaction)
using var t = db.CreateTransaction();
var snapshotView = t.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// Sees fixed snapshot at transaction's tick
foreach (var (id, player) in snapshotView)
{
    // Consistent: reads from snapshot
}

// 3. Bind persistent view to transaction
using (var t = db.CreateTransaction())
{
    var view1Bound = persistentView.ReadWithTransaction(t);
    var view2Bound = anotherPersistentView.ReadWithTransaction(t);

    // Both see identical snapshot at t.Tick
    // Can perform multi-view analysis
}

// 4. Explicit lifetime control
var longLivedView = db.Query<Enemy>()
    .Where(e => e.IsHostile)
    .ToView(lifetime: ViewLifetime.Persistent);

// ... later ...
longLivedView.Dispose();  // Stops receiving updates

// 5. On-demand refresh for persistent views
persistentView.Refresh();  // Forces full re-query if needed
```

### Key Features

1. **Context-Aware Creation**
   - `db.Query().ToView()` → Persistent (auto-updating)
   - `transaction.Query().ToView()` → Snapshot (immutable)

2. **Explicit Component Caching**
   - `cacheComponents: true` → Cache component data (fast reads, more memory)
   - `cacheComponents: false` → Cache only entity IDs (slow reads, less memory)

3. **Transaction Binding**
   - `view.ReadWithTransaction(t)` → Read persistent view within transaction snapshot
   - Enables multi-view consistent analysis

4. **Lifetime Management**
   - Persistent views are `IDisposable`
   - Snapshot views dispose with transaction
   - Explicit `Dispose()` stops update subscription

### Why This Design Wins

**1. Natural Mental Model**
```csharp
// Game loop: Persistent views for real-time state
var enemies = db.Query<Enemy>()
    .Where(e => e.IsAlive)
    .ToView();

void OnRenderFrame()
{
    foreach (var enemy in enemies)  // Always current
        RenderEnemy(enemy);
}

// Analytics: Snapshot for consistent analysis
void AnalyzeGameState()
{
    using var t = db.CreateTransaction();

    var players = t.Query<Player>().Where(p => p.IsActive).ToView();
    var inventory = t.Query<Inventory>().Where(i => i.Gold > 0).ToView();

    // Both see identical snapshot
    var analysis = AnalyzeEconomy(players, inventory);
}
```

**2. Performance Where It Matters**
- Persistent views: Incremental updates (~20 ops per change)
- Snapshot views: No update overhead
- Rendering: Fast cached reads
- Analytics: Consistent snapshots

**3. Flexible Consistency Control**
```csharp
// Usually: Fast persistent reads
foreach (var enemy in enemyView) { }

// Sometimes: Need consistency across views
using (var t = db.CreateTransaction())
{
    foreach (var enemy in enemyView.ReadWithTransaction(t)) { }
    foreach (var player in playerView.ReadWithTransaction(t)) { }
    // Identical snapshot
}
```

**4. Explicit Costs**
- `cacheComponents: true` → More memory, faster reads
- `cacheComponents: false` → Less memory, slower reads
- Developer chooses trade-off explicitly

---

## Implementation Considerations

### Transaction Lifetime for Snapshot Views

```csharp
// Option A: Snapshot view remains valid after transaction disposed
using var t = db.CreateTransaction();
var view = t.Query<Player>().Where(p => p.Age < 18).ToView();

// Transaction disposed, but view cached component data
// View remains valid as read-only snapshot

foreach (var player in view)
{
    // Still works! Reading from cached data
}

// Option B: Snapshot view invalidated when transaction disposed
using var t = db.CreateTransaction();
var view = t.Query<Player>().Where(p => p.Age < 18).ToView();

// Transaction disposed
// View becomes invalid

foreach (var player in view)  // THROWS!
{
    // Can't use view outside transaction scope
}
```

**Recommendation: Option A (Cache Component Data)**
- Snapshot views cache component data during query execution
- Remain valid after transaction disposed
- Enables using snapshots beyond transaction scope
- Explicit about "snapshot" semantics: data frozen at creation

### Multi-View Transaction Binding (Simple Approach)

```csharp
// No special context needed - just bind each view to the transaction
using var t = db.CreateTransaction();

// Each view.ReadWithTransaction(t) binds to the same transaction
// Therefore all see the identical snapshot at t.Tick
foreach (var player in playerView.ReadWithTransaction(t))
{
    // Snapshot at t.Tick
}

foreach (var enemy in enemyView.ReadWithTransaction(t))
{
    // Same snapshot at t.Tick
}

foreach (var item in itemView.ReadWithTransaction(t))
{
    // Same snapshot at t.Tick
}

// All three enumerations see identical snapshot - no special context needed!
```

**Why No ViewContext?**

The transaction **itself** is the context. Since all views are bound to the same `Transaction` object, they automatically see the same snapshot (the transaction's tick). No additional wrapper needed.

**Alternative: Helper Extension Method** (optional, for convenience)

```csharp
// Extension method for binding multiple views at once
public static class ViewTransactionExtensions
{
    public static (IEnumerable<T1>, IEnumerable<T2>, IEnumerable<T3>) ReadViews<T1, T2, T3>(
        this Transaction txn,
        View<T1> view1,
        View<T2> view2,
        View<T3> view3)
    {
        return (
            view1.ReadWithTransaction(txn),
            view2.ReadWithTransaction(txn),
            view3.ReadWithTransaction(txn)
        );
    }
}

// Usage (if you want terser syntax):
using var t = db.CreateTransaction();
var (players, enemies, items) = t.ReadViews(playerView, enemyView, itemView);

foreach (var player in players) { /* ... */ }
foreach (var enemy in enemies) { /* ... */ }
// All identical snapshot
```

But this is just syntactic sugar - the simple `.ReadWithTransaction(t)` approach works perfectly fine.

### Update Batching for Performance

```csharp
// Transaction commits batch of changes
using var t = db.CreateTransaction();
for (int i = 0; i < 1000; i++)
{
    t.UpdateEntity(i, ref components[i]);
}
t.Commit();  // Single incremental update pass for all views

// Efficient: Process 1000 changes → update each view once
```

---

## Option 6 Summary: Practical Usage Guide

### What Is It?

**Hybrid Smart View** is a context-aware API design where views behave differently based on how they're created:

- Views created from `DatabaseEngine` → **Persistent Mode** (auto-updating, latest committed state)
- Views created from `Transaction` → **Snapshot Mode** (immutable, fixed point-in-time)
- Persistent views can be **bound to transactions** when snapshot semantics are needed

### What Problems Does It Solve?

**Problem 1: Game Rendering Needs Real-Time Updates**
```csharp
// ❌ Without persistent views: Must re-query every frame
void OnRenderFrame()
{
    using var t = db.CreateTransaction();
    var enemies = t.Query<Enemy>().Where(e => e.IsAlive).ToView();  // Full query, 60x/sec!

    foreach (var enemy in enemies)
        RenderEnemy(enemy);
}
```

```csharp
// ✅ With persistent views: Query once, updates automatically
var enemies = db.Query<Enemy>()
    .Where(e => e.IsAlive)
    .ToView();  // Query once

void OnRenderFrame()
{
    // Just iterate cache, ~3000x faster than re-querying
    foreach (var enemy in enemies)
        RenderEnemy(enemy);
}
```

**Problem 2: Analytics Need Consistent Snapshots**
```csharp
// ❌ Without snapshot views: Two queries see different data
var totalGold = db.Query<Inventory>().Sum(i => i.Gold);  // tick 1000
// Transaction commits here, changing player data
var totalPlayers = db.Query<Player>().Count();           // tick 1001
// Inconsistent: gold from tick 1000, players from tick 1001
```

```csharp
// ✅ With snapshot views: Both queries see identical data
using var t = db.CreateTransaction();  // tick 1000

var inventories = t.Query<Inventory>().ToView();
var players = t.Query<Player>().ToView();

var totalGold = inventories.Sum(i => i.Gold);      // tick 1000
var totalPlayers = players.Count();                // tick 1000
// Consistent snapshot!
```

**Problem 3: Sometimes Need Both (Real-Time + Occasional Consistent Analysis)**
```csharp
// ❌ Without hybrid: Choose one or the other

// Option A: Persistent (fast, but no consistency guarantee)
var enemyView = db.Query<Enemy>().ToView();
var playerView = db.Query<Player>().ToView();
// Can't do consistent multi-view analysis

// Option B: Snapshot (consistent, but must re-query constantly)
using var t = db.CreateTransaction();
var enemyView = t.Query<Enemy>().ToView();  // Re-query every time!
```

```csharp
// ✅ With hybrid: Use persistent normally, bind to transaction when needed

// Create persistent views once
var enemyView = db.Query<Enemy>().Where(e => e.IsAlive).ToView();
var playerView = db.Query<Player>().Where(p => p.IsActive).ToView();

// Normal usage: Fast iteration (latest committed state)
void OnRenderFrame()
{
    foreach (var enemy in enemyView) { /* Fast */ }
    foreach (var player in playerView) { /* Fast */ }
}

// Occasional analysis: Bind to transaction for consistency
void AnalyzeGameBalance()
{
    using var t = db.CreateTransaction();

    // Same views, but now reading consistent snapshot
    foreach (var enemy in enemyView.ReadWithTransaction(t)) { /* tick 1000 */ }
    foreach (var player in playerView.ReadWithTransaction(t)) { /* tick 1000 */ }
    // Both see identical snapshot!
}
```

### How to Use It: Complete API Reference

#### 1. Creating Persistent Views (Auto-Updating)

```csharp
// Basic persistent view
var youngPlayers = db.Query<Player>()
    .Where(p => p.Age < 18)
    .ToView();

// With explicit component caching control
var highScorePlayers = db.Query<Player>()
    .Where(p => p.Score > 10000)
    .ToView(cacheComponents: true);   // Cache component data (fast reads, more memory)

var inactiveEnemies = db.Query<Enemy>()
    .Where(e => !e.IsActive)
    .ToView(cacheComponents: false);  // Cache only entity IDs (less memory, slower reads)

// Multi-component view
var richYoungPlayers = db.Query<Player, Inventory>()
    .Where((p, i) => p.Age < 18 && i.Gold > 10000)
    .ToView();
```

**Reading from Persistent Views:**
```csharp
// Direct enumeration - always sees latest committed state
foreach (var (entityId, player) in youngPlayers)
{
    Console.WriteLine($"Player {entityId}: {player.Name}, Age {player.Age}");
}

// Get single entity
if (youngPlayers.Contains(123))
{
    var (player, inventory) = richYoungPlayers.Get(123);
}

// Get delta since last query (for game loops)
var delta = youngPlayers.GetDelta();
foreach (var entityId in delta.Added)
{
    SpawnPlayerVisual(entityId);
}
foreach (var entityId in delta.Removed)
{
    DespawnPlayerVisual(entityId);
}
youngPlayers.ClearDelta();
```

**Lifecycle Management:**
```csharp
// Persistent views are IDisposable
using var view = db.Query<Player>().Where(p => p.Age < 18).ToView();

// Stops receiving incremental updates when disposed
// Or dispose manually:
view.Dispose();

// Force full refresh (rarely needed, incremental updates are automatic)
view.Refresh();
```

#### 2. Creating Snapshot Views (Immutable)

```csharp
using var t = db.CreateTransaction();  // tick = 1000

// Create snapshot view
var players = t.Query<Player>()
    .Where(p => p.Age >= 18)
    .ToView();

// View is frozen at tick 1000
foreach (var (entityId, player) in players)
{
    // player reflects state at tick 1000
    // Even if other commits happen, this view stays at tick 1000
}

// Multi-component snapshot
var richPlayers = t.Query<Player, Inventory>()
    .Where((p, i) => p.Age >= 18 && i.Gold > 10000)
    .ToView();
```

**Snapshot View Characteristics:**
```csharp
using var t = db.CreateTransaction();
var snapshotView = t.Query<Player>().Where(p => p.Age < 18).ToView();

// View caches component data during creation
// Remains valid AFTER transaction disposed
using (var t2 = db.CreateTransaction())
{
    // t2 commits some changes
    t2.UpdateEntity(123, ref somePlayer);
    t2.Commit();
}

// snapshotView still shows state at t.Tick
// It's a true snapshot, frozen in time
foreach (var player in snapshotView)
{
    // Still seeing data from tick 1000 (t.Tick)
}
```

#### 3. Binding Persistent Views to Transactions

```csharp
// Create persistent views (normal usage)
var playerView = db.Query<Player>().Where(p => p.IsActive).ToView();
var inventoryView = db.Query<Inventory>().Where(i => i.Gold > 0).ToView();

// Usually: Fast iteration with latest state
foreach (var player in playerView) { /* Latest committed */ }

// When needed: Bind to transaction for consistent snapshot
using (var t = db.CreateTransaction())
{
    // Both views now read from snapshot at t.Tick
    foreach (var (id, player) in playerView.ReadWithTransaction(t))
    {
        // Reading snapshot at t.Tick
    }

    foreach (var (id, inventory) in inventoryView.ReadWithTransaction(t))
    {
        // Same snapshot at t.Tick
    }

    // Can perform multi-view analysis with consistency
    var totalWealth = inventoryView.ReadWithTransaction(t)
        .Where(i => playerView.ReadWithTransaction(t).Any(p => p.EntityId == i.EntityId))
        .Sum(i => i.Gold);
}

// After transaction: back to normal persistent reads
foreach (var player in playerView) { /* Latest committed again */ }
```

#### 4. Practical Game Loop Example

```csharp
public class GameSimulation
{
    private DatabaseEngine _db;

    // Persistent views - created once, live for duration of game
    private View<Enemy, Position> _enemyView;
    private View<Player, Health> _playerView;
    private View<Item, Position> _itemView;

    public void Initialize()
    {
        // Create persistent views once
        _enemyView = _db.Query<Enemy, Position>()
            .Where((e, pos) => e.IsAlive && IsInActiveRegion(pos))
            .ToView();

        _playerView = _db.Query<Player, Health>()
            .Where((p, h) => h.CurrentHP > 0)
            .ToView();

        _itemView = _db.Query<Item, Position>()
            .Where((i, pos) => !i.IsPickedUp)
            .ToView();
    }

    public void OnRenderFrame()
    {
        // Fast iteration: just reads from cached data
        // Views updated automatically when transactions commit

        foreach (var (id, enemy, pos) in _enemyView)
        {
            RenderEnemy(id, enemy, pos);
        }

        foreach (var (id, player, health) in _playerView)
        {
            RenderPlayer(id, player, health);
        }

        foreach (var (id, item, pos) in _itemView)
        {
            RenderItem(id, item, pos);
        }
    }

    public void OnGameTick()
    {
        // Update game state within transaction
        using var t = _db.CreateTransaction();

        // Read and update entities
        foreach (var (id, enemy) in _enemyView)
        {
            t.ReadEntity(id, out Enemy e);
            e.AI_State = UpdateAI(e);
            t.UpdateEntity(id, ref e);
        }

        t.Commit();  // Views automatically updated here!

        // Process deltas for UI updates
        var delta = _enemyView.GetDelta();
        foreach (var id in delta.Added) { SpawnEnemyUI(id); }
        foreach (var id in delta.Removed) { DespawnEnemyUI(id); }
        _enemyView.ClearDelta();
    }

    public void GenerateAnalyticsReport()
    {
        // Need consistent snapshot for report
        using var t = _db.CreateTransaction();

        var enemies = _enemyView.ReadWithTransaction(t).ToList();
        var players = _playerView.ReadWithTransaction(t).ToList();

        // All data from same snapshot - consistent analysis
        var report = new GameReport
        {
            TotalEnemies = enemies.Count,
            TotalPlayers = players.Count,
            AverageEnemyHealth = enemies.Average(e => e.Item2.CurrentHP),
            AveragePlayerHealth = players.Average(p => p.Item3.CurrentHP)
        };

        SaveReport(report);
    }

    public void Shutdown()
    {
        // Clean up views
        _enemyView.Dispose();
        _playerView.Dispose();
        _itemView.Dispose();
    }
}
```

#### 5. When to Use Which Mode

| Use Case | Mode | API | Why |
|----------|------|-----|-----|
| **Game rendering (60 FPS)** | Persistent | `db.Query(...).ToView()` | Fast cached reads, auto-updates |
| **UI updates** | Persistent | `db.Query(...).ToView()` | Delta tracking for incremental UI |
| **AI queries** | Persistent | `db.Query(...).ToView()` | Query once, use many times |
| **Analytics report** | Snapshot | `t.Query(...).ToView()` | Consistent multi-view snapshot |
| **Batch processing** | Snapshot | `t.Query(...).ToView()` | Process consistent set of entities |
| **Spatial queries** | Persistent | `db.Query(...).ToView()` | Fast checks (nearby enemies, etc.) |
| **Balance analysis** | Transaction-bound | `view.ReadWithTransaction(t)` | Compare multiple views consistently |
| **Debug snapshots** | Snapshot | `t.Query(...).ToView()` | Capture state for debugging |

### Key Takeaways

1. **Persistent views** (`db.Query().ToView()`) are for high-frequency, real-time queries
   - Auto-update via incremental maintenance (~3000x faster than re-querying)
   - Always show latest committed state
   - Perfect for game loops, rendering, AI

2. **Snapshot views** (`transaction.Query().ToView()`) are for consistent analysis
   - Frozen at transaction's tick
   - No auto-updates (immutable)
   - Perfect for reports, batch processing, debugging

3. **Transaction binding** (`view.ReadWithTransaction(t)`) gives you both
   - Use persistent views for speed
   - Bind to transaction when consistency needed
   - Best of both worlds

4. **Context determines behavior** - no explicit mode parameter needed
   - Creation from `db` → persistent
   - Creation from `transaction` → snapshot
   - Natural, intuitive API

---

## Conclusion

The **Hybrid Smart View (Option 6)** design provides:

✅ **Persistent views** for high-frequency, low-latency queries (game rendering)
✅ **Snapshot views** for consistent analytical queries
✅ **Transaction binding** for multi-view consistent reads
✅ **Incremental updates** for persistent views (~1000-10000x faster than re-query)
✅ **Natural API** where creation context determines behavior
✅ **Explicit control** over caching, lifetime, and consistency

This design balances the tension between transaction-based MVCC and persistent incremental views by supporting both models and allowing developers to choose based on their use case.
