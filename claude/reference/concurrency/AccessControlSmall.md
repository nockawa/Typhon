# AccessControlSmall Design Document

> A compact 32-bit reader-writer synchronization primitive for space-constrained scenarios where thousands of lock instances are needed.

---

## 1. Purpose

AccessControlSmall is a **compact reader-writer lock** designed for scenarios where:

- Lock instances are embedded in per-node or per-element data structures (B+Tree nodes, page headers)
- Memory budget is tight: 4 bytes per lock, not 8
- Critical sections are short (microsecond-scale)
- Waiter fairness is not required (telemetry is supported via `IContentionTarget`)

### 1.1 Design Constraints

- **32-bit state**: Single `int` for the entire lock state
- **No allocations**: No wait queues, no heap objects
- **Lock-free fast path**: All state changes via `Interlocked.CompareExchange`
- **SpinWait for contention**: Adaptive spin-then-yield
- **Implicit state encoding**: State inferred from field values, no explicit state bits
- **Strict validation**: Throws `InvalidOperationException` on misuse (re-entry, wrong-thread exit, overflow)

### 1.2 Relationship to Other Types

For a full comparison of all AccessControl types, see [AccessControlFamily.md](AccessControlFamily.md).

| Aspect | **AccessControlSmall** | **AccessControl** |
|--------|----------------------|---------------------|
| Size | 4 bytes | 8 bytes |
| Waiter tracking | None | 3 counters (S/E/P) |
| Fairness | None | Promoter > Exclusive > Shared |
| Telemetry | `IContentionTarget` callback | `IContentionTarget` callback |
| Contention flag | 1 bit (sticky) | 1 bit (sticky) |
| Diagnostics | `WasContended`, `IsLockedByCurrentThread` | `WasContended`, `IsLockedByCurrentThread` |
| Recursion | None (throws on re-entry) | None |
| Demote | Supported | Supported |
| Thread ID bits | 16 (max 65,535) | 16 (max 65,535) |
| Shared counter | 15 bits (max 32,767) | 8 bits (max 255) |

**When to choose AccessControlSmall over AccessControl:**
- Thousands of instances (B+Tree nodes, page-level latches)
- Short, predictable critical sections
- No need for fairness guarantees (waiter tracking)

---

## 2. Bit Layout

### 2.1 State Encoding (32-bit)

```
┌───────────────────────────────────────────────────────────────┐
│ 31-16                  │   15       │ 14-0                    │
│ Thread ID              │ Contention │ Shared Usage Counter    │
│ 16 bits                │   Flag     │ 15 bits                 │
└───────────────────────────────────────────────────────────────┘
```

- **Bits 0-14 (15 bits)**: Shared Usage Counter (max 32,767)
- **Bit 15 (1 bit)**: Contention Flag (sticky, set when any thread had to wait)
- **Bits 16-31 (16 bits)**: Thread ID of exclusive holder (0 = not exclusively held)

### 2.2 Implicit State

Unlike `AccessControl` which uses explicit state bits, `AccessControlSmall` infers state from the field values. The **contention flag (bit 15) is ignored** when determining state — it's a diagnostic marker only.

| ThreadId (bits 16-31) | Counter (bits 0-14) | State | Meaning |
|-----------------------|---------------------|-------|---------|
| 0 | 0 | **Idle** | Lock is free |
| 0 | ≥1 | **Shared** | N readers active, no writer |
| ≥1 | 0 | **Exclusive** | One writer active |
| ≥1 | ≥1 | **Invalid** | Should never occur |

> **Note:** The contention flag (bit 15) can be set in any state. When checking for idle state, implementations must mask it out: `(data & ~ContentionFlagMask) == 0`.

### 2.3 Constants

```csharp
private const int ThreadIdShift = 16;
private const int SharedUsedCounterMask = 0x0000_7FFF;  // Bits 0-14 (15 bits, max 32,767)
private const int ContentionFlagMask    = 0x0000_8000;  // Bit 15
```

### 2.4 Encoding / Decoding

```csharp
// Read fields
int threadId = _data >> ThreadIdShift;                    // Upper 16 bits
int sharedCount = _data & SharedUsedCounterMask;          // Lower 15 bits (excludes contention flag)
bool wasContended = (_data & ContentionFlagMask) != 0;    // Bit 15

// Write exclusive: store ThreadId in upper 16 bits, preserve contention flag
int exclusiveValue = (Environment.CurrentManagedThreadId << ThreadIdShift)
                   | (_data & ContentionFlagMask);

// Write shared: increment lower 15 bits (contention flag at bit 15 is unaffected)
int sharedValue = _data + 1;

// Check for idle (must mask out contention flag)
bool isIdle = (_data & ~ContentionFlagMask) == 0;
```

---

## 3. State Machine

### 3.1 Transitions

```
                EnterShared (count=1)
         ┌─────────────────────────────┐
         │                             ▼
      ┌──┴──┐    EnterExclusive   ┌────────┐
      │ Idle │◄──────────────────►│Shared  │
      └──┬──┘    ExitExclusive    │count>=1│
         │                        └───┬────┘
         │  EnterExclusive            │
         │  (_data: 0 → ThreadId)     │ ExitShared (count--)
         ▼                            │ if count==0 → Idle
    ┌──────────┐                      │
    │Exclusive │                      │
    │ThreadId>0│     Promote          │
    │counter=0 │◄─────────────────────┘
    └──────────┘  (only if count==1)
```

### 3.2 Key Differences from AccessControl

| Aspect | AccessControlSmall | AccessControl |
|--------|-------------------|-----------------|
| Idle → Exclusive | CAS `0 → ThreadId` (single compare) | CAS with state field transition |
| Idle → Shared | CAS `_data → _data + 1` (if ThreadId==0) | Set state to Shared, counter = 1 |
| Promote | CAS `1 → ThreadId` (counter must be exactly 1) | Wait for counter==1, set state + ThreadId |
| No waiter priority | Writers can be starved by readers | Waiters tracked, promoters have priority |

---

## 4. API

### 4.1 Core Operations

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct AccessControlSmall
{
    private int _data;

    // ═══════════════════════════════════════════════════════════════════
    // Shared Access (Multiple Concurrent Readers)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter shared access, waiting if necessary.
    /// Blocked while exclusive lock is held.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass NullRef for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if acquired, false if deadline expired or cancellation requested.</returns>
    /// <exception cref="InvalidOperationException">Shared counter overflow (>4095).</exception>
    public bool EnterSharedAccess(ref WaitContext ctx, IContentionTarget target = null);

    /// <summary>
    /// Exit shared access. Must be called once per successful enter.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <exception cref="InvalidOperationException">Counter is already zero.</exception>
    public void ExitSharedAccess(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Exclusive Access (Single Writer)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Try to enter exclusive access without blocking.
    /// Returns false immediately if lock is not idle.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if acquired, false if lock was not idle.</returns>
    /// <exception cref="InvalidOperationException">Already held by current thread (re-entry).</exception>
    public bool TryEnterExclusiveAccess(IContentionTarget target = null);

    /// <summary>
    /// Enter exclusive access, waiting if necessary.
    /// Blocked while any shared or exclusive lock is held.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass NullRef for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if acquired, false if deadline expired or cancellation requested.</returns>
    /// <exception cref="InvalidOperationException">Already held by current thread (re-entry).</exception>
    public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);

    /// <summary>
    /// Exit exclusive access.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <exception cref="InvalidOperationException">Not held by current thread.</exception>
    public void ExitExclusiveAccess(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Promotion
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Promote from shared to exclusive access.
    /// Succeeds only if this thread is the sole shared holder (counter == 1).
    /// On success, transitions directly to exclusive (counter cleared, ThreadId set).
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass NullRef for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if promoted, false if other shared holders exist, deadline expired, or cancellation requested.</returns>
    /// <exception cref="InvalidOperationException">Not holding shared access.</exception>
    public bool TryPromoteToExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Demotion
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Demote from exclusive to shared access.
    /// Atomically releases exclusive lock and acquires shared lock (counter = 1).
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <exception cref="InvalidOperationException">Not holding exclusive access.</exception>
    public void DemoteFromExclusiveAccess(IContentionTarget target = null);

    // ═══════════════════════════════════════════════════════════════════
    // Generic Access
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter shared or exclusive access based on the <paramref name="exclusive"/> flag.
    /// </summary>
    /// <param name="exclusive">True for exclusive, false for shared.</param>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass NullRef for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public bool Enter(bool exclusive, ref WaitContext ctx, IContentionTarget target = null);

    /// <summary>
    /// Exit shared or exclusive access based on the <paramref name="exclusive"/> flag.
    /// </summary>
    /// <param name="exclusive">True for exclusive, false for shared.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public void Exit(bool exclusive, IContentionTarget target = null);

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
public struct AccessControlSmall
{
    /// <summary>True if exclusively held by the current thread.</summary>
    public bool IsLockedByCurrentThread { get; }

    /// <summary>Thread ID holding exclusive (16-bit), or 0 if not held.</summary>
    public int LockedByThreadId { get; }

    /// <summary>Current shared access count (0-32,767).</summary>
    public int SharedUsedCounter { get; }

    /// <summary>
    /// Returns true if this lock has ever experienced contention (a thread had to wait).
    /// This flag is sticky - once set, it remains set until Reset() is called.
    /// </summary>
    public bool WasContended { get; }
}
```

---

## 5. Exclusive Acquisition Semantics

### 5.1 Fast Path

The most common case — lock is idle (`_data == 0`):

```csharp
public bool EnterExclusiveAccess(ref WaitContext ctx)
{
    var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
    var ac = new AtomicChange(ref _data, ref ctx);

    // Detect re-entry
    if ((ac.Initial & ~SharedUsedCounterMask) == ct)
    {
        ThrowInvalidOperationException("Cannot enter exclusive access while already holding it");
    }

    // Fast path: CAS from 0 (idle) directly to ThreadId
    ac.Initial = 0;
    ac.NewValue = ct;
    if (ac.Commit())
    {
        return true;
    }

    // ... slow path
}
```

This is a key optimization: by setting `Initial = 0`, the CAS only succeeds if `_data` is exactly zero (idle, no shared holders). This is a **single comparison** rather than checking state bits separately.

### 5.2 Slow Path

If fast path fails, spin until `_data == 0`:

```csharp
    // Slow path: wait for idle, then retry
    while (true)
    {
        if (!ac.WaitFor(d => d == 0))
        {
            return false;  // Deadline expired
        }

        ac.NewValue = ct;
        if (ac.Commit())
        {
            return true;
        }
    }
```

### 5.3 Re-entry Detection

Unlike `AccessControl` (which currently hangs on re-entry), `AccessControlSmall` **actively detects** re-entry and throws:

```csharp
if ((ac.Initial & ~SharedUsedCounterMask) == ct)
{
    throw new InvalidOperationException("Cannot enter exclusive access while already holding it");
}
```

---

## 6. Promote Semantics

Promotion in `AccessControlSmall` differs from `AccessControl`:

| Aspect | AccessControlSmall | AccessControl |
|--------|-------------------|-----------------|
| Condition | `counter == 1` | `counter == 1` |
| On success | `_data` = ThreadId (counter cleared) | State → Exclusive, counter stays 1 |
| Blocking | Returns false if counter > 1 | Can wait via Promoter Waiters |
| After promote | Call `ExitExclusiveAccess` or `DemoteFromExclusiveAccess` | Call `ExitExclusiveAccess` or `DemoteFromExclusiveAccess` |
| Fairness | None — no pending flag | Promoter waiters block new shared |

```csharp
public bool TryPromoteToExclusiveAccess(ref WaitContext ctx)
{
    var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
    var ac = new AtomicChange(ref _data, ref ctx);

    while (true)
    {
        var counter = ac.Initial & SharedUsedCounterMask;

        if (counter == 0)
            ThrowInvalidOperationException("Cannot promote without holding shared access");

        // Can only promote if sole shared holder
        if (counter != 1)
            return false;

        // CAS from counter=1 to ThreadId (counter cleared)
        ac.NewValue = ct;
        if (ac.Commit())
            return true;

        if (!ac.Wait())
            return false;
    }
}
```

---

## 7. AtomicChange Helper

The `AtomicChange` ref struct encapsulates the CAS-retry loop pattern:

```csharp
private ref struct AtomicChange
{
    public int Initial;       // Snapshot at last fetch
    public int NewValue;      // Desired new value

    private readonly ref int _source;
    private SpinWait _spinWait;
    private readonly ref WaitContext _ctx;

    /// <summary>CAS: source ← NewValue if source == Initial.</summary>
    public bool Commit();

    /// <summary>Retry until valueToCommit succeeds.</summary>
    public void ForceCommit(Func<int, int> valueToCommit);

    /// <summary>Re-read source into Initial.</summary>
    public void Fetch();

    /// <summary>SpinOnce + check deadline, then re-fetch.</summary>
    public bool Wait();

    /// <summary>Spin until predicate is true on the current value.</summary>
    public bool WaitFor(Func<int, bool> predicate);
}
```

This parallels `AccessControl`'s `LockData` ref struct but is simpler (no staging concept — just Initial and NewValue).

---

## 8. Validation Behavior

AccessControlSmall enforces correctness via exceptions (not just Debug.Assert):

| Scenario | Behavior | Exception |
|----------|----------|-----------|
| Shared counter overflow (>32,767) | Throws | `InvalidOperationException` |
| ExitShared when counter == 0 | Throws | `InvalidOperationException` |
| EnterExclusive while already holding | Throws | `InvalidOperationException` |
| ExitExclusive from wrong thread | Throws | `InvalidOperationException` |
| Promote without holding shared | Throws | `InvalidOperationException` |

This is stricter than `AccessControl` which uses `Debug.Assert` for some of these checks.

---

## 9. Limitations

| Limitation | Impact | Workaround |
|------------|--------|------------|
| No waiter fairness | Writers can be starved by continuous readers | Use `AccessControl` if fairness matters |
| No diagnostics | No contention level, last op, or lock class | Crash dumps show raw `_data` only |
| No recursion | Always throws on re-entry | Use `AccessControl` with `RecursiveAllowed` flag |
| 16-bit Thread ID | Standard across all AccessControl types | Consistent with AccessControl and ResourceAccessControl |

---

## 10. Usage Examples

### 10.1 B+Tree Node Latch

```csharp
// Each node embeds a 4-byte lock — thousands of nodes
public struct BTreeNode
{
    public AccessControlSmall Latch;
    public int KeyCount;
    // ... keys, values, child pointers
}

public ref T ReadKey<T>(ref BTreeNode node, int index)
{
    // NullRef = infinite wait for B+Tree node latch (short critical section)
    node.Latch.EnterSharedAccess(ref Unsafe.NullRef<WaitContext>());
    try
    {
        return ref node.Keys[index];
    }
    finally
    {
        node.Latch.ExitSharedAccess();
    }
}
```

### 10.2 Read-Then-Maybe-Write (Promote)

```csharp
public bool TryUpdateIfNeeded(ref BTreeNode node, ref WaitContext ctx)
{
    node.Latch.EnterSharedAccess(ref ctx);
    try
    {
        if (!NeedsUpdate(ref node))
            return false;

        if (!node.Latch.TryPromoteToExclusiveAccess(ref ctx))
            return false;  // Other readers present, can't promote

        try
        {
            ApplyUpdate(ref node);
            return true;
        }
        finally
        {
            node.Latch.ExitExclusiveAccess();
        }
    }
    finally
    {
        // ExitShared only if we didn't promote
        // (promotion clears the shared counter)
    }
}
```

### 10.3 Generic Enter/Exit

```csharp
public void AccessNode(ref BTreeNode node, bool exclusive, ref WaitContext ctx)
{
    node.Latch.Enter(exclusive, ref ctx);
    try
    {
        // ... work
    }
    finally
    {
        node.Latch.Exit(exclusive);
    }
}
```

---

## 11. Summary

| Aspect | Value |
|--------|-------|
| **State size** | 32 bits |
| **Modes** | Shared (multiple), Exclusive (single) |
| **State encoding** | Implicit: ThreadId≠0 → Exclusive, Counter>0 → Shared |
| **Max shared count** | 32,767 (15 bits) |
| **Thread ID bits** | 16 (max 65,535) |
| **Contention flag** | 1 bit (sticky, set when any thread had to wait) |
| **Recursion** | Not supported (throws on re-entry) |
| **Fairness** | None (writer starvation possible) |
| **Wait mechanism** | `SpinWait` via `AtomicChange` helper |
| **Telemetry** | `IContentionTarget` callback (Light + Deep modes) |
| **WaitContext** | All blocking operations accept `ref WaitContext` (deadline + cancellation) |
| **Validation** | Strict — throws `InvalidOperationException` on misuse |
