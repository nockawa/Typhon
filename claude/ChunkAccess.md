# ChunkRandomAccessor Redesign Analysis

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Current Design Analysis](#current-design-analysis)
3. [Access Patterns Classification](#access-patterns-classification)
4. [Alternative Designs](#alternative-designs)
   - Alternative 1: Growable Cache with Reserved Minimum
   - Alternative 2: Scope-Based Access (ChunkAccessScope)
   - Alternative 3: Generation-Based Validation
   - Alternative 4: Hierarchical/Child Accessors
   - Alternative 5: Stack-Allocated Entry Buffer
   - Alternative 6: Read-Only vs Mutable Split
   - Alternative 7: Arena-Based Allocation
5. [Deep Performance Analysis](#deep-performance-analysis)
   - Matrix 1: Memory Indirections
   - Matrix 2: Data Locality & Cache Efficiency
   - Matrix 3: Stack vs Heap Allocation
   - Matrix 4: SIMD Friendliness
   - Matrix 5: Branch Prediction & Code Path Complexity
   - Combined Performance Score
6. [StackChunkAccessor Deep Dive](#understanding-the-capacity-question)
   - Understanding the Capacity Question
   - Design Options (Fixed/Hybrid/Scope-Based/Copy-Out)
   - Recommended Implementation: Scope-Based StackChunkAccessor
   - Usage Patterns
   - Capacity Planning
   - Performance Characteristics
7. [Comparison Matrix (Functional)](#comparison-matrix-functional)
8. [Recommended Approach](#recommended-approach)
9. [Migration Strategy](#migration-strategy)
10. [Appendix: Code Examples](#appendix-code-examples)
11. [Conclusion](#conclusion)

---

## Problem Statement

### Core Issues

The `ChunkRandomAccessor` type serves as a critical caching layer between the `ChunkBasedSegment` storage and higher-level code (B+Trees, ComponentRevision, etc.). Its current design has fundamental tensions:

1. **Fixed Cache Size vs Reentrant Access**: The cache has 8 entries (for SIMD optimization), but recursive algorithms like B+Tree operations may need more simultaneous chunks.

2. **Pin Mechanism Complexity**:
   - Without pinning: Returned pointers become invalid on next request
   - With pinning: Can exhaust cache entries, causing hard crash
   - Manual unpin: Easy to forget, causing resource leaks or exhaustion

3. **No Distinction Between Read and Write**: Read-only access could be much simpler (re-fetch anytime) but is treated the same as mutable access.

4. **Unsafe Default API**: `GetChunkAddress()` returns raw `byte*` that callers can use after it becomes invalid.

### Failure Modes

| Scenario | Current Behavior | Desired Behavior |
|----------|-----------------|------------------|
| Cache exhausted (all pinned) | `NotImplementedException` crash | Graceful handling or prevention |
| Forgot to unpin | Silent resource leak | Compiler/runtime warning or auto-cleanup |
| Use pointer after eviction | Memory corruption | Safe error or auto-refresh |
| Deep recursion | May crash | Automatic scaling |

---

## Current Design Analysis

### Data Structures

```csharp
public unsafe class ChunkRandomAccessor : IDisposable
{
    private ChunkBasedSegment _owner;
    private int _cachedPagesCount;           // Fixed, must be multiple of 8
    private PageAccessor[] _cachedPages;      // Actual page locks
    private CachedEntry[] _cachedEntries;     // Metadata per entry
    private int[] _pageIndices;               // For SIMD search
    private int _mruIndex;                    // Most Recently Used optimization

    [StructLayout(LayoutKind.Sequential)]
    private struct CachedEntry
    {
        public int HitCount;
        public short PinCounter;              // Prevents eviction when > 0
        public PagedMMF.PageState CurrentPageState;
        public short IsDirty;
        public short PromoteCounter;          // For exclusive access
        public byte* BaseAddress;
    }
}
```

### Key Operations

| Method | Returns | Pinning | Safety |
|--------|---------|---------|--------|
| `GetChunkAddress(index, pin, dirty)` | `byte*` | Optional | Unsafe - pointer may become invalid |
| `GetChunk<T>(index, dirty)` | `ref T` | No | Unsafe - reference may become invalid |
| `GetChunkReadOnly<T>(index)` | `ref readonly T` | No | Unsafe - reference may become invalid |
| `GetChunkHandle(index, dirty)` | `ChunkHandle` | Always | Safer - auto-unpin on dispose |
| `GetChunkAsSpan(index, pin, dirty)` | `Span<byte>` | Optional | Unsafe - span may become invalid |

### ChunkHandle (Current Safe-ish Pattern)

```csharp
public unsafe ref struct ChunkHandle : IDisposable
{
    private ChunkRandomAccessor _owner;
    private byte* _chunkDataAddress;
    private int _chunkDataLength;
    private int _entryIndex;

    public void Dispose() => _owner?.UnpinEntry(_entryIndex);

    public Span<byte> AsSpan() => new(_chunkDataAddress, _chunkDataLength);
    public ref T AsRef<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_chunkDataAddress);
}
```

**Problems with ChunkHandle**:
- Still requires `using` statement (forgettable)
- Each handle consumes a cache entry
- Multiple handles can exhaust cache
- No validation that the entry is still valid

### Usage Patterns in Codebase

#### Pattern 1: Simple Single Access (VariableSizedBufferSegment)
```csharp
// UNSAFE: No pinning, pointer invalid after next call
ref var rh = ref Unsafe.AsRef<Header>(accessor.GetChunkAddress(chunkId, dirtyPage: true));
// Any subsequent GetChunkAddress call may evict this!
```

#### Pattern 2: Handle with Using (Transaction, ComponentRevision)
```csharp
// SAFER: Auto-unpin on dispose
using var ch = info.CompRevTableAccessor.GetChunkHandle(chunkId, false);
ref var header = ref ch.AsRef<CompRevStorageHeader>();
// Valid until end of using block
```

#### Pattern 3: Multiple Simultaneous (RevisionWalker)
```csharp
// Holds TWO handles simultaneously
_firstChunkHandle = accessor.GetChunkHandle(firstChunkId, false);
_curChunkHandle = accessor.GetChunkHandle(firstChunkId, false);
// Both pinned, consuming 2 cache entries
```

#### Pattern 4: Recursive (BTree)
```csharp
// Each recursion level may access parent, child, siblings
// NodeRelatives tracks: LeftSibling, RightSibling, LeftAncestor, RightAncestor
// Could need 4+ nodes per recursion level
var child = GetChild(index, accessor);
NodeRelatives.Create(child, index, this, ref relatives, out var childRelatives, accessor);
var rightChild = child.Insert(ref args, ref childRelatives, this, accessor);
```

---

## Access Patterns Classification

### Read-Only Patterns

Read-only access has simpler requirements:
- Data doesn't change (within transaction)
- Can re-fetch if evicted (just a performance cost)
- Multiple readers don't interfere
- No dirty tracking needed

| Use Case | Example | Characteristics |
|----------|---------|-----------------|
| Lookup | BTree.TryGet | Single path traversal, brief access |
| Enumeration | RevisionEnumerator | Sequential, one-at-a-time |
| Validation | CheckConsistency | May access many nodes |
| Copy-out | ReadEntity | Access then copy data |

### Read-Write Patterns

Mutable access has stricter requirements:
- Must maintain stable reference during modification
- Must track dirty state for persistence
- Cannot be evicted mid-write
- May need exclusive page access

| Use Case | Example | Characteristics |
|----------|---------|-----------------|
| Insert | BTree.Add | May modify multiple nodes |
| Update | Transaction.UpdateEntity | Single chunk, brief |
| Split/Merge | BTree split/merge operations | Multiple nodes simultaneously |
| Chain modification | ComponentRevisionManager | Walk + modify chain |

### Hybrid Patterns

Many operations read multiple chunks before writing to one:
- Find the right location (read many)
- Modify (write one or few)
- Update indexes (write few)

---

## Alternative Designs

### Alternative 1: Growable Cache with Reserved Minimum

#### Concept

Keep the current API but allow the cache to grow dynamically when pinned entries exceed the SIMD-optimized portion.

#### Implementation

```csharp
public unsafe class ChunkRandomAccessor : IDisposable
{
    // Primary cache: fixed size, SIMD-optimized
    private const int PrimaryCacheSize = 8;  // Multiple of 8 for SIMD
    private CachedEntry[] _primaryCache;
    private int[] _primaryPageIndices;

    // Overflow cache: dynamic, linear search
    private List<CachedEntry>? _overflowCache;
    private List<int>? _overflowPageIndices;

    // Track usage for diagnostics
    private int _maxOverflowUsed;
    private int _overflowHits;

    private byte* GetPageRawDataAddr(int pageIndex, bool pin, bool dirtyPage, out int cacheEntryIndex)
    {
        // Fast path: SIMD search in primary cache
        var (found, index) = SearchPrimaryCache(pageIndex);
        if (found)
        {
            cacheEntryIndex = index;
            // ... handle hit
            return _primaryCache[index].BaseAddress;
        }

        // Check overflow cache
        if (_overflowCache != null)
        {
            for (int i = 0; i < _overflowCache.Count; i++)
            {
                if (_overflowPageIndices![i] == pageIndex)
                {
                    cacheEntryIndex = PrimaryCacheSize + i;
                    return _overflowCache[i].BaseAddress;
                }
            }
        }

        // Cache miss - find slot
        var primarySlot = FindUnpinnedPrimarySlot();
        if (primarySlot >= 0)
        {
            cacheEntryIndex = primarySlot;
            return LoadIntoSlot(primarySlot, pageIndex, pin, dirtyPage);
        }

        // All primary slots pinned - use overflow
        return LoadIntoOverflow(pageIndex, pin, dirtyPage, out cacheEntryIndex);
    }

    private byte* LoadIntoOverflow(int pageIndex, bool pin, bool dirty, out int index)
    {
        _overflowCache ??= new List<CachedEntry>(4);
        _overflowPageIndices ??= new List<int>(4);

        // Find unpinned overflow slot or add new
        for (int i = 0; i < _overflowCache.Count; i++)
        {
            if (_overflowCache[i].PinCounter == 0)
            {
                index = PrimaryCacheSize + i;
                // Evict and load
                return LoadIntoOverflowSlot(i, pageIndex, pin, dirty);
            }
        }

        // All overflow pinned too - grow
        index = PrimaryCacheSize + _overflowCache.Count;
        _overflowCache.Add(new CachedEntry());
        _overflowPageIndices.Add(-1);
        _maxOverflowUsed = Math.Max(_maxOverflowUsed, _overflowCache.Count);

        return LoadIntoOverflowSlot(_overflowCache.Count - 1, pageIndex, pin, dirty);
    }
}
```

#### Pros
- **No API changes**: Drop-in improvement
- **Never crashes**: Overflow handles exhaustion gracefully
- **SIMD preserved**: Primary path unchanged
- **Diagnostics**: Can track overflow usage to detect problematic patterns
- **Self-healing**: Overflow shrinks when pins released

#### Cons
- **Unbounded growth**: Buggy code (missing unpin) causes memory growth
- **Slower overflow**: Linear search in overflow portion
- **Complexity**: Two-tier cache adds implementation complexity
- **Still manual pinning**: Doesn't solve the "forgot to unpin" problem

#### Read-Only Optimization

For read-only access, we don't need pinning at all with this approach - just accept that re-fetch may happen:

```csharp
// No pin needed for read-only, just re-fetch if evicted
public ref readonly T GetChunkReadOnly<T>(int index) where T : unmanaged
{
    // Note: no pin parameter
    return ref Unsafe.AsRef<T>(GetChunkAddress(index, pin: false, dirtyPage: false));
}
```

---

### Alternative 2: Scope-Based Access (`ChunkAccessScope`)

#### Concept

Replace individual pinning with explicit scopes that manage a group of pinned entries together. Scopes provide clear lifetime boundaries and bulk cleanup.

#### Implementation

```csharp
/// <summary>
/// A scope that pins multiple chunks and unpins them all on dispose.
/// </summary>
public unsafe ref struct ChunkAccessScope : IDisposable
{
    private readonly ChunkRandomAccessor _accessor;
    private readonly int _scopeId;
    private int _pinnedCount;

    // Stack-allocated tracking for small scopes (covers 99% of cases)
    private fixed int _pinnedEntries[8];

    // Heap fallback for deep recursion
    private int[]? _heapPinnedEntries;

    internal ChunkAccessScope(ChunkRandomAccessor accessor, int scopeId)
    {
        _accessor = accessor;
        _scopeId = scopeId;
        _pinnedCount = 0;
        _heapPinnedEntries = null;
    }

    /// <summary>
    /// Get a chunk reference that remains valid for the scope's lifetime.
    /// </summary>
    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged
    {
        var address = _accessor.GetChunkAddressForScope(chunkId, dirty, out var entryIndex);
        TrackPinnedEntry(entryIndex);
        return ref Unsafe.AsRef<T>(address);
    }

    /// <summary>
    /// Get a read-only chunk reference. May be re-fetched but scope handles it.
    /// </summary>
    public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged
    {
        // For read-only, we still pin within scope but mark as read-only for optimization
        var address = _accessor.GetChunkAddressForScope(chunkId, dirty: false, out var entryIndex);
        TrackPinnedEntry(entryIndex);
        return ref Unsafe.AsRef<T>(address);
    }

    private void TrackPinnedEntry(int entryIndex)
    {
        if (_pinnedCount < 8)
        {
            _pinnedEntries[_pinnedCount++] = entryIndex;
        }
        else
        {
            _heapPinnedEntries ??= new int[16];
            if (_pinnedCount - 8 >= _heapPinnedEntries.Length)
            {
                Array.Resize(ref _heapPinnedEntries, _heapPinnedEntries.Length * 2);
            }
            _heapPinnedEntries[_pinnedCount++ - 8] = entryIndex;
        }
    }

    public void Dispose()
    {
        // Bulk unpin - much more efficient than individual unpins
        var accessor = _accessor;
        var count = Math.Min(_pinnedCount, 8);

        for (int i = 0; i < count; i++)
        {
            accessor.UnpinEntry(_pinnedEntries[i]);
        }

        if (_heapPinnedEntries != null)
        {
            for (int i = 0; i < _pinnedCount - 8; i++)
            {
                accessor.UnpinEntry(_heapPinnedEntries[i]);
            }
        }

        _accessor.EndScope(_scopeId);
    }
}

// Usage patterns:

// Simple case - single scope
public bool TryGet(TKey key, out int value, ChunkRandomAccessor accessor)
{
    using var scope = accessor.BeginScope();

    var node = Root;
    while (!node.GetIsLeaf(scope))
    {
        ref readonly var header = ref scope.GetReadOnly<NodeHeader>(node.ChunkId);
        node = GetNearestChild(key, header, scope);
    }

    ref readonly var leaf = ref scope.GetReadOnly<LeafNode>(node.ChunkId);
    // ... search leaf
}

// Nested scopes for recursive operations
public void Insert(ref InsertArguments args, ChunkAccessScope parentScope)
{
    using var scope = args.Accessor.BeginScope();

    ref var node = ref scope.Get<NodeHeader>(ChunkId, dirty: true);

    var child = GetChild(index, scope);
    child.Insert(ref args, scope);  // Recursive, gets own scope

    // parentScope entries still valid, scope entries released on return
}
```

#### Hierarchical Scopes

Scopes can be hierarchical for recursive algorithms:

```csharp
public unsafe class ChunkRandomAccessor
{
    private int _currentScopeDepth;
    private int[] _scopePinCounts;  // Track pins per scope level

    public ChunkAccessScope BeginScope()
    {
        var scopeId = _currentScopeDepth++;
        if (_scopePinCounts == null || scopeId >= _scopePinCounts.Length)
        {
            Array.Resize(ref _scopePinCounts, Math.Max(8, scopeId + 4));
        }
        _scopePinCounts[scopeId] = 0;
        return new ChunkAccessScope(this, scopeId);
    }

    internal void EndScope(int scopeId)
    {
        Debug.Assert(scopeId == _currentScopeDepth - 1, "Scopes must be ended in LIFO order");
        _currentScopeDepth--;
    }
}
```

#### Pros
- **Clear lifetime**: Scope boundary is explicit
- **Bulk cleanup**: Single dispose unpins all
- **Stack-allocated tracking**: No heap allocation for typical cases (≤8 pins)
- **Nested naturally**: Recursive code gets nested scopes
- **Compiler-enforced cleanup**: `ref struct` + `using` = must dispose
- **Readable code**: Scope makes intent clear

#### Cons
- **API change**: Requires updating all callers
- **Scope overhead**: Scope creation/disposal has cost
- **Fixed pattern**: Must fit scope-based thinking
- **Still fixed cache**: Underlying cache limit remains (though overflow helps)

#### Read-Only vs Read-Write in Scopes

The scope can track access patterns for optimization:

```csharp
public ref struct ChunkAccessScope : IDisposable
{
    [Flags]
    private enum EntryFlags : byte
    {
        None = 0,
        Pinned = 1,
        Dirty = 2,
        ReadOnly = 4  // Hint for potential optimization
    }

    private fixed byte _entryFlags[8];

    public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged
    {
        // Mark as read-only - could be used for:
        // 1. Skip dirty tracking
        // 2. Shared page access (no exclusive promotion)
        // 3. Potential copy-on-evict optimization
        var address = _accessor.GetChunkAddressForScope(chunkId, false, out var idx);
        TrackPinnedEntry(idx, EntryFlags.Pinned | EntryFlags.ReadOnly);
        return ref Unsafe.AsRef<T>(address);
    }

    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged
    {
        var flags = EntryFlags.Pinned | (dirty ? EntryFlags.Dirty : EntryFlags.None);
        var address = _accessor.GetChunkAddressForScope(chunkId, dirty, out var idx);
        TrackPinnedEntry(idx, flags);
        return ref Unsafe.AsRef<T>(address);
    }
}
```

---

### Alternative 3: Generation-Based Validation (`ChunkRef<T>`)

#### Concept

Return lightweight handles that validate on each access. If the underlying cache entry was evicted, transparently re-fetch. This eliminates the need for pinning entirely for read-only access.

#### Implementation

```csharp
/// <summary>
/// A validated reference to a chunk. Automatically re-fetches if evicted.
/// </summary>
public readonly unsafe ref struct ChunkRef<T> where T : unmanaged
{
    private readonly ChunkRandomAccessor _accessor;
    private readonly int _chunkId;
    private readonly int _entryIndex;
    private readonly uint _generation;

    internal ChunkRef(ChunkRandomAccessor accessor, int chunkId, int entryIndex, uint generation)
    {
        _accessor = accessor;
        _chunkId = chunkId;
        _entryIndex = entryIndex;
        _generation = generation;
    }

    /// <summary>
    /// Read-only access. May trigger re-fetch if evicted.
    /// </summary>
    public ref readonly T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Fast path: generation matches, entry still valid
            if (_accessor.ValidateGeneration(_entryIndex, _generation))
            {
                return ref Unsafe.AsRef<T>(_accessor.GetCachedAddress(_entryIndex));
            }

            // Slow path: re-fetch (may evict other entries)
            return ref Unsafe.AsRef<T>(_accessor.RefetchForRead(_chunkId));
        }
    }

    /// <summary>
    /// Mutable access. Pins the entry to prevent eviction during write.
    /// </summary>
    public ref T ValueMutable
    {
        get
        {
            // Must pin for writes to ensure stable reference
            return ref _accessor.GetPinnedMutable<T>(_chunkId, _entryIndex, _generation);
        }
    }

    /// <summary>
    /// Check if the reference is still cached (useful for performance-sensitive code).
    /// </summary>
    public bool IsCached => _accessor.ValidateGeneration(_entryIndex, _generation);
}

// Accessor additions
public unsafe partial class ChunkRandomAccessor
{
    private uint _globalGeneration;
    private uint[] _entryGenerations;

    public ChunkRef<T> GetRef<T>(int chunkId) where T : unmanaged
    {
        var address = GetChunkAddress(chunkId, pin: false, dirtyPage: false);
        var entryIndex = FindEntryForChunk(chunkId);
        return new ChunkRef<T>(this, chunkId, entryIndex, _entryGenerations[entryIndex]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ValidateGeneration(int entryIndex, uint expectedGeneration)
    {
        return _entryGenerations[entryIndex] == expectedGeneration;
    }

    private byte* EvictEntry(int entryIndex)
    {
        // Increment generation when evicting - invalidates all refs to this entry
        unchecked { _entryGenerations[entryIndex]++; }
        // ... existing eviction logic
    }
}
```

#### Versioned Cache Entry

```csharp
[StructLayout(LayoutKind.Sequential)]
private struct CachedEntry
{
    public int HitCount;
    public short PinCounter;
    public PagedMMF.PageState CurrentPageState;
    public short IsDirty;
    public short PromoteCounter;
    public byte* BaseAddress;
    public uint Generation;        // Added: incremented on eviction
    public int PageIndex;          // Added: for re-fetch
}
```

#### Usage Examples

```csharp
// Read-only traversal - no pinning needed
public NodeWrapper FindLeaf(TKey key, ChunkRandomAccessor accessor)
{
    var node = Root;
    while (true)
    {
        var nodeRef = accessor.GetRef<NodeHeader>(node.ChunkId);

        if (nodeRef.Value.IsLeaf)  // May re-fetch, that's OK
            return node;

        // Even if nodeRef was evicted, we only need the child ID
        var childId = nodeRef.Value.GetChildId(key);  // May re-fetch again
        node = new NodeWrapper(childId);
    }
}

// Write operation - use ValueMutable for stable reference
public void UpdateNode(int chunkId, ChunkRandomAccessor accessor)
{
    var nodeRef = accessor.GetRef<NodeHeader>(chunkId);
    ref var node = ref nodeRef.ValueMutable;  // Now pinned

    node.Count++;
    node.IsDirty = true;

    // Automatically unpins when scope exits
}
```

#### Pros
- **No manual pinning for reads**: Validation is automatic
- **Safe API**: Can't use invalid data
- **Minimal overhead**: Single uint comparison in fast path
- **Cache-friendly**: No resources held for read-only access
- **Transparent re-fetch**: Caller doesn't see complexity

#### Cons
- **Overhead per access**: Generation check on every `.Value`
- **Unpredictable performance**: Re-fetch can happen anytime
- **Cache thrashing**: Repeated re-fetches if pattern is bad
- **Mutable still needs care**: `ValueMutable` must be used correctly
- **Two access patterns**: Need to know when to use `Value` vs `ValueMutable`

#### Read-Only Optimization: Copy-on-Evict

For small structs, we could cache a copy when evicted:

```csharp
public readonly unsafe ref struct ChunkRef<T> where T : unmanaged
{
    private readonly ChunkRandomAccessor _accessor;
    private readonly int _chunkId;
    private readonly int _entryIndex;
    private readonly uint _generation;
    private readonly T _cachedCopy;  // Backup copy for small types
    private readonly bool _hasCachedCopy;

    public ref readonly T Value
    {
        get
        {
            if (_accessor.ValidateGeneration(_entryIndex, _generation))
            {
                return ref Unsafe.AsRef<T>(_accessor.GetCachedAddress(_entryIndex));
            }

            // For small types, use cached copy instead of re-fetching
            if (_hasCachedCopy && Unsafe.SizeOf<T>() <= 64)
            {
                return ref _cachedCopy;
            }

            return ref Unsafe.AsRef<T>(_accessor.RefetchForRead(_chunkId));
        }
    }
}
```

---

### Alternative 4: Hierarchical/Child Accessors

#### Concept

Allow creating independent child accessors that share the same segment but have their own cache. Perfect for recursive algorithms where each level needs its own chunk access.

#### Implementation

```csharp
public unsafe class ChunkRandomAccessor : IDisposable
{
    private readonly ChunkBasedSegment _owner;
    private readonly ChunkRandomAccessor? _parent;
    private readonly int _depth;

    // Per-accessor cache
    private PageAccessor[] _cachedPages;
    private CachedEntry[] _cachedEntries;
    private int[] _pageIndices;

    // Child management
    private ChunkRandomAccessor? _activeChild;

    // Pool for child accessors
    private static readonly ConcurrentBag<ChunkRandomAccessor> ChildPool = new();

    private ChunkRandomAccessor(ChunkRandomAccessor parent)
    {
        _owner = parent._owner;
        _parent = parent;
        _depth = parent._depth + 1;

        // Smaller cache for children (they're usually short-lived)
        InitializeCache(cachedPagesCount: 8);
    }

    /// <summary>
    /// Create a child accessor for nested operations.
    /// </summary>
    public ChunkRandomAccessor CreateChild()
    {
        if (_activeChild != null)
        {
            throw new InvalidOperationException(
                "Cannot create multiple simultaneous children. " +
                "Dispose the current child first.");
        }

        if (!ChildPool.TryTake(out var child))
        {
            child = new ChunkRandomAccessor(this);
        }
        else
        {
            child.ReinitializeAsChild(this);
        }

        _activeChild = child;
        return child;
    }

    public override void Dispose()
    {
        if (_activeChild != null)
        {
            throw new InvalidOperationException(
                "Cannot dispose parent while child is active.");
        }

        DisposePageAccessors();

        if (_parent != null)
        {
            _parent._activeChild = null;
            ChildPool.Add(this);  // Return to pool
        }
        else
        {
            Pool.Add(this);  // Root accessor pool
        }
    }
}

// Usage in BTree
public void InsertRecursive(ref InsertArguments args, ChunkRandomAccessor accessor)
{
    ref var node = ref accessor.GetChunk<NodeHeader>(ChunkId, dirty: true);

    if (!node.IsLeaf)
    {
        var childChunkId = FindChild(key, accessor);

        // Create child accessor for recursive call
        using var childAccessor = accessor.CreateChild();
        InsertRecursive(ref args, childAccessor);

        // Back to parent - our node ref is still valid
        // (child had its own cache, didn't evict ours)
    }
}
```

#### Shared Read Cache

For read-only access, children could share parent's cache:

```csharp
public unsafe class ChunkRandomAccessor
{
    // Read-through to parent for reads
    public ref readonly T GetChunkReadOnlyWithFallback<T>(int chunkId) where T : unmanaged
    {
        // Try our cache first
        if (TryGetCached(chunkId, out var address))
        {
            return ref Unsafe.AsRef<T>(address);
        }

        // Try parent's cache (read-only, so safe to share)
        if (_parent != null && _parent.TryGetCached(chunkId, out address))
        {
            return ref Unsafe.AsRef<T>(address);
        }

        // Cache miss - load into our cache
        return ref Unsafe.AsRef<T>(GetChunkAddress(chunkId, pin: false, dirty: false));
    }
}
```

#### Pros
- **Perfect isolation**: Parent's entries never evicted by child
- **Natural recursion model**: Each level gets own resources
- **Predictable**: No surprising evictions during operation
- **Pooled**: Child accessor creation is cheap
- **Clear ownership**: Parent can't dispose while child active

#### Cons
- **More memory**: Each level has its own cache (8+ entries per level)
- **Potential page contention**: Multiple accessors hitting PagedMMF
- **Object overhead**: Even pooled, there's creation cost
- **Limited children**: Only one active child at a time (in this design)
- **No cache sharing for writes**: Each level independent

#### Memory Analysis

For a B+Tree of height H with cache size 8 per accessor:
- Maximum simultaneous accessors: H (one per level during insert)
- Maximum cache entries: 8 * H
- For H=10 (very large tree): 80 entries = 80 * ~40 bytes = 3.2KB

This is actually quite reasonable!

---

### Alternative 5: Stack-Allocated Entry Buffer

#### Concept

For operations with known maximum chunk requirements, pass a stack-allocated buffer that provides the entries. Zero heap allocation, perfect for hot paths.

#### Implementation

```csharp
/// <summary>
/// Stack-allocated buffer for chunk entries. Use for known-depth operations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe ref struct ChunkEntryBuffer
{
    // Fixed-size inline storage
    private fixed byte _entries[CachedEntry.Size * 16];
    private fixed int _pageIndices[16];
    private int _capacity;
    private int _used;

    public static ChunkEntryBuffer Create(int capacity)
    {
        if (capacity > 16)
            throw new ArgumentException("Stack buffer limited to 16 entries");

        return new ChunkEntryBuffer { _capacity = capacity, _used = 0 };
    }

    internal ref CachedEntry GetEntry(int index)
    {
        fixed (byte* ptr = _entries)
        {
            return ref Unsafe.AsRef<CachedEntry>(ptr + index * CachedEntry.Size);
        }
    }

    internal ref int GetPageIndex(int index)
    {
        fixed (int* ptr = _pageIndices)
        {
            return ref ptr[index];
        }
    }
}

// Accessor extension
public unsafe partial class ChunkRandomAccessor
{
    /// <summary>
    /// Perform an operation using a provided stack buffer for entries.
    /// </summary>
    public void WithBuffer<TState>(
        ref ChunkEntryBuffer buffer,
        TState state,
        Action<ChunkRandomAccessor, TState> operation)
    {
        var originalEntries = _cachedEntries;
        var originalIndices = _pageIndices;
        var originalCount = _cachedPagesCount;

        try
        {
            // Temporarily use stack buffer
            _cachedPagesCount = buffer._capacity;
            // ... point to buffer's storage

            operation(this, state);
        }
        finally
        {
            // Restore original cache
            _cachedEntries = originalEntries;
            _pageIndices = originalIndices;
            _cachedPagesCount = originalCount;

            // Ensure we released pages from stack buffer
            // (critical - stack will be unwound!)
            ReleaseBufferPages(ref buffer);
        }
    }
}

// Usage
public void BTreeInsert(TKey key, int value, ChunkRandomAccessor accessor)
{
    // Stack-allocate enough entries for tree height + some margin
    var buffer = ChunkEntryBuffer.Create(16);

    accessor.WithBuffer(ref buffer, (key, value), static (acc, args) =>
    {
        // All chunk access here uses stack buffer
        // No heap allocation, predictable memory
        acc.InsertCore(args.key, args.value);
    });
}
```

#### Pros
- **Zero allocation**: Everything on stack
- **Cache-friendly**: Contiguous memory
- **Predictable**: Fixed size, no growth
- **Fast**: No heap, no GC pressure
- **Safe cleanup**: Buffer tied to stack frame

#### Cons
- **Limited size**: Stack space limits (typically 1MB)
- **Must know size upfront**: Can't grow dynamically
- **Complex API**: Callback pattern is awkward
- **Not composable**: Hard to use in recursive code
- **Platform dependent**: Stack limits vary

#### For Hot Paths

This is excellent for known-small operations:

```csharp
// Single-node operations - 2 entries enough
public bool TryGetSingleValue(int chunkId, out int value)
{
    var buffer = ChunkEntryBuffer.Create(2);
    // ...
}

// Leaf-level batch - 8 entries enough
public void BatchReadLeaf(Span<int> chunkIds, Span<int> values)
{
    var buffer = ChunkEntryBuffer.Create(8);
    // ...
}
```

---

### Alternative 6: Read-Only vs Mutable Split

#### Concept

Completely separate the read-only and mutable access patterns into different types with different behaviors.

#### Implementation

```csharp
/// <summary>
/// Read-only chunk access. Never pins, always validates, may re-fetch.
/// </summary>
public unsafe class ChunkReader : IDisposable
{
    private readonly ChunkBasedSegment _owner;
    private readonly PageAccessor[] _cachedPages;
    private readonly int[] _pageIndices;
    private readonly uint[] _generations;
    private int _cacheSize;

    /// <summary>
    /// Get read-only reference. May re-fetch if evicted.
    /// Never holds resources beyond the call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>(int chunkId) where T : unmanaged
    {
        var (si, off) = _owner.GetChunkLocation(chunkId);

        // Try cache
        var entryIndex = FindInCache(si);
        if (entryIndex >= 0)
        {
            return ref GetFromEntry<T>(entryIndex, off);
        }

        // Cache miss - load (may evict anything, no pins)
        return ref LoadAndRead<T>(si, off);
    }

    /// <summary>
    /// Read and copy out. Safest for data that will be used across calls.
    /// </summary>
    public T ReadCopy<T>(int chunkId) where T : unmanaged
    {
        return Read<T>(chunkId);  // Copy on return
    }
}

/// <summary>
/// Mutable chunk access. Explicit acquire/release lifecycle.
/// </summary>
public unsafe class ChunkWriter : IDisposable
{
    private readonly ChunkBasedSegment _owner;
    private readonly ChangeSet? _changeSet;

    // Smaller cache, but entries are more "permanent"
    private readonly PageAccessor[] _cachedPages;
    private readonly CachedEntry[] _entries;

    // Active mutations - must be released
    private readonly Dictionary<int, int> _activeChunks;

    /// <summary>
    /// Acquire mutable access to a chunk. Must call Release when done.
    /// </summary>
    public ChunkMutation<T> Acquire<T>(int chunkId) where T : unmanaged
    {
        if (_activeChunks.ContainsKey(chunkId))
        {
            throw new InvalidOperationException($"Chunk {chunkId} already acquired");
        }

        var address = LoadAndPin(chunkId);
        _activeChunks[chunkId] = /* entry index */;

        return new ChunkMutation<T>(this, chunkId, address);
    }

    /// <summary>
    /// Release a previously acquired chunk.
    /// </summary>
    public void Release(int chunkId)
    {
        if (!_activeChunks.TryGetValue(chunkId, out var entryIndex))
        {
            throw new InvalidOperationException($"Chunk {chunkId} not acquired");
        }

        UnpinEntry(entryIndex);
        _activeChunks.Remove(chunkId);
    }
}

/// <summary>
/// Handle to a mutable chunk. Dispose releases the chunk.
/// </summary>
public unsafe ref struct ChunkMutation<T> : IDisposable where T : unmanaged
{
    private readonly ChunkWriter _owner;
    private readonly int _chunkId;
    private readonly byte* _address;
    private bool _disposed;

    public ref T Value => ref Unsafe.AsRef<T>(_address);

    public void MarkDirty()
    {
        _owner.MarkDirty(_chunkId);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _owner.Release(_chunkId);
            _disposed = true;
        }
    }
}

// Combined accessor for mixed patterns
public unsafe class ChunkAccessor : IDisposable
{
    public ChunkReader Reader { get; }
    public ChunkWriter Writer { get; }

    public ChunkAccessor(ChunkBasedSegment owner, ChangeSet? changeSet = null)
    {
        Reader = new ChunkReader(owner);
        Writer = new ChunkWriter(owner, changeSet);
    }

    public void Dispose()
    {
        Writer.Dispose();
        Reader.Dispose();
    }
}
```

#### Usage

```csharp
// Pure read operation - simple, no resource management
public bool TryGet(TKey key, out int value, ChunkAccessor accessor)
{
    var reader = accessor.Reader;

    var node = Root;
    while (true)
    {
        ref readonly var header = ref reader.Read<NodeHeader>(node.ChunkId);

        if (header.IsLeaf)
        {
            var index = BinarySearch(header, key, reader);
            if (index >= 0)
            {
                value = reader.Read<KeyValueItem>(node.ChunkId + index).Value;
                return true;
            }
            value = default;
            return false;
        }

        node = GetChild(header, key, reader);
    }
}

// Write operation - explicit acquire/release
public void Insert(TKey key, int value, ChunkAccessor accessor)
{
    var reader = accessor.Reader;
    var writer = accessor.Writer;

    // Find insert location (read-only)
    var path = FindInsertPath(key, reader);

    // Modify nodes (mutable)
    foreach (var nodeId in path.NodesToModify)
    {
        using var mutation = writer.Acquire<NodeHeader>(nodeId);

        // Modify the node
        InsertIntoNode(ref mutation.Value, key, value);
        mutation.MarkDirty();

        // Released on dispose
    }
}
```

#### Pros
- **Clear semantics**: Read vs write is explicit
- **Optimized paths**: Read never pins, write always pins
- **No resource leaks for reads**: Nothing to forget
- **Explicit mutation tracking**: Easy to see what's being modified
- **Type safety**: Can't accidentally write through reader

#### Cons
- **Two types to manage**: More API surface
- **Pattern mismatch**: Some operations are mixed read/write
- **Overhead for mixed ops**: Creating both reader and writer
- **Migration cost**: Significant API change

---

### Alternative 7: Arena-Based Allocation

#### Concept

Create an arena for each operation. All chunk accesses within the operation allocate from the arena. When the operation completes, the entire arena is released at once.

#### Implementation

```csharp
/// <summary>
/// An arena that manages chunk access for an operation.
/// All allocations are released together when the arena is disposed.
/// </summary>
public unsafe class ChunkArena : IDisposable
{
    private readonly ChunkBasedSegment _owner;
    private readonly ChangeSet? _changeSet;

    // Arena-owned pages
    private PageAccessor[] _pages;
    private int _pageCount;
    private int _pageCapacity;

    // Chunk -> arena index mapping
    private Dictionary<int, (int arenaPageIndex, int offset)> _chunkMap;

    // Dirty tracking
    private HashSet<int> _dirtyArenaPages;

    public static ChunkArena Create(ChunkBasedSegment owner, int initialCapacity = 16)
    {
        return new ChunkArena(owner, initialCapacity);
    }

    private ChunkArena(ChunkBasedSegment owner, int capacity)
    {
        _owner = owner;
        _pages = new PageAccessor[capacity];
        _pageCapacity = capacity;
        _chunkMap = new Dictionary<int, (int, int)>(capacity * 4);
        _dirtyArenaPages = new HashSet<int>();
    }

    /// <summary>
    /// Get a chunk. Loaded into arena if not already present.
    /// Reference valid for arena's lifetime.
    /// </summary>
    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged
    {
        if (_chunkMap.TryGetValue(chunkId, out var location))
        {
            if (dirty) _dirtyArenaPages.Add(location.arenaPageIndex);
            return ref GetFromArena<T>(location);
        }

        return ref LoadIntoArena<T>(chunkId, dirty);
    }

    public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged
    {
        return ref Get<T>(chunkId, dirty: false);
    }

    private ref T LoadIntoArena<T>(int chunkId, bool dirty) where T : unmanaged
    {
        var (segmentIndex, offset) = _owner.GetChunkLocation(chunkId);

        // Check if we already have this page
        for (int i = 0; i < _pageCount; i++)
        {
            if (_pages[i].PageIndex == segmentIndex)
            {
                // Page already in arena
                _chunkMap[chunkId] = (i, offset);
                if (dirty) _dirtyArenaPages.Add(i);
                return ref GetFromArena<T>((i, offset));
            }
        }

        // Load new page into arena
        EnsureCapacity();
        _owner.GetPageSharedAccessor(segmentIndex, out _pages[_pageCount]);
        var arenaIndex = _pageCount++;

        _chunkMap[chunkId] = (arenaIndex, offset);
        if (dirty) _dirtyArenaPages.Add(arenaIndex);

        return ref GetFromArena<T>((arenaIndex, offset));
    }

    private ref T GetFromArena<T>((int arenaPageIndex, int offset) location) where T : unmanaged
    {
        var page = _pages[location.arenaPageIndex];
        var address = page.GetRawDataAddr() + (location.offset * _owner.Stride);
        return ref Unsafe.AsRef<T>(address);
    }

    public void Dispose()
    {
        // Mark dirty pages
        foreach (var dirtyIndex in _dirtyArenaPages)
        {
            if (_changeSet != null)
            {
                _changeSet.Add(_pages[dirtyIndex]);
            }
        }

        // Release all pages
        for (int i = 0; i < _pageCount; i++)
        {
            _pages[i].Dispose();
        }

        // Could pool the arena itself
    }
}

// Usage
public void ProcessBatch(IEnumerable<int> chunkIds, ChunkBasedSegment segment)
{
    using var arena = ChunkArena.Create(segment, initialCapacity: 32);

    foreach (var id in chunkIds)
    {
        ref var data = ref arena.Get<MyData>(id);
        ProcessData(ref data, arena);  // Can access other chunks too
    }

    // All pages released at once
}

public void BTreeOperation(ref InsertArguments args)
{
    using var arena = ChunkArena.Create(_segment);

    // All node accesses go through arena
    // References valid for entire operation
    ref var root = ref arena.Get<NodeHeader>(RootChunkId);

    // Recursive operations use same arena
    InsertRecursive(ref root, ref args, arena);

    // Bulk release
}
```

#### Pros
- **Bulk allocation/deallocation**: Very efficient
- **No individual tracking**: All released together
- **Stable references**: Everything in arena stays valid
- **Natural fit for transactions**: Arena per transaction
- **Simple mental model**: "Arena owns it all"

#### Cons
- **Memory growth**: Arena only grows, never shrinks during operation
- **All-or-nothing**: Can't release individual chunks
- **Dictionary overhead**: Lookup cost per access
- **Large operations**: Could hold many pages

#### Optimization: Page-Level Granularity

Since chunks within a page share the same PageAccessor, we can optimize:

```csharp
public unsafe class ChunkArena
{
    // Only track pages, not individual chunks
    private Dictionary<int, int> _segmentIndexToArenaIndex;

    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged
    {
        var (segmentIndex, offset) = _owner.GetChunkLocation(chunkId);

        if (!_segmentIndexToArenaIndex.TryGetValue(segmentIndex, out var arenaIndex))
        {
            arenaIndex = LoadPageIntoArena(segmentIndex);
        }

        if (dirty) _dirtyArenaPages.Add(arenaIndex);

        return ref GetChunkInPage<T>(arenaIndex, offset);
    }
}
```

This reduces dictionary size from chunk count to page count.

---

## Implementation Details

### Cache Entry Structure (Enhanced)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
private struct CachedEntry
{
    public byte* BaseAddress;        // 8 bytes - pointer to page data
    public int PageIndex;            // 4 bytes - which segment page
    public int HitCount;             // 4 bytes - for LRU
    public uint Generation;          // 4 bytes - for validation
    public short PinCounter;         // 2 bytes - pinning
    public short PromoteCounter;     // 2 bytes - exclusive access
    public PagedMMF.PageState State; // 1 byte  - current state
    public byte Flags;               // 1 byte  - IsDirty, IsReadOnly, etc.
    public short Reserved;           // 2 bytes - alignment
    // Total: 28 bytes, padded to 32

    [Flags]
    public enum EntryFlags : byte
    {
        None = 0,
        Dirty = 1,
        ReadOnly = 2,
        FromOverflow = 4,
        FromArena = 8
    }
}
```

### Thread-Local Accessor Pool

```csharp
public static class ChunkAccessorPool
{
    [ThreadStatic]
    private static Stack<ChunkRandomAccessor>? t_pool;

    public static ChunkRandomAccessor Rent(ChunkBasedSegment segment, int cacheSize = 8)
    {
        t_pool ??= new Stack<ChunkRandomAccessor>(4);

        if (t_pool.TryPop(out var accessor))
        {
            accessor.Reinitialize(segment, cacheSize);
            return accessor;
        }

        return new ChunkRandomAccessor(segment, cacheSize);
    }

    public static void Return(ChunkRandomAccessor accessor)
    {
        accessor.Reset();
        t_pool ??= new Stack<ChunkRandomAccessor>(4);

        if (t_pool.Count < 8)  // Limit pool size
        {
            t_pool.Push(accessor);
        }
    }
}
```

### Validation Inlining

```csharp
public readonly ref struct ChunkRef<T> where T : unmanaged
{
    // Store validation data inline to avoid indirection
    private readonly byte* _address;
    private readonly uint* _generationPtr;  // Points into accessor's array
    private readonly uint _expectedGeneration;
    private readonly ChunkRandomAccessor _accessor;
    private readonly int _chunkId;

    public ref readonly T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Single pointer dereference + compare
            if (*_generationPtr == _expectedGeneration)
            {
                return ref Unsafe.AsRef<T>(_address);
            }

            return ref RefetchSlow();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref readonly T RefetchSlow()
    {
        return ref Unsafe.AsRef<T>(_accessor.RefetchForRead(_chunkId));
    }
}
```

---

## Deep Performance Analysis

This section analyzes each alternative through the lens of low-level performance characteristics critical for a microsecond-latency database engine.

### Performance Criteria

| Criterion | Description | Why It Matters |
|-----------|-------------|----------------|
| **Memory Indirections** | Number of pointer chases to reach data | Each indirection is ~4ns + potential cache miss (~100ns) |
| **Data Locality** | How well data fits in cache lines | L1 hit: ~1ns, L2: ~4ns, L3: ~12ns, RAM: ~100ns |
| **Stack Allocation** | Use of stack vs heap | Stack is always hot, heap requires allocation + GC |
| **SIMD Friendliness** | Ability to use vector instructions | 8x throughput for parallel operations |
| **Cache Line Efficiency** | Bytes used per 64-byte cache line loaded | Wasted bytes = wasted bandwidth |
| **Branch Prediction** | Predictability of code paths | Misprediction: ~15-20 cycles penalty |

### Current Design: Memory Access Pattern

```
GetChunkAddress(chunkId) hot path:
┌─────────────────────────────────────────────────────────────────────────┐
│ 1. this._pageIndices          [Load array reference - 1 indirection]    │
│ 2. SIMD compare in _pageIndices[0..7]  [Load 32 bytes, 1 cache line]    │
│ 3. this._cachedEntries        [Load array reference - 1 indirection]    │
│ 4. _cachedEntries[hit].BaseAddress     [Load struct - 1 cache line]     │
│ 5. Return pointer                                                        │
└─────────────────────────────────────────────────────────────────────────┘
Total: 2-3 indirections, 2 cache lines minimum (best case: MRU hit = 1 indirection)
```

**Cache Line Analysis for Current CachedEntry:**
```csharp
struct CachedEntry {          // Current layout
    int HitCount;             //  4 bytes
    short PinCounter;         //  2 bytes
    PageState CurrentState;   //  1 byte (enum)
    short IsDirty;            //  2 bytes
    short PromoteCounter;     //  2 bytes
    byte* BaseAddress;        //  8 bytes
}                             // Total: ~19 bytes, padded to 24
// 64 / 24 = 2.67 entries per cache line
```

**Separate Arrays (Structure of Arrays pattern):**
```
_pageIndices:   [int, int, int, int, int, int, int, int] = 32 bytes (fits in 1 cache line)
_cachedEntries: [Entry0, Entry1, ...] = 24 * 8 = 192 bytes (3 cache lines)
_cachedPages:   [PageAccessor * 8] = 64+ bytes (managed refs, 1+ cache lines)
```

---

### Performance Matrix 1: Memory Indirections

**Indirection count for single chunk read (hot path, cache hit):**

| Alternative | Indirections | Access Chain | Notes |
|-------------|:------------:|--------------|-------|
| **Current (MRU hit)** | 1 | `this→entries[mru].BaseAddress` | Best case |
| **Current (SIMD hit)** | 2 | `this→indices[]→entries[].BaseAddress` | Typical |
| **Current (miss)** | 3+ | `+segment→page→rawData` | Cold load |
| **Alt 1: Growable** | 2-3 | Same as current + possible `→overflow[]` | +1 for overflow |
| **Alt 2: Scope** | 2 | `scope(stack)→accessor→entries[]` | Scope on stack |
| **Alt 3: Generation** | 3 | `ref→accessor→generations[]→entries[]` | Validation adds 1 |
| **Alt 4: Hierarchical** | 2 | Same as current per level | But N accessors |
| **Alt 5: Stack Buffer** | 1 | `buffer(stack)→inline entries` | Minimal |
| **Alt 6: Split Reader** | 2 | `reader→entries[]` | Same as current |
| **Alt 7: Arena** | 3 | `arena→dict lookup→pages[]` | Dictionary overhead |

**Scoring (lower is better):**

| Alternative | Hot Path | Cold Path | Score |
|-------------|:--------:|:---------:|:-----:|
| Current | 2 | 3+ | ⭐⭐⭐ |
| Alt 1: Growable | 2-3 | 4+ | ⭐⭐ |
| Alt 2: Scope | 2 | 3 | ⭐⭐⭐ |
| Alt 3: Generation | 3 | 4+ | ⭐⭐ |
| Alt 4: Hierarchical | 2×N | 3×N | ⭐ |
| **Alt 5: Stack Buffer** | **1** | **2** | ⭐⭐⭐⭐⭐ |
| Alt 6: Split | 2 | 3 | ⭐⭐⭐ |
| Alt 7: Arena | 3 | 3 | ⭐⭐ |

---

### Performance Matrix 2: Data Locality & Cache Efficiency

**Cache line usage analysis:**

| Alternative | Primary Data Structure | Bytes per Entry | Entries/Cache Line | Locality Pattern |
|-------------|----------------------|:---------------:|:------------------:|------------------|
| **Current** | Separate arrays (SOA partial) | 24 + 4 + 8 = 36 | 1.7 | Arrays separate |
| **Alt 1: Growable** | +List<> backing array | 36 + overhead | 1.5 | Worse: 2 regions |
| **Alt 2: Scope** | Fixed buffer in ref struct | 4 per tracked | 16 | Excellent: stack |
| **Alt 3: Generation** | +uint[] generations | 36 + 4 = 40 | 1.6 | One more array |
| **Alt 4: Hierarchical** | N × current | 36 × N | 1.7/accessor | Cache pollution |
| **Alt 5: Stack Buffer** | Inline fixed buffer | 24 | 2.6 | Perfect: contiguous |
| **Alt 6: Split** | 2 × smaller caches | 24 × 2 | 2.6 each | Two regions |
| **Alt 7: Arena** | Dictionary + arrays | Variable | Poor | Scattered |

**Cache behavior during B+Tree traversal (depth=5):**

| Alternative | Cache Lines Touched | Cache Misses (Est.) | Working Set |
|-------------|:-------------------:|:-------------------:|:-----------:|
| **Current** | ~15 per level = 75 | ~10-15 | Fixed 400B |
| **Alt 1: Growable** | 75 + overflow | ~15-25 | Variable |
| **Alt 2: Scope** | 75 + 1 per scope | ~10-12 | Fixed + 40B/level |
| **Alt 3: Generation** | 90 (+generations) | ~15-20 | Fixed 450B |
| **Alt 4: Hierarchical** | 75 × partial | ~20-30 | 400B × depth |
| **Alt 5: Stack Buffer** | ~12 per level = 60 | ~8-10 | 512B total stack |
| **Alt 6: Split** | Similar to current | ~12-15 | 2 × 200B |
| **Alt 7: Arena** | Variable + dict | ~20-40 | Grows |

**Scoring (higher is better):**

| Alternative | Cache Efficiency | Locality | Working Set | Score |
|-------------|:----------------:|:--------:|:-----------:|:-----:|
| Current | Good | Medium | Fixed | ⭐⭐⭐ |
| Alt 1: Growable | Medium | Poor | Variable | ⭐⭐ |
| Alt 2: Scope | Good | Excellent | Fixed+small | ⭐⭐⭐⭐ |
| Alt 3: Generation | Medium | Medium | Fixed+ | ⭐⭐⭐ |
| Alt 4: Hierarchical | Poor | Poor | Large | ⭐ |
| **Alt 5: Stack Buffer** | **Excellent** | **Excellent** | **Minimal** | ⭐⭐⭐⭐⭐ |
| Alt 6: Split | Good | Medium | 2× regions | ⭐⭐⭐ |
| Alt 7: Arena | Poor | Poor | Variable | ⭐ |

---

### Performance Matrix 3: Stack vs Heap Allocation

**Allocation analysis per operation:**

| Alternative | Stack Allocated | Heap Allocated | GC Pressure | Pooling |
|-------------|-----------------|----------------|:-----------:|:-------:|
| **Current** | ChunkHandle (24B) | Accessor (pooled) | Low | Yes |
| **Alt 1: Growable** | Same | +List<> on overflow | Medium | Partial |
| **Alt 2: Scope** | Scope (40-80B) | Accessor (pooled) | Low | Yes |
| **Alt 3: Generation** | ChunkRef (24B) | Accessor (pooled) | Low | Yes |
| **Alt 4: Hierarchical** | None | Child accessors | Medium | Yes |
| **Alt 5: Stack Buffer** | **All (256-512B)** | **None** | **None** | N/A |
| **Alt 6: Split** | ChunkMutation (24B) | Reader+Writer | Medium | Partial |
| **Alt 7: Arena** | None | Arena + Dict | High | Partial |

**Allocation cost per chunk access:**

| Alternative | Best Case | Typical | Worst Case |
|-------------|:---------:|:-------:|:----------:|
| **Current** | 0 | 0 | 0 (pooled) |
| **Alt 1: Growable** | 0 | 0 | List resize |
| **Alt 2: Scope** | 0 | 0 | Array alloc (>8 pins) |
| **Alt 3: Generation** | 0 | 0 | 0 |
| **Alt 4: Hierarchical** | 0 (pooled) | 0 | New accessor |
| **Alt 5: Stack Buffer** | **0** | **0** | **0** |
| **Alt 6: Split** | 0 | 0 | Dict entry |
| **Alt 7: Arena** | 0 | Dict add | Dict resize |

**Scoring (higher is better):**

| Alternative | Zero Alloc Hot Path | GC Friendly | Predictable | Score |
|-------------|:-------------------:|:-----------:|:-----------:|:-----:|
| Current | Yes | Yes | Yes | ⭐⭐⭐⭐ |
| Alt 1: Growable | Mostly | Medium | No | ⭐⭐ |
| Alt 2: Scope | Yes | Yes | Yes | ⭐⭐⭐⭐ |
| Alt 3: Generation | Yes | Yes | Yes | ⭐⭐⭐⭐ |
| Alt 4: Hierarchical | Pooled | Medium | Yes | ⭐⭐⭐ |
| **Alt 5: Stack Buffer** | **Always** | **Perfect** | **Always** | ⭐⭐⭐⭐⭐ |
| Alt 6: Split | Mostly | Medium | Mostly | ⭐⭐⭐ |
| Alt 7: Arena | No | Poor | No | ⭐ |

---

### Performance Matrix 4: SIMD Friendliness

**SIMD opportunities analysis:**

| Alternative | Cache Search | Bulk Operations | Data Layout | Vectorizable |
|-------------|:------------:|:---------------:|:-----------:|:------------:|
| **Current** | Vector256<int> | No | SOA partial | Search only |
| **Alt 1: Growable** | Primary only | No | Mixed | Degraded |
| **Alt 2: Scope** | Inherited | Bulk unpin | SOA | Search + unpin |
| **Alt 3: Generation** | Inherited | Batch validate | SOA + gen array | Limited |
| **Alt 4: Hierarchical** | Per accessor | No | AOS per level | Per accessor |
| **Alt 5: Stack Buffer** | Custom | Yes | Configurable | **Full control** |
| **Alt 6: Split** | Each cache | Partial | Separate SOA | Search only |
| **Alt 7: Arena** | No (dict) | No | Mixed | None |

**Optimal data layout for SIMD:**

```
Ideal SOA Layout (Alt 5 opportunity):
┌─────────────────────────────────────────────────────────────────┐
│ PageIndices:   [p0, p1, p2, p3, p4, p5, p6, p7] 32B Vector256   │
│ BaseAddresses: [a0, a1, a2, a3, a4, a5, a6, a7] 64B 2×Vector256 │
│ Metadata:      [m0, m1, m2, m3, m4, m5, m6, m7] 32B Vector256   │
└─────────────────────────────────────────────────────────────────┘
All arrays contiguous, aligned, SIMD-friendly
```

**SIMD instruction usage:**

| Alternative | Search (8-way) | Compare | Gather | Scatter |
|-------------|:--------------:|:-------:|:------:|:-------:|
| **Current** | ✅ Vector256 | ✅ | ❌ | ❌ |
| **Alt 1: Growable** | Partial | ✅ | ❌ | ❌ |
| **Alt 2: Scope** | ✅ Inherited | ✅ | ❌ | ❌ |
| **Alt 3: Generation** | ✅ + gen check | ✅ | ❌ | ❌ |
| **Alt 4: Hierarchical** | ✅ × N | ✅ | ❌ | ❌ |
| **Alt 5: Stack Buffer** | ✅ Optimal | ✅ | Possible | Possible |
| **Alt 6: Split** | ✅ × 2 | ✅ | ❌ | ❌ |
| **Alt 7: Arena** | ❌ Dict | ❌ | ❌ | ❌ |

**Scoring (higher is better):**

| Alternative | SIMD Search | SIMD Operations | Layout | Score |
|-------------|:-----------:|:---------------:|:------:|:-----:|
| Current | Excellent | None | Good | ⭐⭐⭐ |
| Alt 1: Growable | Degraded | None | Mixed | ⭐⭐ |
| Alt 2: Scope | Excellent | Bulk unpin | Good | ⭐⭐⭐⭐ |
| Alt 3: Generation | Good | Limited | Good | ⭐⭐⭐ |
| Alt 4: Hierarchical | Fragmented | None | Poor | ⭐⭐ |
| **Alt 5: Stack Buffer** | **Optimal** | **Full** | **Optimal** | ⭐⭐⭐⭐⭐ |
| Alt 6: Split | Good | None | Good | ⭐⭐⭐ |
| Alt 7: Arena | None | None | Poor | ⭐ |

---

### Performance Matrix 5: Branch Prediction & Code Path Complexity

**Branch analysis for hot path:**

| Alternative | Branches in Hot Path | Predictable? | Fast Path Likelihood |
|-------------|:--------------------:|:------------:|:--------------------:|
| **Current** | 3-4 (MRU, SIMD, found) | High | 90%+ MRU hit |
| **Alt 1: Growable** | 5-6 (+overflow check) | Medium | 80% primary |
| **Alt 2: Scope** | 3-4 (same as current) | High | 90%+ |
| **Alt 3: Generation** | 4-5 (+validation) | High | 95%+ if stable |
| **Alt 4: Hierarchical** | 3-4 × depth | Medium | Per level |
| **Alt 5: Stack Buffer** | 2-3 (minimal) | Excellent | 99%+ |
| **Alt 6: Split** | 3-4 per type | High | 90%+ |
| **Alt 7: Arena** | 5+ (dict lookup) | Medium | Variable |

**Code path analysis:**

```
Current GetChunkAddress:
┌──────────────────────────────────────────────────────────────────┐
│ if (pageIndices[mru] == pageIndex) → 90% taken (MRU fast path)   │
│   └─ return cached (1 branch)                                    │
│ else                                                             │
│   └─ SIMD search → branch on mask (2 branches)                   │
│       └─ if found: return (1 branch)                             │
│       └─ else: evict LRU, load (3+ branches)                     │
└──────────────────────────────────────────────────────────────────┘

Alt 3 Generation validation adds:
┌──────────────────────────────────────────────────────────────────┐
│ if (generation == expected) → 95%+ taken                         │
│   └─ return (1 branch)                                           │
│ else                                                             │
│   └─ refetch (function call, cold path)                          │
└──────────────────────────────────────────────────────────────────┘

Alt 5 Stack buffer (simplified):
┌──────────────────────────────────────────────────────────────────┐
│ SIMD search in inline buffer → 1 branch on result                │
│ if found: return (1 branch)                                      │
│ else: load into buffer (2 branches)                              │
└──────────────────────────────────────────────────────────────────┘
```

**Scoring (higher is better):**

| Alternative | Branch Count | Predictability | Code Simplicity | Score |
|-------------|:------------:|:--------------:|:---------------:|:-----:|
| Current | Medium | High | Medium | ⭐⭐⭐ |
| Alt 1: Growable | High | Medium | Low | ⭐⭐ |
| Alt 2: Scope | Medium | High | Medium | ⭐⭐⭐ |
| Alt 3: Generation | Medium+ | High | Medium | ⭐⭐⭐ |
| Alt 4: Hierarchical | High | Medium | Medium | ⭐⭐ |
| **Alt 5: Stack Buffer** | **Low** | **Excellent** | **High** | ⭐⭐⭐⭐⭐ |
| Alt 6: Split | Medium | High | Medium | ⭐⭐⭐ |
| Alt 7: Arena | High | Low | Low | ⭐ |

---

### Combined Performance Scoring

| Alternative | Indirections | Locality | Stack Alloc | SIMD | Branches | **Total** |
|-------------|:------------:|:--------:|:-----------:|:----:|:--------:|:---------:|
| **Current** | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | **16/25** |
| **Alt 1: Growable** | ⭐⭐ | ⭐⭐ | ⭐⭐ | ⭐⭐ | ⭐⭐ | **10/25** |
| **Alt 2: Scope** | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ | **18/25** |
| **Alt 3: Generation** | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | **15/25** |
| **Alt 4: Hierarchical** | ⭐ | ⭐ | ⭐⭐⭐ | ⭐⭐ | ⭐⭐ | **9/25** |
| **Alt 5: Stack Buffer** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | **25/25** |
| **Alt 6: Split** | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | **15/25** |
| **Alt 7: Arena** | ⭐⭐ | ⭐ | ⭐ | ⭐ | ⭐ | **6/25** |

---

### Performance-Optimized Hybrid Design

Given the analysis, the optimal approach combines:

1. **Stack-allocated entry buffer** for known-depth operations (BTree, short chains)
2. **Scope-based lifetime** for automatic cleanup
3. **SOA layout** for SIMD efficiency
4. **Generation validation** for read-only paths (optional complexity)

#### Optimal Data Layout (Full SOA)

```csharp
/// <summary>
/// Cache entries in Structure-of-Arrays layout for optimal SIMD and cache efficiency.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkCacheSOA
{
    // All arrays aligned for SIMD (32-byte alignment ideal for AVX2)
    public fixed int PageIndices[8];          // 32 bytes - 1 cache line half
    public fixed long BaseAddresses[8];       // 64 bytes - 1 cache line
    public fixed int HitCounts[8];            // 32 bytes - 1 cache line half
    public fixed short PinCounters[8];        // 16 bytes
    public fixed short DirtyFlags[8];         // 16 bytes
    public fixed byte States[8];              //  8 bytes
    public fixed byte Padding[24];            // Align to 192 bytes (3 cache lines)

    // Total: 192 bytes = 3 cache lines for 8 entries
    // Current AOS: 24 bytes × 8 = 192 bytes but scattered access patterns
}
```

#### Understanding the Capacity Question

**The critical question**: Does capacity mean:
- **(A)** Maximum chunks accessed simultaneously (with eviction/LRU)?
- **(B)** Maximum chunks ever touched during operation (no eviction)?

**Answer: It depends on the access pattern**, and this drives the design.

| Pattern | Capacity Meaning | Eviction Allowed? | Example |
|---------|------------------|:-----------------:|---------|
| **Sequential read** | Working set | Yes (LRU) | Iterating linked list |
| **Recursive with backtrack** | Max depth × refs per level | **No** - must pin | BTree insert/delete |
| **Copy-out** | 1-2 entries | Yes | Read, copy, discard |
| **Batch independent** | Batch size or LRU | Yes | Processing N unrelated chunks |

**The problem with naive LRU in recursive algorithms:**

```
BTree Insert (depth=3):
┌─────────────────────────────────────────────────────────────────────┐
│ 1. Get root node ref                    [Slot 0: Root]              │
│ 2. Recurse into child A                                              │
│    3. Get child A ref                   [Slot 1: ChildA]            │
│    4. Recurse into grandchild B                                      │
│       5. Get grandchild B ref           [Slot 2: GrandchildB]       │
│       6. Modify grandchild B            [Uses Slot 2 - OK]          │
│       7. Return from grandchild                                      │
│    8. Modify child A                    [Uses Slot 1 - OK]          │
│    9. Return from child                                              │
│ 10. Modify root                         [Uses Slot 0 - OK]          │
└─────────────────────────────────────────────────────────────────────┘

BUT if we only have 2 slots and use LRU:
┌─────────────────────────────────────────────────────────────────────┐
│ 1. Get root node ref                    [Slot 0: Root]              │
│ 2. Recurse into child A                                              │
│    3. Get child A ref                   [Slot 1: ChildA]            │
│    4. Recurse into grandchild B                                      │
│       5. Get grandchild B ref           [Slot 0: EVICTS Root!]      │
│       6. Modify grandchild B            [OK]                        │
│       7. Return from grandchild                                      │
│    8. Modify child A                    [Slot 1 - OK]               │
│    9. Return from child                                              │
│ 10. Modify root                         [CRASH: Root was evicted!]  │
└─────────────────────────────────────────────────────────────────────┘
```

#### Design Options for StackChunkAccessor

**Option 1: Fixed Capacity, No Eviction (Simple but Limited)**

```csharp
/// <summary>
/// Fixed capacity, no eviction. Use when you know the exact max chunks needed.
/// Fails if capacity exceeded.
/// </summary>
public unsafe ref struct StackChunkAccessor
{
    private fixed int _pageIndices[16];
    private fixed long _baseAddresses[16];
    private int _used;            // Only grows, never shrinks
    private int _capacity;

    // Once you access 16 different pages, you cannot access more
    // All 16 refs remain valid until Dispose
}
```

**Good for**: Small bounded operations (single node read, leaf update)
**Bad for**: Iteration, deep recursion, unknown bounds

---

**Option 2: Pinned + Working Slots (Hybrid)**

```csharp
/// <summary>
/// Hybrid: some slots are "pinned" (survive across calls), others are "working" (LRU).
/// </summary>
public unsafe ref struct StackChunkAccessor
{
    // Pinned slots: 0-7 (for recursive parent refs)
    // Working slots: 8-15 (for temporary access, LRU eviction)
    private fixed int _pageIndices[16];
    private fixed long _baseAddresses[16];
    private fixed byte _pinned[8];        // Which pinned slots are in use
    private int _pinnedCount;
    private int _workingMru;

    /// <summary>Pin a chunk - won't be evicted. Returns slot index for later unpin.</summary>
    public int Pin<T>(int chunkId, out ref T data);

    /// <summary>Unpin a previously pinned slot.</summary>
    public void Unpin(int slot);

    /// <summary>Temporary access - may evict other working (non-pinned) entries.</summary>
    public ref T Get<T>(int chunkId);
}
```

**Usage in recursive algorithm:**

```csharp
void InsertRecursive(int nodeChunkId, ref StackChunkAccessor accessor)
{
    // Pin current node - it will survive child recursion
    int slot = accessor.Pin<NodeHeader>(nodeChunkId, out ref var node);

    try
    {
        if (!node.IsLeaf)
        {
            var childId = FindChild(node, key);
            InsertRecursive(childId, ref accessor);  // Child may use working slots
            // node ref is still valid because we pinned it
        }

        ModifyNode(ref node);  // Safe - pinned slot
    }
    finally
    {
        accessor.Unpin(slot);  // Release for reuse
    }
}
```

**Good for**: Recursive with known max depth
**Bad for**: Unlimited depth (will exhaust pinned slots)

---

**Option 3: Scope-Based Protection (Best for Recursion)**

```csharp
/// <summary>
/// Scope-based: entries acquired in a scope are protected until scope exits.
/// Nested scopes create a stack of protection levels.
/// </summary>
public unsafe ref struct StackChunkAccessor
{
    private fixed int _pageIndices[16];
    private fixed long _baseAddresses[16];
    private fixed byte _scopeLevel[16];   // Which scope level acquired each slot
    private int _currentScope;            // Current nesting depth (0-15)
    private int _used;

    /// <summary>Enter a new scope. Entries acquired in this scope are protected.</summary>
    public void EnterScope() => _currentScope++;

    /// <summary>Exit scope. Entries from this scope become evictable.</summary>
    public void ExitScope() => _currentScope--;

    /// <summary>Get chunk, protected by current scope.</summary>
    public ref T Get<T>(int chunkId);
}
```

**Usage:**

```csharp
void InsertRecursive(int nodeChunkId, ref StackChunkAccessor accessor)
{
    accessor.EnterScope();  // Protect entries we acquire

    try
    {
        ref var node = ref accessor.Get<NodeHeader>(nodeChunkId);  // Protected by scope

        if (!node.IsLeaf)
        {
            var childId = FindChild(node, key);
            InsertRecursive(childId, ref accessor);  // New scope, can't evict ours
            // node ref still valid - protected by our scope
        }

        ModifyNode(ref node);
    }
    finally
    {
        accessor.ExitScope();  // Our entries now evictable by parent
    }
}
```

**Memory layout visualization:**

```
Scope Stack (max depth 16):
┌────────────────────────────────────────────────────────────────────┐
│ Scope 0 (root): Slots 0-2    [Root, maybe siblings]                │
│ Scope 1 (depth 1): Slots 3-5 [ChildA, siblings]                    │
│ Scope 2 (depth 2): Slots 6-8 [GrandchildB, siblings]               │
│ ... remaining slots for deeper recursion or eviction               │
└────────────────────────────────────────────────────────────────────┘

On ExitScope(2): Slots 6-8 marked evictable
On ExitScope(1): Slots 3-5 marked evictable
```

---

**Option 4: Copy-Out Pattern (Safest, Slightly Slower)**

```csharp
/// <summary>
/// All access is copy-out. References only valid for immediate use.
/// Safest pattern - no lifetime concerns.
/// </summary>
public unsafe ref struct StackChunkAccessor
{
    private fixed int _pageIndices[8];
    private fixed long _baseAddresses[8];
    // Small cache, aggressive LRU

    /// <summary>Read and copy. Reference valid only until next call.</summary>
    public T Read<T>(int chunkId) where T : unmanaged;

    /// <summary>Write data to chunk.</summary>
    public void Write<T>(int chunkId, in T data) where T : unmanaged;

    /// <summary>Modify in place. Callback receives ref, must complete before return.</summary>
    public void Modify<T>(int chunkId, ModifyDelegate<T> modifier) where T : unmanaged;
}
```

**Usage:**

```csharp
void InsertRecursive(int nodeChunkId, ref StackChunkAccessor accessor)
{
    // Read and copy header - we own the copy
    var node = accessor.Read<NodeHeader>(nodeChunkId);

    if (!node.IsLeaf)
    {
        var childId = FindChild(node, key);  // Works on our copy
        InsertRecursive(childId, ref accessor);
        // node copy still valid - it's ours on stack
    }

    // Modify: read-modify-write
    accessor.Modify<NodeHeader>(nodeChunkId, (ref NodeHeader n) => {
        n.Count++;
        n.IsDirty = true;
    });
}
```

**Good for**: Simple algorithms, safety-first
**Bad for**: Large structs (copy overhead), frequent modifications

---

#### Recommended Implementation: Scope-Based StackChunkAccessor

```csharp
/// <summary>
/// Stack-allocated chunk accessor with scope-based protection.
/// Zero heap allocation, SIMD-optimized, safe for recursion.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe ref struct StackChunkAccessor : IDisposable
{
    // === SOA Layout for cache efficiency ===
    private fixed int _pageIndices[16];       // 64 bytes - SIMD searchable
    private fixed long _baseAddresses[16];    // 128 bytes
    private fixed byte _scopeLevels[16];      // 16 bytes - which scope owns each slot
    private fixed byte _dirtyFlags[16];       // 16 bytes

    // === State ===
    private ChunkBasedSegment* _segment;
    private PageAccessor* _pageAccessors;     // Points to stack-allocated array
    private ChangeSet* _changeSet;
    private byte _currentScope;               // Current recursion depth (0-15)
    private byte _usedSlots;                  // High water mark
    private byte _capacity;                   // Max slots (8 or 16)

    // === Constants ===
    private const byte MaxScope = 15;
    private const byte InvalidSlot = 255;

    /// <summary>
    /// Create accessor. Caller must provide stack-allocated PageAccessor array.
    /// </summary>
    /// <param name="segment">The segment to access</param>
    /// <param name="pageAccessors">Stack-allocated: stackalloc PageAccessor[16]</param>
    /// <param name="changeSet">Optional change tracking</param>
    /// <param name="capacity">8 or 16 slots</param>
    public static StackChunkAccessor Create(
        ChunkBasedSegment* segment,
        PageAccessor* pageAccessors,
        ChangeSet* changeSet = null,
        int capacity = 16)
    {
        if (capacity != 8 && capacity != 16)
            ThrowHelper.ThrowArgument("Capacity must be 8 or 16 for SIMD alignment");

        var accessor = new StackChunkAccessor();
        accessor._segment = segment;
        accessor._pageAccessors = pageAccessors;
        accessor._changeSet = changeSet;
        accessor._capacity = (byte)capacity;
        accessor._currentScope = 0;
        accessor._usedSlots = 0;

        // Initialize all page indices to -1 (invalid)
        fixed (int* indices = accessor._pageIndices)
        {
            Unsafe.InitBlockUnaligned(indices, 0xFF, 64);
        }

        // Initialize scope levels to 255 (unowned)
        fixed (byte* scopes = accessor._scopeLevels)
        {
            Unsafe.InitBlockUnaligned(scopes, 0xFF, 16);
        }

        return accessor;
    }

    /// <summary>
    /// Enter a new scope. All chunks acquired after this are protected until ExitScope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterScope()
    {
        if (_currentScope >= MaxScope)
            ThrowHelper.ThrowInvalidOp("Maximum scope depth exceeded");
        _currentScope++;
    }

    /// <summary>
    /// Exit current scope. Chunks acquired in this scope become evictable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitScope()
    {
        if (_currentScope == 0)
            ThrowHelper.ThrowInvalidOp("No scope to exit");

        // Mark slots owned by this scope as evictable (scope = 255)
        fixed (byte* scopes = _scopeLevels)
        {
            for (int i = 0; i < _usedSlots; i++)
            {
                if (scopes[i] == _currentScope)
                    scopes[i] = 255;  // Now evictable
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
        var (segmentIndex, offset) = _segment->GetChunkLocation(chunkId);

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
    {
        return ref Get<T>(chunkId, dirty: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T GetFromSlot<T>(int slot, int offset, bool dirty) where T : unmanaged
    {
        fixed (long* addrs = _baseAddresses)
        fixed (byte* dirtyFlags = _dirtyFlags)
        fixed (byte* scopes = _scopeLevels)
        {
            // Claim for current scope if not already owned
            if (scopes[slot] == 255 || scopes[slot] < _currentScope)
                scopes[slot] = _currentScope;

            if (dirty)
                dirtyFlags[slot] = 1;

            var baseAddr = (byte*)addrs[slot];
            var chunkAddr = baseAddr + offset * _segment->Stride;
            return ref Unsafe.AsRef<T>(chunkAddr);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref T LoadAndGet<T>(int segmentIndex, int offset, bool dirty) where T : unmanaged
    {
        var slot = FindEvictableSlot();

        if (slot == InvalidSlot)
            ThrowHelper.ThrowInvalidOp("No evictable slots - increase capacity or check scope usage");

        // Evict if slot was in use
        EvictSlot(slot);

        // Load new page
        LoadIntoSlot(slot, segmentIndex);

        return ref GetFromSlot<T>(slot, offset, dirty);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FindEvictableSlot()
    {
        fixed (byte* scopes = _scopeLevels)
        {
            // First: use unused slots
            if (_usedSlots < _capacity)
                return _usedSlots++;

            // Second: find slot with scope=255 (evictable)
            for (int i = 0; i < _capacity; i++)
            {
                if (scopes[i] == 255)
                    return (byte)i;
            }

            return InvalidSlot;
        }
    }

    private void EvictSlot(int slot)
    {
        fixed (int* indices = _pageIndices)
        fixed (byte* dirtyFlags = _dirtyFlags)
        {
            if (indices[slot] == -1)
                return;  // Slot was never used

            // Handle dirty page
            if (dirtyFlags[slot] != 0 && _changeSet != null)
            {
                _changeSet->Add(_pageAccessors[slot]);
            }

            // Release page accessor
            _pageAccessors[slot].Dispose();
            indices[slot] = -1;
            dirtyFlags[slot] = 0;
        }
    }

    private void LoadIntoSlot(int slot, int segmentIndex)
    {
        fixed (int* indices = _pageIndices)
        fixed (long* addrs = _baseAddresses)
        fixed (byte* scopes = _scopeLevels)
        {
            _segment->GetPageSharedAccessor(segmentIndex, out _pageAccessors[slot]);
            indices[slot] = segmentIndex;
            addrs[slot] = (long)_pageAccessors[slot].GetRawDataAddr();
            scopes[slot] = _currentScope;
        }
    }

    public void Dispose()
    {
        // Flush dirty pages and release all
        fixed (int* indices = _pageIndices)
        fixed (byte* dirtyFlags = _dirtyFlags)
        {
            for (int i = 0; i < _usedSlots; i++)
            {
                if (indices[i] != -1)
                {
                    if (dirtyFlags[i] != 0 && _changeSet != null)
                    {
                        _changeSet->Add(_pageAccessors[i]);
                    }
                    _pageAccessors[i].Dispose();
                }
            }
        }
    }
}
```

#### Usage Patterns

**Pattern 1: Simple Read (No Scopes Needed)**

```csharp
public bool TryGetValue(int chunkId, out MyData value)
{
    // Stack allocate everything
    var pageAccessors = stackalloc PageAccessor[8];
    var accessor = StackChunkAccessor.Create(_segment, pageAccessors, capacity: 8);

    try
    {
        // Simple read - no scope needed, LRU is fine
        ref readonly var data = ref accessor.GetReadOnly<MyData>(chunkId);
        value = data;  // Copy out
        return true;
    }
    finally
    {
        accessor.Dispose();
    }
}
```

**Pattern 2: Iteration (Unknown Count)**

```csharp
public void ProcessAllLeaves()
{
    var pageAccessors = stackalloc PageAccessor[8];
    var accessor = StackChunkAccessor.Create(_segment, pageAccessors, capacity: 8);

    try
    {
        var currentId = _firstLeafChunkId;

        // Can iterate indefinitely - LRU evicts old leaves
        while (currentId != 0)
        {
            ref readonly var leaf = ref accessor.GetReadOnly<LeafNode>(currentId);
            ProcessLeaf(ref leaf);

            // Copy out next ID before potentially evicting this leaf
            currentId = leaf.NextLeafId;
        }
    }
    finally
    {
        accessor.Dispose();
    }
}
```

**Pattern 3: Recursive BTree (Scopes Required)**

```csharp
public int BTreeInsert(TKey key, int value)
{
    var pageAccessors = stackalloc PageAccessor[16];
    var accessor = StackChunkAccessor.Create(_segment, pageAccessors, capacity: 16);

    try
    {
        return InsertRecursive(_rootChunkId, key, value, ref accessor);
    }
    finally
    {
        accessor.Dispose();
    }
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

        // Find child and recurse
        var childId = BinarySearchChild(ref node, key, ref accessor);

        // Recursive call - child will enter its own scope
        // Our node is protected by our scope, won't be evicted
        var result = InsertRecursive(childId, key, value, ref accessor);

        // Back from recursion - node ref is still valid!
        if (NeedsSplit(ref node))
        {
            HandleSplit(ref node, ref accessor);
        }

        return result;
    }
    finally
    {
        // Release our scope - our entries now evictable
        accessor.ExitScope();
    }
}
```

**Pattern 4: Multiple Nodes at Same Level (Siblings)**

```csharp
private void RebalanceNodes(int leftId, int rightId, ref StackChunkAccessor accessor)
{
    accessor.EnterScope();  // Protect both siblings

    try
    {
        // Both refs protected by same scope
        ref var left = ref accessor.Get<NodeHeader>(leftId, dirty: true);
        ref var right = ref accessor.Get<NodeHeader>(rightId, dirty: true);

        // Can safely use both refs - neither will be evicted
        MoveItemsFromRightToLeft(ref left, ref right);
    }
    finally
    {
        accessor.ExitScope();
    }
}
```

#### Capacity Planning

| Operation | Max Scopes | Entries per Scope | Recommended Capacity |
|-----------|:----------:|:-----------------:|:--------------------:|
| Single read | 0 | 1 | 8 |
| Leaf iteration | 0 | 1-2 | 8 |
| BTree lookup | 0 | 1 per level | 8 |
| BTree insert (depth 5) | 5 | 1-3 per level | 16 |
| BTree insert (depth 10) | 10 | 1-3 per level | 16 (tight) |
| Split/merge ops | 2-3 | 3-4 per level | 16 |

**What happens if you exceed capacity?**

```
Capacity: 8 slots
Scope 0: Uses slots 0, 1, 2 (Root + 2 siblings)
Scope 1: Uses slots 3, 4, 5 (Child + 2 siblings)
Scope 2: Uses slots 6, 7    (Grandchild + 1 sibling)
Scope 3: Tries to allocate → NO EVICTABLE SLOTS → Exception!
```

**Solution**: Either increase capacity to 16, or reduce entries per scope.

#### Performance Characteristics

```
StackChunkAccessor (16 slots) memory layout:
┌─────────────────────────────────────────────────────────────────┐
│ _pageIndices[16]     64 bytes  │ 1 cache line (SIMD aligned)   │
│ _baseAddresses[16]  128 bytes  │ 2 cache lines                 │
│ _scopeLevels[16]     16 bytes  │ } Together in 1 cache line    │
│ _dirtyFlags[16]      16 bytes  │ }                             │
│ Pointers + state     32 bytes  │ 1 cache line                  │
└─────────────────────────────────────────────────────────────────┘
Total: 256 bytes on stack + PageAccessor[16] (~256 bytes)
Total stack usage: ~512 bytes

Compared to heap-based ChunkRandomAccessor:
- 0 heap allocations
- All data contiguous on stack (hot in L1)
- SIMD search in 1-2 Vector256 operations
- Scope tracking: single byte compare per slot
```

---

## Comparison Matrix (Functional)

| Aspect | Current | Alt 1: Growable | Alt 2: Scope | Alt 3: Generation | Alt 4: Hierarchical | Alt 5: Stack | Alt 6: Split | Alt 7: Arena |
|--------|---------|-----------------|--------------|-------------------|---------------------|--------------|--------------|--------------|
| **API Change** | - | None | Moderate | Moderate | Minor | Major | Major | Moderate |
| **Read-Only Safety** | Low | Low | High | High | Medium | High | High | High |
| **Write Safety** | Low | Low | High | Medium | High | High | High | High |
| **Memory Overhead** | Fixed | Variable | Fixed+stack | Fixed | Variable | None | Fixed | Variable |
| **Recursion Support** | Poor | Good | Good | Good | Excellent | Limited | Good | Good |
| **Implementation** | - | Easy | Medium | Medium | Medium | Hard | Hard | Medium |
| **Debugging** | Hard | Medium | Easy | Medium | Easy | Easy | Easy | Medium |
| **Resource Leaks** | Common | Less | Rare | Rare | Rare | None | Rare | None |

### Legend
- **Excellent**: Best possible
- **Good**: Works well
- **Medium**: Acceptable trade-offs
- **Limited**: Only for specific cases
- **Low/Hard/Common**: Problem area

---

## Recommended Approach

Based on the analysis, I recommend a **phased hybrid approach**:

### Phase 1: Safety Net (Immediate)

Add growable overflow cache to prevent crashes. No API changes.

```csharp
// Add to ChunkRandomAccessor
private List<CachedEntry>? _overflow;
private List<int>? _overflowIndices;

// Modify FindLruSlot to use overflow when primary exhausted
```

### Phase 2: Scope API (Short-term)

Add `ChunkAccessScope` for new code, keep existing API.

```csharp
// New API
public ChunkAccessScope BeginScope(int expectedChunks = 4);

// Scope usage
using var scope = accessor.BeginScope();
ref var node = ref scope.Get<NodeHeader>(chunkId, dirty: true);
```

### Phase 3: Validated Refs (Medium-term)

Add `ChunkRef<T>` for read-only access patterns.

```csharp
// New API for read-only
public ChunkRef<T> GetRef<T>(int chunkId);

// Usage
var nodeRef = accessor.GetRef<NodeHeader>(chunkId);
var isLeaf = nodeRef.Value.IsLeaf;  // Validates, may re-fetch
```

### Phase 4: Separation (Long-term)

Consider splitting into `ChunkReader` and `ChunkWriter` if patterns warrant.

### Final API Surface

```csharp
public unsafe class ChunkRandomAccessor : IDisposable
{
    // Existing (deprecated but kept for compatibility)
    [Obsolete("Use BeginScope() or GetRef<T>() instead")]
    public byte* GetChunkAddress(int index, bool pin = false, bool dirtyPage = false);

    // Phase 2: Scope-based (recommended for writes)
    public ChunkAccessScope BeginScope(int expectedChunks = 4);

    // Phase 3: Validated refs (recommended for reads)
    public ChunkRef<T> GetRef<T>(int chunkId) where T : unmanaged;

    // Phase 4: Explicit split (if needed)
    // public ChunkReader AsReader();
    // public ChunkWriter AsWriter();
}

public ref struct ChunkAccessScope : IDisposable
{
    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged;
    public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged;
}

public readonly ref struct ChunkRef<T> : where T : unmanaged
{
    public ref readonly T Value { get; }      // Read-only, validated
    public ref T ValueMutable { get; }         // Mutable, pins
    public bool IsCached { get; }              // Check if still in cache
}
```

---

## Migration Strategy

### Step 1: Add Overflow (Week 1)
- Implement growable cache
- Add diagnostics to track overflow usage
- Deploy and monitor

### Step 2: Implement Scope API (Week 2-3)
- Add `ChunkAccessScope`
- Update BTree to use scopes
- Keep existing API working

### Step 3: Implement ChunkRef (Week 3-4)
- Add generation tracking
- Add `ChunkRef<T>`
- Update read-heavy code paths

### Step 4: Gradual Migration (Ongoing)
- Mark old API as `[Obsolete]`
- Migrate callers one component at a time
- Add analyzers/warnings for unsafe patterns

### Step 5: Cleanup (Future)
- Remove deprecated API
- Optimize based on usage patterns
- Consider further specialization

---

## Appendix: Code Examples

### BTree with New API

```csharp
public bool TryGet(TKey key, out int value, ChunkRandomAccessor accessor)
{
    value = default;

    // Pure read - use validated refs
    var nodeRef = accessor.GetRef<NodeHeader>(GetRootChunkId(accessor));

    while (true)
    {
        ref readonly var node = ref nodeRef.Value;

        if (node.IsLeaf)
        {
            var index = BinarySearch(node, key);
            if (index >= 0)
            {
                value = GetItem(node, index).Value;
                return true;
            }
            return false;
        }

        var childId = GetNearestChildId(node, key);
        nodeRef = accessor.GetRef<NodeHeader>(childId);
    }
}

public int Add(TKey key, int value, ChunkRandomAccessor accessor)
{
    using var scope = accessor.BeginScope(expectedChunks: 8);

    if (IsEmpty(accessor))
    {
        CreateRoot(scope);
    }

    ref var root = ref scope.Get<NodeHeader>(GetRootChunkId(accessor), dirty: true);
    var result = InsertRecursive(ref root, key, value, scope);

    return result;
}

private int InsertRecursive(ref NodeHeader node, TKey key, int value, ChunkAccessScope scope)
{
    if (node.IsLeaf)
    {
        return InsertIntoLeaf(ref node, key, value);
    }

    var childId = GetNearestChildId(node, key);
    ref var child = ref scope.Get<NodeHeader>(childId, dirty: true);

    var result = InsertRecursive(ref child, key, value, scope);

    // Handle splits, etc.

    return result;
}
```

### RevisionWalker with New API

```csharp
internal ref struct RevisionWalker : IDisposable
{
    private readonly ChunkAccessScope _scope;
    private int _curChunkId;

    public RevisionWalker(ChunkRandomAccessor accessor, int firstChunkId)
    {
        _scope = accessor.BeginScope(expectedChunks: 4);
        _curChunkId = firstChunkId;

        // First chunk always accessible through scope
        ref var first = ref _scope.GetReadOnly<CompRevStorageHeader>(firstChunkId);
        Header = ref first;
    }

    public ref readonly CompRevStorageHeader Header;

    public bool Step()
    {
        var nextId = GetNextChunkId();
        if (nextId == 0) return false;

        _curChunkId = nextId;
        // Scope handles cache management automatically
        return true;
    }

    public ref readonly CompRevStorageElement GetCurrentElement(int index)
    {
        return ref _scope.GetReadOnly<CompRevStorageElement>(_curChunkId + index);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }
}
```

---

## Conclusion

The current `ChunkRandomAccessor` design conflates several concerns (caching, pinning, lifetime management) into a single API that's error-prone and can fail catastrophically. By separating read-only from mutable access, using scope-based resource management, and adding a growable safety net, we can achieve:

1. **Safety**: No more crashes from cache exhaustion
2. **Clarity**: Explicit scope boundaries make resource ownership clear
3. **Performance**: Optimized paths for read-only access
4. **Maintainability**: Easier to reason about and debug
5. **Gradual migration**: Can be adopted incrementally

The recommended hybrid approach provides maximum safety with minimal disruption to existing code.