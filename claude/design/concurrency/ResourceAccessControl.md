# ResourceAccessControl Design Document

> A specialized 32-bit synchronization primitive for protecting resources that support concurrent access, exclusive modification, and safe destruction.

---

## 1. Purpose

ResourceAccessControl is a **3-mode synchronization primitive** designed for scenarios where:

- Multiple threads need concurrent read/traversal access
- Occasional structural modifications must be exclusive but shouldn't invalidate active accessors
- Resource destruction must wait for all active users to complete

This primitive is specifically designed for data structures like linked chains, append-only lists, or pooled resources where:
- Accessors keep the resource "alive" but don't prevent modifications
- Modifications extend the structure without invalidating existing references
- Only destruction is truly incompatible with all other operations

### 1.1 Design Constraints

- **32-bit state**: Memory footprint matters, single `int` for entire state
- **No allocations**: No wait queue, no heap allocations
- **Lock-free fast path**: All state changes via `Interlocked.CompareExchange`
- **SpinWait for contention**: Waiters use `SpinWait` to yield CPU efficiently
- **API consistency**: Follow `Enter`/`Exit` naming pattern from `NewAccessControl` and `AccessControlSmall`

### 1.2 Relationship to Other Types

| Type | Size | Modes | Telemetry | Use Case |
|------|------|-------|-----------|----------|
| `AccessControlSmall` | 32-bit | Shared/Exclusive | None | Compact traditional RW lock |
| `NewAccessControl` | 64-bit | Shared/Exclusive | `IContentionTarget` callback | Full-featured RW lock with diagnostics |
| `ResourceAccessControl` | 32-bit | Accessing/Modify/Destroy | `IContentionTarget` callback | Resource lifecycle management |

**Key difference**: In `ResourceAccessControl`, MODIFY is **compatible** with ACCESSING. Modifiers can execute while accessors are active (for append-only/extend-only operations).

**Telemetry pattern**: Like `NewAccessControl`, telemetry is implemented via the `IContentionTarget` callback interface. Resources that want contention telemetry implement this interface and pass themselves to the Enter methods. The lock calls back on contention events. See [Observability: Track 4](../../overview/09-observability.md#track-4-per-resource-telemetry-icontentiontarget).

---

## 2. Problem Statement

### 2.1 The Scenario

Consider a `ChainedBlockAllocator`:

```
Chain: [Block A] → [Block B] → [Block C] → null

Thread 1: Enumerating the chain
Thread 2: Enumerating the chain
Thread 3: Enumerating, needs to append new block
Thread 4: Wants to destroy the chain
```

### 2.2 Requirements

| Operation | Concurrent with Accessing? | Concurrent with Modify? | Concurrent with Destroy? |
|-----------|---------------------------|------------------------|-------------------------|
| Accessing (enumerate) | Yes (multiple) | Yes | No |
| Modify (append) | Yes | No (single) | No |
| Destroy | No | No | No |

### 2.3 Why Standard Primitives Don't Fit

**ReaderWriterLock**: Writers block readers. But our "modify" operation is safe for active accessors - it extends the structure without invalidating existing references.

**Standard Latch (SH/UP/EX/KP)**: The KP mode protects against eviction, but EX is incompatible with KP. Our modify operation shouldn't conflict with "keep resource alive" semantics.

**Semaphore/Mutex**: Too coarse - doesn't capture the three distinct access patterns.

---

## 3. The Three Modes

### 3.1 ACCESSING Mode

**Purpose**: "I'm actively using this resource, don't destroy it"

**Semantics**:
- Multiple concurrent holders allowed
- Keeps resource alive (prevents destruction)
- Does not prevent modifications
- Blocked when MODIFY_PENDING or DESTROY is set (fairness)

**Typical use**: Enumeration/traversal of a data structure

### 3.2 MODIFY Mode

**Purpose**: "I need exclusive access to modify the structure"

**Semantics**:
- Single holder only (tracked by Thread ID)
- Compatible with ACCESSING holders (modifications don't invalidate active users)
- Sets MODIFY_PENDING to block new ACCESSING, waits for count to drain to 0
- Blocked when DESTROY is set or another MODIFY is held

**Typical use**: Appending to a chain, structural modifications that extend but don't invalidate

### 3.3 DESTROY Mode

**Purpose**: "I need to tear down this resource"

**Semantics**:
- Single holder only
- Incompatible with everything
- Sets DESTROY flag and waits for all ACCESSING and MODIFY holders to release
- Once acquired, resource can be safely deallocated
- **Terminal**: Once DESTROY is acquired, the primitive cannot be reused

**Typical use**: Resource cleanup, deallocation

---

## 4. Compatibility Matrix

```
               ACCESSING   MODIFY   DESTROY
ACCESSING          ✅        ✅        ❌
MODIFY             ✅        ❌        ❌
DESTROY            ❌        ❌        ❌
```

**Reading**: "Can [row] be held while [column] is acquired?"

- ACCESSING + ACCESSING: Multiple enumerators ✅
- ACCESSING + MODIFY: Enumerator active while modification happens ✅
- MODIFY + MODIFY: Only one modifier ❌
- DESTROY + anything: Destruction is exclusive ❌

---

## 5. Fairness Mechanism

### 5.1 The Starvation Problem

Without fairness, continuous ACCESSING acquisitions could starve MODIFY:

```
Thread M: EnterModify() - waiting for ACCESSING=0
Thread A1: EnterAccessing() ✓
Thread A2: EnterAccessing() ✓
Thread A1: ExitAccessing()
Thread A3: EnterAccessing() ✓    ← New ACCESSING keeps coming
Thread A2: ExitAccessing()
Thread A4: EnterAccessing() ✓    ← MODIFY never gets a chance
...forever...
```

### 5.2 The Solution: Pending Flag

When MODIFY is requested but cannot be immediately granted:

1. Set the MODIFY_PENDING flag
2. New ACCESSING acquisitions are blocked (TryEnter returns false, Enter spins)
3. Existing ACCESSING holders drain naturally
4. MODIFY is granted (Thread ID set)
5. MODIFY_PENDING flag cleared on release

```
Thread M: EnterModify()
          MODIFY_PENDING = 1, spin waiting for ACCESSING = 0...

Thread A5: TryEnterAccessing()
           MODIFY_PENDING = 1 → return false

Existing ACCESSING holders release...
ACCESSING = 0

Thread M: MODIFY granted (ThreadId set, MODIFY_PENDING = 0)

Thread M: ExitModify()
          ThreadId = 0

Thread A5: TryEnterAccessing() → true
```

### 5.3 Priority

DESTROY takes priority over MODIFY_PENDING:

| State | ACCESSING acquisition | MODIFY acquisition |
|-------|----------------------|-------------------|
| Neither pending | ✅ Allowed | ✅ Allowed |
| MODIFY_PENDING | ❌ Blocked | (waiter is spinning) |
| DESTROY | ❌ Blocked | ❌ Blocked |

---

## 6. State Layout

### 6.1 Bit Encoding (32-bit)

```
┌─────────────────────────────────────────────────────────────────┐
│                         32-bit State                            │
├─────────────────────────────────────────────────────────────────┤
│ Bits 0-9    │ ACCESSING count (0 to 1,023)                      │
│ Bits 10-19  │ MODIFY holder Thread ID (0 = not held)            │
│ Bit 20      │ MODIFY_PENDING flag                               │
│ Bit 21      │ DESTROY flag (terminal, never cleared)            │
│ Bits 22-31  │ Reserved (for future use)                         │
└─────────────────────────────────────────────────────────────────┘
```

> **Note on Telemetry**: Unlike the earlier design that embedded a 10-bit block ID in the state, telemetry is now handled via the `IContentionTarget` callback interface. Resources that want contention telemetry implement this interface and pass themselves to the Enter methods. This eliminates bit pressure and allows per-resource telemetry levels.

### 6.2 Constants

```csharp
private const int ACCESSING_COUNT_MASK   = 0x0000_03FF;  // Bits 0-9
private const int THREAD_ID_MASK         = 0x000F_FC00;  // Bits 10-19
private const int MODIFY_PENDING_FLAG    = 0x0010_0000;  // Bit 20
private const int DESTROY_FLAG           = 0x0020_0000;  // Bit 21
// Bits 22-31 reserved for future use

private const int THREAD_ID_SHIFT        = 10;
private const int MAX_ACCESSING_COUNT    = 1023;
```

### 6.3 Helper Methods

```csharp
private static int GetAccessingCount(int state) => state & ACCESSING_COUNT_MASK;
private static int GetThreadId(int state) => (state & THREAD_ID_MASK) >> THREAD_ID_SHIFT;
private static bool IsModifyHeld(int state) => GetThreadId(state) != 0;
private static bool IsModifyPending(int state) => (state & MODIFY_PENDING_FLAG) != 0;
private static bool IsDestroyed(int state) => (state & DESTROY_FLAG) != 0;
private static bool HasPendingOrDestroy(int state) => (state & (MODIFY_PENDING_FLAG | DESTROY_FLAG)) != 0;
```

---

## 7. API

### 7.1 Core Operations

```csharp
public struct ResourceAccessControl
{
    private int _state;

    // ═══════════════════════════════════════════════════════════════════
    // ACCESSING Mode - Multiple concurrent, prevents destruction
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to enter ACCESSING mode without blocking.
    /// </summary>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if MODIFY_PENDING or DESTROY is set</returns>
    public bool TryEnterAccessing(IContentionTarget target = null);

    /// <summary>
    /// Enter ACCESSING mode, spinning if necessary.
    /// Spins while MODIFY_PENDING or DESTROY is set.
    /// </summary>
    /// <param name="timeOut">Optional timeout. Null means wait indefinitely.</param>
    /// <param name="token">Optional cancellation token.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if timed out or canceled</returns>
    public bool EnterAccessing(TimeSpan? timeOut = null, CancellationToken token = default,
        IContentionTarget target = null);

    /// <summary>
    /// Exit ACCESSING mode. Must be called once per successful enter.
    /// </summary>
    /// <param name="target">Optional telemetry target (should match Enter call).</param>
    public void ExitAccessing(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // MODIFY Mode - Single holder, compatible with ACCESSING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to enter MODIFY mode without blocking.
    /// </summary>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired immediately, false if ACCESSING holders exist,
    /// another MODIFY is held, or DESTROY is set</returns>
    public bool TryEnterModify(IContentionTarget target = null);

    /// <summary>
    /// Enter MODIFY mode.
    /// Sets MODIFY_PENDING and spins until ACCESSING count reaches zero.
    /// Spins while DESTROY is set or another MODIFY is held.
    /// </summary>
    /// <param name="timeOut">Optional timeout. Null means wait indefinitely.</param>
    /// <param name="token">Optional cancellation token.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if timed out or canceled</returns>
    public bool EnterModify(TimeSpan? timeOut = null, CancellationToken token = default,
        IContentionTarget target = null);

    /// <summary>
    /// Exit MODIFY mode.
    /// </summary>
    /// <param name="target">Optional telemetry target (should match Enter call).</param>
    public void ExitModify(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Promotion - ACCESSING → MODIFY
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to promote from ACCESSING to MODIFY.
    /// Caller must hold ACCESSING. On success, caller holds MODIFY instead.
    /// Sets MODIFY_PENDING to block new ACCESSING, waits for count to drain to 1.
    /// </summary>
    /// <param name="timeOut">Optional timeout. Null means wait indefinitely.</param>
    /// <param name="token">Optional cancellation token.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if promoted, false if timed out, canceled, or DESTROY is set</returns>
    public bool TryPromoteToModify(TimeSpan? timeOut = null, CancellationToken token = default,
        IContentionTarget target = null);

    /// <summary>
    /// Demote from MODIFY back to ACCESSING.
    /// Caller must hold MODIFY. On return, caller holds ACCESSING instead.
    /// </summary>
    /// <param name="target">Optional telemetry target (should match Enter call).</param>
    public void DemoteFromModify(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // DESTROY Mode - Exclusive, terminal
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter DESTROY mode.
    /// Sets DESTROY flag and spins until ACCESSING=0 and MODIFY not held.
    /// This is a terminal operation - the primitive cannot be reused after success.
    /// </summary>
    /// <param name="timeOut">Optional timeout. Null means wait indefinitely.</param>
    /// <param name="token">Optional cancellation token.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if timed out or canceled</returns>
    public bool EnterDestroy(TimeSpan? timeOut = null, CancellationToken token = default,
        IContentionTarget target = null);

    // No ExitDestroy - destruction is final

    // ═══════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reset the primitive to initial state.
    /// WARNING: Only call when no threads are using this instance.
    /// </summary>
    public void Reset();
}
```

> **Telemetry Pattern**: The `IContentionTarget target` parameter follows the same pattern as `NewAccessControl`. When `target` is null (the default), no telemetry overhead occurs. When provided, the lock calls back to the target based on its `TelemetryLevel`:
> - **None**: No callbacks (zero overhead)
> - **Light**: `RecordContention(waitUs)` called when thread had to wait
> - **Deep**: `LogLockOperation(op, durationUs)` called for every Enter/Exit

### 7.2 Diagnostic Properties

```csharp
public struct ResourceAccessControl
{
    /// <summary>True if MODIFY is held by the current thread.</summary>
    public bool IsModifyHeldByCurrentThread { get; }

    /// <summary>Thread ID holding MODIFY (truncated to 10 bits), or 0 if not held.</summary>
    public int ModifyHolderThreadId { get; }

    /// <summary>Current ACCESSING count.</summary>
    public int AccessingCount { get; }

    /// <summary>True if MODIFY_PENDING is set.</summary>
    public bool IsModifyPending { get; }

    /// <summary>True if DESTROY has been acquired (terminal state).</summary>
    public bool IsDestroyed { get; }

    /// <summary>Get complete diagnostic state snapshot.</summary>
    public ResourceAccessControlState GetDiagnosticState();
}

public readonly struct ResourceAccessControlState
{
    public int AccessingCount { get; init; }
    public int ModifyHolderThreadId { get; init; }
    public bool ModifyPending { get; init; }
    public bool Destroyed { get; init; }
    public int RawState { get; init; }

    public override string ToString() =>
        $"Accessing={AccessingCount}, ModifyHolder={ModifyHolderThreadId}, " +
        $"ModifyPending={ModifyPending}, Destroyed={Destroyed}";
}
```

### 7.3 Scoped Guards

```csharp
public struct ResourceAccessControl
{
    /// <summary>
    /// Enter ACCESSING and return a disposable guard that exits on dispose.
    /// </summary>
    /// <exception cref="TimeoutException">If acquisition times out</exception>
    /// <exception cref="OperationCanceledException">If canceled</exception>
    public AccessingGuard EnterAccessingScoped(TimeSpan? timeOut = null, CancellationToken token = default);

    /// <summary>
    /// Enter MODIFY and return a disposable guard that exits on dispose.
    /// </summary>
    /// <exception cref="TimeoutException">If acquisition times out</exception>
    /// <exception cref="OperationCanceledException">If canceled</exception>
    public ModifyGuard EnterModifyScoped(TimeSpan? timeOut = null, CancellationToken token = default);
}

public readonly ref struct AccessingGuard
{
    private readonly ref int _stateRef;

    internal AccessingGuard(ref int state) => _stateRef = ref state;

    public void Dispose()
    {
        // Calls ExitAccessing logic on _stateRef
    }
}

public readonly ref struct ModifyGuard
{
    private readonly ref int _stateRef;

    internal ModifyGuard(ref int state) => _stateRef = ref state;

    public void Dispose()
    {
        // Calls ExitModify logic on _stateRef
    }
}
```

---

## 8. State Transitions

### 8.1 TryEnterAccessing

```
Can acquire if:
  - MODIFY_PENDING = 0
  - DESTROY = 0
  - ACCESSING count < MAX (1023)

State change on success:
  ACCESSING count += 1
```

### 8.2 ExitAccessing

```
Precondition:
  - ACCESSING count > 0

State change:
  ACCESSING count -= 1
```

### 8.3 TryEnterModify

```
Can acquire if:
  - ThreadId = 0 (no current MODIFY holder)
  - ACCESSING count = 0
  - DESTROY = 0

State change on success:
  ThreadId = CurrentManagedThreadId & 0x3FF
```

### 8.4 EnterModify

```
Phase 1 - Check and potentially set pending:
  If ThreadId != 0 OR DESTROY:
    Spin until conditions clear...

  If ACCESSING count > 0:
    Set MODIFY_PENDING = 1 (if not already)
    Spin until ACCESSING count = 0...

Phase 2 - Acquire:
  Conditions:
    - ThreadId = 0
    - ACCESSING count = 0
    - DESTROY = 0

  State change:
    ThreadId = CurrentManagedThreadId & 0x3FF
    MODIFY_PENDING = 0
```

### 8.5 ExitModify

```
Precondition:
  - ThreadId = CurrentManagedThreadId & 0x3FF

State change:
  ThreadId = 0
```

### 8.6 TryPromoteToModify

```
Precondition:
  - Caller holds ACCESSING (count >= 1)

Phase 1 - Set pending if needed:
  If ACCESSING count > 1:
    Set MODIFY_PENDING = 1
    Spin until ACCESSING count = 1...

Phase 2 - Promote:
  Conditions:
    - ACCESSING count = 1 (just the caller)
    - ThreadId = 0
    - DESTROY = 0

  State change (atomic):
    ACCESSING count = 0
    ThreadId = CurrentManagedThreadId & 0x3FF
    MODIFY_PENDING = 0
```

### 8.7 DemoteFromModify

```
Precondition:
  - ThreadId = CurrentManagedThreadId & 0x3FF

State change (atomic):
  ThreadId = 0
  ACCESSING count += 1
```

### 8.8 EnterDestroy

```
Phase 1 - Set DESTROY flag:
  Set DESTROY = 1 (atomic, never cleared)

Phase 2 - Wait for drain:
  Spin until:
    - ACCESSING count = 0
    - ThreadId = 0 (no MODIFY holder)

  On success: primitive is now terminal
```

---

## 9. Pseudo-Code Implementation

### 9.1 TryEnterAccessing

```csharp
public bool TryEnterAccessing()
{
    int state = Volatile.Read(ref _state);

    // Check if blocked by pending/destroy
    if (HasPendingOrDestroy(state))
        return false;

    // Check overflow
    int count = GetAccessingCount(state);
    if (count >= MAX_ACCESSING_COUNT)
        throw new InvalidOperationException("Max ACCESSING count exceeded");

    int newState = state + 1;  // Increment ACCESSING count

    return Interlocked.CompareExchange(ref _state, newState, state) == state;
}
```

### 9.2 EnterAccessing

```csharp
public bool EnterAccessing(TimeSpan? timeOut = null, CancellationToken token = default)
{
    DateTime deadline = timeOut.HasValue ? DateTime.UtcNow + timeOut.Value : DateTime.MaxValue;
    SpinWait spin = default;

    while (true)
    {
        if (token.IsCancellationRequested || DateTime.UtcNow >= deadline)
            return false;

        int state = Volatile.Read(ref _state);

        if (HasPendingOrDestroy(state))
        {
            spin.SpinOnce();
            continue;
        }

        int count = GetAccessingCount(state);
        if (count >= MAX_ACCESSING_COUNT)
            throw new InvalidOperationException("Max ACCESSING count exceeded");

        int newState = state + 1;

        if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            return true;

        spin.SpinOnce();
    }
}
```

### 9.3 ExitAccessing

```csharp
public void ExitAccessing()
{
    SpinWait spin = default;

    while (true)
    {
        int state = Volatile.Read(ref _state);

        Debug.Assert(GetAccessingCount(state) > 0, "ExitAccessing without matching enter");

        int newState = state - 1;  // Decrement ACCESSING count

        if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            return;

        spin.SpinOnce();
    }
}
```

### 9.4 TryEnterModify

```csharp
public bool TryEnterModify()
{
    int state = Volatile.Read(ref _state);

    // Cannot acquire if destroyed
    if (IsDestroyed(state))
        return false;

    // Cannot acquire if another thread holds MODIFY
    if (IsModifyHeld(state))
        return false;

    // Cannot acquire if there are ACCESSING holders
    if (GetAccessingCount(state) > 0)
        return false;

    int threadId = Environment.CurrentManagedThreadId & 0x3FF;
    int newState = (state & ~THREAD_ID_MASK) | (threadId << THREAD_ID_SHIFT);

    return Interlocked.CompareExchange(ref _state, newState, state) == state;
}
```

### 9.5 EnterModify

```csharp
public bool EnterModify(TimeSpan? timeOut = null, CancellationToken token = default)
{
    DateTime deadline = timeOut.HasValue ? DateTime.UtcNow + timeOut.Value : DateTime.MaxValue;
    SpinWait spin = default;
    int threadId = Environment.CurrentManagedThreadId & 0x3FF;

    while (true)
    {
        if (token.IsCancellationRequested || DateTime.UtcNow >= deadline)
            return false;

        int state = Volatile.Read(ref _state);

        // Cannot proceed if DESTROY is set
        if (IsDestroyed(state))
        {
            spin.SpinOnce();
            continue;
        }

        // Cannot proceed if another MODIFY is held
        if (IsModifyHeld(state))
        {
            spin.SpinOnce();
            continue;
        }

        // If ACCESSING count is zero, try to acquire directly
        if (GetAccessingCount(state) == 0)
        {
            // Clear MODIFY_PENDING (if set) and set ThreadId
            int newState = (state & ~(MODIFY_PENDING_FLAG | THREAD_ID_MASK))
                         | (threadId << THREAD_ID_SHIFT);

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                return true;

            spin.SpinOnce();
            continue;
        }

        // ACCESSING holders exist - set MODIFY_PENDING to block new ones
        if (!IsModifyPending(state))
        {
            int newState = state | MODIFY_PENDING_FLAG;
            Interlocked.CompareExchange(ref _state, newState, state);
            // Don't check result - either we set it or state changed
        }

        spin.SpinOnce();
    }
}
```

### 9.6 ExitModify

```csharp
public void ExitModify()
{
    SpinWait spin = default;
    int expectedThreadId = Environment.CurrentManagedThreadId & 0x3FF;

    while (true)
    {
        int state = Volatile.Read(ref _state);

        Debug.Assert(GetThreadId(state) == expectedThreadId,
            "ExitModify called by thread that doesn't hold MODIFY");

        int newState = state & ~THREAD_ID_MASK;  // Clear ThreadId

        if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            return;

        spin.SpinOnce();
    }
}
```

### 9.7 TryPromoteToModify

```csharp
public bool TryPromoteToModify(TimeSpan? timeOut = null, CancellationToken token = default)
{
    DateTime deadline = timeOut.HasValue ? DateTime.UtcNow + timeOut.Value : DateTime.MaxValue;
    SpinWait spin = default;
    int threadId = Environment.CurrentManagedThreadId & 0x3FF;

    while (true)
    {
        if (token.IsCancellationRequested || DateTime.UtcNow >= deadline)
            return false;

        int state = Volatile.Read(ref _state);

        int count = GetAccessingCount(state);
        Debug.Assert(count > 0, "TryPromoteToModify called without holding ACCESSING");

        // Cannot promote if DESTROY is set
        if (IsDestroyed(state))
            return false;

        // Cannot promote if another MODIFY is held
        if (IsModifyHeld(state))
            return false;

        // If we're the only ACCESSING holder, promote
        if (count == 1)
        {
            // Atomic: ACCESSING -= 1, ThreadId = current, clear MODIFY_PENDING
            int newState = (state - 1)  // Decrement ACCESSING
                         & ~(MODIFY_PENDING_FLAG | THREAD_ID_MASK)
                         | (threadId << THREAD_ID_SHIFT);

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                return true;

            spin.SpinOnce();
            continue;
        }

        // Other ACCESSING holders exist - set MODIFY_PENDING and wait
        if (!IsModifyPending(state))
        {
            int newState = state | MODIFY_PENDING_FLAG;
            Interlocked.CompareExchange(ref _state, newState, state);
        }

        spin.SpinOnce();
    }
}
```

### 9.8 DemoteFromModify

```csharp
public void DemoteFromModify()
{
    SpinWait spin = default;
    int expectedThreadId = Environment.CurrentManagedThreadId & 0x3FF;

    while (true)
    {
        int state = Volatile.Read(ref _state);

        Debug.Assert(GetThreadId(state) == expectedThreadId,
            "DemoteFromModify called by thread that doesn't hold MODIFY");

        // Atomic: clear ThreadId, increment ACCESSING
        int newState = (state & ~THREAD_ID_MASK) + 1;

        if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            return;

        spin.SpinOnce();
    }
}
```

### 9.9 EnterDestroy

```csharp
public bool EnterDestroy(TimeSpan? timeOut = null, CancellationToken token = default)
{
    DateTime deadline = timeOut.HasValue ? DateTime.UtcNow + timeOut.Value : DateTime.MaxValue;
    SpinWait spin = default;

    // Phase 1: Set DESTROY flag
    while (true)
    {
        if (token.IsCancellationRequested || DateTime.UtcNow >= deadline)
            return false;

        int state = Volatile.Read(ref _state);

        if (IsDestroyed(state))
            break;  // Already set (shouldn't happen in normal use)

        int newState = state | DESTROY_FLAG;

        if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            break;

        spin.SpinOnce();
    }

    // Phase 2: Wait for ACCESSING=0 and MODIFY not held
    spin = default;

    while (true)
    {
        if (token.IsCancellationRequested || DateTime.UtcNow >= deadline)
        {
            // Note: DESTROY flag remains set - primitive is now in a broken state
            // This is acceptable as destruction was requested but couldn't complete
            return false;
        }

        int state = Volatile.Read(ref _state);

        if (GetAccessingCount(state) == 0 && !IsModifyHeld(state))
            return true;  // DESTROY complete, primitive is terminal

        spin.SpinOnce();
    }
}
```

### 9.10 Reset

```csharp
public void Reset()
{
    // WARNING: Only call when no threads are using this instance
    _state = 0;
}
```

---

## 10. Debug Support

### 10.1 Per-Thread Mode Tracking (DEBUG only)

```csharp
#if DEBUG
[ThreadStatic]
private static Dictionary<nint, HeldMode>? t_heldModes;

private static Dictionary<nint, HeldMode> HeldModes
    => t_heldModes ??= new Dictionary<nint, HeldMode>();

private enum HeldMode { None, Accessing, Modify }

private unsafe void RecordEnter(HeldMode mode)
{
    fixed (int* ptr = &_state)
    {
        nint key = (nint)ptr;
        if (HeldModes.TryGetValue(key, out var existing))
        {
            // ACCESSING allows multiple entries (ref counting)
            if (mode == HeldMode.Accessing && existing == HeldMode.Accessing)
                return;  // OK - nested ACCESSING

            if (existing != HeldMode.None)
            {
                Debug.Fail($"Invalid reentry: already holds {existing}, attempting {mode}");
            }
        }
        HeldModes[key] = mode;
    }
}

private unsafe void RecordExit(HeldMode expectedMode)
{
    fixed (int* ptr = &_state)
    {
        nint key = (nint)ptr;
        if (!HeldModes.TryGetValue(key, out var held))
        {
            Debug.Fail($"Exit without matching enter: expected {expectedMode}");
            return;
        }

        if (held != expectedMode)
        {
            Debug.Fail($"Mode mismatch on exit: expected {expectedMode}, held {held}");
        }

        HeldModes[key] = HeldMode.None;
    }
}

private unsafe void RecordPromotion()
{
    fixed (int* ptr = &_state)
    {
        nint key = (nint)ptr;
        if (!HeldModes.TryGetValue(key, out var held) || held != HeldMode.Accessing)
        {
            Debug.Fail("Promotion without holding ACCESSING");
        }
        HeldModes[key] = HeldMode.Modify;
    }
}

private unsafe void RecordDemotion()
{
    fixed (int* ptr = &_state)
    {
        nint key = (nint)ptr;
        if (!HeldModes.TryGetValue(key, out var held) || held != HeldMode.Modify)
        {
            Debug.Fail("Demotion without holding MODIFY");
        }
        HeldModes[key] = HeldMode.Accessing;
    }
}
#endif
```

### 10.2 Diagnostic State

```csharp
public ResourceAccessControlState GetDiagnosticState()
{
    int state = Volatile.Read(ref _state);
    return new ResourceAccessControlState
    {
        AccessingCount = GetAccessingCount(state),
        ModifyHolderThreadId = GetThreadId(state),
        ModifyPending = IsModifyPending(state),
        Destroyed = IsDestroyed(state),
        RawState = state
    };
}
```

---

## 11. Telemetry Support

### 11.1 Callback-Based Telemetry via IContentionTarget

Telemetry is implemented via the `IContentionTarget` callback interface. This approach:
- **Eliminates bit pressure**: No embedded block IDs in the 32-bit state
- **Enables per-resource levels**: None/Light/Deep configurable per resource
- **Zero overhead when disabled**: Null target means no telemetry code runs

### 11.2 IContentionTarget Interface

```csharp
public interface IContentionTarget
{
    /// <summary>Current telemetry level. Read via volatile for thread-safety.</summary>
    TelemetryLevel TelemetryLevel { get; }

    /// <summary>Optional link to owning IResource for graph integration.</summary>
    IResource OwningResource { get; }

    /// <summary>
    /// Light mode: Record that contention occurred.
    /// Called when a thread had to wait before acquiring.
    /// </summary>
    void RecordContention(long waitUs);

    /// <summary>
    /// Deep mode: Log a detailed lock operation.
    /// Called for every Enter/Exit when TelemetryLevel >= Deep.
    /// </summary>
    void LogLockOperation(LockOperation operation, long durationUs);
}

public enum TelemetryLevel
{
    None = 0,   // No telemetry, zero overhead
    Light = 1,  // Aggregate counters: contention counts, wait times
    Deep = 2    // Full operation history with timestamps and thread IDs
}

public enum LockOperation : byte
{
    None = 0,
    AccessingAcquired,
    AccessingReleased,
    AccessingWaitStart,
    ModifyAcquired,
    ModifyReleased,
    ModifyWaitStart,
    PromoteToModifyStart,
    PromoteToModifyAcquired,
    DemoteToAccessing,
    DestroyAcquired,
    DestroyWaitStart,
    TimedOut,
    Canceled
}
```

### 11.3 Implementation Pattern

```csharp
public bool EnterModify(TimeSpan? timeOut = null, CancellationToken token = default,
    IContentionTarget target = null)
{
    var level = target?.TelemetryLevel ?? TelemetryLevel.None;
    long waitStartTicks = 0;
    bool hadToWait = false;

    // ... existing spin-wait logic ...

    // On first contention:
    if (needToWait && !hadToWait)
    {
        hadToWait = true;
        waitStartTicks = Stopwatch.GetTimestamp();

        if (level >= TelemetryLevel.Deep)
            target!.LogLockOperation(LockOperation.ModifyWaitStart, 0);
    }

    // After acquiring:
    if (hadToWait && level >= TelemetryLevel.Light)
    {
        var waitUs = ComputeElapsedUs(waitStartTicks);
        target!.RecordContention(waitUs);
    }

    if (level >= TelemetryLevel.Deep)
    {
        var waitUs = hadToWait ? ComputeElapsedUs(waitStartTicks) : 0;
        target!.LogLockOperation(LockOperation.ModifyAcquired, waitUs);
    }

    return true;
}

public void ExitModify(IContentionTarget target = null)
{
    // ... existing exit logic ...

    if (target?.TelemetryLevel >= TelemetryLevel.Deep)
        target.LogLockOperation(LockOperation.ModifyReleased, 0);
}

private static long ComputeElapsedUs(long startTicks)
{
    var elapsed = Stopwatch.GetTimestamp() - startTicks;
    return (elapsed * 1_000_000) / Stopwatch.Frequency;
}
```

### 11.4 Owner Aggregates Pattern

Resources that own `ResourceAccessControl` instances typically implement `IContentionTarget` themselves to aggregate telemetry:

```csharp
public class ChainedBlockAllocator : IContentionTarget
{
    private ResourceAccessControl _access;

    // IContentionTarget implementation
    public TelemetryLevel TelemetryLevel { get; set; } = TelemetryLevel.None;
    public IResource OwningResource => this as IResource;

    private long _contentionCount;
    private long _totalWaitUs;
    private int _deepModeBlockId;  // For ResourceTelemetryAllocator

    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionCount);
        Interlocked.Add(ref _totalWaitUs, waitUs);
    }

    public void LogLockOperation(LockOperation operation, long durationUs)
    {
        if (_deepModeBlockId == 0)
            ResourceTelemetryAllocator.AllocateChain(out _deepModeBlockId);

        ResourceTelemetryAllocator.AppendOperation(ref _deepModeBlockId,
            ResourceOperationEntry.Create(operation, durationUs));
    }

    // Usage: pass `this` as the telemetry target
    public void AppendBlock()
    {
        _access.EnterModify(target: this);
        try
        {
            // ... modification logic ...
        }
        finally
        {
            _access.ExitModify(target: this);
        }
    }
}
```

### 11.5 Deep Mode Storage

For `Deep` mode, operation logs are stored in the global `ResourceTelemetryAllocator`:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ResourceOperationEntry  // 16 bytes
{
    public long Timestamp;           // 8 bytes - Stopwatch.GetTimestamp()
    public uint DurationUs;          // 4 bytes - Wait duration in microseconds
    public ushort ThreadId;          // 2 bytes - Environment.CurrentManagedThreadId
    public LockOperation Operation;  // 1 byte
    public byte Flags;               // 1 byte - Reserved
}

// Blocks hold 6 entries each (96 bytes)
[InlineArray(6)]
internal struct ResourceOperationBlock
{
    private ResourceOperationEntry _element;
}
```

See [ResourceTelemetryAllocator](../../src/Typhon.Engine/Observability/ResourceTelemetryAllocator.cs) for the chain-based storage implementation.

---

## 12. Usage Example: ChainedBlockAllocator

```csharp
public class ChainedBlockAllocator
{
    private ResourceAccessControl _access;
    private Block _head;
    private Block _tail;

    public Enumerator GetEnumerator()
    {
        _access.EnterAccessing();
        return new Enumerator(this);
    }

    public void Destroy()
    {
        if (!_access.EnterDestroy(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Could not acquire destroy lock");

        var current = _head;
        while (current != null)
        {
            var next = current.Next;
            current.Dispose();
            current = next;
        }
        _head = null;
        _tail = null;

        // No ExitDestroy - primitive is now terminal
    }

    internal Block AppendBlock()
    {
        _access.EnterModify();

        var newBlock = new Block();
        if (_tail != null)
            _tail.Next = newBlock;
        else
            _head = newBlock;
        _tail = newBlock;

        _access.ExitModify();
        return newBlock;
    }

    internal void EndEnumeration()
    {
        _access.ExitAccessing();
    }
}

public struct Enumerator : IDisposable
{
    private readonly ChainedBlockAllocator _allocator;
    private Block _current;

    public bool MoveNext()
    {
        _current = _current == null ? _allocator._head : _current.Next;
        return _current != null;
    }

    public Block Current => _current;

    public Block AppendIfNeeded()
    {
        if (_current.Next != null)
            return _current.Next;
        return _allocator.AppendBlock();
    }

    public void Dispose() => _allocator.EndEnumeration();
}
```

### 12.1 Using Scoped Guards

```csharp
public void SafeModification()
{
    using var guard = _access.EnterModifyScoped();

    // Modifications here...
    // Guard automatically calls ExitModify on dispose
}

public IEnumerable<Block> EnumerateBlocks()
{
    using var guard = _access.EnterAccessingScoped();

    var current = _head;
    while (current != null)
    {
        yield return current;
        current = current.Next;
    }
    // Guard automatically calls ExitAccessing on dispose
}
```

---

## 13. Summary

| Aspect | Value |
|--------|-------|
| **State size** | 32 bits |
| **Modes** | ACCESSING (multiple), MODIFY (single), DESTROY (terminal) |
| **Key feature** | MODIFY compatible with ACCESSING (accessors not blocked) |
| **Fairness** | MODIFY_PENDING flag blocks new ACCESSING when MODIFY waiting |
| **Wait mechanism** | `SpinWait` (no allocations) |
| **Reentrancy** | ACCESSING only (via ref counting); MODIFY/DESTROY not reentrant |
| **Max concurrent accessors** | 1,023 |
| **Thread ID storage** | 10 bits (truncated) |
| **Telemetry** | `IContentionTarget` callback interface (None/Light/Deep) |

### Mode Selection Guide

| You need to... | Acquire |
|----------------|---------|
| Read/traverse, keep resource alive | ACCESSING (`EnterAccessing`) |
| Modify structure (extend, append) | MODIFY (`EnterModify`) |
| Upgrade from reading to modifying | `TryPromoteToModify` |
| Downgrade from modifying to reading | `DemoteFromModify` |
| Destroy/deallocate resource | DESTROY (`EnterDestroy`) |

### API Consistency with Other Types

| Operation | ResourceAccessControl | NewAccessControl | AccessControlSmall |
|-----------|----------------------|------------------|-------------------|
| Enter shared/accessing | `EnterAccessing()` | `EnterSharedAccess()` | `EnterSharedAccess()` |
| Exit shared/accessing | `ExitAccessing()` | `ExitSharedAccess()` | `ExitSharedAccess()` |
| Enter exclusive/modify | `EnterModify()` | `EnterExclusiveAccess()` | `EnterExclusiveAccess()` |
| Exit exclusive/modify | `ExitModify()` | `ExitExclusiveAccess()` | `ExitExclusiveAccess()` |
| Try enter | `TryEnterAccessing()`, `TryEnterModify()` | `TryEnterExclusiveAccess()` | — |
| Promote | `TryPromoteToModify()` | `TryPromoteToExclusiveAccess()` | `TryPromoteToExclusiveAccess()` |
| Demote | `DemoteFromModify()` | `DemoteFromExclusiveAccess()` | — |
| Scoped guard | `EnterAccessingScoped()`, `EnterModifyScoped()` | — | — |
| Destroy | `EnterDestroy()` | — | — |
| Reset | `Reset()` | `Reset()` | `Reset()` |
| Timeout/Cancellation | All Enter methods | All Enter methods | All Enter methods |
| Telemetry | `IContentionTarget target` (last param) | `IContentionTarget target` (last param) | — |
