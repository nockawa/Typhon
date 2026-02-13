# #44 — Cancellation Yield Points & Holdoff Regions

**Date:** 2026-02-13
**Status:** Design
**GitHub Issue:** #44
**Decisions:** D4, D5

> 💡 **Quick summary:** Insert `ctx.ThrowIfCancelled()` at safe locations throughout the engine (B+Tree traversal, revision chain walks, commit entry). Wrap critical sections (node splits, commit loop, revision append) in holdoff regions via `using var holdoff = ctx.EnterHoldoff()`. The commit loop runs entirely inside holdoff to prevent partial commit.

## Overview

Cooperative cancellation requires two complementary mechanisms:

1. **Yield points** — locations where `ctx.ThrowIfCancelled()` is called. If the deadline has expired or the token is cancelled (and we're not in holdoff), an exception is thrown. The operation aborts cleanly.

2. **Holdoff regions** — critical sections wrapped in `ctx.EnterHoldoff()` where cancellation checks are suppressed. This prevents aborting mid-split, mid-write, or mid-commit.

### Design Principle: Commit Atomicity

**The most important design decision in this document:**

The entire `Transaction.Commit()` loop (lines 1603-1652 in `Transaction.cs`) runs inside a holdoff region. A yield point is placed **only at the start** of `Commit()`, before any modifications begin. Once commit starts modifying revision chains and indexes, it runs to completion.

**Why:** If a yield point existed *between* component type iterations (e.g., after committing component A but before component B), a timeout could produce a partial commit — component A persisted, component B not. This violates transaction atomicity.

```
Commit() entry
    ├── ctx.ThrowIfCancelled()     ← YIELD POINT (safe: nothing modified yet)
    ├── ctx.EnterHoldoff()         ← HOLDOFF START
    │   ├── CommitComponent(A)     ← runs to completion
    │   ├── CommitComponent(B)     ← runs to completion
    │   └── CommitComponent(C)     ← runs to completion
    └── holdoff.Dispose()          ← HOLDOFF END
```

### Implementation Dependency on #45

This sub-issue defines WHERE yield points and holdoff regions go. The actual parameter threading (`ref UnitOfWorkContext` through method signatures) is shared with #45. In practice, #44 and #45 will be implemented together since internal methods need the context parameter to call `ThrowIfCancelled()`.

## Yield Point Catalog

### Category 1: Transaction Commit Entry

| Location | File | Line | Why Safe |
|----------|------|------|----------|
| Start of `Commit()` | `Transaction.cs` | ~1596 | Nothing modified yet; transaction can be retried |
| Start of `Rollback()` | `Transaction.cs` | (rollback entry) | Nothing cleaned up yet |

```csharp
// In Transaction.Commit(ref UnitOfWorkContext ctx, ...)
public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
{
    AssertThreadAffinity();

    if (State is TransactionState.Created) return true;
    if (State is TransactionState.Rollbacked or TransactionState.Committed) return false;

    // ── Yield point: safe to cancel before any modifications ──
    ctx.ThrowIfCancelled();

    using var holdoff = ctx.EnterHoldoff();  // Entire commit loop in holdoff

    // ... existing commit loop (lines 1603-1672) ...
}
```

### Category 2: B+Tree Traversal (Read Path)

| Location | File | Method | Line | Why Safe |
|----------|------|--------|------|----------|
| Between internal node traversals | `BTree.cs` | `FindLeaf()` | ~831 | Read-only traversal, can restart |

```csharp
// In BTree.FindLeaf()
private NodeWrapper FindLeaf(TKey key, out int index, ref ChunkAccessor accessor,
    ref UnitOfWorkContext ctx)
{
    index = -1;
    if (IsEmpty(ref accessor)) return default;

    var node = Root;
    while (!node.GetIsLeaf(ref accessor))
    {
        node = node.GetNearestChild(key, Comparer, ref accessor);
        ctx.ThrowIfCancelled();  // ← YIELD POINT: between node traversals
    }
    index = node.Find(key, Comparer, ref accessor);
    return node;
}
```

**Note:** This yield point is in the read path (lookups). During commit, the B+Tree is accessed under holdoff (see holdoff catalog below), so this check is a no-op.

### Category 3: Revision Chain Walk

| Location | File | Method | Line | Why Safe |
|----------|------|--------|------|----------|
| Between chunk steps | `RevisionWalker.cs` | `Step()` | ~35 | Read-only traversal between chunks |

```csharp
// In RevisionWalker.Step()
public bool Step(int stepCount, bool loop, out bool hasLooped,
    ref UnitOfWorkContext ctx)
{
    hasLooped = false;
    for (int i = 0; i < stepCount; i++)
    {
        if (_nextChunkId == 0 && !loop) return false;
        // ... chunk traversal ...
        _curChunkId = nextChunkId;
        var chunkSpan = _accessor.GetChunkAsSpan(nextChunkId, false);
        _nextChunkId = ref chunkSpan.Cast<byte, int>()[0];
        _elements = chunkSpan.Slice(sizeof(int)).Cast<byte, CompRevStorageElement>()
            .Slice(0, ComponentRevisionManager.CompRevCountInNext);

        ctx.ThrowIfCancelled();  // ← YIELD POINT: after chunk boundary crossing
    }
    return true;
}
```

**Note:** Revision chain walks that happen during commit (inside `CommitComponent`) are under holdoff, so this check is a no-op in that context. It only fires during explicit read operations.

### Category 4: Page I/O Slow Path (Future)

| Location | File | Method | Line | Why Safe |
|----------|------|--------|------|----------|
| After page cache miss | `ChunkAccessor.cs` | `LoadAndGet()` | ~317 | I/O complete, data consistent |

This is a **future** yield point. The `ChunkAccessor` hot path (MRU + SIMD search) must NOT yield — the cost of `ThrowIfCancelled()` (~10-25ns) is significant relative to the ~5-15ns cache hit path. Only the slow path (page miss + disk I/O) should check.

**Implementation note:** Threading `ref UnitOfWorkContext` through `ChunkAccessor` is invasive. This yield point is deferred to a later iteration unless needed for correctness.

## Holdoff Region Catalog

### Region 1: Transaction Commit Loop (CRITICAL)

| Location | File | Method | Lines | Why Holdoff |
|----------|------|--------|-------|-------------|
| Entire commit loop | `Transaction.cs` | `Commit()` | 1603-1672 | Prevents partial commit (atomicity) |

```csharp
// See Category 1 above — holdoff wraps the entire loop
using var holdoff = ctx.EnterHoldoff();
// ... commit loop ...
```

This is the **most important holdoff region**. It ensures that once `Commit()` starts modifying data, the operation runs to completion regardless of deadline expiry.

### Region 2: B+Tree Node Split

| Location | File | Method | Lines | Why Holdoff |
|----------|------|--------|-------|-------------|
| Internal node split processing | `BTree.NodeWrapper.cs` | `InsertInternal()` | ~249-375 | Parent-child consistency during split |
| Leaf node split | `BTree.NodeWrapper.cs` | `InsertLeaf()` | ~148-196 | Node data movement must be atomic |
| Split operation | `L16BTree.cs` | `SplitRight()` | ~648-690 | Left/right node data must be consistent |
| Split operation | `L32BTree.cs` | `SplitRight()` | ~648 | Same pattern |
| Split operation | `L64BTree.cs` | `SplitRight()` | ~648 | Same pattern |
| Split operation | `String64BTree.cs` | `SplitRight()` | ~667 | Same pattern |

**Implementation approach:** The B+Tree insert methods already hold exclusive locks during split operations. The holdoff is *additional* protection: it ensures that even if the lock was acquired with a tight deadline, the split completes before checking cancellation.

```csharp
// In BTree.NodeWrapper.InsertInternal (conceptual)
if (rightChild is KeyValueItem middle)
{
    using var holdoff = ctx.EnterHoldoff();  // ← HOLDOFF: split result processing
    if (!GetIsFull(ref accessor))
    {
        Insert(index, middle, ref accessor);
    }
    else
    {
        var rightNode = SplitRight(NodeStates.None, ref accessor);
        // ... data movement ...
    }
}
```

**Note on nesting:** If B+Tree insert is called from within `Commit()`, the commit holdoff is already active (count = 1). The split holdoff increments to 2. This is safe — holdoff is counter-based.

### Region 3: Revision Chain Append

| Location | File | Method | Lines | Why Holdoff |
|----------|------|--------|-------|-------------|
| Add revision element | `ComponentRevisionManager.cs` | `AddCompRev()` | ~92-96 | Chain must remain valid |

```csharp
// In ComponentRevisionManager (conceptual)
internal static void AddCompRev(ref ChunkAccessor accessor, int firstChunkId,
    ref CompRevStorageHeader header, CompRevStorageElement element,
    ref UnitOfWorkContext ctx)
{
    using var holdoff = ctx.EnterHoldoff();  // ← HOLDOFF: chain append
    // ... append revision to chain (may allocate new chunk) ...
}
```

**Note:** This is always called from within `CommitComponent()`, which is already under the commit holdoff. The nested holdoff provides defense-in-depth.

### Region 4: Index Update Operations

| Location | File | Method | Lines | Why Holdoff |
|----------|------|--------|-------|-------------|
| Index remove + add | `Transaction.cs` | `UpdateIndices()` | ~1320-1385 | Old entry removed, new must be inserted |

The index update removes the old value and inserts the new one. If cancellation occurred between remove and insert, the index would be inconsistent. This is always called from within `CommitComponent()`, already under commit holdoff.

**No additional holdoff needed** — the commit holdoff covers this.

### Region 5: Rollback Operations

| Location | File | Method | Lines | Why Holdoff |
|----------|------|--------|-------|-------------|
| Entire rollback | `Transaction.cs` | `Rollback()` | varies | Cleanup must complete |

```csharp
public bool Rollback(ref UnitOfWorkContext ctx)
{
    ctx.ThrowIfCancelled();  // Optional: can skip for rollback
    using var holdoff = ctx.EnterHoldoff();  // Rollback must complete
    // ... existing rollback logic ...
}
```

**Design note:** Rollback should arguably NOT check cancellation at all (cleanup must always complete). The holdoff ensures this, and the yield point at entry is optional. If skipped, rollback always runs to completion regardless of deadline state.

## Yield Point Frequency Guidelines

Not every loop iteration needs a check. The cost of `ThrowIfCancelled()` is ~10-25ns (branch + possible Stopwatch read). Guidelines:

| Context | Frequency | Rationale |
|---------|-----------|-----------|
| B+Tree internal traversal | Every node boundary | Typically 3-5 levels deep; ~4 checks per lookup |
| Revision chain walk | Every chunk boundary | Chains are typically 1-3 chunks; ~2 checks per walk |
| Commit entry | Once per commit | One check before starting modifications |
| Page I/O slow path | After each miss | Rare (cache hit rate > 95%); ~1 check per 100+ accesses |

**Do NOT add yield points:**
- Inside `ChunkAccessor` hot path (MRU/SIMD search)
- Inside CAS spin loops in `AccessControl` / `AccessControlSmall`
- Inside `Deadline.IsExpired` / `WaitContext.ShouldStop` (circular dependency)
- Inside `ThrowHelper` throw paths (already failing)

## Error Propagation

| Condition | Exception | Catch Pattern |
|-----------|-----------|---------------|
| Deadline expired | `TyphonTimeoutException` | `catch (TyphonTimeoutException)` |
| Token cancelled | `OperationCanceledException` | `catch (OperationCanceledException)` |
| Lock timeout (existing) | `LockTimeoutException` | `catch (LockTimeoutException)` (subclass of `TyphonTimeoutException`) |

The caller (typically `Transaction.Commit()` wrapper in #45) can catch `TyphonTimeoutException` and re-throw as `TransactionTimeoutException` with full context (transaction ID, elapsed time).

## Files to Modify

| File | Changes |
|------|---------|
| `src/Typhon.Engine/Data/Transaction/Transaction.cs` | Yield point at Commit/Rollback entry, holdoff around commit loop |
| `src/Typhon.Engine/Data/Index/BTree.cs` | Yield point in `FindLeaf()` traversal loop |
| `src/Typhon.Engine/Data/Index/BTree.NodeWrapper.cs` | Holdoff around split processing in `InsertInternal()`, `InsertLeaf()` |
| `src/Typhon.Engine/Data/Revision/RevisionWalker.cs` | Yield point in `Step()` loop |
| `src/Typhon.Engine/Data/Revision/ComponentRevisionManager.cs` | Holdoff in `AddCompRev()` |

### New Files

| File | Contents |
|------|----------|
| `test/Typhon.Engine.Tests/Concurrency/YieldPointTests.cs` | Integration tests |

## Testing Strategy

### Integration Tests (~10 tests)

**Commit path:**
- Short deadline + empty transaction → no throw (fast path returns true)
- Short deadline + populated transaction → yield point at start throws `TyphonTimeoutException`
- Expired deadline during holdoff → no throw; throws after holdoff exits (on next yield point)
- Cancelled token at commit entry → `OperationCanceledException`

**B+Tree path:**
- Short deadline during B+Tree lookup with deep tree → throws at node boundary
- Long deadline during B+Tree lookup → succeeds normally

**Holdoff:**
- Nested holdoff (commit + split) → cancellation deferred until outermost holdoff exits
- Cancel during node split → split completes, exception fires after holdoff
- Cancel during revision append → append completes, exception fires after holdoff

**Backward compatibility:**
- All existing tests pass unchanged (they use `Commit()` without context, which uses `UnitOfWorkContext.None` with infinite deadline — yield point never fires)

## Acceptance Criteria (from Issue #44)

- [ ] Yield points inserted at all identified safe locations
- [ ] Holdoff regions protect all identified critical sections
- [ ] Cancellation during holdoff is deferred, not lost
- [ ] Test: cancel during B+Tree traversal → clean abort, no corruption
- [ ] Test: cancel during page write → write completes, then cancellation fires
