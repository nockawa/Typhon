# I/O Pipeline & Crash Semantics: From RandomAccess.WriteAsync to NVMe NAND

**Date:** January 2025
**Status:** Complete
**Category:** Persistence / Durability
**Related:** [Reliability.md](Reliability.md) (WAL architecture)

---

## Purpose

This document provides an exhaustive analysis of the complete I/O write pipeline from Typhon's `RandomAccess.WriteAsync` call down to the NVMe SSD's NAND flash on **both Windows and Linux**, with particular focus on:

1. What each layer does and what "completion" means at that layer
2. What happens during a **process crash** at each layer
3. What happens during an **OS crash / kernel panic / power loss** at each layer
4. Whether writes are sequential (byte-by-byte) or atomic, and when torn writes can occur
5. Implications for Typhon's durability strategy on both platforms

---

## Table of Contents

1. [Typhon's Current I/O Pattern](#1-typhons-current-io-pattern)
2. [Layer 1: .NET RandomAccess API (Cross-Platform)](#2-layer-1-net-randomaccess-api)
3. [Layer 2: OS Kernel Transition](#3-layer-2-os-kernel-transition)
4. [Layer 3: OS Page/File Cache](#4-layer-3-os-pagefile-cache)
5. [Layer 4: File System](#5-layer-4-file-system)
6. [Layer 5: Storage Driver / Block Layer](#6-layer-5-storage-driver--block-layer)
7. [Layer 6: NVMe Protocol (Shared)](#7-layer-6-nvme-protocol)
8. [Layer 7: SSD Controller & NAND Flash (Shared)](#8-layer-7-ssd-controller--nand-flash)
9. [Complete Crash Analysis Matrix](#9-complete-crash-analysis-matrix)
10. [Write Ordering & Torn Write Analysis](#10-write-ordering--torn-write-analysis)
11. [Platform Comparison Summary](#11-platform-comparison-summary)
12. [Durability Options & Trade-offs for Typhon](#12-durability-options--trade-offs-for-typhon)
13. [Key Findings & Recommendations](#13-key-findings--recommendations)

---

## 1. Typhon's Current I/O Pattern

### File Handle Configuration

Typhon opens its database file with:
```csharp
File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite,
    FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
```

This translates to platform-specific flags:

| FileOptions Flag | Windows Effect | Linux Effect |
|-----------------|----------------|--------------|
| `Asynchronous` | `FILE_FLAG_OVERLAPPED` (true kernel async via IOCP) | **Nothing at kernel level** — only tells .NET to use ThreadPool dispatch |
| `RandomAccess` | `FILE_FLAG_RANDOM_ACCESS` (disables read-ahead in Cache Manager) | `posix_fadvise(POSIX_FADV_RANDOM)` (disables readahead, advisory) |

**Notably absent on both platforms:**
- No write-through (`FILE_FLAG_WRITE_THROUGH` / `O_SYNC`)
- No direct I/O (`FILE_FLAG_NO_BUFFERING` / `O_DIRECT`)
- No explicit flush/fsync calls anywhere in the codebase

### Write Operation

```csharp
// PagedMMF.cs:928-943
internal ValueTask SavePageInternal(int firstMemPageIndex, int length)
{
    var pageOffset = filePageIndex * (long)PageSize;   // 8192-byte aligned
    var lengthToWrite = PageSize * length;              // Multiple of 8KB
    var pageData = _memPages.AsMemory(firstMemPageIndex * PageSize, lengthToWrite);
    return RandomAccess.WriteAsync(_fileHandle, pageData, pageOffset);
}
```

Key characteristics:
- Writes are always **8KB-aligned** (page-aligned at file offset)
- Writes are always **multiples of 8KB** (1 or more contiguous pages)
- Write batching groups contiguous dirty pages into single I/O operations
- The `_memPages` buffer is GCHandle-pinned (won't move during async I/O)

### Current Durability Status

**None.** When `RandomAccess.WriteAsync` completes, data is only in the OS page cache (volatile RAM). There is no mechanism to ensure data reaches stable storage.

---

## 2. Layer 1: .NET RandomAccess API

### Windows Implementation

```
RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, long offset, CancellationToken)
  -> WriteAtOffsetAsync()                    [RandomAccess.Windows.cs]
    -> QueueAsyncWriteFile()
      -> handle.GetOverlappedValueTaskSource()    // Pooled, zero-alloc
      -> vts.PrepareForOperation(buffer, offset)  // Sets NativeOverlapped.Offset/OffsetHigh
      -> Interop.Kernel32.WriteFile(handle, pinnedBuffer, numBytes, IntPtr.Zero, overlapped)
      -> Returns ValueTask wrapping the OverlappedValueTaskSource
```

- Uses Win32 `WriteFile` with `OVERLAPPED` structure
- Handle is bound to an **I/O Completion Port (IOCP)** via `ThreadPoolBoundHandle`
- **Truly async**: no thread consumed while I/O is in-flight
- For buffered writes, often completes **synchronously** (just a memcpy to cache pages)
- The IOCP thread signals completion when the I/O Manager finishes

### Linux Implementation

```
RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, long offset, CancellationToken)
  -> WriteAtOffsetAsync()                    [RandomAccess.Unix.cs]
    -> handle.GetThreadPoolValueTaskSource()
      -> ThreadPool queues work item (IThreadPoolWorkItem)
        -> Worker thread: Interop.Sys.PWrite(handle, buffer, count, offset)
          -> pwrite(fd, buf, count, offset)   [BLOCKS in kernel]
        -> Signals ValueTask completion
```

- Uses POSIX `pwrite()` (or `pwritev()` for scatter/gather)
- **NOT truly async**: a ThreadPool thread blocks in the kernel during I/O
- `FileOptions.Asynchronous` has NO kernel-level effect on Linux
- Concurrent I/O throughput limited by ThreadPool size (default: `Environment.ProcessorCount`)
- io_uring support does NOT exist in .NET runtime (as of .NET 10, 2025)

### What "ValueTask Completion" Means (Both Platforms)

The `ValueTask` completing means:
- **The OS kernel has accepted the data** into its page/file cache
- The data has been copied from Typhon's buffer into kernel-managed pages
- The kernel has marked those pages as **dirty**
- **The data is NOT on disk.** It exists only in volatile RAM.

This is identical on both platforms — completion = data in OS cache, not durable.

### Crash Analysis (Layer 1)

| Crash Type | Windows | Linux |
|-----------|---------|-------|
| Process crash before completion | Write lost entirely. No partial state. | Same |
| Process crash after completion | Data in OS page cache. Lazy writer/flush threads will eventually flush to disk. Survives process crash. | Same |
| OS crash / power loss | Data in page cache is **permanently lost** | Same |

### Key Platform Difference: Async Scalability

| Aspect | Windows (IOCP) | Linux (ThreadPool) |
|--------|----------------|-------------------|
| Threads consumed per pending write | 0 | 1 |
| Max concurrent writes | Thousands | ~ProcessorCount (default) |
| Latency overhead | Minimal (kernel callback) | Context switch + scheduling |
| Mitigation | N/A | `ThreadPool.SetMinThreads(64, 64)` or dedicated I/O threads |

---

## 3. Layer 2: OS Kernel Transition

### Windows: NT I/O Manager

```
WriteFile (kernel32.dll) -> NtWriteFile (ntdll.dll -> SYSCALL)
  -> I/O Manager: IoAllocateIrp()
  -> Set IRP_MJ_WRITE, buffer, offset, length
  -> IoCallDriver() to file system driver stack
```

**IRP (I/O Request Packet)** traversal for buffered writes:
```
File System Filter Drivers (antivirus, backup)
  -> NTFS (ntfs.sys)
    -> Cache Manager: CcCopyWrite() — memcpy to cache pages
    -> IRP completed with STATUS_SUCCESS
    -> IOCP notification sent
```

**Critical**: For buffered writes, the storage driver stack is **never involved** during the write. Only the Cache Manager is touched. Storage drivers are involved later during lazy writer flushes.

### Linux: VFS and System Call

```
pwrite(fd, buf, count, offset)  [SYSCALL]
  -> VFS: vfs_write()
    -> File system: ext4_file_write_iter() / xfs_file_write_iter()
      -> generic_perform_write()
        -> For each page in the write range:
          -> grab_cache_page_write_begin()   // Get/allocate page cache page
          -> copy_from_user()                 // Copy data from userspace
          -> mark_page_dirty()               // Flag page as dirty
    -> Returns bytes written (or error)
```

**Key differences from Windows:**
- Linux pwrite touches the **file system layer** even for buffered writes (ext4/XFS manages the page cache integration)
- The copy is done page-by-page (4KB granularity), not as a single bulk operation
- Each page is independently marked dirty

### Crash Analysis (Layer 2)

| Crash Type | Windows | Linux |
|-----------|---------|-------|
| Process crash | Data safely in OS cache if syscall returned | Same |
| OS crash during memcpy/copy_from_user | Page cache lost entirely (irrelevant) | Same |
| OS crash after completion | Data in cache, not durable | Same |

---

## 4. Layer 3: OS Page/File Cache

### Windows: Cache Manager

**Architecture:**
- Files mapped via **VACBs** (Virtual Address Control Blocks), each 256KB view
- Dirty pages tracked in Memory Manager's page tables
- Dirty file list: `CcDirtySharedCacheMapList`

**Lazy Writer (flush thread):**
- Runs every **~1 second** via `CcLazyWriteScan()`
- Writes approximately **1/8 of total dirty data** per cycle
- Issues paging I/O IRPs (`IRP_MJ_WRITE | IRP_PAGING_IO`) through the storage stack
- Under memory pressure: **Mapped Page Writer** forces earlier writeback
- Write throttling: if dirty pages exceed threshold, new writes may block

**Write ordering:** The Cache Manager does NOT guarantee ordering between different file regions. Pages may be flushed in any order, across different lazy writer cycles.

### Linux: Page Cache + Writeback

**Architecture:**
- Pages associated with inodes via **address_space** (radix tree / xarray)
- Dirty pages tracked per-inode in `i_pages`
- Per-BDI (Block Device Info) flush worker threads (replaced pdflush in kernel 2.6.32+)

**Flush workers (writeback threads):**
- Per-device `wb_workfn()` threads
- Controlled by kernel parameters:
  - `dirty_writeback_centisecs` = 500 (5 seconds between writeback wakeups, default)
  - `dirty_expire_centisecs` = 3000 (dirty pages older than 30 seconds are written, default)
  - `dirty_background_ratio` = 10% (background writeback starts at 10% of memory dirty)
  - `dirty_ratio` = 20% (foreground throttling at 20% dirty — process blocks in write())
- Additional triggers: memory pressure (kswapd), sync operations, unmount

**Write ordering:** Linux also does NOT guarantee ordering between page writebacks. The writeback thread may flush pages in any order. Different pages may go to disk in different I/O batches.

### Crash Analysis (Layer 3)

| Crash Type | Windows | Linux |
|-----------|---------|-------|
| Process crash | Data safe in cache, lazy writer/writeback will flush | Same |
| OS crash before flush | All dirty pages **permanently lost** | Same |
| OS crash during flush | **Partial flush state** — some pages made it to disk, others didn't | Same |
| Timing window (data at risk) | 1-10+ seconds (lazy writer cycle) | 5-30+ seconds (dirty_writeback + dirty_expire) |

### Critical Implication: The Torn Transaction Problem

If Typhon writes pages A, B, C (for one transaction) and the OS crashes mid-flush:
- **Any subset** of {A, B, C} may be on disk
- **Any ordering** is possible (B on disk but not A or C)
- **Individual pages may be torn** (see Layer 6/7)
- This is why WAL is essential: it provides the atomic commit record that allows recovery

---

## 5. Layer 4: File System

### Windows: NTFS

**User data writes:** NTFS is relatively transparent for paging writes from the Cache Manager:
1. Translates file offset to **Logical Cluster Numbers (LCNs)** via `$DATA` attribute run list
2. Passes write to volume manager (contiguous extents = single I/O)
3. Updates metadata (timestamps) — journaled in `$LogFile`

**NTFS journals metadata ONLY, not user data.** Key properties:
- Transaction log (`$LogFile`) uses write-ahead logging (WAL) for metadata
- File system structural consistency guaranteed after crash
- User data consistency NOT guaranteed — files may contain torn pages
- Uses **Update Sequence Arrays (USAs)** for its own multi-sector metadata structures (torn-write detection for MFT records)

**Sector atomicity assumption:** NTFS assumes single-sector writes are atomic:
- 512-byte sectors → 512B atomicity
- 4Kn drives → 4096B atomicity
- 512e drives → 512B logical (but 4KB physical — potential mismatch)

### Linux: ext4 (default) and XFS

#### ext4 Journaling Modes

| Mode | Journals | User Data Guarantee on Crash |
|------|----------|------------------------------|
| `data=journal` | Data + Metadata | Full: see pre- or post-transaction, never partial |
| `data=ordered` (default) | Metadata only | No stale data exposure; recent writes may be lost entirely |
| `data=writeback` | Metadata only | May expose stale data from deleted files in newly-allocated blocks |

**ext4 ordered mode** (the typical Linux server configuration):
- Data blocks are forced to disk **before** associated metadata journal commit
- This prevents "stale data exposure" (reading deleted file contents through new allocations)
- Does NOT prevent torn writes within user data
- Does NOT provide ordering between different user data writes

**ext4 delayed allocation:** Blocks are not allocated until writeback. Same crash risk as Windows lazy writer — data may never reach disk if crash occurs before writeback.

#### XFS

- Journals **metadata only** (no data journal mode)
- Uses **unwritten extents**: newly-allocated blocks read as zeros until data is written (prevents stale data exposure, similar to ext4 ordered)
- Generally more efficient `fsync` behavior than ext4 (less inter-file coupling in the log)
- XFS + `O_DIRECT` + `O_DSYNC` has a **FUA optimization**: overwrites to existing extents use per-I/O FUA flag (no full cache flush needed)

#### File System Write Atomicity (Both ext4 and XFS)

**Neither provides write atomicity for user data:**
- A single `write()` / `pwrite()` call has no atomicity guarantee
- Writes spanning multiple file system blocks (4KB) can be partially flushed
- Writes within a single block can be torn at the sector level
- `data=ordered` only orders data-before-metadata, NOT data blocks among themselves

### Linux: New Atomic Write Support (Kernel 6.11+, 2024-2025)

Linux kernel 6.11 introduced `RWF_ATOMIC` for `pwritev2()`:

```c
struct iovec iov = { .iov_base = aligned_buf, .iov_len = 8192 };
pwritev2(fd, &iov, 1, offset, RWF_ATOMIC);
```

**Requirements:**
- File opened with `O_DIRECT`
- Write size must be a **power of two** between `stx_atomic_write_unit_min` and `stx_atomic_write_unit_max`
- Write offset must be **naturally aligned** to write size
- Hardware must support the requested atomic size (AWUPF ≥ write size)
- Query via `statx(STATX_WRITE_ATOMIC)` fields

**Filesystem support:**
- **XFS (6.13+):** Single-block atomic writes with Direct I/O (uses COW extent swap)
- **ext4 (6.13+):** Single-block, requires 16KB PAGE_SIZE (not x86_64 yet)
- **XFS multi-block (6.16+):** Multi-block atomic writes planned
- **ext4 bigalloc multi-block (6.16+):** Via cluster-based allocation

**Impact for Typhon:** If the NVMe drive reports AWUPF ≥ 2 (8KB for 4KB LBA), and the kernel supports it, Typhon could use `RWF_ATOMIC` for 8KB page writes — **eliminating the torn page problem entirely** without needing a double-write buffer. This is a future optimization opportunity (requires P/Invoke on Linux, .NET doesn't expose `RWF_ATOMIC`).

### Crash Analysis (Layer 4)

| Crash Type | NTFS | ext4 ordered | XFS |
|-----------|------|--------------|-----|
| Process crash | No effect | No effect | No effect |
| OS crash: metadata | Journal replays, FS structure consistent | Journal replays | Log replays |
| OS crash: user data | May be torn (no protection) | May be lost or torn (no protection) | May be lost or torn (zeros via unwritten extents) |
| 8KB page: torn? | Yes (2x 4KB sectors) | Yes | Yes |
| Power loss during write | Same as OS crash | Same | Same |

---

## 6. Layer 5: Storage Driver / Block Layer

### Windows: Storage Driver Stack

```
NTFS paging write IRP
  -> Volume Manager (volmgr.sys)
    -> Partition Manager (partmgr.sys)
      -> Disk Class Driver (disk.sys) — builds SCSI SRBs/CDBs
        -> Storport (storport.sys) — queuing, DMA
          -> StorNVMe (stornvme.sys) — translates SCSI to NVMe commands
            -> NVMe Controller (hardware)
```

**SCSI WRITE CDB:**
```
WRITE(10): Opcode=0x2A, Byte 1 Bit 3=FUA, LBA (4 bytes), Transfer Length
WRITE(16): Opcode=0x8A, for LBA > 2TB
```

**FUA behavior on Windows:**
- `FILE_FLAG_WRITE_THROUGH` → FUA bit set in SCSI WRITE CDB
- StorNVMe translates SCSI FUA → NVMe Write CDW12 bit 30 (FUA)
- Only used for SCSI/SAS/NVMe (NOT for SATA — Windows uses FLUSH CACHE for SATA instead)

**SYNCHRONIZE CACHE** (generated by `FlushFileBuffers`):
- SCSI opcode 0x35 → StorNVMe translates to NVMe Flush command (opcode 0x00)
- Flushes the **entire** drive volatile write cache (all files, not just target file)

### Linux: Block Layer (blk-mq)

```
Filesystem writeback (or O_DIRECT write)
  -> submit_bio()
    -> Block layer: BIO (Block I/O) structure
      -> I/O scheduler (mq-deadline, kyber, or "none" for NVMe)
        -> Request merging (adjacent BIOs combined)
          -> NVMe driver (drivers/nvme/host/)
            -> NVMe Submission Queue
              -> NVMe Controller (hardware)
```

**BIO flags for durability:**

| Flag | Meaning | Generated by |
|------|---------|-------------|
| `REQ_FUA` | Force Unit Access — data must reach stable media before completion | `O_DSYNC`, `O_SYNC`, or explicit FUA write |
| `REQ_PREFLUSH` | Issue a Flush command BEFORE this write | `fsync()` / `fdatasync()` on some configs |
| `REQ_SYNC` | Synchronous write (higher priority scheduling) | `O_SYNC`, `fsync()`, direct reclaim |

**I/O Scheduler for NVMe:**
- **Recommended: "none" (no scheduler)** — NVMe has its own internal scheduling
- mq-deadline or kyber add latency with no benefit for random-access databases
- `echo none > /sys/block/nvme0n1/queue/scheduler`

**Linux FUA handling:**
- `O_DIRECT | O_DSYNC` on XFS for overwrites → single FUA write (no flush needed)
- `O_DIRECT | O_DSYNC` on ext4 → FUA write + journal flush (more expensive)
- `fdatasync()` after buffered write → flush all dirty pages + FLUSH command
- Linux NVMe driver (`nvme_setup_rw()`) sets NVMe FUA bit when BIO has `REQ_FUA`

### Crash Analysis (Layer 5)

| Crash Type | Windows | Linux |
|-----------|---------|-------|
| Process crash | I/O already submitted continues to completion | Same |
| OS crash, I/O in driver queue | IRP abandoned. If already submitted to hardware, may still complete. | BIO abandoned. If already in NVMe SQ, may still complete. |
| OS crash, driver completed | Data in NVMe controller (volatile unless FUA) | Same |
| Power loss | Data in driver queues = lost. Data in NVMe DRAM = depends on PLP. | Same |

---

## 7. Layer 6: NVMe Protocol (Shared — Both Platforms)

The NVMe layer is identical regardless of OS. Both Windows (StorNVMe) and Linux (drivers/nvme/) translate to the same NVMe commands.

### NVMe Write Command

- **Opcode:** 0x01 (Write)
- **CDW10-11:** Starting LBA (64-bit)
- **CDW12 bits 15:0:** Number of Logical Blocks minus 1 (0-based)
- **CDW12 bit 30:** FUA (Force Unit Access)
- Submitted to **Submission Queue (SQ)**, controller signals completion via **Completion Queue (CQ)**

### What "Command Completion" Means

| FUA Bit | Completion Means | Data Location |
|---------|------------------|---------------|
| 0 (no FUA) | Controller has accepted data | Controller DRAM (volatile!) |
| 1 (FUA set) | Data is on non-volatile media | NAND flash (durable) |

**Without FUA:** completion only means the DMA transfer from host memory to controller DRAM finished. Data has NOT been programmed to NAND. It will be "eventually" (milliseconds), but that window is vulnerable.

### NVMe Atomic Write Parameters

| Parameter | Meaning | Typical Consumer | Typical Enterprise |
|-----------|---------|-----------------|-------------------|
| **AWUPF** | Atomic Write Unit Power Fail (controller-wide) | 0 (= 1 LBA, e.g. 4KB) | Up to 128KB |
| **NAWUPF** | Namespace-specific AWUPF | Same or larger | Up to 256KB |
| **NABSPF** | Atomic boundary (writes cannot cross) | 0 (no boundary) | Device-specific |

**For Typhon's 8KB pages (4KB LBA format):**
- AWUPF=0 (1 LBA = 4KB): 8KB write is **NOT atomic**. Torn page possible.
- AWUPF≥1 (≥ 8KB): 8KB write **IS atomic**. No torn page.

### NVMe Flush Command

- **Opcode:** 0x00 (I/O command set)
- Forces all volatile-cached data to non-volatile media
- Completion = all previously-completed writes are now on NAND
- **Expensive:** flushes the entire volatile write cache, not per-file
- This is what `FlushFileBuffers()` (Windows) and `fsync()` (Linux) ultimately generate

### NVMe Write Ordering

**NVMe provides NO ordering guarantees between commands.** Even commands in the same SQ may be reordered by the controller. Only FUA and Flush provide durability barriers.

### Crash Analysis (Layer 6)

| Crash Type | Consequence |
|-----------|-------------|
| Process crash | No effect — NVMe operates independently |
| OS crash, command in SQ | Race: controller may or may not fetch/execute it |
| OS crash, completion posted (no FUA) | Data in controller DRAM — vulnerable to power loss |
| Power loss, data in DRAM, Consumer SSD | **LOST** — no PLP capacitors for user data |
| Power loss, data in DRAM, Enterprise SSD | **SAVED** — PLP capacitors flush DRAM to NAND |
| Power loss with FUA | Data on NAND — durable regardless of PLP |

---

## 8. Layer 7: SSD Controller & NAND Flash (Shared — Both Platforms)

### Write Path Inside the SSD

```
Host Data (via PCIe DMA)
  -> Controller DRAM Write Buffer (~2-8 MB)
    -> FTL: Logical-to-Physical address mapping
      -> NAND Programming Queue
        -> NAND Flash Array (bits written to cells)
```

### NAND Flash Characteristics

| Property | Typical Value | Implication |
|----------|---------------|-------------|
| Page size | 4KB - 16KB | Minimum write unit to NAND |
| Block size | 256 pages (1-4 MB) | Minimum erase unit |
| Program time | 200-2000 μs | Time to write one NAND page |
| Write endurance | 1K-100K P/E cycles | Lifetime (QLC < TLC < MLC < SLC) |

### Out-of-Place Writes

SSDs never overwrite NAND in place:
1. New data → free page in current write block
2. FTL updates L2P mapping in DRAM
3. Old physical page marked as stale
4. Garbage collection reclaims blocks with many stale pages

**Implication:** A "write to LBA X" doesn't touch the old data's physical location. The old data persists on NAND until the block is erased. This is why the FTL mapping table is critical — if it's lost, the SSD can't find any data.

### NAND Page Program Atomicity

- Single NAND page program is **NOT guaranteed atomic** by hardware
- Power loss mid-program → cells in undefined charge states
- MLC/TLC/QLC: interrupted upper-page programming can corrupt already-written lower pages on the same wordline
- **Controller firmware provides LBA-level atomicity:** L2P mapping updated only after successful program verification. Failed programs → old mapping still valid.

### Power Loss Protection (PLP)

| SSD Type | PLP Hardware | On Power Loss |
|----------|-------------|---------------|
| **Enterprise** | Tantalum/polymer capacitors | Flush DRAM buffer to NAND (~6ms for 8MB), then FTL metadata |
| **Consumer** | None or minimal (metadata only) | DRAM buffer contents **LOST**. FTL rebuilt from NAND metadata tags. |

**USENIX FAST study:** 13 of 15 consumer SSDs lost or corrupted data under power-failure testing.

### Crash Analysis (Layer 7)

| Crash Type | Consequence |
|-----------|-------------|
| Process crash | No effect — SSD independent |
| OS crash, data in controller DRAM | Controller continues normally (OS crash ≠ power loss) |
| Power loss, Enterprise SSD | PLP saves in-flight data — durable |
| Power loss, Consumer SSD | DRAM buffer lost — data gone |
| Power loss during NAND programming | ECC detects partial program, falls back to old mapping |
| Power loss, FTL not flushed (Consumer) | Must rebuild from NAND metadata — slow, may lose recent writes |

---

## 9. Complete Crash Analysis Matrix

### Scenario: Typhon writes 8KB page (current config: buffered, no flush, no WriteThrough)

| Layer | After Completion | Process Crash | OS Crash / Kernel Panic | Power Loss (Consumer) | Power Loss (Enterprise) |
|-------|-----------------|---------------|------------------------|----------------------|------------------------|
| .NET RandomAccess | ValueTask done | ✅ Data in OS cache | ❌ Lost | ❌ Lost | ❌ Lost |
| OS Cache (dirty) | Pages marked dirty | ✅ Flush thread writes later | ❌ Lost | ❌ Lost | ❌ Lost |
| Flush thread writes | IRP/BIO submitted | ✅ Completes at HW level | ⚠️ Race (may complete) | ❌ Lost | ❌ Lost |
| NVMe completion (no FUA) | In controller DRAM | ✅ Controller programs NAND | ✅ Controller continues | ❌ DRAM lost | ✅ PLP saves |
| NAND programmed | On flash | ✅ Fully durable | ✅ Fully durable | ✅ Durable | ✅ Durable |

### Key Takeaways

1. **Process crash is survivable** — OS cache persists and will flush to disk eventually
2. **OS crash loses all unflushed data** — typically 5-30 seconds of writes on Linux, 1-10 seconds on Windows
3. **Power loss is catastrophic without FUA/flush** — even data accepted by the NVMe controller may be lost (consumer SSDs)
4. **Enterprise SSDs with PLP** add one layer of defense — but only for data already in the controller's DRAM

---

## 10. Write Ordering & Torn Write Analysis

### Can You Get "First 4KB New, Second 4KB Old" in an 8KB Page?

**Yes, absolutely.** This is the torn page problem:

| Drive Configuration | AWUPF | 8KB Write Atomic? | Torn Page Risk |
|--------------------|-------|-------------------|---------------|
| Consumer NVMe, 4Kn (4KB LBA) | 4KB (1 LBA) | **NO** | Either 4KB half could be torn |
| Consumer NVMe, 512e | 512B (1 LBA) | **NO** | Any of 16 sectors could be torn |
| Enterprise NVMe, AWUPF≥8KB | ≥8KB | **YES** | Safe for single-page writes |
| Enterprise NVMe, AWUPF≥64KB | ≥64KB | **YES** | Safe even for multi-page batches |

### Is the Write Sequential (First Byte to Last Byte)?

**No.** At no layer is sequential byte ordering guaranteed:

1. **OS cache memcpy:** Effectively atomic (all or nothing for the syscall), but irrelevant for durability
2. **Flush to storage:** 8KB write → single I/O command → single NVMe Write. DMA transfer is sequential at PCIe level but controller buffers in DRAM first
3. **Controller to NAND:** Controller decides order of sector programming. For 8KB spanning two 4KB physical pages, programming order is undefined

**The uncertainty is at the sector boundary level,** not byte-by-byte. Within a single sector (4KB on modern NVMe), the write is atomic. The torn write happens between sectors.

### Between Multiple Page Writes (Multi-Page Transaction)

**No ordering guaranteed whatsoever:**
- Windows Cache Manager may flush pages in any order
- Linux writeback may flush pages in any order
- NVMe controller may reorder commands within the SQ
- FTL may program NAND pages in any order

After a crash, the disk may contain: **any subset** of the transaction's pages, in **any combination** of old/new versions, with **possible torn pages** within individual writes.

---

## 11. Platform Comparison Summary

### FileOptions to OS Flags

| .NET FileOptions | Windows Flag | Linux Equivalent | Notes |
|-----------------|-------------|-----------------|-------|
| `Asynchronous` | `FILE_FLAG_OVERLAPPED` | *nothing* (ThreadPool) | Windows: true async. Linux: fake async. |
| `RandomAccess` | `FILE_FLAG_RANDOM_ACCESS` | `POSIX_FADV_RANDOM` | Both: advisory hints only |
| `WriteThrough` | `FILE_FLAG_WRITE_THROUGH` | `O_SYNC` | Linux is stricter (data+metadata vs just FUA) |
| `SequentialScan` | `FILE_FLAG_SEQUENTIAL_SCAN` | `POSIX_FADV_SEQUENTIAL` | Both: read-ahead hints |
| *(no .NET API)* | `FILE_FLAG_NO_BUFFERING` | `O_DIRECT` | Must P/Invoke on both platforms |

### Flush Mechanisms

| Operation | Windows | Linux | Performance |
|-----------|---------|-------|-------------|
| Flush data+metadata to stable storage | `FlushFileBuffers(handle)` | `fsync(fd)` | Expensive: flushes entire disk cache |
| Flush data only | *(no separate API)* | `fdatasync(fd)` | Cheaper: skips metadata if unchanged |
| Per-write durability (data) | `FILE_FLAG_WRITE_THROUGH` | `O_DSYNC` | Per-write cost, uses FUA if supported |
| Per-write durability (data+metadata) | *(same as above)* | `O_SYNC` | Strictest, used by .NET WriteThrough |
| .NET API | `RandomAccess.FlushToDisk(handle)` | Same (.NET 8+) | Calls FlushFileBuffers/fsync |

### Key Performance Differences

| Aspect | Windows | Linux |
|--------|---------|-------|
| Async file write | True async (IOCP), no thread blocked | ThreadPool dispatch, thread blocks |
| Best WAL strategy | `NO_BUFFERING + WRITE_THROUGH` | `O_DIRECT + O_DSYNC` |
| fsync granularity | Entire disk cache flushed | Entire disk cache flushed |
| FUA support for WAL | Yes (StorNVMe) | Yes (nvme driver, REQ_FUA) |
| XFS FUA optimization | N/A | O_DIRECT+O_DSYNC overwrites use FUA directly |
| Atomic writes (hardware) | Not exposed to applications | `RWF_ATOMIC` (kernel 6.11+, XFS/ext4) |
| File system recommendations | NTFS (only real option) | **XFS** (better fsync, FUA optimization, atomic writes) |

### .NET API Gaps on Linux

| Need | .NET Provides | Optimal Linux API | Gap |
|------|--------------|-------------------|-----|
| Data-only sync | `FlushToDisk` → `fsync()` | `fdatasync()` | Must P/Invoke |
| Direct I/O | Nothing | `O_DIRECT` | Must P/Invoke `open()` |
| Per-write data sync | `WriteThrough` → `O_SYNC` | `O_DSYNC` | .NET uses stricter O_SYNC |
| Atomic writes | Nothing | `RWF_ATOMIC` + `pwritev2()` | Must P/Invoke |
| True async file I/O | ThreadPool (slow) | `io_uring` | Not in .NET runtime |

---

## 12. Durability Options & Trade-offs for Typhon

### Option A: Current State (No Durability) — Both Platforms

| Metric | Value |
|--------|-------|
| Write latency | ~1-5 μs (memcpy to page cache) |
| Durability | None |
| Process crash | Data survives (in OS cache) |
| OS crash | Data lost (1-30 seconds of writes) |
| Power loss | Data lost |

### Option B: Periodic FlushFileBuffers/fsync (Group Commit)

```
Write path: WriteAsync -> OS cache (fast) -> periodic flush
```

| Metric | Windows | Linux |
|--------|---------|-------|
| Write latency | ~1-5 μs | ~1-5 μs |
| Flush cost | `FlushFileBuffers`: 50-200 μs | `fdatasync`: 50-200 μs |
| Durability window | Flush interval (configurable) | Same |
| Best practice | P/Invoke `FlushFileBuffers` | P/Invoke `fdatasync` (skip metadata) |

### Option C: WriteThrough / O_DSYNC (Per-Write Durability)

| Metric | Windows | Linux |
|--------|---------|-------|
| File open | `FileOptions.WriteThrough` | P/Invoke with `O_DSYNC` (not `O_SYNC`) |
| Write latency | ~20-100 μs (NVMe FUA) | ~20-100 μs (NVMe FUA via XFS optimization) |
| Durability | Each write durable on completion | Same |
| Overhead | Every write hits disk | Same |

### Option D: Direct I/O + Per-Write Durability (Best for WAL)

| Metric | Windows | Linux |
|--------|---------|-------|
| File open | `NO_BUFFERING + WRITE_THROUGH` | `O_DIRECT + O_DSYNC` |
| Write latency | ~10-80 μs (bypass cache, FUA) | ~10-80 μs (same) |
| Buffer alignment | 4KB (physical sector size) | Logical block size (512B or 4KB) |
| .NET API | P/Invoke for NO_BUFFERING | P/Invoke for O_DIRECT + O_DSYNC |
| Read cache benefit | None (bypasses cache) | None |

**Typhon compatibility:** 8KB pages are already aligned and sized correctly. Buffer memory may need `NativeMemory.AlignedAlloc(size, 4096)` instead of GCHandle-pinned byte[].

### Option E: Dual-Path WAL Architecture (Recommended)

```
                    Commit Path (DURABLE)
                    =====================
Transaction.Commit()
  -> Serialize changes to WAL buffer (aligned, 4KB minimum)
  -> Write to WAL file:
       Windows: RandomAccess.WriteAsync (NO_BUFFERING + WRITE_THROUGH)
       Linux:   pwrite() on O_DIRECT + O_DSYNC fd
  -> On completion: Commit is DURABLE, return success to caller

                    Background Path (FAST)
                    =====================
Checkpoint Timer (every N seconds or N dirty pages):
  -> Write dirty data pages via RandomAccess.WriteAsync (buffered, fast)
  -> Flush:
       Windows: FlushFileBuffers(dataFileHandle)
       Linux:   fdatasync(dataFileFd)
  -> Advance WAL checkpoint marker
  -> Truncate/recycle old WAL entries

                    Recovery Path
                    =============
On startup:
  -> Read WAL from last checkpoint
  -> Replay committed transactions after checkpoint
  -> Verify page checksums (detect torn pages)
  -> Re-apply torn pages from WAL
  -> Database is consistent
```

### Option F: Linux-Specific — RWF_ATOMIC for Torn-Write-Free Pages (Future)

```
On Linux with kernel 6.11+ and AWUPF >= 8KB:
  -> pwritev2(fd, &iov, 1, offset, RWF_ATOMIC)  // O_DIRECT required
  -> Guarantees 8KB page is NEVER torn on power failure
  -> Eliminates need for double-write buffer or full-page WAL images
  -> Still needs fsync/FUA for durability (atomicity ≠ durability)
```

### Performance Expectations

| Operation | Expected Latency | Notes |
|-----------|-----------------|-------|
| Buffered page write | 1-5 μs | memcpy to OS cache (both platforms) |
| WAL write (FUA, O_DIRECT) | 10-80 μs | NVMe FUA, varies by drive |
| WAL write (group commit, amortized) | 2-20 μs | Batch multiple commits per flush |
| FlushFileBuffers / fsync | 50-500 μs | Flushes entire disk cache |
| fdatasync (Linux, data only) | 40-400 μs | Slightly cheaper than fsync |
| Checkpoint (100 dirty pages) | 200-1000 μs | Batch write + single flush |

---

## 13. Key Findings & Recommendations

### Critical Facts

1. **`RandomAccess.WriteAsync` completion = data in volatile RAM only.** No durability on either platform.

2. **There is no write ordering** between pages at any layer. A crash can leave any subset of a transaction's pages on disk.

3. **8KB pages CAN be torn** on consumer NVMe drives (AWUPF = 1 LBA = 4KB). Torn page detection (CRC32C checksums in page headers) is mandatory.

4. **On Linux, .NET async file I/O is fake** — it blocks a ThreadPool thread. For maximum throughput, consider dedicated I/O threads or future io_uring integration.

5. **`FileOptions.WriteThrough` maps to `O_SYNC` on Linux** (not `O_DSYNC`). For WAL writes, P/Invoke `open()` with `O_DSYNC` to avoid unnecessary metadata writes.

6. **XFS is the recommended Linux file system** for Typhon:
   - `O_DIRECT + O_DSYNC` overwrites use FUA directly (no full cache flush)
   - Better fsync behavior (less inter-file coupling than ext4)
   - First filesystem to support multi-block `RWF_ATOMIC` (kernel 6.16+)

7. **`fdatasync()` is preferable to `fsync()` on Linux** when file size doesn't change (overwrites). Saves one metadata journal commit.

8. **Enterprise NVMe SSDs with PLP** protect against power-loss data loss even without FUA/flush. Consumer SSDs provide no such guarantee.

### Recommended Typhon Architecture (Cross-Platform)

```csharp
// === Platform abstraction for durable I/O ===

#if WINDOWS
// WAL: Direct I/O + Write-Through (FUA per write)
const FileOptions NoBuffering = (FileOptions)0x20000000;
var walHandle = File.OpenHandle(walPath, FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None,
    FileOptions.Asynchronous | NoBuffering | FileOptions.WriteThrough);

// Data: Buffered (fast writes, periodic flush)
var dataHandle = File.OpenHandle(dataPath, FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None,
    FileOptions.Asynchronous | FileOptions.RandomAccess);

// Checkpoint flush
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool FlushFileBuffers(SafeFileHandle hFile);

#elif LINUX
// WAL: O_DIRECT + O_DSYNC (FUA per write, data-only sync)
const int O_RDWR = 2, O_CREAT = 64, O_DSYNC = 4096, O_DIRECT = 0x4000;
int walFd = open(walPath, O_RDWR | O_CREAT | O_DIRECT | O_DSYNC, 0644);
var walHandle = new SafeFileHandle((IntPtr)walFd, ownsHandle: true);

// Data: Buffered (fast writes, periodic flush)
var dataHandle = File.OpenHandle(dataPath, FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None,
    FileOptions.Asynchronous | FileOptions.RandomAccess);

// Checkpoint flush (data-only, cheaper than fsync)
[LibraryImport("libc", SetLastError = true)]
static partial int fdatasync(SafeFileHandle fd);
#endif
```

### Platform-Specific Recommendations

| Concern | Windows | Linux |
|---------|---------|-------|
| WAL file flags | `NO_BUFFERING + WRITE_THROUGH` | `O_DIRECT + O_DSYNC` |
| Data file flags | `Asynchronous + RandomAccess` | Same |
| Checkpoint flush | `FlushFileBuffers()` | `fdatasync()` |
| Buffer alignment | 4KB (physical sector) | 4KB (XFS block size) |
| File system | NTFS | XFS (recommended) |
| Torn-write detection | Page checksums (always) | Page checksums (or RWF_ATOMIC on 6.11+) |
| Async strategy | IOCP (native) | ThreadPool (or io_uring in future) |
| I/O scheduler | N/A | `none` for NVMe devices |
| Query AWUPF | `IOCTL_STORAGE_QUERY_PROPERTY` | `statx(STATX_WRITE_ATOMIC)` |

---

## Appendix A: Comparison with Other Database Engines

| Database | Platform | Data Writes | WAL Writes | Flush | Torn Page Protection |
|----------|----------|-------------|------------|-------|---------------------|
| **PostgreSQL** | Linux | Buffered (shared_buffers) | `O_DIRECT` + `fdatasync` | Periodic checkpoint | Full-page images in WAL |
| **SQLite (WAL)** | Both | Buffered | `O_DSYNC` or `fdatasync` | WAL flush on commit | Checksums in WAL frames |
| **SQL Server** | Windows | `NO_BUFFERING + WRITE_THROUGH` | Same | Per-write (no explicit flush) | Page checksums + WAL |
| **InnoDB** | Linux | Buffered (buffer pool) | `O_DIRECT` + `fsync` | Checkpoint + doublewrite | Doublewrite buffer |
| **RocksDB** | Linux | Buffered | `O_DIRECT` + `fdatasync` | WAL sync on commit | Checksums per block |
| **LMDB** | Both | `mmap` + `msync` | No WAL (COW B-tree) | `msync` on commit | COW (never overwrites) |
| **Typhon (current)** | Both | Buffered | N/A | **None** | **None** |
| **Typhon (proposed)** | Both | Buffered | `O_DIRECT+O_DSYNC` / `NO_BUF+WT` | Checkpoint + fdatasync/FFB | Page CRC32C + WAL |

---

## Appendix B: Linux Kernel Tuning for Database Workloads

### Writeback Parameters

```bash
# Reduce dirty page expiry (default 30s is too long for database durability windows)
echo 500 > /proc/sys/vm/dirty_expire_centisecs      # 5 seconds

# Reduce writeback interval (default 5s)
echo 100 > /proc/sys/vm/dirty_writeback_centisecs   # 1 second

# Lower dirty thresholds to reduce data-at-risk window
echo 5 > /proc/sys/vm/dirty_background_ratio        # Start writeback at 5% dirty
echo 10 > /proc/sys/vm/dirty_ratio                  # Throttle at 10% dirty
```

### NVMe I/O Scheduler

```bash
# Use "none" for NVMe (no scheduling overhead)
echo none > /sys/block/nvme0n1/queue/scheduler

# Verify
cat /sys/block/nvme0n1/queue/scheduler
# [none] mq-deadline kyber
```

### Querying Atomic Write Support

```bash
# Check NVMe atomic write capabilities
nvme id-ns /dev/nvme0n1 -H | grep -i atomic
# NAWUPF: 0 (= 1 LBA atomic)
# AWUPF:  0 (= 1 LBA atomic)

# Check via statx (kernel 6.11+)
python3 -c "
import os
st = os.stat('/path/to/file')
# Use statx for STATX_WRITE_ATOMIC fields (requires ctypes/cffi)
"
```

### Filesystem Mount Options for XFS

```bash
# Recommended XFS mount for database workloads:
mount -t xfs -o noatime,nodiratime,logbufs=8,logbsize=256k /dev/nvme0n1p1 /data

# noatime: Don't update access times (reduces metadata writes)
# logbufs=8: More log buffers for concurrent transactions
# logbsize=256k: Larger log buffer (reduces log I/O frequency)
```

---

## Appendix C: .NET P/Invoke Reference (Cross-Platform)

### Windows: FlushFileBuffers

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool FlushFileBuffers(SafeFileHandle hFile);
```

### Linux: fdatasync

```csharp
[LibraryImport("libc", SetLastError = true)]
private static partial int fdatasync(SafeFileHandle fd);
// Note: SafeFileHandle marshals to int fd on Unix automatically
```

### Linux: Open with O_DIRECT + O_DSYNC

```csharp
[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
private static partial int open(string pathname, int flags, int mode);

[LibraryImport("libc", SetLastError = true)]
private static partial int close(int fd);

// x86_64 Linux flag values (CAUTION: arch-dependent!)
const int O_RDWR    = 0x0002;
const int O_CREAT   = 0x0040;
const int O_DIRECT  = 0x4000;    // x86_64
const int O_DSYNC   = 0x1000;    // x86_64

public static SafeFileHandle OpenDirectDsync(string path)
{
    int fd = open(path, O_RDWR | O_CREAT | O_DIRECT | O_DSYNC, 0644);
    if (fd < 0) throw new IOException($"open failed: {Marshal.GetLastPInvokeError()}");
    return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
}
```

### Aligned Buffer Allocation (Both Platforms)

```csharp
using System.Runtime.InteropServices;

// Allocate 4KB-aligned buffer for O_DIRECT / NO_BUFFERING
const nuint Alignment = 4096;
nuint size = 8192; // 8KB page
unsafe
{
    void* ptr = NativeMemory.AlignedAlloc(size, Alignment);
    // Use: new Span<byte>(ptr, (int)size)
    // Free: NativeMemory.AlignedFree(ptr);
}
```

### Linux: RWF_ATOMIC (Future, Kernel 6.11+)

```csharp
// Not yet in .NET — requires raw syscall P/Invoke
[LibraryImport("libc", SetLastError = true)]
private static partial long pwritev2(int fd, /* iovec* */ IntPtr iov, int iovcnt,
    long offset, int flags);

const int RWF_ATOMIC = 0x00000040; // Kernel 6.11+

// Usage: pwritev2(fd, &iov, 1, offset, RWF_ATOMIC);
// Requires: O_DIRECT, power-of-2 size, naturally aligned offset,
//           statx confirms atomic_write_unit_max >= write size
```

---

## Appendix D: Glossary

| Term | Definition |
|------|-----------|
| **AWUPF** | Atomic Write Unit Power Fail — max LBAs guaranteed atomic on power loss |
| **BIO** | Block I/O — Linux kernel block layer request structure |
| **blk-mq** | Multi-queue block layer — Linux's modern block I/O architecture |
| **CDB** | Command Descriptor Block — SCSI command structure |
| **CQ/SQ** | Completion/Submission Queue — NVMe command interface |
| **fdatasync** | Linux: flush file data to disk (skip metadata if unchanged) |
| **FTL** | Flash Translation Layer — logical-to-physical mapping in SSD |
| **FUA** | Force Unit Access — bypass volatile cache, write to stable media |
| **IOCP** | I/O Completion Port — Windows async I/O mechanism |
| **io_uring** | Linux async I/O interface (not in .NET as of 2025) |
| **IRP** | I/O Request Packet — Windows kernel I/O structure |
| **LBA** | Logical Block Address — sector address as seen by OS |
| **O_DIRECT** | Linux: bypass page cache for I/O |
| **O_DSYNC** | Linux: per-write data sync (each write durable on return) |
| **O_SYNC** | Linux: per-write data+metadata sync (stricter than O_DSYNC) |
| **PLP** | Power Loss Protection — capacitors in enterprise SSDs |
| **REQ_FUA** | Linux BIO flag requesting FUA for this specific I/O |
| **RWF_ATOMIC** | Linux pwritev2 flag for atomic writes (kernel 6.11+) |
| **VACB** | Virtual Address Control Block — Windows Cache Manager 256KB view |
| **VWC** | Volatile Write Cache — SSD's DRAM write buffer |
| **WAL** | Write-Ahead Log — durability mechanism |
| **4Kn** | 4K Native — drive with 4096B physical and logical sectors |
| **512e** | 512-byte Emulated — 4096B physical, 512B logical sectors |
