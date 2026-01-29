# Part 4: Implementation Plan

## Files to Modify

| File | Changes |
|------|---------|
| `ComponentTable.cs` | Add `UowEpoch` field to `CompRevStorageElement` |
| `ComponentRevisionManager.cs` | Stamp epoch in `Commit()`, epoch-aware GC |
| `Transaction.cs` | Pass UoW epoch to commit methods |
| `ManagedPagedMMF.cs` | Add `UowRegistrySPI` to `RootFileHeader`, recovery hooks |
| `DatabaseEngine.cs` | UoW creation, registry initialization |

## New Files

| File | Purpose |
|------|---------|
| `UowRegistry.cs` | Registry segment management |
| `UowRegistryEntry.cs` | Entry struct and status enum |
| `UnitOfWork.cs` | UoW lifecycle (links to [02-execution.md](../../overview/02-execution.md)) |

---

## Implementation Phases

### Phase 1: Storage Format Change

**Goal**: Pack `UowEpoch` (15 bits) + `IsolationFlag` (1 bit) into new field

```csharp
// ComponentTable.cs - CompRevStorageElement (12 bytes total)
private ushort _packedEpochFlags;  // +2 bytes

// Bit layout:
// Bit 0:     IsolationFlag (1 = uncommitted transaction)
// Bits 1-15: UowEpoch (0 = legacy/no-UoW, 1-32767 = active epochs)

// Helper properties:
public bool IsolationFlag => (_packedEpochFlags & 1) != 0;
public ushort UowEpoch => (ushort)(_packedEpochFlags >> 1);
```

**Breaking change**: Storage format changes from 10 to 12 bytes per revision.

**Migration**:
- New databases: 12-byte elements automatically
- Existing databases: Migration tool adds 2 zero bytes per element

**Testing**:
- Unit tests for new struct size
- Verify existing tests still pass (epoch 0 = legacy behavior)

### Phase 2: UoW Registry Segment

**Goal**: Implement the registry infrastructure

**Files**:
```
src/Typhon.Engine/Execution/
├── UowRegistry.cs           # Segment management
├── UowRegistryEntry.cs      # Entry struct
└── UowRegistryHeader.cs     # Header struct
```

**Changes**:
- Add `UowRegistrySPI` to `RootFileHeader`
- Create registry segment on database creation
- Load registry on database open

**Testing**:
- Allocate/deallocate epochs
- Bitmap operations
- Segment persistence

### Phase 3: UoW Lifecycle

**Goal**: Implement Unit of Work with epoch stamping

**API**:
```csharp
public UnitOfWork CreateUnitOfWork(TimeSpan timeout, DurabilityMode mode);
public void UnitOfWork.Flush();
public void UnitOfWork.Dispose();
```

**Changes**:
- `Transaction.CommitComponent()` stamps `UowEpoch`
- `UnitOfWork.Flush()` follows data-then-registry fsync order
- `UnitOfWork.Dispose()` without Flush → implicit rollback (no action needed)

**Testing**:
- Single transaction UoW
- Multiple transaction UoW
- UoW disposal without flush

### Phase 4: Visibility & Recovery

**Goal**: Epoch-aware visibility and crash recovery

**Visibility changes**:
```csharp
bool IsRevisionVisible(ref CompRevStorageElement el, long tsn, ushort readerEpoch)
{
    // IsolationFlag is bit 0
    if ((el._packedEpochFlags & 1) != 0) return false;
    if (el.TSN > tsn) return false;

    // Extract 15-bit epoch from bits 1-15
    var epoch = (ushort)(el._packedEpochFlags >> 1);
    if (epoch == 0) return true;           // Legacy/no-UoW
    if (epoch == readerEpoch) return true; // Same UoW
    return !IsPendingBit(epoch);           // Check bitmap
}
```

**Recovery changes**:
- `OnFileLoading()` calls `RecoverUoWs()`
- Void all Pending entries
- Build pending bitmap

**Testing**:
- Crash injection tests (kill process mid-operation)
- Verify recovery correctly voids incomplete UoWs
- Verify committed UoWs remain visible

---

## Testing Strategy

### Unit Tests

```csharp
// CompRevStorageElement size
Assert.AreEqual(12, sizeof(CompRevStorageElement));

// Epoch stamping
tx.CreateEntity(ref comp);
tx.Commit();
Assert.AreEqual(uow.Epoch, GetRevisionEpoch(entityId));

// Visibility within same UoW
using var uow = db.CreateUnitOfWork();
using (var tx1 = uow.CreateTransaction()) { tx1.CreateEntity(...); tx1.Commit(); }
using (var tx2 = uow.CreateTransaction()) { Assert.IsTrue(tx2.ReadEntity(...)); }
```

### Integration Tests

```csharp
// Crash recovery
[Test]
public void UoW_CrashBeforeFlush_RollsBackAllTransactions()
{
    // 1. Create UoW, commit multiple transactions
    // 2. Kill process (simulate crash)
    // 3. Reopen database
    // 4. Verify all changes from crashed UoW are invisible
}
```

### Stress Tests

```csharp
// Concurrent UoWs
[Test]
public void HighConcurrency_ManyUoWs_AllRecoverCorrectly()
{
    // 1. Start 100 concurrent UoWs
    // 2. Randomly crash some mid-operation
    // 3. Flush others
    // 4. Recovery correctly identifies crashed vs committed
}
```

---

## Verification Checklist

### Before Implementation

- [ ] Review current `CompRevStorageElement` usage
- [ ] Identify all callers of `element.Commit()`
- [ ] Document migration path for existing databases

### After Phase 1

- [ ] `sizeof(CompRevStorageElement) == 12`
- [ ] Existing tests pass with epoch 0

### After Phase 2

- [ ] Registry segment created on new database
- [ ] Registry loaded on database open
- [ ] Epoch allocation/deallocation works

### After Phase 3

- [ ] UoW creates registry entry (Pending)
- [ ] Tx.Commit stamps epoch on revisions
- [ ] UoW.Flush updates registry (Committed)
- [ ] Intra-UoW visibility works

### After Phase 4

- [ ] Recovery voids Pending entries
- [ ] Voided epoch revisions invisible
- [ ] Committed epoch revisions visible
- [ ] Crash injection tests pass

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Storage format breaking change | Version header, migration tool |
| Epoch exhaustion (32768 max) | GC recycles; 32K is massive headroom for concurrent UoWs |
| Registry corruption | CRC32 per page; worst case = void all |
| Read path regression | Inline hot path; benchmark before/after |
| Bitmap staleness | Refresh per transaction; acceptable latency |

---

## Open Questions (Deferred)

1. **Registry corruption recovery**: If registry is corrupted, should we:
   - Void all pending epochs (conservative)
   - Try to infer from revision chains (complex)

2. **Void epoch cleanup**: Physical removal of voided revisions:
   - During normal GC (when TSN < MinTSN)?
   - Background task?
   - On next write to same entity?

3. **Epoch allocation strategy**:
   - Sequential (simple, good locality)
   - Random (avoids patterns, better distribution)

These can be decided during implementation.
