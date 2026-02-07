# #40 — Result\<TValue, TStatus\> for Hot-Path Error Returns

**Date:** 2026-02-06
**Status:** Ready for implementation
**GitHub Issue:** #40
**Decisions:** D4

> 💡 **Quick summary:** Create `Result<TValue, TStatus>` readonly struct, 2 per-subsystem status enums, and apply to B+Tree lookups and revision chain reads. ~8 files modified, 3 new files. Zero performance overhead (benchmark-validated).

## Overview

Hot-path data access methods need a way to return "not found" or "snapshot invisible" without throwing exceptions. The `Result<TValue, TStatus>` dual-generic struct carries both the value and a per-subsystem status byte, at zero overhead compared to `bool + out`.

**Key principle:** `Result` handles expected non-error outcomes (not found, invisible, deleted). Actual errors (corruption, I/O failure, bounds violations) remain exceptions.

## The Result Struct

**Location:** `src/Typhon.Engine/Errors/Result.cs`

```csharp
/// <summary>
/// A zero-allocation result type for hot-path methods.
/// <typeparamref name="TValue"/> is the data; <typeparamref name="TStatus"/> is a per-subsystem byte enum.
/// Convention: status value 0 = Success in all enums.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Result<TValue, TStatus>
    where TValue : unmanaged
    where TStatus : unmanaged, Enum
{
    public readonly TValue Value;
    public readonly TStatus Status;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Result(TValue value)
    {
        Value = value;
        Status = default; // 0 = Success
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Result(TStatus status)
    {
        Value = default;
        Status = status;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Result(TValue value, TStatus status)
    {
        Value = value;
        Status = status;
    }

    /// <summary>True when Status == 0 (Success by convention).</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<TStatus, byte>(ref Unsafe.AsRef(in Status)) == 0;
    }

    /// <summary>True when Status != 0.</summary>
    public bool IsFailure
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsSuccess;
    }
}
```

**Design notes:**
- `StructLayout.Sequential` ensures predictable memory layout
- `Unsafe.As<TStatus, byte>` compiles to a single byte comparison — no boxing
- `where TValue : unmanaged` ensures the entire struct is blittable
- `where TStatus : unmanaged, Enum` constrains to byte/int enums
- Struct size: `sizeof(TValue) + 1 byte + padding` — fits in registers for small TValue types
- **Public readonly fields** (not validated properties): The struct is hot-path and zero-allocation. A validated `Value` getter that throws on failure would add branch overhead on every access, and callers must already check `IsSuccess` before using the value. This matches .NET's `Nullable<T>.Value` pattern in spirit, but without the overhead — callers check `IsSuccess`, then access `Value` directly. The overview doc (`10-errors.md`) is updated to reflect this choice.

## Per-Subsystem Status Enums

### `BTreeLookupStatus`

**Location:** `src/Typhon.Engine/Data/Index/BTreeLookupStatus.cs`

```csharp
/// <summary>Status codes for B+Tree key lookups.</summary>
public enum BTreeLookupStatus : byte
{
    /// <summary>Key found, value returned.</summary>
    Success = 0,

    /// <summary>Key does not exist in the tree.</summary>
    NotFound = 1,
}
```

### `RevisionReadStatus`

**Location:** `src/Typhon.Engine/Data/Revision/RevisionReadStatus.cs`

```csharp
/// <summary>Status codes for revision chain reads (MVCC-aware).</summary>
public enum RevisionReadStatus : byte
{
    /// <summary>Revision found and visible at this snapshot tick.</summary>
    Success = 0,

    /// <summary>Entity has no revision chain (never created).</summary>
    NotFound = 1,

    /// <summary>Revision exists but is not visible at the reader's snapshot tick.</summary>
    SnapshotInvisible = 2,

    /// <summary>Entity was tombstoned (deleted) at or before the reader's snapshot tick.</summary>
    Deleted = 3,
}
```

## Integration Points

### B+Tree Lookups

**Files:** `src/Typhon.Engine/Data/Index/BTree.cs` (and specialized variants L16, L32, L64, String64)

```csharp
// BEFORE (actual current signature):
public bool TryGet(TKey key, out int value, ref ChunkAccessor accessor)

// AFTER:
public Result<int, BTreeLookupStatus> TryGet(TKey key, ref ChunkAccessor accessor)
```

The `IBTree` interface also declares an unsafe variant that must change:
```csharp
// BEFORE:
unsafe bool TryGet(void* keyAddr, out int value, ref ChunkAccessor accessor);

// AFTER:
unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor accessor);
```

**Call site migration (14 sites):**

```csharp
// BEFORE (Transaction.cs):
var res = info.PrimaryKeyIndex.TryGet(pk, out firstChunkId, ref accessor);
if (!res)
{
    // handle not found
}

// AFTER:
var result = info.PrimaryKeyIndex.TryGet(pk, ref accessor);
if (result.IsFailure)
{
    // handle not found — result.Status == BTreeLookupStatus.NotFound
}
var firstChunkId = result.Value;
```

**Note:** `TryGetMultiple()` (used for multi-value indexes) returns `VariableSizedBufferAccessor<int>` and is **not** converted by this design. It has a different return pattern (`IsValid` property) and is a separate concern.

### Revision Chain Reads

**File:** `src/Typhon.Engine/Data/Transaction/Transaction.cs`

The MVCC-aware revision reading logic is in `GetCompRevInfoFromIndex()` — a **private** method in `Transaction.cs` that walks the revision chain and checks snapshot visibility:

```csharp
// BEFORE (actual current signature):
private bool GetCompRevInfoFromIndex(long pk, ComponentInfoSingle info, long tick,
    out ComponentInfoBase.CompRevInfo compRevInfo)

// AFTER:
private Result<ComponentInfoBase.CompRevInfo, RevisionReadStatus> GetCompRevInfoFromIndex(
    long pk, ComponentInfoSingle info, long tick)
```

**Why Transaction.cs, not ComponentRevisionManager?** `ComponentRevisionManager` has low-level helpers (`GetRevisionElement`, `AddCompRev`, `GrowChain`) that don't do MVCC visibility checks. The 4-state MVCC logic (not-found, invisible, deleted, success) lives in `Transaction.GetCompRevInfoFromIndex()`. This is where `RevisionReadStatus` provides value.

**Second overload:** `Transaction` also has a `GetCompRevInfoFromIndex(long pk, ComponentInfoMultiple info, long tick)` overload that returns `List<ComponentInfoBase.CompRevInfo>` for multi-value components. This overload is **not** converted — it returns a collection, not a single value, and has a different error-handling pattern. Only the `ComponentInfoSingle` overload is converted to `Result`.

**Call site benefit:** Callers can now distinguish between "never existed" and "tombstoned" and "snapshot invisible" — previously all three returned `false`. The 4-state enum enables richer error messages and diagnostic logging.

## What NOT to Convert

- **Chunk access** (`ChunkBasedSegment.GetChunkLocation`): This is pure arithmetic that throws `InvalidOperationException` on out-of-bounds — a hard error (corruption/programming bug), not an expected outcome. Result\<T\> is for expected non-error outcomes; bounds violations remain exceptions.
- **Schema lookups** (component registration, field metadata): Not hot-path; exceptions are fine
- **Transaction operations** (CreateEntity, Commit): Return types are already well-defined
- **Page cache operations**: Internal, not exposed to callers
- **Lock acquisitions**: Use exceptions (LockTimeoutException) because lock failure is always an error

## Benchmark Validation

The `test/Typhon.Benchmark/ResultPatternBenchmark.cs` confirms zero overhead:

| Pattern | Found (64 lookups) | Overhead |
|---------|-------------------|----------|
| `bool + out` | 785 ns | baseline |
| `Result<TValue, TStatus>` | 780 ns | -0.6% (noise) |

Status enum switch compiles to a jump table: 55 ns for 64 switch operations.

## File Summary

| Action | File | Description |
|--------|------|-------------|
| **Create** | `src/Typhon.Engine/Errors/Result.cs` | Result\<TValue, TStatus\> struct |
| **Create** | `src/Typhon.Engine/Data/Index/BTreeLookupStatus.cs` | B+Tree lookup status enum |
| **Create** | `src/Typhon.Engine/Data/Revision/RevisionReadStatus.cs` | Revision read status enum |
| **Modify** | `src/Typhon.Engine/Data/Index/BTree.cs` | TryGet signature change |
| **Modify** | `src/Typhon.Engine/Data/Index/IBTree.cs` | Interface TryGet signature change |
| **Modify** | `src/Typhon.Engine/Data/Transaction/Transaction.cs` | TryGet callers (3) + GetCompRevInfoFromIndex |
| **Modify** | `test/Typhon.Engine.Tests/Data/BTreeTests.cs` | TryGet caller update (1 site) |
| **Modify** | `test/Typhon.Engine.Tests/Data/TransactionTests.cs` | TryGet caller updates (10 sites) |

## Testing Strategy

- [ ] **Result struct correctness**: `IsSuccess` when status is default(0), `IsFailure` when non-zero
- [ ] **Result struct value preservation**: Value is correct when `IsSuccess`, default when `IsFailure`
- [ ] **BTree integration**: TryGet returns `NotFound` for missing keys, `Success` for present keys
- [ ] **Revision chain integration**: Returns all 4 status codes correctly for different MVCC scenarios
- [ ] **No boxing**: Verify `Unsafe.As<TStatus, byte>` path works for both enum types
- [ ] **Regression**: All existing tests using `bool + out` patterns are updated and pass

## Implementation Notes

- **Gradual migration**: Convert one subsystem at a time. B+Tree first (simplest — only 2 status values, 14 call sites), then revision chain (more complex MVCC logic, private method).
- **BTree TryGet returns `int`**: The value stored in the B+Tree is always an `int` (chunk ID). The Result type is `Result<int, BTreeLookupStatus>`, not generic over TValue.
- **IBTree interface**: The unsafe interface method must change in lockstep with the concrete implementation. All specialized variants (L16BTree, L32BTree, L64BTree, String64BTree) inherit from abstract `BTree<TKey>` so they get the change automatically.
- **Caller updates**: Each converted method requires updating all callers. Use the compiler errors after changing the signature to find them all.
- **`ref WaitContext` parameter**: The `Result` type does not replace the `ref WaitContext` parameter. Methods still take `ref WaitContext` for timeout (after #38); `Result` replaces the `bool` return + `out` parameter.
- **Namespace**: All new types use `namespace Typhon.Engine;` (flat namespace convention). Files are organized in folders for structure.
