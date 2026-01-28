# Component 11: Shared Utilities

> Common infrastructure including type utilities, memory allocators, concurrent collections, hashing, and cross-cutting concerns.

---

## Overview

Shared Utilities contains the foundational code used across all other components. This component is organized into **categories** for progressive deep-dive: from simple type utilities to complex memory allocators and concurrency primitives.

<a href="../assets/typhon-utilities-overview.svg">
  <img src="../assets/typhon-utilities-overview.svg" width="800"
       alt="Shared Utilities — Dependency map showing consumer components and utility categories A through F">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-utilities-overview.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

---

## Status: ⚠️ Partial

Core utilities exist but organization could be improved. Some gaps identified for future implementation.

---

## Categories Overview

| Cat | Name | Purpose | Status |
|-----|------|---------|--------|
| **A** | [Type Utilities](#category-a-type-utilities) | Blittable data types for components | ✅ Complete |
| **B** | [Hashing & Formatting](#category-b-hashing--formatting) | Fast hashing, friendly output | ✅ Complete |
| **C** | [Memory Manipulation](#category-c-memory-manipulation) | Span casting and streaming | ✅ Complete |
| **D** | [Concurrent Collections](#category-d-concurrent-collections) | Thread-safe data structures | ✅ Complete |
| **E** | [Synchronization](#category-e-synchronization) | Locks and waiting primitives | ✅ Complete |
| **F** | [Memory Allocators](#category-f-memory-allocators) | Pinned memory for unsafe code | ✅ Complete |
| ~~G~~ | ~~Telemetry~~ | ~~Logging enrichers~~ | ⚰️ Dead code |
| **P** | [Proposed Additions](#category-p-proposed-additions) | Future utilities | 📋 Planned |

---

# Category A: Type Utilities

Blittable data types that can be stored directly in components without managed references.

## A.1 String64 / String1024

### Purpose

Fixed-size UTF-8 string types for component fields. Enables blittable structs while supporting text data.

### Design

```csharp
[StructLayout(LayoutKind.Sequential)]
public unsafe struct String64 : IComparable<String64>, IEquatable<String64>
{
    private fixed byte _data[64];

    // Construction
    public String64(byte* stringAddr, int length = 64);
    public static implicit operator String64(string str);

    // Access
    public string AsString { get; set; }
    public Span<byte> AsSpan();
    public ReadOnlySpan<byte> AsReadOnlySpan();

    // Comparison (uses MurmurHash2)
    public int CompareTo(String64 other);
    public bool Equals(String64 other);
    public override int GetHashCode();
}
```

| Variant | Size | Max Chars (UTF-8) | Use Case |
|---------|------|-------------------|----------|
| **String64** | 64 bytes | ~63 | Names, identifiers |
| **String1024** | 1024 bytes | ~1023 | Descriptions, paths |

### Why Fixed Size?

| Aspect | String64 | Regular String |
|--------|----------|----------------|
| **Memory layout** | Inline in struct | Heap-allocated reference |
| **Blittable** | ✅ Yes | ❌ No |
| **GC pressure** | None | Per-string allocation |
| **Component storage** | Direct | Requires separate segment |

### Code Location

`src/Typhon.Engine/Misc/String64.cs`

---

## A.2 Variant

### Purpose

Dynamic-typed 64-byte value that can hold any supported field type. Uses a 3-byte type prefix (`tt:`) followed by string-encoded value.

### Design

```csharp
public readonly struct Variant : IComparable<Variant>, IEquatable<Variant>
{
    private readonly String64 _text;  // Stores "tt:value" format

    // Construction
    public Variant(bool value);
    public Variant(sbyte value);
    public Variant(short value);
    public Variant(int value);
    public Variant(long value);
    public Variant(string value, bool truncate);

    // Type checking
    public FieldType FieldType { get; }

    // Type-safe access
    public bool AsBool();
    public int AsInt();
    public long AsLong();
    public string AsString();

    // Explicit casts
    public static explicit operator string(Variant v);
    public static explicit operator int(Variant v);
}
```

### Type Encoding

| Type | Prefix | Example |
|------|--------|---------|
| Boolean | `bo:` | `bo:1` |
| Byte | `sb:` | `sb:-42` |
| Short | `ss:` | `ss:1234` |
| Int | `si:` | `si:12345678` |
| Long | `sl:` | `sl:9876543210` |
| String | `st:` | `st:Hello` |

### Code Location

`src/Typhon.Engine/Misc/Variant.cs`

---

## A.3 Geometric Types

### Purpose

Blittable geometric primitives for ECS game/simulation workloads.

| Type | Fields | Size |
|------|--------|------|
| **Point2F** | X, Y (float) | 8 bytes |
| **Point3F** | X, Y, Z (float) | 12 bytes |
| **Point4F** | X, Y, Z, W (float) | 16 bytes |
| **Point2D** | X, Y (double) | 16 bytes |
| **Point3D** | X, Y, Z (double) | 24 bytes |
| **Point4D** | X, Y, Z, W (double) | 32 bytes |
| **QuaternionF** | X, Y, Z, W (float) | 16 bytes |
| **QuaternionD** | X, Y, Z, W (double) | 32 bytes |

### Code Location

`src/Typhon.Engine/Misc/String64.cs` (co-located with String64)

---

# Category B: Hashing & Formatting

Fast hashing and human-readable formatting utilities.

## B.1 MurmurHash2

### Purpose

Fast non-cryptographic hash function for internal use (hash tables, String64 hash codes).

### Design

```csharp
public unsafe static class MurmurHash2
{
    public static uint Hash(byte[] data);
    public static uint Hash(ReadOnlySpan<byte> data);
    public static uint Hash(byte* dataAddr, int length, uint seed);
}
```

### Characteristics

| Property | Value |
|----------|-------|
| Output | 32-bit unsigned int |
| Default seed | `0xc58f1a7b` |
| Speed | ~3-4 GB/s on modern CPUs |
| Collision resistance | Good for hash tables, NOT for security |

### Code Location

`src/Typhon.Engine/Misc/MurmurHash2.cs`

---

## B.2 MathExtensions

### Purpose

Friendly formatting for sizes, times, and bandwidth. Common math utilities.

### Methods

```csharp
public static class MathExtensions
{
    // Friendly size formatting (1024-based)
    public static string FriendlySize(this long val);   // "1.5M", "256K"
    public static string FriendlySize(this int val);
    public static string FriendlySize(this double val); // "1.5Gb", "256Kb"

    // Friendly amount formatting (1000-based)
    public static string FriendlyAmount(this int val);  // "1.5M", "256K"
    public static string FriendlyAmount(this double val);

    // Time formatting with optional rate
    public static string FriendlyTime(this double val, bool displayRate = true);
    // Example: "5.2ms (192K/sec)"

    // Bandwidth calculation
    public static string Bandwidth(int size, double elapsed);  // "1.5Gb/sec"
    public static string Bandwidth(long size, double elapsed);

    // Power of 2 utilities
    public static bool IsPowerOf2(this int x);
    public static bool IsPowerOf2(this long x);
    public static int NextPowerOf2(this int v);

    // Tick conversions
    public static double TicksToSeconds(this long ticks);
    public static double TotalSeconds(this int ticks);
    public static double TotalSeconds(this long ticks);
}
```

### Example Output

| Input | Method | Output |
|-------|--------|--------|
| `1536` | `FriendlySize()` | `"1.5K"` |
| `1572864` | `FriendlySize()` | `"1.5M"` |
| `0.00052` | `FriendlyTime()` | `"520µs (1.923K/sec)"` |
| `0.00052` | `FriendlyTime(false)` | `"520µs"` |

### Code Location

`src/Typhon.Engine/Misc/MathExtensions.cs`

---

# Category C: Memory Manipulation

Helpers for efficient memory/span operations.

## C.1 SpanHelpers

### Purpose

Extension methods for casting and splitting spans.

### Design

```csharp
internal static class SpanHelpers
{
    // Cast span to different type
    public static Span<TTo> Cast<TFrom, TTo>(this Span<TFrom> span)
        where TFrom : struct where TTo : struct;

    public static ReadOnlySpan<TTo> Cast<TFrom, TTo>(this ReadOnlySpan<TFrom> span)
        where TFrom : struct where TTo : struct;

    // Split byte span into two typed spans
    public unsafe static void Split<TA, TB>(this Span<byte> span, out Span<TA> a, out Span<TB> b)
        where TA : unmanaged
        where TB : unmanaged;
}
```

### Code Location

`src/Typhon.Engine/Misc/SpanHelpers.cs`

---

## C.2 SpanStream

### Purpose

Ref struct for streaming reads from a byte span (zero-allocation binary reading).

### Design

```csharp
public unsafe ref struct SpanStream
{
    private Span<byte> _data;

    public SpanStream(Span<byte> data);

    public int Length { get; }

    // Pop a span of T values
    public Span<T> PopSpan<T>(int length = 1) where T : unmanaged;

    // Pop a single T by reference
    public ref T PopRef<T>() where T : unmanaged;

    // Pop a single T by value
    public T Pop<T>() where T : unmanaged;
}
```

### Usage Example

```csharp
Span<byte> buffer = ...; // Contains: int, float, long
var stream = new SpanStream(buffer);

int a = stream.Pop<int>();
float b = stream.Pop<float>();
ref long c = ref stream.PopRef<long>();
```

### Code Location

`src/Typhon.Engine/Misc/String64.cs` (co-located)

---

# Category D: Concurrent Collections

Thread-safe data structures for high-contention scenarios.

## D.1 Concurrent Bitmaps

### Purpose

Thread-safe bitmap implementations for occupancy tracking and allocation.

### Variants

| Class | Purpose | Capacity |
|-------|---------|----------|
| **ConcurrentBitmap** | Single-level | Variable |
| **BitmapL3Any** | 3-level, find any free | 262,144 bits |
| **ConcurrentBitmapL3Any** | Thread-safe L3, any free | 262,144 bits |
| **ConcurrentBitmapL3All** | L3, track when all set | 262,144 bits |

### L3 Hierarchy Design

```
Level 2 (Top):     1 long = 64 bits → 64 L1 blocks
                          ↓
Level 1 (Middle):  64 longs = 4096 bits → 4096 L0 blocks
                          ↓
Level 0 (Bottom):  4096 longs = 262,144 bits → actual items
```

### Key Operations

```csharp
public class ConcurrentBitmapL3Any
{
    // Atomically set a bit
    public bool TrySetBit(int index);

    // Atomically clear a bit
    public bool TryClearBit(int index);

    // Find and allocate first free bit
    public int Allocate();

    // Check if bit is set
    public bool IsSet(int index);
}
```

### Code Location

`src/Typhon.Engine/Collections/ConcurrentBitmap*.cs`

---

## D.2 ConcurrentArray

### Purpose

Fixed-capacity array with atomic pick/putback semantics for concurrent processing.

### Design

```csharp
internal class ConcurrentArray<T> where T : class
{
    private readonly Memory<T> _data;
    private readonly ConcurrentQueue<int> _freeList;

    public ConcurrentArray(int capacity);

    public int Count { get; }
    public int Capacity { get; }

    // Add item, returns index
    public int Add(T obj);

    // Atomically pick for exclusive processing
    public bool Pick(int index, out T result);

    // Return picked item to collection
    public void PutBack(int index, T obj);

    // Remove picked item (frees slot)
    public void Release(int index);

    // Remove item (spin-waits if picked by another thread)
    public bool Remove(int index, TimeSpan timeOut);

    public void Clear();
}
```

### Pick/PutBack Pattern

The key insight is that `Pick` uses `Interlocked.Exchange` to atomically retrieve AND null-out the slot, preventing other threads from accessing the same item:

```csharp
// Thread A                           // Thread B
Pick(5, out item) → gets item        Pick(5, out item) → gets null (returns false)
// ... process item ...
PutBack(5, item)                     // Can now Pick(5) successfully
```

### Code Location

`src/Typhon.Engine/Collections/ConcurrentArray.cs`

---

## D.3 ConcurrentCollection

### Purpose

Base class for concurrent collection implementations.

### Code Location

`src/Typhon.Engine/Collections/ConcurrentCollection.cs`

---

# Category E: Synchronization

Locks and waiting primitives for concurrent access control.

## E.1 AccessControl (Reader-Writer Lock)

### Purpose

High-performance reader-writer lock allowing multiple shared readers OR one exclusive writer.

### Variants

| Type | Size | Features |
|------|------|----------|
| **AccessControl** | 8 bytes | Full-featured RW lock with telemetry, promote/demote, waiter tracking |
| **AccessControlSmall** | 4 bytes | Compact version for high-density scenarios |
| **ResourceAccessControl** | 4 bytes | 3-mode lifecycle lock (Accessing/Modify/Destroy) |

### Design (AccessControl)

```csharp
public struct AccessControl
{
    private ulong _data;  // Packed bit fields

    // Shared access (multiple concurrent readers)
    public bool EnterSharedAccess(ref WaitContext ctx, IContentionTarget target = null);
    public void ExitSharedAccess(IContentionTarget target = null);

    // Exclusive access (single writer)
    public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);
    public void ExitExclusiveAccess(IContentionTarget target = null);
    public bool TryEnterExclusiveAccess(IContentionTarget target = null);

    // Promotion (shared → exclusive) / Demotion
    public bool TryPromoteToExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);
    public void DemoteFromExclusiveAccess(IContentionTarget target = null);

    public bool IsLockedByCurrentThread { get; }
    public void Reset();
}
```

### Bit Layout (64-bit)

```
Bits  0-7:   Shared Usage Counter (8 bits)
Bits  8-15:  Shared Waiters Counter (8 bits)
Bits 16-23:  Exclusive Waiters (8 bits)
Bits 24-31:  Promoter Waiters (8 bits)
Bits 32-51:  Operations Block Id (20 bits, telemetry only)
Bits 52-61:  Thread Id (10 bits)
Bits 62-63:  State (2 bits: Idle/Shared/Exclusive)
```

### Telemetry Mode

When built with `TELEMETRY` define, operations are logged to a chain of blocks for debugging deadlocks and contention analysis.

### Code Location

`src/Typhon.Engine/Misc/AccessControl/AccessControl.cs`

---

## E.2 AdaptiveWaiter

### Purpose

Efficient waiting strategy that spins briefly then yields, adapting to contention.

### Design

```csharp
public class AdaptiveWaiter
{
    private int _iterationCount = 1 << 16;  // Start with many spins
    private int _curCount = 0;

    // Async version
    public async Task SpinAsync(CancellationToken cancellationToken = default);

    // Sync version
    public void Spin();
}
```

### Adaptive Strategy

1. Start with `2^16` spins before yielding
2. After each yield, halve the spin count (exponential backoff)
3. Minimum spin count: 10

| Contention | Behavior |
|------------|----------|
| Low | Spins 65536 times before first yield |
| Medium | After a few yields, spins ~1000 times |
| High | After many yields, spins only 10 times |
| Single CPU | Always yields (spins waste the only core) |

### Code Location

`src/Typhon.Engine/Misc/AdaptiveWaiter.cs`

---

# Category F: Memory Allocators

Pinned memory allocators for unsafe code requiring stable addresses.

## F.1 Purpose

All allocators serve the same core goal: **allocate pinned memory** so that:
- Pointers remain valid (GC won't move the memory)
- Unsafe code can safely use raw pointers
- Interop with native code works correctly

Each allocator has a specific design for different use cases:

| Allocator | Use Case | Design Focus |
|-----------|----------|--------------|
| **BlockAllocator** | Simple fixed blocks | Minimal overhead |
| **ChainedBlockAllocator** | Growing chains | Linked-list blocks |
| **MemoryAllocator** | Large contiguous regions | Big allocations |
| **StructAllocator** | Typed struct allocation | Type safety |

---

## F.2 ChainedBlockAllocator

### Purpose

Allocates fixed-size blocks that can be chained together (linked-list style).

### Design

```csharp
public class ChainedBlockAllocator<T> : ChainedBlockAllocatorBase where T : struct
{
    public ChainedBlockAllocator(int entryCountPerPage, int? strideOverride = null);

    // Allocate new block, returns ref to data
    public ref T Allocate(out int blockId, bool rootChain);

    // Get block by ID
    public ref T Get(int blockId);

    // Navigate to next block in chain
    public ref T Next(ref T blockData);

    // Thread-safe append to chain
    public unsafe ref T SafeAppend(ref T block);

    // Enumeration
    public Enumerable GetEnumerable(int blockId);
}
```

### Block Header

Each block has a header containing:
- `NextBlockId`: Link to next block in chain
- `ChainGeneration`: For chain versioning
- `AccessControl`: Per-block locking

### Code Location

`src/Typhon.Engine/Misc/BlockAllocator/ChainedBlockAllocator.cs`

---

## F.3 MemoryAllocator

### Purpose

Allocates large contiguous pinned memory regions.

### Components

| Class | Purpose |
|-------|---------|
| **MemoryAllocator** | Main allocator interface |
| **MemoryBlockBase** | Base for memory blocks |
| **MemoryBlockArray** | Array of memory blocks |
| **PinnedMemoryBlock** | GCHandle-pinned memory block |

### Code Location

`src/Typhon.Engine/Misc/MemoryAllocator/`

---

## F.4 Other Allocators

| Class | Purpose | Location |
|-------|---------|----------|
| **BlockAllocatorBase** | Base class | `BlockAllocator/` |
| **StructAllocator** | Typed struct allocation | `BlockAllocator/` |
| **UnmanagedStructAllocator** | Unmanaged memory | `BlockAllocator/` |
| **StoreSpan** | Span-based storage | `BlockAllocator/` |

---

# Category P: Proposed Additions

Based on research of popular database engines (SQLite, RocksDB, LevelDB, LMDB, PostgreSQL, DuckDB), the following utilities would benefit Typhon:

## P.1 Checksum Utilities (CRC32c)

### Rationale

RocksDB uses CRC32c with hardware acceleration; LevelDB uses masked CRCs for stored checksums. Typhon needs data integrity verification for pages.

### Why CRC32c and not CRC32?

The "c" stands for **Castagnoli polynomial** (`0x1EDC6F41`), which is different from the standard IEEE CRC32 polynomial (`0x04C11DB7`).

| Property | CRC32 (IEEE) | CRC32c (Castagnoli) |
|----------|--------------|---------------------|
| Polynomial | `0x04C11DB7` | `0x1EDC6F41` |
| Hardware Support | ❌ None | ✅ Intel SSE4.2, ARM v8.1+ |
| Speed (software) | ~500 MB/s | ~500 MB/s |
| Speed (hardware) | N/A | **~6 GB/s** (10x faster) |
| Error Detection | Good | Better for certain patterns |

**Key insight**: Intel chose Castagnoli for SSE4.2 because it has better mathematical properties for detecting burst errors common in storage systems.

### Hardware Acceleration Details

**.NET Support** via `System.Runtime.Intrinsics.X86.Sse42`:

```csharp
// Check for hardware support
if (Sse42.IsSupported)
{
    // Hardware CRC32c - processes 8 bytes per instruction, 3 cycles each
    crc = Sse42.X64.Crc32(crc, data);  // ~2.67 bits/cycle
}
```

| Platform | Instruction | Available Since |
|----------|-------------|-----------------|
| Intel/AMD x64 | `CRC32` (SSE4.2) | Nehalem i7 (2008) |
| ARM64 | `CRC32C` | ARMv8.1 (2016) |

**Performance**: ~6x improvement vs software. Hardware processes 64KB buffer in ~6 microseconds.

### Proposed

```csharp
public static class Checksum
{
    // CRC32c with automatic hardware detection
    public static uint Crc32c(ReadOnlySpan<byte> data);
    public static uint Crc32c(ReadOnlySpan<byte> data, uint seed);

    // CRC masking (for storing CRC in data that will be CRC'd)
    // LevelDB technique: rotate right 15 bits + add constant
    public static uint Mask(uint crc);
    public static uint Unmask(uint maskedCrc);

    // Check hardware support
    public static bool IsHardwareAccelerated { get; }
}
```

**CRC Masking**: When you store a CRC inside data that will later be CRC'd (e.g., page header contains page CRC), the embedded CRC affects the outer CRC calculation. Masking avoids this circular dependency.

---

## P.2 Core-Local Statistics

### The Problem: Cache Line Bouncing

When multiple threads frequently update a shared counter, they fight over the same cache line:

```
Thread 0 (Core 0)          Thread 1 (Core 1)          Thread 2 (Core 2)
       ↓                          ↓                          ↓
   Increment                  Increment                  Increment
       ↓                          ↓                          ↓
   [shared counter = cache line bounces between cores]

Each increment:
1. Invalidate other cores' cache lines (~100 cycles)
2. Fetch exclusive ownership (~100 cycles)
3. Do the increment (1 cycle)
4. Other cores repeat...

Result: Simple Interlocked.Increment costs 200+ cycles instead of 1
```

### The Solution: Per-Core Counters

```
Thread 0 (Core 0)          Thread 1 (Core 1)          Thread 2 (Core 2)
       ↓                          ↓                          ↓
   counter[0]++               counter[1]++               counter[2]++
       ↓                          ↓                          ↓
   [each core has its own cache line - NO bouncing!]

Reading total: Sum(counter[0..N]) - only needed occasionally
```

### When to Use

| Scenario | Use Core-Local? | Why |
|----------|-----------------|-----|
| "Operations per second" counter | ✅ Yes | Incremented on every operation |
| "Cache hits" tracking | ✅ Yes | Very high frequency |
| "Active transactions" count | ❌ No | Low frequency, needs accuracy |
| "Total bytes written" | ✅ Yes | High frequency, sum is fine |

**RocksDB measured ~30% throughput improvement** by using core-local counters for their statistics.

### Proposed

```csharp
public class CoreLocalCounter
{
    // Padded to 64 bytes each to avoid false sharing
    private long[] _counters;  // One per CPU core

    public void Increment(long delta = 1)
    {
        // Get current processor ID, increment local counter
        // No synchronization needed - each core owns its slot
        _counters[GetCurrentProcessorId()] += delta;
    }

    // Aggregation (reads all cores) - only when you need the total
    public long Value => _counters.Sum();

    // Fast local read (current core only, for debugging)
    public long LocalValue => _counters[GetCurrentProcessorId()];
}
```

---

## P.3 Monotonic Timestamp

### Rationale

`DateTime.UtcNow` is **not monotonic** - the system clock can:
- Jump backwards (NTP sync, DST changes, manual adjustment)
- Jump forwards (time sync corrections)
- Have low resolution (~15.6ms on Windows by default)

For transaction ordering and timeout calculations, Typhon needs a clock that:
- **Never goes backwards**
- Has **sub-microsecond resolution**
- Is **consistent across threads**

### .NET Support

.NET 7+ provides `System.Diagnostics.Stopwatch.GetTimestamp()` which wraps:
- Windows: `QueryPerformanceCounter` (~100ns resolution)
- Linux: `clock_gettime(CLOCK_MONOTONIC)`

### Proposed

```csharp
public static class MonotonicClock
{
    // High-resolution monotonic timestamp (raw ticks)
    public static long GetTimestamp() => Stopwatch.GetTimestamp();

    // Frequency for conversion
    public static long Frequency => Stopwatch.Frequency;

    // Convert to elapsed time
    public static TimeSpan GetElapsed(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp);

    // Convenience methods
    public static double GetElapsedSeconds(long startTimestamp)
        => (GetTimestamp() - startTimestamp) / (double)Frequency;

    public static double GetElapsedMicroseconds(long startTimestamp)
        => GetElapsedSeconds(startTimestamp) * 1_000_000;
}
```

**Note**: Typhon has existing research on `QueryPerformanceCounter` in `claude/research/QueryPerformanceCounter-Deep-Dive.md`.

---

## P.4 Compression Helpers

### Rationale

RocksDB uses compression for:
- **LZ4**: Fast compression for hot data (real-time)
- **ZSTD**: Best ratio for cold data (background compaction)

Typhon could use compression for:
- Cold component data (infrequently accessed)
- WAL compaction
- Backup/export

### Hardware Acceleration Status

| Algorithm | CPU Hardware | GPU Hardware | Status |
|-----------|--------------|--------------|--------|
| **LZ4** | ❌ Software only | ✅ NVIDIA nvCOMP | SSE2/AVX2 optimized but not CPU instructions |
| **ZSTD** | ❌ Software only | ✅ NVIDIA nvCOMP | AVX2 optimized but not CPU instructions |
| **Deflate** | ✅ Intel QAT | ✅ Various | Hardware accelerators exist |

**Key insight**: Unlike CRC32c, there are no CPU instructions for LZ4/ZSTD. However:
- LZ4 uses SIMD (SSE2/AVX2) for faster memory operations
- Native C implementations are ~10x faster than pure managed code
- .NET wrappers like [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) use native binaries

### Performance Characteristics

| Algorithm | Compression | Decompression | Ratio | Best For |
|-----------|-------------|---------------|-------|----------|
| **LZ4** | ~500 MB/s | ~2-4 GB/s | 2-3x | Hot path, real-time |
| **LZ4 HC** | ~50 MB/s | ~2-4 GB/s | 3-4x | Better ratio, same decompress |
| **ZSTD** | ~300 MB/s | ~800 MB/s | 3-5x | Best ratio, still fast |
| **ZSTD -19** | ~5 MB/s | ~800 MB/s | 5-7x | Maximum compression |

### Typical Data Length Usage

| Data Size | Recommended | Rationale |
|-----------|-------------|-----------|
| < 256 bytes | None | Overhead exceeds savings |
| 256B - 4KB | LZ4 | Fast, minimal overhead |
| 4KB - 64KB | LZ4 or ZSTD | Page-sized data, good balance |
| 64KB - 1MB | ZSTD | Better ratio matters at scale |
| > 1MB | ZSTD (streaming) | Large blocks, max ratio |

**Typhon use case**: 8KB pages → LZ4 is ideal. Cold data backup → ZSTD.

### Proposed

```csharp
public interface ICompressor
{
    string Name { get; }
    int MaxCompressedSize(int inputSize);
    int Compress(ReadOnlySpan<byte> input, Span<byte> output);
    int Decompress(ReadOnlySpan<byte> input, Span<byte> output);
}

public static class Compression
{
    public static ICompressor Lz4 { get; }      // K4os.Compression.LZ4
    public static ICompressor Zstd { get; }     // ZstdSharp
    public static ICompressor None { get; }     // Pass-through (no compression)

    // Per-component-type configuration
    public static ICompressor GetForComponentType(Type componentType);
}
```

### .NET Libraries

| Library | Algorithm | Native | NuGet |
|---------|-----------|--------|-------|
| [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | LZ4 | Yes | `K4os.Compression.LZ4` |
| [ZstdSharp](https://github.com/oleg-st/ZstdSharp) | ZSTD | Pure managed | `ZstdSharp.Port` |
| [IronCompress](https://github.com/aloneguid/ironcompress) | Multiple | Yes | `IronCompress` |

---

## Summary: Proposed Additions

| # | Utility | Priority | Rationale |
|---|---------|----------|-----------|
| **P.1** | CRC32c Checksum | High | Page integrity, hardware accelerated |
| **P.2** | Core-Local Statistics | Medium | Hot-path counters without cache bouncing |
| **P.3** | Monotonic Timestamp | High | Transaction ordering, timeout accuracy |
| **P.4** | Compression | Low | Cold data, backups (optional)

---

# Code Locations Summary

| Category | Component | Location |
|----------|-----------|----------|
| A | String64, String1024, Geometric Types | `src/Typhon.Engine/Misc/String64.cs` |
| A | Variant | `src/Typhon.Engine/Misc/Variant.cs` |
| B | MurmurHash2 | `src/Typhon.Engine/Misc/MurmurHash2.cs` |
| B | MathExtensions | `src/Typhon.Engine/Misc/MathExtensions.cs` |
| C | SpanHelpers, SpanStream | `src/Typhon.Engine/Misc/SpanHelpers.cs` |
| D | Concurrent Bitmaps | `src/Typhon.Engine/Collections/` |
| D | ConcurrentArray | `src/Typhon.Engine/Collections/ConcurrentArray.cs` |
| E | AccessControl | `src/Typhon.Engine/Misc/AccessControl/` |
| E | AccessControlSmall | `src/Typhon.Engine/Misc/AccessControlSmall.cs` |
| E | AdaptiveWaiter | `src/Typhon.Engine/Misc/AdaptiveWaiter.cs` |
| F | Block Allocators | `src/Typhon.Engine/Misc/BlockAllocator/` |
| F | Memory Allocators | `src/Typhon.Engine/Misc/MemoryAllocator/` |
| ~~G~~ | ~~CurrentFrameEnricher~~ | ~~`src/Typhon.Engine/Misc/CurrentFrameEnricher.cs`~~ ⚰️ |

---

# Design Principles

## Blittable Types

All utility types used in components must be blittable (no managed references):

```csharp
// ✅ Blittable: can be directly copied
public struct String64 { fixed byte _data[64]; }

// ❌ NOT blittable: contains managed reference
public struct BadString { string _data; }
```

## Zero Allocation

Hot-path utilities avoid heap allocations:

```csharp
// ✅ Good: stack-allocated, no GC
Span<byte> buffer = stackalloc byte[64];

// ❌ Bad: heap allocation per call
var buffer = new byte[64];
```

## Lock-Free Where Possible

Concurrent utilities prefer lock-free operations:

```csharp
// Lock-free bit set
public bool TrySetBit(int index)
{
    return Interlocked.CompareExchange(
        ref _bits[index / 64],
        newValue,
        oldValue) == oldValue;
}
```

## Pinned Memory for Unsafe

Allocators pin memory so pointers remain stable:

```csharp
// Pinned: GC won't move this memory
var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
byte* ptr = (byte*)handle.AddrOfPinnedObject();
// ptr remains valid until handle.Free()
```

---

# Notes

## Dead Code

- **PackedDateTime** (`src/Typhon.Engine/Misc/PackedDateTime.cs`) - Not currently used
- **CurrentFrameEnricher** (`src/Typhon.Engine/Misc/CurrentFrameEnricher.cs`) - Not currently used

## Belongs to Other Components

- **Resource Registry** (`src/Typhon.Engine/Misc/Resource Registry/`) → Component 9 (Resource Management)
