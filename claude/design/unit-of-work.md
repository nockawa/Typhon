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

> **TL;DR — The missing middle tier of the three-tier API hierarchy (DatabaseEngine → UoW → Transaction).** The UoW is the durability boundary for user operations, batching multiple transactions for efficient persistence while maintaining atomicity guarantees on crash recovery. Each UoW is assigned a UoW ID from the UoW Registry, which stamps all revisions created within its scope. On crash, pending UoW IDs are voided — their revisions become instantly invisible without WAL replay.

---

## Table of Contents

1. [Motivation](#1-motivation)
2. [Sub-Issue Breakdown](#2-sub-issue-breakdown)
3. [#48 — DurabilityMode Enum + State Machine Types](#3-48--durabilitymode-enum--state-machine-types)
4. [#49 — UnitOfWork Class](#4-49--unitofwork-class)
5. [#50 — UoW ID Stamping in CompRevStorageElement](#5-50--uow-id-stamping-in-comprevstorageelement)
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
- The UoW is a prerequisite for the WAL (#53), which needs UoW ID-based durability modes.
- Without UoW ID stamping, crash recovery cannot distinguish which revisions belong to which logical operation.

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
| **#50** | UoW ID stamping in CompRevStorageElement | #48 | Storage layout change (10 → 12 bytes) |
| **#51** | UoW Registry — persistent segment for crash-safe UoW ID tracking | #48, #50 | New persistent data structure |
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
/// Free → Pending → WalDurable → Committed → Free (normal path)
/// Free → Pending → Void → Free (crash recovery path, after GC)
/// </remarks>
public enum UnitOfWorkState : byte
{
    /// <summary>Slot available for reuse. Default state for zeroed memory.</summary>
    Free = 0,

    /// <summary>Created, transactions may be in progress. WAL records volatile.</summary>
    Pending = 1,

    /// <summary>WAL flush complete (FUA). Survives crash. Pages may still be dirty.</summary>
    WalDurable = 2,

    /// <summary>Data pages checkpointed. WAL segments recyclable.</summary>
    Committed = 3,

    /// <summary>Crash recovery: UoW was Pending at crash time. All revisions invisible.</summary>
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
        // UoW is passed to TransactionChain, which forwards it to Transaction.Init()
        var tx = _dbe.TransactionChain.CreateTransaction(_dbe, this);
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

        // Return UoW ID to registry
        // (future: _dbe.UowRegistry.Release(_uowId))

        _state = UnitOfWorkState.Free;
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
    // Future: allocate UoW ID from registry (#51)

    return new UnitOfWork(this, durabilityMode, uowId: 0, effectiveTimeout);
}

// EXISTING method — mark as obsolete, then remove
[Obsolete("Use CreateUnitOfWork().CreateTransaction() instead")]
public Transaction CreateTransaction() => TransactionChain.CreateTransaction(this);
```

### Transaction Changes

The `Transaction` class holds a **persistent reference** to its owning UoW. This gives the transaction access to the UoW ID and durability mode at commit time, and prepares for WAL integration (Tier 5) where `Commit()` needs the durability mode to decide FUA vs buffer.

The UoW is passed through the constructor/init path — not via a separate setter — so the association is established at creation time and cannot be forgotten or called out of order.

```csharp
// New internal fields (9 bytes per pooled Transaction — negligible)
internal UnitOfWork OwningUnitOfWork { get; private set; }
internal bool OwnsUnitOfWork { get; set; }  // For CreateQuickTransaction() auto-dispose
```

**Init() signature change:**
```csharp
// Current: public void Init(DatabaseEngine dbe, long tsn)
// New:     public void Init(DatabaseEngine dbe, long tsn, UnitOfWork uow = null)
//          OwningUnitOfWork = uow;
```

**TransactionChain.CreateTransaction() signature change:**
```csharp
// Current: public Transaction CreateTransaction(DatabaseEngine dbe)
// New:     public Transaction CreateTransaction(DatabaseEngine dbe, UnitOfWork uow = null)
//          Forwards uow to Transaction.Init(dbe, tsn, uow)
```

**Reset() — must clear both fields:**
```csharp
// In Reset():
OwningUnitOfWork = null;
OwnsUnitOfWork = false;
```

This is critical for pooled transactions: when a `Transaction` is returned to the pool and later reused by a different UoW (or by the deprecated `CreateTransaction()` with no UoW), stale references must be cleared. `OwnsUnitOfWork` must also be cleared to prevent a reused transaction from disposing an unrelated UoW.

**Dispose() — conditional UoW cleanup:**
```csharp
// In Dispose(), after existing cleanup:
if (OwnsUnitOfWork)
    OwningUnitOfWork?.Dispose();
```

**UoW ID stamping in Commit():** After conflict detection succeeds, the commit path stamps all pending revision elements with `OwningUnitOfWork?.UowId ?? 0`:

```csharp
// In CommitComponent(), after writing the revision element:
element.UowId = OwningUnitOfWork?.UowId ?? 0;
```

**UoW ID 0 semantics (decided):** UoW ID 0 is a reserved sentinel meaning "no UoW" / "always committed." The deprecated `CreateTransaction()` produces transactions with `OwningUnitOfWork = null`, UoW ID 0. The visibility check treats UoW ID 0 as implicitly committed (no registry lookup). The registry never allocates UoW ID 0 — allocation starts at 1.

---

## 5. #50 — UoW ID Stamping in CompRevStorageElement

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

ComponentChunkId stays at offset 0 to preserve 4-byte natural alignment (cache-friendly access pattern for the most-frequently-read field). The new `_packedUowId` field is appended at offset 10.

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

**`_packedUowId` (2 bytes, offset 10):**
- Bits 0–14: UoW ID (15 bits → max 32,767 concurrent UoWs)
- Bit 15: IsolationFlag (moved from `_packedTickLow` bit 0)

**TSN precision gain:** By moving the IsolationFlag to `_packedUowId`, bit 0 of `_packedTickLow` is freed. The TSN now uses the full 48 bits (was 47 effective bits). This eliminates the `safeCutoff = MinTSN & ~1L` masking in `Transaction.cs` line ~1108.

### Storage Density Impact

| Metric | Root Chunk (Before → After) | Overflow Chunks (Before → After) |
|--------|----------------------------|----------------------------------|
| Element size | 10 → 12 bytes | 10 → 12 bytes |
| Elements per chunk | 4 → **3** (−25%) | 6 → **5** (−17%) |
| Wasted bytes | 4 → 8 bytes | 4 → 4 bytes |

The density reduction means revision chains take more chunks. For typical workloads (short revision chains of 1–3 entries), the impact is minimal — most entities fit within the root chunk. Entities with longer revision chains (>3 revisions) will overflow to a second chunk slightly earlier.

**Dynamic capacity calculation** — `ComponentRevisionManager.CompRevCountInRoot` and `CompRevCountInNext` are computed from `sizeof(CompRevStorageElement)` at runtime, so no hardcoded constants need updating. Only comments need correction.

### Visibility Check — Two-Tier Architecture

Typhon's read path uses a **two-tier visibility architecture** that separates normal-operation performance from crash-recovery correctness. Two independent concerns control revision visibility:

- **IsolationFlag** (per-revision, 1 bit): "Has the **transaction** that created this revision committed?" Set by `Transaction.Commit()`. This is the only visibility mechanism needed during normal operation.
- **CommittedBeforeTSN** + **Committed Bitmap** (per-UoW): "Was the **UoW** voided after a crash?" Only needed to filter ghost revisions — revisions that are physically present on disk but belong to voided UoWs. See [§6 CommittedBeforeTSN](#committedbeforetsn--visibility-horizon) for the full mechanism.

**Key insight:** During normal operation, the UoW state (Pending/WalDurable/Committed) is about **durability**, not **visibility**. A revision from a Pending UoW is fully visible to other readers — the user chose `DurabilityMode.Deferred`, accepting the crash-loss tradeoff. This is what makes "Commit = instant (~1-2µs)" possible.

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
bool IsVisible(ref CompRevStorageElement rev, long readerTSN, ushort readerUowId)
{
    // ── Layer 1: Transaction Isolation ──────────────────────────────────
    // IsolationFlag = 1 means the creating transaction hasn't committed yet.
    // Only the same UoW can see its own in-progress revisions.
    if (rev.IsolationFlag)
        return rev.UowId == readerUowId;

    // ── Layer 2: CommittedBeforeTSN Fast Path ───────────────────────────
    // Any revision with TSN below the committed horizon is guaranteed
    // to be from a fully committed (or recycled) UoW — no bitmap needed.
    //
    // During normal operation (no voided UoWs): _committedBeforeTSN = long.MaxValue
    //   → This branch ALWAYS succeeds. The bitmap is never touched.
    //   → Cost: 1 comparison (~0.25ns), branch predictor: always taken.
    //
    // After crash recovery (voided UoWs exist): _committedBeforeTSN = 0
    //   → This branch NEVER succeeds. Falls through to bitmap (Layer 4).
    //   → Temporary until voided entries are cleaned up.
    if (rev.TSN < _committedBeforeTSN)
        return rev.TSN <= readerTSN;

    // ── Layer 3: UoW Identity ───────────────────────────────────────────
    var uowId = rev.UowId;
    if (uowId == 0) return rev.TSN <= readerTSN;  // Legacy sentinel: always committed
    if (uowId == readerUowId) return true;         // Same UoW: read-your-writes

    // ── Layer 4: Committed Bitmap (crash-recovery fallback) ─────────────
    // Only reached when CommittedBeforeTSN is 0 (post-crash, voided UoWs exist).
    // The bitmap returns false for voided UoW IDs, filtering ghost revisions.
    // Once all voided entries are cleaned up, this path becomes unreachable.
    if (!_uowRegistry.IsCommitted(uowId))
        return false;

    return rev.TSN <= readerTSN;
}
```

**Normal operation performance:** Layers 1 + 2 handle 100% of reads. Layer 1 is a single bit test (~1 cycle, almost always false for committed data). Layer 2 is a single comparison (~1 cycle, always true when `_committedBeforeTSN = long.MaxValue`). Total: **2 cycles**. The committed bitmap is never touched.

**Post-crash performance:** Layer 2 fails (CommittedBeforeTSN = 0), reads fall through to Layer 4 (~5-10 cycles for bitmap lookup). This is temporary — once voided entries are cleaned up and GC'd, CommittedBeforeTSN returns to `long.MaxValue` and the bitmap goes cold.

**UoW visibility stub** (added in #50, before registry and CommittedBeforeTSN exist):

```csharp
// In DatabaseEngine — temporary until #51 lands:
// CommittedBeforeTSN is effectively long.MaxValue (no voided UoWs possible yet).
// Layer 2 always succeeds. Layers 3-4 are unreachable.
internal bool IsUowVisible(ushort uowId) =>
    uowId == 0 || true;
```

When #51 lands, `CommittedBeforeTSN` is wired as the primary mechanism. The bitmap activates only after crash recovery.

### Migration

Existing data files have 10-byte `CompRevStorageElement` entries. A file format version bump is required. Migration strategy:

- **Option A (chosen):** Bump file format version. Old files cannot be opened without migration.
- **Option B:** In-place migration on first open (read 10-byte, write 12-byte). Risky if interrupted.

Since Typhon is pre-1.0 with no production data, Option A is the correct choice.

---

## 6. #51 — UoW Registry

**File:** `src/Typhon.Engine/Execution/UowRegistry.cs` (new)

### Purpose

The UoW Registry is an **in-memory data structure** (backed by a checkpoint cache in the `ManagedMMF`) that tracks the state of all active and recently-completed UoWs. The WAL is the authority for all UoW state transitions; the registry pages in the data file are written during checkpoints and used to accelerate recovery. It provides:

1. **UoW ID allocation** — O(1) bitmap scan for free slots, no blocking FUA (WAL group commit)
2. **Crash recovery** — Checkpoint baseline + WAL delta replay; Pending UoW IDs voided on startup
3. **Visibility checks** — Committed bitmap as crash-recovery fallback (not hot path)

### Registry Entry

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UowRegistryEntry   // 40 bytes — slot index = UoW ID (no need to store ID in the entry)
{
    public UnitOfWorkState State;    // 1 byte — Free/Pending/WalDurable/Committed/Void
    public byte Reserved;            // 1 byte — alignment
    public short Reserved2;          // 2 bytes — alignment
    public int TransactionCount;     // 4 bytes — number of transactions committed in this UoW
    public long CreatedTicks;        // 8 bytes — Stopwatch ticks at creation
    public long CommittedTicks;      // 8 bytes — Stopwatch ticks at WAL flush (0 if not yet)
    public long MaxTSN;              // 8 bytes — highest TSN of any transaction in this UoW (for GC)
    public long Reserved3;           // 8 bytes — future use (e.g., MinTSN for precise CommittedBeforeTSN)
    // Total: 40 bytes — divides evenly into both root (6000/40=150) and overflow (8000/40=200) pages
}
```

### Durability Model — WAL-Based Registry

The UoW Registry is **not** independently FUA'd. Instead, UoW state transitions are recorded as **WAL records**, and the registry pages in the `ManagedMMF` are a **checkpoint cache** — written during regular checkpoints, not on every allocation.

**Why:** The main `ManagedMMF` file is opened with normal flags (OS-controlled writeback). It has no `O_DIRECT`/`FILE_FLAG_NO_BUFFERING` capabilities, making per-write FUA impossible. Even if it did, a blocking FUA on every `AllocateUowId()` would cost ~15-85µs — unacceptable for an engine targeting ~1-2µs commits.

**How it works:**

```
AllocateUowId():
  1. Scan allocation bitmap for first Free slot (O(1))
  2. CAS the slot state: Free → Pending (in-memory only)
  3. Write "UoW Created" WAL record to ring buffer
  4. WAL group flush batches the record with other pending writes (one FUA for all)
  5. Return UoW ID — usable after WAL flush confirms durability
```

The WAL already has the infrastructure for efficient durable writes: ring buffer, dedicated I/O thread, group commit batching, FUA-capable file flags. UoW creation piggybacks on this existing pipeline.

**Group commit amortization:**

| Scenario | Per-UoW FUA (old) | WAL group commit (new) |
|----------|-------------------|------------------------|
| 1 UoW creation | ~40µs (1 FUA) | ~40µs (1 FUA) |
| 5 UoW creations in batch window | ~200µs (5 FUAs) | ~40µs (1 FUA, amortized ~8µs each) |
| 10 UoW creations in batch window | ~400µs (10 FUAs) | ~40µs (1 FUA, amortized ~4µs each) |

**Critical invariant (preserved):** The UoW creation WAL record must be flushed (durable) before any revision referencing that UoW ID can be written to a data page. This ensures crash recovery always finds and voids in-flight UoWs. The invariant is the same as the original design — only the mechanism changes (WAL FUA instead of registry page FUA).

### Recovery Model — Checkpoint + WAL Delta

The registry pages are a **checkpoint cache**: written during regular checkpoints, disposable if corrupted. Recovery combines the cached state with WAL replay:

```
Startup:
  1. Read registry pages from ManagedMMF (checkpoint baseline)
     ├─ Checksum valid   → use as baseline         ← O(1), fast
     └─ Checksum invalid → start with empty registry (all Free)
  2. Replay WAL records since last checkpoint:
     ├─ "UoW Created"   → set entry to Pending
     ├─ "UoW WalDurable"→ set entry to WalDurable
     ├─ "UoW Committed" → set entry to Committed
     └─ (etc.)
  3. Void any Pending entries (crash recovery)
  4. Rebuild allocation + committed bitmaps
  5. Registry is fully reconstructed
```

**Torn registry pages:** If the checkpoint write is interrupted (torn page), recovery simply **ignores the cached state** and rebuilds entirely from WAL. The worst case is slower recovery (replay more WAL), never data loss. This eliminates the need for FPI on registry pages — they're disposable.

**WAL segment recycling constraint:** WAL segments can only be recycled after BOTH the data pages AND the registry checkpoint covering them are successfully written. Since both happen in the same checkpoint cycle, this adds no new constraint.

### Storage Format — Flat Array in Growing Logical Segment

The registry uses a **flat array** stored in a growing `LogicalSegment` within the `ManagedMMF`. No chunks, no overflow chains, no BitmapL3 — just a dense array where slot index = UoW ID.

**Why not chunks?** The chunk-based design (`ChunkBasedSegment`) solves dynamic alloc/free with page chains and free-list tracking. The registry doesn't need this — it's a fixed-index array where entries are addressed by UoW ID (slot index). A flat array is simpler, faster, and has zero metadata overhead.

**Page layout** (8KB pages, 40-byte entries):

```
Root page:     entries[  0 .. 149]     150 × 40 = 6000 bytes (exact fit)
Overflow 0:    entries[150 .. 349]     200 × 40 = 8000 bytes (exact fit)
Overflow 1:    entries[350 .. 549]     200 × 40 = 8000 bytes (exact fit)
...
Overflow N:    entries[150+N*200 .. 150+(N+1)*200-1]
```

40 bytes divides evenly into both root (6000) and overflow (8000) usable page sizes — zero waste, no entry spans a page boundary.

**Addressing** (same formula as `ChunkBasedSegment` root/overflow split):

```csharp
const int RootCapacity = 150;       // 6000 / 40
const int OverflowCapacity = 200;   // 8000 / 40

int GetPageIndex(int slotIndex)
{
    if (slotIndex < RootCapacity)
        return 0;  // root page
    return 1 + (slotIndex - RootCapacity) / OverflowCapacity;
}

int GetPageOffset(int slotIndex)
{
    if (slotIndex < RootCapacity)
        return slotIndex * 40;
    return ((slotIndex - RootCapacity) % OverflowCapacity) * 40;
}
```

**Growing segment** — pages are allocated on demand:

| Capacity | Pages (root + overflow) | Size |
|----------|------------------------|------|
| 150 entries | 1 (root only) | 8 KB |
| 350 entries | 1 + 1 = 2 | 16 KB |
| 4,150 entries | 1 + 20 = 21 | 168 KB |
| 32,750 entries | 1 + 163 = 164 | ~1.3 MB |

For unit tests creating 1-5 UoWs: **1 page, 8 KB**. Grows only when needed.

**`Free = 0` guarantee:** Fresh/zeroed pages are automatically all-Free entries. No initialization needed when the segment grows.

### RootFileHeader Integration

The MMF file's root header must reference the registry segment. A new field is added to `RootFileHeader`:

```csharp
public int UowRegistrySPI;      // Starting Page Index of UoW Registry segment
```

This follows the same pattern as `OccupancyMapSPI`, `ComponentTableSPI`, and `FieldTableSPI`. Included in the file format version bump (§5).

### Allocation Bitmap (In-Memory)

The allocation bitmap tracks which registry slots are `Free` for O(1) allocation. It is **in-memory only**, rebuilt from persistent `UowRegistryEntry.State` fields on startup:

```csharp
// 32,768 bits = 4KB — one bit per possible UoW ID
private readonly ulong[] _allocationBitmap = new ulong[512];

// Bit = 1 means Free (available for allocation)
// Bit = 0 means in-use (Pending/WalDurable/Committed/Void)
```

Rationale: the WAL is the authoritative source of truth for UoW state transitions. The in-memory registry (including this bitmap) is rebuilt from the checkpoint cache + WAL delta on startup. An in-memory bitmap avoids any disk I/O on the allocation hot path.

### Allocation Algorithm

```
AllocateUowId():
  1. Scan allocation bitmap for first Free slot (O(1) with BitOperations.TrailingZeroCount)
  2. If no free slot: back-pressure (#52) — wait or throw
  3. CAS the slot state: Free → Pending (in-memory)
  4. Clear the allocation bitmap bit (slot is no longer free)
  5. Write CreatedTicks, reset other fields (in-memory)
  6. Write "UoW Created" WAL record to ring buffer
  7. WAL group flush makes the record durable (batched with other writes)
  8. Return UoW ID — usable after WAL flush confirms
```

**Critical invariant:** The "UoW Created" WAL record must be durable (FUA'd) before any revision referencing this UoW ID can be written to a data page. This ensures crash recovery always knows about in-flight UoWs. The invariant is enforced by the WAL pipeline — the same mechanism that ensures transaction commits are durable before data pages are checkpointed.

**No blocking FUA on registry pages:** The in-memory registry is updated immediately (steps 3-5). Durability comes from the WAL record (steps 6-7), which is batched via group commit. The registry pages are written to disk during the next checkpoint — lazily, not urgently.

### Crash Recovery Algorithm

```
OnStartup():
  1. Read UoW Registry pages from ManagedMMF (checkpoint baseline)
     ├─ Page checksums valid → use as baseline
     └─ Page checksums invalid → start with all-Free entries (rebuild from WAL)
  2. Replay WAL records since last checkpoint:
     ├─ "UoW Created"   → set entry to Pending
     ├─ "UoW WalDurable"→ set entry to WalDurable
     ├─ "UoW Committed" → set entry to Committed
     └─ (handles any state transitions missed by checkpoint)
  3. For each entry with State == Pending:
     → Set State = Void
     → All revisions stamped with this UoW ID become instantly invisible
  4. For each entry with State == WalDurable:
     → Leave as-is (WAL replay will redo their data)
  5. For each entry with State == Committed:
     → Leave as-is (GC will transition to Free when safe)
  6. Rebuild committed bitmap from entry states
  7. Rebuild allocation bitmap from entry states
  8. Set CommittedBeforeTSN = (_voidEntryCount > 0) ? 0 : long.MaxValue
```

**Recovery time:** O(checkpoint) + O(WAL delta). The checkpoint read is O(1) — a few pages. The WAL delta is bounded by the checkpoint interval (10s default). For clean shutdown: checkpoint is up-to-date, WAL delta is empty → recovery is instant.

**Torn registry pages:** If any registry page has an invalid checksum, the recovery simply starts with an empty registry and rebuilds from WAL. This is slower (full WAL replay) but correct. No data loss, no FPI needed for registry pages.

### Committed Bitmap (In-Memory) — Crash-Recovery Fallback

The committed bitmap is a **cold-path fallback** used only after crash recovery while voided UoW entries exist. It is **not** on the normal-operation read path.

```csharp
// 32,768 bits = 4KB — fits in L1 cache
private readonly ulong[] _committedBitmap = new ulong[512];

public bool IsCommitted(ushort uowId) =>
    (_committedBitmap[uowId >> 6] & (1UL << (uowId & 63))) != 0;
```

The bitmap is rebuilt from registry entry states on startup. A bit is set when a UoW transitions to Committed/WalDurable (durability states where WAL replay can recover data). A bit is unset for Free, Pending (which becomes Void on crash), and Void entries.

**Why it exists at all:** After a crash, some revisions may be physically present on disk (written by the page cache before crash) but belong to UoWs that were Pending at crash time — these are **ghost revisions**. The bitmap filters them: voided UoW IDs have bit = 0, so `IsCommitted()` returns `false`, making ghost revisions invisible without walking revision chains.

**Why it's not on the hot path:** During normal operation, `CommittedBeforeTSN = long.MaxValue` causes the visibility fast path (Layer 2, see §5) to succeed for every read. The bitmap is never accessed. It activates only after crash recovery produces Void entries, and goes cold again once those entries are cleaned up.

### CommittedBeforeTSN — Visibility Horizon

`CommittedBeforeTSN` is a `long` value maintained by the UoW Registry that enables **zero-overhead visibility checks during normal operation**. It is the primary visibility mechanism (Layer 2 in the [visibility check](#visibility-check--two-tier-architecture)), with the committed bitmap as its fallback.

#### Definition

`CommittedBeforeTSN` is the **maximum TSN below which all revisions are guaranteed to be from a committed (or recycled) UoW**. Any revision with `rev.TSN < CommittedBeforeTSN` can skip the committed bitmap entirely — it is guaranteed visible (subject to the standard `TSN ≤ readerTSN` snapshot check).

#### The Problem It Solves: UoW ID Reuse (ABA Problem)

UoW IDs are 15-bit values recycled from a pool. When a UoW completes and its registry slot is recycled, a new UoW may claim the same ID. This creates an ABA problem:

1. UoW-A (UowId=2) commits revisions at TSN=45, 46. Bitmap bit 2 = 1.
2. GC recycles slot 2: bitmap bit 2 = 0.
3. UoW-B grabs slot 2 (UowId=2). State = Pending, bitmap bit 2 = 0.
4. A reader (TSN=50) encounters UoW-A's old revision: `{TSN=45, UowId=2}`.
5. `IsCommitted(2)` → bitmap bit 2 = 0 → **FALSE**. The committed revision appears invisible. **Data loss.**

The committed bitmap cannot distinguish "old committed revision from UoW-A" from "new uncommitted revision from UoW-B" — both have `UowId=2`.

`CommittedBeforeTSN` solves this by **bypassing the bitmap entirely** for revisions old enough to be guaranteed committed. In the scenario above, `CommittedBeforeTSN = long.MaxValue` (no voided UoWs), so the fast path succeeds: `TSN(45) < long.MaxValue → true`. The bitmap is never consulted. UoW-A's revision is correctly visible.

#### Values and State Transitions

| Engine State | CommittedBeforeTSN | Effect on Reads |
|---|---|---|
| **Normal operation** (no Void entries) | `long.MaxValue` | Layer 2 always succeeds. Bitmap never touched. Zero overhead. |
| **After crash** (Void entries exist) | `0` | Layer 2 always fails. All reads fall through to bitmap (Layer 4). |
| **Void entries cleaned up** (Void → Free) | `long.MaxValue` | Back to normal. Bitmap goes cold. |

```csharp
// In UowRegistry:
private long _committedBeforeTSN = long.MaxValue;

// Updated when Void entries appear or disappear
internal void OnVoidEntryCreated() => _committedBeforeTSN = 0;
internal void OnVoidEntryFreed()
{
    if (_voidEntryCount == 0)
        _committedBeforeTSN = long.MaxValue;
}
```

#### Why `long.MaxValue` During Normal Operation Is Correct

During normal operation (no crashes, no voided UoWs):

- **IsolationFlag** handles per-transaction visibility. A revision with `IsolationFlag = 0` is from a committed transaction → visible. The UoW's durability state (Pending/WalDurable/Committed) is irrelevant for visibility.
- **No ghost revisions exist.** Ghost revisions only appear when pages are flushed to disk by the page cache and then a crash voids the owning UoW. Without a crash, no Void entries exist.
- **UoW ID reuse is safe** because the fast path uses TSN comparison, not the bitmap. Old revisions from recycled UoW IDs are visible via `rev.TSN < long.MaxValue`. New revisions from the recycled badge are visible or invisible based purely on `IsolationFlag` and `TSN ≤ readerTSN`.

#### Why `0` After Crash Recovery

After a crash, ghost revisions may exist on disk:

1. The page cache flushed a dirty page containing a revision from a Pending UoW.
2. The crash prevented the UoW from completing. Recovery voids it.
3. The revision is physically on disk, `IsolationFlag = 0` (its transaction committed before crash), but it belongs to a voided UoW → it must be invisible.

Setting `CommittedBeforeTSN = 0` forces **all reads** through the committed bitmap (Layer 4). The bitmap returns `false` for voided UoW IDs, correctly filtering ghost revisions.

**Why not compute a precise threshold?** The `UowRegistryEntry` does not store `MinTSN` (first TSN). Without it, we cannot determine the exact TSN boundary of voided revisions. The conservative `0` is correct and acceptable because:
- Crashes are rare events
- The post-crash bitmap window is short (voided revisions are cleaned up as `MinTSN` advances)
- The bitmap is 4KB (fits in L1 cache), costing ~5-10 cycles per read — acceptable for a rare, temporary state

**Future enhancement:** If `MinTSN` is added to `UowRegistryEntry`, `CommittedBeforeTSN` can become a precise threshold: `min(MinTSN across all Void entries)`. This would allow partial fast-path success even during the post-crash window.

#### Startup Computation

| Startup Type | Initial Value | Rationale |
|---|---|---|
| **Clean startup** (no Void entries) | `long.MaxValue` | No ghost revisions. Normal operation. |
| **Crash recovery** (Void entries created) | `0` | Conservative. Bitmap handles all reads until voided entries are cleaned up. |

On startup, after the registry reconstruction (checkpoint baseline + WAL delta replay):
```csharp
_committedBeforeTSN = (_voidEntryCount > 0) ? 0 : long.MaxValue;
```

### Entry Lifecycle

See [06-durability.md §6.4](../overview/06-durability.md#64-uow-registry) for the full lifecycle reference. Summary:

```
                   ┌───────────────────────────────────────┐
                   ▼                                       │
              ┌──────────┐                                 │
              │   Free   │◄─── GC (MinTSN past MaxTSN) ────┤
              └────┬─────┘                                 │
                   │ AllocateUowId()                       │
                   │ [WAL record + group commit]           │
                   ▼                                       │
              ┌──────────┐     crash                       │
              │ Pending  │─────────────► Void ─────────────┤
              └────┬─────┘              (recovery)         │
                   │ WAL flush (FUA complete)              │
                   ▼                                       │
             ┌───────────┐                                 │
             │ WalDurable│                                 │
             └─────┬──────┘                                │
                   │ Checkpoint (data pages fsync'd)       │
                   ▼                                       │
             ┌───────────┐                                 │
             │ Committed │─────────────────────────────────┘
             └───────────┘
```

**Free → Pending:** `AllocateUowId()` claims a free slot (in-memory). A "UoW Created" WAL record is written to the ring buffer and flushed via group commit. The UoW ID is usable after the WAL flush confirms durability. No FUA on registry pages — the WAL record is the durable record.

**Pending → WalDurable:** All WAL records for this UoW (including the creation record) have been flushed via FUA. *(No-op until WAL lands in Tier 5.)*

**WalDurable → Committed:** Checkpoint Manager has written all dirty data pages for this UoW to the data file and called `fsync`. WAL segments covering this UoW are now recyclable. The committed bitmap bit is set atomically.

**Committed → Free:** GC condition — `TransactionChain.MinTSN > entry.MaxTSN`. Slot recycled, allocation bitmap bit set. This is safe because `CommittedBeforeTSN` (see [§6 CommittedBeforeTSN](#committedbeforetsn--visibility-horizon)) ensures old revisions with this UoW ID remain visible via the TSN fast path, even after the committed bitmap bit is cleared. The bitmap is not consulted during normal operation.

**Pending → Void** *(crash recovery only)*: On startup, any entry in `Pending` state had an in-flight UoW at crash time. Recovery sets `State = Void`, sets `CommittedBeforeTSN = 0`, which activates the committed bitmap as the visibility filter for all reads. Ghost revisions with this UoW ID become invisible (bitmap bit stays unset). Once the voided entry is cleaned up (Void → Free), `CommittedBeforeTSN` returns to `long.MaxValue`.

**Void → Free:** Same GC condition as Committed → Free (`MinTSN > MaxTSN`). Voided revisions are cleaned up by `DeferredCleanupManager` during normal revision chain GC. When `_voidEntryCount` reaches 0, `CommittedBeforeTSN` returns to `long.MaxValue` and the committed bitmap goes cold.

---

## 7. #52 — Resource Back-Pressure

**File:** `src/Typhon.Engine/Execution/UnitOfWork.cs` (integrated into `CreateUnitOfWork`)

### Scope

This tier implements back-pressure for **UoW Registry slots only**. Future pressure sources (WAL ring buffer, page cache, transaction pool) will be added in their respective tiers. No general `BackPressureGate` abstraction is built now — the four pressure sources have different shapes (binary vs threshold-based, different signal sources) and premature abstraction would be wasteful. When Tier 5 (WAL) lands, the pattern will be clearer.

### Admission Control

Before allocating a new UoW, the engine checks registry capacity as a **fast-path optimization**. The actual atomicity guarantee is in `AllocateUowId()`'s CAS — the admission check avoids entering the heavier allocation path when the registry is obviously full.

```csharp
public UnitOfWork CreateUnitOfWork(DurabilityMode mode, TimeSpan timeout = default)
{
    var effectiveTimeout = timeout == default
        ? TimeoutOptions.Current.DefaultUowTimeout
        : timeout;
    var wc = WaitContext.FromTimeout(effectiveTimeout);

    // 1. Admission check — fast-path optimization (not a guarantee, see TOCTOU note)
    if (_uowRegistry.ActiveCount >= _uowRegistry.MaxConcurrentUoWs)
    {
        WaitForFreeSlot(ref wc);
    }

    // 2. Allocate UoW ID — CAS provides the real atomicity.
    //    May still fail if another thread raced past the admission check (TOCTOU).
    //    AllocateUowId handles this gracefully: retries internally or waits.
    var uowId = _uowRegistry.AllocateUowId(ref wc);
    return new UnitOfWork(this, mode, uowId, effectiveTimeout);

    // Future (Tier 5): check WAL ring buffer pressure before allocation
    // Future: check page cache pressure before allocation
}
```

### TOCTOU Note

The admission check and `AllocateUowId()` are not atomic:

```
Thread A checks: ActiveCount (99) < Max (100) → proceeds
Thread B checks: ActiveCount (99) < Max (100) → proceeds
Both call AllocateUowId() → one gets slot 100, the other finds no free slot
```

This is by design. The admission check is an **optimization** — it avoids the heavier allocation path when the registry is obviously full. The real atomicity is in `AllocateUowId()`, which uses CAS on individual bitmap words. If CAS fails to find a free slot (all claimed between check and allocation), `AllocateUowId` falls into the same event-based wait path internally.

`AllocateUowId()` signature change to support this:

```csharp
// In UowRegistry:
// Returns allocated UoW ID (1–32767).
// If no free slot available, waits on _slotFreed event until deadline expires.
// Throws TyphonResourceExhaustedException on timeout.
public ushort AllocateUowId(ref WaitContext wc)
{
    while (true)
    {
        // Scan allocation bitmap for first Free slot (O(1) with BitOperations.TrailingZeroCount)
        var slot = TryClaimFreeSlot();
        if (slot >= 0)
        {
            InitializeEntry(slot);
            return (ushort)slot;
        }

        // No free slot — wait for signal from Release()
        if (!WaitForSlotFreed(ref wc))
        {
            throw new TyphonResourceExhaustedException(
                "Storage/UowRegistry/AllocateUowId", ResourceType.UowRegistrySlot,
                ActiveCount, MaxConcurrentUoWs);
        }
    }
}
```

### Event-Based Wait Mechanism

Instead of spin-polling `ActiveCount`, the registry uses a `ManualResetEventSlim` signaled by `Release()`. This gives near-instant wake latency with zero CPU burn while waiting.

```csharp
// In UowRegistry:
private readonly ManualResetEventSlim _slotFreed = new(false);

public void Release(ushort uowId)
{
    // ... mark slot Free, set allocation bitmap bit ...
    _slotFreed.Set();  // Wake one or more waiters
}

/// <summary>
/// Wait for a registry slot to become free.
/// Returns true if a slot was freed, false on timeout/cancellation.
/// </summary>
private bool WaitForSlotFreed(ref WaitContext wc)
{
    // ManualResetEventSlim.Wait supports both timeout (int ms) and CancellationToken
    try
    {
        return _slotFreed.Wait(wc.Deadline.RemainingMilliseconds, wc.CancellationToken);
    }
    catch (OperationCanceledException)
    {
        return false;
    }
    finally
    {
        _slotFreed.Reset();  // Re-arm for next wait
    }
}
```

**Why `ManualResetEventSlim` over `AdaptiveWaiter` polling:**

| Aspect | AdaptiveWaiter polling | ManualResetEventSlim |
|--------|----------------------|----------------------|
| CPU while waiting | ~1ms poll cycles | Zero (kernel wait) |
| Wake latency | Up to ~1ms (next poll) | Near-instant (OS wakes thread) |
| Complexity | Simple loop | Slightly more (event lifecycle) |
| Best for | Sub-ms spin waits (lock contention) | Longer waits (resource availability) |

The back-pressure wait is fundamentally different from lock contention: locks are held for ~50-500ns (fast spin is appropriate), while UoW slots may be held for milliseconds to seconds (spinning wastes CPU). An event-based wait matches the timescale.

**Spurious wakes are harmless:** If `_slotFreed` fires but another thread claims the slot first, `TryClaimFreeSlot()` simply returns -1 and the loop re-enters the wait. The event is just a hint ("something changed, re-check"), not a guarantee.

### Admission Control in CreateUnitOfWork

The admission check before `AllocateUowId()` is a fast-path optimization:

```csharp
private void WaitForFreeSlot(ref WaitContext wc)
{
    // Fast path: check if registry is full before entering the heavier AllocateUowId path
    // This avoids bitmap scanning when we know it will fail.
    if (!_uowRegistry.WaitForSlotFreed(ref wc))
    {
        throw new TyphonResourceExhaustedException(
            "Storage/UowRegistry/WaitForFreeSlot", ResourceType.UowRegistrySlot,
            _uowRegistry.ActiveCount, _uowRegistry.MaxConcurrentUoWs);
    }
}
```

### Error Semantics

Back-pressure timeout throws `TyphonResourceExhaustedException` (not `TyphonTimeoutException`) to distinguish "resource exhausted after waiting" from "operation timed out during execution". The exception carries diagnostic context:

```csharp
/// <summary>
/// Thrown when a bounded resource is exhausted and the wait deadline expired.
/// </summary>
public class TyphonResourceExhaustedException : TyphonTimeoutException
{
    public string ResourcePath { get; }    // e.g. "Storage/UowRegistry/AllocateUowId"
    public ResourceType ResourceType { get; }  // e.g. ResourceType.UowRegistrySlot
    public long CurrentUsage { get; }      // e.g. 32767 (all slots in use)
    public long MaxCapacity { get; }       // e.g. 32767

    // Inherits from TyphonTimeoutException so existing catch blocks still work,
    // but operators can catch specifically for resource exhaustion diagnostics.
}
```

**Why a subclass of `TyphonTimeoutException`?** The caller experiences this as a timeout — they asked for a UoW and didn't get one within the deadline. The subclass adds *why* it timed out (resource exhaustion vs. lock contention vs. I/O delay). Existing `catch (TyphonTimeoutException)` handlers still work; operators who want finer diagnostics can catch the subclass.

### Pressure Thresholds (Roadmap)

| Resource | Threshold | Action | When Available |
|----------|-----------|--------|----------------|
| UoW Registry slots | 100% full | Event-based wait via `ManualResetEventSlim` | This tier (#52) |
| WAL ring buffer | > 80% capacity | Wait for WAL Writer to drain | Tier 5 (#53) |
| Page cache | > 95% capacity | Wait for eviction | Future |
| Transaction pool | > 80% capacity | Wait for tx completion | Future |

Each future pressure source will use the signaling mechanism most appropriate to its shape. No shared abstraction until at least two sources are implemented.

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

Existing tests continue using `dbe.CreateQuickTransaction()` with `[Obsolete]` suppression. **New tests** for UoW lifecycle (#49) use the new API. A separate follow-up PR migrates existing tests — keeping the mechanical refactor isolated from the functional changes.

```csharp
// Existing tests — unchanged, suppressed warning:
#pragma warning disable CS0618
using var tx = dbe.CreateQuickTransaction();
#pragma warning restore CS0618

// New UoW tests:
using var uow = dbe.CreateUnitOfWork();
using var tx = uow.CreateTransaction();
```

### Phase 3: Migrate Existing Tests (Follow-Up PR)

Separate PR that mechanically migrates all existing tests to UoW-based API:

```csharp
// Before: using var tx = dbe.CreateQuickTransaction();
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
  #50  CompRevStorageElement UoW ID stamping         (M — storage layout + visibility)

Week 2: Core class + registry
  #49  UnitOfWork class                             (L — class + API migration + tests)
  #51  UoW Registry                                 (M — persistent data structure)

Week 3: Integration
  #52  Resource back-pressure                       (S — admission control)
  ──   Test migration + integration testing
```

**Critical path:** #48 → #50 → #51 (registry needs UoW ID field in storage), and #48 → #49 → #52 (back-pressure needs UoW class).

---

## 10. Files to Create/Modify

### New Files

| File | Sub-Issue | Description |
|------|-----------|-------------|
| `src/Typhon.Engine/Execution/DurabilityMode.cs` | #48 | Enums: DurabilityMode, DurabilityOverride, UnitOfWorkState |
| `src/Typhon.Engine/Execution/UnitOfWork.cs` | #49 | Core UoW class |
| `src/Typhon.Engine/Execution/UowRegistry.cs` | #51 | Persistent UoW registry with `ManualResetEventSlim` for back-pressure |
| `src/Typhon.Engine/Execution/TyphonResourceExhaustedException.cs` | #52 | Exception subclass with resource diagnostics |
| `test/Typhon.Engine.Tests/UnitOfWorkTests.cs` | #49 | UoW lifecycle, transaction ownership |
| `test/Typhon.Engine.Tests/UowRegistryTests.cs` | #51, #52 | Registry allocation, recovery, back-pressure |

### Modified Files

| File | Sub-Issue | Changes |
|------|-----------|---------|
| `src/Typhon.Engine/Data/ComponentTable.cs` | #50 | CompRevStorageElement: 10 → 12 bytes, new `_packedUowId` field |
| `src/Typhon.Engine/Data/DatabaseEngine.cs` | #49, #52 | Add `CreateUnitOfWork()` with admission check, deprecate `CreateTransaction()` |
| `src/Typhon.Engine/Data/Transaction/Transaction.cs` | #49, #50 | Add `OwningUnitOfWork`/`OwnsUnitOfWork`, UoW ID stamping in Commit() |
| `src/Typhon.Engine/Data/Transaction/TransactionChain.cs` | #49 | `CreateTransaction(dbe, uow)` — forward UoW to `Init()` |
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
| `UoW_MultipleTransactions_SameUowId` | All transactions share the UoW's ID |
| `UoW_DisposedUoW_ThrowsOnCreateTx` | Cannot create transaction after dispose |
| `UoW_DurabilityMode_PreservedFromCreation` | Mode set at creation is accessible |
| `UoW_StateTransitions` | Free → Pending → WalDurable → Committed → Free |
| `UoW_Flush_NoOp_WithoutWAL` | Flush succeeds (no-op) without WAL infrastructure |
| `UoW_CancellationPropagates` | UoW disposal cancels outstanding operations |
| `UoW_CreateContext_ComposesDeadlines` | `CreateContext(timeout)` returns `Deadline.Min(uow, timeout)` |
| `UoW_CreateContext_DefaultUsesUowDeadline` | `CreateContext()` without timeout uses UoW's deadline |
| `UoW_TransactionCount_Increments` | TransactionCount tracks created transactions |
| `UoW_DoubleDispose_NoThrow` | Dispose() is idempotent |

### Transaction Binding Tests (#49)

| Test | What It Verifies |
|------|-----------------|
| `Tx_OwningUnitOfWork_SetViaInit` | `OwningUnitOfWork` is set through `Init()`, not a separate setter |
| `Tx_Reset_ClearsOwningUnitOfWork` | After `Reset()`, `OwningUnitOfWork` is null |
| `Tx_Reset_ClearsOwnsUnitOfWork` | After `Reset()`, `OwnsUnitOfWork` is false — prevents stale auto-dispose |
| `Tx_PoolReuse_NoPriorUowLeak` | A pooled transaction reused by a new UoW has no stale reference |
| `Tx_DeprecatedCreateTx_UowIdIsZero` | Transaction via deprecated `CreateTransaction()` has `OwningUnitOfWork = null`, effective UoW ID 0 |
| `QuickTx_DisposesUoW` | `CreateQuickTransaction()` transaction disposes its UoW on `Transaction.Dispose()` |
| `QuickTx_CommitThenDispose_CleanLifecycle` | Create → commit → dispose chain works end-to-end |

### Storage Tests (#50)

| Test | What It Verifies |
|------|-----------------|
| `CompRevStorageElement_UowId_RoundTrips` | 15-bit UoW ID stored and retrieved correctly |
| `CompRevStorageElement_IsolationFlag_InPackedUowId` | Flag moved to bit 15 of `_packedUowId` |
| `CompRevStorageElement_TSN_FullPrecision` | 48-bit TSN without bit 0 masking |
| `CompRevStorageElement_Size_Is12Bytes` | `sizeof(CompRevStorageElement) == 12` |
| `CompRevStorageElement_FitsChunk` | 5 elements per 64-byte chunk (overflow) |
| `CompRevStorageElement_FitsRootChunk` | 3 elements per root chunk (with 20-byte header) |
| `CompRevStorageElement_UowId_MaxValue` | UoW ID 32,767 (all 15 bits set) round-trips correctly |
| `CompRevStorageElement_UowId_Zero_Sentinel` | UoW ID 0 with IsolationFlag=0 returns correct field values |
| `CompRevStorageElement_IsolationAndUowId_Independent` | Setting one doesn't corrupt the other (bit-field isolation) |

### Visibility Tests (#50)

| Test | What It Verifies |
|------|-----------------|
| `Visibility_CommittedRevision_IsVisible` | IsolationFlag=0, TSN ≤ readerTSN → visible |
| `Visibility_UncommittedRevision_SameUoW_IsVisible` | IsolationFlag=1, same UoW ID → visible (read-your-writes) |
| `Visibility_UncommittedRevision_DifferentUoW_NotVisible` | IsolationFlag=1, different UoW ID → invisible |
| `Visibility_UowIdZero_AlwaysCommitted` | UoW ID 0 never consults registry, treated as committed |
| `Visibility_CommittedBeforeTSN_FastPath` | When `CommittedBeforeTSN = long.MaxValue`, bitmap never accessed |
| `Visibility_PostCrash_VoidedUoW_NotVisible` | After simulated crash, voided UoW's revisions are invisible |

### Registry Tests (#51)

| Test | What It Verifies |
|------|-----------------|
| `Registry_AllocateUowId_ReturnsUniqueIds` | No duplicate UoW IDs |
| `Registry_AllocateUowId_StartsAt1` | UoW ID 0 is never allocated (reserved sentinel) |
| `Registry_Release_MakesSlotFree` | Released UoW ID can be reused |
| `Registry_CrashRecovery_VoidsPending` | Pending entries voided on simulated recovery |
| `Registry_CrashRecovery_WalDurable_Preserved` | WalDurable entries survive recovery (not voided) |
| `Registry_CommittedBitmap_Accurate` | In-memory bitmap matches persistent state |
| `Registry_FullCapacity_WaitsForSlot` | When all slots used, `AllocateUowId` blocks until `Release()` signals the event |
| `Registry_FullCapacity_TimesOut` | When all slots used and deadline expires, throws `TyphonResourceExhaustedException` with correct usage/capacity |
| `Registry_BackPressure_SpuriousWake_Retries` | Event fires but another thread claims the slot first → waiter re-enters wait, does not throw |
| `Registry_BackPressure_TOCTOU_GracefulRetry` | Two threads pass admission check concurrently, one gets the last slot, other falls into event wait inside `AllocateUowId` |
| `Registry_BackPressure_CancellationToken_Aborts` | Cancellation token fires while waiting → returns promptly, throws `OperationCanceledException` |
| `Registry_ABA_UowIdReuse_SafeWithCommittedBeforeTSN` | UoW-A commits (ID=2), GC recycles slot 2, UoW-B grabs ID=2 in Pending state. Reader with TSN > UoW-A's revisions still sees UoW-A's data via CommittedBeforeTSN fast path (bitmap not consulted). Verifies no false invisibility from ID reuse |
| `Registry_VoidEntry_SetsCommittedBeforeTSN_ToZero` | Creating a Void entry transitions CommittedBeforeTSN from `long.MaxValue` to 0 |
| `Registry_AllVoidFreed_RestoresCommittedBeforeTSN` | When last Void entry is freed, CommittedBeforeTSN returns to `long.MaxValue` |
| `Registry_ConcurrentAllocation_NoDuplicates` | Multiple threads allocating simultaneously get unique IDs |
| `Registry_PageGrowth_OnDemand` | Allocating beyond root page capacity (>150) triggers overflow page |

### Integration Tests

| Test | What It Verifies |
|------|-----------------|
| `CreateEntity_ThroughUoW_Succeeds` | Full path: UoW → tx → create → commit |
| `ReadEntity_ThroughUoW_SeesCommitted` | MVCC visibility through UoW layer |
| `MultipleUoWs_ConcurrentAccess` | Parallel UoWs don't interfere |
| `UoW_WithTimeout_PropagatesDeadline` | Deadline flows from UoW to lock acquisitions |
| `UoW_CommitStampsUowId` | After commit, revision elements carry the UoW's ID |
| `UoW_MultipleCommits_AllStampedSameId` | Multiple transactions in one UoW all stamp the same UoW ID |
| `DeprecatedApi_StampsUowIdZero` | Transactions via deprecated API stamp UoW ID 0 |
| `CrossUoW_IsolationCorrect` | UoW-A's uncommitted revisions invisible to UoW-B's transactions |

---

## 12. Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| **UoW pooling** | Not pooled (heap-allocated) | UoWs are infrequent (1 per tick/request), CTS must be fresh each time |
| **UoW ID size** | 15 bits (32,767 max) | Sufficient for any realistic concurrency level |
| **UoW ID 0 semantics** | Reserved sentinel = "always committed" | Legacy `CreateTransaction()` uses UoW ID 0. Visibility check treats 0 as implicitly committed. Registry allocates starting at 1. IsolationFlag already handles crash safety for uncommitted revisions |
| **UoW → Transaction binding** | Persistent reference via `Init()` parameter (not a separate setter) | 8 bytes per pooled object. Set at creation, cleared at `Reset()`. Init-path avoids forgotten/out-of-order calls. `OwnsUnitOfWork` also cleared at Reset to prevent stale auto-dispose. Prepares for WAL (Tier 5) |
| **UnitOfWorkState.Free** | Default value (= 0) | Zeroed memory is automatically Free — safe initial state for fresh pages. Enables array-indexed state tables |
| **CompRevStorageElement growth** | 10 → 12 bytes (per [ADR-027](../adr/027-even-sized-hot-path-structs.md)) | UoW ID + IsolationFlag in 2-byte `_packedUowId` field, root: 3/chunk (was 4), overflow: 5/chunk (was 6) |
| **CompRevStorageElement field order** | ComponentChunkId first, `_packedUowId` at offset 10 | Preserves 4-byte alignment of most-accessed field. Same as current layout with 2 bytes appended |
| **IsolationFlag location** | Moved to `_packedUowId` bit 15 | Frees bit 0 of TSN, gains 1 bit precision, eliminates `& ~1L` masking |
| **Registry storage** | Flat array in growing `LogicalSegment` (not `ChunkBasedSegment`) | No dynamic alloc/free needed — fixed-index array where slot index = UoW ID. No chunks, no metadata, zero overhead |
| **Registry durability** | WAL-based (registry pages are checkpoint cache) | WAL already has FUA-capable file, group commit batching, ring buffer. No separate FUA path for registry. UoW creation cost amortized via group commit |
| **Registry entry** | 40 bytes — padded for clean page division, slot index = UoW ID | 6000/40=150 (root), 8000/40=200 (overflow) — exact fit, zero waste. Redundant UowId field removed. MaxTSN added for GC. 8 bytes reserved for future (MinTSN) |
| **Registry capacity** | 150 entries per root page, 200 per overflow | Growing segment: 1 page (8KB) for tests, scales to ~1.3MB at max capacity |
| **Registry recovery** | Checkpoint baseline + WAL delta replay | Torn registry pages are disposable — rebuild from WAL. No FPI needed for registry pages. Worst case = slower recovery, never data loss |
| **Allocation bitmap** | In-memory, rebuilt on startup | Persistent `UowRegistryEntry.State` is the source of truth. Avoids disk I/O on allocation hot path |
| **Visibility architecture** | Two-tier: CommittedBeforeTSN (Tier 1) + committed bitmap (Tier 2) | Normal operation: zero overhead (TSN comparison, bitmap never touched). Post-crash: bitmap filters ghost revisions temporarily. Performance-first: common path pays nothing, rare crash-recovery path pays ~5-10 cycles |
| **CommittedBeforeTSN** | `long.MaxValue` normally, `0` when Void entries exist | Solves UoW ID reuse ABA problem. Binary flag avoids storing MinTSN in entry. Future: precise threshold if MinTSN added to entry |
| **Committed bitmap role** | Cold-path crash-recovery fallback (not hot-path) | IsolationFlag handles per-transaction visibility. Bitmap only needed for ghost revisions from voided UoWs. Never accessed during normal operation |
| **RootFileHeader** | New `UowRegistrySPI` field | Same pattern as ComponentTableSPI, FieldTableSPI. Included in format version bump |
| **Back-pressure wait** | `ManualResetEventSlim` signaled by `Release()` | Event-based, not spin-polling. UoW slots are held ms–seconds (spinning wastes CPU). Near-instant wake, zero CPU burn while waiting. Spurious wakes are harmless (re-check loop) |
| **Back-pressure scope** | Registry slots only (no general abstraction) | Four future pressure sources have different shapes (binary vs threshold, different signals). Premature abstraction would be wasteful. Generalize when Tier 5 lands |
| **Back-pressure error** | `TyphonResourceExhaustedException` (subclass of `TyphonTimeoutException`) | Carries diagnostic context (resource path, current usage, max capacity). Existing `catch (TyphonTimeoutException)` still works; operators can catch subclass for finer diagnostics |
| **Admission check TOCTOU** | Fast-path optimization, not a guarantee | `AllocateUowId()` CAS provides real atomicity. If another thread races past the check, `AllocateUowId` falls into event-based wait internally. No correctness issue |
| **API deprecation** | `[Obsolete]` first, remove in v1.0 | Smooth migration, tests updated incrementally |
| **File format version** | Bumped (no in-place migration) | Pre-1.0, no production data to migrate |

---

## 13. Open Questions

1. ~~**Convenience API:** Resolved → yes, `CreateQuickTransaction()` with auto-dispose. See §8.~~

2. ~~**Test migration scope:** Resolved → incremental. Existing tests stay on deprecated API. See §8.~~

3. ~~**Registry page allocation:** Resolved → flat array in growing `LogicalSegment` (not chunks). 40-byte entries, 150/root + 200/overflow.~~

4. ~~**UoW ID 0 semantics:** Resolved → UoW ID 0 = "always committed" sentinel.~~

5. ~~**Visibility check scope:** Resolved → add `IsEpochVisible()` stub in #50, wired into visibility check. See §5.~~

6. ~~**DeferredCleanupManager interaction:** Resolved → no change needed. Deferred cleanup operates on TSN-based cutoffs (`MinTSN`), which are orthogonal to UoW state. Voided UoWs make revisions invisible to *readers*, but cleanup still GCs revision chain entries based on MinTSN. The cleanup path does not need to distinguish voided vs committed UoWs — it only cares whether any active transaction can still reference a revision's TSN.~~
