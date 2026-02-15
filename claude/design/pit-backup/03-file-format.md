# Part 3: File Format Specification

> Byte-level specification of the `.pack` backup file format, the backup catalog (`catalog.dat`), page index structures, allocation bitmap, compression encoding, and CRC coverage.

**Parent:** [README.md](./README.md) | **Previous:** [02 - Backup Creation](./02-backup-creation.md) | **Next:** [04 - Reconstruction & Restore](./04-reconstruction.md)

<a href="../../assets/typhon-pitbackup-file-format.svg">
  <img src="../../assets/typhon-pitbackup-file-format.svg" width="600"
       alt="PIT Backup — .pack File Format Layout">
</a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

---

## Table of Contents

1. [Pack File Format (.pack)](#pack-file-format-pack)
2. [PackPageEntry Structure](#packpageentry-structure)
3. [Catalog Format (catalog.dat)](#catalog-format-catalogdat)
4. [Allocation Bitmap](#allocation-bitmap)
5. [Compression Details](#compression-details)
6. [CRC Coverage](#crc-coverage)
7. [Versioning](#versioning)

---

## Pack File Format (.pack)

Each `.pack` file is a self-contained backup unit containing compressed page data, a sorted page index for binary search, an optional allocation bitmap, and integrity checksums. The file is written sequentially (header, pages, bitmap, index, footer) and can be read either sequentially or via random access using the footer's index pointer.

### File Layout

```
Offset    Size       Field                    Description
-----------------------------------------------------------------
HEADER (128 bytes)
0x00      8          Magic                    "TYPHBACK" (0x54595048 4241434B)
0x08      2          FormatVersion            1
0x0A      2          Flags                    0x01=Compressed, 0x02=HasAllocationBitmap
0x0C      4          BackupId                 Monotonic backup sequence number
0x10      4          CheckpointEpoch          Epoch at backup time
0x14      8          DateTime                 UTC ticks (100ns units since epoch)
0x1C      4          TotalAllocatedPages      Total pages in DB at backup time
0x20      4          PageCount                Number of pages in THIS file
0x24      4          PageSize                 8192 (constant for now)
0x28      1          CompressionType          0=None, 1=LZ4, 2=Zstd
0x29      3          Reserved                 Zero-filled
0x2C      4          AllocBitmapOffset        Offset to allocation bitmap (0 if absent)
0x30      4          AllocBitmapSize          Size of allocation bitmap in bytes
0x34      44         Reserved                 Zero-filled for future use
0x60      4          HeaderCRC                CRC32C of bytes 0x00-0x5F
0x64      28         Padding                  Zero-filled to 128 bytes

PAGE DATA (variable)
0x80      variable   CompressedPages          Sequential compressed page frames
                                              Each frame: [compressed_size:4][data:compressed_size]
                                              Uncompressed size always = PageSize (8192)

ALLOCATION BITMAP (optional, if Flags & 0x02)
varies    variable   AllocationBitmap         1 bit per page: which pages exist in the DB
                                              Size = ceil(TotalAllocatedPages / 8)

PAGE INDEX (PageCount x 16 bytes)
varies    16 x N     PageIndex                Array of PackPageEntry structs (sorted by PageId)

FOOTER (32 bytes)
EOF-32    8          PageIndexOffset          File offset where PageIndex starts
EOF-24    4          PageCount                Redundant copy (matches header)
EOF-20    4          AllocBitmapOffset        Redundant copy (or 0)
EOF-16    8          Reserved                 Zero-filled
EOF-8     4          FooterMagic              0x5041434B ("PACK")
EOF-4     4          FileCRC                  CRC32C of entire file (bytes 0 to EOF-4)
```

### Header Details

**Magic (8 bytes):** The ASCII string `TYPHBACK` encoded as the 64-bit little-endian value `0x4B43414248505954`. This enables quick file type identification. Readers must reject files that do not start with this magic value.

**FormatVersion (2 bytes):** Version of the pack file format. Initial implementation uses version 1. Readers must reject files with a format version higher than what they support.

**Flags (2 bytes):** Bitfield controlling optional features:

| Bit | Name | Meaning |
|-----|------|---------|
| 0 (0x01) | Compressed | Page data uses the compression algorithm specified by CompressionType. If clear, all pages are stored raw. |
| 1 (0x02) | HasAllocationBitmap | The file contains an allocation bitmap section. |
| 2-15 | Reserved | Must be zero. Readers must not reject files with unknown flag bits (forward compatibility). |

**BackupId (4 bytes):** Monotonically increasing sequence number. The first backup in a chain has BackupId = 1. This ID is the primary key for referencing backups in the catalog and during reconstruction.

**CheckpointEpoch (4 bytes):** The checkpoint epoch at which this backup was anchored. All pages in this backup reflect committed state up to this epoch.

**DateTime (8 bytes):** UTC timestamp as .NET ticks (100-nanosecond intervals since 0001-01-01 00:00:00 UTC). Stored as `DateTime.UtcNow.Ticks` at backup creation time. Enables human-readable backup listing and time-based restore targets.

**TotalAllocatedPages (4 bytes):** Total number of pages in the database at backup time. This is the size of the full page address space, not the number of pages in this backup file. Used for:
- Sizing the allocation bitmap
- Validating reconstruction completeness
- Monitoring database growth over time

**PageCount (4 bytes):** Number of pages stored in this specific .pack file. For incremental backups, this is the number of changed pages. For compacted backups, this equals TotalAllocatedPages.

**PageSize (4 bytes):** Page size in bytes. Currently always 8192. Stored explicitly to enable future page size flexibility without format changes.

**CompressionType (1 byte):** Compression algorithm used for page data:

| Value | Algorithm | Notes |
|-------|-----------|-------|
| 0 | None | Pages stored raw (8192 bytes each) |
| 1 | LZ4 | Default. LZ4 frame format per page. |
| 2 | Zstd | Higher ratio, lower speed. For archival use. |
| 3-255 | Reserved | Future algorithms |

**AllocBitmapOffset (4 bytes):** File offset where the allocation bitmap begins. Zero if the file does not contain an allocation bitmap (Flags bit 1 is clear).

**AllocBitmapSize (4 bytes):** Size of the allocation bitmap section in bytes. Zero if absent. Equal to `ceil(TotalAllocatedPages / 8)` when present.

**HeaderCRC (4 bytes):** CRC32C checksum computed over bytes 0x00 through 0x5F (the first 96 bytes of the header). This enables header integrity verification before reading the rest of the file.

**Padding (28 bytes):** Zero-filled padding to bring the header to exactly 128 bytes. This ensures the PAGE DATA section starts at a well-aligned offset (0x80 = 128).

### Footer Details

The footer is always the last 32 bytes of the file. Its fixed size and position (relative to EOF) enable readers to locate the page index without scanning the entire file.

**PageIndexOffset (8 bytes):** Absolute file offset where the page index array begins. The reader seeks to this offset, reads `PageCount * 16` bytes, and deserializes the `PackPageEntry` array.

**PageCount (4 bytes):** Redundant copy of the header's PageCount. Enables quick validation: if `footer.PageCount != header.PageCount`, the file is corrupt.

**AllocBitmapOffset (4 bytes):** Redundant copy of the header's AllocBitmapOffset. Enables locating the allocation bitmap from the footer alone (useful when reading backward from EOF).

**FooterMagic (4 bytes):** The ASCII string `PACK` encoded as `0x4B434150` (little-endian). Enables quick detection of a valid footer when reading backward from EOF.

**FileCRC (4 bytes):** CRC32C checksum of the entire file from byte 0 to EOF-4 (everything except the CRC field itself). This is the strongest integrity check: it covers the header, all page data, the allocation bitmap, the page index, and the footer fields preceding it.

---

## PackPageEntry Structure

Each entry in the page index describes one page stored in the .pack file. The index is sorted by `PageId` to enable O(log N) binary search during reconstruction.

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct PackPageEntry  // 16 bytes
{
    public int PageId;           // 4B - File page index
    public int ChangeRevision;   // 4B - ChangeRevision at capture time
    public int DataOffset;       // 4B - Offset into PAGE DATA section (relative to 0x80)
    public int CompressedSize;   // 4B - Compressed size in bytes (0 = uncompressed/raw)
}
```

### Field Details

**PageId (4 bytes):** The file page index of this page within the database. This is the page's permanent identity and is used as the lookup key during reconstruction. Range: 0 to TotalAllocatedPages - 1.

**ChangeRevision (4 bytes):** The value of `PageBaseHeader.ChangeRevision` at the time this page was captured. This serves two purposes:
- **Reconstruction ordering:** When multiple .pack files contain the same PageId, the one with the highest ChangeRevision is the most recent version.
- **Verification:** After restoration, the restored page's ChangeRevision should match the value stored in the index entry.

**DataOffset (4 bytes):** Offset of this page's compressed data within the PAGE DATA section, relative to the section start at 0x80. The absolute file offset is `0x80 + DataOffset`. This relative addressing keeps offsets small (4 bytes sufficient for files up to 4 GB of page data).

**CompressedSize (4 bytes):** Size of the compressed page data in bytes. Special value:
- `0` indicates the page is stored uncompressed (raw 8192 bytes). This happens when compression does not reduce the page size (compressed output >= PageSize).
- Any positive value indicates LZ4 or Zstd compressed data of that size. The decompressed output is always exactly PageSize (8192) bytes.

### Sorting and Search

The page index array is sorted by `PageId` in ascending order. This enables binary search during reconstruction:

```csharp
// Find a page in the index (O(log N))
int FindPage(PackPageEntry* index, int count, int targetPageId)
{
    int lo = 0, hi = count - 1;
    while (lo <= hi)
    {
        int mid = lo + (hi - lo) / 2;
        if (index[mid].PageId == targetPageId)
            return mid;
        if (index[mid].PageId < targetPageId)
            lo = mid + 1;
        else
            hi = mid - 1;
    }
    return -1;  // not found in this .pack file
}
```

### Size Impact

The page index is compact: 16 bytes per page. For reference:

| Changed Pages | Index Size | % of Page Data (50% compression) |
|---------------|-----------|----------------------------------|
| 12,800        | 200 KB    | 0.4%                             |
| 128,000       | 2 MB      | 0.4%                             |
| 1,280,000     | 20 MB     | 0.4%                             |
| 12,800,000    | 200 MB    | 0.4%                             |

The index overhead is consistently ~0.4% of the compressed page data -- negligible.

---

## Catalog Format (catalog.dat)

The catalog is an append-only file that tracks all backup points in a chain. It is the index of indexes: rather than opening every .pack file to enumerate backup points, the catalog provides a single, compact listing.

### File Layout

```
Offset    Size       Field                    Description
-----------------------------------------------------------------
CATALOG HEADER (64 bytes)
0x00      8          Magic                    "TYPHCATL" (0x5459504843 41544C)
0x08      2          FormatVersion            1
0x0A      2          EntryCount               Number of backup entries
0x0C      4          LastBackupId             Highest BackupId in catalog
0x10      48         Reserved                 Zero-filled
0x3C      4          HeaderCRC                CRC32C of bytes 0x00-0x3B

ENTRIES (EntryCount x 128 bytes each)
Per entry:
0x00      4          BackupId                 Monotonic sequence number
0x04      4          CheckpointEpoch          Checkpoint epoch captured
0x08      8          DateTime                 UTC ticks
0x10      4          TotalAllocatedPages      DB page count at this point
0x14      4          PageCount                Pages in this backup's .pack file
0x18      8          FileSize                 Size of .pack file in bytes
0x20      4          Flags                    0x01=Compacted (full/compacted backup)
0x24      4          PreviousBackupId         BackupId of prior backup in chain (0 for first)
0x28      4          ChainBaseId              BackupId of chain base (oldest needed for full restore)
0x2C      4          EntryCRC                 CRC32C of this entry (bytes 0x00-0x2B)
0x30      80         FileName                 UTF-8 file name, null-terminated, max 79 chars
```

### Header Details

**Magic (8 bytes):** The ASCII string `TYPHCATL` encoded as `0x4C54414348505954` (little-endian). Identifies this file as a Typhon backup catalog.

**FormatVersion (2 bytes):** Catalog format version. Initial implementation uses version 1.

**EntryCount (2 bytes):** Number of catalog entries following the header. Maximum 65,535 entries per catalog file. For backup intervals of 4 hours, this supports ~30 years of backups before the catalog needs rotation.

**LastBackupId (4 bytes):** The highest BackupId currently in the catalog. Used to generate the next BackupId (`LastBackupId + 1`) without scanning all entries. Updated atomically when appending a new entry.

**HeaderCRC (4 bytes):** CRC32C of bytes 0x00 through 0x3B. Enables header integrity verification.

### Entry Details

Each catalog entry is a fixed 128 bytes, enabling direct indexed access: `entry[i]` is at file offset `64 + i * 128`.

**BackupId (4 bytes):** The unique, monotonically increasing identifier for this backup point. BackupId = 1 is the first backup. Compacted backups receive a new BackupId (they are new entries, not modifications of existing ones).

**CheckpointEpoch (4 bytes):** The checkpoint epoch at which this backup was anchored. Matches the header's CheckpointEpoch in the corresponding .pack file.

**DateTime (8 bytes):** UTC timestamp of backup creation. Same encoding as the .pack header.

**TotalAllocatedPages (4 bytes):** Total pages in the database at backup time. Tracks database growth across the backup chain.

**PageCount (4 bytes):** Number of pages stored in this backup's .pack file. For incremental backups, this is the changed page count. For compacted backups, this is the full database page count.

**FileSize (8 bytes):** Size of the .pack file in bytes. Enables quick storage usage reporting without stating each file.

**Flags (4 bytes):** Bitfield for entry-level metadata:

| Bit | Name | Meaning |
|-----|------|---------|
| 0 (0x01) | Compacted | This is a compacted (full) backup, not an incremental. It contains ALL pages. |
| 1 (0x02) | Partial | Backup completed with errors; some pages may be missing. |
| 2-31 | Reserved | Must be zero. |

**PreviousBackupId (4 bytes):** The BackupId of the immediately preceding backup in the chain. Value 0 means this is the first backup in the chain (or a compacted backup that starts a new chain). Used for chain validation: walking backward from the latest entry via PreviousBackupId should reach a compacted backup or the first backup.

**ChainBaseId (4 bytes):** The BackupId of the oldest backup needed for a full restore to this point. For a compacted backup, `ChainBaseId == BackupId` (it is self-sufficient). For an incremental backup, `ChainBaseId` points to the most recent compacted backup (or the very first backup if no compaction has occurred). This enables quick answering of "which files do I need to restore to point X?"

**EntryCRC (4 bytes):** CRC32C of this entry's bytes 0x00 through 0x2B (the 44 bytes preceding the CRC field). Enables per-entry integrity verification without reading the entire catalog.

**FileName (80 bytes):** The .pack file name as a null-terminated UTF-8 string. Maximum 79 characters plus null terminator. Typical format: `backup-0001.pack`. The file name is relative to the backup directory (no path component). This field enables the catalog to reference .pack files without imposing a naming convention on the backup manager.

### Append Protocol

Adding a new entry to the catalog is a 3-step process:

1. **Compute** the new entry's fields, including `EntryCRC`
2. **Append** the 128-byte entry at offset `64 + EntryCount * 128`
3. **Update** the header: increment `EntryCount`, set `LastBackupId`, recompute `HeaderCRC`

**Crash safety:** If a crash occurs between steps 2 and 3, the entry is written but the header's `EntryCount` is stale. On recovery, the catalog reader can detect this by checking if there is a valid entry (with correct `EntryCRC`) at the offset implied by `EntryCount`. If so, the header is repaired. If not, the orphaned bytes are ignored.

---

## Allocation Bitmap

The allocation bitmap records which pages exist (are allocated) in the database at backup time. This is distinct from the dirty bitmap (which tracks changed pages).

### Purpose

The allocation bitmap is essential for **reconstruction correctness**. The page index in each .pack file tells you which pages changed, but it does not tell you which pages exist. Without the allocation bitmap, the reconstruction engine cannot distinguish between:

- A page that was never allocated (should not exist in the restored database)
- A page that exists but was not modified since the last backup (should be carried forward from an earlier backup)

### When It Is Included

| Backup Type | Allocation Bitmap | Rationale |
|-------------|------------------|-----------|
| First backup | MUST include | No prior backup to reference; bitmap defines the initial page set |
| Compacted backup | MUST include | Starts a new chain; must be self-describing |
| Incremental backup | SHOULD include | Small cost (1.6 MB for 100 GB DB); enables independent verification |

Including the allocation bitmap in every backup (including incrementals) is recommended because:
- The cost is negligible: `ceil(TotalAllocatedPages / 8)` bytes, which is 0.002% of the database size
- It enables standalone verification of any .pack file without referencing the chain
- It makes the reconstruction engine simpler: each backup declares the full page set, and the page index declares the changes

### Size

| Database Size | Total Pages | Allocation Bitmap Size |
|---------------|-------------|----------------------|
| 1 GB          | 131,072     | 16 KB                |
| 10 GB         | 1,310,720   | 160 KB               |
| 100 GB        | 13,107,200  | 1.6 MB               |
| 1 TB          | 134,217,728 | 16 MB                |

Formula: `ceil(TotalAllocatedPages / 8)` bytes.

### Encoding

The allocation bitmap is a dense bit array where bit `i` is set if page `i` is allocated in the database. Bit ordering is LSB-first within each byte (bit 0 of byte 0 = page 0, bit 7 of byte 0 = page 7, bit 0 of byte 1 = page 8, etc.).

```
Byte 0:  [page7][page6][page5][page4][page3][page2][page1][page0]
Byte 1:  [page15][page14][page13][page12][page11][page10][page9][page8]
...
```

### Relationship to Dirty Bitmap

| Property | Dirty Bitmap | Allocation Bitmap |
|----------|-------------|-------------------|
| What it tracks | Pages modified since last backup | Pages that exist in the database |
| Maintained by | Checkpoint pipeline | Page allocator |
| Lifetime | Cleared after each backup | Snapshot at backup time |
| Stored in | Engine memory (persisted at checkpoint) | .pack file |
| Size | Same (`ceil(totalPages / 8)`) | Same (`ceil(totalPages / 8)`) |

The dirty bitmap is always a subset of the allocation bitmap: you can only modify a page that exists.

---

## Compression Details

### Per-Page Compression

Each page is compressed independently, enabling random-access decompression. There is no cross-page compression context or dictionary. This design trades a small compression ratio reduction for the ability to decompress any single page without decompressing its neighbors.

### Frame Format

Each compressed page in the PAGE DATA section is stored as a length-prefixed frame:

```
[compressed_size: 4 bytes, little-endian][compressed_data: compressed_size bytes]
```

- `compressed_size` is the number of bytes of compressed data that follow
- The decompressed output is always exactly `PageSize` (8192) bytes
- The `compressed_size` in the frame matches the `CompressedSize` field in the corresponding `PackPageEntry` (providing redundant validation)

### Incompressible Pages

Some pages do not compress well (e.g., pages containing already-compressed data, encrypted data, or high-entropy content). When LZ4 or Zstd produces output that is >= PageSize bytes, the page is stored uncompressed:

- The 4-byte compressed_size prefix in the frame is set to `PageSize` (8192)
- The page data follows as raw, uncompressed 8192 bytes
- The `CompressedSize` field in the `PackPageEntry` is set to `0` to signal "stored raw"

This convention avoids the pathological case where compression expands the data. The reader checks `PackPageEntry.CompressedSize`:
- If `CompressedSize > 0`: decompress `CompressedSize` bytes using the configured algorithm
- If `CompressedSize == 0`: read `PageSize` raw bytes (no decompression needed)

### Algorithm Characteristics

| Algorithm | Compression Ratio (typical for DB pages) | Compression Speed | Decompression Speed | Use Case |
|-----------|-------------------------------------------|-------------------|---------------------|----------|
| LZ4       | 40-60% of original                        | ~2 GB/s           | ~4 GB/s             | Default. Best for real-time backup with minimal latency impact. |
| Zstd      | 30-50% of original                        | ~500 MB/s         | ~1.5 GB/s           | Archival. Better ratio when backup window duration is not critical. |
| None      | 100% (no compression)                     | N/A               | N/A                 | Debugging, benchmarking, or when storage is not a concern. |

### Compression Ratio by Page Content Type

Database pages exhibit different compression characteristics depending on their content:

| Page Type | Typical Compression | Reason |
|-----------|-------------------|--------|
| B+Tree leaf (data) | 40-55% | Repetitive field structures, many null/zero regions |
| B+Tree internal | 50-65% | Dense key arrays with sequential patterns |
| Free/unused pages | 5-10% | Mostly zeros, compress extremely well |
| Component table pages | 45-60% | Structured data with repetitive schemas |
| Allocation bitmap pages | 20-40% | Sparse bit patterns compress well |

---

## CRC Coverage

Typhon's backup system uses CRC32C checksums at multiple granularities to detect corruption at different levels. The hardware CRC32C instruction (SSE 4.2 / ARMv8 CRC) provides ~8 GB/s throughput, making checksumming effectively free relative to I/O.

### CRC Summary Table

| CRC Field | Location | Covers | Purpose |
|-----------|----------|--------|---------|
| HeaderCRC | .pack header, offset 0x60 | Bytes 0x00-0x5F of header | Detect header corruption before reading page data |
| EntryCRC | catalog entry, offset 0x2C | Each catalog entry bytes 0x00-0x2B | Detect per-entry catalog corruption |
| FooterMagic | .pack footer, offset EOF-8 | N/A (magic value) | Quick footer detection when reading backward from EOF |
| FileCRC | .pack footer, offset EOF-4 | Entire .pack file bytes 0 to EOF-4 | Detect any file-level corruption (strongest check) |
| Page CRC | In `PageBaseHeader` within page data | Individual page content | Detect page-level corruption (computed during checkpoint via seqlock) |
| Catalog HeaderCRC | catalog header, offset 0x3C | Bytes 0x00-0x3B of catalog header | Detect catalog header corruption |

### Verification Hierarchy

The CRC checks form a verification hierarchy, from fast/coarse to slow/thorough:

1. **Header check (instant):** Read 128 bytes, verify HeaderCRC. Rejects corrupt or non-Typhon files immediately.
2. **Footer check (instant):** Seek to EOF-32, verify FooterMagic. Confirms the file was fully written (not truncated).
3. **Index check (fast):** Read page index via footer's PageIndexOffset, spot-check a few entries against page data.
4. **Full file check (thorough):** Compute CRC32C over the entire file (0 to EOF-4), compare against FileCRC. Detects any bit-level corruption anywhere in the file.
5. **Page-level check (per-page):** Decompress each page, verify the embedded CRC in `PageBaseHeader`. Detects corruption within individual pages, including corruption that survived file-level checks (e.g., if the page was corrupted before backup).

### CRC Algorithm

All CRC fields use **CRC32C** (Castagnoli), not CRC32 (IEEE 802.3). CRC32C is:
- Hardware-accelerated on x86 (SSE 4.2, `_mm_crc32_u64`) and ARM (CRC extension)
- Used throughout Typhon (page checksums, WAL records)
- Better error detection than CRC32 for the same polynomial degree
- Standard in storage systems (iSCSI, Btrfs, ext4)

---

## Versioning

### Format Version Semantics

The `FormatVersion` field (2 bytes) in both .pack files and the catalog provides a clear contract between writers and readers:

- **Version 1:** Initial implementation as specified in this document
- **Future versions:** May extend the format with new fields, new compression algorithms, or structural changes

### Reader Behavior

```
if (header.FormatVersion > SUPPORTED_VERSION)
    throw new UnsupportedBackupFormatException(
        $"Pack file format version {header.FormatVersion} is not supported. " +
        $"Maximum supported version: {SUPPORTED_VERSION}. " +
        "Upgrade Typhon to read this backup.");
```

Readers MUST reject files with a FormatVersion higher than what they support. This prevents silent data corruption from misinterpreting fields that have changed meaning in a newer version.

### Backward-Compatible Extensions

New features that fit within the existing format can be added WITHOUT incrementing FormatVersion:

- **New flag bits:** The Flags field has 14 unused bits. New optional features can use these bits. Readers that do not understand a flag bit simply ignore the associated data.
- **New compression types:** The CompressionType field has 253 unused values. A new compression algorithm can be added with a new type value. Readers that do not support the algorithm reject the file with a clear error.
- **Reserved field usage:** The 44 bytes of Reserved space in the header and 48 bytes in the catalog header can be assigned meaning. Readers that do not understand these fields read zeros (harmless).

### Breaking Changes

Changes that alter the meaning of existing fields, change the layout of structures, or redefine the page index format MUST increment FormatVersion. Examples:

- Changing `PackPageEntry` from 16 bytes to a different size
- Changing the footer position or layout
- Altering the CRC computation scope
- Changing the page data frame format

### Migration Path

When FormatVersion is incremented, the recommended approach is:

1. The new writer always writes the new version
2. The new reader supports both old and new versions
3. The `typhon-backup compact` command produces files in the new format, effectively migrating the chain
4. Old .pack files remain readable until pruned

---

## Related Documents

- [01 - Architecture](./01-architecture.md) -- Core principles and system integration
- [02 - Backup Creation](./02-backup-creation.md) -- Step-by-step backup flow that produces .pack files
- [04 - Reconstruction & Restore](./04-reconstruction.md) -- How .pack files are read during restoration
- [06-durability.md](../../overview/06-durability.md) -- PageBaseHeader, ChangeRevision, CRC32C usage
- [03-storage.md](../../overview/03-storage.md) -- Page layout, PageSize, file page indexing
