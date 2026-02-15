# ComponentCollectionBuilder Implementation Design

## Overview

This document details the implementation of `ComponentCollectionBuilder<T>` using a **direct VSBS buffer approach** - no temporary managed allocations, just work directly in the persistent buffer.

## Core Principle

**Work directly in VSBS buffers from the start.**

- Builder allocates VSBS buffer on first `Add()`
- Items are written directly to the buffer (no intermediate List/Array)
- On `Build()`, return ComponentCollection with the buffer ID
- Transaction tracks builder buffers for rollback cleanup
- On commit: buffers stay (already populated)
- On rollback: free unused builder buffers

## Implementation

### 1. ComponentCollectionBuilder\<T>

```csharp
public ref struct ComponentCollectionBuilder<T> : IDisposable where T : unmanaged
{
    private readonly Transaction _transaction;
    private readonly VariableSizedBufferSegment<T> _vsbs;
    private readonly ChunkRandomAccessor _accessor;
    private int _bufferId;
    private bool _built;

    internal ComponentCollectionBuilder(
        Transaction transaction,
        VariableSizedBufferSegment<T> vsbs)
    {
        _transaction = transaction;
        _vsbs = vsbs;
        _accessor = vsbs.Segment.CreateChunkAccessor(transaction.ChangeSet);
        _bufferId = 0;  // Lazy allocation on first Add
        _built = false;
    }

    public void Add(T item)
    {
        if (_built)
            throw new InvalidOperationException("Builder has already been built.");

        // Allocate buffer on first Add
        if (_bufferId == 0)
        {
            _bufferId = _vsbs.AllocateBuffer(_accessor);

            // Register with transaction for rollback cleanup
            _transaction.RegisterBuilderBuffer(_vsbs, _bufferId);
        }

        // Add directly to VSBS buffer
        _vsbs.AddElement(_bufferId, item, _accessor);
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        if (_built)
            throw new InvalidOperationException("Builder has already been built.");

        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void Clear()
    {
        if (_built)
            throw new InvalidOperationException("Builder has already been built.");

        if (_bufferId != 0)
        {
            // Free current buffer
            _transaction.UnregisterBuilderBuffer(_bufferId);
            _vsbs.DeleteBuffer(_bufferId, _accessor);
            _bufferId = 0;
        }
    }

    public ComponentCollection<T> Build()
    {
        if (_built)
            throw new InvalidOperationException("Builder has already been built.");

        _built = true;

        if (_bufferId == 0)
            return ComponentCollection<T>.Empty;

        // Unregister - buffer is now owned by component, not builder
        _transaction.UnregisterBuilderBuffer(_bufferId);

        return new ComponentCollection<T> { _bufferId = _bufferId };
    }

    public void Dispose()
    {
        _accessor.CommitChanges();
        _accessor.Dispose();
    }
}
```

**Key Points:**
- `ref struct` - stack-only, no boxing
- Lazy buffer allocation on first `Add()`
- Direct writes to VSBS (zero intermediate allocations)
- `_built` flag prevents reuse after Build()
- Dispose commits any pending chunk changes

---

### 2. Transaction Extensions

```csharp
public partial class Transaction
{
    // Cache of VSBS instances per type
    private Dictionary<Type, object>? _vsbsCache;

    // Track builder buffers for rollback cleanup
    private List<(VariableSizedBufferSegmentBase vsbs, int bufferId)>? _pendingBuilderBuffers;

    public ComponentCollectionBuilder<T> CreateCollectionBuilder<T>() where T : unmanaged
    {
        var vsbs = GetOrCreateVariableSizedBufferSegment<T>();
        return new ComponentCollectionBuilder<T>(this, vsbs);
    }

    internal VariableSizedBufferSegment<T> GetOrCreateVariableSizedBufferSegment<T>()
        where T : unmanaged
    {
        _vsbsCache ??= new Dictionary<Type, object>();

        var type = typeof(T);
        if (_vsbsCache.TryGetValue(type, out var cached))
            return (VariableSizedBufferSegment<T>)cached;

        var vsbs = _engine.GetOrCreateCollectionSegment<T>();
        _vsbsCache[type] = vsbs;
        return vsbs;
    }

    internal void RegisterBuilderBuffer(VariableSizedBufferSegmentBase vsbs, int bufferId)
    {
        _pendingBuilderBuffers ??= new List<(VariableSizedBufferSegmentBase, int)>();
        _pendingBuilderBuffers.Add((vsbs, bufferId));
    }

    internal void UnregisterBuilderBuffer(int bufferId)
    {
        if (_pendingBuilderBuffers != null)
        {
            for (int i = _pendingBuilderBuffers.Count - 1; i >= 0; i--)
            {
                if (_pendingBuilderBuffers[i].bufferId == bufferId)
                {
                    _pendingBuilderBuffers.RemoveAt(i);
                    return;
                }
            }
        }
    }

    public ReadOnlySpan<T> ReadCollection<T>(ComponentCollection<T> collection)
        where T : unmanaged
    {
        if (collection.IsEmpty)
            return ReadOnlySpan<T>.Empty;

        var vsbs = GetOrCreateVariableSizedBufferSegment<T>();
        using var accessor = vsbs.GetReadOnlyAccessor(collection._bufferId, ChangeSet);
        return accessor.ReadOnlyElements;
    }

    protected override void OnCommit()
    {
        // Buffers successfully used in committed components - just clear tracking
        _pendingBuilderBuffers?.Clear();
    }

    protected override void OnRollback()
    {
        // Free any builder buffers that weren't built into components
        if (_pendingBuilderBuffers != null)
        {
            foreach (var (vsbs, bufferId) in _pendingBuilderBuffers)
            {
                using var accessor = vsbs.Segment.CreateChunkAccessor(ChangeSet);
                vsbs.DeleteBuffer(bufferId, accessor);
            }
            _pendingBuilderBuffers.Clear();
        }
    }
}
```

**Key Points:**
- `_vsbsCache` avoids repeated lookups per transaction
- `_pendingBuilderBuffers` tracks buffers for rollback cleanup
- `RegisterBuilderBuffer()` called when buffer allocated
- `UnregisterBuilderBuffer()` called when Build() succeeds
- On rollback: free all pending builder buffers
- On commit: clear tracking (buffers referenced by components)

---

### 3. DatabaseEngine - VSBS Management

```csharp
public partial class DatabaseEngine
{
    // One VSBS per element type (shared across all collections)
    private readonly ConcurrentDictionary<Type, object> _collectionSegments = new();

    internal VariableSizedBufferSegment<T> GetOrCreateCollectionSegment<T>()
        where T : unmanaged
    {
        var type = typeof(T);

        if (_collectionSegments.TryGetValue(type, out var existing))
            return (VariableSizedBufferSegment<T>)existing;

        return (VariableSizedBufferSegment<T>)_collectionSegments.GetOrAdd(type, _ =>
        {
            var elementSize = Unsafe.SizeOf<T>();
            var stride = ComputeOptimalStride(elementSize);

            var segmentId = AllocateNewSegmentId();
            var chunkSegment = new ChunkBasedSegment(
                _storage,
                segmentId,
                stride,
                "CollectionSegment_" + type.Name);

            return new VariableSizedBufferSegment<T>(chunkSegment);
        });
    }

    private int ComputeOptimalStride(int elementSize)
    {
        // Target ~8-16 elements per chunk
        int targetElementsPerChunk = 16;
        int baseSize = elementSize * targetElementsPerChunk;

        // Add header overhead
        unsafe { baseSize += sizeof(VariableSizedBufferRootHeader); }

        // Round up to standard sizes
        if (baseSize <= 64) return 64;
        if (baseSize <= 128) return 128;
        if (baseSize <= 256) return 256;
        if (baseSize <= 512) return 512;
        if (baseSize <= 1024) return 1024;
        if (baseSize <= 2048) return 2048;
        return 4096;
    }
}
```

---

## Complete Flow Examples

### Example 1: Create Collection

```csharp
using var t = dbe.CreateQuickTransaction();

// 1. Create builder
var builder = t.CreateCollectionBuilder<int>();
// → No VSBS buffer allocated yet
// → _bufferId = 0

// 2. Add first item
builder.Add(100);
// → VSBS buffer allocated: _bufferId = 42
// → Transaction.RegisterBuilderBuffer(vsbs, 42)
// → Item written to buffer: [100]

// 3. Add more items
builder.Add(200);
builder.Add(300);
// → Items written directly to buffer: [100, 200, 300]

// 4. Build collection
var items = builder.Build();
// → Transaction.UnregisterBuilderBuffer(42)
// → Returns ComponentCollection<int> { _bufferId = 42 }

// 5. Use in component
var inv = new InventoryComponent { ItemIds = items };
var entityId = t.CreateEntity(ref inv);

// 6. Commit
t.Commit();
// → Component revision written with bufferId=42
// → Buffer already populated and ready
// → _pendingBuilderBuffers cleared
```

**Memory State After Commit:**
```
VariableSizedBufferSegment<int>:
  Buffer 42:
    RefCount: 1 (referenced by component revision)
    Elements: [100, 200, 300]
```

---

### Example 2: Update Collection (Copy + Modify)

```csharp
using var t = dbe.CreateQuickTransaction();

if (t.ReadEntity(entityId, out InventoryComponent inv))
{
    // 1. Create builder
    var builder = t.CreateCollectionBuilder<int>();

    // 2. Copy existing items
    var existing = t.ReadCollection(inv.ItemIds);  // [100, 200, 300]
    builder.AddRange(existing);
    // → New buffer allocated: _bufferId = 99
    // → Transaction.RegisterBuilderBuffer(vsbs, 99)
    // → Items copied: [100, 200, 300]

    // 3. Add new item
    builder.Add(400);
    // → Buffer now: [100, 200, 300, 400]

    // 4. Build and replace
    inv.ItemIds = builder.Build();
    // → Transaction.UnregisterBuilderBuffer(99)
    // → inv.ItemIds = ComponentCollection<int> { _bufferId = 99 }

    // 5. Update entity
    t.UpdateEntity(entityId, ref inv);
    t.Commit();
    // → New revision written with bufferId=99
}
```

**Memory State After Commit:**
```
VariableSizedBufferSegment<int>:
  Buffer 42 (old):
    RefCount: 1 (referenced by old revision)
    Elements: [100, 200, 300]

  Buffer 99 (new):
    RefCount: 1 (referenced by new revision)
    Elements: [100, 200, 300, 400]
```

**Later, when MinTick advances past old revision:**
```
Old revision garbage collected:
  → DeleteBuffer(42) called
  → RefCount: 1 → 0
  → Buffer 42 freed
```

---

### Example 3: Rollback Scenario

```csharp
using var t = dbe.CreateQuickTransaction();

var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);
builder.Add(200);
// → Buffer allocated: _bufferId = 77
// → Transaction.RegisterBuilderBuffer(vsbs, 77)
// → Buffer contains: [100, 200]

// Something goes wrong...
t.Rollback();
// → OnRollback() called
// → Iterates _pendingBuilderBuffers
// → Finds (vsbs, 77)
// → vsbs.DeleteBuffer(77)
// → Buffer 77 freed immediately
// → No leak!
```

---

### Example 4: Builder Without Build() (Abandoned)

```csharp
using var t = dbe.CreateQuickTransaction();

var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);
// → Buffer allocated: _bufferId = 55
// → Transaction.RegisterBuilderBuffer(vsbs, 55)

// Builder goes out of scope without Build()
builder.Dispose();

// Later...
t.Commit();
// → OnCommit() called
// → _pendingBuilderBuffers still contains (vsbs, 55)
// → Buffer 55 leaked!
```

**Wait, this is a problem!** If user creates builder, adds items, but never calls Build(), and then commits, the buffer leaks.

**Solution:** Clear pending buffers on commit too:

```csharp
protected override void OnCommit()
{
    // Free any builder buffers that were never built
    if (_pendingBuilderBuffers != null)
    {
        foreach (var (vsbs, bufferId) in _pendingBuilderBuffers)
        {
            using var accessor = vsbs.Segment.CreateChunkAccessor(ChangeSet);
            vsbs.DeleteBuffer(bufferId, accessor);
        }
        _pendingBuilderBuffers.Clear();
    }
}
```

**Now both commit and rollback clean up abandoned builders.**

---

## Helper Methods for Common Operations

### AppendToCollection

```csharp
public partial class Transaction
{
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

**Usage:**
```csharp
inv.ItemIds = t.AppendToCollection(inv.ItemIds, 400);
```

**What happens:**
1. New buffer allocated
2. Old items copied: [100, 200, 300]
3. New item added: [100, 200, 300, 400]
4. Build() returns new ComponentCollection
5. Old buffer has RefCount=1 (from old revision)
6. New buffer has RefCount=1 (from new revision)
7. On next MinTick GC, old buffer freed

---

### RemoveFromCollection

```csharp
public ComponentCollection<T> RemoveFromCollection<T>(
    ComponentCollection<T> collection, T item) where T : unmanaged
{
    if (collection.IsEmpty)
        return collection;

    var builder = CreateCollectionBuilder<T>();
    var existing = ReadCollection(collection);

    // Copy all except the item to remove
    foreach (var elem in existing)
    {
        if (!EqualityComparer<T>.Default.Equals(elem, item))
        {
            builder.Add(elem);
        }
    }

    return builder.Build();
}
```

**Usage:**
```csharp
inv.ItemIds = t.RemoveFromCollection(inv.ItemIds, 200);
```

---

### ModifyCollection

```csharp
public ComponentCollection<T> ModifyCollection<T>(
    ComponentCollection<T> collection,
    Action<ComponentCollectionBuilder<T>> modifier) where T : unmanaged
{
    var builder = CreateCollectionBuilder<T>();

    if (!collection.IsEmpty)
    {
        var existing = ReadCollection(collection);
        builder.AddRange(existing);
    }

    modifier(builder);
    return builder.Build();
}
```

**Usage:**
```csharp
inv.ItemIds = t.ModifyCollection(inv.ItemIds, builder =>
{
    // Remove items < 200
    // This is tricky because we already added existing items...
    // Better signature below
});
```

**Better signature:**
```csharp
public ComponentCollection<T> ModifyCollection<T>(
    ComponentCollection<T> collection,
    Func<ReadOnlySpan<T>, ComponentCollectionBuilder<T>, ComponentCollectionBuilder<T>> modifier)
    where T : unmanaged
{
    var builder = CreateCollectionBuilder<T>();
    var existing = collection.IsEmpty ? ReadOnlySpan<T>.Empty : ReadCollection(collection);

    modifier(existing, builder);
    return builder.Build();
}
```

**Usage:**
```csharp
inv.ItemIds = t.ModifyCollection(inv.ItemIds, (current, builder) =>
{
    // Filter and transform
    foreach (var item in current)
    {
        if (item >= 200)
            builder.Add(item);
    }

    // Add new items
    builder.Add(500);
    builder.Add(600);

    return builder;
});
```

---

## Performance Characteristics

### Memory Allocations

**Creating Collection (10 items):**
```
CreateCollectionBuilder():  0 allocations (ref struct, stack-only)
Add() x 10:                 0 allocations (writes to VSBS)
AllocateBuffer():           1 allocation (VSBS chunk)
Build():                    0 allocations (returns value type)
───────────────────────────────────────────────────────────
Total:                      1 allocation (the buffer itself)
```

**Compared to List\<T> approach:**
```
new List<T>():              1 allocation
Add() x 10:                 1-2 allocations (List growth)
Build():                    1 allocation (copy to VSBS)
───────────────────────────────────────────────────────────
Total:                      3-4 allocations (2-3 temporary!)
```

**Zero intermediate allocations** is a huge win! 🎉

### Time Complexity

| Operation | Complexity | Notes |
|-----------|------------|-------|
| CreateBuilder() | O(1) | Stack allocation |
| First Add() | O(1) | Allocate buffer |
| Subsequent Add() | O(1) amortized | May allocate new chunk |
| AddRange(n) | O(n) | n Add() calls |
| Build() | O(1) | Return struct |
| AppendToCollection(n, 1) | O(n) | Copy existing + add |
| RemoveFromCollection(n, x) | O(n) | Copy filtering |

### Comparison to Previous Designs

| Metric | List\<T> Builder | VSBS Builder (This) |
|--------|-----------------|---------------------|
| Managed allocations | 2-3 | 0 |
| Persistent allocations | 1 | 1 |
| Memory copies | 1-2 (growth + final) | 0 (write-once) |
| GC pressure | High | Zero |
| Code complexity | Medium | Low |
| MVCC semantics | Complex (defer to commit) | Simple (track for rollback) |

---

## Edge Cases & Error Handling

### 1. Empty Collection

```csharp
var builder = t.CreateCollectionBuilder<int>();
var empty = builder.Build();
// → No buffer allocated (_bufferId = 0)
// → Returns ComponentCollection<int>.Empty
```

### 2. Builder Reuse After Build()

```csharp
var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);
var c1 = builder.Build();

builder.Add(200);  // ← THROWS InvalidOperationException
```

**Reason:** Prevents confusion about ownership of buffer.

### 3. Abandoned Builder

```csharp
var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);
// Don't call Build()

t.Commit();
// → OnCommit() frees buffer allocated by builder
// → No leak
```

### 4. Builder During Rollback

```csharp
var builder = t.CreateCollectionBuilder<int>();
builder.Add(100);

t.Rollback();
// → OnRollback() frees buffer
// → Builder now has invalid _bufferId

builder.Build();  // ← What happens?
```

**Issue:** Builder has `_bufferId` but buffer was freed.

**Solution:** Clear builder state on rollback, or throw on Build():

```csharp
public ref struct ComponentCollectionBuilder<T>
{
    private bool _disposed;

    internal void MarkDisposed()
    {
        _disposed = true;
    }

    public ComponentCollection<T> Build()
    {
        if (_disposed)
            throw new ObjectDisposedException("Builder is no longer valid after transaction rollback.");

        // ... rest
    }
}

// In Transaction:
protected override void OnRollback()
{
    if (_pendingBuilderBuffers != null)
    {
        foreach (var (vsbs, bufferId) in _pendingBuilderBuffers)
        {
            // Free buffer...
        }

        // Mark all builders as disposed
        // But how? We don't have references to them...
    }
}
```

**Actually, this is fine:** After rollback, transaction is disposed anyway. Builder can't be used because it has a reference to the disposed transaction. Users shouldn't use builder after transaction ends.

---

## Benefits of This Design

✅ **Zero intermediate allocations** - Work directly in VSBS
✅ **Simple ownership** - Buffer owned by builder until Build()
✅ **Natural cleanup** - Transaction tracks for rollback
✅ **Efficient updates** - Copy-on-write with direct buffer operations
✅ **MVCC compatible** - RefCount handles old/new revisions
✅ **Low complexity** - No deferred resolution, no field walking
✅ **Performance** - Single allocation, no copies, direct writes

---

## Implementation Checklist

**Phase 1: Core Builder**
- [ ] `ComponentCollectionBuilder<T>` ref struct
- [ ] Lazy buffer allocation on first Add()
- [ ] Direct VSBS write operations
- [ ] Build() returns ComponentCollection
- [ ] Transaction.CreateCollectionBuilder()

**Phase 2: Transaction Tracking**
- [ ] `_pendingBuilderBuffers` list
- [ ] RegisterBuilderBuffer() / UnregisterBuilderBuffer()
- [ ] OnCommit() cleanup
- [ ] OnRollback() cleanup

**Phase 3: Helper Methods**
- [ ] ReadCollection()
- [ ] AppendToCollection()
- [ ] RemoveFromCollection()
- [ ] ModifyCollection()

**Phase 4: VSBS Management**
- [ ] DatabaseEngine.GetOrCreateCollectionSegment()
- [ ] ComputeOptimalStride()
- [ ] One VSBS per type

**Phase 5: Bulk Operations (Optional Optimization)**
- [ ] VariableSizedBufferSegment.AddElements() bulk write
- [ ] ComponentCollectionBuilder.AddRange() using bulk write

---

## Summary

The simplified design:
1. **Works directly in VSBS buffers** from the start
2. **Zero managed allocations** during building
3. **Transaction tracks builder buffers** for cleanup
4. **Commit/Rollback** handle cleanup automatically
5. **RefCount + MinTick GC** handle old revision cleanup

This is **way simpler** than the over-engineered approaches with List\<T> staging or deferred commit-time allocation. Sometimes the straightforward solution is the best one! 🎯
