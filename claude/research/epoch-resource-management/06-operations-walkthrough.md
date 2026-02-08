# Operations Walkthrough — Before and After

**Parent:** [README.md](./README.md)
**Prerequisites:** [03 — Page Cache](./03-page-cache-evolution.md), [04 — ChunkAccessor](./04-chunk-accessor-redesign.md), [05 — Exclusive Access](./05-exclusive-access.md)

---

## How to Read This Document

For each operation, we show:
1. **Before**: The current implementation with ref-counting, pinning, and promotion
2. **After**: The epoch-based implementation
3. **What changed**: Highlighted simplifications

All examples use pseudo-logic, not actual C# code.

---

## Operation 1: B+Tree Lookup (Read Path)

The most frequent operation. A single-key lookup traversing the tree from root to leaf.

### Before

```
BTree.TryGet(primaryKey, ref accessor):
  // Start at root node
  chunkId = rootChunkId

  while true:
    // Access node — NO pinning (unsafe, but fast)
    ref node = ref accessor.GetChunkReadOnly<BTreeNode>(chunkId)

    // ⚠ WARNING: next GetChunkReadOnly call may evict this page,
    // invalidating 'node'. For a simple traversal this is OK because
    // we read the node, extract childChunkId, then move on.
    // But if we needed TWO nodes simultaneously, we'd need pinning.

    if node.IsLeaf:
      // Search leaf for key
      result = SearchLeaf(ref node, primaryKey)
      return result

    // Find child to descend into
    childIndex = FindChild(ref node, primaryKey)
    chunkId = node.Children[childIndex]
    // 'node' reference becomes stale after next GetChunkReadOnly
```

### After

```
BTree.TryGet(primaryKey, ref accessor):
  // Epoch scope already entered by Transaction (or caller)
  // All pages are safe for the entire scope — no eviction possible

  chunkId = rootChunkId

  while true:
    // Access node — SAFE (epoch-protected)
    ref node = ref accessor.GetChunkReadOnly<BTreeNode>(chunkId)

    // ✓ SAFE: even if accessor evicts this slot internally,
    // the underlying page memory is epoch-protected.
    // 'node' pointer remains valid.

    if node.IsLeaf:
      result = SearchLeaf(ref node, primaryKey)
      return result

    childIndex = FindChild(ref node, primaryKey)
    chunkId = node.Children[childIndex]
    // 'node' reference is STILL valid (epoch-protected)
    // We just don't need it anymore
```

### What Changed

```
Removed: Nothing in the logic itself
Added:   Nothing
Changed: Safety guarantee — GetChunkReadOnly returns a reference that's
         valid for the entire epoch scope, not just until the next call.

         This means callers can hold multiple references simultaneously
         without pinning. The "unsafe by default" API becomes safe.
```

### Performance Impact

**None.** The hot path is identical. The only difference is that the returned reference has a stronger validity guarantee (free — no extra work).

---

## Operation 2: B+Tree Insert (No Split)

Insert a key-value pair into a leaf node that has space.

### Before

```
BTree.Insert(key, value, ref accessor):
  // Navigate to correct leaf (same as lookup)
  leafChunkId = NavigateToLeaf(key, ref accessor)

  // Get mutable access to leaf — no pin (unsafe)
  ref leaf = ref accessor.GetChunk<BTreeNode>(leafChunkId, dirty: true)

  // Insert into leaf
  InsertIntoLeaf(ref leaf, key, value)

  // Done — leaf was modified in-place via dirty flag
  // accessor will flush dirty page on CommitChanges()
```

### After

```
BTree.Insert(key, value, ref accessor):
  // Navigate to correct leaf (same as lookup)
  leafChunkId = NavigateToLeaf(key, ref accessor)

  // Get mutable access to leaf — SAFE (epoch-protected)
  ref leaf = ref accessor.GetChunk<BTreeNode>(leafChunkId, dirty: true)

  // Insert into leaf
  InsertIntoLeaf(ref leaf, key, value)

  // Done — identical behavior
```

### What Changed

```
Removed: Nothing
Added:   Nothing
Changed: Safety guarantee on the ref return (same as lookup)
```

---

## Operation 3: B+Tree Insert with Split (Exclusive Access)

The complex case. A leaf is full and must be split, which propagates up the tree.

### Before

```
BTree.InsertWithSplit(key, value, ref accessor):
  // Navigate to leaf, collecting path (parent chain)
  path = NavigateToLeafWithPath(key, ref accessor)
  leafChunkId = path.Last()

  // Pin the leaf — prevent eviction during split
  using var leafHandle = accessor.GetChunkHandle(leafChunkId, dirty: true)
  ref leaf = ref leafHandle.AsRef<BTreeNode>()

  if !leaf.IsFull:
    InsertIntoLeaf(ref leaf, key, value)
    return  // leafHandle.Dispose() unpins

  // Leaf is full — need to split
  // Promote leaf page to exclusive (for in-place structural modification)
  if !accessor.TryPromoteChunk(leafChunkId):
    // Promotion failed (another thread holds shared access)
    // Must retry entire operation
    return RETRY

  // Allocate new sibling leaf
  newLeafChunkId = segment.AllocateChunk()

  // Pin the new leaf too
  using var newLeafHandle = accessor.GetChunkHandle(newLeafChunkId, dirty: true)
  ref newLeaf = ref newLeafHandle.AsRef<BTreeNode>()

  // Split: move half the keys to new leaf
  SplitLeaf(ref leaf, ref newLeaf, key, value)

  // Demote leaf page
  accessor.DemoteChunk(leafChunkId)

  // Propagate split to parent
  parentChunkId = path[path.Count - 2]
  using var parentHandle = accessor.GetChunkHandle(parentChunkId, dirty: true)
  ref parent = ref parentHandle.AsRef<BTreeNode>()

  // ⚠ We now hold 3 handles: leaf, newLeaf, parent
  // Getting close to pin pressure if this recurses...

  if !parent.IsFull:
    InsertChildPointer(ref parent, newLeafChunkId, splitKey)
    return  // all handles dispose, unpinning slots

  // Parent also full — recursive split
  // ⚠ Each recursion level adds 2-3 pinned handles
  // ⚠ Deep tree (5+ levels) × 3 handles = 15 pins → CRASH RISK
  ...
```

### After

```
BTree.InsertWithSplit(key, value, ref accessor):
  // Navigate to leaf, collecting path
  path = NavigateToLeafWithPath(key, ref accessor)
  leafChunkId = path.Last()

  ref leaf = ref accessor.GetChunk<BTreeNode>(leafChunkId, dirty: true)
  // ✓ SAFE: epoch-protected, no handle needed

  if !leaf.IsFull:
    InsertIntoLeaf(ref leaf, key, value)
    return

  // Leaf is full — need to split
  // Acquire exclusive on the leaf's page (page-level latch)
  pageIndex = segment.GetPageIndex(leafChunkId)
  mmf.AcquireExclusive(pageIndex)

  // Allocate new sibling leaf
  newLeafChunkId = segment.AllocateChunk()
  ref newLeaf = ref accessor.GetChunk<BTreeNode>(newLeafChunkId, dirty: true)
  // ✓ SAFE: both leaf and newLeaf are epoch-protected, no pin limit

  // Split
  SplitLeaf(ref leaf, ref newLeaf, key, value)

  // Release exclusive on leaf's page
  mmf.ReleaseExclusive(pageIndex)

  // Propagate to parent
  parentChunkId = path[path.Count - 2]
  ref parent = ref accessor.GetChunk<BTreeNode>(parentChunkId, dirty: true)
  // ✓ SAFE: we can hold refs to leaf, newLeaf, AND parent simultaneously
  // No pin pressure. No slot limit concern.

  if !parent.IsFull:
    InsertChildPointer(ref parent, newLeafChunkId, splitKey)
    return

  // Parent also full — recursive split
  // ✓ Each recursion level just adds GetChunk calls
  // ✓ No pinning means no crash risk regardless of tree depth
  // ✓ Exclusive access is per-page, acquired/released per node
  ...
```

### What Changed

```
Removed:
  - GetChunkHandle (×3: leaf, newLeaf, parent)
  - TryPromoteChunk / DemoteChunk
  - using statements for handle disposal
  - "All slots pinned" crash risk during deep recursion

Added:
  - mmf.AcquireExclusive / ReleaseExclusive (direct page-level)
  - One pageIndex lookup for exclusive

Changed:
  - Exclusive access is per-PAGE, not per-ChunkAccessor-slot
  - No limit on simultaneous node references
  - Recursive splits are safe regardless of depth
  - Code reads linearly (no nested using blocks)
```

---

## Operation 4: Revision Chain Walk

Walking a component's revision chain to find the correct version for a transaction's snapshot.

### Before

```
RevisionChainWalker(firstChunkId, targetTSN, ref accessor):
  // Pin the first chunk of the chain
  using var firstHandle = accessor.GetChunkHandle(firstChunkId, false)
  ref header = ref firstHandle.AsRef<CompRevStorageHeader>()

  chunkId = firstChunkId
  while true:
    using var chunkHandle = accessor.GetChunkHandle(chunkId, false)
    ref chunk = ref chunkHandle.AsRef<RevisionChunk>()

    // ⚠ Now holding firstHandle AND chunkHandle pinned simultaneously
    // If chain spans many chunks, we might exhaust pins

    for each revision in chunk:
      if revision.TSN <= targetTSN:
        return revision

    chunkId = chunk.NextChunkId
    if chunkId == InvalidId: break
    // chunkHandle.Dispose() releases pin before next iteration

  return NOT_FOUND
```

### After

```
RevisionChainWalker(firstChunkId, targetTSN, ref accessor):
  ref header = ref accessor.GetChunkReadOnly<CompRevStorageHeader>(firstChunkId)
  // ✓ No handle, no pin. Epoch-protected.

  chunkId = firstChunkId
  while true:
    ref chunk = ref accessor.GetChunkReadOnly<RevisionChunk>(chunkId)
    // ✓ Safe to hold refs to multiple chunks simultaneously
    // ✓ No pin limit — chain can be arbitrarily long

    for each revision in chunk:
      if revision.TSN <= targetTSN:
        return revision

    chunkId = chunk.NextChunkId
    if chunkId == InvalidId: break

  return NOT_FOUND
```

### What Changed

```
Removed:
  - GetChunkHandle (×N where N = chain length)
  - using statements per chunk
  - Simultaneous pin concerns

Changed:
  - Direct GetChunkReadOnly calls (faster, simpler)
  - No limit on chain length traversal
  - Linear, simple code flow
```

---

## Operation 5: Transaction CRUD (Create Entity)

Creating a new entity with a component.

### Before

```
Transaction.CreateEntity<T>(ref T component):
  // Get or create component info for this type
  info = GetOrCreateComponentInfo<T>()

  // Allocate chunk for component data
  contentChunkId = info.ComponentSegment.AllocateChunk()

  // Write component data using transaction's accessor
  ref chunk = ref info.CompContentAccessor.GetChunk<T>(contentChunkId, dirty: true)
  chunk = component  // copy data

  // Create revision entry
  revChunkId = info.CompRevTableSegment.AllocateChunk()

  // ⚠ Critical: info.CompRevTableAccessor is stored as a FIELD in ComponentInfo
  // If ComponentInfo is passed by value (struct copy), the accessor is copied
  // The copy's Dispose won't affect the original → ref count leak
  // Must access via reference to the original field!
  using var revHandle = info.CompRevTableAccessor.GetChunkHandle(revChunkId, dirty: true)
  ref revHeader = ref revHandle.AsRef<CompRevStorageHeader>()
  revHeader.FirstItemRevision = currentTSN
  ...

  // Later, in Transaction.Dispose():
  //   Must call info.CompContentAccessor.Dispose()
  //   Must call info.CompRevTableAccessor.Dispose()
  //   Must do this via direct field reference, not copy!
  //   Getting this wrong → page cache exhaustion
```

### After

```
Transaction.CreateEntity<T>(ref T component):
  // Epoch scope entered in Transaction constructor
  // All page access is epoch-protected for transaction lifetime

  info = GetOrCreateComponentInfo<T>()

  contentChunkId = info.ComponentSegment.AllocateChunk()
  ref chunk = ref info.CompContentAccessor.GetChunk<T>(contentChunkId, dirty: true)
  chunk = component

  revChunkId = info.CompRevTableSegment.AllocateChunk()
  ref revHeader = ref info.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(
    revChunkId, dirty: true)
  revHeader.FirstItemRevision = currentTSN
  ...

  // In Transaction.Dispose():
  //   info.CompContentAccessor.Dispose() → flushes dirty pages (simple)
  //   info.CompRevTableAccessor.Dispose() → flushes dirty pages (simple)
  //   No ref count concerns. Struct copy of accessor is harmless.
  //   EpochGuard.Dispose() exits scope (single operation)
```

### What Changed

```
Removed:
  - GetChunkHandle for revision entry
  - using statement for handle
  - Critical "must access via field reference" constraint
  - Struct copy danger in ComponentInfo

Changed:
  - Direct GetChunk calls everywhere (uniform API)
  - Dispose() is about dirty flushing only, not correctness
  - Struct copy of accessor is performance-only concern, not bug source
```

---

## Operation 6: Segment Growth

When a ChunkBasedSegment runs out of space and needs to allocate new pages.

### Before

```
ChunkBasedSegment.AllocateChunk():
  // Find free chunk via bitmap
  chunkId = BitmapL3.FindFreeChunk()

  if chunkId == NONE:
    // Need more pages — segment growth
    // ⚠ CRITICAL: Must dispose all active accessors BEFORE growth
    // because growth may change segment metadata that accessors cache
    //
    // This is the most error-prone part of the system:
    //   1. All callers must know to dispose their accessors
    //   2. After growth, they must recreate accessors
    //   3. If any accessor survives growth, its cached addresses may be stale

    GrowSegment()
    chunkId = BitmapL3.FindFreeChunk()

  return chunkId
```

### After

```
ChunkBasedSegment.AllocateChunk():
  chunkId = BitmapL3.FindFreeChunk()

  if chunkId == NONE:
    // Need more pages — segment growth
    // Acquire exclusive on bitmap/metadata pages
    mmf.AcquireExclusive(bitmapPageIndex)

    GrowSegment()
    // New pages get AccessEpoch = GlobalEpoch (freshly loaded)

    mmf.ReleaseExclusive(bitmapPageIndex)
    chunkId = BitmapL3.FindFreeChunk()

    // Existing ChunkAccessors may have stale local slot data
    // (pointing to old pages that are still valid in the page cache)
    // BUT: the page memory is epoch-protected, so old pointers still work.
    // New allocations go to new pages, which accessors will load on demand.

  return chunkId
```

### What Changed

```
Removed:
  - "Must dispose all active accessors before growth" constraint
  - Accessor recreation after growth

Added:
  - Exclusive lock on bitmap page during growth

Changed:
  - Growth no longer requires coordinating with all accessors
  - Old accessor slot data may be stale but pointers remain valid
  - New pages are loaded on-demand through normal cache miss path
```

---

## Operation 7: Transaction Lifecycle

The full lifecycle of a transaction, showing epoch scope interaction.

### After (New Model)

```
Transaction.Create():
  // 1. Enter epoch scope — pins thread to current epoch
  _epochGuard = EpochGuard.Enter()

  // 2. Create accessor per component table (lightweight, no ref counting)
  for each registered component type:
    info.CompContentAccessor = segment.CreateChunkAccessor(changeSet)
    info.CompRevTableAccessor = segment.CreateChunkAccessor(changeSet)

Transaction.ReadEntity(entityId, out T component):
  // 3. Look up in primary key index
  chunkId = info.PrimaryKeyIndex.TryGet(entityId, ref accessor)

  // 4. Walk revision chain to find correct version
  revisionChunkId = RevisionChainWalker(chunkId, this.TSN, ref revAccessor)

  // 5. Read component data
  ref readonly data = ref info.CompContentAccessor.GetChunkReadOnly<T>(revisionChunkId)
  component = data  // copy out

Transaction.Commit():
  // 6. Flush dirty pages from all accessors
  for each component info:
    info.CompContentAccessor.CommitChanges()
    info.CompRevTableAccessor.CommitChanges()

  // 7. Commit transaction (MVCC logic — unchanged)
  TransactionChain.Commit(this)

Transaction.Dispose():
  // 8. Dispose accessors (flush any remaining dirty pages)
  for each component info:
    info.CompContentAccessor.Dispose()
    info.CompRevTableAccessor.Dispose()

  // 9. Exit epoch scope — unpin thread, advance global epoch
  _epochGuard.Dispose()

  // 10. Return transaction to pool (unchanged)
```

### Key Observations

1. **One EpochGuard per transaction**: All page access within the transaction is protected by a single scope.
2. **Accessor Dispose is simple**: Just dirty page flushing, no ref count management.
3. **Struct copy safety**: ComponentInfo can be freely copied within the transaction — no correctness risk.
4. **Nested operations**: B+Tree lookups/inserts within the transaction inherit the epoch scope (they just increment/decrement the nesting depth — zero cost).

---

## Summary Table

| Operation | Current Complexity | Epoch Complexity | Main Simplification |
|-----------|-------------------|------------------|---------------------|
| B+Tree Lookup | Low (unsafe refs) | Low (safe refs) | Safety for free |
| B+Tree Insert | Medium (dirty tracking) | Low (same) | No change |
| B+Tree Split | **Very High** (pin×3, promote, demote, recursion risk) | **Low** (exclusive latch, direct refs) | Dramatic: eliminates all pin/promote ceremony |
| Revision Walk | Medium (handle per chunk) | Low (direct refs) | Eliminates N handles for chain of length N |
| Transaction CRUD | **High** (struct copy danger, field reference requirement) | **Low** (no struct copy danger) | Eliminates entire class of bugs |
| Segment Growth | **Very High** (must dispose all accessors, recreate after) | **Medium** (exclusive on metadata, no accessor coordination) | Eliminates accessor coordination |

---

**Next:** [07 — Performance Analysis](./07-performance-analysis.md) — CPU cycle projections and comparisons.
