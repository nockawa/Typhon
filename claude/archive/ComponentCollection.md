# ComponentCollection Design & User API

## Overview

ComponentCollection enables components to store variable-sized collections of unmanaged data while maintaining full MVCC compliance. This document presents the **user-facing API design** for creating, reading, updating, and deleting collections within transactions, following patterns as close as possible to the existing component CRUD API.

## Design Goals

1. **Consistency with Component API**: Collection operations should mirror component operations (`CreateEntity`, `ReadEntity`, `UpdateEntity`, `DeleteEntity`)
2. **MVCC Compliance**: Full snapshot isolation with zero-copy for unchanged collections
3. **Type Safety**: Compile-time type checking via generics
4. **Performance**: Microsecond-level operations, minimal allocations
5. **Safety**: Clear ownership, no dangling references, dispose patterns for resources
6. **Ergonomics**: Intuitive, minimal boilerplate, hard to misuse

## Component Type Definitions

### ComponentCollection\<T>

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct ComponentCollection<T> where T : unmanaged
{
    internal int _bufferId;  // 4 bytes - references buffer in VariableSizedBufferSegment

    public static readonly ComponentCollection<T> Empty = default;
    public bool IsEmpty => _bufferId == 0;
}
```

**Key Points:**
- 4 bytes total (minimal overhead in component structs)
- Fully unmanaged and blittable
- Default value represents empty collection
- Buffer ID references storage in VariableSizedBufferSegment

### EntityCollection (Specialized)

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct EntityCollection
{
    private ComponentCollection<long> _inner;

    public static readonly EntityCollection Empty = default;
    public bool IsEmpty => _inner.IsEmpty;
}
```

**Usage:** For storing relationships between entities (guild members, inventory, etc.)

---

## Design Propositions

### **Proposition 1: Builder Pattern (Explicit Construction)**

This approach uses explicit builders for creation and modification, with separate read-only accessors.

#### API Surface

```csharp
// Transaction extensions
public ComponentCollectionBuilder<T> CreateCollectionBuilder<T>() where T : unmanaged;
public ReadOnlySpan<T> ReadCollection<T>(ComponentCollection<T> collection) where T : unmanaged;
```

#### Usage Pattern

##### Creating Collections

```csharp
using var t = dbe.CreateQuickTransaction();

// Build a collection
var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);
builder.Add(200);
builder.Add(300);
var items = builder.Build();

// Use in component
var component = new InventoryComponent { ItemIds = items };
long entityId = t.CreateEntity(ref component);

t.Commit();
```

##### Reading Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Read as span
    var items = t.ReadCollection(inv.ItemIds);

    foreach (var itemId in items)
    {
        Console.WriteLine($"Item: {itemId}");
    }
}
```

##### Updating Collections (Replace)

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Build new collection
    var builder = t.CreateCollectionBuilder<int>();

    // Copy existing
    var existing = t.ReadCollection(inv.ItemIds);
    builder.AddRange(existing);

    // Add new items
    builder.Add(400);

    // Replace collection
    inv.ItemIds = builder.Build();
    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

##### Deleting Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    inv.ItemIds = ComponentCollection<int>.Empty;
    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

#### Pros
✅ **Explicit and clear**: Builder pattern is well-known
✅ **Safe**: Immutable collections after Build(), no accidental mutations
✅ **Flexible**: Can optimize Build() to reuse buffers when possible
✅ **Consistent**: Similar to StringBuilder, CollectionBuilder is familiar
✅ **Zero allocations for reads**: ReadCollection returns Span directly

#### Cons
❌ **Verbose**: Requires multiple steps for updates (read → copy → modify → build)
❌ **Manual copying**: User must explicitly copy old data
❌ **Not intuitive**: Doesn't mirror UpdateEntity pattern closely
❌ **Allocation overhead**: Builder may allocate temporary storage

---

### **Proposition 2: Accessor Pattern (Unified Read/Write)**

This approach uses a single accessor type that supports both reading and writing, with a dispose pattern to commit changes.

#### API Surface

```csharp
// Transaction extensions
public ComponentCollectionAccessor<T> CreateComponentCollectionAccessor<T>(
    ref ComponentCollection<T> collection) where T : unmanaged;
```

```csharp
// Accessor type
public ref struct ComponentCollectionAccessor<T> where T : unmanaged
{
    public ReadOnlySpan<T> ReadOnlyElements { get; }
    public Span<T> Elements { get; }  // For in-place modification

    public void Add(T item);
    public void Remove(T item);
    public void Clear();

    public void Dispose();  // Commits changes back to collection
}
```

#### Usage Pattern

##### Creating Collections

```csharp
using var t = dbe.CreateQuickTransaction();

var component = new InventoryComponent();

// Create and populate collection
using (var accessor = t.CreateComponentCollectionAccessor(ref component.ItemIds))
{
    accessor.Add(100);
    accessor.Add(200);
    accessor.Add(300);
}  // Dispose commits to component.ItemIds

long entityId = t.CreateEntity(ref component);
t.Commit();
```

##### Reading Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    using var accessor = t.CreateComponentCollectionAccessor(ref inv.ItemIds);

    var items = accessor.ReadOnlyElements;
    foreach (var itemId in items)
    {
        Console.WriteLine($"Item: {itemId}");
    }
}
```

##### Updating Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    using (var accessor = t.CreateComponentCollectionAccessor(ref inv.ItemIds))
    {
        accessor.Add(400);  // Adds to existing collection
    }

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

##### Deleting Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    using (var accessor = t.CreateComponentCollectionAccessor(ref inv.ItemIds))
    {
        accessor.Clear();
    }

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

#### Pros
✅ **Concise**: Single type for both read and write
✅ **Familiar**: Using pattern similar to file streams
✅ **Intuitive**: Add() directly modifies collection
✅ **Automatic commit**: Dispose handles buffer management
✅ **No manual copying**: Framework handles copy-on-write

#### Cons
❌ **Mutable accessor**: Can be confusing in read-only contexts
❌ **ref parameter required**: Unusual pattern in C#
❌ **Hidden allocations**: Not clear when new buffer is allocated
❌ **MVCC complexity**: Harder to reason about when changes are visible
❌ **Lifetime issues**: Component is copied (struct), accessor has ref to copied field

---

### **Proposition 3: Hybrid Pattern (Recommended)**

Combines the best of both: builders for construction/major updates, accessors for reading, helper methods for common operations.

#### API Surface

```csharp
// Transaction extensions for collection operations
public ComponentCollectionBuilder<T> CreateCollectionBuilder<T>() where T : unmanaged;
public ReadOnlySpan<T> ReadCollection<T>(ComponentCollection<T> collection) where T : unmanaged;
public ComponentCollection<T> AppendToCollection<T>(ComponentCollection<T> collection, T item) where T : unmanaged;
public ComponentCollection<T> RemoveFromCollection<T>(ComponentCollection<T> collection, T item) where T : unmanaged;
public ComponentCollection<T> ModifyCollection<T>(ComponentCollection<T> collection,
    Func<ReadOnlySpan<T>, ComponentCollectionBuilder<T>, ComponentCollectionBuilder<T>> modifier) where T : unmanaged;
```

```csharp
// Builder type
public ref struct ComponentCollectionBuilder<T> where T : unmanaged
{
    public void Add(T item);
    public void AddRange(ReadOnlySpan<T> items);
    public void Clear();
    public ComponentCollection<T> Build();
}
```

#### Usage Pattern

##### Creating Collections

```csharp
using var t = dbe.CreateQuickTransaction();

// Option 1: Builder for multiple items
var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);
builder.Add(200);
builder.Add(300);
var items = builder.Build();

var component = new InventoryComponent { ItemIds = items };
long entityId = t.CreateEntity(ref component);

// Option 2: Builder with AddRange for bulk initialization
var builder2 = t.CreateCollectionBuilder<int>();
builder2.AddRange(new[] { 100, 200, 300 });
var items2 = builder2.Build();

t.Commit();
```

##### Reading Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Direct span access
    var items = t.ReadCollection(inv.ItemIds);

    foreach (var itemId in items)
    {
        Console.WriteLine($"Item: {itemId}");
    }

    // Or LINQ-style operations (no allocations with Span LINQ)
    var count = items.Length;
    var hasItem = items.Contains(100);
}
```

##### Updating Collections - Simple Operations

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Simple append (common case)
    inv.ItemIds = t.AppendToCollection(inv.ItemIds, 400);

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

##### Updating Collections - Complex Modifications

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Complex modification with builder
    inv.ItemIds = t.ModifyCollection(inv.ItemIds, (current, builder) =>
    {
        // Copy items that pass filter
        foreach (var item in current)
        {
            if (item != 200)  // Remove item 200
                builder.Add(item);
        }

        // Add new items
        builder.Add(500);
        builder.Add(600);

        return builder;
    });

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

##### Updating Collections - Replace Entire Collection

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    var builder = t.CreateCollectionBuilder<int>();
    builder.AddRange(new[] { 1000, 2000, 3000 });

    inv.ItemIds = builder.Build();
    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

##### Deleting Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Option 1: Assign empty
    inv.ItemIds = ComponentCollection<int>.Empty;

    // Option 2: Build empty collection
    inv.ItemIds = t.CreateCollectionBuilder<int>().Build();

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

#### Pros
✅ **Best of both worlds**: Explicit builders + convenient helpers
✅ **Performance**: Helper methods can optimize common cases (append, remove)
✅ **Familiar patterns**: Similar to component CRUD with ref parameters
✅ **Clear semantics**: Build() returns new collection, assignments are explicit
✅ **Flexible**: Choose verbosity vs convenience based on use case
✅ **Safe**: Immutable collections after Build(), explicit mutations
✅ **Optimizable**: AppendToCollection can detect small changes and optimize

#### Cons
❌ **Larger API surface**: More methods to learn
❌ **Multiple ways**: Can be confusing which method to use
❌ **Helper limitations**: Complex modifications still need builder pattern

---

### **Proposition 4: Inline Pattern (Maximum Simplicity)**

Minimalist approach with the fewest API methods, prioritizing simplicity over flexibility.

#### API Surface

```csharp
// Transaction extensions
public ComponentCollection<T> CreateCollection<T>(ReadOnlySpan<T> items) where T : unmanaged;
public ReadOnlySpan<T> ReadCollection<T>(ComponentCollection<T> collection) where T : unmanaged;
public ComponentCollection<T> UpdateCollection<T>(ComponentCollection<T> collection,
    Action<List<T>> modifier) where T : unmanaged;
```

#### Usage Pattern

##### Creating Collections

```csharp
using var t = dbe.CreateQuickTransaction();

// Create from array/span
var items = t.CreateCollection<int>(new[] { 100, 200, 300 });

var component = new InventoryComponent { ItemIds = items };
long entityId = t.CreateEntity(ref component);

t.Commit();
```

##### Reading Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    var items = t.ReadCollection(inv.ItemIds);

    foreach (var itemId in items)
    {
        Console.WriteLine($"Item: {itemId}");
    }
}
```

##### Updating Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Modify via callback with mutable list
    inv.ItemIds = t.UpdateCollection(inv.ItemIds, items =>
    {
        items.Add(400);
        items.Remove(200);
    });

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

##### Deleting Collections

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    inv.ItemIds = ComponentCollection<int>.Empty;

    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

#### Pros
✅ **Minimal API**: Only 3 methods to learn
✅ **Simple**: Very easy to understand and use
✅ **Callback pattern**: Similar to UpdateEntity callback overloads
✅ **Hides complexity**: Framework manages all buffer allocation

#### Cons
❌ **Performance cost**: UpdateCollection must allocate List\<T>
❌ **Less control**: Can't optimize for small changes easily
❌ **Hidden allocations**: Not clear when copying occurs
❌ **List dependency**: Uses managed List\<T>, allocation on each update

---

## Comparison Matrix

| Feature | Builder | Accessor | Hybrid | Inline |
|---------|---------|----------|--------|--------|
| **API Methods** | 2 | 1 | 5 | 3 |
| **Learning Curve** | Medium | Low | High | Very Low |
| **Read Performance** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Update Performance** | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **Safety** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Ergonomics** | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Explicitness** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **MVCC Clarity** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Similarity to Component CRUD** | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |

---

## Recommendation: **Proposition 3 (Hybrid Pattern)**

### Rationale

The Hybrid Pattern provides the best balance for Typhon's requirements:

1. **Performance-Critical Path Optimized**:
   - `ReadCollection` returns Span with zero allocations
   - `AppendToCollection` can optimize for single-item additions without full buffer copy
   - Builder pattern allows pre-sizing and efficient bulk operations

2. **Consistency with Component API**:
   - All operations return new `ComponentCollection<T>` values (immutable semantics)
   - Assignments are explicit: `component.Collection = newValue`
   - Mirrors `UpdateEntity(id, ref component)` pattern

3. **Safety**:
   - Immutable collections prevent accidental mutations
   - Clear ownership: builder creates, transaction manages lifetime
   - Type-safe via generics

4. **Progressive Disclosure**:
   - Beginners can use simple helpers (`AppendToCollection`, `RemoveFromCollection`)
   - Advanced users can use builder pattern for complex scenarios
   - All patterns are consistent and composable

5. **MVCC Integration**:
   - Clear when new buffers are allocated (Build() calls)
   - Easy to detect unchanged collections (same ChunkId)
   - Reference counting straightforward (increment on Build, share on copy)

### Implementation Priority

**Phase 1: Core Operations**
- `CreateCollectionBuilder<T>()`
- `ReadCollection<T>()`
- `ComponentCollectionBuilder<T>` implementation
- Basic buffer management and MVCC integration

**Phase 2: Helper Methods**
- `AppendToCollection<T>()`
- `RemoveFromCollection<T>()`
- Optimizations for common cases

**Phase 3: Advanced Operations**
- `ModifyCollection<T>()` callback pattern
- `EntityCollection` specialized type
- Bulk operations and LINQ-style helpers

---

## EntityCollection API (Specialized for Entity References)

### Type Definition

```csharp
public struct EntityCollection
{
    private ComponentCollection<long> _inner;

    public static readonly EntityCollection Empty = default;
    public bool IsEmpty => _inner.IsEmpty;
}
```

### Transaction Extensions

```csharp
public ReadOnlySpan<long> ReadEntityCollection(EntityCollection collection);
public EntityCollection CreateEntityCollection(ReadOnlySpan<long> entityIds);
public EntityCollection AppendToEntityCollection(EntityCollection collection, long entityId);
public EntityCollection RemoveFromEntityCollection(EntityCollection collection, long entityId);

// Navigation helpers
public IEnumerable<T> ReadEntitiesFromCollection<T>(EntityCollection collection) where T : unmanaged;
```

### Usage Example

```csharp
[Component]
public struct GuildComponent
{
    [Field]
    public String64 GuildName;

    [Field]
    public EntityCollection Members;
}

// Create guild with members
using var t = dbe.CreateQuickTransaction();

var player1 = new PlayerComponent { Name = "Alice" };
var player2 = new PlayerComponent { Name = "Bob" };

var p1Id = t.CreateEntity(ref player1);
var p2Id = t.CreateEntity(ref player2);

var members = t.CreateEntityCollection(new[] { p1Id, p2Id });
var guild = new GuildComponent
{
    GuildName = "Dragon Slayers",
    Members = members
};

var guildId = t.CreateEntity(ref guild);
t.Commit();

// Read guild members
using var t2 = dbe.CreateQuickTransaction();

if (t2.ReadEntity(guildId, out GuildComponent g))
{
    // Option 1: Get entity IDs
    var memberIds = t2.ReadEntityCollection(g.Members);
    foreach (var id in memberIds)
    {
        Console.WriteLine($"Member: {id}");
    }

    // Option 2: Navigate to components directly
    foreach (var player in t2.ReadEntitiesFromCollection<PlayerComponent>(g.Members))
    {
        Console.WriteLine($"Player: {player.Name}");
    }
}

// Add member to guild
using var t3 = dbe.CreateQuickTransaction();

var newPlayer = new PlayerComponent { Name = "Charlie" };
var newPlayerId = t3.CreateEntity(ref newPlayer);

if (t3.ReadEntity(guildId, out GuildComponent g))
{
    g.Members = t3.AppendToEntityCollection(g.Members, newPlayerId);
    t3.UpdateEntity(guildId, ref g);
    t3.Commit();
}
```

---

## Complete Usage Examples

### Example 1: Inventory System

```csharp
[Component]
public struct InventoryComponent
{
    [Field]
    public ComponentCollection<int> ItemIds;

    [Field]
    public ComponentCollection<int> Quantities;
}

// Create inventory
using var t = dbe.CreateQuickTransaction();

var itemBuilder = t.CreateCollectionBuilder<int>();
itemBuilder.AddRange(new[] { 1001, 1002, 1003 });  // Sword, Shield, Potion

var qtyBuilder = t.CreateCollectionBuilder<int>();
qtyBuilder.AddRange(new[] { 1, 1, 5 });

var inventory = new InventoryComponent
{
    ItemIds = itemBuilder.Build(),
    Quantities = qtyBuilder.Build()
};

var playerId = t.CreateEntity(ref inventory);
t.Commit();

// Read inventory
using var t2 = dbe.CreateQuickTransaction();

if (t2.ReadEntity(playerId, out InventoryComponent inv))
{
    var items = t2.ReadCollection(inv.ItemIds);
    var qtys = t2.ReadCollection(inv.Quantities);

    for (int i = 0; i < items.Length; i++)
    {
        Console.WriteLine($"Item {items[i]}: {qtys[i]}x");
    }
}

// Add item to inventory (simple)
using var t3 = dbe.CreateQuickTransaction();

if (t3.ReadEntity(playerId, out InventoryComponent inv))
{
    inv.ItemIds = t3.AppendToCollection(inv.ItemIds, 1004);
    inv.Quantities = t3.AppendToCollection(inv.Quantities, 3);

    t3.UpdateEntity(playerId, ref inv);
    t3.Commit();
}

// Update item quantity (complex)
using var t4 = dbe.CreateQuickTransaction();

if (t4.ReadEntity(playerId, out InventoryComponent inv))
{
    var items = t4.ReadCollection(inv.ItemIds);

    // Find item index
    int itemIndex = -1;
    for (int i = 0; i < items.Length; i++)
    {
        if (items[i] == 1003)  // Potion
        {
            itemIndex = i;
            break;
        }
    }

    if (itemIndex >= 0)
    {
        // Rebuild quantities with updated value
        inv.Quantities = t4.ModifyCollection(inv.Quantities, (current, builder) =>
        {
            builder.AddRange(current);
            // Use temp storage from builder to modify
            var tempList = builder.GetInternalList();
            tempList[itemIndex] = current[itemIndex] + 5;  // Add 5 potions
            return builder;
        });

        t4.UpdateEntity(playerId, ref inv);
        t4.Commit();
    }
}
```

### Example 2: Skill System with Leveling

```csharp
[Component]
public struct SkillsComponent
{
    [Field]
    public ComponentCollection<ushort> SkillIds;

    [Field]
    public ComponentCollection<byte> SkillLevels;
}

public struct SkillData
{
    public ushort Id;
    public byte Level;
}

// Helper method to work with paired collections
SkillData[] ReadSkills(Transaction t, SkillsComponent skills)
{
    var ids = t.ReadCollection(skills.SkillIds);
    var levels = t.ReadCollection(skills.SkillLevels);

    var result = new SkillData[ids.Length];
    for (int i = 0; i < ids.Length; i++)
    {
        result[i] = new SkillData { Id = ids[i], Level = levels[i] };
    }
    return result;
}

// Create character with skills
using var t = dbe.CreateQuickTransaction();

var skillBuilder = t.CreateCollectionBuilder<ushort>();
skillBuilder.AddRange(new ushort[] { 101, 102, 201 });  // Fireball, Ice Bolt, Heal

var levelBuilder = t.CreateCollectionBuilder<byte>();
levelBuilder.AddRange(new byte[] { 5, 3, 7 });

var skills = new SkillsComponent
{
    SkillIds = skillBuilder.Build(),
    SkillLevels = levelBuilder.Build()
};

var charId = t.CreateEntity(ref skills);
t.Commit();

// Level up a skill
using var t2 = dbe.CreateQuickTransaction();

if (t2.ReadEntity(charId, out SkillsComponent current))
{
    var skillData = ReadSkills(t2, current);

    // Find and level up Fireball
    for (int i = 0; i < skillData.Length; i++)
    {
        if (skillData[i].Id == 101)
        {
            skillData[i].Level++;
            break;
        }
    }

    // Rebuild collections
    var idBuilder = t2.CreateCollectionBuilder<ushort>();
    var lvlBuilder = t2.CreateCollectionBuilder<byte>();

    foreach (var skill in skillData)
    {
        idBuilder.Add(skill.Id);
        lvlBuilder.Add(skill.Level);
    }

    current.SkillIds = idBuilder.Build();
    current.SkillLevels = lvlBuilder.Build();

    t2.UpdateEntity(charId, ref current);
    t2.Commit();
}
```

### Example 3: Guild Membership (Entity References)

```csharp
[Component]
public struct GuildComponent
{
    [Field]
    public String64 GuildName;

    [Field]
    public EntityCollection Members;
}

[Component]
public struct PlayerComponent
{
    [Field]
    public String64 Name;

    [Field]
    public int Level;
}

// Create guild with members
using var t = dbe.CreateQuickTransaction();

var alice = new PlayerComponent { Name = "Alice", Level = 50 };
var bob = new PlayerComponent { Name = "Bob", Level = 45 };

var aliceId = t.CreateEntity(ref alice);
var bobId = t.CreateEntity(ref bob);

var members = t.CreateEntityCollection(new[] { aliceId, bobId });
var guild = new GuildComponent
{
    GuildName = "Dragon Slayers",
    Members = members
};

var guildId = t.CreateEntity(ref guild);
t.Commit();

// List guild members
using var t2 = dbe.CreateQuickTransaction();

if (t2.ReadEntity(guildId, out GuildComponent g))
{
    Console.WriteLine($"Guild: {g.GuildName}");

    foreach (var player in t2.ReadEntitiesFromCollection<PlayerComponent>(g.Members))
    {
        Console.WriteLine($"  {player.Name} (Lv {player.Level})");
    }
}

// Add new member
using var t3 = dbe.CreateQuickTransaction();

var charlie = new PlayerComponent { Name = "Charlie", Level = 48 };
var charlieId = t3.CreateEntity(ref charlie);

if (t3.ReadEntity(guildId, out GuildComponent g))
{
    g.Members = t3.AppendToEntityCollection(g.Members, charlieId);
    t3.UpdateEntity(guildId, ref g);
    t3.Commit();
}

// Remove member
using var t4 = dbe.CreateQuickTransaction();

if (t4.ReadEntity(guildId, out GuildComponent g))
{
    g.Members = t4.RemoveFromEntityCollection(g.Members, bobId);
    t4.UpdateEntity(guildId, ref g);
    t4.Commit();
}
```

---

## MVCC Behavior

Collections participate fully in snapshot isolation:

```csharp
// Transaction 1 starts
using var t1 = dbe.CreateQuickTransaction();

if (t1.ReadEntity(entityId, out InventoryComponent inv1))
{
    var items1 = t1.ReadCollection(inv1.ItemIds);
    Console.WriteLine($"T1 sees {items1.Length} items");  // 3 items

    // Transaction 2 modifies collection
    using (var t2 = dbe.CreateQuickTransaction())
    {
        if (t2.ReadEntity(entityId, out InventoryComponent inv2))
        {
            inv2.ItemIds = t2.AppendToCollection(inv2.ItemIds, 999);
            t2.UpdateEntity(entityId, ref inv2);
            t2.Commit();
        }
    }

    // T1 still sees old snapshot
    if (t1.ReadEntity(entityId, out InventoryComponent inv1Again))
    {
        var items2 = t1.ReadCollection(inv1Again.ItemIds);
        Console.WriteLine($"T1 still sees {items2.Length} items");  // Still 3 items!
    }
}

// New transaction sees updated data
using var t3 = dbe.CreateQuickTransaction();
if (t3.ReadEntity(entityId, out InventoryComponent inv3))
{
    var items3 = t3.ReadCollection(inv3.ItemIds);
    Console.WriteLine($"T3 sees {items3.Length} items");  // 4 items
}
```

### Zero-Copy Optimization

When a component is updated but its collection fields remain unchanged, no new buffers are allocated:

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // Only modify non-collection field
    inv.Gold = 1000;

    // ItemIds and Quantities unchanged - same buffer IDs
    // On commit: reference counts incremented, no new buffers allocated
    t.UpdateEntity(entityId, ref inv);
    t.Commit();
}
```

---

## Performance Characteristics

| Operation | Complexity | Allocations | Notes |
|-----------|------------|-------------|-------|
| `CreateCollectionBuilder()` | O(1) | 1 (builder) | Reusable builder object |
| `Builder.Add()` | O(1) amortized | 0-1 | May resize internal storage |
| `Builder.Build()` | O(n) | 1 (buffer) | Allocates VSBS buffer, copies data |
| `ReadCollection()` | O(1) | 0 | Returns Span over buffer |
| `AppendToCollection()` | O(n) | 1 (buffer) | Optimizable for small n |
| `ModifyCollection()` | O(n) | 1-2 | Callback + buffer allocation |
| Iterate Span | O(n) | 0 | Zero-cost iteration |

---

## Implementation Notes

### Buffer Management

```csharp
internal class CollectionBufferManager
{
    private readonly ManagedPagedMMF _storage;
    private readonly ConcurrentDictionary<int, VariableSizedBufferSegment> _segments;

    public VariableSizedBufferSegment GetSegmentForType<T>() where T : unmanaged
    {
        int typeSize = Unsafe.SizeOf<T>();
        // Get or create VSBS for this type size
        return _segments.GetOrAdd(typeSize, size =>
            new VariableSizedBufferSegment<T>(_storage, segmentId));
    }

    public ComponentCollection<T> AllocateBuffer<T>(ReadOnlySpan<T> data, ChangeSet changeSet)
        where T : unmanaged
    {
        var segment = GetSegmentForType<T>();
        int bufferId = segment.AllocateBuffer(data.Length * Unsafe.SizeOf<T>(), changeSet);

        // Write data to buffer
        var buffer = segment.GetBuffer(bufferId);
        data.CopyTo(MemoryMarshal.Cast<byte, T>(buffer));

        return new ComponentCollection<T> { _bufferId = bufferId };
    }
}
```

### Transaction Integration

```csharp
public partial class Transaction
{
    private Dictionary<(Type, int), object>? _collectionCache;

    public ReadOnlySpan<T> ReadCollection<T>(ComponentCollection<T> collection)
        where T : unmanaged
    {
        if (collection.IsEmpty)
            return ReadOnlySpan<T>.Empty;

        // Cache resolved spans at transaction level
        var cacheKey = (typeof(T), collection._bufferId);

        if (_collectionCache?.TryGetValue(cacheKey, out var cached) == true)
            return (ReadOnlySpan<T>)cached;

        var segment = _engine.CollectionManager.GetSegmentForType<T>();
        var buffer = segment.GetBuffer(collection._bufferId);
        var span = MemoryMarshal.Cast<byte, T>(buffer);

        _collectionCache ??= new Dictionary<(Type, int), object>();
        _collectionCache[cacheKey] = span;

        return span;
    }

    public ComponentCollectionBuilder<T> CreateCollectionBuilder<T>()
        where T : unmanaged
    {
        return new ComponentCollectionBuilder<T>(this, _engine.CollectionManager);
    }

    public ComponentCollection<T> AppendToCollection<T>(
        ComponentCollection<T> collection, T item) where T : unmanaged
    {
        var builder = CreateCollectionBuilder<T>();

        if (!collection.IsEmpty)
        {
            var existing = ReadCollection(collection);
            builder.AddRange(existing);
        }

        builder.Add(item);
        return builder.Build();
    }
}
```

---

## Migration from Current Implementation

The current test code uses:
```csharp
using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);
cca.Add(item);
```

Migration to Hybrid Pattern:
```csharp
// Before (current accessor pattern)
using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);
cca.Add(item);

// After (hybrid pattern)
var builder = t.CreateCollectionBuilder<int>();
builder.Add(item);
e.Collection = builder.Build();
```

The accessor pattern can be kept as an internal implementation detail or deprecated in favor of the more explicit builder pattern.

---

## Open Questions

1. **Builder Reuse**: Should `ComponentCollectionBuilder<T>` be reusable (Clear() method), or one-time-use?
2. **Maximum Size**: Should collections have a maximum size limit (e.g., 64KB)?
3. **Indexed Access**: Should there be `GetItemAt(index)` helpers, or always use Span indexing?
4. **Bulk Helpers**: Should there be `AddRange(IEnumerable<T>)` in addition to `AddRange(ReadOnlySpan<T>)`?
5. **Source Generation**: Should collection field detection use source generators or reflection?

---

## Summary

**Recommended Design: Hybrid Pattern (Proposition 3)**

**Core API:**
- `CreateCollectionBuilder<T>()` - for building collections
- `ReadCollection<T>()` - for reading as Span
- `AppendToCollection<T>()` - helper for single-item additions
- `RemoveFromCollection<T>()` - helper for single-item removals
- `ModifyCollection<T>()` - callback pattern for complex updates

**Benefits:**
- ✅ Mirrors component CRUD patterns closely
- ✅ High performance with zero-copy reads
- ✅ Safe immutable semantics
- ✅ Progressive disclosure (simple → complex)
- ✅ MVCC-friendly with clear buffer lifecycle
- ✅ Optimizable for common operations

This design balances performance, safety, and usability while maintaining consistency with Typhon's existing component API.
