# Epoch-Based Resource Management — Design Specification

| Field | Value |
|-------|-------|
| **Status** | In Progress |
| **GitHub Issue** | #69 |
| **Branch** | `feature/69-epoch-resource-management` |
| **Target** | Phase 0-4, incremental delivery |
| **Prerequisites** | Error Foundation (#36), existing concurrency primitives |
| **Related** | [Research](../research/epoch-resource-management/README.md), [ADR-007](../adr/007-clock-sweep-eviction.md), [CompRevDeferredCleanup](CompRevDeferredCleanup.md) |
| **Replaces** | Per-resource reference counting in PagedMMF + ChunkAccessor |

---

> 💡 **TL;DR:** Replace per-resource reference counting with epoch-scoped protection. One `EpochGuard` per transaction protects all page accesses within that scope — eliminating PinCounter, PromoteCounter, ChunkHandle, and the "all slots pinned" crash mode. Jump to [§4 Phase 0](#4-phase-0-epoch-foundation) for the core types, or [Quick Navigation](#quick-navigation) for the full map.

### Quick Navigation

| Section | What You'll Find |
|---------|-----------------|
| [§1 Scope & Summary](#1-scope--summary) | What changes, what doesn't |
| [§2 Prerequisites & Dependencies](#2-prerequisites--dependencies) | Build order, existing types reused |
| [§3 Open Questions Resolution](#3-open-questions-resolution) | All 5 research open questions resolved |
| [§4 Phase 0: Epoch Foundation](#4-phase-0-epoch-foundation) | `EpochManager`, `EpochThreadRegistry`, `EpochGuard` — full C# |
| [§5 Phase 1: Page Cache Dual-Mode](#5-phase-1-page-cache-dual-mode) | `PageInfo` changes, dual eviction predicate — full C# |
| [§6 Phase 2: ChunkAccessor v2](#6-phase-2-chunkacccessor-v2) | Complete rewrite, SOA layout, benchmarks — full C# |
| [§7 Phase 3: Caller Migration](#7-phase-3-caller-migration) | Transaction, B+Tree, CRUD integration — pseudocode |
| [§8 Phase 4: Cleanup](#8-phase-4-cleanup) | Dead code removal — bullet list |
| [Appendix A](#appendix-a-complete-type-inventory) | Type inventory (new, modified, deleted) |
| [Appendix B](#appendix-b-benchmark-plan) | Benchmark targets and methodology |

---

## 1. Scope & Summary

### What This Replaces

The current page-access model requires **2N obligations** (acquire + release) for N page accesses: every `ChunkAccessor` slot load calls `RequestPage` (incrementing `ConcurrentSharedCounter`), and every eviction or dispose calls `TransitionPageFromAccessToIdle` (decrementing it). The `PinCounter` and `PromoteCounter` fields add further per-access bookkeeping. This creates struct-copy safety bugs, complex exclusive access coordination, and the "all slots pinned" hard-crash failure mode.

Epoch-based resource management replaces this with **2 obligations total** per transaction: enter epoch scope, exit epoch scope. All pages accessed within a scope are protected from eviction by their `AccessEpoch` tag. See [research README](../research/epoch-resource-management/README.md) for the full analysis.

### Type Changes Summary

| Category | Types |
|----------|-------|
| **New** | `EpochManager`, `EpochThreadRegistry`, `EpochGuard` |
| **Modified** | `PageInfo`, `PagedMMF`, `ChangeSet`, `ChunkAccessor`, `Transaction`, `DatabaseEngine` |
| **Deleted** | `SlotData`, `SlotDataBuffer`, `PageAccessorBuffer`, `ChunkHandle`, `ChunkHandleUnsafe`, `StateSnapshot` (ChunkAccessor's) |

### Relationship to CompRevDeferredCleanup

These are **orthogonal concerns**:

- **EBR** protects **page lifetime** — ensures pages aren't evicted while any thread holds a reference.
- **CompRevDeferredCleanup** manages **revision lifetime** — garbage-collects old MVCC revisions that no active transaction can see.

Both use epoch/timestamp boundaries, but they operate on different resource types and can be implemented independently. EBR simplifies the cleanup walker's page access (no more PinCounter management).

---

## 2. Prerequisites & Dependencies

### Required Before Phase 0

| Prerequisite | Status | Why Needed |
|-------------|--------|------------|
| Error Foundation (#36) | ✅ Complete | Provides `Deadline`, `WaitContext`, `ThrowHelper`, `ResourceExhaustedException` |
| `AccessControlSmall` | ✅ Exists | Pattern for CAS latch + `WaitContext` integration |
| `IResource` / `IMetricSource` | ✅ Exists | Resource graph integration for `EpochManager` |
| `ResourceType` enum | ✅ Exists | Will use `ResourceType.Synchronization` for `EpochManager` |

### New ADR Required

A new ADR (extending ADR-007) documents epoch-based eviction predicate changes:

- **ADR-007 stays**: Clock-sweep algorithm unchanged — still circular scan, still counter-based second chance.
- **What changes**: The eviction predicate in `TryAcquire` adds an `AccessEpoch < MinActiveEpoch` check. A page cannot be evicted if any active epoch scope might still reference it.
- **Rationale**: Epoch tags replace `ConcurrentSharedCounter == 0` as the safety check.

---

## 3. Open Questions Resolution

The [research README](../research/epoch-resource-management/README.md) identified 5 open questions. All are resolved:

### Q1: Epoch Advancement Frequency

**Resolution:** Advance on outermost scope exit (no batching).

Each outermost `EpochGuard.Dispose()` calls `Interlocked.Increment(ref _globalEpoch)`. The ~8ns CAS cost is negligible compared to the 290–1220 cycle savings from eliminating ref-counting per page access. If future benchmarks show >5% overhead from advancement frequency, introduce batching (advance every N exits) as a tuning knob.

### Q2: ChangeSet Adaptation

**Resolution:** New `AddByMemPageIndex(int)` method on `ChangeSet`.

```csharp
public void AddByMemPageIndex(int memPageIndex)
{
    if (_changedMemoryPageIndices.Add(memPageIndex))
    {
        _owner.IncrementDirty(memPageIndex);
    }
}
```

The existing `Add(PageAccessor)` remains for backward compatibility during dual-mode Phase 1. The new overload accepts a raw `int` (memory page index) directly from `ChunkAccessor` — no `PageAccessor` allocation required.

### Q3: WAL Interaction

**Resolution:** Compatible by nesting. WAL operations execute within a transaction's epoch scope. Since `EpochGuard` supports nesting (depth tracking), WAL operations that access pages inherit the enclosing transaction's epoch protection. No WAL-specific epoch code is needed.

### Q4: Long-Running Queries

**Resolution:** Caller responsibility, same as MVCC. An epoch scope that spans minutes will delay page eviction for pages tagged during that scope — exactly like a long-running MVCC transaction delays revision cleanup. Callers needing long-lived access should use scope-splitting: exit and re-enter epoch scopes at natural boundaries (e.g., per-batch in a bulk scan). This matches existing MVCC guidance.

### Q5: Benchmark Targets

**Resolution:** Concrete targets defined in [§6.6](#66-benchmark-targets).

---

## 4. Phase 0: Epoch Foundation

Phase 0 creates the core epoch infrastructure with no existing code dependencies. All types are new and can be tested in isolation.

### 4.1 EpochManager

Singleton per `DatabaseEngine` instance. Owns the global epoch counter and the thread registry.

**File:** `src/Typhon.Engine/Concurrency/EpochManager.cs` (NEW)

```csharp
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Manages epoch-based resource protection. One instance per <see cref="DatabaseEngine"/>.
/// Threads enter/exit epoch scopes via <see cref="EpochGuard"/>; pages tagged with an epoch
/// cannot be evicted until all scopes referencing that epoch have exited.
/// </summary>
[PublicAPI]
public sealed class EpochManager : IResource, IMetricSource
{
    private long _globalEpoch;
    private readonly EpochThreadRegistry _registry;

    // === IResource implementation ===
    private readonly string _id;
    private readonly IResource _parent;
    private readonly List<IResource> _children = [];
    private readonly DateTime _createdAt = DateTime.UtcNow;
    private IResourceRegistry _owner;

    // === Metrics ===
    private long _epochAdvances;
    private long _scopeEnters;
    private long _registryExhaustionCount;

    public EpochManager(string id, IResource parent)
    {
        _id = id;
        _parent = parent;
        _globalEpoch = 1; // Start at 1 so 0 means "no epoch" / "not pinned"
        _registry = new EpochThreadRegistry();
    }

    /// <summary>Current global epoch value. Monotonically increasing.</summary>
    public long GlobalEpoch => _globalEpoch;

    /// <summary>
    /// The minimum epoch pinned by any active thread. Pages tagged with an epoch
    /// &gt;= this value cannot be evicted. Returns <see cref="GlobalEpoch"/> if no threads are active.
    /// </summary>
    public long MinActiveEpoch => _registry.ComputeMinActiveEpoch(_globalEpoch);

    /// <summary>Total number of epoch advances since creation.</summary>
    public long EpochAdvances => _epochAdvances;

    /// <summary>Total number of scope entries since creation.</summary>
    public long ScopeEnters => _scopeEnters;

    /// <summary>Number of active (pinned) slots in the thread registry.</summary>
    public int ActiveSlotCount => _registry.ActiveSlotCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Scope Management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter an epoch scope on the current thread. Pins the current global epoch,
    /// preventing eviction of pages tagged with this or later epochs.
    /// </summary>
    /// <returns>The depth before entering (0 for outermost scope).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int EnterScope()
    {
        _scopeEnters++;
        return _registry.PinCurrentThread(_globalEpoch);
    }

    /// <summary>
    /// Exit an epoch scope on the current thread. If this is the outermost scope,
    /// unpins the thread and advances the global epoch.
    /// </summary>
    /// <param name="expectedDepth">The depth returned by the matching <see cref="EnterScope"/> call.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitScope(int expectedDepth)
    {
        if (_registry.UnpinCurrentThread(expectedDepth))
        {
            // Outermost scope exited — advance the global epoch
            Interlocked.Increment(ref _globalEpoch);
            _epochAdvances++;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IResource
    // ═══════════════════════════════════════════════════════════════════════

    public string Id => _id;
    public ResourceType Type => ResourceType.Synchronization;
    public IResource Parent => _parent;
    public IEnumerable<IResource> Children => _children;
    public DateTime CreatedAt => _createdAt;
    public IResourceRegistry Owner => _owner;

    public bool RegisterChild(IResource child)
    {
        _children.Add(child);
        return true;
    }

    public bool RemoveChild(IResource resource) => _children.Remove(resource);

    // ═══════════════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════════════

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("EpochAdvances", _epochAdvances);
        writer.WriteThroughput("ScopeEnters", _scopeEnters);
        writer.WriteCapacity(_registry.ActiveSlotCount, EpochThreadRegistry.MaxSlots);
        writer.WriteThroughput("RegistryExhaustions", _registryExhaustionCount);
    }

    public void ResetPeaks()
    {
        // No high-water marks currently tracked
    }

    /// <summary>Increment exhaustion counter. Called by <see cref="EpochThreadRegistry"/> on slot exhaustion.</summary>
    internal void RecordRegistryExhaustion() => _registryExhaustionCount++;

    public void Dispose()
    {
        _registry.Dispose();
    }
}
```

### 4.2 EpochThreadRegistry

Fixed-size array of thread-local epoch pins. Uses `[ThreadStatic]` for O(1) slot lookup after initial registration.

**File:** `src/Typhon.Engine/Concurrency/EpochThreadRegistry.cs` (NEW)

```csharp
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Fixed-size registry of per-thread epoch pins. Each thread claims a slot on first use
/// and releases it on thread death (via <see cref="EpochSlotHandle"/> CriticalFinalizerObject).
/// </summary>
/// <remarks>
/// <para>Memory layout: three parallel arrays (SOA) for cache-friendly MinActiveEpoch scan.</para>
/// <para><c>_pinnedEpochs[256]</c> is the hot array — scanned every time MinActiveEpoch is computed.
/// <c>_slotStates[256]</c> and <c>_depths[256]</c> are warm (accessed on enter/exit).
/// <c>_ownerThreads[256]</c> is cold (accessed only on registration and liveness checks).</para>
/// </remarks>
internal sealed class EpochThreadRegistry : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════════

    public const int MaxSlots = 256;

    internal const byte SlotFree = 0;
    internal const byte SlotActive = 1;

    // ═══════════════════════════════════════════════════════════════════════
    // SOA Storage
    // ═══════════════════════════════════════════════════════════════════════

    // Hot: scanned on every MinActiveEpoch computation
    private readonly long[] _pinnedEpochs = new long[MaxSlots];     // 0 = not pinned

    // Warm: read/written on every EnterScope/ExitScope
    private readonly byte[] _slotStates = new byte[MaxSlots];       // SlotFree / SlotActive
    private readonly int[] _depths = new int[MaxSlots];             // Nesting depth per slot

    // Cold: registration and liveness checks
    private readonly Thread[] _ownerThreads = new Thread[MaxSlots];

    // Slot allocation tracking
    private int _highWaterMark;  // Next slot to try for allocation (grows monotonically)
    private int _activeSlotCount;

    // Per-thread slot index (O(1) lookup after first registration)
    [ThreadStatic]
    private static int _threadSlotIndex;

    [ThreadStatic]
    private static bool _threadHasSlot;

    /// <summary>Number of slots currently owned by active threads.</summary>
    public int ActiveSlotCount => _activeSlotCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Slot Management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pin the current thread to the given epoch. Claims a slot on first call.
    /// </summary>
    /// <returns>Nesting depth before this call (0 = outermost).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PinCurrentThread(long epoch)
    {
        if (!_threadHasSlot)
        {
            ClaimSlot();
        }

        var slot = _threadSlotIndex;
        var depth = _depths[slot];
        _depths[slot] = depth + 1;

        if (depth == 0)
        {
            // Outermost scope: pin to current epoch
            _pinnedEpochs[slot] = epoch;
        }

        return depth;
    }

    /// <summary>
    /// Unpin the current thread if this is the outermost scope exit.
    /// </summary>
    /// <param name="expectedDepth">Depth returned by the matching <see cref="PinCurrentThread"/>.</param>
    /// <returns>True if this was the outermost scope exit (thread is now unpinned).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool UnpinCurrentThread(int expectedDepth)
    {
        var slot = _threadSlotIndex;
        var currentDepth = _depths[slot];

        // Depth validation: catch copy-safety violations
        if (currentDepth != expectedDepth + 1)
        {
            ThrowHelper.ThrowInvalidOp(
                $"EpochGuard depth mismatch: expected {expectedDepth + 1}, got {currentDepth}. " +
                "Probable cause: EpochGuard was copied or disposed out of order.");
        }

        _depths[slot] = expectedDepth;

        if (expectedDepth == 0)
        {
            // Outermost scope: unpin
            _pinnedEpochs[slot] = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Claim a free slot for the current thread. Called once per thread lifetime.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]  // Keep hot path (PinCurrentThread) small
    private void ClaimSlot()
    {
        var thread = Thread.CurrentThread;

        // Scan from high water mark first (fast path: no contention on new slots)
        for (int attempts = 0; attempts < MaxSlots; attempts++)
        {
            var candidate = Interlocked.Increment(ref _highWaterMark) - 1;
            if (candidate >= MaxSlots)
            {
                // Wrap around — need to reclaim dead thread slots
                break;
            }

            if (TryClaimSlot(candidate, thread))
            {
                return;
            }
        }

        // Slow path: scan all slots looking for dead-thread slots to reclaim
        for (int i = 0; i < MaxSlots; i++)
        {
            if (_slotStates[i] == SlotActive)
            {
                var owner = _ownerThreads[i];
                if (owner != null && !owner.IsAlive)
                {
                    // Thread died without cleanup — reclaim the slot
                    if (TryReclaimDeadSlot(i, thread))
                    {
                        return;
                    }
                }
            }
            else if (_slotStates[i] == SlotFree)
            {
                if (TryClaimSlot(i, thread))
                {
                    return;
                }
            }
        }

        ThrowHelper.ThrowResourceExhausted(
            "Concurrency/EpochThreadRegistry", ResourceType.Synchronization,
            _activeSlotCount, MaxSlots);
    }

    private bool TryClaimSlot(int index, Thread thread)
    {
        if (Interlocked.CompareExchange(ref _slotStates[index], SlotActive, SlotFree) == SlotFree)
        {
            _ownerThreads[index] = thread;
            _pinnedEpochs[index] = 0;
            _depths[index] = 0;
            _threadSlotIndex = index;
            _threadHasSlot = true;
            Interlocked.Increment(ref _activeSlotCount);

            // Register finalizer for thread death cleanup
            _ = new EpochSlotHandle(this, index);
            return true;
        }

        return false;
    }

    private bool TryReclaimDeadSlot(int index, Thread newOwner)
    {
        // The slot is marked active but the owning thread is dead.
        // Use CAS on the owner thread reference to claim it atomically.
        var oldOwner = _ownerThreads[index];
        if (oldOwner != null && !oldOwner.IsAlive &&
            Interlocked.CompareExchange(ref _ownerThreads[index], newOwner, oldOwner) == oldOwner)
        {
            _pinnedEpochs[index] = 0;
            _depths[index] = 0;
            _threadSlotIndex = index;
            _threadHasSlot = true;
            // activeSlotCount doesn't change — we're reusing an active slot
            return true;
        }

        return false;
    }

    /// <summary>
    /// Release a slot. Called by <see cref="EpochSlotHandle"/> finalizer on thread death.
    /// </summary>
    internal void FreeSlot(int index)
    {
        _pinnedEpochs[index] = 0;
        _depths[index] = 0;
        _ownerThreads[index] = null;
        _slotStates[index] = SlotFree;
        Interlocked.Decrement(ref _activeSlotCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MinActiveEpoch
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the minimum epoch pinned by any active thread.
    /// Returns <paramref name="currentGlobalEpoch"/> if no threads are pinned.
    /// </summary>
    /// <remarks>
    /// Scans all 256 slots. The _pinnedEpochs array is 2KB (256 × 8 bytes = 32 cache lines).
    /// At ~4 cycles per cache line, a full scan takes ~128 cycles (~40ns at 3.5GHz).
    /// This is called during eviction, which is already a slow path.
    /// </remarks>
    public long ComputeMinActiveEpoch(long currentGlobalEpoch)
    {
        var min = currentGlobalEpoch;

        // Scan with liveness check: skip slots whose owning thread has died
        for (int i = 0; i < MaxSlots; i++)
        {
            var pinned = _pinnedEpochs[i];
            if (pinned == 0)
            {
                continue;
            }

            // Liveness check: if the thread died, clear the slot
            if (_slotStates[i] == SlotActive)
            {
                var thread = _ownerThreads[i];
                if (thread != null && !thread.IsAlive)
                {
                    FreeSlot(i);
                    continue;
                }
            }

            if (pinned < min)
            {
                min = pinned;
            }
        }

        return min;
    }

    public void Dispose()
    {
        // Clear all slots — finalizers may still run but will see SlotFree
        for (int i = 0; i < MaxSlots; i++)
        {
            _slotStates[i] = SlotFree;
            _pinnedEpochs[i] = 0;
            _ownerThreads[i] = null;
        }
    }
}

/// <summary>
/// CriticalFinalizerObject attached to each thread that claims an epoch slot.
/// When the thread dies (and GC collects the ThreadStatic reference), the finalizer
/// releases the slot back to the registry.
/// </summary>
internal sealed class EpochSlotHandle : System.Runtime.ConstrainedExecution.CriticalFinalizerObject
{
    private readonly EpochThreadRegistry _registry;
    private readonly int _slotIndex;

    internal EpochSlotHandle(EpochThreadRegistry registry, int slotIndex)
    {
        _registry = registry;
        _slotIndex = slotIndex;
    }

    ~EpochSlotHandle()
    {
        _registry.FreeSlot(_slotIndex);
    }
}
```

### 4.3 EpochGuard

Lightweight `ref struct` scope wrapper. The only user-facing type for epoch management.

**File:** `src/Typhon.Engine/Concurrency/EpochGuard.cs` (NEW)

```csharp
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// RAII scope guard for epoch-based resource protection.
/// Enter via <see cref="Enter"/>, exit via <see cref="Dispose"/>.
/// Supports nesting — only the outermost scope advances the global epoch.
/// </summary>
/// <remarks>
/// <para>Copy safety: depth validation in <see cref="Dispose"/> detects misuse
/// (e.g., accidental struct copy disposing twice). A copied guard would have a stale
/// <c>_expectedDepth</c> that won't match the registry's current depth.</para>
/// <para>This is a ref struct to prevent heap allocation and boxing.
/// Always use in a <c>using</c> statement or explicit try/finally.</para>
/// </remarks>
public ref struct EpochGuard
{
    private readonly EpochManager _manager;
    private readonly int _expectedDepth;
    private bool _disposed;

    private EpochGuard(EpochManager manager, int depth)
    {
        _manager = manager;
        _expectedDepth = depth;
        _disposed = false;
    }

    /// <summary>
    /// Enter an epoch scope. Returns a guard that must be disposed to exit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EpochGuard Enter(EpochManager manager)
    {
        var depth = manager.EnterScope();
        return new EpochGuard(manager, depth);
    }

    /// <summary>
    /// Exit the epoch scope. If this is the outermost scope, advances the global epoch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _manager.ExitScope(_expectedDepth);
        }
    }
}
```

**Copy-safety proof:** If an `EpochGuard` is copied (which the compiler allows for ref structs in some contexts), both copies share the same `_expectedDepth`. The first `Dispose()` succeeds and decrements the depth. The second `Dispose()` finds `_disposed == true` and no-ops. If somehow both copies are live and undisposed, only the `_disposed` flag prevents double-exit. The depth validation in `UnpinCurrentThread` serves as a backup safety net for more exotic misuse patterns.

### 4.4 Error Integration

No new exception class. Epoch registry exhaustion reuses the existing `ResourceExhaustedException` via a convenience `ThrowHelper` method that encapsulates the arguments.

**Rationale:** Dedicated exception types in Typhon earn their existence through catch-site diversity (e.g., `LockTimeoutException` has 16+ throw sites). Epoch exhaustion has exactly one throw site (`ClaimSlot`) and no known need for type-specific catch handling. If that changes, promoting to a dedicated subclass is a trivial refactor.

**ThrowHelper addition** (modify existing `ThrowHelper.cs`):

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
public static void ThrowEpochRegistryExhausted()
    => throw new ResourceExhaustedException(
        "Concurrency/EpochThreadRegistry",
        ResourceType.Synchronization,
        EpochThreadRegistry.MaxSlots,
        EpochThreadRegistry.MaxSlots);
```

This gives `ClaimSlot` a clean one-line call site (`ThrowHelper.ThrowEpochRegistryExhausted()`) while keeping the error hierarchy flat. Callers catching `ResourceExhaustedException` handle this case automatically.

### 4.5 Observability

`EpochManager` implements both `IResource` and `IMetricSource`:

**Resource graph position:**
```
DatabaseEngine (root)
  └── EpochManager (Synchronization)
```

**Metrics exposed via `IMetricWriter`:**

| Metric | Writer Method | Description |
|--------|-------------|-------------|
| `EpochAdvances` | `WriteThroughput` | Counter: total outermost scope exits |
| `ScopeEnters` | `WriteThroughput` | Counter: total scope entries (including nested) |
| (slot utilization) | `WriteCapacity` | Gauge: current / max (256) — unnamed, computed as utilization ratio by snapshot builder |
| `RegistryExhaustions` | `WriteThroughput` | Counter: ClaimSlot failures |

All metrics are read via plain field access (no locks) — atomic on x64 for ≤64-bit primitives.

### 4.6 Unit Tests

**File:** `test/Typhon.Engine.Tests/Concurrency/EpochManagerTests.cs` (NEW)

Test categories:

| Test | What It Validates |
|------|-------------------|
| `EnterExit_SingleThread_EpochAdvances` | Outermost exit increments GlobalEpoch |
| `NestedScopes_InnerExitDoesNotAdvance` | Nested exit doesn't increment; outer does |
| `MinActiveEpoch_SinglePinned_ReturnsPinnedValue` | One active scope pins MinActiveEpoch |
| `MinActiveEpoch_NoPinned_ReturnsGlobal` | No active scopes → MinActiveEpoch == GlobalEpoch |
| `MultiThread_MinActiveEpoch_ReturnsOldest` | Two threads, earliest pin wins |
| `ThreadDeath_SlotReclaimed` | Finalizer frees slot; new thread can claim it |
| `CopySafety_DoubleDispose_NoOp` | Copied guard's second Dispose is safe no-op |
| `RegistryExhaustion_ThrowsResourceExhausted` | 256 threads pinned → exception on 257th |
| `DepthMismatch_ThrowsInvalidOp` | Out-of-order dispose detected |
| `ScopeMetrics_Reported` | ReadMetrics produces expected counters |

### 4.7 Files Summary

| Action | File |
|--------|------|
| NEW | `src/Typhon.Engine/Concurrency/EpochManager.cs` |
| NEW | `src/Typhon.Engine/Concurrency/EpochThreadRegistry.cs` |
| NEW | `src/Typhon.Engine/Concurrency/EpochGuard.cs` |
| MODIFY | `src/Typhon.Engine/Errors/ThrowHelper.cs` (add `ThrowEpochRegistryExhausted`) |
| NEW | `test/Typhon.Engine.Tests/Concurrency/EpochManagerTests.cs` |

---

## 5. Phase 1: Page Cache Dual-Mode

Phase 1 adds epoch-tagged page access alongside existing ref-counting. Both mechanisms coexist, enabling incremental migration. Existing callers continue using `RequestPage`; new callers use `RequestPageEpoch`.

### 5.1 PageInfo Field Additions

**File:** `src/Typhon.Engine/Storage/PagedMMF.PageInfo.cs` (MODIFY)

Add `AccessEpoch` field to `PageInfo`. Keep `ConcurrentSharedCounter` for dual-mode:

```csharp
internal class PageInfo
{
    private const int ClockSweepMaxValue = 5;

    public readonly int MemPageIndex;
    public int FilePageIndex;
    public int ClockSweepCounter => _clockSweepCounter;
    public int DirtyCounter;

    public AccessControlSmall StateSyncRoot;
    public PageState PageState;
    public int LockedByThreadId;
    public int ConcurrentSharedCounter;

    /// <summary>
    /// The epoch at which this page was last accessed via epoch-based protection.
    /// Pages with AccessEpoch >= MinActiveEpoch cannot be evicted.
    /// Value 0 means "not epoch-tagged" (legacy access only).
    /// </summary>
    public long AccessEpoch;

    // ... rest unchanged ...
}
```

**Memory impact:** +8 bytes per `PageInfo` (one `long`). With 256 default pages, this is +2KB total — negligible.

### 5.2 Dual-Mode Eviction Predicate

**File:** `src/Typhon.Engine/Storage/PagedMMF.cs` (MODIFY)

The existing `TryAcquire` method currently checks:

```csharp
if (info.PageState is PageState.Free or PageState.Idle)
```

Under dual mode, an `Idle` page may still be epoch-protected. Add epoch check:

```csharp
private bool TryAcquire(PageInfo info, long minActiveEpoch)
{
    // First pass: check without locking
    var state = info.PageState;
    if (state != PageState.Free && state != PageState.Idle)
    {
        return false;
    }

    // Epoch check: if the page was accessed within an active epoch, skip it
    if (state == PageState.Idle && info.AccessEpoch >= minActiveEpoch)
    {
        return false;
    }

    // Second pass: under lock (same as existing code)
    try
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!info.StateSyncRoot.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/TryAcquire",
                TimeoutOptions.Current.PageCacheLockTimeout);
        }

        if (info.IOReadTask != null && info.IOReadTask.IsCompletedSuccessfully)
        {
            info.ResetIOCompletionTask();
        }

        if (info.PageState is PageState.Free or PageState.Idle)
        {
            // Re-check epoch under lock (may have changed since first pass)
            if (info.PageState == PageState.Idle && info.AccessEpoch >= minActiveEpoch)
            {
                return false;
            }

            if (info.PageState == PageState.Idle)
            {
                _memPageIndexByFilePageIndex.TryRemove(info.FilePageIndex, out _);
            }
            info.ResetClockSweepCounter();
            info.FilePageIndex = -1;
            info.AccessEpoch = 0;  // Clear epoch tag on reallocation
            info.PageState = PageState.Allocating;
            Interlocked.Decrement(ref _metrics.FreeMemPageCount);
            Debug.Assert(info.ConcurrentSharedCounter == 0);
            Debug.Assert(info.LockedByThreadId == 0);
            return true;
        }
        else
        {
            return false;
        }
    }
    finally
    {
        info.StateSyncRoot.ExitExclusiveAccess();
    }
}
```

The `AllocateMemoryPageCore` method passes `EpochManager.MinActiveEpoch` to `TryAcquire`. The `EpochManager` reference is stored as a field on `PagedMMF` (set during construction, null if epoch mode not enabled).

### 5.3 RequestPageEpoch — New Method

New method for epoch-protected shared access. Does **not** increment `ConcurrentSharedCounter` — the page is protected by its `AccessEpoch` tag instead.

**File:** `src/Typhon.Engine/Storage/PagedMMF.cs` (MODIFY — add method)

```csharp
/// <summary>
/// Request epoch-tagged shared access to a page. The page is protected from eviction
/// by its AccessEpoch tag rather than by ref-counting. Caller must be inside an
/// <see cref="EpochGuard"/> scope.
/// </summary>
/// <param name="filePageIndex">The file page to access.</param>
/// <param name="currentEpoch">Current global epoch to tag the page with.</param>
/// <param name="memPageIndex">Output: memory page index for direct address computation.</param>
/// <returns>True if the page is ready; false on timeout.</returns>
internal bool RequestPageEpoch(int filePageIndex, long currentEpoch, out int memPageIndex)
{
    // Fetch page to memory (same as RequestPage)
    if (!FetchPageToMemory(filePageIndex, out memPageIndex))
    {
        return false;
    }

    var pi = _memPagesInfo[memPageIndex];

    // Tag the page with the current epoch (max of existing tag and current)
    // This ensures a page accessed in epoch 5 and again in epoch 7 keeps the higher tag,
    // preventing premature eviction if epoch-5 scopes are still active
    long existing;
    do
    {
        existing = pi.AccessEpoch;
        if (currentEpoch <= existing)
        {
            break; // Already tagged with a >= epoch
        }
    } while (Interlocked.CompareExchange(ref pi.AccessEpoch, currentEpoch, existing) != existing);

    // Ensure data is ready (wait for pending I/O)
    var ioTask = pi.IOReadTask;
    if (ioTask != null && !ioTask.IsCompletedSuccessfully)
    {
        ioTask.GetAwaiter().GetResult();
        pi.ResetIOCompletionTask();
    }

    pi.IncrementClockSweepCounter();
    return true;
}

/// <summary>
/// Get the raw memory address for a memory page. Used by ChunkAccessor v2 to compute
/// chunk addresses directly without PageAccessor intermediary.
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal unsafe byte* GetMemPageRawDataAddress(int memPageIndex)
    => GetMemPageAddress(memPageIndex) + PageHeaderSize;
```

### 5.4 ChangeSet.AddByMemPageIndex

**File:** `src/Typhon.Engine/Storage/ChangeSet.cs` (MODIFY)

```csharp
/// <summary>
/// Mark a page as dirty by its memory page index directly.
/// Used by epoch-mode ChunkAccessor which doesn't hold PageAccessor instances.
/// </summary>
public void AddByMemPageIndex(int memPageIndex)
{
    if (_changedMemoryPageIndices.Add(memPageIndex))
    {
        _owner.IncrementDirty(memPageIndex);
    }
}
```

### 5.5 Tests & Regression

| Test | What It Validates |
|------|-------------------|
| `EpochTaggedPage_NotEvictedWhileScopeActive` | Page with AccessEpoch >= MinActiveEpoch survives clock sweep |
| `EpochTaggedPage_EvictedAfterScopeExit` | Page evictable once all referencing scopes exit |
| `DualMode_LegacyRefCount_StillWorks` | Existing `RequestPage` path unchanged |
| `DualMode_MixedAccess_BothProtected` | One page via legacy, one via epoch — both safe |
| `RequestPageEpoch_IOWait_Succeeds` | Page with pending disk I/O waits correctly |
| `AddByMemPageIndex_MarksDirty` | ChangeSet new overload increments dirty counter |

### 5.6 Files Summary

| Action | File |
|--------|------|
| MODIFY | `src/Typhon.Engine/Storage/PagedMMF.PageInfo.cs` (+`AccessEpoch` field) |
| MODIFY | `src/Typhon.Engine/Storage/PagedMMF.cs` (+`RequestPageEpoch`, modified `TryAcquire`) |
| MODIFY | `src/Typhon.Engine/Storage/ChangeSet.cs` (+`AddByMemPageIndex`) |
| NEW | `test/Typhon.Engine.Tests/Storage/EpochPageCacheTests.cs` |

---

## 6. Phase 2: ChunkAccessor v2

Phase 2 rewrites `ChunkAccessor` to use epoch-based protection. This is the largest single change — replacing the ~776-line struct with a simpler, faster version.

### 6.1 New SOA Memory Layout

The current `ChunkAccessor` is ~1KB. The new version is ~280 bytes:

```
Current layout (~1032 bytes):
  _pageIndices[16]     = 64 bytes    (SIMD searchable — KEEP)
  SlotDataBuffer[16]   = 256 bytes   (BaseAddress + HitCount + PinCounter + DirtyFlag + PromoteCounter)
  PageAccessorBuffer   = 16 × PageAccessor (large — each has owner ref, IO state, etc.)
  + segment ref, changeSet ref, mru, usedSlots, stride, rootHeaderOffset

New layout (~280 bytes):
  _pageIndices[16]     = 64 bytes    (SIMD searchable — KEEP)
  _baseAddresses[16]   = 128 bytes   (raw data address per slot — SOA for cache locality)
  _memPageIndices[16]  = 64 bytes    (memory page index per slot — for ChangeSet)
  _dirtyFlags          = 2 bytes     (ushort bitmask, 1 bit per slot)
  _clockHand           = 1 byte      (eviction cursor, wraps at _usedSlots)
  _mruSlot             = 1 byte      (most recently used slot)
  _usedSlots           = 1 byte      (high water mark)
  _stride              = 4 bytes     (cached chunk size)
  _rootHeaderOffset    = 4 bytes     (cached LogicalSegment.RootHeaderIndexSectionLength)
  + segment ref (8)    + changeSet ref (8) + pagedMMF ref (8) + epochManager ref (8)
```

**Cache line analysis:**
- `_pageIndices` = 1 cache line (64 bytes) — SIMD hot path
- `_baseAddresses` = 2 cache lines (128 bytes) — address lookup after SIMD hit
- `_memPageIndices` = 1 cache line — only touched on dirty flush
- Control fields (`_dirtyFlags` through `_rootHeaderOffset`) = packed into ~13 bytes

The SIMD search + address lookup now requires 3 cache lines max (vs 5+ for the current layout with interleaved SlotData + PageAccessor arrays).

### 6.2 Complete ChunkAccessor v2

**File:** `src/Typhon.Engine/Storage/Segments/ChunkAccessor.cs` (REWRITE)

```csharp
using JetBrains.Annotations;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine;

/// <summary>
/// Stack-allocated chunk accessor with epoch-based page protection.
/// - Zero heap allocation (struct, always pass by ref)
/// - SIMD-optimized page lookup
/// - MRU cache for repeated access
/// - Clock-hand eviction (no pinning, no "all slots pinned" failure)
/// - Fixed 16-slot capacity
/// WARNING: This struct is ~280 bytes. Always pass by ref.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkAccessor : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════
    // SOA Layout — optimized for cache line access patterns
    // ═══════════════════════════════════════════════════════════════════════

    // Hot: SIMD searchable page indices (1 cache line)
    private fixed int _pageIndices[Capacity];           // 64 bytes

    // Hot: base addresses for direct pointer arithmetic (2 cache lines)
    private fixed long _baseAddresses[Capacity];        // 128 bytes

    // Warm: memory page indices for ChangeSet dirty tracking (1 cache line)
    private fixed int _memPageIndices[Capacity];        // 64 bytes

    // ═══════════════════════════════════════════════════════════════════════
    // Control fields (packed)
    // ═══════════════════════════════════════════════════════════════════════

    private ushort _dirtyFlags;         // 2 bytes — bitmask, bit N = slot N is dirty
    private byte _clockHand;            // 1 byte — eviction cursor
    private byte _mruSlot;              // 1 byte — most recently used slot
    private byte _usedSlots;            // 1 byte — high water mark (0-16)
    private int _stride;                // 4 bytes — cached chunk size
    private int _rootHeaderOffset;      // 4 bytes — cached LogicalSegment.RootHeaderIndexSectionLength

    // ═══════════════════════════════════════════════════════════════════════
    // References (not part of hot path cache lines)
    // ═══════════════════════════════════════════════════════════════════════

    private ChunkBasedSegment _segment;
    private ChangeSet _changeSet;
    private PagedMMF _pagedMMF;
    private EpochManager _epochManager;

    // ═══════════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════════

    private const int Capacity = 16;
    private const int InvalidPageIndex = -1;

    public ChunkBasedSegment Segment => _segment;

    /// <summary>
    /// Create a new ChunkAccessor. Requires an active <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkAccessor(ChunkBasedSegment segment, PagedMMF pagedMMF,
        EpochManager epochManager, ChangeSet changeSet = null)
    {
        _segment = segment;
        _pagedMMF = pagedMMF;
        _epochManager = epochManager;
        _changeSet = changeSet;
        _mruSlot = 0;
        _usedSlots = 0;
        _clockHand = 0;
        _dirtyFlags = 0;
        _stride = segment.Stride;
        _rootHeaderOffset = LogicalSegment.RootHeaderIndexSectionLength;

        // Initialize page indices to invalid (-1)
        fixed (int* pageIndices = _pageIndices)
        {
            Unsafe.InitBlockUnaligned(pageIndices, 0xFF, 64);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Public API (3 methods — down from 8)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get mutable reference to chunk data.
    /// Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T GetChunk<T>(int chunkId, bool dirty = false) where T : unmanaged
        => ref Unsafe.AsRef<T>(GetChunkAddress(chunkId, dirty));

    /// <summary>
    /// Get read-only reference to chunk data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref readonly T GetChunkReadOnly<T>(int chunkId) where T : unmanaged
        => ref GetChunk<T>(chunkId, dirty: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Span<byte> GetChunkAsSpan(int index, bool dirtyPage = false)
        => new(GetChunkAddress(index, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index, bool dirtyPage = false)
        => new(GetChunkAddress(index, dirtyPage), _stride);

    internal void ClearChunk(int index)
    {
        var addr = GetChunkAddress(index);
        new Span<long>(addr, _stride / 8).Clear();
    }

    internal void DirtyChunk(int index)
    {
        (int si, _) = _segment.GetChunkLocation(index);
        for (int i = 0, used = 0; used < _usedSlots; i++)
        {
            if (_pageIndices[i] == InvalidPageIndex)
            {
                continue;
            }

            ++used;
            if (_pageIndices[i] == si)
            {
                _dirtyFlags |= (ushort)(1 << i);
                return;
            }
        }
    }

    /// <summary>
    /// Flush dirty pages to the ChangeSet.
    /// Call at the end of an atomic operation.
    /// </summary>
    public void CommitChanges()
    {
        if (_changeSet == null || _dirtyFlags == 0)
        {
            return;
        }

        var dirty = _dirtyFlags;
        while (dirty != 0)
        {
            var slot = BitOperations.TrailingZeroCount(dirty);
            _changeSet.AddByMemPageIndex(_memPageIndices[slot]);
            dirty &= (ushort)~(1 << slot);
        }

        _dirtyFlags = 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hot Path: GetChunkAddress
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CRITICAL HOT PATH: Three-tier page lookup.
    /// 1. MRU check (branch prediction friendly)
    /// 2. SIMD search (parallel scan of 16 slots)
    /// 3. Clock-hand eviction (no "all slots pinned" failure mode)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal byte* GetChunkAddress(int chunkId, bool dirty = false)
    {
        (int pageIndex, int offset) = _segment.GetChunkLocation(chunkId);

        // === ULTRA FAST PATH: MRU check ===
        var mru = _mruSlot;
        if (_pageIndices[mru] == pageIndex)
        {
            if (dirty)
            {
                _dirtyFlags |= (ushort)(1 << mru);
            }

            var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
            return (byte*)_baseAddresses[mru] + headerOffset + offset * _stride;
        }

        // === FAST PATH: SIMD search through cache ===
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            // Search first 8 slots
            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                var slot = BitOperations.TrailingZeroCount(mask0);
                return GetFromSlot(slot, pageIndex, offset, dirty);
            }

            // Search second 8 slots
            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                var slot = 8 + BitOperations.TrailingZeroCount(mask1);
                return GetFromSlot(slot, pageIndex, offset, dirty);
            }
        }

        // === SLOW PATH: Cache miss — evict and load ===
        return LoadAndGet(pageIndex, offset, dirty);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SIMD Hit Helper
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* GetFromSlot(int slotIndex, int pageIndex, int offset, bool dirty)
    {
        _mruSlot = (byte)slotIndex;

        if (dirty)
        {
            _dirtyFlags |= (ushort)(1 << slotIndex);
        }

        var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
        return (byte*)_baseAddresses[slotIndex] + headerOffset + offset * _stride;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Slow Path: Load + Evict
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte* LoadAndGet(int pageIndex, int offset, bool dirty)
    {
        var slot = FindEvictionSlot();
        EvictSlot(slot);
        LoadIntoSlot(slot, pageIndex);
        return GetFromSlot(slot, pageIndex, offset, dirty);
    }

    /// <summary>
    /// Clock-hand eviction with MRU skip. Unlike the old LRU scan, this is O(1) amortized
    /// and cannot fail (no pinned slots to skip).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindEvictionSlot()
    {
        // Fast path: unused slot available
        if (_usedSlots < Capacity)
        {
            return _usedSlots++;
        }

        // Clock-hand scan: skip MRU slot, evict first non-MRU
        var hand = _clockHand;
        var mru = _mruSlot;

        // At most 16 iterations (all slots are evictable — no pinning)
        for (int i = 0; i < Capacity; i++)
        {
            if (hand == mru)
            {
                hand = (byte)((hand + 1) % Capacity);
                continue;
            }

            _clockHand = (byte)((hand + 1) % Capacity);
            return hand;
        }

        // Fallback: MRU is the only slot (shouldn't happen with Capacity=16)
        _clockHand = (byte)((mru + 1) % Capacity);
        return mru;
    }

    /// <summary>
    /// Evict a slot: flush dirty state to ChangeSet. No page release needed —
    /// epoch protection handles lifetime.
    /// </summary>
    private void EvictSlot(int slot)
    {
        if (_pageIndices[slot] == InvalidPageIndex)
        {
            return;
        }

        // Flush dirty page to ChangeSet
        if ((_dirtyFlags & (1 << slot)) != 0 && _changeSet != null)
        {
            _changeSet.AddByMemPageIndex(_memPageIndices[slot]);
            _dirtyFlags &= (ushort)~(1 << slot);
        }

        _pageIndices[slot] = InvalidPageIndex;
    }

    /// <summary>
    /// Load a page into a slot using epoch-tagged access.
    /// </summary>
    private void LoadIntoSlot(int slot, int pageIndex)
    {
        // Request page via epoch path — no ref-counting
        _segment.GetPageEpochAccess(pageIndex, _epochManager.GlobalEpoch,
            out var memPageIndex, _pagedMMF);

        _pageIndices[slot] = pageIndex;
        _memPageIndices[slot] = memPageIndex;
        _baseAddresses[slot] = (long)_pagedMMF.GetMemPageRawDataAddress(memPageIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Exclusive Access (latch-based, decoupled from lifetime)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acquire exclusive (write) latch on a chunk's page.
    /// Epoch protection handles lifetime; this latch handles mutual exclusion.
    /// </summary>
    public bool TryLatchExclusive(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        // Find slot (must be loaded)
        var slot = FindSlotForPage(pageIndex);
        if (slot < 0)
        {
            return false;
        }

        // Acquire latch on the PageInfo
        return _pagedMMF.TryPromoteToExclusive(pageIndex,
            _pagedMMF.GetPageInfoByMemIndex(_memPageIndices[slot]),
            out _);
    }

    /// <summary>
    /// Release exclusive latch on a chunk's page.
    /// </summary>
    public void UnlatchExclusive(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        var slot = FindSlotForPage(pageIndex);
        if (slot >= 0)
        {
            var pi = _pagedMMF.GetPageInfoByMemIndex(_memPageIndices[slot]);
            _pagedMMF.DemoteExclusive(pi, PagedMMF.PageState.Shared);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSlotForPage(int pageIndex)
    {
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                return BitOperations.TrailingZeroCount(mask0);
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                return 8 + BitOperations.TrailingZeroCount(mask1);
            }
        }

        return -1;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Segment Header Access
    // ═══════════════════════════════════════════════════════════════════════

    internal ref T GetChunkBasedSegmentHeader<T>(int offset, bool dirty) where T : unmanaged
    {
        var addr = GetChunkAddress(0, dirty);
        var pageHeaderAddr = addr - _rootHeaderOffset - PagedMMF.PageHeaderSize;
        return ref Unsafe.AsRef<T>(pageHeaderAddr + offset);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose: dirty flush only (no page release)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dispose: flush all dirty pages to ChangeSet.
    /// No page locks to release — epoch handles lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_segment == null)
        {
            return;
        }

        // Flush all dirty slots
        if (_changeSet != null && _dirtyFlags != 0)
        {
            var dirty = _dirtyFlags;
            while (dirty != 0)
            {
                var slot = BitOperations.TrailingZeroCount(dirty);
                _changeSet.AddByMemPageIndex(_memPageIndices[slot]);
                dirty &= (ushort)~(1 << slot);
            }
        }

        _usedSlots = 0;
        _dirtyFlags = 0;
        _segment = null!;
    }
}
```

### 6.3 What's Eliminated

| Deleted Type/Field | Why |
|-------------------|-----|
| `SlotData` struct | Replaced by SOA arrays (`_baseAddresses`, `_memPageIndices`, `_dirtyFlags`) |
| `SlotDataBuffer` | Contained `SlotData` — no longer needed |
| `PageAccessorBuffer` | `PageAccessor` instances no longer held per slot |
| `ChunkHandle` ref struct | Pin-based scoping eliminated — direct references safe under epoch |
| `ChunkHandleUnsafe` ref struct | Same as above |
| `StateSnapshot` (ChunkAccessor's) | Pin/promote counters gone — snapshot validation simplified |
| `PinCounter` field | Epoch protection replaces per-slot pinning |
| `PromoteCounter` field | Latch-based exclusive replaces promote/demote ref counting |
| `HitCount` field | Clock-hand eviction doesn't need per-slot hit counts |
| `GetChunkHandle()` | No pinning → no handles needed |
| `GetChunkHandleUnsafe()` | Same |
| `GetChunkAddressAndPin()` | Same |
| `TryPromoteChunk()` | Replaced by `TryLatchExclusive()` |
| `DemoteChunk()` | Replaced by `UnlatchExclusive()` |
| `UnpinSlot()` | No pinning |
| `FindLRUSlot()` | Replaced by `FindEvictionSlot()` (clock-hand) |

### 6.4 Struct Size Comparison

| Component | Current | New | Delta |
|-----------|---------|-----|-------|
| `_pageIndices[16]` | 64 | 64 | 0 |
| Slot data | 256 (SlotDataBuffer) | 128+64+2 = 194 | -62 |
| Page accessors | ~512 (PageAccessorBuffer) | 0 | -512 |
| Control fields | ~16 | ~13 | -3 |
| References | ~16 (segment + changeSet) | ~32 (+pagedMMF, +epochManager) | +16 |
| **Total** | **~864** | **~303** | **~-561** |

### 6.5 Required Supporting Changes

`ChunkBasedSegment` needs a new method to support epoch-based page access:

```csharp
/// <summary>
/// Get epoch-tagged page access for a logical page index.
/// </summary>
internal void GetPageEpochAccess(int logicalPageIndex, long currentEpoch,
    out int memPageIndex, PagedMMF pagedMMF)
{
    var filePageIndex = GetFilePageIndex(logicalPageIndex);
    pagedMMF.RequestPageEpoch(filePageIndex, currentEpoch, out memPageIndex);
}
```

`PagedMMF` needs a helper to expose `PageInfo` by memory page index:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal PageInfo GetPageInfoByMemIndex(int memPageIndex) => _memPagesInfo[memPageIndex];
```

### 6.6 Benchmark Targets

| Operation | Current (est.) | Target | Measurement Method |
|-----------|---------------|--------|-------------------|
| MRU hit (GetChunkAddress) | ~5ns | ≤ 5ns | BenchmarkDotNet, single-chunk repeated access |
| SIMD hit (non-MRU cached) | ~8ns | ≤ 8ns | BenchmarkDotNet, rotating 4-chunk access |
| Slot eviction + load | ~50ns (excl. I/O) | ≤ 25ns | BenchmarkDotNet, 17-chunk rotating (forces eviction) |
| EpochGuard enter/exit pair | N/A (new) | ≤ 5ns | BenchmarkDotNet, isolated scope benchmark |
| CommitChanges (16 dirty) | ~200ns | ≤ 100ns | BenchmarkDotNet, all-dirty flush |
| Dispose (16 slots) | ~400ns | ≤ 150ns | BenchmarkDotNet, full-cache dispose |

**Gate criterion:** Phase 2 is complete when all targets are met. If MRU hit exceeds 5ns, profile before proceeding.

### 6.7 Files Summary

| Action | File |
|--------|------|
| REWRITE | `src/Typhon.Engine/Storage/Segments/ChunkAccessor.cs` |
| MODIFY | `src/Typhon.Engine/Storage/Segments/ChunkBasedSegment.cs` (+`GetPageEpochAccess`) |
| MODIFY | `src/Typhon.Engine/Storage/PagedMMF.cs` (+`GetPageInfoByMemIndex`) |
| NEW | `test/Typhon.Engine.Tests/Storage/ChunkAccessorV2Tests.cs` |
| NEW | `test/Typhon.Benchmark/ChunkAccessorBenchmarks.cs` |

---

## 7. Phase 3: Caller Migration

Phase 3 migrates all callers from the old `ChunkAccessor` API (handles, pin/unpin, promote/demote) to the new API (direct references, latch/unlatch). This is method-level pseudocode — the pattern is clear from the first few examples.

### 7.1 Transaction Integration

`Transaction` becomes the primary `EpochGuard` lifecycle owner.

```
Transaction.BeginWork():
    _epochGuard = EpochGuard.Enter(_databaseEngine.EpochManager)

Transaction.Commit():
    // ... existing commit logic ...
    _epochGuard.Dispose()

Transaction.Rollback():
    // ... existing rollback logic ...
    _epochGuard.Dispose()

Transaction.Dispose():
    if (_epochGuard is not disposed)
        _epochGuard.Dispose()  // Safety net
```

**Key change:** `ChunkAccessor` construction now requires the `PagedMMF` and `EpochManager` references. These are available through `ChunkBasedSegment.Owner` (the `ManagedPagedMMF` / `PagedMMF`) and `DatabaseEngine.EpochManager`.

### 7.2 B+Tree Migration

B+Trees are the heaviest ChunkAccessor users. Current code uses `ChunkHandle`/`TryPromoteChunk`/`DemoteChunk`. New code uses direct references and `TryLatchExclusive`/`UnlatchExclusive`.

**Before (lookup):**
```csharp
using var handle = accessor.GetChunkHandle(nodeChunkId);
ref var node = ref handle.AsRef<BTreeNode>();
// ... search node ...
```

**After (lookup):**
```csharp
ref var node = ref accessor.GetChunk<BTreeNode>(nodeChunkId);
// ... search node ...
// No handle dispose needed — epoch protects the reference
```

**Before (insert with split):**
```csharp
if (!accessor.TryPromoteChunk(nodeChunkId))
    return false; // retry
ref var node = ref accessor.GetChunk<BTreeNode>(nodeChunkId, dirty: true);
// ... modify node ...
accessor.DemoteChunk(nodeChunkId);
```

**After (insert with split):**
```csharp
if (!accessor.TryLatchExclusive(nodeChunkId))
    return false; // retry
ref var node = ref accessor.GetChunk<BTreeNode>(nodeChunkId, dirty: true);
// ... modify node ...
accessor.UnlatchExclusive(nodeChunkId);
```

### 7.3 ComponentTable/Transaction CRUD Migration

CRUD operations (Create, Read, Update, Delete on components) use ChunkAccessor to access revision chains and component data. The migration is mechanical:

- Remove all `ChunkHandle` / `ChunkHandleUnsafe` usage → direct `GetChunk<T>` calls
- Remove all `using var handle = ...` patterns → no scope needed
- Ensure `EpochGuard` is active (managed by `Transaction`)

### 7.4 Segment Operations Migration

`ChunkBasedSegment` internal operations (AllocateChunk, FreeChunk, GrowSegment) that create their own `ChunkAccessor` must wrap in `EpochGuard` if not called within a transaction scope:

```
ChunkBasedSegment.AllocateChunk():
    using var guard = EpochGuard.Enter(epochManager)  // if not nested
    var accessor = new ChunkAccessor(this, pagedMMF, epochManager, changeSet)
    // ... allocation logic ...
    accessor.Dispose()
```

### 7.5 Migration Order

Migrate in this order (read-only paths first, write paths last):

| Step | Target | Risk | Notes |
|------|--------|------|-------|
| 1 | B+Tree lookups (read-only) | Low | Remove handles, use direct refs |
| 2 | Revision chain walks (read-only) | Low | Remove handles in CompRevTable |
| 3 | Transaction.Read operations | Low | Verify epoch guard active |
| 4 | B+Tree inserts (single node) | Medium | Replace promote/demote with latch |
| 5 | B+Tree inserts (with split) | Medium | Multi-node latch ordering |
| 6 | Transaction.Create/Update/Delete | Medium | Full write path |
| 7 | Segment growth operations | Medium | Ensure epoch scope in segment ops |
| 8 | Index rebuild / bulk operations | Low | Wrap in epoch scope |

Each step must pass existing tests before proceeding to the next.

---

## 8. Phase 4: Cleanup

Phase 4 removes all legacy code that the epoch-based system replaces. This is mechanical deletion.

### 8.1 Remove Ref-Counting from PagedMMF

- Remove `ConcurrentSharedCounter` from `PageInfo` (all callers now use epoch)
- Remove `LockedByThreadId` from `PageInfo` (exclusive latch uses `AccessControlSmall` directly)
- Remove `TransitionPageToAccess` method (replaced by `RequestPageEpoch`)
- Remove `TransitionPageFromAccessToIdle` method (no ref-count decrement needed)
- Remove `RequestPage` method (replaced by `RequestPageEpoch`)
- Remove the old `TryAcquire` overload (without `minActiveEpoch` parameter)

### 8.2 Simplify PageState Enum

Current 6 states → 4 states:

| Current State | Action |
|--------------|--------|
| `Free` | **KEEP** — page not allocated |
| `Allocating` | **KEEP** — page being allocated |
| `Idle` | **KEEP** — page loaded, not exclusively latched |
| `Shared` | **REMOVE** — no shared-access state needed (epoch handles this) |
| `Exclusive` | **KEEP** — page exclusively latched for writes |
| `IdleAndDirty` | **REMOVE** — merge with `Idle` (dirty tracked by `DirtyCounter > 0`) |

The eviction predicate becomes:
```
Evictable = (PageState == Idle) AND (DirtyCounter == 0) AND (AccessEpoch < MinActiveEpoch)
```

### 8.3 Delete Old Types

- `SlotData` struct
- `SlotDataBuffer` inline array
- `PageAccessorBuffer` inline array
- `ChunkHandle` ref struct
- `ChunkHandleUnsafe` ref struct
- `ChunkAccessor.StateSnapshot` struct
- `PageAccessor` struct (if all callers migrated to epoch access)

### 8.4 Documentation Updates

- Update `claude/overview/03-storage.md` — PageState machine, eviction predicate
- Update `claude/overview/01-concurrency.md` — Add epoch section
- Create new ADR for epoch-based eviction (referenced in [§2](#2-prerequisites--dependencies))
- Archive this design document to `claude/archive/`
- Update `CLAUDE.md` high-level architecture section if needed

---

## Appendix A: Complete Type Inventory

### New Types

| Type | Kind | File | Phase |
|------|------|------|-------|
| `EpochManager` | class | `src/Typhon.Engine/Concurrency/EpochManager.cs` | 0 |
| `EpochThreadRegistry` | class | `src/Typhon.Engine/Concurrency/EpochThreadRegistry.cs` | 0 |
| `EpochSlotHandle` | class | `src/Typhon.Engine/Concurrency/EpochThreadRegistry.cs` | 0 |
| `EpochGuard` | ref struct | `src/Typhon.Engine/Concurrency/EpochGuard.cs` | 0 |

### Modified Types

| Type | Change | Phase |
|------|--------|-------|
| `PageInfo` | +`AccessEpoch` field | 1 |
| `PagedMMF` | +`RequestPageEpoch`, +`GetMemPageRawDataAddress`, +`GetPageInfoByMemIndex`, modified `TryAcquire` | 1 |
| `ChangeSet` | +`AddByMemPageIndex` method | 1 |
| `ChunkAccessor` | Complete rewrite | 2 |
| `ChunkBasedSegment` | +`GetPageEpochAccess` method | 2 |
| `Transaction` | +`EpochGuard` lifecycle | 3 |
| `DatabaseEngine` | +`EpochManager` field | 3 |
| `ThrowHelper` | +`ThrowEpochRegistryExhausted` | 0 |

### Deleted Types (Phase 4)

| Type | Reason |
|------|--------|
| `SlotData` | Replaced by SOA arrays |
| `SlotDataBuffer` | Container for `SlotData` |
| `PageAccessorBuffer` | `PageAccessor` no longer per-slot |
| `ChunkHandle` | Pin-based scoping eliminated |
| `ChunkHandleUnsafe` | Same |
| `ChunkAccessor.StateSnapshot` | Pin/promote counters gone |
| `PageAccessor` (potentially) | All callers use epoch access |

---

## Appendix B: Benchmark Plan

### Microbenchmarks (Phase 0)

```csharp
[Benchmark] public void EpochGuard_EnterExit()
{
    using var guard = EpochGuard.Enter(_epochManager);
}

[Benchmark] public void EpochGuard_NestedThreeLevels()
{
    using var g1 = EpochGuard.Enter(_epochManager);
    using var g2 = EpochGuard.Enter(_epochManager);
    using var g3 = EpochGuard.Enter(_epochManager);
}

[Benchmark] public void MinActiveEpoch_256Slots()
{
    _epochManager.MinActiveEpoch; // Property access triggers scan
}
```

### ChunkAccessor Benchmarks (Phase 2)

```csharp
[Benchmark] public void MRU_Hit()
{
    // Access same chunk repeatedly
    for (int i = 0; i < 1000; i++)
        accessor.GetChunkAddress(fixedChunkId);
}

[Benchmark] public void SIMD_Hit_4Chunks()
{
    // Rotate through 4 chunks (all cached, non-MRU)
    for (int i = 0; i < 1000; i++)
        accessor.GetChunkAddress(chunks[i % 4]);
}

[Benchmark] public void Eviction_17Chunks()
{
    // 17 chunks forces eviction on every access after warmup
    for (int i = 0; i < 17; i++)
        accessor.GetChunkAddress(chunks[i]);
}

[Benchmark] public void CommitChanges_AllDirty()
{
    // 16 slots all dirty
    accessor.CommitChanges();
}
```

### End-to-End Benchmarks (Phase 3)

```csharp
[Benchmark] public void BTreeLookup_1000Keys()
{
    // Existing BTree benchmark — should show improvement from handle elimination
}

[Benchmark] public void Transaction_CreateReadCommit()
{
    // Full transaction cycle — measures EpochGuard overhead vs ref-count savings
}
```

### Regression Gate

All existing benchmarks must not regress by more than 5%. If any benchmark regresses, investigate before proceeding. The epoch overhead (~8ns per outermost scope) should be recovered from eliminating per-page ref-count operations (~17ns saved per page access).
