# Part 1: The Crash Recovery Problem

## Current MVCC Design

Typhon's MVCC uses `CompRevStorageElement` (10 bytes) to track component revisions:

```csharp
struct CompRevStorageElement  // 10 bytes (current)
{
    int ComponentChunkId;      // 4 bytes - points to component data
    uint _packedTickHigh;      // 4 bytes - TSN high bits (bits 47-16)
    ushort _packedTickLow;     // 2 bytes - TSN low bits (bits 15-0) + IsolationFlag (bit 0)
}
```

**IsolationFlag semantics:**
- `true` = Uncommitted (visible only to owning transaction)
- `false` = Committed (visible to all transactions with TSN >= this revision)

When `Transaction.Commit()` is called, it sets `IsolationFlag = false`, making the revision visible immediately.

### New Structure with UoW Support (12 bytes)

```csharp
struct CompRevStorageElement  // 12 bytes (new)
{
    int ComponentChunkId;      // 4 bytes - points to component data
    uint _packedTickHigh;      // 4 bytes - TSN high bits (bits 47-16)
    ushort _packedTickLow;     // 2 bytes - TSN low bits (bits 15-0)
    ushort _packedEpochFlags;  // 2 bytes - UowEpoch (15 bits) + IsolationFlag (1 bit)

    // _packedEpochFlags layout:
    // Bit 0:     IsolationFlag (1 = uncommitted transaction)
    // Bits 1-15: UowEpoch (0 = legacy/no-UoW, 1-32767 = active epochs)
}
```

| Field | Bits | Range |
|-------|------|-------|
| ComponentChunkId | 32 | 4 billion chunks |
| TSN | 48 | 281 trillion (~8.9 years at 1M tx/sec) |
| UowEpoch | 15 | 32,768 values (0 = no UoW) |
| IsolationFlag | 1 | 0/1 |
| **Total** | **96 bits** | **12 bytes (+2 from current)** |

---

## The Problem

**Scenario**: UoW with multiple transactions, crash before Flush()

```
Timeline:
1. CreateUnitOfWork() → UoW #42 created
2. CreateTransaction() → Tx #100
3. tx.CreateEntity(player) → Revision created, IsolationFlag=true
4. tx.Commit() → IsolationFlag=false ← VISIBLE NOW
5. CreateTransaction() → Tx #101
6. tx.UpdateEntity(player) → New revision, IsolationFlag=true
7. tx.Commit() → IsolationFlag=false ← VISIBLE NOW
8. [SERVER CRASH]
9. Flush() never called

After restart:
- Both revisions have IsolationFlag=false
- They appear committed to the database
- No way to identify they belonged to incomplete UoW #42
```

**Result**: Partial state committed. ACID violation at UoW level.

---

## Requirements

1. **Full rollback on crash**: If UoW doesn't call `Flush()`, ALL its transactions must be invisible after recovery

2. **Intra-UoW visibility**: Within a UoW, Tx2 must see Tx1's committed changes

3. **Inter-UoW isolation**: Different UoWs should not see each other's uncommitted work

4. **Efficient recovery**: Don't scan entire database on startup

5. **Minimal read overhead**: Hot path must remain fast

---

## Why Current Design Can't Solve This

| What's Missing | Why It Matters |
|----------------|----------------|
| **UoW membership in revision** | Can't identify which revisions belong to incomplete UoW |
| **Durable UoW commit marker** | Can't distinguish committed vs crashed UoW |
| **No WAL** | No transaction log to replay on recovery |

The core insight: We need to add **UoW identity** to revisions AND track **UoW completion status** durably.

---

## Constraints

| Constraint | Limit |
|------------|-------|
| Per-revision overhead | ≤ +2 bytes acceptable |
| Concurrent UoWs | Must support 64+ |
| Recovery time | Must be O(1), not O(database size) |
| Read path impact | ≤ 5 cycles in hot path |
