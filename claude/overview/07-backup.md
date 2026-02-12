# Component 7: Backup & Restore

> Page-level incremental snapshots with Copy-on-Write and reverse-delta chains.

---

## Overview

The Backup component creates consistent point-in-time snapshots of the database at the page level. It uses a **Copy-on-Write (CoW) shadow buffer** to capture pre-modification page content during active backup, and stores incremental history as **reverse deltas** — enabling instant restore of the latest state and zero-cost deletion of old snapshots.

<a href="../assets/typhon-backup-overview.svg"><img src="../assets/typhon-backup-overview.svg" width="1200" alt="Backup & Restore — Component Overview"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

**Key design choices:**
- **No WAL dependency** — backups are self-contained page snapshots
- **Reverse deltas** — latest snapshot is always full; older ones store backward diffs
- **CoW at the page cache level** — near-zero cost when no backup is active (1 volatile read)
- **Sequential writes** — backup pages written in arrival order, page index stored at end

---

## Status: 🆕 New

This component does not yet exist in the codebase. This document captures the design.

---

## Sub-Components

| # | Name | Purpose | Status |
|---|------|---------|--------|
| **7.1** | [CoW Shadow Buffer](#71-cow-shadow-buffer) | Capture pre-modification pages during backup | 🆕 New |
| **7.2** | [Snapshot Manager](#72-snapshot-manager) | Create, list, delete snapshots | 🆕 New |
| **7.3** | [Reverse Delta Engine](#73-reverse-delta-engine) | Compute and apply backward diffs | 🆕 New |
| **7.4** | [Restore Manager](#74-restore-manager) | Restore database from snapshot chain | 🆕 New |
| **7.5** | [Backup File Format](#75-backup-file-format) | On-disk layout of snapshot files | 🆕 New |
| **7.6** | [Retention Policy](#76-retention-policy) | Automatic cleanup of old snapshots | 🆕 New |
| **7.7** | [Backup Store Abstraction](#77-backup-store-abstraction) | Pluggable I/O backend for local/remote storage | 🆕 New |

---

## 7.1 CoW Shadow Buffer

### Purpose

Intercept page mutations during an active backup to preserve the consistent snapshot-start state. The mechanism ensures that transactions continue at full speed while the backup captures a frozen view.

<a href="../assets/typhon-backup-cow-mechanism.svg"><img src="../assets/typhon-backup-cow-mechanism.svg" width="945" alt="CoW Shadow Buffer — Hot Path"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

### Interception Point

All write paths converge at `TryLatchPageExclusive` in `PagedMMF.cs` before a page transitions to `PageState.Exclusive`. This is the single CoW hook:

```csharp
// Inside TryLatchPageExclusive, just before: pi.PageState = PageState.Exclusive
if (_backupActive != 0)        // 1 volatile read — near-zero cost when inactive
{
    TryCoWForBackup(pi);
}
```

### TryCoWForBackup — The CoW Logic

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]  // keep hot path icache-friendly
private void TryCoWForBackup(PageInfo pi)
{
    int filePageIndex = pi.FilePageIndex;

    // Atomic test-and-set: only first thread for this page wins
    if (!_cowBitmap.TrySet(filePageIndex))
        return;  // already CoW'd — skip

    // Allocate shadow slot (lock-free bump allocator)
    int slot = Interlocked.Increment(ref _shadowPoolHead) - 1;
    if (slot >= _shadowPoolSize)
        slot = WaitForFreeSlot();  // backpressure if pool exhausted

    // Copy 8KB from page cache to shadow pool (both pinned)
    byte* src = _memPagesAddr + (pi.MemPageIndex * PageSize);
    byte* dst = _shadowPoolAddr + (slot * PageSize);
    Buffer.MemoryCopy(src, dst, PageSize, PageSize);

    // Enqueue for background writer
    _backupQueue.Enqueue(new ShadowEntry
    {
        FilePageIndex = filePageIndex,
        ChangeRevision = ((PageBaseHeader*)src)->ChangeRevision,
        ShadowSlotIndex = slot
    });
}
```

### Data Structures

| Structure | Purpose | Size |
|-----------|---------|------|
| `_backupActive` | Volatile int flag (0/1) | 4 bytes |
| `_cowBitmap` | ConcurrentBitmapL3All — 1 bit per page | ~12.5 KB for 100K pages |
| `_shadowPool` | Pre-allocated pinned byte[] ring buffer | Configurable (default 4 MB = 512 pages) |
| `_backupQueue` | ConcurrentQueue\<ShadowEntry\> | Lock-free MPSC |

### Performance Characteristics

| Scenario | Cost |
|----------|------|
| No backup active | 1 volatile read (~1 CPU cycle) |
| Backup active, page already CoW'd | Volatile read + bitmap test (~5-10 cycles) |
| Backup active, first write to page | Bitmap set + 8KB memcpy + enqueue (~200-500 ns) |

### Shadow Pool Backpressure

When the shadow pool is exhausted (writer thread slower than mutation rate), `WaitForFreeSlot()` blocks the calling thread until the background writer frees slots. This provides natural throttling — transaction latency increases gracefully under extreme write pressure during backup.

---

## 7.2 Snapshot Manager

### Purpose

Coordinate the full backup lifecycle: force checkpoint, enable CoW, drive the page reader, finalize the backup file.

### Backup Lifecycle

```
1. Force checkpoint    → flush all dirty pages, ensure on-disk consistency
2. Record backup epoch → increment global 2-byte counter
3. Allocate resources  → shadow pool (if not pre-allocated), clear CoW bitmap
4. Set _backupActive=1 → atomic, enables CoW interception
5. Sequential reader   → read all pages from data file
                         for each: if CoW'd → use shadow version; else → use file version
6. Set _backupActive=0 → disable CoW
7. Drain queue         → process remaining shadow entries
8. Write page index    → {FilePageIndex, offset, compressedSize} per page
9. Write footer        → checksum, epoch, page count, timestamp
```

### Concurrency During Backup

| Concern | Resolution |
|---------|------------|
| Two threads CoW same page | `ConcurrentBitmap.TrySet` — atomic, only first wins |
| Checkpoint during backup | CoW fires before checkpoint flush, preserving snapshot state |
| Page loaded from disk directly to Exclusive | CoW captures it — content matches on-disk (harmless duplicate) |
| Transaction commits during backup | Normal operation — CoW captures pre-modification state |

### Interface Sketch

```csharp
public interface ISnapshotManager
{
    /// <summary>Create a new snapshot (full or incremental reverse-delta)</summary>
    Task<SnapshotInfo> CreateSnapshotAsync(
        IBackupStore store,
        IProgress<BackupProgress> progress = null,
        CancellationToken token = default);

    /// <summary>List all available snapshots in a store</summary>
    IAsyncEnumerable<SnapshotInfo> ListSnapshotsAsync(
        IBackupStore store,
        CancellationToken token = default);

    /// <summary>Delete a snapshot (just removes the delta file, no cascade)</summary>
    ValueTask DeleteSnapshotAsync(
        IBackupStore store,
        SnapshotInfo snapshot,
        CancellationToken token = default);
}
```

---

## 7.3 Reverse Delta Engine

### Purpose

Store incremental backup history as **reverse deltas** — each older snapshot records how to reconstruct its state by going backward from the latest (full) snapshot.

<a href="../assets/typhon-backup-reverse-delta.svg"><img src="../assets/typhon-backup-reverse-delta.svg" width="1200" alt="Reverse Delta Chain — Snapshot Strategy"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

### Why Reverse Deltas?

| Operation | Forward Delta | Reverse Delta |
|-----------|--------------|---------------|
| Restore latest | Apply full + all deltas (slow) | Read full directly (instant) |
| Restore old point | Read full directly | Apply reverse deltas backward |
| Delete oldest | Cascade: must rebuild next delta | Just delete file |
| New snapshot cost | Store forward diff | Convert previous full → delta, write new full |

Since "restore latest" is by far the most common operation, reverse deltas optimize for the common case.

### How It Works

When creating Snapshot N+1:
1. Read Snapshot N (the current full backup)
2. For each page P modified since Snapshot N:
   - Compute `delta(P_current, P_previous)` — the diff to go backward
   - Store this delta in the new reverse-delta file for Snapshot N
3. Snapshot N+1 becomes the new full backup (all current pages)
4. Snapshot N is now just a reverse-delta file

### Change Tracking

Each page's `PageBaseHeader` contains a 4-byte `ChangeRevision` that increments on every flush. Combined with a 2-byte `BackupEpoch` tracked per page in the backup index:

```csharp
// In the backup page index, per page:
struct BackupPageEntry
{
    int FilePageIndex;      // 4 bytes — which page
    ushort BackupEpoch;     // 2 bytes — last backup epoch that captured this page
    int ChangeRevision;     // 4 bytes — ChangeRevision at capture time
    long FileOffset;        // 8 bytes — offset in backup file
    int CompressedSize;     // 4 bytes — size after compression
}
// Total: 22 bytes per page
```

A page needs backing up when: `page.ChangeRevision != lastBackup[page].ChangeRevision`

### Delta Algorithm

For 8KB pages, a two-stage approach:

1. **XOR** the current and previous page content → produces a diff buffer
2. **Compress** the XOR result with LZ4/Zstd

When pages have only a few fields changed (common for B+Tree leaf updates), the XOR result is mostly zeros → compresses extremely well. For reorganized pages (B+Tree splits), the delta approaches full page size — acceptable since splits are infrequent.

---

## 7.4 Restore Manager

### Purpose

Reconstruct the database state from the snapshot chain.

### Restore Algorithm

**Restore latest** (most common):
```
1. Read the full snapshot file (latest)
2. Decompress each page
3. Write pages to the data file at their FilePageIndex positions
4. Verify CRC32C checksums
5. Database is ready — no WAL replay needed
```

**Restore older point-in-time**:
```
1. Read the full snapshot (latest state)
2. Apply reverse deltas backward: Δ_N → Δ_(N-1) → ... → Δ_target
3. For each delta: XOR-decompress to reconstruct the target page state
4. Write reconstructed pages to data file
5. Verify CRC32C checksums
```

### Interface Sketch

```csharp
public interface IRestoreManager
{
    /// <summary>Restore database from a specific snapshot</summary>
    Task RestoreAsync(
        IBackupStore store,
        SnapshotInfo snapshot,
        RestoreOptions options = null,
        CancellationToken token = default);

    /// <summary>Validate snapshot chain integrity</summary>
    Task<ValidationResult> ValidateAsync(
        IBackupStore store,
        CancellationToken token = default);
}

public class RestoreOptions
{
    public bool Overwrite { get; set; } = false;
    public bool VerifyChecksums { get; set; } = true;
}
```

> **No WAL replay**: WAL segments are recycled after checkpoint. Restore targets a checkpoint-consistent state. Any transactions committed between the last checkpoint and the backup are included in the captured pages (checkpoint is forced before backup starts).

---

## 7.5 Backup File Format

### Full Snapshot File (`.typhon-snap`)

```
┌──────────────────────────────────────────────────┐
│  Header (256 bytes)                              │
│    Magic: "TYPHSNAP"                             │
│    Version: 1                                    │
│    Type: Full                                    │
│    BackupEpoch: ushort                           │
│    Timestamp: UTC (8 bytes)                      │
│    TotalPages: int                               │
│    Compression: LZ4 | Zstd | None               │
│    DatabaseFileSize: long                        │
│    HeaderChecksum: CRC32C                        │
├──────────────────────────────────────────────────┤
│  Page Data (variable, sequential)                │
│    For each modified page:                       │
│      [CompressedPageData: variable]              │
├──────────────────────────────────────────────────┤
│  Page Index (N × 22 bytes)                       │
│    [FilePageIndex:4, BackupEpoch:2,              │
│     ChangeRevision:4, FileOffset:8,             │
│     CompressedSize:4]                            │
├──────────────────────────────────────────────────┤
│  Footer (64 bytes)                               │
│    PageIndexOffset: long                         │
│    PageCount: int                                │
│    FileChecksum: CRC32C (entire file)            │
└──────────────────────────────────────────────────┘
```

### Reverse Delta File (`.typhon-rdelta`)

```
┌──────────────────────────────────────────────────┐
│  Header (256 bytes)                              │
│    Magic: "TYPHRDLT"                             │
│    Version: 1                                    │
│    Type: ReverseDelta                            │
│    SourceEpoch: ushort (the epoch this goes to)  │
│    TargetEpoch: ushort (the epoch this came from)│
│    Timestamp: UTC                                │
│    DeltaPageCount: int (pages with diffs)        │
│    Compression: LZ4 | Zstd                      │
│    HeaderChecksum: CRC32C                        │
├──────────────────────────────────────────────────┤
│  Delta Data (variable, sequential)               │
│    For each changed page:                        │
│      [Compressed XOR delta: variable]            │
├──────────────────────────────────────────────────┤
│  Delta Index (M × 18 bytes)                      │
│    [FilePageIndex:4, FileOffset:8,               │
│     CompressedSize:4, OriginalRevision:2]        │
├──────────────────────────────────────────────────┤
│  Footer (64 bytes)                               │
│    DeltaIndexOffset: long                        │
│    DeltaCount: int                               │
│    FileChecksum: CRC32C                          │
└──────────────────────────────────────────────────┘
```

---

## 7.6 Retention Policy

### Purpose

Automatically manage backup storage by enforcing configurable limits on how many snapshots to keep and for how long. The policy runs after each new snapshot is created, trimming the oldest reverse deltas until all limits are satisfied.

### Dual-Limit Strategy

The retention policy uses two primary limits plus an optional volume cap. Cleanup triggers when **any** limit is exceeded — the most restrictive constraint wins:

```
MaxCount:     int       (max reverse deltas to keep; 0 = unlimited)
MaxAge:       TimeSpan  (delete deltas older than this; Zero = unlimited)
MaxTotalSize: long      (total backup directory size in bytes; 0 = unlimited)
```

At least one of the three must be set. The full snapshot (`.typhon-snap`) is **never** deleted by retention — only reverse deltas are trimmed.

### Cleanup Algorithm

After each successful snapshot creation:

```
1. List all .typhon-rdelta files in backup directory, sorted oldest-first
2. For each delta (oldest → newest):
     if (deltaCount > MaxCount)         → delete
     if (delta.Timestamp + MaxAge < now) → delete
     if (TotalDirectorySize > MaxTotalSize AND deltaCount > MinKeep) → delete
3. Stop when all limits are satisfied
```

**Key property of reverse deltas:** Oldest deltas are always at the tail of the chain. Deleting them doesn't affect newer deltas or the full snapshot. No cascade, no rebuild.

### MinKeep Guarantee

A `MinKeep` parameter (default: 1) ensures that retention never deletes the last N deltas, even if the volume cap is exceeded. This prevents a situation where a spike in database size causes all history to be purged:

```csharp
MinKeep: int = 1   // Always keep at least this many deltas regardless of MaxTotalSize
```

### Configuration Examples

**Game server — hourly backups, 7 days history, bounded storage:**
```csharp
new RetentionPolicy
{
    MaxCount = 0,             // no count limit
    MaxAge = TimeSpan.FromDays(7),
    MaxTotalSize = 10L * 1024 * 1024 * 1024,  // 10 GB cap
    MinKeep = 5
}
// Result: keeps up to 168 hourly deltas (7 days), but trims oldest if > 10 GB
```

**Simulation — daily backups, keep last 30, no volume concern:**
```csharp
new RetentionPolicy
{
    MaxCount = 30,
    MaxAge = TimeSpan.Zero,   // no age limit
    MaxTotalSize = 0          // no volume limit
}
// Result: keeps exactly last 30 daily snapshots (30 days of history)
```

**Embedded device — minimal storage, frequent backups:**
```csharp
new RetentionPolicy
{
    MaxCount = 5,
    MaxAge = TimeSpan.FromHours(12),
    MaxTotalSize = 500L * 1024 * 1024,  // 500 MB cap
    MinKeep = 2
}
// Result: at most 5 deltas, none older than 12h, max 500 MB total, always keeps 2
```

### Behavior Matrix

| Scenario | MaxCount=30 | MaxAge=7d | MaxTotalSize=10GB | What happens |
|----------|:-----------:|:---------:|:-----------------:|--------------|
| Hourly, small DB (2GB) | 30h of history | 168 deltas | Never hit | Age wins: 7 days |
| Hourly, large DB (20GB) | 30h of history | 168 deltas | Trims early | Volume wins |
| Daily, small DB (2GB) | 30 days | 7 days | Never hit | Age wins: 7 days |
| Daily, large DB (50GB) | 30 days | 7 days | Trims early | Volume wins |
| Every 10 min, small DB | 5h of history | 1008 deltas | Unlikely hit | Count wins: 30 |

### Interface Sketch

```csharp
public class RetentionPolicy
{
    /// <summary>Max reverse deltas to keep. 0 = unlimited.</summary>
    public int MaxCount { get; set; }

    /// <summary>Delete deltas older than this. Zero = unlimited.</summary>
    public TimeSpan MaxAge { get; set; }

    /// <summary>Max total size of backup directory in bytes. 0 = unlimited.</summary>
    public long MaxTotalSize { get; set; }

    /// <summary>Always keep at least this many deltas, even if over volume budget.</summary>
    public int MinKeep { get; set; } = 1;
}

public interface IRetentionManager
{
    /// <summary>Apply retention policy, deleting expired deltas. Returns deleted count.</summary>
    int ApplyRetention(string backupDirectory, RetentionPolicy policy);

    /// <summary>Preview what would be deleted without actually deleting.</summary>
    IReadOnlyList<SnapshotInfo> PreviewRetention(string backupDirectory, RetentionPolicy policy);

    /// <summary>Get current backup directory statistics.</summary>
    BackupDirectoryStats GetStats(string backupDirectory);
}

public class BackupDirectoryStats
{
    public long TotalSize { get; set; }
    public int DeltaCount { get; set; }
    public DateTime OldestDelta { get; set; }
    public DateTime NewestDelta { get; set; }
    public long FullSnapshotSize { get; set; }
}
```

### Integration with Snapshot Manager

Retention is applied automatically after `CreateSnapshotAsync` completes:

```csharp
// Inside SnapshotManager.CreateSnapshotAsync:
var snapshot = await CreateSnapshotInternal(...);
if (_retentionPolicy != null)
{
    _retentionManager.ApplyRetention(destinationPath, _retentionPolicy);
}
return snapshot;
```

The `PreviewRetention` method allows applications to show users what *would* be deleted before changing the policy — important for avoiding surprises when tightening limits.

---

## 7.7 Backup Store Abstraction

The backup system writes and reads through a **pluggable `IBackupStore` interface** rather than directly to the filesystem. This separates I/O mechanics from backup logic, enabling local, remote (S3, Azure Blob), or custom storage backends without changing the engine.

### Design Rationale

The reverse-delta algorithm's I/O pattern maps naturally to a streaming interface:
- **Snapshot creation**: Sequential page writes → `IBackupWriter.WriteAsync` calls
- **Page index at end**: Written as a final block before `FinalizeAsync`
- **Delta creation**: Requires reading the previous full snapshot → `IBackupReader.ReadAsync` with offset
- **Retention cleanup**: Listing + deleting old files → `ListAsync` + `DeleteAsync`

For remote storage, this maps cleanly to multipart upload (each page write = a part), and random-access reads for delta computation.

### Interface Sketch

```csharp
/// <summary>Storage backend for backup files (snapshots and deltas)</summary>
public interface IBackupStore
{
    /// <summary>List all backup files in the store</summary>
    IAsyncEnumerable<BackupFileInfo> ListAsync(CancellationToken token = default);

    /// <summary>Delete a backup file by name</summary>
    ValueTask DeleteAsync(string fileName, CancellationToken token = default);

    /// <summary>Open a writer for a new backup file</summary>
    IBackupWriter CreateWriter(string fileName);

    /// <summary>Open a reader for an existing backup file</summary>
    IBackupReader OpenReader(string fileName);
}

/// <summary>Sequential writer for backup data (supports streaming/multipart upload)</summary>
public interface IBackupWriter : IAsyncDisposable
{
    /// <summary>Append data sequentially</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default);

    /// <summary>Current write position (bytes written so far)</summary>
    long Position { get; }

    /// <summary>Finalize the file (flush, complete multipart upload, etc.)</summary>
    ValueTask FinalizeAsync(CancellationToken token = default);
}

/// <summary>Random-access reader for backup data</summary>
public interface IBackupReader : IAsyncDisposable
{
    /// <summary>Read data at a specific offset (required for delta computation)</summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default);

    /// <summary>Total file length</summary>
    long Length { get; }
}

/// <summary>Metadata about a backup file in the store</summary>
public record BackupFileInfo(
    string FileName,
    long Size,
    DateTime CreatedUtc,
    BackupFileType Type);  // Snapshot or ReverseDelta
```

### Default Implementation: Local File Store

The built-in `LocalBackupStore` wraps standard `FileStream` operations:

| Interface Method | Local Implementation |
|-----------------|---------------------|
| `ListAsync` | `Directory.EnumerateFiles` + parse headers |
| `DeleteAsync` | `File.Delete` |
| `CreateWriter` | `new FileStream(..., FileMode.Create, FileAccess.Write)` |
| `OpenReader` | `new FileStream(..., FileMode.Open, FileAccess.Read)` |
| `FinalizeAsync` | `FileStream.FlushAsync` |

### Remote Storage Considerations

Remote implementations (shipped as external packages) must handle:

| Concern | Approach |
|---------|----------|
| **Sequential writes** | Map to multipart upload parts (S3) or append blocks (Azure) |
| **Finalize** | `CompleteMultipartUpload` / commit block list |
| **Random reads** | HTTP Range requests (`Range: bytes=offset-offset+length`) |
| **Delta creation** | Download previous full snapshot, or cache locally during backup |
| **Atomicity** | File only visible after `FinalizeAsync` — incomplete uploads are invisible |
| **Listing** | Prefix-based listing with header parsing for metadata |

> **Key trade-off for remote deltas**: Creating a reverse delta requires reading the *previous* full snapshot to compute XOR diffs. Remote-only deployments must either (a) download the previous snapshot during backup, or (b) keep a local cache of the latest full snapshot. The `IBackupReader` interface supports both patterns transparently.

### Integration with Other Components

- **SnapshotManager** (§7.2): Accepts `IBackupStore` as a constructor dependency instead of path strings
- **RestoreManager** (§7.4): Opens snapshots/deltas via `IBackupStore.OpenReader`
- **RetentionManager** (§7.6): Uses `ListAsync`/`DeleteAsync` for cleanup — works identically for local and remote

---

## Compression

| Algorithm | Ratio | Speed | Use Case |
|-----------|-------|-------|----------|
| **LZ4** | ~2:1 | ~2 GB/s | Default — fast backup with acceptable size |
| **Zstd** | ~3:1 | ~500 MB/s | Long-term archival storage |
| **None** | 1:1 | Max | Ultra-fast local backup, debugging |

For XOR-based reverse deltas, compression is particularly effective: unchanged bytes produce zeros in the XOR result, which compress to near-zero size.

---

## Code Locations (Planned)

| Component | Planned Location |
|-----------|------------------|
| CoW Shadow Buffer | `src/Typhon.Engine/Backup/CowShadowBuffer.cs` |
| Snapshot Manager | `src/Typhon.Engine/Backup/SnapshotManager.cs` |
| Reverse Delta Engine | `src/Typhon.Engine/Backup/ReverseDeltaEngine.cs` |
| Restore Manager | `src/Typhon.Engine/Backup/RestoreManager.cs` |
| Backup Format | `src/Typhon.Engine/Backup/BackupFormat.cs` |
| Backup Page Index | `src/Typhon.Engine/Backup/BackupPageIndex.cs` |
| Retention Manager | `src/Typhon.Engine/Backup/RetentionManager.cs` |
| Backup Store (interface) | `src/Typhon.Engine/Backup/IBackupStore.cs` |
| Local Backup Store | `src/Typhon.Engine/Backup/LocalBackupStore.cs` |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| **WAL dependency** | None | Backups are self-contained page snapshots; checkpoint forced before backup |
| **Delta direction** | Reverse (latest=full) | Optimizes restore-latest (common case); deletion is free |
| **CoW trigger** | TryLatchPageExclusive | Single convergence point for all write paths |
| **Shadow pool** | Pre-allocated pinned ring buffer | Lock-free allocation, no GC pressure |
| **Change tracking** | ChangeRevision (4B) + BackupEpoch (2B) | Per-page, lightweight, no wraparound concern |
| **Backup unit** | Page (8KB) | Matches storage model, no semantic knowledge needed |
| **Delta algorithm** | XOR + compress | Simple, fast, excellent ratio for sparse changes |
| **Compression** | LZ4 default | Best speed/ratio for 8KB blocks |
| **Concurrent backups** | One at a time | Simplicity; at most 2 page revisions (live + shadow) |
| **Consistency** | Checkpoint-first | No fuzzy snapshot logic needed |
| **Retention** | Dual-limit (count + age + volume cap) | Simple, predictable, frequency-independent |
| **Storage I/O** | Pluggable `IBackupStore` | Engine-agnostic; local default, remote as external package |

---

## Open Questions

1. **Shadow pool sizing heuristic** — Should it be a fixed size, or dynamically sized based on database write rate? Starting at 512 pages (4MB) seems reasonable.

2. **Backup scheduling** — Should the engine provide a built-in periodic scheduler, or leave this to the application layer?

3. **Encryption** — Should backup files support encryption at rest? If so, which algorithm (AES-256-GCM)?
