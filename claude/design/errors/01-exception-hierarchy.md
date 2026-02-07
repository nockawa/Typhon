# #37 — TyphonException Hierarchy

**Date:** 2026-02-06
**Status:** In progress
**GitHub Issue:** #37
**Decisions:** D1, D2, D6, D8, D10, D11

> 💡 **Quick summary:** Create `TyphonException` base class, `TyphonErrorCode` enum, 5 concrete exception subclasses, and extend `ThrowHelper`. ~8 new files, 2 modified files.

## Overview

Build the structured exception hierarchy that all Tier 1 (and future) error handling depends on. The hierarchy provides three levels of catch granularity:

```
catch (TyphonException)              → any engine error
catch (TyphonTimeoutException)       → any timeout (lock, transaction, query)
catch (LockTimeoutException)         → specific lock timeout
```

## Files to Create

### 1. `TyphonErrorCode` Enum

**Location:** `src/Typhon.Engine/Errors/TyphonErrorCode.cs`

```csharp
/// <summary>
/// Numeric error codes organized by subsystem range.
/// Only Tier 1 codes are defined; reserved ranges are filled by later tiers.
/// Codes within a range are assigned sequentially as needed; gaps are intentional
/// to allow insertion without renumbering.
/// </summary>
public enum TyphonErrorCode : int
{
    // 0 — Unspecified / generic
    Unspecified = 0,

    // 1xxx — Transaction
    TransactionTimeout = 1002,

    // 2xxx — Storage
    DataCorruption = 2003,
    StorageCapacityExceeded = 2004,

    // 3xxx — Component (reserved)
    // 4xxx — Index (reserved)
    // 5xxx — Query (reserved)

    // 6xxx — Resource
    ResourceExhausted = 6001,
    LockTimeout = 6003,

    // 7xxx — Durability (reserved)
}
```

### 2. `TyphonException` Base Class

**Location:** `src/Typhon.Engine/Errors/TyphonException.cs`

```csharp
/// <summary>
/// Base class for all Typhon engine exceptions.
/// Provides structured error information: numeric code and transience hint.
/// </summary>
public class TyphonException : Exception
{
    public TyphonException(TyphonErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public TyphonException(TyphonErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Numeric error code identifying the specific failure.</summary>
    public TyphonErrorCode ErrorCode { get; }

    /// <summary>
    /// Hint: true if this failure is temporary and retrying may succeed.
    /// The engine does NOT retry automatically — this is informational for callers.
    /// Default is false — subclasses must explicitly opt in to transience.
    /// </summary>
    public virtual bool IsTransient => false;
}
```

**Design notes:**
- `IsTransient` defaults to `false` — safe default, subclasses opt in explicitly (e.g., `TyphonTimeoutException` → `true`, `ResourceExhaustedException` → `true`)
- No `ErrorCategory` enum — the type hierarchy already provides classification. Callers catch specific types; adding a redundant enum is dead weight.
- No `Context` dictionary (D8) — metadata goes in typed properties on subclasses
- No nullable annotations per coding standards

### 3. `TyphonTimeoutException` Intermediate Base

**Location:** `src/Typhon.Engine/Errors/TyphonTimeoutException.cs`

```csharp
/// <summary>
/// Base for all timeout-related exceptions.
/// Enables <c>catch (TyphonTimeoutException)</c> to handle any timeout uniformly.
/// Does NOT inherit from System.TimeoutException (would break single-inheritance chain).
/// </summary>
public class TyphonTimeoutException : TyphonException
{
    public TyphonTimeoutException(TyphonErrorCode errorCode, string message, TimeSpan waitDuration, Exception innerException)
        : base(errorCode, message, innerException)
    {
        WaitDuration = waitDuration;
    }

    public TyphonTimeoutException(TyphonErrorCode errorCode, string message, TimeSpan waitDuration)
        : base(errorCode, message)
    {
        WaitDuration = waitDuration;
    }

    /// <summary>How long the caller waited before the timeout fired.</summary>
    public TimeSpan WaitDuration { get; }

    /// <summary>Timeouts are always transient — the resource is presumably available later.</summary>
    public override bool IsTransient => true;
}
```

### 4. `LockTimeoutException`

**Location:** `src/Typhon.Engine/Errors/LockTimeoutException.cs`

```csharp
/// <summary>
/// A lock acquisition (shared or exclusive) exceeded its deadline.
/// Always transient — the resource is presumably available later.
/// </summary>
public class LockTimeoutException : TyphonTimeoutException
{
    public LockTimeoutException(string resourceName, TimeSpan waitDuration)
        : base(TyphonErrorCode.LockTimeout, $"Lock timeout on '{resourceName}' after {waitDuration.TotalMilliseconds:F0}ms", waitDuration)
    {
        ResourceName = resourceName;
    }

    /// <summary>Name or path of the resource that could not be locked.</summary>
    public string ResourceName { get; }
}
```

### 5. `TransactionTimeoutException`

**Location:** `src/Typhon.Engine/Errors/TransactionTimeoutException.cs`

```csharp
/// <summary>
/// A transaction exceeded its overall deadline.
/// Tier 1 class — throw sites activated in Tier 2 when Execution Context is implemented.
/// </summary>
public class TransactionTimeoutException : TyphonTimeoutException
{
    public TransactionTimeoutException(long transactionId, TimeSpan waitDuration)
        : base(TyphonErrorCode.TransactionTimeout,
               $"Transaction {transactionId} timed out after {waitDuration.TotalMilliseconds:F0}ms", waitDuration)
    {
        TransactionId = transactionId;
    }

    /// <summary>ID of the transaction that timed out.</summary>
    public long TransactionId { get; }
}
```

### 6. `StorageException`

**Location:** `src/Typhon.Engine/Errors/StorageException.cs`

```csharp
/// <summary>
/// A storage-layer failure (I/O error, page fault, segment corruption).
/// </summary>
public class StorageException : TyphonException
{
    public StorageException(TyphonErrorCode errorCode, string message, Exception innerException)
        : base(errorCode, message, innerException) { }

    public StorageException(TyphonErrorCode errorCode, string message)
        : base(errorCode, message) { }
}
```

### 7. `CorruptionException`

**Location:** `src/Typhon.Engine/Errors/CorruptionException.cs`

```csharp
/// <summary>
/// Data integrity violation — checksum mismatch, structural corruption, invalid page state.
/// Never transient (inherits default false). Requires human intervention or restore from backup.
/// </summary>
public class CorruptionException : StorageException
{
    public CorruptionException(string componentName, int pageIndex, string detail)
        : base(TyphonErrorCode.DataCorruption,
               $"Corruption in '{componentName}' at page {pageIndex}: {detail}")
    {
        ComponentName = componentName;
        PageIndex = pageIndex;
    }

    /// <summary>Name of the component where corruption was detected.</summary>
    public string ComponentName { get; }

    /// <summary>Page index where corruption was detected, or -1 if not page-specific.</summary>
    public int PageIndex { get; }
}
```

### 8. Re-parent `ResourceExhaustedException`

**Location:** Modify existing `src/Typhon.Engine/Resources/ResourceExhaustedException.cs`

**Change:** `ResourceExhaustedException : Exception` → `ResourceExhaustedException : TyphonException`

Preserve the existing 3-constructor API, `ResourceType` enum parameter, `Utilization` computed property, and `FormatMessage` helper. Only the base class and its constructor calls change.

```csharp
[PublicAPI]
public class ResourceExhaustedException : TyphonException
{
    public string ResourcePath { get; }
    public ResourceType ResourceType { get; }
    public long CurrentUsage { get; }
    public long Limit { get; }
    public double Utilization => Limit > 0 ? (double)CurrentUsage / Limit : 1.0;

    // Constructor 1: Full details (existing signature preserved)
    public ResourceExhaustedException(string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        : base(TyphonErrorCode.ResourceExhausted,
               FormatMessage(resourcePath, currentUsage, limit))
    {
        ResourcePath = resourcePath;
        ResourceType = resourceType;
        CurrentUsage = currentUsage;
        Limit = limit;
    }

    // Constructor 2: Custom message (existing signature preserved)
    public ResourceExhaustedException(string message, string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        : base(TyphonErrorCode.ResourceExhausted, message)
    {
        ResourcePath = resourcePath;
        ResourceType = resourceType;
        CurrentUsage = currentUsage;
        Limit = limit;
    }

    // Constructor 3: Custom message + inner exception (existing signature preserved)
    public ResourceExhaustedException(string message, Exception innerException, string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        : base(TyphonErrorCode.ResourceExhausted, message, innerException)
    {
        ResourcePath = resourcePath;
        ResourceType = resourceType;
        CurrentUsage = currentUsage;
        Limit = limit;
    }

    /// <summary>
    /// Resource exhaustion is transient — the resource may self-heal (eviction, pool drain).
    /// </summary>
    public override bool IsTransient => true;

    private static string FormatMessage(string resourcePath, long currentUsage, long limit) =>
        $"Resource '{resourcePath}' exhausted: {currentUsage:N0} / {limit:N0} ({(limit > 0 ? (double)currentUsage / limit * 100 : 100):F1}% utilization)";
}
```

**Breaking change**: Code catching `catch (Exception ex) when (ex is ResourceExhaustedException)` still works. Code catching `catch (TyphonException)` now includes it. This is the correct behavior. All three existing constructors and the `Utilization` property are preserved — this is fully source-compatible.

### 9. Extract and Extend ThrowHelper

**Current location:** `ThrowHelper` is currently embedded inside `src/Typhon.Engine/Storage/Segments/ChunkAccessor.cs` (lines 44-52) with 2 methods (`ThrowArgument`, `ThrowInvalidOp`).

**Action:** Extract `ThrowHelper` into its own file at `src/Typhon.Engine/Errors/ThrowHelper.cs`. Move the existing 2 methods and add the new Tier 1 methods. Remove the class from `ChunkAccessor.cs` (leave a comment or just delete — the class was internal so no external impact).

```csharp
/// <summary>
/// Centralized throw helpers with [NoInlining] to keep hot-path method bodies small.
/// The JIT won't inline throw paths into callers, preserving cache-friendly code layout.
/// </summary>
internal static class ThrowHelper
{
    // --- Existing (moved from ChunkAccessor.cs) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgument(string message) => throw new ArgumentException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidOp(string message) => throw new InvalidOperationException(message);

    // --- New — Tier 1 ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowLockTimeout(string resourceName, TimeSpan waitDuration)
        => throw new LockTimeoutException(resourceName, waitDuration);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowResourceExhausted(string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        => throw new ResourceExhaustedException(resourcePath, resourceType, currentUsage, limit);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowCorruption(string componentName, int pageIndex, string detail)
        => throw new CorruptionException(componentName, pageIndex, detail);
}
```

## File Summary

| Action | File | Description |
|--------|------|-------------|
| **Create** | `src/Typhon.Engine/Errors/TyphonErrorCode.cs` | Error code enum with ranges |
| **Create** | `src/Typhon.Engine/Errors/TyphonException.cs` | Base exception |
| **Create** | `src/Typhon.Engine/Errors/TyphonTimeoutException.cs` | Timeout intermediate |
| **Create** | `src/Typhon.Engine/Errors/LockTimeoutException.cs` | Lock timeout |
| **Create** | `src/Typhon.Engine/Errors/TransactionTimeoutException.cs` | Transaction timeout |
| **Create** | `src/Typhon.Engine/Errors/StorageException.cs` | Storage base |
| **Create** | `src/Typhon.Engine/Errors/CorruptionException.cs` | Corruption detection |
| **Create** | `src/Typhon.Engine/Errors/ThrowHelper.cs` | Extracted from ChunkAccessor.cs + new throw methods |
| **Modify** | `src/Typhon.Engine/Resources/ResourceExhaustedException.cs` | Re-parent to TyphonException |
| **Modify** | `src/Typhon.Engine/Storage/Segments/ChunkAccessor.cs` | Remove embedded ThrowHelper class |

## Testing Strategy

### Unit Tests — `test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs`

- [ ] `TyphonException` has correct `ErrorCode` and default `IsTransient == false`
- [ ] `LockTimeoutException` has `ResourceName`, `WaitDuration`, and `IsTransient == true` (via `TyphonTimeoutException`)
- [ ] `TransactionTimeoutException` has `TransactionId` and `WaitDuration`
- [ ] `CorruptionException` is NOT transient (inherits base default `false`)
- [ ] `ResourceExhaustedException` re-parented: `is TyphonException` returns true
- [ ] `ResourceExhaustedException` preserves existing properties (`ResourcePath`, `ResourceType`, `CurrentUsage`, `Limit`, `Utilization`)
- [ ] Catch granularity: `catch (TyphonTimeoutException)` catches both `LockTimeoutException` and `TransactionTimeoutException`
- [ ] Catch granularity: `catch (TyphonException)` catches `ResourceExhaustedException`
- [ ] Inner exception propagation works through all constructors
- [ ] Error code uniqueness: no duplicate values in `TyphonErrorCode`

### Integration — Verify no regressions

- [ ] All existing tests pass (the re-parenting of `ResourceExhaustedException` should be source-compatible)

## Implementation Notes

- **Namespace**: All new types use `namespace Typhon.Engine;` (the project's flat namespace convention). Files are organized in the `Errors/` folder for structure, but the namespace stays flat — no `using` statements needed anywhere.
- **No serialization**: Pre-1.0, no need for `[Serializable]` or `ISerializable` on exceptions
- **Message format**: Include enough detail for diagnostics without exposing internal pointers or memory addresses
- **Coding standards**: Follow `.editorconfig` — expression-bodied members for simple properties, braces on new lines, `_camelCase` for private fields
- **ThrowHelper extraction**: When moving `ThrowHelper` out of `ChunkAccessor.cs`, verify no other code references it by fully-qualified name. The class is `internal` so only project-internal references exist.
