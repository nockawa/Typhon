# User Guide: Component Lifecycle & Transactions

**Last Updated:** January 2026
**Applies to:** Typhon.Engine transaction and component APIs

---

> 💡 **TL;DR — Hands-on CRUD guide.** Jump to [Getting Started](#getting-started) for setup code, then follow Create → Read → Update → Delete through sections 2–5. Come back to [Understanding Transactions](#understanding-transactions) and [Best Practices](#best-practices) once you're comfortable with the basics.

---

## Table of Contents
1. [Getting Started](#getting-started)
2. [Creating Entities and Components](#creating-entities-and-components)
3. [Reading Components](#reading-components)
4. [Modifying Components](#modifying-components)
5. [Deleting Components](#deleting-components)
6. [Understanding Transactions](#understanding-transactions)
7. [Concurrent Modifications](#concurrent-modifications)
8. [Best Practices](#best-practices)

---

## Getting Started

### Setting Up the Database

```csharp
// 1. Create DatabaseEngine (usually via dependency injection)
using var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();

// 2. Register your component types (do this once at startup)
dbe.RegisterComponent<PlayerComponent>();
dbe.RegisterComponent<InventoryComponent>();
dbe.RegisterComponent<HealthComponent>();
```

### Defining a Component

```csharp
[Component]
public struct PlayerComponent
{
    [Field]
    public String64 PlayerName;

    [Field]
    [Index]  // Creates B+Tree index for fast lookups
    public int AccountId;

    [Field]
    public float Experience;
}
```

**Key Rules for Components:**
- Must be a `struct` (value type)
- Must be `unmanaged` (no managed references like strings, arrays, classes)
- Use `String64` for strings (max 64 bytes)
- Use `ComponentCollection<T>` for variable-sized collections
- Marked with `[Component]` attribute
- Fields marked with `[Field]` attribute

---

## Creating Entities and Components

### Basic Creation

Every operation on the database happens **inside a transaction**:

```csharp
// Create a transaction
using var t = dbe.CreateTransaction();

// Create a component
var player = new PlayerComponent
{
    PlayerName = "Alice",
    AccountId = 12345,
    Experience = 0f
};

// Create an entity with this component
// Returns a unique entity ID (long)
long entityId = t.CreateEntity(ref player);

// IMPORTANT: Nothing is saved until you commit!
bool success = t.Commit();

if (success)
{
    Console.WriteLine($"Player created with ID: {entityId}");
}
else
{
    Console.WriteLine("Transaction failed (conflict or error)");
}
```

### What Just Happened?

1. **Transaction Created**: You get a snapshot of the database as of that moment
2. **Entity Created**: You create an entity with ID (e.g., `42`)
3. **Component Attached**: The `PlayerComponent` is attached to entity `42`
4. **Commit**: Changes are written to the database
5. **Transaction Disposed**: Resources are cleaned up (via `using`)

### Creating Multiple Components for Same Entity

An entity can have multiple component types:

```csharp
using var t = dbe.CreateTransaction();

// Create the entity with first component
var player = new PlayerComponent { PlayerName = "Bob", AccountId = 99999 };
long entityId = t.CreateEntity(ref player);

// Add another component to the same entity
var health = new HealthComponent { HP = 100, MaxHP = 100 };
t.CreateComponent(entityId, ref health);

// Add inventory
var inventory = new InventoryComponent { Gold = 50 };
t.CreateComponent(entityId, ref inventory);

t.Commit();
```

**Result**: Entity `42` now has:
- `PlayerComponent`
- `HealthComponent`
- `InventoryComponent`

### Using the Builder Pattern

For convenience, you can chain operations:

```csharp
using var t = dbe.CreateTransaction();

var player = new PlayerComponent { PlayerName = "Charlie", AccountId = 11111 };
var health = new HealthComponent { HP = 100, MaxHP = 100 };

long entityId = t.CreateEntity(ref player);
t.CreateComponent(entityId, ref health)
 .Commit();
```

---

## Reading Components

### Reading a Single Component

```csharp
using var t = dbe.CreateTransaction();

long entityId = 42;

// Try to read the PlayerComponent
if (t.ReadEntity(entityId, out PlayerComponent player))
{
    Console.WriteLine($"Player: {player.PlayerName}");
    Console.WriteLine($"Account: {player.AccountId}");
    Console.WriteLine($"XP: {player.Experience}");
}
else
{
    Console.WriteLine("Entity doesn't exist or doesn't have PlayerComponent");
}

// No need to commit for read-only transactions
// But you still need to dispose the transaction (via using)
```

### Reading Multiple Components

```csharp
using var t = dbe.CreateTransaction();

if (t.ReadEntity(entityId, out PlayerComponent player) &&
    t.ReadEntity(entityId, out HealthComponent health))
{
    Console.WriteLine($"{player.PlayerName} has {health.HP}/{health.MaxHP} HP");
}
```

### Querying by Index

If you marked a field with `[Index]`, you can query by that field:

```csharp
using var t = dbe.CreateTransaction();

// Get the index for AccountId field
var index = t.GetIndex<PlayerComponent, int>(nameof(PlayerComponent.AccountId));

// Find all entities with AccountId == 12345
// Returns IEnumerable<long> (entity IDs)
var entityIds = index.Get(12345);

foreach (var id in entityIds)
{
    if (t.ReadEntity(id, out PlayerComponent player))
    {
        Console.WriteLine($"Found player: {player.PlayerName}");
    }
}
```

### Scanning All Entities of a Type

```csharp
using var t = dbe.CreateTransaction();

// Get all entities that have PlayerComponent
foreach (var entityId in t.GetEntities<PlayerComponent>())
{
    if (t.ReadEntity(entityId, out PlayerComponent player))
    {
        Console.WriteLine($"Player {entityId}: {player.PlayerName}");
    }
}
```

---

## Modifying Components

### Basic Update

```csharp
using var t = dbe.CreateTransaction();

// 1. Read the current component
if (t.ReadEntity(entityId, out PlayerComponent player))
{
    // 2. Modify it (it's a struct, so this is a copy)
    player.Experience += 100f;

    // 3. Write it back
    t.UpdateEntity(entityId, player);

    // 4. Commit the change
    t.Commit();
}
```

### What Happens During Update?

1. **Read**: You get the component as it exists in your transaction's snapshot
2. **Modify**: You change the struct (local copy)
3. **UpdateEntity**: The change is tracked in the transaction (not yet written to database)
4. **Commit**: A new revision is created, old revision is kept for MVCC

### Update with Callback

For convenience, you can use a callback pattern:

```csharp
using var t = dbe.CreateTransaction();

bool updated = t.UpdateEntity<PlayerComponent>(entityId, player =>
{
    player.Experience += 100f;
    return player; // Return the modified component
});

if (updated)
{
    t.Commit();
}
```

### Updating Multiple Components Atomically

```csharp
using var t = dbe.CreateTransaction();

// Read both components
if (t.ReadEntity(entityId, out PlayerComponent player) &&
    t.ReadEntity(entityId, out HealthComponent health))
{
    // Modify both
    player.Experience += 50f;
    health.HP -= 10;

    // Update both
    t.UpdateEntity(entityId, player);
    t.UpdateEntity(entityId, health);

    // Both changes committed atomically
    t.Commit();
}
```

**Important**: Either both changes succeed, or both fail. This is ACID atomicity.

### Conditional Updates

```csharp
using var t = dbe.CreateTransaction();

if (t.ReadEntity(entityId, out HealthComponent health))
{
    // Only heal if not at max HP
    if (health.HP < health.MaxHP)
    {
        health.HP = Math.Min(health.HP + 20, health.MaxHP);
        t.UpdateEntity(entityId, health);
        t.Commit();
    }
    else
    {
        // No changes needed, just dispose transaction
        Console.WriteLine("Already at max HP");
    }
}
```

---

## Deleting Components

### Delete a Specific Component Type

```csharp
using var t = dbe.CreateTransaction();

// Remove PlayerComponent from entity 42
// Entity still exists, just doesn't have PlayerComponent anymore
bool deleted = t.DeleteComponent<PlayerComponent>(entityId);

if (deleted)
{
    t.Commit();
    Console.WriteLine("PlayerComponent removed");
}
```

### Delete Entire Entity

```csharp
using var t = dbe.CreateTransaction();

// Delete all components for this entity
// Entity ID 42 no longer exists
bool deleted = t.DeleteEntity(entityId);

if (deleted)
{
    t.Commit();
    Console.WriteLine("Entity completely removed");
}
```

### Conditional Delete

```csharp
using var t = dbe.CreateTransaction();

if (t.ReadEntity(entityId, out PlayerComponent player))
{
    // Delete inactive players
    if (player.Experience == 0f)
    {
        t.DeleteEntity(entityId);
        t.Commit();
    }
}
```

---

## Understanding Transactions

### Transaction Lifecycle

```csharp
// 1. CREATE
using var t = dbe.CreateTransaction();
// At this moment, transaction gets a timestamp (tick)
// This timestamp defines what data the transaction can see

// 2. OPERATIONS
t.CreateEntity(ref component);  // Tracked in transaction cache
t.ReadEntity(id, out var c);    // Read from snapshot or cache
t.UpdateEntity(id, component);  // Tracked in transaction cache
t.DeleteComponent<T>(id);       // Tracked in transaction cache

// 3. COMMIT OR ROLLBACK
bool success = t.Commit();      // Write changes to database
// OR
t.Rollback();                   // Discard all changes

// 4. DISPOSE (automatic with 'using')
// Transaction is returned to pool for reuse
```

### Read Your Own Writes

Within a transaction, you always see your own changes:

```csharp
using var t = dbe.CreateTransaction();

var player = new PlayerComponent { PlayerName = "Test" };
long id = t.CreateEntity(ref player);

// You can immediately read what you just created
if (t.ReadEntity(id, out PlayerComponent readBack))
{
    Console.WriteLine($"I created: {readBack.PlayerName}");
    // Output: "I created: Test"
}

// But it's not in the database yet!
// If you don't commit, no one else will ever see it
```

### Rollback on Error

```csharp
using var t = dbe.CreateTransaction();

try
{
    // Do some operations
    t.UpdateEntity(id1, component1);
    t.UpdateEntity(id2, component2);

    // Something goes wrong
    if (someCondition)
    {
        throw new Exception("Business logic error");
    }

    t.Commit();
}
catch (Exception ex)
{
    // Transaction will be automatically rolled back on Dispose
    // OR you can explicitly rollback:
    t.Rollback();
    Console.WriteLine("Transaction rolled back due to error");
}
```

### Read-Only Transactions

```csharp
// For read-only operations, you still need a transaction
// But you don't need to commit
using var t = dbe.CreateTransaction();

var allPlayers = t.GetEntities<PlayerComponent>();
foreach (var id in allPlayers)
{
    if (t.ReadEntity(id, out PlayerComponent player))
    {
        Console.WriteLine(player.PlayerName);
    }
}

// No commit needed, transaction auto-disposed
```

---

## Concurrent Modifications

### Snapshot Isolation

Each transaction sees the database **as it was** when the transaction started:

```csharp
// Transaction 1 starts
using var t1 = dbe.CreateTransaction();
t1.ReadEntity(42, out PlayerComponent player1);
Console.WriteLine($"T1 sees XP: {player1.Experience}");  // Output: 100

// Transaction 2 starts and modifies
using (var t2 = dbe.CreateTransaction())
{
    t2.ReadEntity(42, out PlayerComponent player2);
    player2.Experience = 999f;
    t2.UpdateEntity(42, player2);
    t2.Commit();  // T2 commits successfully
}

// T1 reads again - STILL sees old value!
t1.ReadEntity(42, out PlayerComponent playerAgain);
Console.WriteLine($"T1 still sees XP: {playerAgain.Experience}");  // Output: 100

// This is snapshot isolation - T1 sees a consistent snapshot
```

### Write Conflicts (Optimistic Concurrency)

When two transactions modify the same component, the **last commit wins**:

```csharp
// Both transactions start at roughly the same time
using var t1 = dbe.CreateTransaction();
using var t2 = dbe.CreateTransaction();

// Both read the same component
t1.ReadEntity(42, out PlayerComponent p1);
t2.ReadEntity(42, out PlayerComponent p2);

// Both modify it differently
p1.Experience += 100f;  // T1: 0 -> 100
p2.Experience += 50f;   // T2: 0 -> 50

t1.UpdateEntity(42, p1);
t2.UpdateEntity(42, p2);

// T1 commits first - succeeds
bool t1Success = t1.Commit();  // true
Console.WriteLine($"T1 committed: {t1Success}");

// T2 commits second - DEFAULT BEHAVIOR: creates new revision
// The conflict is detected, and T2's changes are applied on top of T1's committed version
bool t2Success = t2.Commit();  // true
Console.WriteLine($"T2 committed: {t2Success}");

// Final result: Last write wins
// But the old revision (T1's value) is kept for historical queries
```

### Understanding Conflict Detection

When T2 commits after T1:

1. **Conflict Detected**: T2's snapshot is older than current data
2. **Default Resolution**: "Last write wins"
   - Creates new revision with T2's data
   - T1's committed data becomes a historical revision
3. **Both commits succeed**: No exceptions thrown
4. **MVCC History Preserved**: Both versions exist for historical queries

### Custom Conflict Handling

You can provide a custom conflict handler:

```csharp
// When registering the component
dbe.RegisterComponent<PlayerComponent>(options =>
{
    options.ConflictHandler = (current, incoming) =>
    {
        // 'current' = what T1 committed
        // 'incoming' = what T2 wants to write

        // Custom logic: merge the changes
        var merged = new PlayerComponent
        {
            PlayerName = incoming.PlayerName,  // Use T2's name
            AccountId = current.AccountId,      // Keep T1's account ID
            Experience = current.Experience + incoming.Experience  // Add both XP gains!
        };

        return merged;
    };
});
```

### Explicit Conflict Detection

If you want to detect conflicts and handle them yourself:

```csharp
using var t = dbe.CreateTransaction();

// Read initial version
if (t.ReadEntity(42, out PlayerComponent initial))
{
    long initialRevision = t.GetRevisionNumber(42, typeof(PlayerComponent));

    // Modify
    initial.Experience += 100f;
    t.UpdateEntity(42, initial);

    // Before committing, check if another transaction modified it
    long currentRevision = t.GetCurrentRevisionNumber(42, typeof(PlayerComponent));

    if (currentRevision != initialRevision)
    {
        Console.WriteLine("Conflict detected! Someone else modified this component.");

        // Option 1: Rollback and retry
        t.Rollback();

        // Option 2: Re-read and merge
        t.ReadEntity(42, out PlayerComponent latest);
        // ... merge logic ...

        // Option 3: Let default conflict resolution handle it
        t.Commit();
    }
    else
    {
        // No conflict, safe to commit
        t.Commit();
    }
}
```

### Long-Running Transactions

**Avoid long-running transactions!** They prevent garbage collection of old revisions:

```csharp
// BAD: Don't do this
using var t = dbe.CreateTransaction();
t.ReadEntity(42, out var component);

// ... do lots of work, make network calls, wait for user input ...
Thread.Sleep(10000);  // 10 seconds!

t.UpdateEntity(42, component);
t.Commit();  // May conflict with many other transactions

// GOOD: Keep transactions short
void ProcessEntity(long entityId)
{
    using var t = dbe.CreateTransaction();
    t.ReadEntity(entityId, out var component);

    // Compute changes
    component.Experience += CalculateXP();

    t.UpdateEntity(entityId, component);
    t.Commit();
}
```

### Retry Pattern for Conflicts

If you need strong consistency guarantees:

```csharp
bool TryUpdateWithRetry(long entityId, Func<PlayerComponent, PlayerComponent> updateFunc, int maxRetries = 3)
{
    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        using var t = dbe.CreateTransaction();

        if (t.ReadEntity(entityId, out PlayerComponent component))
        {
            var updated = updateFunc(component);
            t.UpdateEntity(entityId, updated);

            if (t.Commit())
            {
                return true;  // Success!
            }

            // Commit failed, retry with fresh snapshot
            Thread.Sleep(10);  // Small delay
        }
    }

    return false;  // Failed after retries
}

// Usage
bool success = TryUpdateWithRetry(42, player =>
{
    player.Experience += 100f;
    return player;
});
```

---

## Best Practices

### 1. Always Use 'using' with Transactions

```csharp
// GOOD
using var t = dbe.CreateTransaction();
// ... operations ...
t.Commit();

// BAD - may leak resources
var t = dbe.CreateTransaction();
// ... operations ...
t.Commit();
// Forgot to dispose!
```

### 2. Keep Transactions Short

```csharp
// GOOD - focused transaction
void AddExperience(long playerId, float xp)
{
    using var t = dbe.CreateTransaction();
    if (t.ReadEntity(playerId, out PlayerComponent p))
    {
        p.Experience += xp;
        t.UpdateEntity(playerId, p);
        t.Commit();
    }
}

// BAD - transaction open too long
void ProcessUserRequest(long playerId)
{
    using var t = dbe.CreateTransaction();  // Transaction starts

    // Read data
    t.ReadEntity(playerId, out var player);

    // Compute something expensive (BAD!)
    var result = ExpensiveCalculation();  // 500ms

    // Wait for network (VERY BAD!)
    var response = await HttpClient.GetAsync("...");  // 1000ms

    // Finally update
    player.Experience += result;
    t.UpdateEntity(playerId, player);
    t.Commit();  // Transaction held for 1.5 seconds!
}

// BETTER - transaction only for database operations
async Task ProcessUserRequest(long playerId)
{
    // Read data
    PlayerComponent player;
    using (var t = dbe.CreateTransaction())
    {
        t.ReadEntity(playerId, out player);
    }

    // Do expensive work outside transaction
    var result = await ExpensiveCalculation();
    var response = await HttpClient.GetAsync("...");

    // Write result
    using (var t = dbe.CreateTransaction())
    {
        // Re-read to get latest version
        if (t.ReadEntity(playerId, out var latest))
        {
            latest.Experience += result;
            t.UpdateEntity(playerId, latest);
            t.Commit();
        }
    }
}
```

### 3. Check Return Values

```csharp
// GOOD - check if read succeeded
using var t = dbe.CreateTransaction();
if (t.ReadEntity(entityId, out PlayerComponent player))
{
    // Entity exists and has PlayerComponent
    Console.WriteLine(player.PlayerName);
}
else
{
    // Entity doesn't exist or doesn't have this component
    Console.WriteLine("Player not found");
}

// BAD - assume it exists
using var t = dbe.CreateTransaction();
t.ReadEntity(entityId, out PlayerComponent player);
Console.WriteLine(player.PlayerName);  // May be default/garbage if read failed!
```

### 4. Explicit Commit

```csharp
// GOOD - explicit commit
using var t = dbe.CreateTransaction();
t.UpdateEntity(id, component);
bool success = t.Commit();
if (!success)
{
    Logger.Error("Transaction failed");
}

// OKAY - implicit rollback on dispose
using var t = dbe.CreateTransaction();
t.UpdateEntity(id, component);
t.Commit();
// If commit fails, changes are lost (rolled back on dispose)

// BAD - forgot to commit
using var t = dbe.CreateTransaction();
t.UpdateEntity(id, component);
// Transaction disposed without commit = changes lost!
```

### 5. Understand Snapshot Isolation

```csharp
// If you need to see latest data, create a NEW transaction
long GetLatestExperience(long playerId)
{
    // Fresh transaction = latest snapshot
    using var t = dbe.CreateTransaction();
    if (t.ReadEntity(playerId, out PlayerComponent p))
    {
        return p.Experience;
    }
    return 0;
}

// Don't reuse old transactions expecting fresh data
using var t1 = dbe.CreateTransaction();
t1.ReadEntity(42, out var p1);  // XP = 100

// Another transaction commits changes
// ...

// t1 STILL sees old snapshot!
t1.ReadEntity(42, out var p2);  // XP = 100 (not updated!)
```

### 6. Batch Related Operations

```csharp
// GOOD - batch related operations in one transaction
void LevelUp(long playerId)
{
    using var t = dbe.CreateTransaction();

    if (t.ReadEntity(playerId, out PlayerComponent player) &&
        t.ReadEntity(playerId, out HealthComponent health))
    {
        // All changes atomic
        player.Experience = 0;
        player.Level++;
        health.MaxHP += 10;
        health.HP = health.MaxHP;

        t.UpdateEntity(playerId, player);
        t.UpdateEntity(playerId, health);
        t.Commit();
    }
}

// BAD - multiple transactions for related changes
void LevelUp(long playerId)
{
    // Race condition: another transaction could run between these!
    using (var t1 = dbe.CreateTransaction())
    {
        t1.ReadEntity(playerId, out PlayerComponent p);
        p.Level++;
        t1.UpdateEntity(playerId, p);
        t1.Commit();
    }

    using (var t2 = dbe.CreateTransaction())
    {
        t2.ReadEntity(playerId, out HealthComponent h);
        h.MaxHP += 10;
        t2.UpdateEntity(playerId, h);
        t2.Commit();
    }
}
```

### 7. Understand Component Mutability

```csharp
// Components are STRUCTS (value types), not classes

using var t = dbe.CreateTransaction();
if (t.ReadEntity(42, out PlayerComponent player))
{
    // This modifies a LOCAL COPY
    player.Experience += 100;

    // Changes are NOT in the database until you call UpdateEntity!
    t.UpdateEntity(42, player);  // REQUIRED
    t.Commit();
}

// Common mistake:
if (t.ReadEntity(42, out PlayerComponent player))
{
    player.Experience += 100;  // Modified local copy
    t.Commit();  // BUG: Forgot UpdateEntity, changes lost!
}
```

---

## Summary

### Component Operations Quick Reference

| Operation | API | Requires Commit |
|-----------|-----|-----------------|
| Create entity + component | `t.CreateEntity(ref component)` | Yes |
| Add component to entity | `t.CreateComponent(entityId, ref component)` | Yes |
| Read component | `t.ReadEntity(entityId, out component)` | No |
| Update component | `t.UpdateEntity(entityId, component)` | Yes |
| Delete component | `t.DeleteComponent<T>(entityId)` | Yes |
| Delete entity | `t.DeleteEntity(entityId)` | Yes |
| Query by index | `t.GetIndex<T, TKey>(...).Get(key)` | No |
| Get all entities | `t.GetEntities<T>()` | No |

### Transaction Guarantees

- **Atomicity**: All operations in a transaction succeed or all fail
- **Consistency**: Database remains in valid state
- **Isolation**: Snapshot isolation - you see a consistent view from transaction start
- **Durability**: Committed changes are persisted (configurable per component)

### Concurrency Model

- **Optimistic Concurrency**: No locks during transaction execution
- **Snapshot Isolation**: Each transaction sees database as of its start time
- **Conflict Resolution**: Detected at commit time, default is "last write wins"
- **No Read Locks**: Readers never block writers, writers never block readers
- **MVCC History**: Old revisions kept for garbage collection by MinTick

### When Operations Become Visible

```
T1: CreateTransaction() -> Read() -> Update() -> Commit() ✓
                                                    ↑
                                                    Changes visible to NEW transactions

T2:                        CreateTransaction() -> Read() sees OLD data
T3:                                             CreateTransaction() -> Read() sees NEW data
```

---

This guide covers the essentials of working with Typhon's component system. For advanced topics like custom conflict handlers, historical queries, and performance optimization, see the full documentation.
