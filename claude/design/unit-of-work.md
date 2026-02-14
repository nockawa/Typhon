# Unit of Work (Tier 4) — Design

**Date:** 2026-02-14
**Status:** In progress
**GitHub Issue:** #47
**Branch:** `feature/47-unit-of-work`
**Related:**
- [Execution System](../overview/02-execution.md) §2.1–2.3
- [ADR-001: Three-Tier API Hierarchy](../adr/001-three-tier-api-hierarchy.md)
- [ADR-005: Durability Mode Per UoW](../adr/005-durability-mode-per-uow.md)
- [ADR-027: Even-Sized Hot-Path Structs](../adr/027-even-sized-hot-path-structs.md) (pre-approves 12-byte CompRevStorageElement)
- [ADR-034: UnitOfWorkContext Struct Design](../adr/034-unitofworkcontext-struct-design.md)
- [UoW Crash Recovery](../ideas/uow-crash-recovery/) (4-part series)

> **Terminology note:** Typhon has two orthogonal concepts that were both originally called "epoch". **Resource epoch** (`EpochManager.GlobalEpoch`, 64-bit) protects page cache from eviction during active operations — this retains the "epoch" name (industry-standard EBRM term). **UoW ID** (`UnitOfWorkContext.UowId`, 15-bit) identifies crash-recovery boundaries and stamps revisions — renamed from "epoch" to "UoW ID" to eliminate ambiguity. They are completely independent systems.

> **TL;DR — The missing middle tier of the three-tier API hierarchy (DatabaseEngine → UoW → Transaction).** The UoW is the durability boundary for user operations, batching multiple transactions for efficient persistence while maintaining atomicity guarantees on crash recovery. Each UoW is assigned an epoch from the UoW Registry, which stamps all revisions created within its scope. On crash, pending epochs are voided — their revisions become instantly invisible without WAL replay.

---

## Table of Contents

1. [Motivation](#1-motivation)
2. [Sub-Issue Breakdown](#2-sub-issue-breakdown)
3. [#48 — DurabilityMode Enum + State Machine Types](#3-48--durabilitymode-enum--state-machine-types)
4. [#49 — UnitOfWork Class](#4-49--unitofwork-class)
5. [#50 — Epoch Stamping in CompRevStorageElement](#5-50--epoch-stamping-in-comprevstorageelement)
6. [#51 — UoW Registry](#6-51--uow-registry)
7. [#52 — Resource Back-Pressure](#7-52--resource-back-pressure)
8. [API Migration](#8-api-migration)
9. [Implementation Order](#9-implementation-order)
10. [Files to Create/Modify](#10-files-to-createmodify)
11. [Testing Strategy](#11-testing-strategy)
12. [Design Decisions](#12-design-decisions)
13. [Open Questions](#13-open-questions)

---

## 1. Motivation

Typhon currently exposes `DatabaseEngine.CreateTransaction()` as a direct API. This bypasses the durability boundary — every transaction is logically independent with no control over *when* changes become crash-safe.

The three-tier API hierarchy ([ADR-001](../adr/001-three-tier-api-hierarchy.md)) requires an intermediate layer:

```
DatabaseEngine (setup & schema)
    └── UnitOfWork (durability boundary, lifetime scope)
            └── Transaction (MVCC operations, conflict resolution)
```

**Why this matters now:**
- The foundation is complete: `UnitOfWorkContext` (24-byte struct), `Deadline`, `DeadlineWatchdog`, yield points, holdoff regions, and `Transaction.Commit(ref UnitOfWorkContext)` are all implemented (Tiers 1–3).
- The UoW is a prerequisite for the WAL (#53), which needs epoch-based durability modes.
- Without epoch stamping, crash recovery cannot distinguish which revisions belong to which logical operation.

**What this Tier does NOT include:**
- WAL Writer / ring buffer (Tier 5, #53)
- Checkpoint Manager (future)
- Actual FUA writes — durability modes are defined but `Deferred` is the only *functional* mode until WAL lands

---

## 2. Sub-Issue Breakdown

| # | Title | Dependency | Scope |
|---|-------|-----------|-------|
| **#48** | DurabilityMode enum + UoW state machine types | None | Type definitions only |
| **#49** | UnitOfWork class — lifecycle, transaction ownership, API migration | #48 | Core class + API change |
| **#50** | Epoch stamping in CompRevStorageElement | #48 | Storage layout change (10 → 12 bytes) |
| **#51** | UoW Registry — persistent segment for crash-safe epoch tracking | #48, #50 | New persistent data structure |
| **#52** | Resource back-pressure at UoW entry | #49 | Admission control gate |

```
#48 ──→ #49 ──→ #52
  │
  ├────→ #50 ──→ #51
  │
  └────→ (all others depend on types)
```

---

## 3. #48 — DurabilityMode Enum + State Machine Types

**File:** `src/Typhon.Engine/Execution/DurabilityMode.cs` (new)

### DurabilityMode

```csharp
/// <summary>
/// Controls when WAL records become crash-safe.
/// Specified per Unit of Work at creation time.
/// </summary>
public enum DurabilityMode : byte
{
    /// <summary>
    /// WAL records buffered. Durable only after explicit Flush()/FlushAsync().
    /// Commit latency: ~1-2µs. Data-at-risk: until Flush().
    /// Best for: game ticks, batch imports, simulation steps.
    /// </summary>
    Deferred = 0,

    /// <summary>
    /// WAL writer auto-flushes every N ms (default 5ms).
    /// Commit latency: ~1-2µs. Data-at-risk: ≤ GroupCommitInterval.
    /// Best for: general server workload, request handlers.
    /// </summary>
    GroupCommit = 1,

    /// <summary>
    /// FUA on every tx.Commit(). Blocks until WAL record is on stable media.
    /// Commit latency: ~15-85µs. Data-at-risk: zero.
    /// Best for: financial trades, irreversible state changes.
    /// </summary>
    Immediate = 2,
}
```

### DurabilityOverride

```csharp
/// <summary>
/// Per-transaction override for durability. Can only escalate (never downgrade).
/// </summary>
public enum DurabilityOverride : byte
{
    /// <summary>Use the owning UoW's DurabilityMode.</summary>
    Default = 0,

    /// <summary>Force FUA for this specific commit (escalation only).</summary>
    Immediate = 1,
}
```

### UnitOfWorkState

```csharp
/// <summary>
/// State machine for UoW lifecycle. Transitions are one-way.
/// </summary>
/// <remarks>
/// Pending → WalDurable → Committed → Recyclable (normal path)
/// Pending → Void → Recyclable (crash recovery path)
/// </remarks>
public enum UnitOfWorkState : byte
{
    /// <summary>Created, transactions may be in progress. WAL records volatile.</summary>
    Pending = 0,

    /// <summary>WAL flush complete (FUA). Survives crash. Pages may still be dirty.</summary>
    WalDurable = 1,

    /// <summary>Data pages checkpointed. WAL segments recyclable.</summary>
    Committed = 2,

    /// <summary>Epoch can be reused by a new UoW.</summary>
    Recyclable = 3,

    /// <summary>Crash recovery: epoch was Pending at crash time. All revisions invisible.</summary>
    Void = 4,
}
```

**Notes:**
- Initially only `Deferred` is meaningful (no WAL yet), but the full enum is defined for API stability.
- `UnitOfWorkState` is stored in the UoW Registry (§6) and in the in-memory `UnitOfWork` object.

---

## 4. #49 — UnitOfWork Class

**File:** `src/Typhon.Engine/Execution/UnitOfWork.cs` (new)

### Class Design

```csharp
public sealed class UnitOfWork : IDisposable
{
    private readonly DatabaseEngine _dbe;
    private readonly DurabilityMode _durabilityMode;
    private readonly ushort _uowId;
    private UnitOfWorkState _state;
    private int _transactionCount;
    private int _committedTransactionCount;
    private bool _disposed;

    // Cancellation infrastructure — UoW owns the CTS
    private readonly CancellationTokenSource _cts;
    private readonly Deadline _deadline;

    // Properties
    public DurabilityMode DurabilityMode => _durabilityMode;
    public UnitOfWorkState State => _state;
    public ushort UowId => _uowId;
    public int TransactionCount => _transactionCount;
    public int CommittedTransactionCount => _committedTransactionCount;
    public bool IsDisposed => _disposed;

    // UnitOfWorkContext is created on demand for each transaction
    // (the struct is 24 bytes, cheap to create)

    public Transaction CreateTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != UnitOfWorkState.Pending)
            throw new InvalidOperationException($"Cannot create transaction: UoW is {_state}");

        Interlocked.Increment(ref _transactionCount);
        var tx = _dbe.TransactionChain.CreateTransaction(_dbe);
        // Associate transaction with this UoW's epoch
        tx.SetUnitOfWork(this);
        return tx;
    }

    /// <summary>
    /// Creates a UnitOfWorkContext for use with Transaction.Commit(ref ctx).
    /// Composes the UoW's deadline with the provided timeout.
    /// </summary>
    public UnitOfWorkContext CreateContext(TimeSpan timeout = default)
    {
        var effectiveDeadline = timeout == default
            ? _deadline
            : Deadline.Min(_deadline, Deadline.FromTimeout(timeout));

        return new UnitOfWorkContext(effectiveDeadline, _cts.Token, _uowId);
    }

    // Flush (no-op until WAL is implemented in Tier 5)
    public void Flush()
    {
        // Future: signal WAL writer, wait for FUA
        // For now: transition state directly
        if (_state == UnitOfWorkState.Pending)
            _state = UnitOfWorkState.WalDurable;
    }

    public Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel any outstanding operations
        _cts.Cancel();
        _cts.Dispose();

        // Return epoch to registry
        // (future: _dbe.UowRegistry.Release(_uowId))

        _state = UnitOfWorkState.Recyclable;
    }
}
```

### DatabaseEngine Changes

```csharp
// NEW method
public UnitOfWork CreateUnitOfWork(
    DurabilityMode durabilityMode = DurabilityMode.Deferred,
    TimeSpan timeout = default)
{
    // timeout == default → use configured default UoW timeout
    var effectiveTimeout = timeout == default
        ? TimeoutOptions.Current.DefaultUowTimeout  // New option
        : timeout;

    // Future: back-pressure check (#52)
    // Future: allocate epoch from registry (#51)

    return new UnitOfWork(this, durabilityMode, uowId: 0, effectiveTimeout);
}

// EXISTING method — mark as obsolete, then remove
[Obsolete("Use CreateUnitOfWork().CreateTransaction() instead")]
public Transaction CreateTransaction() => TransactionChain.CreateTransaction(this);
```

### Transaction Changes

The `Transaction` class holds a **persistent reference** to its owning UoW. This gives the transaction access to epoch ID and durability mode at commit time, and prepares for WAL integration (Tier 5) where `Commit()` needs the durability mode to decide FUA vs buffer.

```csharp
// New internal field (8 bytes per pooled Transaction — negligible)
internal UnitOfWork OwningUnitOfWork { get; private set; }
```

**Init() signature change:**
```csharp
// Current: public void Init(DatabaseEngine dbe, long tsn)
// New:     public void Init(DatabaseEngine dbe, long tsn, UnitOfWork uow = null)
//          OwningUnitOfWork = uow;
```

**Reset() addition:**
```csharp
// In Reset(): OwningUnitOfWork = null;
```

**UoW ID stamping in Commit():** After conflict detection succeeds, the commit path stamps all pending revision elements with `OwningUnitOfWork?.UowId ?? 0`:

```csharp
// In CommitComponent(), after writing the revision element:
element.UowId = OwningUnitOfWork?.UowId ?? 0;
```

**Epoch 0 semantics (decided):** Epoch 0 is a reserved sentinel meaning "no UoW" / "always committed." The deprecated `CreateTransaction()` produces transactions with `OwningUnitOfWork = null`, epoch 0. The visibility check treats epoch 0 as implicitly committed (no registry lookup). The registry never allocates epoch 0 — allocation starts at 1.

---

## 5. #50 — Epoch Stamping in CompRevStorageElement

**File:** `src/Typhon.Engine/Data/ComponentTable.cs` (modify `CompRevStorageElement`)

### Current Layout (10 bytes)

```
Offset  Size  Field
  0      4    ComponentChunkId
  4      4    _packedTickHigh     (upper 32 bits of TSN)
  8      2    _packedTickLow      (lower 15 bits of TSN + bit 0 = IsolationFlag)
─────────────
 10 bytes total
```

**Current chunk capacity** (64-byte chunks):
- Root chunk: 20-byte `CompRevStorageHeader` → (64 − 20) / 10 = **4 elements**
- Overflow chunks: no header → 64 / 10 = **6 elements** (4 bytes wasted)

> The `CompRevStorageHeader` (20 bytes) contains: `NextChunkId` (4), `AccessControlSmall` (4), `FirstItemRevision` (4), `FirstItemIndex` (2), `ItemCount` (2), `ChainLength` (2), `LastCommitRevisionIndex` (2).

### New Layout (12 bytes)

ComponentChunkId stays at offset 0 to preserve 4-byte natural alignment (cache-friendly access pattern for the most-frequently-read field). The new `_packedEpoch` field is appended at offset 10.

```
Offset  Size  Field
  0      4    ComponentChunkId                              (4-byte aligned ✓)
  4      4    _packedTickHigh     (upper 32 bits of TSN)
  8      2    _packedTickLow      (full 16 bits of TSN — bit 0 freed from IsolationFlag)
 10      2    _packedUowId        (bits 0-14: UowId, bit 15: IsolationFlag)
─────────────
 12 bytes total (Pack=2, divisible by 4 per ADR-027)
```

**New chunk capacity:**
- Root chunk: (64 − 20) / 12 = **3 elements** (8 bytes wasted)
- Overflow chunks: 64 / 12 = **5 elements** (4 bytes wasted)

### Field Details

**`_packedEpoch` (2 bytes, offset 10):**
- Bits 0–14: UoW Epoch ID (15 bits → max 32,767 concurrent UoWs)
- Bit 15: IsolationFlag (moved from `_packedTickLow` bit 0)

**TSN precision gain:** By moving the IsolationFlag to `_packedEpoch`, bit 0 of `_packedTickLow` is freed. The TSN now uses the full 48 bits (was 47 effective bits). This eliminates the `safeCutoff = MinTSN & ~1L` masking in `Transaction.cs` line ~1108.

### Storage Density Impact

| Metric | Root Chunk (Before → After) | Overflow Chunks (Before → After) |
|--------|----------------------------|----------------------------------|
| Element size | 10 → 12 bytes | 10 → 12 bytes |
| Elements per chunk | 4 → **3** (−25%) | 6 → **5** (−17%) |
| Wasted bytes | 4 → 8 bytes | 4 → 4 bytes |

The density reduction means revision chains take more chunks. For typical workloads (short revision chains of 1–3 entries), the impact is minimal — most entities fit within the root chunk. Entities with longer revision chains (>3 revisions) will overflow to a second chunk slightly earlier.

**Dynamic capacity calculation** — `ComponentRevisionManager.CompRevCountInRoot` and `CompRevCountInNext` are computed from `sizeof(CompRevStorageElement)` at runtime, so no hardcoded constants need updating. Only comments need correction.

### Visibility Check (Updated)

A revision is visible to a reading transaction if:

```csharp
bool IsVisible(ref CompRevStorageElement rev, long readerTSN, ushort readerEpoch)
{
    // 1. Must not be isolated (uncommitted)
    if (rev.IsolationFlag)
    {
        // Exception: same epoch can see its own uncommitted revisions
        return rev.UowId == readerUowId;
    }

    // 2. Epoch must be committed (or epoch 0 = legacy/always committed)
    if (!_dbe.IsUowVisible(rev.UowId))
        return false;

    // 3. TSN must be ≤ reader's snapshot
    return rev.TSN <= readerTSN;
}
```

**Epoch visibility stub** (added in #50, before registry exists):

```csharp
// In DatabaseEngine:
internal bool IsUowVisible(ushort uowId) =>
    uowId == 0 || _uowRegistry?.IsCommitted(uowId) ?? true;
```

This returns `true` for epoch 0 (sentinel) and `true` for all other epochs until the registry (#51) is wired in. When #51 lands, the `_uowRegistry.IsCommitted()` path activates naturally — no visibility code changes needed.

### Migration

Existing data files have 10-byte `CompRevStorageElement` entries. A file format version bump is required. Migration strategy:

- **Option A (chosen):** Bump file format version. Old files cannot be opened without migration.
- **Option B:** In-place migration on first open (read 10-byte, write 12-byte). Risky if interrupted.

Since Typhon is pre-1.0 with no production data, Option A is the correct choice.

---

## 6. #51 — UoW Registry

**File:** `src/Typhon.Engine/Execution/UowRegistry.cs` (new)

### Purpose

The UoW Registry is a **persistent, memory-mapped data structure** that tracks the state of all active and recently-completed UoW epochs. It provides:

1. **Epoch allocation** — O(1) bitmap scan for free slots
2. **Crash recovery** — Pending epochs voided on startup
3. **Visibility checks** — Committed bitmap for revision visibility

### Registry Entry

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UowRegistryEntry
{
    public ushort UowId;             // 2 bytes — the UoW identifier
    public UnitOfWorkState State;    // 1 byte — Pending/WalDurable/Committed/Recyclable/Void
    public byte Reserved;            // 1 byte — alignment + future use
    public long CreatedTicks;        // 8 bytes — Stopwatch ticks at creation
    public long CommittedTicks;      // 8 bytes — Stopwatch ticks at WAL flush (0 if not yet)
    public int TransactionCount;     // 4 bytes — number of transactions committed in this epoch
    // Total: 24 bytes
}
```

### Page Layout

The registry should use `ChunkBasedSegment` (reuses proven allocation tracking via `BitmapL3`), with 24-byte stride per entry.

**Capacity per page** (8KB pages with standard segment headers):
- Root page: ~192-byte header → ~6000 usable bytes → **250 entries**
- Overflow pages: ~8000 usable bytes → **333 entries**
- One root page supports 250 concurrent UoWs — sufficient for most workloads
- Overflow pages allocated on demand via `ChunkBasedSegment` growth

### Registry Header (First 64 bytes of First Page)

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct UowRegistryHeader
{
    public int Version;              // Format version
    public int EntryCount;           // Total allocated entries
    public int ActiveCount;          // Currently Pending entries
    public int HighWaterMark;        // Highest epoch ID ever allocated
    public long LastRecoveryTicks;   // When crash recovery last ran
    // ... padding to 64 bytes
}
```

### Allocation Algorithm

```
AllocateEpoch():
  1. Scan bitmap for first Recyclable slot (O(1) with FindNextUnset)
  2. If no free slot: back-pressure (#52) — wait or throw
  3. CAS the slot state: Recyclable → Pending
  4. Write CreatedTicks, reset other fields
  5. Flush page (FUA) — the Pending state must be durable BEFORE any revisions are created
  6. Return epoch ID
```

**Critical invariant:** The Pending state must be durable on disk before any revision uses this epoch. This ensures that on crash, the registry scan finds and voids the epoch.

### Crash Recovery Algorithm

```
OnStartup():
  1. Read UoW Registry pages
  2. For each entry with State == Pending:
     → Set State = Void (atomic write + FUA)
     → All revisions stamped with this epoch become instantly invisible
  3. For each entry with State == WalDurable:
     → Leave as-is (WAL replay will handle these in Tier 5)
  4. For each entry with State == Committed:
     → Transition to Recyclable (epoch is fully persisted)
  5. Update ActiveCount, LastRecoveryTicks
```

**Recovery time:** O(N) where N = total registry entries (typically < 1000). Bounded by page reads, not computation.

### Committed Bitmap (In-Memory)

For fast visibility checks, the registry maintains an in-memory bitmap:

```csharp
// 32,768 bits = 4KB — fits in L1 cache
private readonly ulong[] _committedBitmap = new ulong[512];

public bool IsCommitted(ushort uowId) =>
    (_committedBitmap[uowId >> 6] & (1UL << (uowId & 63))) != 0;
```

This bitmap is rebuilt from the registry on startup and updated atomically as epochs transition to Committed.

---

## 7. #52 — Resource Back-Pressure

**File:** `src/Typhon.Engine/Execution/UnitOfWork.cs` (integrated into `CreateUnitOfWork`)

### Admission Control

Before allocating a new UoW, the engine checks resource pressure:

```csharp
public UnitOfWork CreateUnitOfWork(DurabilityMode mode, TimeSpan timeout = default)
{
    // 1. Check registry capacity
    if (_uowRegistry.ActiveCount >= _uowRegistry.MaxConcurrentEpochs)
    {
        // Wait for an epoch to become recyclable, or throw
        WaitForEpochSlot(deadline);
    }

    // 2. Future: check WAL ring buffer pressure (Tier 5)
    // if (walRingUtilization > 0.80) WaitForWalDrain(deadline);

    // 3. Future: check page cache pressure
    // if (pageCacheUtilization > 0.95) WaitForEviction(deadline);

    // 4. Allocate epoch and create UoW
    var uowId = _uowRegistry.AllocateUowId();
    return new UnitOfWork(this, mode, uowId, timeout);
}
```

### Pressure Thresholds

| Resource | Threshold | Action | When Available |
|----------|-----------|--------|----------------|
| UoW Registry slots | 100% full | Wait for Recyclable slot | This tier (#51) |
| WAL ring buffer | > 80% capacity | Wait for WAL Writer to drain | Tier 5 (#53) |
| Page cache | > 95% capacity | Wait for eviction | Future |
| Transaction pool | > 80% capacity | Wait for tx completion | Future |

### Wait Mechanism

```csharp
private void WaitForEpochSlot(Deadline deadline)
{
    var waiter = new AdaptiveWaiter();  // Spin → Yield → Sleep
    while (_uowRegistry.ActiveCount >= _uowRegistry.MaxConcurrentEpochs)
    {
        if (deadline.IsExpired)
            throw new TyphonTimeoutException("Timed out waiting for UoW epoch slot");
        waiter.Wait();
    }
}
```

This uses `AdaptiveWaiter` (already implemented in `src/Typhon.Engine/Misc/AdaptiveWaiter.cs`) for efficient spinning under contention.

---

## 8. API Migration

### Phase 1: Add New API (This Tier)

```csharp
// DatabaseEngine — add CreateUnitOfWork
public UnitOfWork CreateUnitOfWork(
    DurabilityMode mode = DurabilityMode.Deferred,
    TimeSpan timeout = default);

// Mark old API as obsolete
[Obsolete("Use CreateUnitOfWork().CreateTransaction() instead. Will be removed in v1.0.")]
public Transaction CreateTransaction();
```

### Phase 2: Test Strategy (Incremental)

Existing tests continue using `dbe.CreateTransaction()` with `[Obsolete]` suppression. **New tests** for UoW lifecycle (#49) use the new API. A separate follow-up PR migrates existing tests — keeping the mechanical refactor isolated from the functional changes.

```csharp
// Existing tests — unchanged, suppressed warning:
#pragma warning disable CS0618
using var tx = dbe.CreateTransaction();
#pragma warning restore CS0618

// New UoW tests:
using var uow = dbe.CreateUnitOfWork();
using var tx = uow.CreateTransaction();
```

### Phase 3: Migrate Existing Tests (Follow-Up PR)

Separate PR that mechanically migrates all existing tests to UoW-based API:

```csharp
// Before: using var tx = dbe.CreateTransaction();
// After:  using var uow = dbe.CreateUnitOfWork();
//         using var tx = uow.CreateTransaction();
```

### Phase 4: Remove Old API (Future)

Once all tests and consumers are migrated, remove `DatabaseEngine.CreateTransaction()`. This is a breaking change gated on the v1.0 milestone.

### Convenience API: CreateQuickTransaction()

For simple single-transaction use cases, `CreateQuickTransaction()` auto-wraps in a UoW with auto-dispose semantics:

```csharp
public static class DatabaseEngineExtensions
{
    public static Transaction CreateQuickTransaction(
        this DatabaseEngine dbe,
        DurabilityMode mode = DurabilityMode.Deferred)
    {
        var uow = dbe.CreateUnitOfWork(mode);
        var tx = uow.CreateTransaction();
        tx.OwnsUnitOfWork = true;  // Flag: Transaction.Dispose() also disposes UoW
        return tx;
    }
}
```

**Auto-dispose mechanism:** Transaction gets an `internal bool OwnsUnitOfWork` flag. When `true`, `Transaction.Dispose()` also calls `OwningUnitOfWork.Dispose()`. This keeps the ownership chain clean and explicit.

---

## 9. Implementation Order

```
Week 1: Foundation types + storage change
  #48  DurabilityMode enum + state machine types  (XS — types only)
  #50  CompRevStorageElement epoch stamping         (M — storage layout + visibility)

Week 2: Core class + registry
  #49  UnitOfWork class                             (L — class + API migration + tests)
  #51  UoW Registry                                 (M — persistent data structure)

Week 3: Integration
  #52  Resource back-pressure                       (S — admission control)
  ──   Test migration + integration testing
```

**Critical path:** #48 → #50 → #51 (registry needs epoch field in storage), and #48 → #49 → #52 (back-pressure needs UoW class).

---

## 10. Files to Create/Modify

### New Files

| File | Sub-Issue | Description |
|------|-----------|-------------|
| `src/Typhon.Engine/Execution/DurabilityMode.cs` | #48 | Enums: DurabilityMode, DurabilityOverride, UnitOfWorkState |
| `src/Typhon.Engine/Execution/UnitOfWork.cs` | #49 | Core UoW class |
| `src/Typhon.Engine/Execution/UowRegistry.cs` | #51 | Persistent epoch registry |
| `test/Typhon.Engine.Tests/UnitOfWorkTests.cs` | #49 | UoW lifecycle, transaction ownership |
| `test/Typhon.Engine.Tests/UowRegistryTests.cs` | #51 | Registry allocation, recovery |

### Modified Files

| File | Sub-Issue | Changes |
|------|-----------|---------|
| `src/Typhon.Engine/Data/ComponentTable.cs` | #50 | CompRevStorageElement: 10 → 12 bytes, new `_packedEpoch` field |
| `src/Typhon.Engine/Data/DatabaseEngine.cs` | #49 | Add `CreateUnitOfWork()`, deprecate `CreateTransaction()` |
| `src/Typhon.Engine/Data/Transaction/Transaction.cs` | #49, #50 | Add `OwningUnitOfWork`, epoch stamping in Commit() |
| `src/Typhon.Engine/Data/ComponentTable.cs` | #50 | Chunk size constants, visibility check updates |
| `test/Typhon.Engine.Tests/*.cs` | #49 | Migrate all tests to UoW-based API |

### Directory Creation

```
src/Typhon.Engine/Execution/    (new — UoW, registry, future scheduler)
```

---

## 11. Testing Strategy

### Unit Tests (#48, #49)

| Test | What It Verifies |
|------|-----------------|
| `UoW_Create_Dispose_Lifecycle` | Create → use → dispose without crash |
| `UoW_CreateTransaction_ReturnsValidTx` | Transaction works through UoW |
| `UoW_MultipleTransactions_SameEpoch` | All transactions share the UoW's epoch |
| `UoW_DisposedUoW_ThrowsOnCreateTx` | Cannot create transaction after dispose |
| `UoW_DurabilityMode_PreservedFromCreation` | Mode set at creation is accessible |
| `UoW_StateTransitions` | Pending → WalDurable → Committed → Recyclable |
| `UoW_Flush_NoOp_WithoutWAL` | Flush succeeds (no-op) without WAL infrastructure |
| `UoW_CancellationPropagates` | UoW disposal cancels outstanding operations |

### Storage Tests (#50)

| Test | What It Verifies |
|------|-----------------|
| `CompRevStorageElement_UowId_RoundTrips` | 15-bit UoW ID stored and retrieved correctly |
| `CompRevStorageElement_IsolationFlag_InEpochField` | Flag moved to bit 15 of `_packedEpoch` |
| `CompRevStorageElement_TSN_FullPrecision` | 48-bit TSN without bit 0 masking |
| `CompRevStorageElement_Size_Is12Bytes` | `sizeof(CompRevStorageElement) == 12` |
| `CompRevStorageElement_FitsChunk` | 5 elements per 64-byte chunk |

### Registry Tests (#51)

| Test | What It Verifies |
|------|-----------------|
| `Registry_AllocateEpoch_ReturnsUniqueIds` | No duplicate epoch IDs |
| `Registry_Release_MakesSlotRecyclable` | Released epoch can be reused |
| `Registry_CrashRecovery_VoidsPending` | Pending entries voided on simulated recovery |
| `Registry_CommittedBitmap_Accurate` | In-memory bitmap matches persistent state |
| `Registry_FullCapacity_BlocksAllocation` | Back-pressure when all slots used |

### Integration Tests

| Test | What It Verifies |
|------|-----------------|
| `CreateEntity_ThroughUoW_Succeeds` | Full path: UoW → tx → create → commit |
| `ReadEntity_ThroughUoW_SeesCommitted` | MVCC visibility through UoW layer |
| `MultipleUoWs_ConcurrentAccess` | Parallel UoWs don't interfere |
| `UoW_WithTimeout_PropagatesDeadline` | Deadline flows from UoW to lock acquisitions |

---

## 12. Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| **UoW pooling** | Not pooled (heap-allocated) | UoWs are infrequent (1 per tick/request), CTS must be fresh each time |
| **Epoch size** | 15 bits (32,767 max) | Sufficient for any realistic concurrency level |
| **Epoch 0 semantics** | Reserved sentinel = "always committed" | Legacy `CreateTransaction()` uses epoch 0. Visibility check treats 0 as implicitly committed. Registry allocates starting at 1. IsolationFlag already handles crash safety for uncommitted revisions |
| **UoW → Transaction binding** | Persistent reference (`Transaction._unitOfWork`) | 8 bytes per pooled object. Clean access to epoch + durability mode at commit time. Avoids expanding UnitOfWorkContext struct. Prepares for WAL (Tier 5) |
| **UnitOfWorkState.Void** | Contiguous value (= 4), not sentinel 255 | Enables array-indexed state tables and simpler bitmap logic. Can adjust later if needed |
| **CompRevStorageElement growth** | 10 → 12 bytes (per [ADR-027](../adr/027-even-sized-hot-path-structs.md)) | Epoch + isolation in 2-byte field, root: 3/chunk (was 4), overflow: 5/chunk (was 6) |
| **CompRevStorageElement field order** | ComponentChunkId first, `_packedEpoch` at offset 10 | Preserves 4-byte alignment of most-accessed field. Same as current layout with 2 bytes appended |
| **IsolationFlag location** | Moved to `_packedEpoch` bit 15 | Frees bit 0 of TSN, gains 1 bit precision, eliminates `& ~1L` masking |
| **Registry infrastructure** | `ChunkBasedSegment` (reuse, not custom) | Proven allocation tracking via BitmapL3, automatic growth, familiar API |
| **Registry persistence** | Memory-mapped segment | Must be durable before revisions use epoch |
| **Registry capacity** | 250 entries per root page, 333 per overflow | Sufficient for most workloads |
| **Back-pressure** | AdaptiveWaiter spin-wait | Reuses existing primitive, respects deadlines |
| **API deprecation** | `[Obsolete]` first, remove in v1.0 | Smooth migration, tests updated incrementally |
| **File format version** | Bumped (no in-place migration) | Pre-1.0, no production data to migrate |

---

## 13. Open Questions

1. ~~**Convenience API:** Resolved → yes, `CreateQuickTransaction()` with auto-dispose. See §8.~~

2. ~~**Test migration scope:** Resolved → incremental. Existing tests stay on deprecated API. See §8.~~

3. ~~**Registry page allocation:** Resolved → use `ChunkBasedSegment`.~~

4. ~~**Epoch 0 semantics:** Resolved → epoch 0 = "always committed" sentinel.~~

5. ~~**Visibility check scope:** Resolved → add `IsEpochVisible()` stub in #50, wired into visibility check. See §5.~~

6. **DeferredCleanupManager interaction:** The current Commit()/Dispose() has significant logic around deferred cleanup (tail detection, batch enqueue, processing). When revisions have epoch stamps, should void-epoch revisions be skipped during cleanup? Does the cleanup cutoff change? (Answer: likely no change needed — deferred cleanup operates on TSN-based cutoffs, which are orthogonal to epoch state. Void epochs make revisions invisible to *readers*, but cleanup still GCs revision chain entries based on MinTSN.)
