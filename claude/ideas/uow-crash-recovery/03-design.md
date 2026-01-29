# Part 3: Detailed Design (Hybrid Approach)

## Data Structures

### Extended CompRevStorageElement (12 bytes)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct CompRevStorageElement
{
    public int ComponentChunkId;       // 4 bytes
    private uint _packedTickHigh;      // 4 bytes - TSN bits 47-16
    private ushort _packedTickLow;     // 2 bytes - TSN bits 15-0
    private ushort _packedEpochFlags;  // 2 bytes - UowEpoch (15 bits) + IsolationFlag (1 bit)

    // _packedEpochFlags layout:
    // Bit 0:     IsolationFlag (1 = uncommitted transaction)
    // Bits 1-15: UowEpoch (0 = legacy/no-UoW, 1-32767 = active epochs)
}
```

**Storage efficiency analysis:**

| Field | Bits | Range | Rationale |
|-------|------|-------|-----------|
| TSN | 48 | 281 trillion | ~8.9 years at 1M tx/sec |
| UowEpoch | 15 | 32,768 | Massive headroom for concurrent UoWs with recycling |
| IsolationFlag | 1 | 0/1 | Transaction-level isolation |

**Total: 12 bytes (+2 bytes / +20% from current 10 bytes)**

### UoW Registry Segment

```csharp
// Header (4104 bytes - fits in first page with entries)
[StructLayout(LayoutKind.Sequential)]
internal struct UowRegistryHeader
{
    public ushort CurrentEpoch;        // Next epoch to allocate (1-32767, 0 reserved)
    public ushort ActiveCount;         // Number of active entries
    public uint Reserved;              // Alignment

    // Fast pending check: 32768 epochs via 512 ulongs (4096 bytes)
    // Bit N = 1 means epoch N is Pending or Void (invisible to other UoWs)
    public fixed ulong PendingBitmap[512];  // 4096 bytes
}

// Entry (32 bytes each)
[StructLayout(LayoutKind.Sequential)]
internal struct UowRegistryEntry
{
    public ushort Epoch;               // Matches revision's UowEpoch (1-32767)
    public UowStatus Status;           // Free=0, Pending=1, Committed=2, Void=3
    public int Reserved;               // Alignment
    public long MinTSN;                // First TSN in this UoW
    public long MaxTSN;                // Last TSN (for GC recycling)
    public long FlushTimestamp;        // Diagnostics
}

internal enum UowStatus : ushort
{
    Free = 0,       // Entry available for reuse
    Pending = 1,    // UoW active, not yet flushed
    Committed = 2,  // UoW flushed successfully
    Void = 3        // UoW crashed, revisions should be invisible
}
```

**Note on 15-bit epochs**: With 32,768 possible epoch values and recycling, we'll never have more than a few thousand concurrent UoWs in practice. The bitmap allows O(1) visibility checks for any epoch.

### Segment Layout

```
Page 0 (8192 bytes):
├── UowRegistryHeader (4104 bytes including 4KB bitmap)
└── UowRegistryEntry[127] (32 × 127 = 4064 bytes)

Page 1+ (overflow for >127 concurrent UoWs):
└── UowRegistryEntry[256] per page (32 × 256 = 8192 bytes)
```

**Capacity**: First page holds 127 entries, each overflow page adds 256. With recycling, this easily supports thousands of concurrent UoWs.

---

## Core Algorithms

### 1. UoW Creation

```csharp
public UnitOfWork CreateUnitOfWork(TimeSpan timeout, DurabilityMode mode)
{
    _registryLock.EnterExclusiveAccess();
    try
    {
        // 1. Allocate epoch from registry
        var entry = FindFreeEntry() ?? AllocateNewEntry();
        entry.Status = UowStatus.Pending;
        entry.MinTSN = _db.TransactionChain.NextFreeId;
        entry.MaxTSN = 0;

        // 2. Set pending bit in bitmap
        SetPendingBit(entry.Epoch);

        // 3. CRITICAL: fsync registry BEFORE any operations
        _registry.SaveChanges();
        FlushRegistryToDisk();

        return new UnitOfWork(this, entry.Epoch, timeout, mode);
    }
    finally
    {
        _registryLock.ExitExclusiveAccess();
    }
}
```

**Why fsync registry first?**
- If crash after registry fsync but before any data: registry shows Pending, no revisions exist → clean
- If crash before registry fsync: no UoW entry exists → clean

### 2. Transaction Commit

```csharp
// In CommitComponent method:
private void CommitRevision(ref CompRevStorageElement element, long tsn)
{
    element.TSN = tsn;
    element.UowEpoch = CurrentUow.Epoch;  // Stamp with UoW's epoch
    element.IsolationFlag = false;        // Visible within UoW now
}
```

No additional fsync needed - the epoch stamp links revision to UoW.

### 3. UoW Flush

```csharp
public void Flush()
{
    // 1. Persist ALL data pages first
    foreach (var tx in _transactions)
        tx.ChangeSet.SaveChanges();
    FlushDataToDisk();  // fsync data files

    // 2. Update registry entry to Committed
    _registryLock.EnterExclusiveAccess();
    try
    {
        var entry = GetEntry(_epoch);
        entry.Status = UowStatus.Committed;
        entry.MaxTSN = _maxTsnUsed;
        entry.FlushTimestamp = Stopwatch.GetTimestamp();

        // 3. Clear pending bit
        ClearPendingBit(_epoch);

        // 4. fsync registry (THIS IS THE COMMIT POINT)
        _registry.SaveChanges();
        FlushRegistryToDisk();
    }
    finally
    {
        _registryLock.ExitExclusiveAccess();
    }
}
```

**The commit point is the registry fsync.**
- Data written but registry still Pending → crash → void
- Data written AND registry Committed → crash → visible

### 4. Visibility Check (Hot Path)

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal bool IsRevisionVisible(
    ref CompRevStorageElement element,
    long readerTSN,
    ushort readerUowEpoch)
{
    // Check 1: Transaction isolation
    // IsolationFlag is bit 0 of _packedEpochFlags
    if ((element._packedEpochFlags & 1) != 0) return false;

    // Check 2: MVCC TSN (unchanged)
    if (element.TSN > readerTSN) return false;

    // Check 3: UoW epoch (NEW)
    // Epoch is bits 1-15 of _packedEpochFlags
    var epoch = (ushort)(element._packedEpochFlags >> 1);

    // Fast path: epoch 0 = legacy/no-UoW
    if (epoch == 0) return true;

    // Fast path: same UoW → always visible
    if (epoch == readerUowEpoch) return true;

    // Check pending bitmap
    return !IsPendingBit(epoch);
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private bool IsPendingBit(ushort epoch)
{
    var idx = epoch / 64;
    var bit = epoch % 64;

    // Use thread-local cached bitmap for performance
    return (CachedPendingBitmap[idx] & (1UL << bit)) != 0;
}
```

**Performance**: 3-4 integer comparisons + shift + optional bitmap lookup (~5-10 cycles total)

### 5. Crash Recovery

```csharp
internal void RecoverFromCrash()
{
    var registry = LoadRegistrySegment();

    // Void all pending UoWs
    int voidedCount = 0;
    foreach (ref var entry in registry.Entries)
    {
        if (entry.Status == UowStatus.Pending)
        {
            entry.Status = UowStatus.Void;
            voidedCount++;
        }
    }

    if (voidedCount > 0)
    {
        _logger.LogWarning("Recovery: voided {Count} incomplete UoWs", voidedCount);
        registry.SaveChanges();
        FlushRegistryToDisk();
    }

    // Build pending bitmap (void epochs treated as "pending" for visibility)
    BuildPendingBitmap();
}
```

**Recovery is O(registry size)**, not O(database size).

### 6. Epoch Recycling (GC)

```csharp
internal void GarbageCollectCompletedUows()
{
    var minActiveTSN = _db.TransactionChain.MinTSN;

    _registryLock.EnterExclusiveAccess();
    try
    {
        foreach (ref var entry in _registry.Entries)
        {
            // Can recycle when:
            // 1. Status is Committed or Void
            // 2. No active transaction could reference this epoch's revisions
            if ((entry.Status == UowStatus.Committed || entry.Status == UowStatus.Void)
                && entry.MaxTSN < minActiveTSN)
            {
                entry.Status = UowStatus.Free;
                _freeEpochQueue.Enqueue(entry.Epoch);
            }
        }
    }
    finally
    {
        _registryLock.ExitExclusiveAccess();
    }
}
```

---

## Crash Scenarios

### Scenario 1: Crash During UoW Operations

```
1. CreateUnitOfWork() → registry.Pending fsynced ✓
2. Tx1.Commit() → revision with epoch written
3. Tx2.Commit() → revision with epoch written
4. [CRASH]
5. Flush() never called

Recovery:
- Load registry → entry still Pending
- Set to Void
- All revisions with this epoch → invisible via bitmap
- State = before UoW started ✓
```

### Scenario 2: Crash During Flush (After Data, Before Registry)

```
1. UoW operations complete
2. Flush() starts
3. Data pages fsynced ✓
4. [CRASH before registry update]

Recovery:
- Registry entry still Pending → set to Void
- Data pages exist but epoch is voided → invisible
- State = before UoW started ✓
```

### Scenario 3: Crash After Flush

```
1. Flush() completes
2. Registry entry = Committed fsynced ✓
3. [CRASH]

Recovery:
- Registry entry is Committed → no action
- All revisions visible ✓
```

---

## Performance Summary

| Operation | Cost |
|-----------|------|
| UoW creation | 1 registry write + fsync |
| Tx commit | +1 field write (negligible) |
| UoW flush | Data fsync + 1 registry write + fsync |
| Read (hot path) | 3 comparisons (~3-5 cycles) |
| Read (cold path) | + bitmap lookup (~5-10 cycles) |
| Recovery | O(registry entries) < 1ms |
