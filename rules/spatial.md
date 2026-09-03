# Spatial Index Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-04-10 |
| Domain | Spatial R-Tree, Queries, Trigger Volumes, Interest Management, Spatial Tiers (Clusters, Dormancy, Checkerboard, Migration) |

> Invariants that ensure spatial query correctness, tree structural integrity,
> consistent interaction with the ECS lifecycle, and spatial-tier cluster management
> (cell mapping, tier indexing, dormancy, checkerboard dispatch, migration dirty bits).

---

## Module: R-Tree Structure

### ST-01: Node MBR correctness `[fatal][silent]`
  invariant ∀ leaf L: L.NodeMBR == union(L.entries[0..count-1].coords)
  invariant ∀ internal I: I.NodeMBR == union(I.children[0..count-1].NodeMBR)
  invariant ∀ internal I, ∀ i: I.entries[i].coords == I.children[i].NodeMBR
    (the second invariant is only reachable through this one — RefitInternalMBR unions I's OWN entry
     array, so a caller that refits without first refreshing the changed entry produces a too-tight I)
  scope: every caller that refits an internal node — SpatialRTree.PropagateSplit (both the current-level
    absorb and the ancestor loop), RefitAncestors, RefitAncestorsBottomUp, WriteInternalEntry, BulkLoad,
    RemoveEmptyLeaf, SplitInternalNode
    NOT the SpatialNodeHelper primitives (RefitLeafMBR / RefitInternalMBR / ExpandLeafMBR): all three are
    correct in isolation, and scoping the rule to them is precisely what let #588 pass a scope-driven
    review — the invariant lives or dies in the callers
  on_violation:
    MBR too tight → queries miss entities (false negatives, silent data loss for game logic)
    MBR too loose → unnecessary subtree visits (performance only)
  detection: TreeValidator.ValidateInternalEntryFreshness. Recomputing a node's MBR from its own entries
    (ValidateMBRTightness alone) CANNOT see staleness — it compares a value against itself. Violations are
    also transient: the next insert descending the same path refreshes the entry and heals it, so an
    end-of-workload check comes back clean. Validate at checkpoints DURING mutation, never only after.

### ST-02: Union category mask — never under-represents `[perf]`
  invariant ∀ leaf L: L.UnionCategoryMask ⊇ OR(L.entries[0..count-1].CategoryMask)
  invariant ∀ internal I: I.UnionCategoryMask ⊇ OR(I.children[0..count-1].UnionCategoryMask)
  never UnionCategoryMask missing a bit that exists in a descendant entry
  scope: SpatialNodeHelper.RefitLeafMBR, SpatialRTree.RefitInternalUnionMask, RemoveEmptyLeaf
  on_violation: subtree pruning skips entities matching the query mask → false negatives
    (transient over-representation after remove is acceptable — causes extra visits, not missed results)

### ST-03: Parent pointer consistency `[fatal][silent]`
  invariant ∀ node N where N ≠ root: N.ParentChunkId points to an internal node containing N as a child
  invariant root.ParentChunkId == 0
  scope: SpatialRTree.Insert, Split, Remove, CreateNewRoot
  on_violation: RefitAncestorsBottomUp follows wrong chain; MBRs diverge from actual data;
    tree structure silently degrades over time

### ST-04: Entity count accuracy `[silent]`
  invariant _entityCount == sum(∀ leaf L: L.Count)
  requires: Interlocked operations on _entityCount
  scope: SpatialRTree.Insert, InsertWithSplit, Remove
  on_violation: kNN initial radius estimate degrades; TreeValidator reports inconsistency;
    persisted metadata disagrees with tree content

### ST-05: Back-pointer consistency `[fatal]`
  invariant ∀ payload P in tree: BackPointer[P.payloadId] == (P.leafChunkId, P.slotIndex, treeSelector)
  invariant 🔴 the back-pointer array is the SOLE store of a payload's location, not a repair channel. EVERY path
            that places or moves an entry writes it — Insert for a new entry, ScatterLeafEntries for a split,
            Remove for the swap-with-last and the retirement. Miss the Insert and the array is silently
            incomplete: a caller must then merge it with Insert's return value, and the moment it keeps its own
            copy that copy goes stale the first time ANOTHER payload's split or removal relocates this one. The
            failure that follows is not a missed lookup — TryUpdateLeafEntryInPlace refuses on the identity
            check, which is the check working, and the escape path underneath then removes at the stale
            location, which is a live entry belonging to somebody else. (#872 step 9; found by measurement, not
            by review.)
  invariant the back-pointer KEY is stable for the payload's lifetime — invariant under MVCC revision minting,
            cluster migration, and leaf-slot swap. A key that can be re-minted while the payload is alive is
            not a valid back-pointer key, however convenient it is to reach.
  note 🔴 the key is a PAYLOAD id, not necessarily an entity id. SpatialRTree is generic over what it indexes:
       the entity-level trees key on EntityId, the per-cell cluster trees of #872 step 9 key on a CLUSTER chunk
       id. The field was called EntityId until step 9 renamed it to PayloadId, which read as a type guarantee
       it never made.
  scope: SpatialMaintainer.InsertSpatial / UpdateSpatialCore / RemoveFromSpatial,
         SpatialRTree.Insert (the new-entry write), ScatterLeafEntries, Remove,
         CellClusterTree.Add / UpdateAt / RemoveAt (which read the array rather than holding a handle)
  on_violation: the lookup misses, the update falls through to a fresh Insert, the prior leaf entry is orphaned →
    duplicate EntityIds in the tree (TreeValidator "R5 violation"); or update/remove targets the wrong leaf slot
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously keyed the back-pointer on `componentChunkId` — the key the
    implementation uses and the direct cause of confirmed bug #548. MVCC re-mints the content chunk id per revision, so
    for StorageMode.Versioned the lookup misses and every update double-inserts; SingleVersion is unaffected because
    its chunk id is stable. The design series specified `entityId` all along (03-tree-operations.md invariant B1,
    05-ecs-integration.md ReadBackPointer(entityId)). The rule had diverged from its own design and therefore RATIFIED
    the defect — which is why review never flagged the implementation. The key-stability clause is the missing half:
    re-keying alone states the what without the why, and the next storage mode reintroduces it.

### ST-07: Escape-bound in-place update `[fatal][silent]`
  invariant TryUpdateLeafEntryInPlace writes ONLY when all three hold: the target is a leaf, the slot is live,
    and the entry's payload id equals the caller's. The identity check is not defensive coding — without it a
    stale handle writes cluster X's bounds into cluster Y's slot, breaking CA-01 in both directions with no
    exception raised anywhere near the cause.
  invariant it writes only when the new bounds are CONTAINED by the leaf's current MBR on every axis. That is
    what makes skipping the refit sound: an entry that stays inside its leaf cannot change the union's outer
    edge outward, so no ancestor can become too tight.
  invariant 🔴 the handle comes from PayloadBackPointers, never from a caller-held copy — see ST-05. The escape
    path removes at the handle it was given, so a handle that has gone stale removes a stranger.
  invariant 🔴 ST-01's leaf equality is SUSPENDED between an in-place write and the end-of-pass refit, and only
    there. Not refitting is the entire saving, and its cost is that the leaf MBR becomes a strict SUPERSET of
    the union of its entries. Too-loose is ST-01's performance-only direction, so the window is safe — but it
    must not outlive the exclusive window, because a query running against a loose MBR is merely slower while
    a REBUILD or a validator run against one reports a violation that is real.
  post after the pass closes: ∀ leaf touched by an in-place update, L.NodeMBR == union(L.entries) once more,
    i.e. ST-01 holds unconditionally outside the window.
  scope: SpatialRTree.TryUpdateLeafEntryInPlace, CellClusterTree.UpdateAt
  verified: CellClusterTreeDifferentialTests (EscapeBoundUpdate_KeepsTheTreeAgreeingWithTheScan carries the
    attribute; HandlesStayValid_AcrossUpdatesAndRemovals covers the handle half)
  on_violation:
    wrote through a stale handle → two clusters hold each other's bounds → CA-01 fails silently, SQ-01 false
      negatives for both, and TreeValidator passes throughout because the TREE is structurally perfect —
      only the addresses held outside it are wrong
    refit skipped and never made up → ST-01 equality permanently false; queries stay correct, validators do not
  requires: ST-05 (the handle is where the array says it is)

### ST-06: OLC version validity `[fatal]`
  invariant ∀ node: OlcVersion ≥ 4 (version ≥ 1, lock=0, obsolete=0) when not write-locked
  never OlcVersion == 0 for an allocated, non-locked node
  scope: SpatialRTree.AllocNode, OlcLatch
  on_violation: queries see version 0 → restart loop; if node permanently stuck at 0 → infinite retry

---

## Module: Queries

### SQ-01: Query completeness — no false negatives `[fatal]`
  invariant ∀ query Q, ∀ entity E:
    E geometrically matches Q ∧ (Q.categoryMask == 0 ∨ (E.CategoryMask & Q.categoryMask) == Q.categoryMask)
    → E ∈ result set
  scope: SpatialRTree.Query.cs (all enumerators), CountInAABB
  on_violation: spatial query misses entities — game logic sees incomplete world state
  requires: ST-01 (MBR correctness), ST-02 (union mask not under-representing)

### SQ-02: Category mask semantics — AND-conjunctive `[fatal]`
  invariant categoryMask == 0 → no category filtering (all entities match)
  invariant categoryMask ≠ 0 → entry matches iff (entry.CategoryMask & categoryMask) == categoryMask
  never (entry.CategoryMask & categoryMask) != 0 treated as a match (that would be OR-disjunctive)
  scope: all query enumerators, CountInAABB leaf scan
  on_violation: queries return wrong entity set — wrong enemies targeted, wrong zones triggered

### SQ-03: Count query consistency `[fatal]`
  invariant CountInAABB(region, mask) == |{ E : E ∈ QueryAABB(region, mask) }|
  scope: SpatialRTree.CountInAABB, AABBQueryEnumerator
  on_violation: count disagrees with materialized query — game logic makes wrong density decisions

### SQ-04: Subtree counting shortcut correctness `[fatal]`
  invariant fully-contained flag propagation: node fully contained → all descendants fully contained
  invariant fullyContained ∧ categoryMask == 0 → count += nodeCount (no per-entry work)
  invariant fullyContained ∧ categoryMask ≠ 0 → skip overlap tests, still check per-entry category mask
  never fullyContained ∧ categoryMask ≠ 0 → count += nodeCount (would over-count)
  scope: SpatialRTree.CountInAABB
  on_violation: count is wrong — over-count if category check skipped, under-count if entries missed

### SQ-05: Traversal buffer safety `[silent]`
  invariant stackTop < 256 for all DFS-based queries
  invariant RayEnumerator never drops a child that hits within maxDist while below MaxRayHeapCapacity
  scope: AABBQueryEnumerator, OccupantQueryEnumerator, FrustumEnumerator, CountInAABB, RayEnumerator
  on_violation: children silently dropped → incomplete results with no error indication.
    The overflow branch records SpatialRTreeDiagnostics.RecordDfsStackOverflow — an always-on counter, deliberately
    non-throwing because an optimistic read latch is held. Corrected 2026-07-27: this rule previously said
    "Debug.Fail fires in debug builds only"; there are no Debug.Fail calls on this path, and the drop is observable
    in every build via that counter.
  ray heap (added 2026-07-31, #589): the ray priority queue is NOT a fixed buffer. Its 64-entry inline array is a
    fast path that spills to pooled arrays on demand, so a dense scene stays complete; only MaxRayHeapCapacity
    (a corrupt/cyclic-tree backstop, not a scene limit) drops children, and it records like the DFS sites.
    Ordinary growth increments RayHeapSpillCount — a perf signal, not a correctness one.
    The rule previously scoped only the four DFS enumerators, so nothing covered the ray path at all: it folded
    `_heapSize < 64` into its push condition with no else and no counter, and a dense scene silently lost ~80% of
    its hits. Frontier size is NOT bounded by tree depth — a ray whose subtrees share an entry distance holds them
    all pending at once — so no depth-derived constant may be used to bound it.
  detection: a scene that partitions ALONG the ray axis cannot reach this. Front-to-back popping then drains
    depth-first (siblings get well-separated entry distances) and the frontier stays at 5-15 nodes, which is why
    the pre-existing 200-entity ray test never filled the heap. Partition PERPENDICULAR to the ray so every node
    shares an entry distance and siblings pile up unconsumed.

---

## Module: Fat AABB Updates

### SF-01: Fat AABB containment — fast vs slow path `[perf]`
  invariant E.tightAABB ⊂ E.fatAABB → no tree mutation (fast path, ~25ns)
  invariant E.tightAABB ⊄ E.fatAABB → remove + reinsert with new fat AABB (slow path, ~500ns)
  scope: SpatialMaintainer.UpdateSpatial
  on_violation: entity position diverges from tree position; queries return stale spatial data

### SF-02: Static tree skip `[perf]`
  never tick fence / batch update processing visits static tree entities
  scope: SpatialMaintainer, SpatialIndexState
  on_violation: wasted CPU on immutable data; potential accidental mutation of bulk-loaded tree

---

## Module: Trigger Volumes

### TV-01: Event completeness `[fatal]`
  invariant ∀ entity transition (outside→inside) between consecutive evaluations → exactly one Enter event
  invariant ∀ entity transition (inside→outside) between consecutive evaluations → exactly one Leave event
  never an entity inside at both evaluations produces Enter or Leave (only Stay if subscribed)
  scope: SpatialTriggerSystem.EvaluateRegion
  on_violation: game logic misses zone transitions or fires duplicate events

### TV-02: Frequency contract `[perf]`
  invariant region with EvaluationFrequency = N evaluated at most once every N ticks
  scope: SpatialTriggerSystem.Evaluate
  on_violation: region evaluated too often (wasted CPU) or not often enough (missed transitions)

---

## Module: Interest Management

### IM-01: No missed changes `[fatal]`
  invariant ∀ entity E mutated at tick T, ∀ observer O where O.LastConsumedTick < T ≤ currentTick:
    E within O.InterestRegion ∧ (E.CategoryMask & O.CategoryMask) matches
    → E ∈ O.ChangeBuffer
  scope: SpatialInterestSystem.GetSpatialChanges
  on_violation: observer misses entity update → client sees stale state → desync

### IM-02: Ring buffer safety `[fatal]`
  invariant currentTick - observer.LastConsumedTick > RingSize → observer flagged for full sync
  never stale (recycled) bitmaps used for dirty accumulation
  scope: SpatialInterestSystem.GetSpatialChanges, DirtyBitmapRing
  on_violation: observer uses recycled bitmap data → phantom changes or missed changes

### IM-03: SV-only scope `[fatal]`
  invariant interest management dirty tracking only applies to SingleVersion ComponentTables
  never Versioned tables participate in ring buffer system
  scope: SpatialInterestSystem
  on_violation: DirtyBitmap infrastructure doesn't exist for Versioned → crash or undefined behavior

---

## Module: Cluster Spatial AABBs (Issue #230)

### CA-01: Per-cluster AABB containment `[fatal][silent]`
  invariant 🔴 FRAME (#872 step 9, decision C15): A is stored CELL-RELATIVE — offsets from the world-space minimum
    corner of C's cell, which SpatialGrid.CellOrigin derives from ClusterCellMap[C.chunkId]. E.spatialAABB is
    world-space. The containment below therefore only holds once both sides are in the SAME frame, and the rule is
    read literally false for every cluster in a cell whose origin is non-zero if that is forgotten. C13
    (cluster→cell exclusivity) is what makes the origin unique per cluster and hence what makes the frame
    well-defined at all — CA-01 depends on C13 and not merely alongside it.
  invariant ∀ active cluster C with ClusterAabbs[C.chunkId] = A:
    toCellRelative(union(∀ occupied entity E in C: E.spatialAABB), origin(C)) ⊆ A
    (degenerate entities with NaN/Inf bounds are excluded from the union)
    conversion rounds AWAY from the entity — min down, max up (ClusterSpatialAabb.ToCellRelativeMin/Max). The
    narrowing to f32 is where the error is: round-to-nearest can place a bound INSIDE the entity it must contain,
    which is this rule's own silent failure mode.
  invariant 🔴 the QUERY side shares the frame or the rule is unobservable: AabbClusterEnumerator.SetCellQueryFrame
    converts the query box into the cell's frame with the OPPOSITE-signed rounding (outward on both sides). A box
    narrowed by one ULP there drops a cluster grazing its edge — an SQ-01 false negative that no containment check
    on the storage side can see.
  invariant RecomputeClusterAabb scans all occupied slots via TZCNT loop
    over the 64-bit occupancy word and calls ReadAndValidateBoundsFromPtr per slot
  post after the refresh pass: ∀ cluster C selected by ClusterProcessBitmap:
    ClusterAabbs[C.chunkId] ⊇ union of C's live entity AABBs, and == the exact union when a shrink was pending
    (corrected 2026-07-28: the pass no longer reads dirtyBits — it is driven by ClusterProcessBitmap +
     ClusterShrinkPendingAxes; and on the common grow path the CAS-grown superset is kept, never re-tightened,
     so "exact union" holds only when shrinkMask != 0)
  invariant 🔴 ClusterAabbs[chunkId] has EXACTLY ONE writer class at any instant:
    (a) CAS-grow from ClusterRef.WriteSpatial / MaybeGrowAndFlagShrink, during system dispatch
    (b) blind full-struct store from the AabbRefresh fence phase
    The tick barrier separating dispatch from the fence is what makes (b) safe, and it is LOAD-BEARING. Relax it and
    the fence store silently discards concurrent grows → AABB too tight → this rule's own containment fails.
  scope: ArchetypeClusterState.RecomputeClusterAabb, RecomputeDirtyClusterAabbs, RecomputeDirtyClusterAabbsSlice
         (the parallel path that performs the store), RebuildClusterAabbs,
         ClusterRef.WriteSpatial / MaybeGrowAndFlagShrink (the concurrent writer class),
         ClusterRef.TryGetCellOrigin, ClusterSpatialAabb.ToCellRelativeMin, ClusterSpatialAabb.ToCellRelativeMax,
         SpatialGrid.CellOrigin (the frame the containment is expressed in — C15),
         AabbClusterEnumerator.SetCellQueryFrame (the query half; without it the invariant is unobservable)
  note the store is a blind `stored = fresh`, not a grow-merge (issue #573). Safe only under the barrier above; any
       overlap scheme must make it a union first. Neither ArchetypeClusterState nor TyphonRuntime contains a single
       Volatile, so the cluster-side read path is not a valid lock-free protocol on a weak memory model regardless of
       load order — arm64 is a supported target.
  on_violation:
    AABB too tight → per-cell cluster spatial queries miss entities (false negatives, silent)
    AABB too loose → extra overlap tests (performance only)
  requires: occupancy word accurately reflects live slots

### CA-02: The per-cell index tracks ClusterAabbs, and equality is not the test for it `[fatal][silent]`
  invariant the broadphase prunes on the bound held by the PER-CELL structure — CellSpatialIndex.MinX[] et al, or
    the leaf entry in a promoted CellClusterTree — never on ClusterAabbs. So:
    ∀ active cluster C, after the AabbRefresh phase and its promoted-cell drain:
      indexBound(C) ⊇ ClusterAabbs[C.chunkId]
    Too loose costs an overlap test; too tight is an SQ-01 false negative that no containment check on ClusterAabbs
    can observe, because ClusterAabbs is correct in that failure.
  invariant 🔴 the fence may NOT decide whether to write the index by comparing ClusterAabbs against itself.
    CA-01 names two writer classes for ClusterAabbs; the index has ONE, the fence. When writer class (a) — the
    CAS-grow in ClusterRef.MaybeGrowAndFlagShrink — has already applied the grow, the fence's `stored == fresh`
    test answers "the fence learned nothing", which is TRUE and not the question. The index is still a tick behind.
    ClusterRef.MaybeGrowAndFlagShrink says so in its own summary: it returns true "in which case the cluster needs
    a fence-time PerCellIndex.UpdateAt with the fresh AABB".
    On the SpatialBarrierOnly branch the comparison was worse than wrong, it was a TAUTOLOGY: `fresh` is assigned
    from `stored` when no shrink is pending, so a grow-only tick could never update the index at all — and both
    demos run barrier-only.
  invariant the signal is ClusterProcessBitmap, which WriteSpatial sets on exactly `aabbChanged || migrationFlagged`
    and ClearAabbRefreshBookkeeping zeroes once per tick. A writer that leaves ClusterAabbs alone for the fence to
    recompute (OpenMut / GetSpan) sets no bit, which is precisely the case where equality does mean nothing changed.
  scope: ArchetypeClusterState.RecomputeDirtyClusterAabbsSlice, ArchetypeClusterState.IsClusterProcessBitSet,
    ArchetypeClusterState.ApplyOrDeferClusterUpdate, ArchetypeClusterState.UpdateClusterInPerCellIndex,
    ClusterRef.MaybeGrowAndFlagShrink
  verified: CellTreeParallelFenceTests.CellIndexTracksClusterAabbs_AfterAWriteTimeGrow (both slicing branches,
    50 serial fence ticks of rotation, queries compared against entity positions read straight out of cluster
    storage). Pre-fix it failed on both branches with the index one to two ticks inside ClusterAabbs on every axis.
  on_violation:
    index bound tighter than ClusterAabbs → the cell prunes a cluster the query overlaps → every entity in it
      disappears from the result, silently, and CA-01 holds throughout because ClusterAabbs is right
  requires: CA-01 (ClusterAabbs itself contains the entities)

---

## Module: VDB Cell Grid (Issue #872 step 8)

### VG-01: A cell key names a live cell, or nothing `[fatal][silent]`
  invariant a cell key is a POOL SLOT in SpatialGrid's CellState pool, not a coordinate:
    ∀ key k handed out by ComputeCellKey / WorldToCellKey / TryGetCellKey: k ∈ [0, SpatialGrid.CellCount)
    ∀ such k: CellKeyToCoords(k) == the (x, y, z) the key was resolved from
  invariant a coordinate with no cell resolves to ABSENT, never to another cell's key:
    TryGetCellKey(x, y, z, out k) == false → k < 0
    TryGetNeighbourCellKey over a block that has not been created → false, and true once it is
  invariant cell keys are NOT stable across a rebuild or a ResetCellState — slots are handed out in
    creation order, so nothing may cache a key across either
  invariant creation is monotonic: a cell, once created, is never removed or renumbered while the grid
    lives (step 8 ships no destruction path; §3.5's windowed sweep is deferred)
  scope: SpatialGrid.ComputeCellKey, SpatialGrid.TryGetCellKey, SpatialGrid.TryGetNeighbourCellKey,
    SpatialGrid.CellKeyToCoords, SpatialGrid.ResetCellState, VdbBlockKey.Pack
  verified: VdbSpatialGridTests (AC82_NeighbourAcrossAnAbsentBlockBoundary_IsAbsentThenAppears carries the
    attribute; ResetCellState_InvalidatesTheBlockCacheOnEveryThread and AC84_RebuildFromTheSamePopulation
    cover the stability and monotonicity clauses). VdbBlockKeyTests covers the packing clause but carries no
    attribute of its own — the packing is reached through every one of these.
  on_violation:
    a remembered "absent" that later has a cell → query misses every cluster in it → SQ-01 false negative
    a block key that truncates an axis → two regions alias one block → each query returns the other's clusters
    a cached key surviving a rebuild → counters read against a cell that now belongs to a different position

### VG-02: Read paths never create cells `[perf][fatal]`
  invariant only a path that must PLACE something resolves with create — entity spawn, migration
    destination, and the rebuild's serial reduce. Everything else uses TryGetCellKey:
    query broadphase (AabbClusterEnumerator), tier assignment (SetTierInAABB, the accessor's
    coordinate-keyed setters), and every diagnostic or demo sweep
  invariant the rebuild's PARALLEL map phase resolves to COORDINATES only (ReadCellCoordsFromSpatialField).
    Creating from the map would make each pool slot a function of the worker count, which
    RebuildSpatialStateFromData's serial reduce exists to prevent
  invariant SpatialGridAccessor.GetCell(x, y, z) is the ONE coordinate-keyed accessor that DOES create, and
    exists for tests and diagnostics that need a cell to write into. Game code reading grid state must not use it
  note the determinism guarantee is REBUILD-ONLY. Migration destinations are created from fence workers, so a
    pool slot's index depends on worker interleaving there; only RebuildSpatialStateFromData promises a
    worker-count-independent numbering, and it does so by creating in its serial reduce
  scope: AabbClusterEnumerator.MoveNext, SpatialGrid.SetTierInAABB, SpatialGridAccessor.SetCellTier,
    SpatialGridAccessor.SetCellTierMin, SpatialGridAccessor.ComputeCellKey, SpatialGridAccessor.WorldToCell,
    SpatialGridAccessor.GetCell, ArchetypeClusterState.MapClusterForRebuild
  verified: VdbSpatialGridTests (ReadPaths_DoNotCreateCells carries the attribute and covers
    SpatialGrid.TryGetCellKey / TryGetCellKeyAt / SetTierInAABB). The accessor surface, the query broadphase and
    the parallel map phase are NOT directly verified — they are covered only indirectly, by the suite staying
    green with sparse memory
  on_violation:
    a read path resolving with create → one cell per swept coordinate → the grid silently becomes dense
      again, with correct answers and the memory C2 exists to avoid. Nothing fails; ResidentBytes is the
      only observable
    creating from the parallel map → pool slots depend on thread interleaving → rebuild output stops being
      bit-identical across worker counts

---

## Module: Per-cell R-Tree promotion (Issue #872 step 9)

### PC-01: A promoted cell's tree has exactly one writer `[fatal][silent]`
  invariant SpatialRTree is single-writer by specification (ADR-044; O2 in
    claude/design/Spatial/SpatialIndex/03-tree-operations.md). Nothing in CellClusterTree adds a latch, so
    every mutation of a promoted cell half must be serialised by the CALLER
  invariant the fence's AabbRefresh phase slices by CLUSTER id, not by cell (FenceWorkPlan.EmitAabbRefreshSliceItems
    emits bitmap-word or active-index ranges), so two workers routinely carry clusters of one cell. Neither
    slicing branch may therefore write a promoted cell's tree:
    ∀ slice worker w, ∀ cluster c whose cell is promoted:
      w appends (c.chunkId, cellKey) to its OWN List<PromotedAabbApply> and writes nothing
      the buffer is merged under _finalizeLock (EnqueuePromotedAppliesBulk), one acquisition per slice
      DrainPromotedAabbApplies replays them on ONE thread, in FinalizeArchetypeFence, after the phase barrier
  invariant 🔴 the deferral is conditional on a sink being supplied, and the fallback is MANDATORY: a null
    buffer means the caller is already the single writer (the serial whole-archetype recompute), so the write
    happens inline. Written as `buffer?.Add(...)` with no else, a null sink DISCARDS the update — ClusterAabbs
    advances, the tree does not, and the two diverge into SQ-01 false negatives with nothing raised. That
    shape was reachable on the only configuration the promotion guard leaves open
  invariant replay order is by cluster id, not arrival order. Arrival order depends on how the planner sliced
    and which worker finished first, so replaying in it would make the tree — and every handle in
    ClusterSpatialIndexSlot — a function of the worker count
  invariant promotion itself (MaybePromoteCellHalf) runs from AddClusterToPerCellIndex, which the parallel
    Migrate phase reaches. Cell-disjoint slicing does NOT protect the per-archetype resources it touches
    (Array.Resize of ClusterSpatialIndexSlot, the shared ChunkBasedSegment's Grow), so promotion is refused
    alongside a parallel fence until that is closed — see the guard named in scope
  scope: ArchetypeClusterState.ApplyOrDeferClusterUpdate, ArchetypeClusterState.EnqueuePromotedAppliesBulk,
    ArchetypeClusterState.DrainPromotedAabbApplies, ArchetypeClusterState.UpdateClusterInPerCellIndex,
    DatabaseEngine.AssertCellTreePromotionIsSafeForParallelFence
  verified: CellTreeParallelFenceTests (both slicing branches, 50 parallel-fence ticks with motion). Ablated:
    reverting the divert to an unconditional UpdateClusterInPerCellIndex reddens it 3 runs of 3, on the
    membership comparison — "cell 1 holds cluster 46 twice", the duplicate a second concurrent
    remove-and-reinsert leaves behind
  on_violation:
    two workers in one tree → a leaf entry duplicated or lost, or RemoveChecked raising an identity mismatch
      out of a fence worker. The tree stays STRUCTURALLY valid, so TreeValidator passes over corruption
    deferral without a fallback → the promoted cells silently stop tracking their clusters' bounds
    replay in arrival order → handles depend on worker count, and a rebuild stops reproducing them
  requires: ST-05 (handles live in PayloadBackPointers), ST-07 (the in-place window closes inside the fence)

---

## Module: Intra-cell relocation (Issue #872 step 10)

### CR-01: The pending-migration queue drains its prefix and keeps the rest `[fatal][silent]`
  invariant the queue has PRODUCERS ON BOTH SIDES OF ITS CONSUMER, and that is the whole rule.
    DetectClusterMigrations files during Prep, which precedes Migrate; FlagOutliersForMigration and
    DetectDriftersInCluster file during AabbRefresh, which FOLLOWS it. So:
      let P = PendingMigrationCount when PrepareArchetypeFence returns
      Migrate executes exactly PendingMigrations[0 .. P)
      Finalize removes exactly that prefix and shifts the remainder down
      ∀ tick: PendingMigrationCount after Finalize == (requests filed during this tick's AabbRefresh)
  invariant the prefix must be recorded on EVERY exit path of Prep, not at the bottom of its body. Prep has
    three returns, and the clean-bitmap `return true` is the one an archetype written through the spatial
    barrier takes on every ordinary tick — the barrier sets ClusterProcessBitmap and leaves
    ClusterDirtyBitmap clean. A prefix of zero there means Migrate executes the whole queue while Finalize
    compacts nothing away
  invariant neither failure is loud, and they fail in opposite directions:
    prefix too LARGE (the pre-#872 `PendingMigrationCount = 0`) → every request filed by the AabbRefresh
      producers is discarded before it is ever drained. The outlier guard had been detecting, counting itself
      in telemetry, merging under the finalize lock and dropping the result since #230; its own comment says
      those requests "execute next tick", and none of them ever did
    prefix too SMALL → executed requests stay queued and re-execute against slots their entities have already
      left, and the queue grows without bound. Measured: 16 000 entities produced 17 234 migrations on the
      first tick and 224 854 on the twentieth, against ~10 900 genuine drifters per tick
  scope: DatabaseEngine.PrepareArchetypeFence, ArchetypeClusterState.CompactPendingMigrations,
    ArchetypeClusterState.PendingMigrationDrainCount, DatabaseEngine.FinalizeArchetypeFence
  verified: ClusterRelocationTests.PendingQueue_KeepsOnlyWhatTheCurrentTickFiled (nine ticks of continuous
    intra-cell motion, asserting per tick that what remains queued is at most what that tick detected).
    Ablated: forcing the prefix to zero reddens it, and also reddens
    ClusterDriftParallelTests.DriftDetection_YieldsTheRulesDrifterSet_WhicheverFenceRunsIt
  on_violation:
    prefix too large → intra-cell drift is detected forever and repaired never; the ~24x selectivity win the
      issue exists for simply does not arrive, with every counter reporting healthy detection
    prefix too small → unbounded queue growth and repeated migration of slots whose occupants have moved on
  requires: MD-01 (dirty bits reflect both source and destination after each executed request)

### CR-02: A relocation destination is a preference, and every consumer must treat it as one `[fatal]`
  invariant MigrationRequest.DestClusterChunkId names the least-enlargement cluster detection chose, or
    AnyCluster (-1). It is computed a whole phase before the drain, so between the two the pinned cluster can
    fill up, or be drained and freed and its chunk id reallocated to a DIFFERENT cell
  invariant the claim must therefore validate identity, not bounds:
    TryClaimPinnedSlot requires ClusterCellMap[pin] == the request's DestCellKey before claiming
    on failure it falls back to the first-fit ClaimSlotInCell — it must NOT refuse the migration, which would
      strand the entity in a cluster it no longer belongs to
  invariant a pinned claim is a FOURTH success site for CellState.EntityCount. TryClaimSlotInCluster
    deliberately does not touch it and the scan overloads bump it at three separate sites, so the pinned path
    owes its own increment; without it a cell under-counts by one per relocation
  invariant placement reads ClusterAabbs for candidate clusters while the AabbRefresh phase is concurrently
    writing that array for clusters other slices own, so under W > 1 a candidate's box may be read either side
    of its own refresh. That is TOLERATED, and only because the pin is advisory: losing the race costs a
    slightly worse box, never a wrong one. It follows that the chosen DESTINATION is not reproducible across
    worker counts, while the DRIFTER SET is — the latter is decided from a cluster's own freshly computed
    bound and its own entities, both slice-local
  scope: ArchetypeClusterState.ClaimSlotInCell, ArchetypeClusterState.TryClaimPinnedSlot,
    ArchetypeClusterState.ChooseRelocationTarget, MigrationRequest.DestClusterChunkId
  verified: ClusterRelocationTests (Placement_ChoosesTheLeastEnlargementCandidate against an independent
    least-enlargement computation, Placement_NeverChoosesTheSourceCluster,
    Placement_TreatsAnEmptyClusterAsZeroEnlargement, Relocation_LeavesEveryEntityResidentExactlyOnce for the
    cell count). Ablated: ignoring the pin, and dropping the source exclusion, each redden their own test
  on_violation:
    no identity check → the entity lands in a cluster belonging to another cell; C13 broken silently, with
      every counter still balancing
    refusing instead of falling back → the entity stays in a cluster whose cell it has left
    missing EntityCount increment → cells report fewer entities than they hold
  requires: CC-01 (ClusterCellMap validity), CA-01 (the destination's bound must cover what it admits)

### CR-03: A drifter is defined by the target region alone `[silent]`
  invariant the rule is two-level, and the levels are not interchangeable:
    gate    — a cluster whose largest axis extent ≤ CellSize * ClusterTargetExtentRatio is skipped whole; no
              entity in it can be improved by moving, so a tight world does three float compares per written
              cluster and no per-entity work
    entity  — inside a gated cluster, an entity whose centre lies outside the target box by more than
              CellSize * ClusterDriftMarginRatio is a drifter
  invariant 🔴 the target box is centred on the cluster's CENTROID, never on the midpoint of its AABB. A box
    midpoint sits halfway between the two extremes, so ONE far outlier drags it half the distance to itself:
    thirty entities at x≈12 plus one at x=90 put the midpoint at 50, where nothing lives, and the whole core
    then reads as drifting. Relocating the majority to chase a point defined by the entity that should have
    left is the inverse of the intended repair. The centroid moves by 1/N instead of 1/2
  invariant DriftersDetected counts DETECTION, not outcome — it is incremented before placement is attempted.
    A cell whose every other cluster is full yields drifters and no migrations, and that gap is the signal
    step 11 needs; folding a placement outcome into a detection number destroys it
  invariant the intra-cell margin gets its own counter (DriftAbsorbedCount) and must not reuse
    HysteresisAbsorbedCount. That one is about cell-boundary oscillation and tunes MigrationHysteresisRatio;
    this one tunes ClusterDriftMarginRatio. The margins move independently, so one number tunes neither
  invariant 🔴 SCOPED EXCEPTION on the legacy (ActiveClusterIds) refresh branch: a cluster is examined only when its
    bound moved or its ClusterProcessBitmap bit is set. An entity moved by a writer that sets no process bit — OpenMut,
    or a raw GetSpan mutation — to a position INSIDE its cluster's existing bound satisfies neither, and is never
    tested. A real AC-10.1 false negative, kept because the same gate is what makes a quiet tick cost nothing on a
    branch that walks every active cluster with no dirty-bit filter; lifting it reddens AMotionlessTick_DetectsNothing.
    Closing it needs a per-cluster written-this-tick signal that branch does not carry. An archetype whose spatial
    writes all go through WriteSpatial is unaffected, and TYPHON009 flags the sites that do not
  invariant an entity the outlier guard has queued for ANOTHER cell is not also a drifter here. Two requests
    naming one source slot would have the second drain find it empty, and the guard's escape outranks an
    intra-cell quality move. Running the guard first only made the collision unlikely; since the two share one
    gather pass the guard returns the slots it claimed and detection excludes them by mask
  invariant one gather pass per written cluster feeds both consumers (D1). The cluster is walked at most twice
    per tick — once for the BOUND, through the double-precision directed-rounding reader C15 needs, and once
    for the CENTRES, cached SoA for the guard and the drift test to scan. The two readers stay separate on
    purpose: for a BSphere field the stored centre is not the midpoint of the derived bounds, only equal to it
    up to a rounding step, so deriving one from the other would move drift decisions by an ULP at the
    target-region boundary and decouple production from the oracle that reads the component the same way
  scope: ArchetypeClusterState.DetectDriftersInCluster, ArchetypeClusterState.GatherClusterCentres,
    SpatialGridConfig.ClusterTargetExtentRatio, SpatialGridConfig.ClusterDriftMarginRatio,
    SpatialMigrationTelemetry.DriftAbsorbedCount
  verified: ClusterDriftDetectionTests against ClusterDriftOracle (an independent implementation of the rule,
    not a call to the production predicate), plus ClusterDriftParallelTests for the serial ≡ oracle ≡ parallel
    equality at W in {1,2,8} under a real TyphonRuntime. Ablated: swapping the centroid for the AABB midpoint,
    and disabling the margin, each redden the differential
  on_violation:
    midpoint instead of centroid → a one-entity repair becomes a full cluster shuffle, away from where the
      cluster actually is
    gate removed → the per-entity walk runs on every written cluster, against a budget of 0.576 ns/entity
    counters merged → neither margin can be tuned from telemetry

---

---

## Module: Cell repair — the full Morton re-sort (Issue #872 step 12)

### RP-01: A repair unit is admitted whole or not at all `[fatal][silent]`
  invariant the budget gates ADMISSION, never progress. A Morton sort cannot be halved: a partly re-sorted cell
    has paid the cost and banked part of the benefit, and its downstream batches have already formed. So a unit
    whose projected cost exceeds the remaining budget is not begun — no entity of it is gathered, sorted or
    moved — and the refusal is counted
  invariant the projection precedes the work and uses only the population: entities * RepairNsPerEntity, where
    the population is a popcount of the unit's occupancy words. Measuring instead of projecting would decide
    admission after the cost was already paid, which is the thing the whole-unit rule exists to prevent
  invariant ReclusterBudgetUsedMs reports the PROJECTED spend of admitted units, not elapsed time. The number
    that gates has to be the number that is reported, or the two drift and neither can be reasoned about
  invariant a refusal must not spend budget. remainingNs is decremented only after a unit has emitted requests
  scope: ArchetypeClusterState.PlanCellRepairs, ArchetypeClusterState.RepairOneCell,
    SpatialGridConfig.ReclusterBudgetMs, SpatialGridConfig.RepairNsPerEntity,
    SpatialMigrationTelemetry.RepairUnitsRefused, SpatialMigrationTelemetry.ReclusterBudgetUsedMs
  verified: ClusterRepairTests.ARepairIsNeverBegunWithoutTheBudgetToFinishIt drives the budget to 99 % of the
    projected cost and asserts nothing moved, nothing was spent and the refusal was counted;
    TheSameCellIsRepairedOnceTheBudgetCoversTheUnit is its control at 150 %, so the pair separates "the rule
    fired" from "nothing was ever nominated"
  on_violation:
    budget stops a unit mid-way → a cell left in a state strictly worse than the one it started in
    refusal spends budget → later units in the same tick are starved by work that never happened

### RP-02: The repair plan is produced serially and its destinations are pinned exactly `[fatal][silent]`
  invariant NOMINATION is parallel (AabbRefresh workers) and its output is consumed as a SET — sorted, with
    repeats skipped — so no permutation of the same cells changes the plan
  invariant PLANNING is single-threaded: it runs in Prep, which dispatches one work item per archetype. The
    cluster ranking, the Morton sort and the destination assignment all live there
  invariant 🔴 a repair request pins the destination SLOT as well as the cluster, and the reason is the SORT, not
    the slicing. Before Migrate dispatches, SortPendingMigrationsByDestCellKey runs an Array.Sort — introsort,
    UNSTABLE — over a comparer reading DestCellKey alone, so every request a repair emits for one cell compares
    equal and the planner's emission order within that cell is permuted arbitrarily. That sort runs only on the
    parallel path, so first fit would give the serial and parallel fences different packings from identical
    input. NOT slicing: FenceWorkPlan.EmitMigrationApplyItems advances each boundary until DestCellKey changes, so
    one cell's run is never split and two workers can never claim into the same fresh cluster. Recorded because
    the wrong reason is worse than none — whoever makes that sort stable would, from it, correctly conclude the
    pinned slot is dead code
  invariant both pins remain PREFERENCES. The fallback chain is exact slot -> the pinned cluster's first free
    slot -> ClaimSlotInCell. A lost pin costs a worse box, never a wrong cell — CR-02 governs the rest
  invariant the sort's comparator is a TOTAL order — (mortonKey, sourceLocation) — so equal keys do not leave
    the tie to the sort's internals. sourceLocation is chunkId * 64 + slot and is unique across a unit
  scope: ArchetypeClusterState.PlanCellRepairs, ArchetypeClusterState.ExecuteRepairPlan,
    ArchetypeClusterState.TryClaimExactSlotInCluster, MigrationRequest.DestSlotIndex
  verified: ClusterRepairParallelTests.ARepairProducesTheSamePacking_WhicheverFenceRunsIt compares the
    per-cluster tag sequence across the serial fence and a real TyphonRuntime at W in {1,2,8}, having first
    asserted that each arm actually repacked and did not collapse to one cluster
  on_violation:
    slot not pinned → the packing depends on where slice boundaries fell; identical input, different layout
    planning parallelised → the cluster ranking races and no two runs agree

### RP-03: A repair allocates EMPTY destinations and re-sorts only when the sort would change something `[fatal][silent]`
  invariant destinations are FRESH clusters, never the unit's own. A re-pack is a permutation of the unit's
    slots, and ExecuteMigrations claims one slot at a time, so a destination still holding an entity yet to move
    fails its claim and falls back to first fit — the placement #872 exists to repair
  invariant 🔴 a fresh destination is published with occupancy ZERO and no CellState.EntityCount bump. Pre-setting
    the bits would be cheaper and is what "no claim on the repair path" would literally mean, but occupancy is
    authoritative: the unfiltered Count() fast path sums occupancy popcounts, so a set bit with no entity behind
    it over-reports the database for the width of a tick
  invariant every fresh destination is recorded via RecordClusterDrain at allocation. If every request targeting
    it is skipped it stays empty, and nothing else would ever schedule it for freeing — a release is the only
    event that normally does. The existing Finalize pass re-reads occupancy and frees only what is still empty
  invariant 🔴 the planner must top up _drainedClusterIds after it emits. PreSizeMigrationBuffers sizes that list
    from PendingMigrationCount, on the premise that one migration releases at most one source slot, and it runs in
    Prep's CORE — before the planner, which then files `count` more migrations AND consumes `destinationCount`
    drain entries of its own. Without the top-up the Migrate phase overflows into RecordClusterDrain's fallback
    grow, which parallel workers reach and which writes its entry AFTER releasing the lock, re-reading the field:
    an entry can be discarded by a concurrent resize, and a discarded drain record is this rule's leaked chunk id
  invariant destinations are allocated BEFORE any request is emitted. Interleaving lets an allocation fail partway
    and leave the unit half re-packed — the state RP-01 calls strictly worse than untouched — where allocating up
    front makes the failure atomic: nothing emitted, and the clusters already taken are freed by Finalize because
    they are still empty
  invariant 🔴 the no-op check must also stop the SCAN, not only the moves. Nomination fires on extent alone and a
    Morton packing does not in general bring every cluster under the threshold, so a converged cell is nominated
    again every tick and would pay a full GatherClusterCentres walk plus an Array.Sort to reach the same verdict —
    on Prep, single-threaded, and charged to no budget, since a unit that emits nothing is never debited. The memo
    is the hash of the unit's ranked bounds, which the ranking loop has already read. Heuristic in one direction:
    entities shuffling strictly inside their clusters' existing bounds change the key order without changing any
    bound, and that re-sort is skipped — the same exposure CR-03 records, and the delta path's population
  invariant 🔴 a unit already packed in sort order is NOT re-packed. Nomination fires on extent alone and a Morton
    packing does not in general bring every cluster under the threshold, so the same cell is nominated on every
    subsequent tick; without the check it is re-packed forever at full cost and zero gain. The test is exact and
    one pass: if every group of the packing already draws from a single source cluster, the sorted partition and
    the current one coincide
  scope: ArchetypeClusterState.AllocateEmptyClusterForCell, ArchetypeClusterState.IsAlreadyPackedInSortOrder,
    ArchetypeClusterState.ExecuteRepairPlan, ArchetypeClusterState.PreSizeDrainedClusterIds,
    ArchetypeClusterState.HashUnitGeometry
  verified: ClusterRepairTests.ARepairPreservesEveryEntityAndEveryInvariant (population, CA-01, C13, EntityMap
    resolution and CellState.EntityCount after the re-pack); ClusterRepairCrashTests (the rebuilt cell layer
    agrees with cluster storage across a reopen); ClusterRepairConvergenceTests, which pins TERMINATION — that a
    repaired cell is not repaired again while nothing moves, that it re-converges after destroys punch holes in
    the packing, and that IsAlreadyPackedInSortOrder answers partition equality on hand-built inputs including a
    partial trailing group. The no-op guard was added against a measurement: a 2 000-entity cell re-packed all
    2 000 on five consecutive ticks with its mean extent pinned at 23.0 throughout
  on_violation:
    destinations reused → claims fail and the sorted packing degrades to first fit
    occupancy pre-set → Count() over-reports for a tick
    drain not recorded → a cluster that never received an entity leaks its chunk id
    no-op check removed → a converged cell re-packs every tick, forever

### RP-04: The repair trigger is its own threshold, and it must see a still cell `[silent]`
  invariant 🔴 P7's stated value cannot fire. The design says to start at "the existing cellSize x 1.2 extent
    check", but that check belongs to the OUTLIER GUARD and looks for a bound that has escaped its own cell,
    which happens only when a cluster holds entities that should have migrated out. A cluster whose entities all
    belong to its cell tops out near 1.05 x cellSize (the hysteresis margin), so 1.2 is unreachable and AC-12.1's
    own scenario — AABBs at ~90 % of the cell — sits below it. ClusterRepairExtentRatio (0.75) replaces it
  invariant the repair threshold sits strictly between the drift gate (ClusterTargetExtentRatio, 0.25) and the
    outlier guard (1.2), so a nominating cluster has always been drift-gated too. Nominating at the drift gate
    would ask for a re-sort of every cluster the delta path is already working on, which is the opposite of rare
  invariant only the UPPER half is enforced by a throw. At or above 1.2 the threshold can never be reached, so the
    value silently disables the feature — a configuration error. The lower relation is a tuning guideline about
    two mechanisms competing and stops applying the moment the drift gate is switched off (the fixtures do that by
    setting the target ratio to 100, which no cluster can exceed), so throwing on it would reject a legal
    configuration in which repair is the only mechanism running
  invariant 🔴 nomination on the legacy refresh branch runs BEFORE the boundsMoved / process-bit skip. The design's
    own trigger list for repair includes "initial load / rebuild" — a cell laid out badly and then never written —
    and below the skip that cell is invisible. Measured: a cell spawned in scattered order sat at a mean extent of
    86.4 of 100 for six consecutive ticks with clustersScanned = 0 and no nomination. It is free there because the
    branch has already recomputed the bound over every occupied slot
  invariant 🔴 KNOWN GAP, barrier-only mode: that branch iterates ClusterProcessBitmap, which by construction holds
    only clusters written this tick, so a still cell is never nominated. Closing it needs a signal that ranks CELLS
    rather than reacting to cluster writes — step 11's priority queue
  scope: SpatialGridConfig.ClusterRepairExtentRatio, ArchetypeClusterState.RecomputeDirtyClusterAabbsSlice,
    ArchetypeClusterState.EnqueueRepairNominationsBulk
  verified: ClusterRepairTests.ARepairPassTightensADegradedCell drives a still, never-moved cell and asserts the
    repair both fires and tightens it; the fixture would go green with the nomination deleted only if the cell
    were also moving, which it is not
  on_violation:
    trigger left at 1.2 → the repair path never runs, and its test suite says nothing
    nomination below the skip → a degraded cell that stops moving is never repaired

### RP-05: A repair is the only thing that narrows a zone map `[perf][silent]`
  invariant ZoneMapArray.Widen is the only writer on the hot path and never narrows, so a cluster accumulates the
    union of every value it has held, and a RECYCLED chunk id inherits its previous tenant's bounds because
    nothing invalidates them on free. ZoneMapArray.Invalidate existed with no caller at all before this step
  invariant the direction of the error is conservative — MayContain over-reports, so queries open clusters they
    need not and never miss one — which is why it is a [perf] rule and not a [fatal] one
  invariant a repair invalidates every indexed field's zone map for each destination cluster it allocates, so
    Widen rebuilds from the re-packed contents. Without it the narrowing AC-12.2 measures cannot occur at all
  invariant the narrowing is real only for a field CORRELATED with position. Locality-grouping tightens the min/max
    of a coordinate, or of something tracking one; for an index on an unrelated quantity a re-sort neither helps
    nor harms, and claiming otherwise would be claiming magic
  scope: ArchetypeClusterState.InvalidateClusterZoneMaps, ZoneMapArray.Invalidate, ZoneMapArray.TryGetBounds
  verified: ClusterRepairTests.ARepairNarrowsTheZoneMapsOfTheCellItRepacks measures total recorded width before
    and after over every cluster of the cell and every indexed field; measured 3 541 -> 796 (22 %) at 2 000
    entities in 41 clusters. It also asserts the total is non-zero afterwards, so "narrower" cannot be satisfied
    by "invalidated and never re-widened"
  on_violation:
    invalidate omitted → the re-packed cluster inherits a stale wide bound and prunes nothing
    invalidate without a following widen → the map reads "unknown", which is conservative but buys no pruning

## Module: ClusterCellMap (Issue #229)

### CC-01: ClusterCellMap validity `[fatal]`
  invariant ∀ active cluster C:
    ClusterCellMap == null ∨ C.chunkId ≥ ClusterCellMap.Length ∨
    ClusterCellMap[C.chunkId] ∈ [-1, SpatialGrid.CellCount)
  invariant ClusterCellMap[chunkId] ≥ 0 → cluster is assigned to a valid cell
  invariant ClusterCellMap[chunkId] < 0 → cluster is unassigned (skipped by TierClusterIndex)
  note the bound moved in #872 step 8. CellCount was the whole world's cell count, fixed at config time;
    it is now the number of cells that EXIST, which grows as cells are first touched. The invariant is
    unchanged in FORM but WEAKER as a detector: against a world-sized bound a stale or corrupted key was
    usually out of range and threw, whereas a small growing bound accepts it silently. A key is meaningful
    only against the grid instance that issued it, and only until a rebuild (VG-01)
  note transiently false between ResetCellState and the rebuild that follows it — the map still holds the
    old keys while the grid holds no cells. RebuildSpatialStateFromData refills both; nothing may read
    ClusterCellMap in between
  scope: ArchetypeClusterState.ClaimSlotInCell, RebuildCellState, RebuildSpatialStateFromData, TierClusterIndex.Rebuild
  on_violation:
    cellKey ≥ CellCount → IndexOutOfRangeException in SpatialGrid.GetCell
    cellKey corrupted → cluster bucketed into wrong cell → wrong tier assignment, wrong query results
    cellKey held across a rebuild → names a different position's cell → silently wrong counters and tiers

---

## Module: TierClusterIndex (Issue #231)

### TI-01: Rebuild-before-dispatch ordering `[fatal]`
  invariant [RebuildIfStale] → [any parallel system reads per-tier arrays]
  invariant RebuildIfStale runs single-threaded at tick start (BuildTierIndexesAtTickStart)
    before any parallel system dispatch begins
  invariant Debug: Interlocked.CompareExchange(_rebuildInProgress, 1, 0) == 0
    asserts no concurrent Rebuild calls
  scope: TierClusterIndex.Rebuild, TierClusterIndex.RebuildIfStale, TyphonRuntime.BuildTierIndexesAtTickStart
  on_violation: parallel readers see partially-written tier arrays → torn reads, wrong cluster lists,
    clusters dispatched to wrong systems

### TI-02: Single-bit tier byte at rebuild `[fatal]`
  invariant ∀ cell processed during Rebuild:
    BitOperations.PopCount(cell.Tier) == 1
  invariant Debug.Assert validates single-bit at rebuild (TZCNT maps directly to array index)
  requires: SC-01 (SetCellTier rejects multi-bit flags)
  scope: TierClusterIndex.Rebuild
  on_violation: PopCount > 1 → TZCNT maps to wrong tier index → cluster assigned to wrong tier array

### TI-03: TierVersion monotonicity `[silent]`
  invariant _tierVersion only increments (never decremented or reset to 0)
  invariant SetCellTier: no-op when cell.Tier == (byte)tier (no spurious version bump)
  invariant ResetAllTiers: bumps _tierVersion at most once, only when at least one cell actually changed
  scope: SpatialGrid.SetCellTier, SpatialGrid.ResetAllTiers, SpatialGrid.SetCellTierMin
  on_violation:
    spurious bumps → unnecessary TierClusterIndex rebuilds (perf only, not correctness)
    missed bumps → TierClusterIndex uses stale tier assignment → systems process wrong clusters

---

## Module: Migration Dirty Bits (Issue #232)

### MD-01: Post-migration dirty bit consistency `[fatal][silent]`
  invariant after ExecuteMigrations for each migrated entity (src → dst):
    dirtyBits[srcChunkId] bit srcSlot is cleared
    dirtyBits[dstChunkId] bit dstSlot is set
  invariant if dstChunkId ≥ dirtyBits.Length:
    Array.Resize(ref dirtyBits, max(dirtyBits.Length * 2, dstChunkId + 1))
  invariant caller's ref parameter is updated so subsequent readers
    (DirtyRing archive, WAL publish loop, PreviousTickDirtySnapshot) see the grown array
  scope: DatabaseEngine.ExecuteMigrations (lines 1630-1645)
  on_violation:
    source bit not cleared → WAL serializes stale/zeroed source slot → corrupt replay
    dest bit not set → WAL misses new slot data → entity lost on crash recovery
    array not grown → IndexOutOfRangeException or silent skip of new destination cluster

### MD-02: Parallel migration apply — concurrent-mutation primitives `[fatal][silent]`
  invariant during ExecuteMigrationsSlice (parallel-fence Migrate phase, multiple workers per archetype):
    src occupancy bit clear uses Interlocked.And on the cluster's u64 occupancy word
    src per-component EnabledBits clear uses Interlocked.And per component slot
    src cell.EntityCount decrement uses Interlocked.Decrement(ref CellState.EntityCount)
    dst cell.EntityCount / ClusterCount increment uses Interlocked.Increment(ref CellState.*)
    src/dst dirtyBits flips are NOT written by the worker on the parallel path: it appends a
      DirtyBitDelta to its own chunk-local buffer, and OnAfterChunk applies the whole buffer through
      ArchetypeClusterState.ApplyDirtyBitDeltas under _finalizeLock, one acquisition per (chunk x
      archetype). Plain bit ops are correct there BECAUSE the lock excludes sibling workers. Only the
      serial WriteTickFence path (dirtyBuffer == null) writes the array directly, with Interlocked.And /
      Interlocked.Or plus GrowFenceDirtyBitsForChunkId — safe because it is the only thread.
      CORRECTED 2026-08-14: this rule previously specified Interlocked flips from the worker, which the
      implementation has never done — the buffered-plus-lock design shipped in the same PR as the rule.
      A rule that describes a design nobody built cannot catch a regression in the one that exists
    the per-chunk delta buffers are sized in FenceMigrateExecSystem.Prepare, never grown from a worker:
      growing the shared buffer array from concurrent DispatchItem calls loses a bucket when two growers
      race, and the plain reference store is unordered against its Array.Copy on arm64
    cluster-fully-drains path (popcount(prev & ~mask) == 0) does NOT finalize in the worker:
      it records the chunkId via RecordClusterDrain (Interlocked.Increment slot reservation)
      and returns. CellClusterPool.RemoveCluster, RemoveClusterFromPerCellIndex,
      RemoveFromActiveList and ClusterSegment.FreeChunk run later, in
      DrainPendingClusterFinalizations, which re-checks occupancy and skips refilled clusters
  invariant DrainPendingClusterFinalizations runs exactly once per archetype per fence, from
    FinalizeArchetypeFence, AFTER the Migrate and AabbRefresh phase barriers. It takes no lock,
    and must not: its safety is that the barriers exclude every concurrent ClaimSlotInCell and
    ReleaseSlot for this archetype, so the occupancy re-check and the free are single-threaded.
    A lock here would not substitute for the barrier — finalizing while a claimer holds a
    CAS-won slot frees a live chunk no matter who holds what
  invariant FenceDirtyBits is pre-sized in PrepareArchetypeFence's tail BEFORE any Migrate-phase worker
    observes it. The bound is a deliberate over-estimate (max(PrimarySegmentCapacity, existingLen) +
    2*PendingMigrationCount + 64), NOT the strict PrimarySegmentCapacity + PendingMigrationCount this rule
    used to state — that one was observed to under-estimate under AntHill loads. It is a performance
    measure, not the safety argument: the parallel path never touches the array, and the on-demand grow in
    ApplyDirtyBitDeltas / GrowFenceDirtyBitsForChunkId is what actually makes an under-estimate survivable
  invariant PendingMigrations is sorted by DestCellKey (SortPendingMigrationsByDestCellKey)
    by TickDriver between Prep and Migrate dispatches, so each worker slice owns disjoint dst cells
  invariant PendingMigrationCount = 0 reset happens once per fence in FinalizeArchetypeFence
    AFTER all Migrate-phase slices complete, never inside ExecuteMigrationsSlice
  scope: DatabaseEngine.ExecuteMigrations, DatabaseEngine.FinalizeArchetypeFence,
    ArchetypeClusterState.ClearSlotMetadata, ArchetypeClusterState.ApplyDirtyBitDeltas,
    ReleaseSlot (Persistent + Transient overloads), DecrementCellEntityCountOnRelease,
    FinaliseEmptyClusterCellState, ClaimSlotInCell (both overloads),
    RecordClusterDrain, DrainPendingClusterFinalizations
  verified: FenceDirtyBitApplyTests
  on_violation:
    plain ++/-- on cell counters → torn updates across workers → drift in EntityCount/ClusterCount
    plain occupancy clear → lost concurrent slot release → ghost entity in cluster
    worker finalizes inline instead of recording the drain → frees a chunk a concurrent
      ClaimSlotInCell has already CAS-claimed → live entity written into a freed chunk
    finalize pass moved before the phase barriers → same race, now unconditional
    Array.Resize from worker → lost writes from siblings holding the old array reference
    ApplyDirtyBitDeltas called without _finalizeLock while siblings run → its plain bit ops lose
      concurrent flips and its grow drops their writes
    the chunk-buffer array grown from a worker → two growers race, one bucket is dropped from the array,
      and its deltas are lost the moment anything reads buckets back out of the array rather than through
      the reference the worker already holds

### MD-03: False-sharing avoidance for concurrent-mutation state `[perf][fatal]`
  invariant any data structure mutated concurrently from multiple workers isolates each
    independently-mutated element onto its own ≥64-byte cache line
  forbid bit-packed latch arrays (one bit per latch in a shared long[]) — adjacent latches
    share cache lines → catastrophic ping-pong on every CAS even under no logical contention
  prefer AoS (cluster all per-element state into one padded struct) over SoA + parallel
    padded arrays when padding is required — same memory cost, fewer cache-line fetches
  note CellState is [StructLayout(Explicit, Size = 64)] with 24 bytes of fields + 40 reserved
    (corrected 2026-07-27 from a stated 16; corrected again 2026-09-02 when #872 step 8 added
     CellX/CellY/CellZ at offsets 12/16/20 — a cell key became a pool slot and carries no position,
     so the cell has to hold its own coordinates)
  canonical example: CellState (24 bytes of fields, [StructLayout(Explicit, Size=64)],
    40 bytes reserved tail) — Tier, Flags, EntityCount, ClusterCount, CellX/Y/Z all on one line per cell
  applies to: CellState array, ArchetypeClusterState._finalizeLock (PaddedFinalizeLock 64B
    struct), any future per-cell or per-cluster latch arrays
  scope: SpatialGrid.GetCell, ArchetypeClusterState._finalizeLock, any new per-element
    Interlocked-mutated array
  note the dense CellState[] became a CHUNKED pool in #872 step 8. The 64-byte layout is unchanged, and
    the chunking is what keeps the `ref CellState` a stable interior pointer while the pool grows — a
    resize would hand a concurrent worker a doomed array, which is MD-02's concern rather than this one
  on_violation:
    bit-packed latches → 8× ping-pong amplification, parallel speedup collapses
    16-byte cell descriptors in flat array → 4-cell ping-pong per migration
    unpadded shared latch field → adjacent hot fields invalidate the line on every acquire

---

## Module: Dormancy (Issue #233)

### DM-01: Wake guarantee — max one-tick latency `[fatal]`
  invariant ∀ cluster C in Sleeping state:
    SetDirty(C.chunkId, _) → DormancyReporter.RequestWake(archetypeId, C.chunkId)
  invariant DormancyReporter.DrainAll runs single-threaded at tick fence (WriteClusterTickFence),
    processes all thread-local wake requests, calls ProcessWakeRequest per entry
  invariant ProcessWakeRequest: Sleeping → WakePending (no-op if already WakePending)
  invariant TransitionWakePendingToActive: WakePending → Active at next tick start
    (BuildTierIndexesAtTickStart, before tier index rebuild)
  post maximum latency: dirty write at tick T → WakePending at tick T fence → Active at tick T+1 start
  scope: ArchetypeClusterState.SetDirty, DormancyReporter.RequestWake, DrainAll,
    ArchetypeClusterState.ProcessWakeRequest, TransitionWakePendingToActive
  on_violation: sleeping cluster with dirty writes never wakes → entity changes never dispatched to systems

### DM-02: SleepingClusterCount consistency `[fatal]`
  invariant SleepingClusterCount == |{ C ∈ ActiveClusterIds : SleepStates[C] ∈ {Sleeping, WakePending} }|
  invariant incremented: Active → Sleeping (DormancySweep, counter ≥ SleepThresholdTicks)
  invariant decremented: WakePending → Active (TransitionWakePendingToActive)
  invariant decremented: RemoveFromActiveList when SleepStates[chunkId] ∈ {Sleeping, WakePending}
  invariant SleepingClusterCount == 0 → all dormancy filtering in OnParallelQueryPrepare is skipped
    (zero overhead fast path)
  scope: ArchetypeClusterState.DormancySweep, TransitionWakePendingToActive, RemoveFromActiveList
  on_violation:
    count too high → false filtering, active clusters skipped by systems
    count too low → sleeping clusters dispatched to systems (wasted CPU)
    count stuck > 0 → dormancy filter overhead even when no clusters are sleeping

### DM-03: Sleep counter overflow guard `[perf]`
  invariant SleepCounters is ushort[] (range [0, 65535])
  invariant SleepThresholdTicks clamped to [0, ushort.MaxValue] via property setter:
    _sleepThresholdTicks = Math.Clamp(value, 0, ushort.MaxValue)
  invariant counter can never exceed ushort.MaxValue because transition fires at SleepThresholdTicks
    (which is ≤ ushort.MaxValue), so the counter is consumed before overflow
  scope: ArchetypeClusterState.SleepThresholdTicks (property), DormancySweep
  on_violation: counter wraps to 0 → cluster oscillates between Active and Sleeping every 65536 ticks
    instead of staying asleep

---

## Module: Checkerboard Partition (Issue #234)

### CB-01: Exhaustive disjoint partition `[fatal]`
  invariant ∀ filtered cluster set S for a checkerboard system:
    Red ∪ Black == S ∧ Red ∩ Black == ∅
  invariant Red = { C ∈ S : (cellX + cellY + cellZ) % 2 == 0 } where (cellX, cellY, cellZ) = grid.CellKeyToCoords(ClusterCellMap[C])
  invariant Black = { C ∈ S : (cellX + cellY + cellZ) % 2 == 1 }
  note the grid gained a Z axis in #872 step 8; a flat world has cellZ == 0 throughout, so its partition is unchanged.
    The three-dimensional parity is still a proper 2-colouring for 6-neighbour adjacency, which is the property the
    two-phase dispatch actually relies on.
  invariant fallback: ClusterCellMap == null ∨ grid == null → Red = S, Black = ∅
    (non-spatial archetype degenerates to single-phase dispatch)
  invariant unmapped cluster (cellKey < 0) → assigned to Red (fallback)
  scope: TyphonRuntime.SplitCheckerboardClusters
  on_violation:
    non-exhaustive → some clusters never processed → entity state stale
    non-disjoint → some clusters processed twice → double-apply side effects

### CB-02: Two-phase dispatch protocol `[fatal]`
  invariant phase 0 → 1: split into Red/Black, serve Red cluster list
  invariant phase 1 → 2: serve Black cluster list (triggered by re-dispatch after Red completes)
  invariant phase 2 → 0: reset for next tick
  never phase 0 serves Black (Black only served after Red completes)
  scope: TyphonRuntime.OnParallelQueryPrepare (checkerboard section, phase 0→1 / 1→2),
         TyphonRuntime.OnParallelQueryCleanup (phase 2→0 reset + Red→Black re-dispatch)
  note corrected 2026-07-27 — `OnParallelQueryEnd` does not exist in the engine
  on_violation: both phases see same partition → clusters processed twice or zero times

---

## Module: SetCellTier Validation (Issue #231)

### SC-01: Single-bit SimTier enforcement `[fatal]`
  invariant SetCellTier(cellKey, tier): tier must be SimTier.None or a single-bit flag
  invariant tier ≠ SimTier.None ∧ ¬tier.IsSingleTier() → throw ArgumentException
  invariant TierClusterIndex.Rebuild Debug.Assert validates PopCount(cell.Tier) == 1
    for every non-zero tier byte encountered
  never a CellDescriptor.Tier byte has more than one bit set
  scope: SpatialGrid.SetCellTier, SpatialGrid.SetCellTierMin, TierClusterIndex.Rebuild
  on_violation: multi-bit tier stored → TZCNT at rebuild produces wrong index →
    cluster routed to wrong tier array → system processes wrong cluster set
