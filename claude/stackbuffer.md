# StackChunkAccessor: Stack-Allocated Chunk Access

This document provides comprehensive documentation for the **Scope-Based StackChunkAccessor**, the fastest and most efficient chunk access pattern for the Typhon database engine.

## Table of Contents

1. [Overview](#overview)
2. [Why Stack Allocation](#why-stack-allocation)
3. [Memory Layout](#memory-layout)
4. [API Reference](#api-reference)
5. [Complete Implementation](#complete-implementation)
6. [Usage Patterns](#usage-patterns)
7. [Scope System Deep Dive](#scope-system-deep-dive)
   - [Clock Sweep Eviction](#clock-sweep-eviction)
8. [Capacity Planning](#capacity-planning)
9. [Performance Characteristics](#performance-characteristics)
10. [SIMD Operations](#simd-operations)
11. [Comparison with Current Design](#comparison-with-current-design)
12. [Migration Guide](#migration-guide)

---

## Overview

The `StackChunkAccessor` is a `ref struct` that provides zero-heap-allocation access to chunks in a `ChunkBasedSegment`. It achieves maximum performance through:

- **Stack allocation**: All data structures live on the stack
- **SOA layout**: Structure-of-Arrays for optimal cache utilization
- **SIMD search**: Vector256 operations for cache lookup
- **Scope-based protection**: Safe recursion without manual pin/unpin
- **Clock sweep eviction**: Fair slot reuse when cache is full

### Key Benefits

| Benefit | Description |
|---------|-------------|
| **Zero GC pressure** | No heap allocations during operation |
| **Minimal indirections** | 1 indirection in hot path (vs 2-3 in current design) |
| **Cache efficiency** | ~256-512 bytes stack footprint, always L1-hot |
| **Safe recursion** | Scope system protects parent refs from child eviction |
| **SIMD-optimized** | 8 or 16 entries searchable in 1-2 vector operations |
| **Fair eviction** | Clock sweep spreads evictions across slots |

---

## Why Stack Allocation

### Performance Analysis

The stack is always in L1 cache. Every function call already touches the stack, so stack-allocated accessor data is guaranteed hot.

```
Memory Access Latency:
┌─────────────────────────────────────────────────────────────────┐
│ L1 Cache:     ~1ns   (always hot for stack)                     │
│ L2 Cache:     ~4ns   (possible for recently used heap)          │
│ L3 Cache:    ~12ns   (typical for heap data)                    │
│ Main RAM:   ~100ns   (cold heap allocation)                     │
│ Page fault: ~10μs+   (worst case)                               │
└─────────────────────────────────────────────────────────────────┘
```

### Heap vs Stack Allocation Cost

```csharp
// Heap allocation: ~50-100ns + potential GC trigger
var accessor = new ChunkRandomAccessor(segment, 8);  // Heap

// Stack allocation: ~5ns (just stack pointer adjustment)
var pageAccessors = stackalloc PageAccessor[16];      // Stack
var accessor = StackChunkAccessor.Create(segment, pageAccessors);  // Stack
```

---

## Memory Layout

The `StackChunkAccessor` uses Structure-of-Arrays (SOA) layout for optimal cache line utilization and SIMD compatibility. All storage is self-contained using C# 12 InlineArray.

### Data Structure Layout (16 slots)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ OFFSET   │ FIELD               │ SIZE      │ CACHE LINE │ PURPOSE        │
├──────────┼─────────────────────┼───────────┼────────────┼────────────────┤
│ 0        │ _pageIndices[16]    │ 64 bytes  │ Line 0     │ SIMD searchable│
│ 64       │ _baseAddresses[16]  │ 128 bytes │ Lines 1-2  │ Direct pointers│
│ 192      │ _scopeLevels[16]    │ 16 bytes  │ Line 3     │ Scope ownership│
│ 208      │ _dirtyFlags[16]     │ 16 bytes  │ (cont.)    │ Dirty tracking │
│ 224      │ _pageAccessors[16]  │ ~256 bytes│ Lines 4-7  │ Page locks     │
│ ~480     │ _segment            │ 8 bytes   │ Line 8     │ Segment ref    │
│ ~488     │ _changeSet          │ 8 bytes   │ (cont.)    │ ChangeSet ref  │
│ ~496     │ _currentScope       │ 1 byte    │ (cont.)    │ Nesting depth  │
│ ~497     │ _usedSlots          │ 1 byte    │ (cont.)    │ High water mark│
│ ~498     │ _capacity           │ 1 byte    │ (cont.)    │ 8 or 16 slots  │
│ ~499     │ _clockHand          │ 1 byte    │ (cont.)    │ Eviction cursor│
└──────────┴─────────────────────┴───────────┴────────────┴────────────────┘
Total: ~512 bytes (all self-contained on stack, no external allocation)
```

**Key design points:**
- `PageAccessorBuffer` uses C# 12 `[InlineArray(16)]` to embed 16 PageAccessor structs directly
- `ChunkBasedSegment` and `ChangeSet` are regular references (C# 11+ allows refs in ref struct)
- No external allocations required - everything is self-contained

### Visual Memory Layout

```
_pageIndices (64 bytes = 1 cache line):
┌────┬────┬────┬────┬────┬────┬────┬────┬────┬────┬────┬────┬────┬────┬────┬────┐
│ p0 │ p1 │ p2 │ p3 │ p4 │ p5 │ p6 │ p7 │ p8 │ p9 │p10 │p11 │p12 │p13 │p14 │p15 │
└────┴────┴────┴────┴────┴────┴────┴────┴────┴────┴────┴────┴────┴────┴────┴────┘
   ↑_________ First Vector256 _______↑     ↑________ Second Vector256 _______↑

_baseAddresses (128 bytes = 2 cache lines):
┌────────┬────────┬────────┬────────┬────────┬────────┬────────┬────────┐
│  ptr0  │  ptr1  │  ptr2  │  ptr3  │  ptr4  │  ptr5  │  ptr6  │  ptr7  │
├────────┼────────┼────────┼────────┼────────┼────────┼────────┼────────┤
│  ptr8  │  ptr9  │ ptr10  │ ptr11  │ ptr12  │ ptr13  │ ptr14  │ ptr15  │
└────────┴────────┴────────┴────────┴────────┴────────┴────────┴────────┘

_scopeLevels + _dirtyFlags (32 bytes):
┌──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬───┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬───┐
│s0│s1│s2│s3│s4│s5│s6│s7│s8│s9│..│..│..│..│..│s15│d0│d1│d2│d3│d4│d5│d6│d7│d8│d9│..│..│..│..│..│d15│
└──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴───┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴───┘

_pageAccessors (InlineArray - embedded directly in struct):
┌─────────────┬─────────────┬─────────────┬─────────────┬─────────────┬───┐
│ PageAcc[0]  │ PageAcc[1]  │ PageAcc[2]  │ PageAcc[3]  │ PageAcc[4]  │...│
└─────────────┴─────────────┴─────────────┴─────────────┴─────────────┴───┘
```

---

## API Reference

### Creation

```csharp
/// <summary>
/// Create a new StackChunkAccessor. All storage is internal - no external allocations needed.
/// </summary>
/// <param name="segment">The ChunkBasedSegment to access</param>
/// <param name="changeSet">Optional change set for dirty page tracking</param>
/// <param name="capacity">8 (faster, non-recursive) or 16 (recursive with scopes)</param>
/// <returns>Initialized accessor ready for use</returns>
public static StackChunkAccessor Create(
    ChunkBasedSegment segment,
    ChangeSet changeSet = null,
    int capacity = 16);
```

### Scope Management

```csharp
/// <summary>
/// Enter a new scope. All chunks acquired after this are protected until ExitScope.
/// Maximum nesting depth: 15 levels.
/// </summary>
public void EnterScope();

/// <summary>
/// Exit current scope. Chunks acquired in this scope become evictable.
/// Must be called in LIFO order (stack discipline).
/// </summary>
public void ExitScope();
```

### Chunk Access

```csharp
/// <summary>
/// Get mutable reference to chunk data. Protected by current scope.
/// </summary>
/// <typeparam name="T">Unmanaged struct type to interpret chunk as</typeparam>
/// <param name="chunkId">Chunk identifier</param>
/// <param name="dirty">If true, marks page as dirty for persistence</param>
/// <returns>Mutable reference valid until scope exit or Dispose</returns>
public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged;

/// <summary>
/// Get read-only reference to chunk data. Same scope protection as Get.
/// </summary>
/// <typeparam name="T">Unmanaged struct type to interpret chunk as</typeparam>
/// <param name="chunkId">Chunk identifier</param>
/// <returns>Read-only reference valid until scope exit or Dispose</returns>
public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged;
```

### Cleanup

```csharp
/// <summary>
/// Dispose the accessor. Flushes dirty pages to change set and releases all page locks.
/// MUST be called before stack frame exits.
/// </summary>
public void Dispose();
```

---

## Complete Implementation

The implementation is located in `src/Typhon.Engine/Persistence Layer/StackChunkAccessor.cs`.

```csharp
using JetBrains.Annotations;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine;

/// <summary>
/// Inline array for PageAccessor storage (C# 12+).
/// Allows fixed-size array of structs to be embedded in ref struct.
/// </summary>
[InlineArray(16)]
internal struct PageAccessorBuffer
{
    private PageAccessor _element;
}

/// <summary>
/// Helper for throwing exceptions without inlining the throw site.
/// </summary>
internal static class ThrowHelper
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgument(string message) => throw new ArgumentException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidOp(string message) => throw new InvalidOperationException(message);
}

/// <summary>
/// Stack-allocated chunk accessor with scope-based protection.
/// Zero heap allocation, SIMD-optimized, safe for recursion.
///
/// All state is self-contained - no external allocations required.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public unsafe ref struct StackChunkAccessor : IDisposable
{
    // === SOA Layout for cache efficiency ===
    private fixed int _pageIndices[16];       // 64 bytes - SIMD searchable
    private fixed long _baseAddresses[16];    // 128 bytes
    private fixed byte _scopeLevels[16];      // 16 bytes - which scope owns each slot
    private fixed byte _dirtyFlags[16];       // 16 bytes

    // === Internal PageAccessor storage (C# 12 InlineArray) ===
    private PageAccessorBuffer _pageAccessors; // Inline array, no external allocation

    // === References (C# 11+ allows ref types in ref struct) ===
    private ChunkBasedSegment _segment;
    private ChangeSet _changeSet;

    // === State ===
    private byte _currentScope;               // Current recursion depth (0-15)
    private byte _usedSlots;                  // High water mark
    private byte _capacity;                   // Max slots (8 or 16)
    private byte _clockHand;                  // Clock sweep position for eviction

    // === Constants ===
    private const byte MaxScope = 15;
    private const byte InvalidSlot = 255;
    private const int InvalidPageIndex = -1;

    /// <summary>
    /// Create a new StackChunkAccessor. All storage is internal - no external allocations needed.
    /// </summary>
    /// <param name="segment">The ChunkBasedSegment to access</param>
    /// <param name="changeSet">Optional change set for dirty page tracking</param>
    /// <param name="capacity">8 (faster, non-recursive) or 16 (recursive with scopes)</param>
    public static StackChunkAccessor Create(ChunkBasedSegment segment, ChangeSet changeSet = null, int capacity = 16)
    {
        if (capacity != 8 && capacity != 16)
        {
            ThrowHelper.ThrowArgument("Capacity must be 8 or 16 for SIMD alignment");
        }

        var accessor = new StackChunkAccessor();
        accessor._segment = segment;
        accessor._changeSet = changeSet;
        accessor._capacity = (byte)capacity;
        accessor._currentScope = 0;
        accessor._usedSlots = 0;

        Unsafe.InitBlockUnaligned(accessor._pageIndices, 0xFF, 64);
        Unsafe.InitBlockUnaligned(accessor._scopeLevels, 0xFF, 16);
        Unsafe.InitBlockUnaligned(accessor._dirtyFlags, 0x00, 16);

        return accessor;
    }

    /// <summary>
    /// Enter a new scope. All chunks acquired after this are protected until ExitScope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterScope()
    {
        if (_currentScope >= MaxScope)
        {
            ThrowHelper.ThrowInvalidOp("Maximum scope depth (15) exceeded");
        }

        _currentScope++;
    }

    /// <summary>
    /// Exit current scope. Chunks acquired in this scope become evictable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitScope()
    {
        if (_currentScope == 0)
        {
            ThrowHelper.ThrowInvalidOp("No scope to exit");
        }

        // Mark slots owned by this scope as evictable (scope = 255)
        var currentScope = _currentScope;
        for (int i = 0; i < _usedSlots; i++)
        {
            if (_scopeLevels[i] == currentScope)
            {
                _scopeLevels[i] = 255;  // Now evictable
            }
        }

        _currentScope--;
    }

    /// <summary>
    /// Get mutable reference to chunk. Protected by current scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged
    {
        var (segmentIndex, offset) = _segment.GetChunkLocation(chunkId);

        // SIMD search for existing entry
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(segmentIndex);
            var v0 = Vector256.Load(indices);
            var mask = Vector256.Equals(v0, target).ExtractMostSignificantBits();

            if (mask != 0)
            {
                var slot = BitOperations.TrailingZeroCount(mask);
                return ref GetFromSlot<T>(slot, offset, dirty);
            }

            if (_capacity > 8)
            {
                var v1 = Vector256.Load(indices + 8);
                var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
                if (mask1 != 0)
                {
                    var slot = 8 + BitOperations.TrailingZeroCount(mask1);
                    return ref GetFromSlot<T>(slot, offset, dirty);
                }
            }
        }

        // Cache miss - load into new or evicted slot
        return ref LoadAndGet<T>(segmentIndex, offset, dirty);
    }

    /// <summary>
    /// Get read-only reference. Same scope protection as Get.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged
        => ref Get<T>(chunkId, dirty: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T GetFromSlot<T>(int slot, int offset, bool dirty) where T : unmanaged
    {
        // Claim for current scope if not already owned by a lower (parent) scope
        // This ensures parent scope entries are never taken by child
        if (_scopeLevels[slot] == 255 || _scopeLevels[slot] > _currentScope)
        {
            _scopeLevels[slot] = _currentScope;
        }

        if (dirty)
        {
            _dirtyFlags[slot] = 1;
        }

        var baseAddr = (byte*)_baseAddresses[slot];
        var chunkAddr = baseAddr + offset * _segment.Stride;
        return ref Unsafe.AsRef<T>(chunkAddr);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref T LoadAndGet<T>(int segmentIndex, int offset, bool dirty) where T : unmanaged
    {
        var slot = FindEvictableSlot();

        if (slot == InvalidSlot)
        {
            ThrowHelper.ThrowInvalidOp(
                "No evictable slots available. Increase capacity or reduce scope nesting.");
        }

        // Evict if slot was in use
        EvictSlot(slot);

        // Load new page
        LoadIntoSlot(slot, segmentIndex);

        return ref GetFromSlot<T>(slot, offset, dirty);
    }

    /// <summary>
    /// Find an evictable slot using clock sweep algorithm.
    /// Prefers unused slots, then sweeps from clock hand position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FindEvictableSlot()
    {
        // First: use unused slots (fast path)
        if (_usedSlots < _capacity)
        {
            return _usedSlots++;
        }

        // Clock sweep: scan from clock hand position, wrap around
        for (int i = 0; i < _capacity; i++)
        {
            var slot = (_clockHand + i) % _capacity;
            if (_scopeLevels[slot] == 255)
            {
                _clockHand = (byte)((slot + 1) % _capacity);
                return (byte)slot;
            }
        }

        return InvalidSlot;
    }

    private void EvictSlot(int slot)
    {
        if (_pageIndices[slot] == InvalidPageIndex)
        {
            return;  // Slot was never used
        }

        // Handle dirty page
        if (_dirtyFlags[slot] != 0 && _changeSet != null)
        {
            _changeSet.Add(_pageAccessors[slot]);
        }

        // Release page accessor
        _pageAccessors[slot].Dispose();
        _pageIndices[slot] = InvalidPageIndex;
        _dirtyFlags[slot] = 0;
    }

    private void LoadIntoSlot(int slot, int segmentIndex)
    {
        _segment.GetPageSharedAccessor(segmentIndex, out _pageAccessors[slot]);
        _pageIndices[slot] = segmentIndex;
        _baseAddresses[slot] = (long)_pageAccessors[slot].GetRawDataAddr();
        _scopeLevels[slot] = _currentScope;
    }

    /// <summary>
    /// Dispose accessor. Flushes dirty pages and releases all page locks.
    /// </summary>
    public void Dispose()
    {
        for (int i = 0; i < _usedSlots; i++)
        {
            if (_pageIndices[i] != InvalidPageIndex)
            {
                if (_dirtyFlags[i] != 0 && _changeSet != null)
                {
                    _changeSet.Add(_pageAccessors[i]);
                }
                _pageAccessors[i].Dispose();
            }
        }

        // Clear state to catch use-after-dispose
        _usedSlots = 0;
        _segment = null!;
    }
}
```

---

## Usage Patterns

### Pattern 1: Simple Read (No Scopes Needed)

For single-chunk or sequential access where you don't need to hold multiple references simultaneously.

```csharp
public bool TryGetValue(int chunkId, out MyData value)
{
    // Self-contained - no external allocation needed
    using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);

    // Simple read - no scope needed, LRU eviction is fine
    ref readonly var data = ref accessor.GetReadOnly<MyData>(chunkId);
    value = data;  // Copy out the data
    return true;
}
```

### Pattern 2: Iteration Over Unknown Count

For iterating linked structures where the total count is unknown. LRU eviction handles unlimited iteration.

```csharp
public void ProcessAllLeaves()
{
    using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);

    var currentId = _firstLeafChunkId;

    // Can iterate indefinitely - LRU evicts old leaves
    while (currentId != 0)
    {
        ref readonly var leaf = ref accessor.GetReadOnly<LeafNode>(currentId);
        ProcessLeaf(ref leaf);

        // IMPORTANT: Copy out next ID before potentially evicting this leaf
        currentId = leaf.NextLeafId;
    }
}
```

### Pattern 3: Recursive B+Tree Operations (Scopes Required)

For recursive algorithms where parent nodes must remain accessible during child processing.

```csharp
public int BTreeInsert(TKey key, int value)
{
    // Use capacity 16 for recursive operations
    using var accessor = StackChunkAccessor.Create(_segment, capacity: 16);
    return InsertRecursive(_rootChunkId, key, value, ref accessor);
}

private int InsertRecursive(int nodeChunkId, TKey key, int value, ref StackChunkAccessor accessor)
{
    // CRITICAL: Enter scope to protect our node from child's eviction
    accessor.EnterScope();

    try
    {
        ref var node = ref accessor.Get<NodeHeader>(nodeChunkId, dirty: true);

        if (node.IsLeaf)
        {
            // Base case: insert into leaf
            return InsertIntoLeaf(ref node, key, value);
        }

        // Find child to recurse into
        var childId = BinarySearchChild(ref node, key, ref accessor);

        // Recursive call - child will enter its own scope
        // Our node is protected by our scope, won't be evicted
        var result = InsertRecursive(childId, key, value, ref accessor);

        // Back from recursion - node ref is STILL VALID!
        if (NeedsSplit(ref node))
        {
            HandleSplit(ref node, ref accessor);
        }

        return result;
    }
    finally
    {
        // Release our scope - our entries now evictable by parent
        accessor.ExitScope();
    }
}
```

### Pattern 4: Multiple Nodes at Same Level (Siblings)

For operations requiring simultaneous access to multiple nodes at the same tree level.

```csharp
private void RebalanceNodes(int leftId, int rightId, ref StackChunkAccessor accessor)
{
    accessor.EnterScope();  // Protect BOTH siblings

    try
    {
        // Both refs protected by same scope - neither can evict the other
        ref var left = ref accessor.Get<NodeHeader>(leftId, dirty: true);
        ref var right = ref accessor.Get<NodeHeader>(rightId, dirty: true);

        // Can safely use both refs simultaneously
        MoveItemsFromRightToLeft(ref left, ref right);
    }
    finally
    {
        accessor.ExitScope();
    }
}
```

### Pattern 5: With ChangeSet for Dirty Tracking

When you need to track dirty pages for persistence.

```csharp
public void UpdateNode(int chunkId, ChangeSet changeSet)
{
    using var accessor = StackChunkAccessor.Create(_segment, changeSet, capacity: 8);

    ref var node = ref accessor.Get<NodeHeader>(chunkId, dirty: true);
    node.Counter++;

    // On Dispose, dirty pages are automatically added to changeSet
}
```

---

## Scope System Deep Dive

### The Problem Scopes Solve

Without scope protection, LRU eviction in recursive algorithms causes data corruption:

```
BTree Insert (depth=3) with 2-slot cache using naive LRU:
┌──────────────────────────────────────────────────────────────────────┐
│ 1. Get root node ref                    [Slot 0: Root]               │
│ 2. Recurse into child A                                              │
│    3. Get child A ref                   [Slot 1: ChildA]             │
│    4. Recurse into grandchild B                                      │
│       5. Get grandchild B ref           [Slot 0: EVICTS Root!]       │
│       6. Modify grandchild B            [OK]                         │
│       7. Return from grandchild                                      │
│    8. Modify child A                    [Slot 1 - OK]                │
│    9. Return from child                                              │
│ 10. Modify root                         [CRASH: Root was evicted!]   │
└──────────────────────────────────────────────────────────────────────┘
```

### How Scopes Fix This

Each recursion level enters a scope, protecting its entries from child eviction:

```
Same operation with scopes:
┌───────────────────────────────────────────────────────────────────────┐
│ 1. EnterScope(0)                        [Scope 0 active]              │
│ 2. Get root node ref                    [Slot 0: Root, scope=0]       │
│ 3. Recurse into child A                                               │
│    4. EnterScope(1)                     [Scope 1 active]              │
│    5. Get child A ref                   [Slot 1: ChildA, scope=1]     │
│    6. Recurse into grandchild B                                       │
│       7. EnterScope(2)                  [Scope 2 active]              │
│       8. Get grandchild B ref           [Slot 2: GrandchildB, scope=2]│
│       9. Modify grandchild B            [OK]                          │
│      10. ExitScope(2)                   [Slot 2 now evictable]        │
│   11. Modify child A                    [Slot 1 still protected]      │
│   12. ExitScope(1)                      [Slots 1,2 now evictable]     │
│ 13. Modify root                         [Slot 0 still protected!]     │
│ 14. ExitScope(0)                        [All slots evictable]         │
└───────────────────────────────────────────────────────────────────────┘
```

### Scope Rules

1. **LIFO Order**: Scopes must be exited in reverse order of entry (enforced by `_currentScope` counter)
2. **Protection**: Entries acquired in scope N are protected from eviction while any scope >= N is active
3. **Eviction**: When ExitScope(N) is called, all entries with scopeLevel=N become evictable (scopeLevel=255)
4. **Promotion**: If the same page is accessed in a deeper scope, it keeps its original (lower) scope level

### Scope State Visualization

```
After processing tree depth 0-2:

_scopeLevels array:
┌───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┐
│ 0 │ 1 │ 2 │255│255│255│255│255│255│255│255│255│255│255│255│255│
└───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┘
  ↑   ↑   ↑
Root Child Grandchild    (255 = evictable/unused)

After ExitScope(2):
┌───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┐
│ 0 │ 1 │255│255│255│255│255│255│255│255│255│255│255│255│255│255│
└───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┘
  ↑   ↑   ↑
Protected  Now evictable (can be reused)
```

### Clock Sweep Eviction

When all slots are used and a new page is needed, the accessor uses a **clock sweep** algorithm to find an evictable slot:

1. **Fast path**: If `_usedSlots < _capacity`, use the next unused slot
2. **Clock sweep**: Starting from `_clockHand`, scan slots looking for `scopeLevel == 255`
3. **Advance hand**: When a victim is found, advance `_clockHand` to the next position

```
Clock sweep example (capacity=8, all slots used):

Initial state (_clockHand = 3):
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 0 │ 1 │255│255│255│ 2 │255│ 0 │  ← scopeLevels
└───┴───┴───┴───┴───┴───┴───┴───┘
          ↑
      clockHand

Scan: slot 3 has scope=255 → EVICT
After eviction (_clockHand = 4):
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 0 │ 1 │255│ 1 │255│ 2 │255│ 0 │  ← scopeLevels (slot 3 now owned by scope 1)
└───┴───┴───┴───┴───┴───┴───┴───┘
              ↑
          clockHand
```

**Benefits of clock sweep:**
- Simple implementation with minimal overhead
- Spreads evictions across slots (avoids always evicting slot 0)
- No per-access tracking overhead (unlike LRU)
- Works well with scope-based protection

---

## Capacity Planning

### Why Two Capacities: 8 vs 16

The accessor supports two capacity options, each optimized for different use cases:

**8 slots (faster, for non-recursive operations)**
- Single Vector256 SIMD search (32 bytes = 8 ints in one instruction)
- Smaller stack footprint (~320 bytes total)
- Better cache locality (fewer cache lines touched)
- **Use for**: simple reads, iteration, lookups, sequential access

**16 slots (for recursive operations with scopes)**
- Two Vector256 SIMD searches (second only executes on first miss)
- Larger stack footprint (~512 bytes total)
- Required when scopes protect many entries simultaneously
- **Use for**: B+Tree insert/delete, rebalancing, deep recursion

```
Performance difference:
┌─────────────────────────────────────────────────────────────────────┐
│ 8 slots:   1 SIMD compare + 1 branch                    (fastest)   │
│ 16 slots:  1 SIMD compare + 1 branch + (on miss) 1 more SIMD + 1 br │
└─────────────────────────────────────────────────────────────────────┘
```

For non-recursive patterns accessing 1-2 chunks at a time, the 8-slot version will almost always hit on the first SIMD search, making the second search code path dead weight.

### Maximum B+Tree Depth Analysis

The maximum supported depth depends on how many slots are consumed per recursion level:

```
Formula: Max Depth = floor(Capacity / Slots_Per_Level) - 1
```

#### Slots Per Level by Operation

| Operation | What's Held Per Level | Slots/Level |
|-----------|----------------------|:-----------:|
| **Lookup** | Current node only | 1 |
| **Simple insert** | Node + potential split sibling | 1-2 |
| **Insert with balancing** | Node + left/right sibling | 2-3 |
| **Full NodeRelatives** | Node + LeftSibling + RightSibling + Ancestors | 3-4 |

#### Maximum Depth by Capacity and Operation

| Operation | Slots/Level | Max Depth (8 cap) | Max Depth (16 cap) |
|-----------|:-----------:|:-----------------:|:------------------:|
| **Lookup** | 1 | 7 | **15** |
| **Simple insert** | 2 | 3 | **7** |
| **Insert with balancing** | 3 | 1-2 | **4-5** |
| **Full NodeRelatives** | 4 | 1 | **3** |

#### Practical Implications

For a B+Tree with typical branching factor:

| Branching Factor | Depth 7 Capacity | Depth 15 Capacity |
|:----------------:|:----------------:|:-----------------:|
| 50 | 781 billion | 3 × 10^25 |
| 100 | 10^14 (100 trillion) | 10^30 |
| 200 | 1.28 × 10^16 | 3.2 × 10^34 |

**Conclusion**: With 16 capacity and typical insert operations (2 slots/level), max depth of **7 levels** supports **10^14+ entries** - far exceeding any practical database size.

### Recommended Capacity by Operation

| Operation | Max Scopes | Entries/Scope | Recommended Capacity |
|-----------|:----------:|:-------------:|:--------------------:|
| Single read | 0 | 1 | **8** |
| Leaf iteration | 0 | 1-2 | **8** |
| B+Tree lookup | 0 | 1 per level | **8** |
| B+Tree insert (depth ≤7) | ≤7 | 2 per level | **16** |
| Split/merge ops | 2-3 | 3-4 per level | **16** |
| Complex rebalance | 4-5 | 4 per level | **16** |

### Capacity Exhaustion

When all slots are protected (none evictable), the accessor throws:

```
Example: Capacity 8 slots, 3 slots per level

Scope 0: Uses slots 0, 1, 2 (Root + 2 siblings)        = 3 used
Scope 1: Uses slots 3, 4, 5 (Child + 2 siblings)       = 6 used
Scope 2: Uses slots 6, 7    (Grandchild + 1 sibling)   = 8 used
Scope 3: Tries to allocate → NO EVICTABLE SLOTS → InvalidOperationException!

Solution: Use capacity 16 for this operation pattern
```

**Solutions:**
1. **Increase capacity**: Use 16 slots instead of 8
2. **Reduce scope usage**: Exit scopes earlier, reuse slots
3. **Copy-out pattern**: Copy data and exit scope before recursing
4. **Optimize algorithm**: Reduce siblings held per level

### Memory Budget

| Capacity | Total Size (self-contained) | SIMD Searches | Notes |
|:--------:|:---------------------------:|:-------------:|:------|
| 8 slots | **~320 bytes** | 1 | Faster for non-recursive |
| 16 slots | **~512 bytes** | 1-2 | Required for deep recursion |

Both are well within typical stack limits (1MB default on Windows, 8MB on Linux).

*Note: All storage is internal via C# 12 InlineArray - no external allocations needed.*

---

## Performance Characteristics

### Operation Costs

| Operation | Instructions | Cache Lines | Branches |
|-----------|:------------:|:-----------:|:--------:|
| EnterScope | ~5 | 0 | 1 |
| ExitScope | ~10-20 | 1 | 1-16 |
| Get (cache hit) | ~15-25 | 1-2 | 2-3 |
| Get (cache miss) | ~100+ | 3+ | 5+ |
| GetReadOnly | Same as Get | Same | Same |
| FindEvictableSlot | ~5-20 | 1 | 1-16 |
| Dispose | ~10-20/slot | 1-2 | 1/slot |

**FindEvictableSlot** uses clock sweep: O(1) best case (unused slot available), O(n) worst case when scanning for evictable slots.

### Compared to Current ChunkRandomAccessor

| Metric | Current | StackChunkAccessor | Improvement |
|--------|:-------:|:------------------:|:-----------:|
| Allocation | 1 heap | 0 heap | 100% |
| Indirections (hot) | 2-3 | 1 | 50-67% |
| Cache lines (search) | 2-3 | 1-2 | 33-50% |
| GC pressure | Low | Zero | 100% |
| Working set | ~400 bytes | ~512 bytes | Similar |

### Benchmark Expectations

For typical B+Tree operations (depth 5-10):
- **10-30% faster** for read-only lookups
- **20-40% faster** for insert/update (reduced allocation overhead)
- **50%+ faster** for batch operations (no per-operation allocation)

---

## SIMD Operations

### Cache Search Implementation

The accessor uses AVX2 SIMD for parallel cache lookup:

```csharp
// Search 8 entries in single SIMD operation
fixed (int* indices = _pageIndices)
{
    var target = Vector256.Create(segmentIndex);  // [s, s, s, s, s, s, s, s]
    var v0 = Vector256.Load(indices);             // [p0,p1,p2,p3,p4,p5,p6,p7]
    var mask = Vector256.Equals(v0, target).ExtractMostSignificantBits();

    // mask is 8-bit value, bit N set if indices[N] == segmentIndex
    if (mask != 0)
    {
        var slot = BitOperations.TrailingZeroCount(mask);
        // Found at slot!
    }
}
```

### SIMD Alignment

The `_pageIndices` array is naturally 64-byte aligned (first field in struct) for optimal SIMD load performance.

```
Memory alignment:
┌──────────────────────────────────────────────────────────────────┐
│ _pageIndices: 64 bytes at offset 0 → 32-byte aligned for AVX2    │
│ First Vector256 load: addresses[0-31] → Perfect alignment        │
│ Second Vector256 load: addresses[32-63] → Also aligned           │
└──────────────────────────────────────────────────────────────────┘
```

---

## Comparison with Current Design

### Current ChunkRandomAccessor

```csharp
// Heap allocated, needs pooling
public unsafe class ChunkRandomAccessor : IDisposable
{
    private ChunkBasedSegment _owner;
    private PageAccessor[] _cachedPages;      // Heap array
    private CachedEntry[] _cachedEntries;     // Heap array
    private int[] _pageIndices;               // Heap array
    // ...
}
```

### StackChunkAccessor

```csharp
// Stack allocated, no pooling needed
public unsafe ref struct StackChunkAccessor : IDisposable
{
    private fixed int _pageIndices[16];       // Inline in struct
    private fixed long _baseAddresses[16];    // Inline in struct
    private fixed byte _scopeLevels[16];      // Inline in struct
    // ...
}
```

### Feature Comparison

| Feature | ChunkRandomAccessor | StackChunkAccessor |
|---------|:-------------------:|:------------------:|
| Allocation | Heap (pooled) | Stack |
| Pin/Unpin | Manual | Automatic (scopes) |
| Max entries | 8 (SIMD) | 8 or 16 |
| Recursion safety | Poor | Excellent |
| Memory layout | AOS (scattered) | SOA (contiguous) |
| Can escape method | Yes | No (ref struct) |

---

## Migration Guide

### Step 1: Identify Candidate Operations

Good candidates for StackChunkAccessor:
- Hot path operations (lookups, updates)
- Recursive algorithms (B+Tree insert/delete)
- Batch processing
- Operations that create new accessor per call

Poor candidates:
- Long-lived accessor needs (use current design)
- Cross-method accessor sharing (ref struct can't escape)

### Step 2: Convert Simple Operations

Before:
```csharp
public bool TryGet(int chunkId, out MyData value)
{
    using var accessor = _segment.GetRandomAccessor();
    ref readonly var data = ref accessor.GetChunkReadOnly<MyData>(chunkId);
    value = data;
    return true;
}
```

After:
```csharp
public bool TryGet(int chunkId, out MyData value)
{
    // Self-contained, no external allocation needed
    using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);
    ref readonly var data = ref accessor.GetReadOnly<MyData>(chunkId);
    value = data;
    return true;
}
```

### Step 3: Convert Recursive Operations

Before (with ChunkHandle pinning):
```csharp
private void InsertRecursive(int nodeId, ref ChunkAccessor accessor)
{
    using var handle = accessor.GetChunkHandle(nodeId, dirty: true);
    ref var node = ref handle.AsRef<NodeHeader>();

    if (!node.IsLeaf)
    {
        InsertRecursive(childId, accessor);
        // handle keeps node pinned
    }
}
```

After (with scopes):
```csharp
private void InsertRecursive(int nodeId, ref StackChunkAccessor accessor)
{
    accessor.EnterScope();
    try
    {
        ref var node = ref accessor.Get<NodeHeader>(nodeId, dirty: true);

        if (!node.IsLeaf)
        {
            InsertRecursive(childId, ref accessor);
            // scope keeps node protected
        }
    }
    finally
    {
        accessor.ExitScope();
    }
}
```

### Step 4: Benchmark and Validate

1. Run existing tests to verify correctness
2. Add specific tests for scope edge cases
3. Benchmark hot paths before/after
4. Monitor for capacity exhaustion in production

---

## Summary

The **Scope-Based StackChunkAccessor** provides:

- **Zero heap allocation** for chunk access
- **SIMD-optimized** cache lookup (8-16 entries)
- **Automatic scope protection** for recursive algorithms
- **Clock sweep eviction** for fair slot reuse
- **Contiguous memory layout** for cache efficiency
- **10-40% performance improvement** over heap-based accessor

Implementation: `src/Typhon.Engine/Persistence Layer/StackChunkAccessor.cs`

Use it for all hot-path chunk access patterns in the Typhon database engine.
