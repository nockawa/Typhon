# AccessControl Design Document

> A 64-bit reader-writer synchronization primitive with waiter fairness and telemetry support.

**Status:** Complete

---

## 1. Purpose

AccessControl is the **primary reader-writer lock** for Typhon, designed for scenarios requiring:

- High-concurrency read access (multiple simultaneous readers)
- Exclusive write access with promotion/demotion support
- Fairness guarantees via waiter counting
- Telemetry integration via `IContentionTarget` callback

### 1.1 Design Goals

| Goal | Rationale |
|------|-----------|
| **64-bit atomic state** | Single `Interlocked.CompareExchange` for all transitions |
| **Waiter fairness** | Prevent starvation via explicit waiter counters |
| **Zero external allocation** | No wait queues or heap allocations in the primitive |
| **WaitContext-based timeout** | All blocking operations accept `ref WaitContext` (deadline + cancellation) |
| **16-bit thread ID** | Supports servers with 500+ cores (max 65,535) |

### 1.2 Relationship to Other Types

For a full comparison of all AccessControl types, see [AccessControlFamily.md](AccessControlFamily.md).

| Type | Size | Waiters | Telemetry | Use Case |
|------|------|---------|-----------|----------|
| `AccessControl` | 64-bit | Tracked (fairness) | `IContentionTarget` | Full-featured RW lock |
| `AccessControlSmall` | 32-bit | Not tracked | `IContentionTarget` | Space-constrained scenarios |
| `ResourceAccessControl` | 32-bit | Not tracked | `IContentionTarget` | Resource lifecycle (Access/Modify/Destroy) |

---

## 2. Bit Layout

### 2.1 State Encoding (64-bit)

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ 63-62 │ 61-49    │  48      │ 47-32    │ 31-24    │ 23-16    │ 15-8     │ 7-0       │
│ State │ Reserved │Contention│ ThreadId │ Promoter │ Exclusive│ Shared   │ Shared    │
│   2   │   13     │  Flag    │   16     │ Waiters  │ Waiters  │ Waiters  │ Counter   │
│       │          │    1     │          │    8     │    8     │    8     │    8      │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

**Lower 32 bits:**
- Bits 0-7: Shared Usage Counter (max 255 concurrent readers)
- Bits 8-15: Shared Waiters Counter (fairness)
- Bits 16-23: Exclusive Waiters Counter (fairness)
- Bits 24-31: Promoter Waiters Counter (fairness)

**Upper 32 bits:**
- Bits 32-47: Thread ID (16 bits, max 65,535)
- Bit 48: Contention Flag (sticky, set when any thread had to wait)
- Bits 49-61: Reserved (13 bits, for future use)
- Bits 62-63: State (2 bits)

### 2.2 Constants

```csharp
// Masks
private const ulong SharedCounterMask     = 0x0000_0000_0000_00FF;  // Bits 0-7
private const ulong SharedWaitersMask     = 0x0000_0000_0000_FF00;  // Bits 8-15
private const ulong ExclusiveWaitersMask  = 0x0000_0000_00FF_0000;  // Bits 16-23
private const ulong PromoterWaitersMask   = 0x0000_0000_FF00_0000;  // Bits 24-31
private const ulong ThreadIdMask          = 0x0000_FFFF_0000_0000;  // Bits 32-47
private const ulong ContentionFlagMask    = 0x0001_0000_0000_0000;  // Bit 48
private const ulong StateMask             = 0xC000_0000_0000_0000;  // Bits 62-63

// Shifts
private const int SharedWaitersShift    = 8;
private const int ExclusiveWaitersShift = 16;
private const int PromoterWaitersShift  = 24;
private const int ThreadIdShift         = 32;

// States
private const ulong IdleState       = 0x0000_0000_0000_0000;  // 00
private const ulong ExclusiveState  = 0x4000_0000_0000_0000;  // 01
private const ulong SharedState     = 0x8000_0000_0000_0000;  // 10
// 11 reserved
```

---

## 3. State Machine

### 3.1 States

| Value | Name | Meaning |
|-------|------|---------|
| 00 | Idle | Not held by anyone |
| 01 | Exclusive | Held exclusively by one thread |
| 10 | Shared | Held by one or more readers |
| 11 | (Reserved) | Future use |

### 3.2 Transitions

```
                    EnterShared (count=1)
            ┌─────────────────────────────────┐
            │                                 ▼
         ┌──┴──┐    EnterExclusive      ┌─────────┐
         │ Idle│◄──────────────────────►│Exclusive│
         └──┬──┘    ExitExclusive       └────┬────┘
            │                                │
            │ EnterShared                    │ Demote
            │ (count++)                      │
            ▼                                ▼
       ┌────────┐                       ┌────────┐
       │ Shared │◄──────────────────────┤ Shared │
       │count>=1│       Promote         │count=1 │
       └────────┘                       └────────┘
            │
            │ ExitShared (count--)
            │ if count==0
            ▼
         ┌──────┐
         │ Idle │
         └──────┘
```

### 3.3 Waiter Priority

When multiple waiters exist, priority is enforced via waiter counters:

**Priority order:** Promoters > Exclusive > Shared

```csharp
// Shared can start only if no exclusive or promoter waiters
bool CanShareStart => (data & (PromoterWaitersMask | ExclusiveWaitersMask)) == 0;

// Exclusive can start only if no promoter waiters
bool CanExclusiveStart => (data & PromoterWaitersMask) == 0;

// Promote can always attempt when SharedCounter == 1
bool CanPromote => (data & SharedCounterMask) == 1;
```

---

## 4. API

### 4.1 Core Operations

```csharp
public struct AccessControl
{
    private ulong _data;

    // ═══════════════════════════════════════════════════════════════════
    // Shared Access (Multiple Concurrent Readers)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter shared access, waiting if necessary.
    /// Blocked when exclusive is held or higher-priority waiters exist.
    /// </summary>
    public bool EnterSharedAccess(ref WaitContext ctx, IContentionTarget target = null);

    /// <summary>
    /// Exit shared access. Must be called once per successful enter.
    /// </summary>
    public void ExitSharedAccess(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Exclusive Access (Single Writer)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter exclusive access, waiting if necessary.
    /// </summary>
    public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);

    /// <summary>
    /// Try to enter exclusive access without waiting.
    /// </summary>
    public bool TryEnterExclusiveAccess(IContentionTarget target = null);

    /// <summary>
    /// Exit exclusive access.
    /// </summary>
    public void ExitExclusiveAccess(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Promotion / Demotion
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Promote from shared to exclusive access.
    /// Caller must hold shared. Waits for other shared holders to release.
    /// </summary>
    public bool TryPromoteToExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);

    /// <summary>
    /// Demote from exclusive back to shared access.
    /// Caller must hold exclusive.
    /// </summary>
    public void DemoteFromExclusiveAccess(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reset to initial state. Only call when no threads are using this instance.
    /// </summary>
    public void Reset();
}
```

### 4.2 Diagnostic Properties

```csharp
public struct AccessControl
{
    /// <summary>True if exclusively held by the current thread.</summary>
    public bool IsLockedByCurrentThread { get; }

    /// <summary>Thread ID holding exclusive (16-bit), or 0.</summary>
    public int LockedByThreadId { get; }

    /// <summary>Current shared access count.</summary>
    public int SharedUsedCounter { get; }

    /// <summary>
    /// Returns true if this lock has ever experienced contention (a thread had to wait).
    /// This flag is sticky - once set, it remains set until Reset() is called.
    /// </summary>
    public bool WasContended { get; }
}
```

---

## 5. Telemetry Integration

### 5.1 IContentionTarget Callback

Telemetry is provided via the `IContentionTarget` interface:

```csharp
public interface IContentionTarget
{
    TelemetryLevel TelemetryLevel { get; }
    void RecordContention(long waitTimeUs);
    void LogLockOperation(LockOperation operation, long elapsedUs);
}

public enum TelemetryLevel
{
    None = 0,   // No telemetry
    Light = 1,  // Contention events only
    Deep = 2    // All lock operations
}
```

### 5.2 LockOperation Values for AccessControl

```csharp
public enum LockOperation
{
    SharedAcquired,
    SharedReleased,
    SharedWaitStart,
    ExclusiveAcquired,
    ExclusiveReleased,
    ExclusiveWaitStart,
    PromoteToExclusiveAcquired,
    PromoteToExclusiveStart,
    DemoteToShared,
    TimedOut,
    Canceled,
}
```

### 5.3 Telemetry Usage

```csharp
// On acquisition after waiting:
if (hadToWait && level >= TelemetryLevel.Light)
{
    target?.RecordContention(ComputeElapsedUs(waitStartTicks));
}

// On all operations (Deep level):
if (level >= TelemetryLevel.Deep)
{
    target?.LogLockOperation(LockOperation.ExclusiveAcquired, elapsedUs);
}
```

---

## 6. Usage Examples

### 6.1 Basic Reader-Writer

```csharp
private AccessControl _lock;

public void ReadData()
{
    var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
    if (_lock.EnterSharedAccess(ref ctx))
    {
        try
        {
            // Multiple threads can read concurrently
        }
        finally
        {
            _lock.ExitSharedAccess();
        }
    }
}

public void WriteData()
{
    // NullRef = infinite wait (explicit opt-in)
    _lock.EnterExclusiveAccess(ref Unsafe.NullRef<WaitContext>());
    try
    {
        // Only one thread can write
    }
    finally
    {
        _lock.ExitExclusiveAccess();
    }
}
```

### 6.2 Promotion Pattern

```csharp
public void ReadThenMaybeWrite()
{
    var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
    if (_lock.EnterSharedAccess(ref ctx))
    {
        try
        {
            var data = ReadData();

            if (NeedsUpdate(data))
            {
                var promoteCtx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100));
                if (_lock.TryPromoteToExclusiveAccess(ref promoteCtx))
                {
                    try
                    {
                        WriteData(data);
                    }
                    finally
                    {
                        _lock.DemoteFromExclusiveAccess();
                    }
                }
            }
        }
        finally
        {
            _lock.ExitSharedAccess();
        }
    }
}
```

### 6.3 With Telemetry

```csharp
public class MyResource : IContentionTarget
{
    private AccessControl _lock;

    public TelemetryLevel TelemetryLevel => TelemetryLevel.Light;

    public void RecordContention(long waitTimeUs)
    {
        _contentionCount++;
        _totalWaitUs += waitTimeUs;
    }

    public void LogLockOperation(LockOperation op, long elapsedUs) { }

    public void DoWork()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
        if (_lock.EnterExclusiveAccess(ref ctx, this))  // Pass 'this' for telemetry
        {
            try { /* work */ }
            finally { _lock.ExitExclusiveAccess(this); }
        }
    }
}
```

---

## 7. Summary

| Aspect | Value |
|--------|-------|
| **State size** | 64 bits |
| **Modes** | Shared (multiple), Exclusive (single) |
| **Fairness** | Waiter counters: Promoters > Exclusive > Shared |
| **Wait mechanism** | `SpinWait` with adaptive yielding |
| **Max shared count** | 255 |
| **Max waiter counts** | 255 each (shared, exclusive, promoter) |
| **Thread ID storage** | 16 bits (max 65,535) |
| **Telemetry** | `IContentionTarget` callback interface |

### Field Summary

| Field | Bits | Purpose |
|-------|------|---------|
| Shared Counter | 8 (0-7) | Count of current shared holders |
| Shared Waiters | 8 (8-15) | Fairness: threads waiting for shared |
| Exclusive Waiters | 8 (16-23) | Fairness: threads waiting for exclusive |
| Promoter Waiters | 8 (24-31) | Fairness: threads waiting to promote |
| ThreadId | 16 (32-47) | Owner thread ID (exclusive mode) |
| Contention Flag | 1 (48) | Sticky flag set when any thread had to wait |
| Reserved | 13 (49-61) | For future use |
| State | 2 (62-63) | Idle/Shared/Exclusive |

### Code Location

`src/Typhon.Engine/Concurrency/AccessControl.cs`
`src/Typhon.Engine/Concurrency/AccessControl.LockData.cs`
