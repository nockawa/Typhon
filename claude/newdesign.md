# Typhon Architecture Redesign - Type Decomposition for Testability

## Executive Summary

This document proposes **5 new concrete types** extracted from the monolithic `Transaction` and `ComponentTable` classes to improve separation of concerns and enable granular unit testing. Each proposal includes:

- Detailed responsibility breakdown
- Pros/cons analysis
- Refactoring cost estimate
- Performance risk assessment
- Specific unit tests to add

**Critical constraint**: Runtime performance is the absolute priority. All proposals use zero-cost abstractions (struct wrappers, aggressive inlining) to ensure no performance regression.

---

## Table of Contents

1. [Current Architecture Problems](#1-current-architecture-problems)
2. [Proposed Type Extractions](#2-proposed-type-extractions)
   - 2.1 [RevisionChainManager](#21-revisionchainmanager)
   - 2.2 [ConflictDetector](#22-conflictdetector)
   - 2.3 [RevisionGarbageCollector](#23-revisiongarbagecollector)
   - 2.4 [TransactionOperationCache](#24-transactionoperationcache)
   - 2.5 [SecondaryIndexManager](#25-secondaryindexmanager)
3. [Test Strategy](#3-test-strategy)
4. [Implementation Roadmap](#4-implementation-roadmap)
5. [Appendix: Architecture Diagrams](#5-appendix-architecture-diagrams)

---

## 1. Current Architecture Problems

### 1.1 Monolithic Classes

The current architecture has two excessively large classes that handle too many responsibilities:

#### Transaction.cs (~1000 LOC) - 6 Responsibilities Mixed Together

```
┌──────────────────────────────────────────────────────────────────────────┐
│                    TRANSACTION CLASS - CURRENT STATE                     │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  1. State Management          ────►  Transaction lifecycle (tick, state) │
│                                                                          │
│  2. Operation Caching         ────►  Dictionary<PK, Operation> per type  │
│     └── _createList                                                      │
│     └── _readList                                                        │
│     └── _updateList                                                      │
│     └── _deleteList                                                      │
│                                                                          │
│  3. Revision Chain Manipulation ───► CreateEntity, UpdateEntity logic    │
│     └── CompRevStorageHeader navigation                                  │
│     └── Circular buffer management                                       │
│     └── Overflow chunk allocation                                        │
│                                                                          │
│  4. Commit Logic              ────►  CommitComponent() - 150+ lines      │
│     └── Two-phase commit                                                 │
│     └── Isolation flag clearing                                          │
│     └── Index updates                                                    │
│                                                                          │
│  5. Rollback Logic            ────►  Component deletion, chain cleanup   │
│                                                                          │
│  6. GC Triggering             ────►  TryGCIfNeeded(), revision cleanup   │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

#### ComponentTable.cs (~800 LOC) - 5 Responsibilities Mixed Together

```
┌──────────────────────────────────────────────────────────────────────────┐
│                  COMPONENTTABLE CLASS - CURRENT STATE                    │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  1. Segment Management        ────►  ComponentSegment, CompRevTableSeg   │
│                                                                          │
│  2. Primary Key Index         ────►  L64BTree for EntityID → ChunkId     │
│                                                                          │
│  3. Secondary Index Management ───►  Dictionary<fieldId, IBTree>         │
│     └── Index creation per field                                         │
│     └── Index updates on commit                                          │
│     └── Index queries                                                    │
│                                                                          │
│  4. Schema-Driven Access      ────►  Field offsets, type mapping         │
│                                                                          │
│  5. Revision Storage          ────►  Mixed with segment management       │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Testing Pain Points

| Problem | Impact |
|---------|--------|
| Cannot test revision chain logic without full database stack | Forces integration tests for algorithm bugs |
| Conflict detection embedded in CommitComponent() | Impossible to unit test edge cases |
| GC logic mixed with transaction state | Cannot test cleanup independently |
| Index updates scattered across Transaction and ComponentTable | Hard to verify index consistency |
| Operation caching logic interleaved with business logic | No focused tests for cache behavior |

### 1.3 Code Quality Issues

From current `Transaction.cs`:
```csharp
// TODO: also cleanup PK index if all comp are of Deleted type
// TODO: Also cleanup comp data chunks
private void GC(ComponentTable table)
{
    // 100+ lines of mixed concerns
}
```

These TODOs indicate incomplete functionality that's hard to fix because it's tangled with other concerns.

---

## 2. Proposed Type Extractions

### 2.1 RevisionChainManager

#### Purpose
Extract all MVCC revision chain manipulation from `Transaction` and `ComponentTable` into a single focused type.

#### Current Code Locations

| Source File | Code Block | Lines |
|-------------|------------|------:|
| Transaction.cs | CreateEntity revision logic | ~80 |
| Transaction.cs | UpdateEntity revision logic | ~60 |
| Transaction.cs | ReadEntity revision lookup | ~40 |
| Transaction.cs | CommitComponent conflict handling | ~50 |
| ComponentTable.cs | CompRevStorageHeader management | ~30 |

**Total**: ~260 lines to extract

#### Proposed Interface

```csharp
// New file: src/Typhon.Engine/Database Engine/RevisionChainManager.cs

/// <summary>
/// Manages component revision chains for MVCC snapshot isolation.
/// Extracted from Transaction.cs to enable isolated testing of:
/// - Circular buffer management
/// - Overflow chunk allocation
/// - Visibility rules
/// - Revision lookup algorithms
/// </summary>
public readonly struct RevisionChainManager
{
    private readonly ChunkBasedSegment _revisionSegment;
    private readonly ChunkBasedSegment _componentSegment;
    private readonly ChunkRandomAccessor _accessor;

    public RevisionChainManager(
        ChunkBasedSegment revisionSegment,
        ChunkBasedSegment componentSegment,
        ChunkRandomAccessor accessor)
    {
        _revisionSegment = revisionSegment;
        _componentSegment = componentSegment;
        _accessor = accessor;
    }

    /// <summary>
    /// Creates a new revision chain for a new entity.
    /// </summary>
    /// <param name="componentChunkId">Where component data is stored</param>
    /// <param name="tick">Transaction tick for visibility</param>
    /// <param name="isolated">True if uncommitted (invisible to other transactions)</param>
    /// <returns>ChunkId of the new revision chain header</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CreateRevisionChain(int componentChunkId, long tick, bool isolated)
    {
        var chunkId = _revisionSegment.AllocateChunk(true);
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chunkId);

        header.FirstItemRevision = 1;
        header.FirstItemIndex = 0;
        header.ItemCount = 1;
        header.ChainLength = 1;
        header.LastCommitRevisionIndex = isolated ? -1 : 0;

        ref var element = ref header.GetElement(0);
        element.ComponentChunkId = componentChunkId;
        element.DateTime = PackedDateTime.Now;
        element.IsolationFlag = isolated ? (byte)1 : (byte)0;

        return chunkId;
    }

    /// <summary>
    /// Adds a new revision to an existing chain.
    /// Handles circular buffer wrapping and overflow chunk allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int revisionIndex, int componentChunkId) AddRevision(
        int chainChunkId,
        long tick,
        bool isolated,
        bool reuseComponentChunk = false)
    {
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);

        var newRevisionNumber = header.FirstItemRevision + header.ItemCount;
        var newIndex = (header.FirstItemIndex + header.ItemCount) % CompRevStorageHeader.MaxElements;

        // Check if we need to allocate overflow chunk
        if (header.ItemCount >= CompRevStorageHeader.MaxElements * header.ChainLength)
        {
            var overflowChunkId = _revisionSegment.AllocateChunk(true);
            // Navigate to last chunk and link
            ref var lastChunk = ref GetLastChunk(ref header);
            lastChunk.NextChunkId = overflowChunkId;
            header.ChainLength++;
        }

        // Get element in appropriate chunk
        ref var element = ref GetElementForRevision(ref header, newRevisionNumber);

        var componentChunkId = reuseComponentChunk
            ? GetLatestComponentChunkId(ref header)
            : _componentSegment.AllocateChunk(true);

        element.ComponentChunkId = componentChunkId;
        element.DateTime = PackedDateTime.Now;
        element.IsolationFlag = isolated ? (byte)1 : (byte)0;

        header.ItemCount++;

        return (newRevisionNumber - header.FirstItemRevision, componentChunkId);
    }

    /// <summary>
    /// Finds the visible revision for a given tick.
    /// Implements MVCC visibility rules:
    /// 1. Skip isolated revisions (unless same transaction)
    /// 2. Find newest revision with tick <= transactionTick
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetVisibleRevision(
        int chainChunkId,
        long transactionTick,
        int? currentTransactionId,
        out int componentChunkId,
        out int revisionIndex)
    {
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);

        // Walk from newest to oldest
        for (int i = header.ItemCount - 1; i >= 0; i--)
        {
            ref var element = ref GetElementForIndex(ref header, i);

            // Skip isolated revisions from other transactions
            if (element.IsolationFlag != 0)
            {
                // TODO: Check if this is our transaction (need transaction ID in element)
                continue;
            }

            // Check visibility based on tick
            if (element.DateTime.ToTick() <= transactionTick)
            {
                componentChunkId = element.ComponentChunkId;
                revisionIndex = i;
                return !element.IsDeleted;
            }
        }

        componentChunkId = 0;
        revisionIndex = -1;
        return false;
    }

    /// <summary>
    /// Clears isolation flag, making revision visible to other transactions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRevision(int chainChunkId, int revisionIndex)
    {
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);
        ref var element = ref GetElementForIndex(ref header, revisionIndex);
        element.IsolationFlag = 0;
        header.LastCommitRevisionIndex = revisionIndex;
    }

    /// <summary>
    /// Gets conflict detection info for optimistic locking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int lastCommitIndex, int currentIndex) GetConflictInfo(int chainChunkId)
    {
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);
        return (header.LastCommitRevisionIndex, header.ItemCount - 1);
    }

    // Private helpers
    private ref CompRevStorageHeader GetLastChunk(ref CompRevStorageHeader header) { /* ... */ }
    private ref CompRevStorageElement GetElementForRevision(ref CompRevStorageHeader header, int revisionNumber) { /* ... */ }
    private ref CompRevStorageElement GetElementForIndex(ref CompRevStorageHeader header, int index) { /* ... */ }
    private int GetLatestComponentChunkId(ref CompRevStorageHeader header) { /* ... */ }
}
```

#### Pros

| Benefit | Description |
|---------|-------------|
| **Isolated Testing** | Can test circular buffer logic, overflow handling, visibility rules independently |
| **Single Responsibility** | One class for all revision chain concerns |
| **Clearer Algorithm** | MVCC visibility rules in one place instead of scattered |
| **Bug Fix Enablement** | Easier to fix TODO items (PK cleanup, comp data cleanup) |
| **Reusability** | Could be reused for other MVCC implementations |

#### Cons

| Drawback | Mitigation |
|----------|------------|
| New type to maintain | Well-defined interface limits scope |
| Coordination overhead | Pass RevisionChainManager to Transaction |
| Potential cache locality impact | Keep struct-based, inline aggressively |

#### Refactoring Cost: **MEDIUM-HIGH**

| Task | Effort |
|------|--------|
| Extract code from Transaction.cs | 4 hours |
| Extract code from ComponentTable.cs | 2 hours |
| Update Transaction to use new type | 3 hours |
| Write unit tests | 4 hours |
| Integration testing | 2 hours |
| **Total** | **~15 hours** |

#### Performance Risk: **LOW**

- Struct-based implementation (no heap allocation)
- All methods marked `AggressiveInlining`
- No virtual dispatch
- Same memory access patterns as current code
- **Mitigation**: Benchmark before/after to verify

---

### 2.2 ConflictDetector

#### Purpose
Extract conflict detection logic from `Transaction.CommitComponent()` into a focused type that can be unit tested with various conflict scenarios.

#### Current Code Location

From `Transaction.cs` `CommitComponent()` (lines 842-987):
```csharp
// Current: conflict detection in CommitComponent method
private bool CommitComponent(ref CommitContext context)
{
    // ... setup code ...

    ref var firstChunkHeader = ref firstChunkHandle.AsRef<CompRevStorageHeader>();

    // Conflict detection logic (line 899):
    // BuildPhase: do we have a conflict that requires us to create a new revision?
    var hasConflict = (conflictSolver?.IsBuildPhase ?? true) &&
                      (firstChunkHeader.LastCommitRevisionIndex >= compRevInfo.CurRevisionIndex);

    if (hasConflict)
    {
        // Create a new revision and copy data forward
        AddCompRev(info, ref compRevInfo, context.CommitTime.Ticks, false);

        // Copy the revision we are dealing with to the new one
        var dstChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, dirtyPage: true);
        var srcChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId);
        new Span<byte>(srcChunk, sizeToCopy).CopyTo(new Span<byte>(dstChunk, sizeToCopy));
    }

    // If conflict + solver exists, record for custom resolution
    if (hasConflict && conflictSolver != null)
    {
        conflictSolver.AddEntry(pk, info, readChunk, committedChunk, committingChunk, toCommitChunk);
    }
    // ... ~100 more lines of commit/index logic
}
```

**Lines to Extract**: ~50 lines of pure conflict detection logic

#### Proposed Interface

```csharp
// New file: src/Typhon.Engine/Database Engine/ConflictDetector.cs

/// <summary>
/// Detects and resolves conflicts during transaction commit.
/// Implements optimistic concurrency control for MVCC.
/// </summary>
public readonly struct ConflictDetector
{
    /// <summary>
    /// Conflict resolution strategies.
    /// </summary>
    public enum Resolution
    {
        /// <summary>No conflict detected.</summary>
        NoConflict,

        /// <summary>Apply "last write wins" - copy forward and overwrite.</summary>
        LastWriteWins,

        /// <summary>Abort the transaction.</summary>
        Abort,

        /// <summary>Custom handler decided the resolution.</summary>
        Custom
    }

    /// <summary>
    /// Information about a detected conflict.
    /// </summary>
    public readonly struct ConflictInfo
    {
        public readonly long PrimaryKey;
        public readonly int OurRevisionIndex;
        public readonly int CommittedRevisionIndex;
        public readonly int ComponentChunkId;
        public readonly bool WasDeleted;

        public ConflictInfo(long pk, int ourIndex, int committedIndex, int chunkId, bool deleted)
        {
            PrimaryKey = pk;
            OurRevisionIndex = ourIndex;
            CommittedRevisionIndex = committedIndex;
            ComponentChunkId = chunkId;
            WasDeleted = deleted;
        }
    }

    /// <summary>
    /// Delegate for custom conflict resolution.
    /// </summary>
    /// <param name="conflict">Information about the conflict</param>
    /// <param name="ourData">Our uncommitted component data</param>
    /// <param name="committedData">The other transaction's committed data</param>
    /// <returns>Resolution strategy</returns>
    public delegate Resolution ConflictHandler<TComp>(
        in ConflictInfo conflict,
        ref TComp ourData,
        ref TComp committedData) where TComp : unmanaged;

    /// <summary>
    /// Checks if there's a conflict for a given entity during build phase.
    /// Matches the actual logic from Transaction.CommitComponent() line 899.
    /// </summary>
    /// <param name="lastCommitRevisionIndex">The LastCommitRevisionIndex from CompRevStorageHeader</param>
    /// <param name="curRevisionIndex">Our transaction's revision index (CurRevisionIndex)</param>
    /// <param name="isBuildPhase">Whether we're in build phase (true) or commit phase (false)</param>
    /// <returns>True if conflict exists and needs new revision</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasConflict(int lastCommitRevisionIndex, int curRevisionIndex, bool isBuildPhase = true)
    {
        // Conflict exists if:
        // 1. We're in build phase (or no solver exists, defaulting to true)
        // 2. AND LastCommitRevisionIndex >= our CurRevisionIndex
        // This means another transaction committed after we created our revision
        return isBuildPhase && (lastCommitRevisionIndex >= curRevisionIndex);
    }

    /// <summary>
    /// Resolves a conflict using the default "last write wins" strategy.
    /// </summary>
    /// <typeparam name="TComp">Component type</typeparam>
    /// <param name="conflict">Conflict information</param>
    /// <param name="ourData">Our data (will be preserved)</param>
    /// <param name="committedData">Other transaction's data (will be overwritten)</param>
    /// <returns>Always returns LastWriteWins</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Resolution ResolveLastWriteWins<TComp>(
        in ConflictInfo conflict,
        ref TComp ourData,
        ref TComp committedData) where TComp : unmanaged
    {
        // No data manipulation needed - we just keep our data
        return Resolution.LastWriteWins;
    }

    /// <summary>
    /// Detects conflicts for a batch of operations.
    /// </summary>
    /// <param name="operations">Operation cache entries</param>
    /// <param name="revisionManager">For reading revision chain state</param>
    /// <param name="conflicts">Output list of detected conflicts</param>
    /// <returns>Number of conflicts detected</returns>
    public static int DetectConflicts<TKey>(
        ReadOnlySpan<OperationCacheEntry<TKey>> operations,
        in RevisionChainManager revisionManager,
        Span<ConflictInfo> conflicts)
    {
        int conflictCount = 0;

        foreach (ref readonly var op in operations)
        {
            var (lastCommit, current) = revisionManager.GetConflictInfo(op.ChainChunkId);

            if (HasConflict(op.ExpectedCommitIndex, lastCommit, op.OurRevisionIndex))
            {
                conflicts[conflictCount++] = new ConflictInfo(
                    op.PrimaryKey,
                    op.OurRevisionIndex,
                    lastCommit,
                    op.ComponentChunkId,
                    op.IsDelete);
            }
        }

        return conflictCount;
    }
}

/// <summary>
/// Entry in operation cache with conflict detection metadata.
/// </summary>
public readonly struct OperationCacheEntry<TKey>
{
    public readonly TKey PrimaryKey;
    public readonly int ChainChunkId;
    public readonly int OurRevisionIndex;
    public readonly int ExpectedCommitIndex;
    public readonly int ComponentChunkId;
    public readonly bool IsDelete;

    // Constructor omitted for brevity
}
```

#### Pros

| Benefit | Description |
|---------|-------------|
| **Focused Testing** | Can test conflict scenarios without full transaction lifecycle |
| **Clear Algorithm** | Conflict detection logic documented in one place |
| **Extensibility** | Easy to add new conflict resolution strategies |
| **Small Scope** | Minimal extraction, low risk |

#### Cons

| Drawback | Mitigation |
|----------|------------|
| Additional indirection | Static methods with inlining |
| Needs coordination with Transaction | Clear API boundary |

#### Refactoring Cost: **LOW**

| Task | Effort |
|------|--------|
| Extract conflict detection code | 1 hour |
| Create ConflictDetector struct | 1 hour |
| Update Transaction.CommitComponent() | 2 hours |
| Write unit tests | 2 hours |
| **Total** | **~6 hours** |

#### Performance Risk: **NONE**

- Static methods with `AggressiveInlining`
- No allocations
- Same code paths as before, just reorganized
- Struct-based ConflictInfo avoids heap allocation

---

### 2.3 RevisionGarbageCollector

#### Purpose
Extract revision cleanup logic into a dedicated type that:
1. Can be tested independently
2. Completes the TODO items for PK index cleanup and component data cleanup
3. Provides clear metrics on cleanup operations

#### Current Code Location

From `Transaction.cs`:
```csharp
// TODO: also cleanup PK index if all comp are of Deleted type
// TODO: Also cleanup comp data chunks
private void GC(ComponentTable table)
{
    // ~100 lines of incomplete cleanup logic
    // Mixed with transaction state checks
}
```

**Lines to Extract**: ~100 lines + new code for TODO fixes

#### Proposed Interface

```csharp
// New file: src/Typhon.Engine/Database Engine/RevisionGarbageCollector.cs

/// <summary>
/// Garbage collector for old component revisions.
/// Cleans up:
/// - Old revision entries (before minTick)
/// - Component data chunks for deleted revisions
/// - Primary key index entries for fully deleted entities
/// - Revision chain chunks when empty
/// </summary>
public struct RevisionGarbageCollector
{
    /// <summary>
    /// Statistics from a GC run.
    /// </summary>
    public readonly struct GCStats
    {
        public readonly int RevisionsRemoved;
        public readonly int ComponentChunksFreed;
        public readonly int RevisionChainsFreed;
        public readonly int PKIndexEntriesRemoved;
        public readonly TimeSpan Duration;

        public GCStats(int revisions, int compChunks, int chains, int pkEntries, TimeSpan duration)
        {
            RevisionsRemoved = revisions;
            ComponentChunksFreed = compChunks;
            RevisionChainsFreed = chains;
            PKIndexEntriesRemoved = pkEntries;
            Duration = duration;
        }

        public override string ToString() =>
            $"GC: {RevisionsRemoved} revisions, {ComponentChunksFreed} chunks, {RevisionChainsFreed} chains, {PKIndexEntriesRemoved} PKs in {Duration.TotalMilliseconds:F2}ms";
    }

    private readonly ChunkBasedSegment _revisionSegment;
    private readonly ChunkBasedSegment _componentSegment;
    private readonly ChunkRandomAccessor _accessor;

    public RevisionGarbageCollector(
        ChunkBasedSegment revisionSegment,
        ChunkBasedSegment componentSegment,
        ChunkRandomAccessor accessor)
    {
        _revisionSegment = revisionSegment;
        _componentSegment = componentSegment;
        _accessor = accessor;
    }

    /// <summary>
    /// Cleans up revisions older than minTick from a single revision chain.
    /// </summary>
    /// <param name="chainChunkId">Root chunk of revision chain</param>
    /// <param name="minTick">Minimum tick to keep (older revisions are removed)</param>
    /// <param name="alsoFreeComponentData">If true, frees component data chunks</param>
    /// <returns>Cleanup statistics</returns>
    public GCStats CleanupChain(int chainChunkId, long minTick, bool alsoFreeComponentData = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int revisionsRemoved = 0;
        int chunksFreed = 0;
        int chainsFreed = 0;

        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);

        // Find revisions older than minTick
        int oldestToKeep = -1;
        for (int i = 0; i < header.ItemCount; i++)
        {
            ref var element = ref GetElement(ref header, i);
            if (element.DateTime.ToTick() >= minTick)
            {
                oldestToKeep = i;
                break;
            }

            // This revision can be removed
            revisionsRemoved++;

            // Free component data chunk if requested (fixes TODO)
            if (alsoFreeComponentData && element.ComponentChunkId != 0)
            {
                _componentSegment.FreeChunk(element.ComponentChunkId);
                chunksFreed++;
            }
        }

        if (oldestToKeep > 0)
        {
            // Compact the chain - shift revisions to beginning
            CompactChain(ref header, oldestToKeep);

            // Free any overflow chunks that are now empty
            chainsFreed = FreeEmptyOverflowChunks(ref header);
        }

        sw.Stop();
        return new GCStats(revisionsRemoved, chunksFreed, chainsFreed, 0, sw.Elapsed);
    }

    /// <summary>
    /// Checks if an entity is fully deleted (all revisions are deleted).
    /// If so, the PK index entry can be removed (fixes TODO).
    /// </summary>
    /// <param name="chainChunkId">Root chunk of revision chain</param>
    /// <returns>True if entity is fully deleted and PK entry can be removed</returns>
    public bool IsFullyDeleted(int chainChunkId)
    {
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);

        // Check if all remaining revisions are marked as deleted
        for (int i = 0; i < header.ItemCount; i++)
        {
            ref var element = ref GetElement(ref header, i);
            if (!element.IsDeleted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Frees all resources for a fully deleted entity.
    /// Call after removing from PK index.
    /// </summary>
    /// <param name="chainChunkId">Root chunk of revision chain</param>
    /// <returns>Number of chunks freed</returns>
    public int FreeDeletedEntity(int chainChunkId)
    {
        int chunksFreed = 0;

        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(chainChunkId);

        // Free all component data chunks
        for (int i = 0; i < header.ItemCount; i++)
        {
            ref var element = ref GetElement(ref header, i);
            if (element.ComponentChunkId != 0)
            {
                _componentSegment.FreeChunk(element.ComponentChunkId);
                chunksFreed++;
            }
        }

        // Free all revision chain chunks (overflow first, then root)
        int currentChunk = chainChunkId;
        while (currentChunk != 0)
        {
            ref var chunk = ref _accessor.GetChunk<CompRevStorageHeader>(currentChunk);
            int nextChunk = chunk.NextChunkId;
            _revisionSegment.FreeChunk(currentChunk);
            chunksFreed++;
            currentChunk = nextChunk;
        }

        return chunksFreed;
    }

    /// <summary>
    /// Performs full GC across all entities in a component table.
    /// </summary>
    /// <param name="pkIndex">Primary key index to iterate</param>
    /// <param name="minTick">Minimum tick to keep</param>
    /// <returns>Total cleanup statistics</returns>
    public GCStats FullGC(L64BTree pkIndex, long minTick)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int totalRevisions = 0;
        int totalChunks = 0;
        int totalChains = 0;
        int totalPKs = 0;

        var keysToRemove = new List<long>();

        // Iterate all entities
        foreach (var entry in pkIndex.Enumerate())
        {
            var chainChunkId = entry.Value;
            var stats = CleanupChain(chainChunkId, minTick, alsoFreeComponentData: true);

            totalRevisions += stats.RevisionsRemoved;
            totalChunks += stats.ComponentChunksFreed;
            totalChains += stats.RevisionChainsFreed;

            // Check if entity is fully deleted (fixes TODO)
            if (IsFullyDeleted(chainChunkId))
            {
                keysToRemove.Add(entry.Key);
            }
        }

        // Remove PK index entries for fully deleted entities (fixes TODO)
        foreach (var pk in keysToRemove)
        {
            if (pkIndex.TryGet(pk, out var chainChunkId))
            {
                pkIndex.Remove(pk);
                FreeDeletedEntity(chainChunkId);
                totalPKs++;
            }
        }

        sw.Stop();
        return new GCStats(totalRevisions, totalChunks, totalChains, totalPKs, sw.Elapsed);
    }

    // Private helpers
    private ref CompRevStorageElement GetElement(ref CompRevStorageHeader header, int index) { /* ... */ }
    private void CompactChain(ref CompRevStorageHeader header, int startIndex) { /* ... */ }
    private int FreeEmptyOverflowChunks(ref CompRevStorageHeader header) { /* ... */ }
}
```

#### Pros

| Benefit | Description |
|---------|-------------|
| **Completes TODOs** | Finally implements PK index cleanup and component data cleanup |
| **Observable** | GCStats provides metrics for monitoring |
| **Testable** | Can verify cleanup behavior with specific scenarios |
| **Predictable** | Isolated from transaction state, deterministic behavior |

#### Cons

| Drawback | Mitigation |
|----------|------------|
| Need to coordinate with Transaction | Clear trigger points |
| List allocation in FullGC | Consider pooling or span-based approach |

#### Refactoring Cost: **MEDIUM**

| Task | Effort |
|------|--------|
| Extract existing GC code | 2 hours |
| Implement PK index cleanup (TODO) | 2 hours |
| Implement component data cleanup (TODO) | 2 hours |
| Write unit tests | 3 hours |
| Integration with Transaction | 2 hours |
| **Total** | **~11 hours** |

#### Performance Risk: **LOW**

- GC runs asynchronously or at end of transactions
- Not on hot path
- Can add rate limiting if needed
- GCStats helps monitor impact

---

### 2.4 TransactionOperationCache

#### Purpose
Extract the operation caching dictionaries and their management from `Transaction` into a dedicated cache type.

#### Current Code Location

From `Transaction.cs`:
```csharp
// Current: Multiple dictionaries scattered throughout
private class ComponentCache<TComp>
{
    public Dictionary<long, TComp> Creates = new();
    public Dictionary<long, TComp> Reads = new();
    public Dictionary<long, TComp> Updates = new();
    public HashSet<long> Deletes = new();
}

private Dictionary<Type, object> _componentCaches = new();
```

**Lines to Extract**: ~60 lines + cache management logic

#### Proposed Interface

```csharp
// New file: src/Typhon.Engine/Database Engine/TransactionOperationCache.cs

/// <summary>
/// Caches component operations within a transaction for:
/// - Read-your-own-writes (uncommitted reads)
/// - Batch commit processing
/// - Rollback support
/// </summary>
public sealed class TransactionOperationCache : IDisposable
{
    /// <summary>
    /// Operation types tracked by the cache.
    /// </summary>
    public enum OperationType : byte
    {
        Create,
        Read,
        Update,
        Delete
    }

    /// <summary>
    /// Cached operation with metadata.
    /// </summary>
    public readonly struct CachedOperation<TComp> where TComp : unmanaged
    {
        public readonly long PrimaryKey;
        public readonly OperationType Type;
        public readonly int RevisionChainChunkId;
        public readonly int OurRevisionIndex;
        public readonly int ExpectedCommitIndex;
        public readonly TComp Component;

        public CachedOperation(
            long pk,
            OperationType type,
            int chainChunkId,
            int revIndex,
            int commitIndex,
            TComp component)
        {
            PrimaryKey = pk;
            Type = type;
            RevisionChainChunkId = chainChunkId;
            OurRevisionIndex = revIndex;
            ExpectedCommitIndex = commitIndex;
            Component = component;
        }
    }

    /// <summary>
    /// Type-specific cache using pooled dictionaries.
    /// </summary>
    private sealed class TypedCache<TComp> where TComp : unmanaged
    {
        // Pooled dictionaries to reduce allocations
        private static readonly ObjectPool<Dictionary<long, CachedOperation<TComp>>> DictPool =
            new(() => new Dictionary<long, CachedOperation<TComp>>());

        private Dictionary<long, CachedOperation<TComp>> _operations;

        public TypedCache()
        {
            _operations = DictPool.Rent();
        }

        public void Add(long pk, in CachedOperation<TComp> operation)
        {
            _operations[pk] = operation;
        }

        public bool TryGet(long pk, out CachedOperation<TComp> operation)
        {
            return _operations.TryGetValue(pk, out operation);
        }

        public bool Contains(long pk) => _operations.ContainsKey(pk);

        public IEnumerable<CachedOperation<TComp>> GetAll() => _operations.Values;

        public IEnumerable<CachedOperation<TComp>> GetByType(OperationType type)
        {
            foreach (var op in _operations.Values)
            {
                if (op.Type == type) yield return op;
            }
        }

        public int Count => _operations.Count;

        public void Clear()
        {
            _operations.Clear();
        }

        public void Return()
        {
            _operations.Clear();
            DictPool.Return(_operations);
            _operations = null;
        }
    }

    private readonly Dictionary<Type, object> _caches = new();

    /// <summary>
    /// Gets or creates a typed cache for a component type.
    /// </summary>
    public TypedCache<TComp> GetCache<TComp>() where TComp : unmanaged
    {
        var type = typeof(TComp);
        if (!_caches.TryGetValue(type, out var cache))
        {
            cache = new TypedCache<TComp>();
            _caches[type] = cache;
        }
        return (TypedCache<TComp>)cache;
    }

    /// <summary>
    /// Records a Create operation.
    /// </summary>
    public void RecordCreate<TComp>(
        long pk,
        ref TComp component,
        int chainChunkId,
        int revisionIndex) where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        cache.Add(pk, new CachedOperation<TComp>(
            pk,
            OperationType.Create,
            chainChunkId,
            revisionIndex,
            -1, // No expected commit for creates
            component));
    }

    /// <summary>
    /// Records a Read operation (for read-your-own-writes tracking).
    /// </summary>
    public void RecordRead<TComp>(
        long pk,
        ref TComp component,
        int chainChunkId,
        int revisionIndex,
        int expectedCommitIndex) where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        // Only record if not already in cache (Create/Update takes precedence)
        if (!cache.Contains(pk))
        {
            cache.Add(pk, new CachedOperation<TComp>(
                pk,
                OperationType.Read,
                chainChunkId,
                revisionIndex,
                expectedCommitIndex,
                component));
        }
    }

    /// <summary>
    /// Records an Update operation.
    /// </summary>
    public void RecordUpdate<TComp>(
        long pk,
        ref TComp component,
        int chainChunkId,
        int revisionIndex,
        int expectedCommitIndex) where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        cache.Add(pk, new CachedOperation<TComp>(
            pk,
            OperationType.Update,
            chainChunkId,
            revisionIndex,
            expectedCommitIndex,
            component));
    }

    /// <summary>
    /// Records a Delete operation.
    /// </summary>
    public void RecordDelete<TComp>(
        long pk,
        int chainChunkId,
        int revisionIndex,
        int expectedCommitIndex) where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        cache.Add(pk, new CachedOperation<TComp>(
            pk,
            OperationType.Delete,
            chainChunkId,
            revisionIndex,
            expectedCommitIndex,
            default));
    }

    /// <summary>
    /// Tries to read from cache (for read-your-own-writes).
    /// </summary>
    public bool TryReadFromCache<TComp>(long pk, out TComp component) where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        if (cache.TryGet(pk, out var op) && op.Type != OperationType.Delete)
        {
            component = op.Component;
            return true;
        }
        component = default;
        return false;
    }

    /// <summary>
    /// Checks if entity was deleted in this transaction.
    /// </summary>
    public bool WasDeleted<TComp>(long pk) where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        return cache.TryGet(pk, out var op) && op.Type == OperationType.Delete;
    }

    /// <summary>
    /// Gets all operations that need to be committed.
    /// </summary>
    public IEnumerable<CachedOperation<TComp>> GetOperationsToCommit<TComp>() where TComp : unmanaged
    {
        var cache = GetCache<TComp>();
        foreach (var op in cache.GetAll())
        {
            // Skip reads - only mutations need commit
            if (op.Type != OperationType.Read)
            {
                yield return op;
            }
        }
    }

    /// <summary>
    /// Clears all caches (for rollback or after commit).
    /// </summary>
    public void Clear()
    {
        foreach (var cache in _caches.Values)
        {
            // Use reflection or interface to call Clear
            var clearMethod = cache.GetType().GetMethod("Clear");
            clearMethod?.Invoke(cache, null);
        }
    }

    public void Dispose()
    {
        foreach (var cache in _caches.Values)
        {
            // Return pooled dictionaries
            var returnMethod = cache.GetType().GetMethod("Return");
            returnMethod?.Invoke(cache, null);
        }
        _caches.Clear();
    }
}
```

#### Pros

| Benefit | Description |
|---------|-------------|
| **Clear Separation** | Cache management separate from business logic |
| **Pooling** | Dictionary pooling reduces GC pressure |
| **Testable** | Can verify cache behavior independently |
| **Metrics Ready** | Easy to add cache hit/miss statistics |

#### Cons

| Drawback | Mitigation |
|----------|------------|
| Additional type | Clear responsibility boundary |
| Slight memory overhead | Pooling mitigates |
| Reflection in Dispose | Consider interface-based approach |

#### Refactoring Cost: **MEDIUM**

| Task | Effort |
|------|--------|
| Create TransactionOperationCache | 3 hours |
| Update Transaction to use cache | 3 hours |
| Add pooling support | 2 hours |
| Write unit tests | 2 hours |
| **Total** | **~10 hours** |

#### Performance Risk: **LOW**

- Dictionary access patterns unchanged
- Pooling may improve performance
- Inlined accessors
- Same algorithmic complexity

---

### 2.5 SecondaryIndexManager

#### Purpose
Extract secondary index management from `ComponentTable` into a dedicated type that handles:
- Index creation based on schema
- Index updates during commit
- Index queries

#### Current Code Location

From `ComponentTable.cs`:
```csharp
// Current: Index management scattered
private Dictionary<int, IBTree> _fieldIndexes;

private void CreateIndexes(DBComponentDefinition def)
{
    foreach (var field in def.FieldsByName.Values)
    {
        if (field.HasIndex)
        {
            var indexSegment = new ChunkBasedSegment(...);
            var index = CreateBTreeForFieldType(field);
            _fieldIndexes[field.FieldId] = index;
        }
    }
}

// Index updates in Transaction.CommitComponent() - mixed with other logic
```

**Lines to Extract**: ~80 lines from ComponentTable + ~40 lines from Transaction

#### Proposed Interface

```csharp
// New file: src/Typhon.Engine/Database Engine/SecondaryIndexManager.cs

/// <summary>
/// Manages secondary indexes for a component type.
/// Handles index creation, updates, and queries.
/// </summary>
public sealed class SecondaryIndexManager<TComp> : IDisposable where TComp : unmanaged
{
    /// <summary>
    /// Index entry for batch updates.
    /// </summary>
    public readonly struct IndexUpdate
    {
        public readonly int FieldId;
        public readonly long OldValue;
        public readonly long NewValue;
        public readonly long PrimaryKey;
        public readonly bool IsRemoval;

        public IndexUpdate(int fieldId, long oldValue, long newValue, long pk, bool isRemoval)
        {
            FieldId = fieldId;
            OldValue = oldValue;
            NewValue = newValue;
            PrimaryKey = pk;
            IsRemoval = isRemoval;
        }
    }

    private readonly Dictionary<int, IBTree> _indexes;
    private readonly Dictionary<int, DBComponentDefinition.Field> _indexedFields;
    private readonly ManagedPagedMMF _storage;
    private readonly int _componentStride;

    public SecondaryIndexManager(
        ManagedPagedMMF storage,
        DBComponentDefinition definition)
    {
        _storage = storage;
        _componentStride = definition.ComponentStorageTotalSize;
        _indexes = new Dictionary<int, IBTree>();
        _indexedFields = new Dictionary<int, DBComponentDefinition.Field>();

        // Create indexes for all indexed fields
        foreach (var field in definition.FieldsByName.Values)
        {
            if (field.HasIndex && !field.IsStatic)
            {
                CreateIndex(field);
            }
        }
    }

    /// <summary>
    /// Gets the number of indexed fields.
    /// </summary>
    public int IndexCount => _indexes.Count;

    /// <summary>
    /// Checks if a field has an index.
    /// </summary>
    public bool HasIndex(int fieldId) => _indexes.ContainsKey(fieldId);

    /// <summary>
    /// Creates an index for a field.
    /// </summary>
    private void CreateIndex(DBComponentDefinition.Field field)
    {
        var segment = new ChunkBasedSegment(
            _storage,
            GetIndexNodeSize(field.Type),
            $"SecIdx_{field.Name}");

        var accessor = segment.GetAccessor();
        IBTree index = field.Type switch
        {
            FieldType.Short or FieldType.UShort => new L16BTree(segment, accessor, false, field.IndexAllowMultiple),
            FieldType.Int or FieldType.UInt or FieldType.Float => new L32BTree(segment, accessor, false, field.IndexAllowMultiple),
            FieldType.Long or FieldType.ULong or FieldType.Double => new L64BTree(segment, accessor, false, field.IndexAllowMultiple),
            FieldType.String64 => new String64BTree(segment, accessor, false, field.IndexAllowMultiple),
            _ => throw new NotSupportedException($"Field type {field.Type} does not support indexing")
        };

        _indexes[field.FieldId] = index;
        _indexedFields[field.FieldId] = field;
    }

    /// <summary>
    /// Extracts the index key from a component for a given field.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe long ExtractKey(ref TComp component, int fieldId)
    {
        var field = _indexedFields[fieldId];
        fixed (TComp* ptr = &component)
        {
            byte* fieldPtr = (byte*)ptr + field.OffsetInComponentStorage;

            return field.Type switch
            {
                FieldType.Short => *(short*)fieldPtr,
                FieldType.UShort => *(ushort*)fieldPtr,
                FieldType.Int => *(int*)fieldPtr,
                FieldType.UInt => *(uint*)fieldPtr,
                FieldType.Float => BitConverter.SingleToInt32Bits(*(float*)fieldPtr),
                FieldType.Long => *(long*)fieldPtr,
                FieldType.ULong => (long)*(ulong*)fieldPtr,
                FieldType.Double => BitConverter.DoubleToInt64Bits(*(double*)fieldPtr),
                _ => throw new NotSupportedException()
            };
        }
    }

    /// <summary>
    /// Adds an entry to an index.
    /// </summary>
    public void Add(int fieldId, long key, long primaryKey)
    {
        if (_indexes.TryGetValue(fieldId, out var index))
        {
            // Cast key to appropriate type based on index
            index.Add(key, (int)primaryKey);
        }
    }

    /// <summary>
    /// Removes an entry from an index.
    /// </summary>
    public void Remove(int fieldId, long key, long primaryKey)
    {
        if (_indexes.TryGetValue(fieldId, out var index))
        {
            if (index.AllowMultiple)
            {
                index.RemoveValue(key, (int)primaryKey);
            }
            else
            {
                index.Remove(key);
            }
        }
    }

    /// <summary>
    /// Queries an index for a single value.
    /// </summary>
    public bool TryGet(int fieldId, long key, out long primaryKey)
    {
        primaryKey = 0;
        if (!_indexes.TryGetValue(fieldId, out var index))
            return false;

        if (index.TryGet(key, out var value))
        {
            primaryKey = value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Queries an index for multiple values.
    /// </summary>
    public ReadOnlySpan<int> GetMultiple(int fieldId, long key)
    {
        if (!_indexes.TryGetValue(fieldId, out var index))
            return ReadOnlySpan<int>.Empty;

        return index.GetMultiple(key);
    }

    /// <summary>
    /// Computes index updates needed for a component change.
    /// </summary>
    public void ComputeUpdates(
        ref TComp oldComponent,
        ref TComp newComponent,
        long primaryKey,
        Span<IndexUpdate> updates,
        out int updateCount)
    {
        updateCount = 0;

        foreach (var kvp in _indexedFields)
        {
            var fieldId = kvp.Key;
            var oldKey = ExtractKey(ref oldComponent, fieldId);
            var newKey = ExtractKey(ref newComponent, fieldId);

            if (oldKey != newKey)
            {
                updates[updateCount++] = new IndexUpdate(
                    fieldId,
                    oldKey,
                    newKey,
                    primaryKey,
                    isRemoval: false);
            }
        }
    }

    /// <summary>
    /// Applies a batch of index updates.
    /// </summary>
    public void ApplyUpdates(ReadOnlySpan<IndexUpdate> updates)
    {
        foreach (ref readonly var update in updates)
        {
            if (update.IsRemoval)
            {
                Remove(update.FieldId, update.OldValue, update.PrimaryKey);
            }
            else
            {
                // Update = Remove old + Add new
                if (update.OldValue != 0)
                {
                    Remove(update.FieldId, update.OldValue, update.PrimaryKey);
                }
                Add(update.FieldId, update.NewValue, update.PrimaryKey);
            }
        }
    }

    /// <summary>
    /// Gets index for direct access (for advanced queries).
    /// </summary>
    public IBTree GetIndex(int fieldId)
    {
        return _indexes.TryGetValue(fieldId, out var index) ? index : null;
    }

    private static int GetIndexNodeSize(FieldType type)
    {
        return type switch
        {
            FieldType.Short or FieldType.UShort => L16BTree.NodeSize,
            FieldType.Int or FieldType.UInt or FieldType.Float => L32BTree.NodeSize,
            FieldType.Long or FieldType.ULong or FieldType.Double => L64BTree.NodeSize,
            FieldType.String64 => String64BTree.NodeSize,
            _ => throw new NotSupportedException()
        };
    }

    public void Dispose()
    {
        // Note: IBTree implementations may need IDisposable
        _indexes.Clear();
        _indexedFields.Clear();
    }
}
```

#### Pros

| Benefit | Description |
|---------|-------------|
| **Focused Responsibility** | All index logic in one place |
| **Batch Updates** | ComputeUpdates enables efficient batch processing |
| **Testable** | Can test index behavior without full ComponentTable |
| **Type Safety** | Generic parameter ensures type consistency |

#### Cons

| Drawback | Mitigation |
|----------|------------|
| Another type in the chain | Clear ownership in ComponentTable |
| Unsafe code for field extraction | Contained in single method, well-tested |

#### Refactoring Cost: **MEDIUM**

| Task | Effort |
|------|--------|
| Create SecondaryIndexManager | 3 hours |
| Move index creation from ComponentTable | 2 hours |
| Update Transaction commit for index updates | 3 hours |
| Write unit tests | 3 hours |
| **Total** | **~11 hours** |

#### Performance Risk: **LOW**

- Same B+Tree implementations underneath
- Batch update pattern may improve performance
- Unsafe code is already in use
- Field extraction uses same memory layout

---

## 3. Test Strategy

### 3.1 New Test Structure

```
test/Typhon.Engine.Tests/
├── Unit/                                    # NEW: Pure unit tests (no disk I/O)
│   ├── RevisionChainManagerTests.cs         # Tests for 2.1
│   ├── ConflictDetectorTests.cs             # Tests for 2.2
│   ├── RevisionGarbageCollectorTests.cs     # Tests for 2.3
│   ├── TransactionOperationCacheTests.cs    # Tests for 2.4
│   └── SecondaryIndexManagerTests.cs        # Tests for 2.5
│
├── Integration/                             # NEW: Component integration tests
│   ├── RevisionChain+GCTests.cs             # RevisionChainManager + GC
│   ├── Transaction+CacheTests.cs            # Transaction + OperationCache
│   └── ComponentTable+IndexTests.cs         # ComponentTable + SecondaryIndexManager
│
├── E2E/                                     # EXISTING: Full stack tests (renamed)
│   ├── TransactionTests.cs                  # (moved from Database Engine/)
│   ├── BTreeTests.cs
│   └── ComponentCollectionTests.cs
│
├── TestDoubles/                             # NEW: In-memory implementations
│   ├── InMemoryChunkStorage.cs
│   ├── InMemoryIndex.cs
│   └── TestChunkAccessor.cs
│
├── TestBase.cs                              # EXISTING
├── UnitTestBase.cs                          # NEW: Base for unit tests
└── IntegrationTestBase.cs                   # NEW: Base for integration tests
```

### 3.2 Unit Tests for Each New Type

#### 3.2.1 RevisionChainManager Tests

```csharp
// test/Typhon.Engine.Tests/Unit/RevisionChainManagerTests.cs

[TestFixture]
public class RevisionChainManagerTests : UnitTestBase
{
    private InMemoryChunkStorage _revisionStorage;
    private InMemoryChunkStorage _componentStorage;
    private RevisionChainManager _manager;

    [SetUp]
    public void Setup()
    {
        _revisionStorage = new InMemoryChunkStorage(CompRevStorageHeader.Size);
        _componentStorage = new InMemoryChunkStorage(64); // CompA size
        _manager = new RevisionChainManager(
            _revisionStorage.AsSegment(),
            _componentStorage.AsSegment(),
            _revisionStorage.CreateAccessor());
    }

    // Basic Creation Tests
    [Test]
    public void CreateRevisionChain_NewEntity_ReturnsValidChunkId()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: true);

        Assert.That(chainId, Is.GreaterThan(0));
    }

    [Test]
    public void CreateRevisionChain_IsolatedTrue_SetsIsolationFlag()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: true);

        // Verify internal state
        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        Assert.That(header.GetElement(0).IsolationFlag, Is.EqualTo(1));
    }

    [Test]
    public void CreateRevisionChain_IsolatedFalse_SetsCommitIndex()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: false);

        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        Assert.That(header.LastCommitRevisionIndex, Is.EqualTo(0));
    }

    // Revision Addition Tests
    [Test]
    public void AddRevision_SingleAdd_IncrementsItemCount()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: false);

        _manager.AddRevision(chainId, tick: 200, isolated: false);

        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        Assert.That(header.ItemCount, Is.EqualTo(2));
    }

    [Test]
    public void AddRevision_CircularBufferWrap_HandlesCorrectly()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: false);

        // Add enough revisions to wrap the circular buffer
        for (int i = 0; i < CompRevStorageHeader.MaxElements; i++)
        {
            _manager.AddRevision(chainId, tick: 100 + (i + 1) * 10, isolated: false);
        }

        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        Assert.That(header.ChainLength, Is.GreaterThan(1)); // Should have overflow
    }

    // Visibility Tests (MVCC)
    [Test]
    public void TryGetVisibleRevision_IsolatedRevision_NotVisibleToOtherTransactions()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: true);

        var visible = _manager.TryGetVisibleRevision(
            chainId,
            transactionTick: 200,
            currentTransactionId: null, // Different transaction
            out _,
            out _);

        Assert.That(visible, Is.False);
    }

    [Test]
    public void TryGetVisibleRevision_CommittedRevision_VisibleToLaterTicks()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: false);

        var visible = _manager.TryGetVisibleRevision(
            chainId,
            transactionTick: 200,
            currentTransactionId: null,
            out var resultChunkId,
            out _);

        Assert.That(visible, Is.True);
        Assert.That(resultChunkId, Is.EqualTo(compChunkId));
    }

    [Test]
    public void TryGetVisibleRevision_OlderTick_FindsOlderRevision()
    {
        var comp1 = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(comp1, tick: 100, isolated: false);

        var (_, comp2) = _manager.AddRevision(chainId, tick: 200, isolated: false);

        // Transaction at tick 150 should see revision 1, not revision 2
        _manager.TryGetVisibleRevision(chainId, transactionTick: 150, null, out var resultChunkId, out _);

        Assert.That(resultChunkId, Is.EqualTo(comp1));
    }

    // Commit Tests
    [Test]
    public void CommitRevision_ClearsIsolationFlag()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: true);

        _manager.CommitRevision(chainId, revisionIndex: 0);

        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        Assert.That(header.GetElement(0).IsolationFlag, Is.EqualTo(0));
    }

    [Test]
    public void CommitRevision_UpdatesLastCommitIndex()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: true);

        _manager.CommitRevision(chainId, revisionIndex: 0);

        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        Assert.That(header.LastCommitRevisionIndex, Is.EqualTo(0));
    }

    // Conflict Detection Info
    [Test]
    public void GetConflictInfo_ReturnsCorrectIndices()
    {
        var compChunkId = _componentStorage.AllocateChunk(true);
        var chainId = _manager.CreateRevisionChain(compChunkId, tick: 100, isolated: false);
        _manager.AddRevision(chainId, tick: 200, isolated: false);

        var (lastCommit, current) = _manager.GetConflictInfo(chainId);

        Assert.That(lastCommit, Is.EqualTo(1)); // Second revision committed
        Assert.That(current, Is.EqualTo(1));    // Current is index 1
    }
}
```

#### 3.2.2 ConflictDetector Tests

```csharp
// test/Typhon.Engine.Tests/Unit/ConflictDetectorTests.cs

[TestFixture]
public class ConflictDetectorTests
{
    // Basic Conflict Detection
    [Test]
    public void HasConflict_NoChange_ReturnsFalse()
    {
        var result = ConflictDetector.HasConflict(
            expectedCommitIndex: 5,
            currentCommitIndex: 5,
            ourRevisionIndex: 6);

        Assert.That(result, Is.False);
    }

    [Test]
    public void HasConflict_OtherTransactionCommitted_ReturnsTrue()
    {
        var result = ConflictDetector.HasConflict(
            expectedCommitIndex: 5,
            currentCommitIndex: 6,  // Someone else committed
            ourRevisionIndex: 7);

        Assert.That(result, Is.True);
    }

    [Test]
    public void HasConflict_OurOwnCommit_ReturnsFalse()
    {
        var result = ConflictDetector.HasConflict(
            expectedCommitIndex: 5,
            currentCommitIndex: 6,
            ourRevisionIndex: 6);  // It was us who committed

        Assert.That(result, Is.False);
    }

    // Resolution Strategy Tests
    [Test]
    public void ResolveLastWriteWins_AlwaysReturnsLastWriteWins()
    {
        var conflict = new ConflictDetector.ConflictInfo(
            pk: 123,
            ourIndex: 7,
            committedIndex: 6,
            chunkId: 42,
            deleted: false);

        var ourData = new CompA(100);
        var committedData = new CompA(200);

        var result = ConflictDetector.ResolveLastWriteWins(
            in conflict,
            ref ourData,
            ref committedData);

        Assert.That(result, Is.EqualTo(ConflictDetector.Resolution.LastWriteWins));
        Assert.That(ourData.A, Is.EqualTo(100)); // Our data preserved
    }

    // Batch Detection Tests
    [Test]
    public void DetectConflicts_NoConflicts_ReturnsZero()
    {
        var operations = new[]
        {
            CreateOperation(pk: 1, expectedCommit: 5, chainChunkId: 10),
            CreateOperation(pk: 2, expectedCommit: 3, chainChunkId: 20),
        };

        var mockManager = CreateMockRevisionManager(new Dictionary<int, (int lastCommit, int current)>
        {
            [10] = (5, 6),  // No conflict
            [20] = (3, 4),  // No conflict
        });

        var conflicts = new ConflictDetector.ConflictInfo[10];
        var count = ConflictDetector.DetectConflicts(operations, mockManager, conflicts);

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void DetectConflicts_OneConflict_ReturnsOne()
    {
        var operations = new[]
        {
            CreateOperation(pk: 1, expectedCommit: 5, chainChunkId: 10),
            CreateOperation(pk: 2, expectedCommit: 3, chainChunkId: 20),
        };

        var mockManager = CreateMockRevisionManager(new Dictionary<int, (int lastCommit, int current)>
        {
            [10] = (6, 7),  // Conflict! Expected 5, but 6 was committed
            [20] = (3, 4),  // No conflict
        });

        var conflicts = new ConflictDetector.ConflictInfo[10];
        var count = ConflictDetector.DetectConflicts(operations, mockManager, conflicts);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(conflicts[0].PrimaryKey, Is.EqualTo(1));
    }

    // Edge Cases
    [Test]
    public void HasConflict_FirstCommitOnNewEntity_ReturnsFalse()
    {
        var result = ConflictDetector.HasConflict(
            expectedCommitIndex: -1,  // New entity, no expected
            currentCommitIndex: -1,   // Still uncommitted
            ourRevisionIndex: 0);

        Assert.That(result, Is.False);
    }

    // Helper methods
    private static OperationCacheEntry<long> CreateOperation(long pk, int expectedCommit, int chainChunkId)
    {
        return new OperationCacheEntry<long>(pk, chainChunkId, 0, expectedCommit, 0, false);
    }

    private static RevisionChainManager CreateMockRevisionManager(
        Dictionary<int, (int lastCommit, int current)> conflictInfo)
    {
        // Use InMemoryChunkStorage to create a manager with pre-configured state
        // Implementation details...
        throw new NotImplementedException("Use proper test double");
    }
}
```

#### 3.2.3 RevisionGarbageCollector Tests

```csharp
// test/Typhon.Engine.Tests/Unit/RevisionGarbageCollectorTests.cs

[TestFixture]
public class RevisionGarbageCollectorTests : UnitTestBase
{
    private InMemoryChunkStorage _revisionStorage;
    private InMemoryChunkStorage _componentStorage;
    private RevisionChainManager _chainManager;
    private RevisionGarbageCollector _gc;

    [SetUp]
    public void Setup()
    {
        _revisionStorage = new InMemoryChunkStorage(CompRevStorageHeader.Size);
        _componentStorage = new InMemoryChunkStorage(64);
        _chainManager = new RevisionChainManager(
            _revisionStorage.AsSegment(),
            _componentStorage.AsSegment(),
            _revisionStorage.CreateAccessor());
        _gc = new RevisionGarbageCollector(
            _revisionStorage.AsSegment(),
            _componentStorage.AsSegment(),
            _revisionStorage.CreateAccessor());
    }

    // Basic Cleanup Tests
    [Test]
    public void CleanupChain_OldRevisions_RemovesOlderThanMinTick()
    {
        // Create chain with 3 revisions at ticks 100, 200, 300
        var comp1 = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp1, tick: 100, isolated: false);
        _chainManager.AddRevision(chainId, tick: 200, isolated: false);
        _chainManager.AddRevision(chainId, tick: 300, isolated: false);

        // Cleanup with minTick=250 should remove first two
        var stats = _gc.CleanupChain(chainId, minTick: 250);

        Assert.That(stats.RevisionsRemoved, Is.EqualTo(2));
    }

    [Test]
    public void CleanupChain_ComponentDataCleanup_FreesChunks()
    {
        var comp1 = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp1, tick: 100, isolated: false);
        var (_, comp2) = _chainManager.AddRevision(chainId, tick: 200, isolated: false);

        var initialChunkCount = _componentStorage.AllocatedCount;

        var stats = _gc.CleanupChain(chainId, minTick: 150, alsoFreeComponentData: true);

        Assert.That(stats.ComponentChunksFreed, Is.EqualTo(1));
        Assert.That(_componentStorage.AllocatedCount, Is.LessThan(initialChunkCount));
    }

    // Fully Deleted Entity Tests
    [Test]
    public void IsFullyDeleted_AllRevisionsDeleted_ReturnsTrue()
    {
        var comp = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp, tick: 100, isolated: false);

        // Mark as deleted
        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        header.GetElement(0).SetDeleted(true);

        var result = _gc.IsFullyDeleted(chainId);

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsFullyDeleted_SomeRevisionsNotDeleted_ReturnsFalse()
    {
        var comp = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp, tick: 100, isolated: false);
        _chainManager.AddRevision(chainId, tick: 200, isolated: false);

        // Only mark second as deleted
        ref var header = ref _revisionStorage.GetChunk<CompRevStorageHeader>(chainId);
        header.GetElement(1).SetDeleted(true);

        var result = _gc.IsFullyDeleted(chainId);

        Assert.That(result, Is.False);
    }

    // FreeDeletedEntity Tests
    [Test]
    public void FreeDeletedEntity_FreesAllChunks()
    {
        var comp = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp, tick: 100, isolated: false);

        var initialRevChunks = _revisionStorage.AllocatedCount;
        var initialCompChunks = _componentStorage.AllocatedCount;

        var chunksFreed = _gc.FreeDeletedEntity(chainId);

        Assert.That(chunksFreed, Is.EqualTo(2)); // 1 rev chunk + 1 comp chunk
        Assert.That(_revisionStorage.AllocatedCount, Is.LessThan(initialRevChunks));
        Assert.That(_componentStorage.AllocatedCount, Is.LessThan(initialCompChunks));
    }

    // GCStats Tests
    [Test]
    public void CleanupChain_ReturnsAccurateStats()
    {
        var comp = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp, tick: 100, isolated: false);

        var stats = _gc.CleanupChain(chainId, minTick: 50); // Nothing to clean

        Assert.That(stats.RevisionsRemoved, Is.EqualTo(0));
        Assert.That(stats.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    // Edge Cases
    [Test]
    public void CleanupChain_EmptyChain_NoException()
    {
        // This shouldn't happen in practice, but handle gracefully
        var chainId = _revisionStorage.AllocateChunk(true);
        // Don't initialize - simulates corruption

        Assert.DoesNotThrow(() => _gc.CleanupChain(chainId, minTick: 100));
    }

    [Test]
    public void CleanupChain_OverflowChunks_CleansEntireChain()
    {
        var comp = _componentStorage.AllocateChunk(true);
        var chainId = _chainManager.CreateRevisionChain(comp, tick: 100, isolated: false);

        // Add enough to cause overflow
        for (int i = 0; i < CompRevStorageHeader.MaxElements + 5; i++)
        {
            _chainManager.AddRevision(chainId, tick: 100 + (i + 1) * 10, isolated: false);
        }

        var stats = _gc.CleanupChain(chainId, minTick: 100000, alsoFreeComponentData: true);

        // Should clean all revisions
        Assert.That(stats.RevisionsRemoved, Is.GreaterThan(CompRevStorageHeader.MaxElements));
    }
}
```

#### 3.2.4 TransactionOperationCache Tests

```csharp
// test/Typhon.Engine.Tests/Unit/TransactionOperationCacheTests.cs

[TestFixture]
public class TransactionOperationCacheTests
{
    private TransactionOperationCache _cache;

    [SetUp]
    public void Setup()
    {
        _cache = new TransactionOperationCache();
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    // Basic Operations
    [Test]
    public void RecordCreate_NewEntry_StoredInCache()
    {
        var comp = new CompA(42);
        _cache.RecordCreate(pk: 1, ref comp, chainChunkId: 10, revisionIndex: 0);

        var found = _cache.TryReadFromCache<CompA>(pk: 1, out var result);

        Assert.That(found, Is.True);
        Assert.That(result.A, Is.EqualTo(42));
    }

    [Test]
    public void RecordUpdate_OverwritesPreviousCreate()
    {
        var comp1 = new CompA(42);
        var comp2 = new CompA(100);

        _cache.RecordCreate(pk: 1, ref comp1, chainChunkId: 10, revisionIndex: 0);
        _cache.RecordUpdate(pk: 1, ref comp2, chainChunkId: 10, revisionIndex: 1, expectedCommitIndex: 0);

        _cache.TryReadFromCache<CompA>(pk: 1, out var result);

        Assert.That(result.A, Is.EqualTo(100));
    }

    // Read-Your-Own-Writes
    [Test]
    public void TryReadFromCache_DeletedEntity_ReturnsFalse()
    {
        var comp = new CompA(42);
        _cache.RecordCreate(pk: 1, ref comp, chainChunkId: 10, revisionIndex: 0);
        _cache.RecordDelete<CompA>(pk: 1, chainChunkId: 10, revisionIndex: 1, expectedCommitIndex: 0);

        var found = _cache.TryReadFromCache<CompA>(pk: 1, out _);

        Assert.That(found, Is.False);
    }

    [Test]
    public void WasDeleted_DeletedEntity_ReturnsTrue()
    {
        _cache.RecordDelete<CompA>(pk: 1, chainChunkId: 10, revisionIndex: 0, expectedCommitIndex: -1);

        var wasDeleted = _cache.WasDeleted<CompA>(pk: 1);

        Assert.That(wasDeleted, Is.True);
    }

    // Operation Enumeration
    [Test]
    public void GetOperationsToCommit_ExcludesReads()
    {
        var comp = new CompA(42);
        _cache.RecordCreate(pk: 1, ref comp, chainChunkId: 10, revisionIndex: 0);
        _cache.RecordRead(pk: 2, ref comp, chainChunkId: 20, revisionIndex: 0, expectedCommitIndex: 0);
        _cache.RecordUpdate(pk: 3, ref comp, chainChunkId: 30, revisionIndex: 1, expectedCommitIndex: 0);

        var operations = _cache.GetOperationsToCommit<CompA>().ToList();

        Assert.That(operations, Has.Count.EqualTo(2)); // Create + Update, not Read
        Assert.That(operations.Any(o => o.PrimaryKey == 2), Is.False);
    }

    // Type Isolation
    [Test]
    public void DifferentComponentTypes_IsolatedCaches()
    {
        var compA = new CompA(42);
        var compB = new CompB(100, 1.5f);

        _cache.RecordCreate(pk: 1, ref compA, chainChunkId: 10, revisionIndex: 0);
        _cache.RecordCreate(pk: 1, ref compB, chainChunkId: 20, revisionIndex: 0);

        _cache.TryReadFromCache<CompA>(pk: 1, out var resultA);
        _cache.TryReadFromCache<CompB>(pk: 1, out var resultB);

        Assert.That(resultA.A, Is.EqualTo(42));
        Assert.That(resultB.A, Is.EqualTo(100));
    }

    // Clear/Reset
    [Test]
    public void Clear_RemovesAllEntries()
    {
        var comp = new CompA(42);
        _cache.RecordCreate(pk: 1, ref comp, chainChunkId: 10, revisionIndex: 0);

        _cache.Clear();

        var found = _cache.TryReadFromCache<CompA>(pk: 1, out _);
        Assert.That(found, Is.False);
    }

    // Pooling Behavior
    [Test]
    public void Dispose_ReturnsDictionariesToPool()
    {
        var comp = new CompA(42);
        _cache.RecordCreate(pk: 1, ref comp, chainChunkId: 10, revisionIndex: 0);

        Assert.DoesNotThrow(() => _cache.Dispose());
    }

    // Edge Cases
    [Test]
    public void TryReadFromCache_NotInCache_ReturnsFalse()
    {
        var found = _cache.TryReadFromCache<CompA>(pk: 999, out _);
        Assert.That(found, Is.False);
    }

    [Test]
    public void RecordRead_DoesNotOverwriteCreate()
    {
        var comp1 = new CompA(42);
        var comp2 = new CompA(100);

        _cache.RecordCreate(pk: 1, ref comp1, chainChunkId: 10, revisionIndex: 0);
        _cache.RecordRead(pk: 1, ref comp2, chainChunkId: 10, revisionIndex: 0, expectedCommitIndex: 0);

        _cache.TryReadFromCache<CompA>(pk: 1, out var result);

        Assert.That(result.A, Is.EqualTo(42)); // Create value preserved
    }
}
```

#### 3.2.5 SecondaryIndexManager Tests

```csharp
// test/Typhon.Engine.Tests/Unit/SecondaryIndexManagerTests.cs

[TestFixture]
public class SecondaryIndexManagerTests : TestBase<SecondaryIndexManagerTests>
{
    // Note: SecondaryIndexManager needs real B+Tree which needs segments
    // These are integration-level tests

    private SecondaryIndexManager<CompD> _indexManager;
    private DatabaseEngine _dbe;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(_dbe);

        var table = _dbe.GetOrCreateComponentTable<CompD>();
        // Extract or create index manager from table
        // _indexManager = table.IndexManager; // After refactoring
    }

    // Index Creation Tests
    [Test]
    public void Constructor_CreatesIndexesForIndexedFields()
    {
        // CompD has 3 indexed fields: A (float), B (int), C (double)
        Assert.That(_indexManager.IndexCount, Is.EqualTo(3));
    }

    [Test]
    public void HasIndex_IndexedField_ReturnsTrue()
    {
        // Field B is indexed
        var fieldId = GetFieldId<CompD>("B");
        Assert.That(_indexManager.HasIndex(fieldId), Is.True);
    }

    // Key Extraction Tests
    [Test]
    public void ExtractKey_IntField_ReturnsCorrectValue()
    {
        var comp = new CompD(1.5f, 42, 3.14);
        var fieldId = GetFieldId<CompD>("B");

        var key = _indexManager.ExtractKey(ref comp, fieldId);

        Assert.That(key, Is.EqualTo(42));
    }

    [Test]
    public void ExtractKey_FloatField_ReturnsBitPattern()
    {
        var comp = new CompD(1.5f, 42, 3.14);
        var fieldId = GetFieldId<CompD>("A");

        var key = _indexManager.ExtractKey(ref comp, fieldId);

        Assert.That(key, Is.EqualTo(BitConverter.SingleToInt32Bits(1.5f)));
    }

    // Add/Remove Tests
    [Test]
    public void Add_NewEntry_CanBeRetrieved()
    {
        var fieldId = GetFieldId<CompD>("B");
        _indexManager.Add(fieldId, key: 42, primaryKey: 100);

        var found = _indexManager.TryGet(fieldId, key: 42, out var pk);

        Assert.That(found, Is.True);
        Assert.That(pk, Is.EqualTo(100));
    }

    [Test]
    public void Remove_ExistingEntry_NoLongerFound()
    {
        var fieldId = GetFieldId<CompD>("B");
        _indexManager.Add(fieldId, key: 42, primaryKey: 100);

        _indexManager.Remove(fieldId, key: 42, primaryKey: 100);

        var found = _indexManager.TryGet(fieldId, key: 42, out _);
        Assert.That(found, Is.False);
    }

    // Multiple Value Index Tests (AllowMultiple = true)
    [Test]
    public void GetMultiple_MultipleEntriesForSameKey_ReturnsAll()
    {
        var fieldId = GetFieldId<CompD>("A"); // AllowMultiple = true

        _indexManager.Add(fieldId, key: 100, primaryKey: 1);
        _indexManager.Add(fieldId, key: 100, primaryKey: 2);
        _indexManager.Add(fieldId, key: 100, primaryKey: 3);

        var results = _indexManager.GetMultiple(fieldId, key: 100);

        Assert.That(results.Length, Is.EqualTo(3));
    }

    // Batch Update Tests
    [Test]
    public void ComputeUpdates_FieldChanged_ReturnsUpdate()
    {
        var oldComp = new CompD(1.5f, 42, 3.14);
        var newComp = new CompD(1.5f, 100, 3.14); // B changed

        var updates = new SecondaryIndexManager<CompD>.IndexUpdate[10];
        _indexManager.ComputeUpdates(ref oldComp, ref newComp, pk: 1, updates, out var count);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(updates[0].OldValue, Is.EqualTo(42));
        Assert.That(updates[0].NewValue, Is.EqualTo(100));
    }

    [Test]
    public void ComputeUpdates_NoChange_ReturnsEmpty()
    {
        var comp = new CompD(1.5f, 42, 3.14);

        var updates = new SecondaryIndexManager<CompD>.IndexUpdate[10];
        _indexManager.ComputeUpdates(ref comp, ref comp, pk: 1, updates, out var count);

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void ApplyUpdates_BatchUpdates_AppliesAll()
    {
        var fieldId = GetFieldId<CompD>("B");
        _indexManager.Add(fieldId, key: 42, primaryKey: 1);

        var updates = new[]
        {
            new SecondaryIndexManager<CompD>.IndexUpdate(fieldId, oldValue: 42, newValue: 100, pk: 1, isRemoval: false)
        };

        _indexManager.ApplyUpdates(updates);

        var foundOld = _indexManager.TryGet(fieldId, key: 42, out _);
        var foundNew = _indexManager.TryGet(fieldId, key: 100, out var pk);

        Assert.That(foundOld, Is.False);
        Assert.That(foundNew, Is.True);
        Assert.That(pk, Is.EqualTo(1));
    }

    // Helper
    private static int GetFieldId<T>(string fieldName)
    {
        // Use reflection or DatabaseDefinitions to get field ID
        throw new NotImplementedException("Use actual schema lookup");
    }
}
```

### 3.3 Test Infrastructure Additions

#### 3.3.1 InMemoryChunkStorage

```csharp
// test/Typhon.Engine.Tests/TestDoubles/InMemoryChunkStorage.cs

/// <summary>
/// In-memory implementation of chunk storage for unit testing.
/// No disk I/O, fast allocation/deallocation.
/// </summary>
public sealed class InMemoryChunkStorage : IDisposable
{
    private readonly int _stride;
    private readonly List<byte[]> _chunks;
    private readonly Queue<int> _freeList;
    private int _allocatedCount;

    public InMemoryChunkStorage(int stride)
    {
        _stride = stride;
        _chunks = new List<byte[]> { new byte[stride] }; // Chunk 0 is null
        _freeList = new Queue<int>();
        _allocatedCount = 0;
    }

    public int Stride => _stride;
    public int AllocatedCount => _allocatedCount;
    public int TotalChunks => _chunks.Count;

    public int AllocateChunk(bool clear)
    {
        if (_freeList.TryDequeue(out var id))
        {
            if (clear) Array.Clear(_chunks[id]);
            _allocatedCount++;
            return id;
        }

        id = _chunks.Count;
        _chunks.Add(new byte[_stride]);
        _allocatedCount++;
        return id;
    }

    public void FreeChunk(int chunkId)
    {
        if (chunkId > 0 && chunkId < _chunks.Count)
        {
            _freeList.Enqueue(chunkId);
            _allocatedCount--;
        }
    }

    public ref T GetChunk<T>(int chunkId) where T : unmanaged
    {
        return ref MemoryMarshal.AsRef<T>(_chunks[chunkId]);
    }

    public Span<byte> GetChunkSpan(int chunkId) => _chunks[chunkId];

    /// <summary>
    /// Returns a wrapper that can be used where ChunkBasedSegment is expected.
    /// </summary>
    public ChunkBasedSegmentMock AsSegment() => new(this);

    public InMemoryChunkAccessor CreateAccessor() => new(this);

    public void Dispose()
    {
        _chunks.Clear();
        _freeList.Clear();
    }
}

/// <summary>
/// Mock that implements the same interface as ChunkBasedSegment.
/// </summary>
public readonly struct ChunkBasedSegmentMock
{
    private readonly InMemoryChunkStorage _storage;

    public ChunkBasedSegmentMock(InMemoryChunkStorage storage) => _storage = storage;

    public int Stride => _storage.Stride;
    public int AllocateChunk(bool clear) => _storage.AllocateChunk(clear);
    public void FreeChunk(int chunkId) => _storage.FreeChunk(chunkId);
}

/// <summary>
/// In-memory chunk accessor.
/// </summary>
public sealed class InMemoryChunkAccessor : IDisposable
{
    private readonly InMemoryChunkStorage _storage;

    public InMemoryChunkAccessor(InMemoryChunkStorage storage) => _storage = storage;

    public ref T GetChunk<T>(int chunkId) where T : unmanaged
        => ref _storage.GetChunk<T>(chunkId);

    public Span<byte> GetChunkSpan(int chunkId) => _storage.GetChunkSpan(chunkId);

    public void SetDirty(int chunkId) { /* No-op for in-memory */ }

    public void Dispose() { }
}
```

#### 3.3.2 UnitTestBase

```csharp
// test/Typhon.Engine.Tests/UnitTestBase.cs

/// <summary>
/// Base class for pure unit tests.
/// No dependency injection, no disk I/O.
/// </summary>
[TestFixture]
public abstract class UnitTestBase
{
    protected Random Rand { get; } = new(123456789);

    protected InMemoryChunkStorage CreateChunkStorage(int stride)
        => new(stride);

    protected static int CompRevStorageSize => CompRevStorageHeader.Size;
    protected static int DefaultComponentSize => 64;
}
```

---

## 4. Implementation Roadmap

### Phase 1: Foundation (No Breaking Changes)

**Goals**: Create new types alongside existing code, enable new tests

| Step | Task | Effort | Dependencies |
|------|------|-------:|--------------|
| 1.1 | Create test infrastructure (InMemoryChunkStorage, UnitTestBase) | 4h | None |
| 1.2 | Create ConflictDetector (static, no refactoring needed) | 3h | 1.1 |
| 1.3 | Write ConflictDetector unit tests | 2h | 1.2 |
| 1.4 | Create RevisionChainManager struct | 6h | 1.1 |
| 1.5 | Write RevisionChainManager unit tests | 4h | 1.4 |

**Deliverable**: New types working alongside existing code, new unit tests passing

### Phase 2: Extraction (Careful Refactoring)

**Goals**: Wire up new types, remove duplicated code

| Step | Task | Effort | Dependencies |
|------|------|-------:|--------------|
| 2.1 | Refactor Transaction to use ConflictDetector | 2h | 1.3 |
| 2.2 | Refactor Transaction to use RevisionChainManager | 4h | 1.5, 2.1 |
| 2.3 | Create TransactionOperationCache | 4h | 2.2 |
| 2.4 | Refactor Transaction to use OperationCache | 3h | 2.3 |
| 2.5 | Create RevisionGarbageCollector (fix TODOs) | 6h | 2.2 |
| 2.6 | Refactor Transaction GC to use new GC type | 2h | 2.5 |

**Deliverable**: Transaction.cs reduced from ~1000 to ~400 lines, all existing tests passing

### Phase 3: ComponentTable Refactoring

**Goals**: Extract SecondaryIndexManager, simplify ComponentTable

| Step | Task | Effort | Dependencies |
|------|------|-------:|--------------|
| 3.1 | Create SecondaryIndexManager | 4h | 2.6 |
| 3.2 | Write SecondaryIndexManager tests | 3h | 3.1 |
| 3.3 | Refactor ComponentTable to use SecondaryIndexManager | 3h | 3.2 |
| 3.4 | Update Transaction commit to use SecondaryIndexManager | 2h | 3.3 |

**Deliverable**: ComponentTable.cs reduced from ~800 to ~400 lines

### Phase 4: Validation

**Goals**: Ensure no performance regression, update documentation

| Step | Task | Effort | Dependencies |
|------|------|-------:|--------------|
| 4.1 | Run benchmark suite, compare results | 2h | 3.4 |
| 4.2 | Profile memory allocations | 2h | 4.1 |
| 4.3 | Optimize if regressions found | Variable | 4.2 |
| 4.4 | Update CLAUDE.md with new architecture | 2h | 4.3 |
| 4.5 | Add inline documentation | 2h | 4.4 |

**Total Estimated Effort**: ~60 hours

### Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Performance regression | Benchmark at each phase, struct-based types |
| Breaking existing tests | Refactor incrementally, run full test suite after each step |
| Scope creep | Strict phase gates, don't add features |
| Incomplete TODO fixes | Phase 2.5 specifically addresses GC TODOs |

---

## 5. Appendix: Architecture Diagrams

### 5.1 Current vs Proposed Architecture

```
CURRENT ARCHITECTURE                    PROPOSED ARCHITECTURE
═══════════════════                    ═════════════════════

┌─────────────────────┐                ┌─────────────────────┐
│   DatabaseEngine    │                │   DatabaseEngine    │
└─────────┬───────────┘                └─────────┬───────────┘
          │                                      │
          ▼                                      ▼
┌─────────────────────┐                ┌─────────────────────┐
│    Transaction      │                │    Transaction      │
│ ┌─────────────────┐ │                │ (orchestration only)│
│ │ State mgmt      │ │                └─────────┬───────────┘
│ │ Op caching      │ │                          │
│ │ Revision logic  │ │                    ┌─────┼────────┬─────────────┐
│ │ Commit logic    │ │                    │     │        │             │
│ │ Rollback logic  │ │                    ▼     ▼        ▼             ▼
│ │ GC triggering   │ │          ┌─────────┴─┐ ┌─┴────┐ ┌──────────┐ ┌─────────┐
│ └─────────────────┘ │          │  Conflict │ │ Op   │ │ Revision │ │ GC      │
└─────────┬───────────┘          │  Detector │ │ Cache│ │ Chain    │ │         │
          │                      └───────────┘ └──────┘ │ Manager  │ └─────────┘
          ▼                                             └──────┬───┘
┌─────────────────────┐                                        │
│   ComponentTable    │                ┌─────────────────────┐ │
│ ┌─────────────────┐ │                │   ComponentTable    │ │
│ │ Segments        │ │                │ (storage only)      │◄┘
│ │ PK Index        │ │                └─────────┬───────────┘
│ │ Secondary Idx   │ │                          │
│ │ Schema access   │ │                    ┌─────┴─────┐
│ │ Rev storage     │ │                    │           │
│ └─────────────────┘ │                    ▼           ▼
└─────────────────────┘          ┌─────────────┐ ┌───────────────┐
                                 │ Secondary   │ │ PK Index      │
                                 │ Index Mgr   │ │ (L64BTree)    │
                                 └─────────────┘ └───────────────┘
```

### 5.2 Data Flow: Transaction Commit

```
                              COMMIT SEQUENCE
                              ══════════════

Transaction.Commit()
         │
         ▼
┌────────────────────────────────────────────────────────────────────┐
│  1. Get operations from TransactionOperationCache                  │
│     └── cache.GetOperationsToCommit<TComp>()                       │
└────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────────────┐
│  2. Detect conflicts using ConflictDetector                        │
│     └── ConflictDetector.DetectConflicts(operations, revMgr, ...)  │
│     └── For each conflict: resolve or abort                        │
└────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────────────┐
│  3. Commit revisions via RevisionChainManager                      │
│     └── For each operation:                                        │
│         └── revisionManager.CommitRevision(chainId, revIndex)      │
└────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────────────┐
│  4. Update secondary indexes via SecondaryIndexManager             │
│     └── indexManager.ComputeUpdates(old, new, pk, updates)         │
│     └── indexManager.ApplyUpdates(updates)                         │
└────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────────────┐
│  5. Clear operation cache                                          │
│     └── cache.Clear()                                              │
└────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────────────┐
│  6. Optionally trigger GC via RevisionGarbageCollector             │
│     └── If minTick advanced: gc.FullGC(pkIndex, minTick)           │
└────────────────────────────────────────────────────────────────────┘
```

### 5.3 Test Coverage Matrix

```
                          Unit    Integration   E2E
                          ═════   ═══════════   ═══
RevisionChainManager       ██████████  ████████    ████████████████
ConflictDetector           ██████████  ████        ████████████████
RevisionGarbageCollector   ██████████  ████████    ████████████████
TransactionOperationCache  ██████████  ████        ████████████████
SecondaryIndexManager      ████        ██████████  ████████████████
Transaction                            ████████    ████████████████
ComponentTable                         ██████████  ████████████████
DatabaseEngine                                     ████████████████
BTree                                  ██████████  ████████████████
PagedMMF                                           ████████████████

Legend: ████ = Covered   (blank) = Not Applicable
```

---

## Summary

This redesign proposes extracting 5 concrete types from the monolithic `Transaction` and `ComponentTable` classes:

| Type | Source | Lines Extracted | Refactoring Cost | Perf Risk |
|------|--------|---------------:|-----------------|-----------|
| RevisionChainManager | Transaction + ComponentTable | ~260 | Medium-High | Low |
| ConflictDetector | Transaction | ~50 | Low | None |
| RevisionGarbageCollector | Transaction | ~100+ | Medium | Low |
| TransactionOperationCache | Transaction | ~60 | Medium | Low |
| SecondaryIndexManager | ComponentTable | ~80 | Medium | Low |

**Total estimated effort**: ~60 hours

**Benefits**:
- 50+ new unit tests covering core algorithms
- TODOs fixed (PK index cleanup, component data cleanup)
- Clearer code organization
- Faster test execution (in-memory tests)
- Same runtime performance (struct-based, inlined)

**Not included** (per user decision):
- PageCache extraction from PagedMMF (high performance risk)
