# Migration Plan — Phased Implementation Strategy

**Parent:** [README.md](./README.md)

---

## Guiding Principles

1. **Incremental**: Each phase produces a working system. No big-bang rewrite.
2. **Testable**: Each phase has clear acceptance criteria verified by existing + new tests.
3. **Reversible**: If a phase reveals unexpected issues, we can stop and the system still works.
4. **Performance-validated**: Benchmark after each phase to detect regressions early.

---

## Phase Overview

```
Phase 0: Foundation          (NEW code, no existing changes)
  └→ EpochManager, EpochThreadRegistry, EpochGuard

Phase 1: Page Cache Dual-Mode  (MODIFY PagedMMF to support both modes)
  └→ Add AccessEpoch to PageInfo, epoch-aware eviction predicate
  └→ Keep ref-counting operational (dual-mode)

Phase 2: ChunkAccessor v2     (NEW ChunkAccessor alongside old one)
  └→ Build epoch-based ChunkAccessor
  └→ Test with existing operations

Phase 3: Migration            (SWITCH callers from old to new)
  └→ Migrate B+Tree, Transaction, ComponentTable, segments
  └→ Remove old ChunkAccessor, ChunkHandle, etc.

Phase 4: Cleanup              (REMOVE old code paths)
  └→ Remove ref-counting from PagedMMF
  └→ Simplify PageAccessor
  └→ Remove dual-mode support
```

---

## Phase 0: Epoch Foundation

### Deliverables

1. `EpochManager` class — owns GlobalEpoch, thread registry, MinActiveEpoch cache
2. `EpochThreadRegistry` — fixed-size array of pinned epochs
3. `EpochGuard` ref struct — scope enter/exit wrapper

### Implementation Steps

```
Step 0.1: Create EpochManager
  - GlobalEpoch (long, starts at 1)
  - Increment method (Interlocked.Increment)
  - Read method (plain read, x64 atomic)

Step 0.2: Create EpochThreadRegistry
  - Fixed long[] array (256 slots)
  - Thread registration via [ThreadStatic] slot index
  - Pin/Unpin methods
  - MinActiveEpoch scan method

Step 0.3: Create EpochGuard
  - ref struct implementing IDisposable
  - Enter: read GlobalEpoch, store in registry, increment nesting depth
  - Exit/Dispose: decrement depth, if 0 clear registry + advance epoch

Step 0.4: Unit tests
  - Single-thread: enter/exit, nesting, epoch advancement
  - Multi-thread: concurrent scopes, MinActiveEpoch correctness
  - Stress: rapid enter/exit, many threads
```

### Acceptance Criteria

- EpochGuard.Enter/Exit takes <5ns on hot path
- MinActiveEpoch scan takes <200ns for 256 slots
- No memory leaks or thread-safety issues under stress

### Risk: None

This phase adds new code with no modifications to existing code. Zero regression risk.

---

## Phase 1: Page Cache Dual-Mode

### Deliverables

1. `PageInfo.AccessEpoch` field added
2. Epoch-aware eviction predicate (alongside existing ref-count predicate)
3. `PagedMMF.RequestPageEpoch()` method — epoch-tagged access without ref counting

### Implementation Steps

```
Step 1.1: Add AccessEpoch to PageInfo
  - New long field, initialized to 0
  - Updated on every page access (both old and new paths)

Step 1.2: Add epoch-aware eviction to TryAcquire
  - If AccessEpoch < MinActiveEpoch AND refcount == 0: evictable
  - This is a SUPERSET of the current condition (both must be satisfied)
  - Ensures correctness during migration (old code still ref-counts)

Step 1.3: Add RequestPageEpoch method
  - Same as RequestPage but skips ref-counting
  - Tags page with AccessEpoch
  - Returns lightweight PageAccessor (no dispose needed for shared)

Step 1.4: Tests
  - Verify epoch-tagged pages are protected from eviction
  - Verify epoch-tagged pages become evictable after scope exit
  - Verify existing ref-counted path still works (regression test)
  - Mixed test: some pages ref-counted, some epoch-tagged
```

### Acceptance Criteria

- All existing tests pass (no regression)
- New epoch-tagged access path works correctly
- Eviction correctly considers both epoch AND ref-count (dual mode)

### Risk: Low

Changes are additive. Existing code paths are unchanged. The dual-mode eviction is strictly more conservative (requires BOTH conditions).

---

## Phase 2: ChunkAccessor v2

### Deliverables

1. New `ChunkAccessor` implementation using epoch-based access
2. Simplified memory layout (~326 bytes vs ~1KB)
3. No PinCounter, PromoteCounter, ChunkHandle
4. All existing GetChunk/GetChunkReadOnly APIs preserved (same signatures)

### Implementation Steps

```
Step 2.1: Create new ChunkAccessor struct
  - SOA layout: _pageIndices, _baseAddresses, _memPageIndices, _hitCounts, _dirtyFlags
  - GetChunk<T>, GetChunkReadOnly<T>, GetChunkAddress — same SIMD hot path
  - Simplified FindLRUSlot (no pin/promote checks)
  - Simplified EvictSlot (no PageAccessor.Dispose)
  - Simplified LoadIntoSlot (uses RequestPageEpoch)

Step 2.2: Simplified Dispose
  - Only flushes dirty pages to ChangeSet
  - No ref-count release, no demotion

Step 2.3: Remove ChunkHandle / ChunkHandleUnsafe
  - These types are not needed — GetChunk returns epoch-safe references

Step 2.4: Comprehensive tests
  - Port all existing ChunkAccessor tests to v2
  - Remove pin-related tests (PinCounter, AllSlotsPinned)
  - Add epoch-integration tests (verify references valid within scope)
  - Stress tests: concurrent access with epoch scopes
  - Performance benchmarks: compare v1 vs v2
```

### Acceptance Criteria

- New ChunkAccessor passes all ported tests
- GetChunk returns references valid for entire epoch scope
- No "all slots pinned" failure mode
- Performance equal or better than v1

### Risk: Medium

New implementation must handle all edge cases (segment growth, dirty tracking, root page offsets). Thorough testing is essential.

---

## Phase 3: Caller Migration

### Deliverables

1. All callers switched from old to new ChunkAccessor
2. Exclusive access migrated to page-level latch
3. EpochGuard integrated into Transaction lifecycle

### Implementation Steps

```
Step 3.1: Integrate EpochGuard into Transaction
  - Transaction constructor: EpochGuard.Enter()
  - Transaction.Dispose(): EpochGuard.Dispose()
  - All operations within transaction are epoch-protected

Step 3.2: Migrate B+Tree operations
  - Replace GetChunkHandle with GetChunk/GetChunkReadOnly
  - Replace TryPromoteChunk/DemoteChunk with AcquireExclusive/ReleaseExclusive
  - Remove using statements for handles

Step 3.3: Migrate ComponentTable / Transaction CRUD
  - Replace accessor creation with epoch-based accessors
  - Simplify ComponentInfo (no struct copy concerns)
  - Simplify DisposeAccessors (just flush dirty)

Step 3.4: Migrate segment operations
  - Replace GetPageSharedAccessor with epoch-tagged access where applicable
  - Keep exclusive access for bitmap modifications (via latch)

Step 3.5: Integration tests
  - Full CRUD test suite with epoch-based accessors
  - Concurrent transaction stress tests
  - B+Tree operation tests with epoch-based exclusive access
```

### Acceptance Criteria

- All existing tests pass with new implementation
- No ref-counting usage remains in main code paths
- Exclusive access works via page-level latch

### Risk: High

This is the most risk-prone phase — changing the callers that exercise every code path. Mitigation: migrate one caller at a time, run full test suite after each migration.

### Suggested Migration Order

```
1. Revision chain walkers (read-only, simplest change)
2. B+Tree lookups (read-only traversal)
3. Transaction reads (CRUD read path)
4. Transaction writes (CRUD write path)
5. B+Tree inserts without splits
6. B+Tree inserts with splits (exclusive access)
7. Segment growth operations
8. Bitmap L3 operations
```

---

## Phase 4: Cleanup

### Deliverables

1. Remove `ConcurrentSharedCounter` from `PageInfo`
2. Remove old `PageAccessor` disposal path for shared access
3. Remove old `ChunkAccessor` (v1) if still present
4. Remove `ChunkHandle`, `ChunkHandleUnsafe` types
5. Simplify `PageState` enum
6. Remove dual-mode eviction (epoch-only)
7. Update documentation

### Implementation Steps

```
Step 4.1: Remove ref-counting from PagedMMF
  - Remove ConcurrentSharedCounter field
  - Remove TransitionPageFromAccessToIdle (shared path)
  - Remove shared-path state transitions
  - Simplify RequestPage (remove dual-mode)

Step 4.2: Simplify PageAccessor
  - Remove Dispose for shared path (or make it no-op)
  - Keep Dispose for exclusive path (releases latch)

Step 4.3: Delete old types
  - Delete ChunkHandle, ChunkHandleUnsafe
  - Delete old ChunkAccessor if replaced
  - Delete StateSnapshot validation (pin/promote checking)

Step 4.4: Update documentation
  - Update claude/overview/03-storage.md
  - Update claude/reference/StackChunkAccessor.md (mark superseded)
  - Create new reference doc for epoch-based access
  - Update CLAUDE.md architecture section

Step 4.5: Final benchmarks
  - Compare full benchmark suite: before vs after
  - Validate no performance regression
  - Document any improvements
```

### Risk: Low

By this phase, all code is already working with the new model. Cleanup removes dead code.

---

## Timeline Estimate

```
Phase 0 (Foundation):       Small (new code, well-defined)
Phase 1 (Page Cache):       Small (additive changes)
Phase 2 (ChunkAccessor v2): Medium (new impl, thorough testing)
Phase 3 (Migration):        Large (many callers, integration testing)
Phase 4 (Cleanup):          Small (remove dead code)
```

---

## Rollback Strategy

### Phase 0-1: Trivial Rollback

New code can be deleted. No existing code was modified.

### Phase 2: Moderate Rollback

New ChunkAccessor exists alongside old. Revert by switching back to old accessor.

### Phase 3: Incremental Rollback

Each caller migration is independent. Revert individual callers by switching back to old APIs. The dual-mode page cache supports both old and new callers simultaneously.

### Phase 4: No Rollback Needed

Only entered after everything is verified working. Dead code removal is trivially reversible via git.

---

## Open Questions

1. **Epoch advancement frequency**: Should we advance on every outermost scope exit, or batch (every N exits)? Batching reduces CAS contention but delays reclamation.

2. **Thread registry sizing**: 256 slots is generous. Could use a dynamic growing array, but fixed is simpler and sufficient.

3. **ChangeSet integration**: The current ChangeSet takes PageAccessor instances. Need to add a `AddByMemPageIndex` method or similar.

4. **Telemetry**: Need new metrics for epoch system (GlobalEpoch value, MinActiveEpoch, epoch advancement rate, scope duration distribution).

5. **Deadline integration**: How does the epoch system interact with the new deadline/timeout infrastructure (feature/36-error-foundation)? Epoch operations should respect deadlines.

---

**Back to:** [README.md](./README.md)
