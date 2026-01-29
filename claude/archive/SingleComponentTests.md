# Single Component CRUD Tests - Analysis Report

## Summary

This report documents the comprehensive unit tests created for single (non-AllowMultiple) component CRUD operations in the Typhon database engine, along with analysis of failing tests and proposed fixes.

## Test Results

**Total Tests**: 46
**Passed**: 29
**Failed**: 17

### Passing Tests (29)

| Test Name | Category |
|-----------|----------|
| Create_SingleComponent_Success | Create |
| Create_MultipleComponentTypes_Success | Create |
| Create_MultipleEntities_SameTransaction | Create |
| Create_MultipleEntities_SeparateTransactions | Create |
| Create_ReadInSameTransaction_Success | Create |
| Read_SameEntity_MultipleTimes_ConsistentResults | Read |
| Read_MultipleComponentTypes_Success | Read |
| Read_ComponentNotOnEntity_ReturnsFalse | Read |
| Update_SeparateTransaction_Success | Update |
| Update_SameTransactionAsCreate_OverwritesWithoutNewRevision | Update |
| Update_MultipleTimes_SameTransaction | Update |
| Update_MultipleTimes_SeparateTransactions | Update |
| Update_NonExistentEntity_ReturnsFalse | Update |
| Update_MultipleComponentTypes_Success | Update |
| Update_ReadBeforeUpdate_SeparateTransaction | Update |
| Delete_SeparateTransaction_Success | Delete |
| Delete_MultipleComponentTypes_Success | Delete |
| Delete_OneComponentType_OthersRemain | Delete |
| EdgeCase_DefaultValues_Success | Edge Cases |
| EdgeCase_ExtremeValues_Success | Edge Cases |
| EdgeCase_NegativeValues_Success | Edge Cases |
| EdgeCase_RapidCRUDCycles_Success | Edge Cases |
| EdgeCase_UseAfterDispose_ThrowsOrReturnsFalse | Edge Cases |
| EdgeCase_EmptyString_Success | Edge Cases |
| EdgeCase_ConcurrentReads_Success | Edge Cases |
| EdgeCase_InterleavedCreations_UniqueIds | Edge Cases |
| IndexedComponent_CRUD_Success | Indexed |
| IndexedComponent_UpdateIndexedField_Success | Indexed |
| ComplexScenario_InterleavedCreateUpdate_Success | Complex |

---

## Failing Tests Analysis

### Issue Category 1: GetComponentRevision Returns 0 Instead of -1 for Non-Existent Entities

**Affected Tests:**
- `Create_ThenRollback_EntityNotReadable`
- `Read_NonExistentEntity_ReturnsFalse`
- `Delete_SeparateTransaction_Success`
- `ComplexScenario_FullLifecycle_Success`

**Symptom:**
`GetComponentRevision<CompA>(entityId)` returns 0 instead of -1 for entities that don't exist or have been deleted.

**Root Cause Analysis:**

The `GetComponentRevision<T>` method (Transaction.cs:399-427) returns -1 only when the primary key is not found in the cache:

```csharp
if (!infoSingle.CompRevInfoCache.TryGetValue(pk, out var compRevInfo))
{
    return -1;
}
```

However, after a rollback or deletion:
1. The cache entry may still exist with stale/invalid data
2. The method proceeds to calculate revision from the (potentially freed) revision table chunk
3. This returns 0 or garbage instead of -1

**Proposed Fix:**

In `GetComponentRevision<T>`, after retrieving from cache, verify the component still exists:

```csharp
// After getting from cache, check if it was deleted
if (compRevInfo.CurCompContentChunkId == 0)
{
    return -1;
}
```

For non-cached entries, also try the index lookup to see if the entity exists:

```csharp
if (!infoSingle.CompRevInfoCache.TryGetValue(pk, out var compRevInfo))
{
    // Try to find in index before returning -1
    if (!GetCompRevInfoFromIndex(pk, infoSingle, TransactionTick, out compRevInfo))
    {
        return -1;
    }
    // Entry exists but not in cache - calculate revision
}
```

---

### Issue Category 2: Rollback Does Not Properly Restore Entity State

**Affected Tests:**
- `Update_ThenRollback_OriginalValuePreserved`
- `Delete_ThenRollback_EntityRestored`
- `Resurrection_CreateDeleteRollback_EntityReadable`
- `Resurrection_UpdateDeleteRollback_OriginalRestored`
- `ComplexScenario_MixedCommitRollback_CorrectFinalState`

**Symptom:**
After rolling back an update or delete operation, the entity cannot be read in a new transaction.

**Root Cause Analysis:**

When rolling back an Update or Delete (Transaction.cs:1569-1574):

```csharp
// In case of update or delete, mark void the revision entry we added
if ((compRevInfo.Operations & (ComponentInfoBase.OperationType.Updated | ComponentInfoBase.OperationType.Deleted)) != 0)
{
    curElement[0].DateTime = PackedDateTime48.FromPackedDateTimeTicks(0);
    curElement[0].ComponentChunkId = 0;
}
```

This marks the **new** revision entry (created for the update/delete) as void. However, the revision chain now contains:
1. The original valid revision (e.g., revision 1 from create)
2. A voided revision (revision 2 from the rolled-back update)

When a new transaction tries to read:
1. `GetCompRevInfoFromIndex` walks the revision chain
2. It finds revision 2 first (most recent), but it has `ComponentChunkId == 0` (void)
3. The read fails because it can't find valid data

**The problem**: The rollback marks the wrong revision or doesn't properly restore the previous revision index.

**Proposed Fix:**

Option A: After rollback, remove the voided entry from the revision chain (decrease ItemCount):

```csharp
if ((compRevInfo.Operations & (ComponentInfoBase.OperationType.Updated | ComponentInfoBase.OperationType.Deleted)) != 0)
{
    curElement[0].DateTime = PackedDateTime48.FromPackedDateTimeTicks(0);
    curElement[0].ComponentChunkId = 0;

    // Decrement ItemCount to effectively remove this entry
    firstChunkHeader.ItemCount--;
    dirtyFirstChunk = true;
}
```

Option B: Modify `GetCompRevInfoFromIndex` to skip over voided entries (DateTime == 0):

```csharp
// When walking revision chain, skip entries with DateTime == 0
if (element.DateTime.ToPackedDateTimeTicks() == 0 || element.ComponentChunkId == 0)
{
    continue; // Skip void entries
}
```

---

### Issue Category 3: Delete in Same Transaction as Create Still Readable

**Affected Tests:**
- `Delete_SameTransactionAsCreate_EntityNotReadable`

**Symptom:**
Creating an entity, then deleting it in the same transaction, then reading it returns True instead of False.

**Root Cause Analysis:**

In `UpdateComponent` (which handles Delete when `isDelete == true`), Transaction.cs:767-771:

```csharp
// Check if we need to delete a component we previously added
if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
{
    info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
}
```

The chunk is freed, but `compRevInfo.CurCompContentChunkId` is **never set to 0**!

Then in `ReadComponent`, Transaction.cs:673-678:

```csharp
// Deleted component ?
if (compRevInfo.CurCompContentChunkId == 0)
{
    t = default;
    return false;
}
```

Since `CurCompContentChunkId` still holds the old (freed) chunk ID, this check passes and the read proceeds to access freed memory.

**Proposed Fix:**

After freeing the chunk, set `CurCompContentChunkId` to 0:

```csharp
// Check if we need to delete a component we previously added
if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
{
    info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
    compRevInfo.CurCompContentChunkId = 0;  // ADD THIS LINE
}
```

---

### Issue Category 4: Delete Same Entity Twice Returns True

**Affected Tests:**
- `Delete_SameEntity_Twice_SecondReturnsFalse`

**Symptom:**
Deleting the same entity twice returns True both times instead of False on the second delete.

**Root Cause Analysis:**

The first delete commits and removes the entity. The second delete transaction:
1. Calls `UpdateComponent` with `isDelete = true`
2. Entity not in cache, so calls `GetCompRevInfoFromIndex`
3. This finds the revision chain (which still has an entry but with `ComponentChunkId == 0`)
4. Returns true from `GetCompRevInfoFromIndex` even though the component is deleted

The check for "already deleted" at Transaction.cs:762-765 only works if the entity is in the transaction's cache:

```csharp
// Can't update a deleted component...
if ((compRevInfo.Operations & ComponentInfoBase.OperationType.Deleted) == ComponentInfoBase.OperationType.Deleted)
{
    return false;
}
```

But on a fresh transaction, the cache doesn't have the `Deleted` flag set.

**Proposed Fix:**

After fetching from index, check if the component is already deleted:

```csharp
// Fetch the cache by getting the revision closest to the transaction tick
if (!GetCompRevInfoFromIndex(pk, info, TransactionTick, out compRevInfo))
{
    return false;
}

// Check if already deleted (CurCompContentChunkId == 0)
if (compRevInfo.CurCompContentChunkId == 0)
{
    return false;
}
```

---

### Issue Category 5: MVCC Isolation Not Working Correctly

**Affected Tests:**
- `MVCC_TransactionSeesSnapshotAtCreationTime`
- `MVCC_TransactionSeesEntityBeforeDelete`
- `MVCC_LongRunningTransaction_PreventsRevisionCleanup`

**Symptom:**
- Early transaction sees updated value (200) instead of snapshot value (100)
- New transaction still sees entity after deletion

**Root Cause Analysis:**

The MVCC implementation relies on:
1. Each revision having a DateTime timestamp
2. `GetCompRevInfoFromIndex` finding the revision valid at `TransactionTick`
3. Cleanup not removing revisions still needed by active transactions

Potential issues:
1. **Timestamp ordering**: The revision chain walk may not correctly identify the revision valid at a given tick
2. **Cleanup too aggressive**: The `CleanUpUnusedEntries` may be removing revisions still needed
3. **IsolationFlag handling**: New revisions have `IsolationFlag = true` until committed, but this might not be checked correctly

**Investigation Required:**

Need to examine `GetCompRevInfoFromIndex` implementation to verify:
1. It correctly walks the revision chain
2. It respects the DateTime and IsolationFlag
3. It returns the correct revision for a given TransactionTick

---

### Issue Category 6: IndexedComponent Rollback Issue

**Affected Tests:**
- `IndexedComponent_UpdateThenRollback_OriginalIndexValue`

**Symptom:**
After updating an indexed component and rolling back, the original values are not correctly restored.

**Root Cause:**
Same as Issue Category 2 - rollback doesn't properly restore entity state, but additionally the index entries may not be correctly restored.

---

## Summary of Required Fixes

### Fix 1: Delete in Same Transaction (CRITICAL - Memory Safety)
**Location**: Transaction.cs:767-771

```csharp
// Before
if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
{
    info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
}

// After
if (isDelete && (compRevInfo.CurCompContentChunkId != 0))
{
    info.CompContentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
    compRevInfo.CurCompContentChunkId = 0;
}
```

### Fix 2: GetComponentRevision for Deleted Entities
**Location**: Transaction.cs:417-426

```csharp
// After getting compRevInfo from cache
if (compRevInfo.CurCompContentChunkId == 0)
{
    return -1;
}
```

### Fix 3: Delete Already Deleted Entity Returns False
**Location**: Transaction.cs:779-783

```csharp
// After GetCompRevInfoFromIndex succeeds
if (compRevInfo.CurCompContentChunkId == 0)
{
    return false; // Already deleted
}
```

### Fix 4: Rollback Properly Restores Previous State
**Location**: Transaction.cs:1569-1574

Either decrement `ItemCount` to effectively remove the voided entry, or modify `GetCompRevInfoFromIndex` to skip voided entries.

### Fix 5: MVCC Isolation (Investigation Required)
Needs deeper investigation of `GetCompRevInfoFromIndex` and revision chain walking logic.

---

## Test File Location

The comprehensive test file was created at:
```
test/Typhon.Engine.Tests/Database Engine/SingleComponentCRUDTests.cs
```

It contains 46 tests organized into the following regions:
- Create Tests (6 tests)
- Read Tests (4 tests)
- Update Tests (9 tests)
- Delete Tests (7 tests)
- Resurrection Tests (4 tests)
- MVCC Isolation Tests (3 tests)
- Edge Cases (8 tests)
- Indexed Component Tests (3 tests)
- Complex Scenarios (3 tests)

---

## Recommendations

1. **Priority 1 (Critical)**: Fix the delete-in-same-transaction bug (Fix 1) as it causes use-after-free memory access.

2. **Priority 2 (High)**: Fix the rollback issues (Fix 4) as they break transaction isolation guarantees.

3. **Priority 3 (Medium)**: Fix GetComponentRevision and double-delete issues (Fixes 2 & 3) for API correctness.

4. **Priority 4 (Investigation)**: Investigate and fix MVCC isolation issues (Fix 5).

5. **Testing**: After applying fixes, re-run the test suite to verify all 46 tests pass.
