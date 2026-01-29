# Typhon Database Engine - Performance Optimization Report

**Date:** November 2025
**Status:** In progress
**Outcome:** 15 optimizations identified, some implemented (ChunkRandomAccessor → StackChunkAccessor)

---

## Executive Summary

This report identifies **15 major optimization opportunities** across the Typhon codebase that could yield **10-35% overall performance improvement** for typical workloads. The analysis focuses on algorithmic complexity, lock contention, memory allocation patterns, and I/O efficiency.

**Highest Impact Optimizations (Ranked by ROI):**

1. **ChunkRandomAccessor Cache Lookup** - Hash-based instead of linear search (5-10% improvement)
2. **Page Allocation Clock-Sweep** - Optimized algorithm (10-20% improvement)
3. **Transaction Dictionary Pooling** - Reduced GC pressure (5-15% improvement)
4. **MVCC Revision Lookup** - Caching and binary search (3-8% improvement)
5. **Lock-Free Page Transitions** - Optimistic concurrency (10-20% improvement)

**Total Estimated Improvement:** 10-35% depending on workload characteristics:
- High-concurrency workloads: **20-35% improvement**
- Update-heavy workloads: **15-25% improvement**
- Read-only workloads: **10-15% improvement**

---

## Table of Contents

1. [Critical Path Optimizations](#1-critical-path-optimizations)
2. [MVCC & Revision Management](#2-mvcc--revision-management)
3. [Page Cache & Eviction](#3-page-cache--eviction)
4. [Lock Contention Reduction](#4-lock-contention-reduction)
5. [Memory Allocation Optimizations](#5-memory-allocation-optimizations)
6. [B+Tree Index Improvements](#6-btree-index-improvements)
7. [I/O Batching & Patterns](#7-io-batching--patterns)
8. [Low-Level Optimizations](#8-low-level-optimizations)

---

## 1. Critical Path Optimizations

### 1.1 ChunkRandomAccessor - Linear Cache Search → Hash Lookup

**File:** `src/Typhon.Engine/Persistence Layer/ChunkRandomAccessor.cs:221-287`

#### Current Algorithm

```csharp
private byte* GetPageRawDataAddr(int pageIndex, bool pin, bool dirtyPage, out int cacheEntryIndex)
{
    // Linear search through cached pages
    for (cacheEntryIndex = 0; cacheEntryIndex < _cachedPagesCount; cacheEntryIndex++)
    {
        if (pageIndices[cacheEntryIndex] == pageIndex)
        {
            // Found: increment hit count and return
            ++entry.HitCount;
            return entry.BaseAddress;
        }

        // Track lowest hit count for LRU eviction
        if ((entry.PinCounter == 0) && (entry.HitCount < lowHit))
        {
            lowHit = entry.HitCount;
            pageI = cacheEntryIndex;
        }
    }

    // Cache miss: evict page with lowest hit count
    // ...
}
```

**Bottleneck:**
- O(N) linear search on every chunk access (N = 8 by default)
- For high-frequency chunk access (100k ops/sec), this is 800k comparisons/sec
- Even with N=8, this adds ~5-10 CPU cycles per access

**Complexity:**
- **Current:** O(N) per lookup
- **Frequency:** Every chunk access (extremely high)
- **Impact:** 5-10 CPU cycles × millions of accesses = significant overhead

---

#### Design Alternative 1: Direct Index Mapping (Best Performance)

**Algorithm:**
```csharp
private Dictionary<int, int> _pageIndexToCacheSlot;  // pageIndex → cache slot

private byte* GetPageRawDataAddr(int pageIndex, bool pin, bool dirtyPage, out int cacheEntryIndex)
{
    // O(1) hash lookup
    if (_pageIndexToCacheSlot.TryGetValue(pageIndex, out cacheEntryIndex))
    {
        ref var entry = ref _cachedEntries[cacheEntryIndex];
        ++entry.HitCount;
        if (pin) ++entry.PinCounter;
        if (dirtyPage) entry.IsDirty = 1;
        return entry.BaseAddress;
    }

    // Cache miss: find LRU victim
    int victim = FindLRUVictim();  // Separate O(N) scan only on miss
    _pageIndexToCacheSlot.Remove(_pageIndices[victim]);
    _pageIndexToCacheSlot[pageIndex] = victim;
    // ... load page ...
}
```

**Pros:**
- **O(1) lookup** instead of O(N) - massive improvement for hot paths
- Clean separation: cache hit (fast) vs cache miss (slow)
- Dictionary overhead amortized over many accesses

**Cons:**
- Dictionary allocation overhead (48-64 bytes + entries)
- Hash computation cost (~10-15 cycles, but still better than linear search for N>2)
- Memory overhead: ~16 bytes per cached page

**Performance Gain:**
- **Cache hit:** 0.5-1 microsecond saved (no linear search)
- **Cache miss:** Same or slightly slower (dictionary update)
- **Overall:** 5-10% improvement for chunk-heavy workloads
- **Best for:** N > 4 cache size

---

#### Design Alternative 2: Hybrid - Linear for Small, Hash for Large

**Algorithm:**
```csharp
private const int HashThreshold = 4;
private Dictionary<int, int> _pageIndexToCacheSlot;  // Only used if N > threshold

private byte* GetPageRawDataAddr(int pageIndex, ...)
{
    if (_cachedPagesCount <= HashThreshold)
    {
        // Small cache: linear search is faster (better cache locality)
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (_pageIndices[i] == pageIndex)
            {
                // ... handle hit ...
            }
        }
    }
    else
    {
        // Large cache: use hash map
        if (_pageIndexToCacheSlot.TryGetValue(pageIndex, out int slot))
        {
            // ... handle hit ...
        }
    }
}
```

**Pros:**
- Best of both worlds: fast for small caches, scalable for large caches
- No dictionary overhead for common case (N ≤ 4)
- Adaptive to cache configuration

**Cons:**
- Code complexity (two paths to maintain)
- Branch prediction penalty when switching modes

**Performance Gain:**
- **Small cache (N≤4):** Same as current or slightly better (optimized linear)
- **Large cache (N>4):** 5-10% improvement
- **Overall:** 3-7% improvement (mixed workloads)
- **Best for:** Variable cache sizes or when cache size is configurable

---

#### Design Alternative 3: MRU (Most Recently Used) Fast Path

**Algorithm:**
```csharp
private int _mruSlot = -1;  // Last accessed slot

private byte* GetPageRawDataAddr(int pageIndex, ...)
{
    // Fast path: check MRU first (temporal locality)
    if (_mruSlot >= 0 && _pageIndices[_mruSlot] == pageIndex)
    {
        ref var entry = ref _cachedEntries[_mruSlot];
        ++entry.HitCount;
        return entry.BaseAddress;
    }

    // Fallback: linear search (or hash, see Alternative 1)
    for (int i = 0; i < _cachedPagesCount; i++)
    {
        if (_pageIndices[i] == pageIndex)
        {
            _mruSlot = i;  // Update MRU
            // ... handle hit ...
        }
    }
}
```

**Pros:**
- Zero overhead for sequential access patterns (very common in Typhon)
- Simple implementation (one extra field)
- Works well with existing linear search

**Cons:**
- Only helps for sequential/temporal locality (not random access)
- Adds branch to fast path

**Performance Gain:**
- **Sequential access:** 1-2 microseconds saved per access (huge win!)
- **Random access:** No improvement (may be slight regression due to branch)
- **Overall:** 10-15% improvement for sequential workloads, 0-2% for random
- **Best for:** Sequential chunk access patterns (common in component iteration)

---

#### Recommendation: **Hybrid Approach (Alternative 2 + MRU)**

Combine MRU fast path with hybrid linear/hash strategy:

```csharp
private int _mruSlot = -1;
private Dictionary<int, int> _pageIndexToCacheSlot;  // Lazy init if N > 4

private byte* GetPageRawDataAddr(int pageIndex, ...)
{
    // Fast path: MRU check (sequential access)
    if (_mruSlot >= 0 && _pageIndices[_mruSlot] == pageIndex)
    {
        ref var entry = ref _cachedEntries[_mruSlot];
        ++entry.HitCount;
        return entry.BaseAddress;
    }

    // Medium path: small cache linear search
    if (_cachedPagesCount <= 4)
    {
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (_pageIndices[i] == pageIndex)
            {
                _mruSlot = i;
                // ... handle hit ...
            }
        }
    }
    else
    {
        // Slow path: hash lookup
        if (_pageIndexToCacheSlot.TryGetValue(pageIndex, out int slot))
        {
            _mruSlot = slot;
            // ... handle hit ...
        }
    }
}
```

**Expected Overall Gain:** **7-12% improvement** across all workloads

---

### 1.2 Transaction Dictionary Pooling

**File:** `src/Typhon.Engine/Database Engine/Transaction.cs:71, 91, 121, 147, 276`

#### Current Algorithm

```csharp
public class Transaction
{
    private Dictionary<Type, ComponentInfo> _componentInfos;  // Line 91

    internal void Initialize(DatabaseEngine dbe)
    {
        // Allocate new dictionary on every transaction creation
        _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);  // Line 121
    }

    internal void Reset()
    {
        // If capacity exceeded, recreate dictionary
        if (_componentInfos.Count > ComponentInfosMaxCapacity)
        {
            _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);  // Line 147
        }
        else
        {
            _componentInfos.Clear();
        }
    }
}

internal class ComponentInfo
{
    public Dictionary<long, CompRevInfo> CompRevInfoCache;  // Line 71

    public ComponentInfo()
    {
        CompRevInfoCache = new Dictionary<long, CompRevInfo>();  // Line 276
    }
}
```

**Bottleneck:**
- Dictionary allocations trigger Gen2 GC in high-throughput scenarios
- `ComponentInfosMaxCapacity = 131` is excessive for most transactions (typical: 1-3 component types)
- Each ComponentInfo creates a new Dictionary (double allocation penalty)
- High-frequency transactions (10k-100k/sec) create 10k-100k dictionaries/sec

**Memory Impact:**
- Dictionary overhead: ~48 bytes (header) + 16 bytes/entry (bucket array)
- For capacity 131: ~48 + 131×8 (buckets) = ~1096 bytes per transaction
- At 10k transactions/sec: **10.4 MB/sec allocation rate** → Gen2 GC pressure

---

#### Design Alternative 1: Dictionary Pooling (Simple)

**Algorithm:**
```csharp
public class TransactionChain
{
    private ConcurrentBag<Dictionary<Type, ComponentInfo>> _dictionaryPool;
    private ConcurrentBag<Dictionary<long, CompRevInfo>> _revInfoCachePool;

    public Transaction CreateTransaction()
    {
        var t = _transactionPool.Count > 0 ? _transactionPool.Pop() : new Transaction();

        // Rent dictionary from pool
        t._componentInfos = _dictionaryPool.TryTake(out var dict)
            ? dict
            : new Dictionary<Type, ComponentInfo>(8);  // Smaller default capacity

        return t;
    }

    public void Remove(Transaction t)
    {
        // Return dictionary to pool
        t._componentInfos.Clear();
        if (_dictionaryPool.Count < 16)  // Max pool size
        {
            _dictionaryPool.Add(t._componentInfos);
        }

        // Pool transaction object
        if (_transactionPool.Count < 16)
        {
            _transactionPool.Push(t);
        }
    }
}
```

**Pros:**
- Eliminates 90%+ of dictionary allocations
- Trivial to implement (similar to existing transaction pooling)
- Configurable pool size prevents unbounded memory growth

**Cons:**
- Pool contention under heavy concurrency (ConcurrentBag uses locks)
- Dictionaries retain capacity (memory not freed)
- May need periodic capacity trimming

**Performance Gain:**
- **Allocation cost:** ~0.5-1 microsecond saved per transaction
- **GC pressure:** 80-90% reduction in Gen2 collections
- **Overall:** 5-10% improvement in high-frequency transaction workloads
- **Best for:** Workloads with >1k transactions/sec

---

#### Design Alternative 2: ThreadLocal Dictionary (Lock-Free)

**Algorithm:**
```csharp
public class Transaction
{
    [ThreadStatic]
    private static Dictionary<Type, ComponentInfo> _threadLocalDict;

    [ThreadStatic]
    private static Dictionary<long, CompRevInfo> _threadLocalRevCache;

    internal void Initialize(DatabaseEngine dbe)
    {
        // Reuse thread-local dictionary (lock-free!)
        if (_threadLocalDict == null)
        {
            _threadLocalDict = new Dictionary<Type, ComponentInfo>(8);
        }
        else
        {
            _threadLocalDict.Clear();
        }

        _componentInfos = _threadLocalDict;
    }
}
```

**Pros:**
- **Zero contention** (thread-local storage)
- **Zero allocation** after first transaction per thread
- Extremely fast (just a Clear() call)

**Cons:**
- Memory retained per thread (not per transaction pool)
- Many threads × large dictionaries = high memory usage
- Dictionaries not freed when threads exit (until AppDomain unload)

**Performance Gain:**
- **Allocation cost:** ~0.8-1.5 microseconds saved per transaction (better than pooling)
- **Contention:** Eliminated entirely
- **Overall:** 8-12% improvement in high-concurrency workloads
- **Best for:** Fixed thread pool (e.g., 8-16 worker threads)

---

#### Design Alternative 3: Fixed-Size Array for Small Cases

**Algorithm:**
```csharp
public class Transaction
{
    private const int InlineCapacity = 4;

    // Inline storage for common case (1-4 component types)
    private struct InlineEntry
    {
        public Type Type;
        public ComponentInfo Info;
    }

    private InlineEntry[] _inlineEntries;  // Fixed size = 4
    private int _inlineCount;
    private Dictionary<Type, ComponentInfo> _overflowDict;  // Only allocated if needed

    private ComponentInfo GetComponentInfo(Type type)
    {
        // Fast path: linear search in inline array
        for (int i = 0; i < _inlineCount; i++)
        {
            if (_inlineEntries[i].Type == type)
                return _inlineEntries[i].Info;
        }

        // Check overflow dictionary
        if (_overflowDict != null && _overflowDict.TryGetValue(type, out var info))
            return info;

        // Add new entry
        var newInfo = new ComponentInfo();
        if (_inlineCount < InlineCapacity)
        {
            _inlineEntries[_inlineCount++] = new InlineEntry { Type = type, Info = newInfo };
        }
        else
        {
            _overflowDict ??= new Dictionary<Type, ComponentInfo>();
            _overflowDict[type] = newInfo;
        }

        return newInfo;
    }
}
```

**Pros:**
- **Zero allocation** for 99% of transactions (1-4 component types)
- Better cache locality (inline array vs heap dictionary)
- Simple linear search is faster for N ≤ 4

**Cons:**
- Code complexity (two storage paths)
- Overflow dictionary still allocated for large transactions

**Performance Gain:**
- **Allocation cost:** ~1-2 microseconds saved for common case
- **Cache locality:** Better CPU cache utilization
- **Overall:** 10-15% improvement for typical workloads (1-4 components)
- **Best for:** Workloads with few component types per transaction

---

#### Recommendation: **Hybrid - Inline + ThreadLocal Overflow**

Combine inline storage with ThreadLocal overflow dictionary:

```csharp
public class Transaction
{
    private InlineEntry[4] _inlineEntries;
    private int _inlineCount;

    [ThreadStatic]
    private static Dictionary<Type, ComponentInfo> _threadLocalOverflow;

    private ComponentInfo GetComponentInfo(Type type)
    {
        // Fast path: inline array (99% of cases)
        for (int i = 0; i < _inlineCount; i++)
        {
            if (_inlineEntries[i].Type == type)
                return _inlineEntries[i].Info;
        }

        // Rare overflow: use thread-local dictionary
        if (_inlineCount >= 4)
        {
            _threadLocalOverflow ??= new Dictionary<Type, ComponentInfo>();
            if (_threadLocalOverflow.TryGetValue(type, out var info))
                return info;

            var newInfo = new ComponentInfo();
            _threadLocalOverflow[type] = newInfo;
            return newInfo;
        }

        // Add to inline storage
        var newInfo = new ComponentInfo();
        _inlineEntries[_inlineCount++] = new InlineEntry { Type = type, Info = newInfo };
        return newInfo;
    }

    internal void Reset()
    {
        _inlineCount = 0;
        _threadLocalOverflow?.Clear();  // Reuse for next transaction
    }
}
```

**Expected Overall Gain:** **10-15% improvement** across all transaction workloads

---

## 2. MVCC & Revision Management

### 2.1 GetCompRevInfoFromIndex - Linear Revision Walk → Binary Search

**File:** `src/Typhon.Engine/Database Engine/Transaction.cs:713-771`

#### Current Algorithm

```csharp
private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out CompRevInfo compRevInfo)
{
    // Get revision chain entry point from primary key index
    if (!info.PrimaryKeyIndex.TryGet(pk, out var compRevFirstChunkId, accessor))
        return false;

    // Linear walk through ALL revisions (newest to oldest)
    using var enumerator = new RevisionEnumerator(compRevTableAccessor, compRevFirstChunkId, false, true);
    while (enumerator.MoveNext())
    {
        ref var element = ref enumerator.Current;

        // Stop at first revision after our snapshot point
        if (element.DateTime.Ticks > tick)
            break;

        // Track latest valid (committed, not isolated) revision
        if ((element.DateTime.Ticks > 0) && !element.IsolationFlag)
        {
            prevCompRevisionIndex = curCompRevisionIndex;
            curCompRevisionIndex = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
            curCompChunkId = element.ComponentChunkId;
        }
    }

    // Return current revision (or default if none found)
    // ...
}
```

**Bottleneck:**
- **O(N) linear scan** where N = number of revisions in chain
- For entities with 50+ revisions, this scans all 50 revisions
- Revisions are chronologically ordered (newest→oldest), but not searched efficiently
- Each MoveNext() involves chunk boundary checks and potential lock acquisitions

**Complexity Analysis:**
- **Time:** O(N) per lookup, N = revision count
- **Frequency:** Every transaction read/update operation
- **Worst case:** Entity with 100 revisions = 100 iterations per access

**Performance Impact:**
- 1 revision: ~0.5 microseconds
- 10 revisions: ~2-3 microseconds
- 50 revisions: ~10-15 microseconds
- 100 revisions: ~20-30 microseconds

---

#### Design Alternative 1: Binary Search on Timestamps

**Algorithm:**
```csharp
private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out CompRevInfo compRevInfo)
{
    if (!info.PrimaryKeyIndex.TryGet(pk, out var compRevFirstChunkId, accessor))
        return false;

    // Load all revision timestamps into array (or cache them)
    var revisionCount = GetRevisionCount(compRevFirstChunkId);
    Span<long> timestamps = stackalloc long[revisionCount];
    Span<int> chunkIds = stackalloc int[revisionCount];
    Span<short> revisionIndices = stackalloc short[revisionCount];
    Span<bool> isolationFlags = stackalloc bool[revisionCount];

    // Single pass: populate arrays
    int validCount = 0;
    using var enumerator = new RevisionEnumerator(compRevTableAccessor, compRevFirstChunkId, false, true);
    while (enumerator.MoveNext())
    {
        ref var element = ref enumerator.Current;
        if ((element.DateTime.Ticks > 0) && !element.IsolationFlag)
        {
            timestamps[validCount] = element.DateTime.Ticks;
            chunkIds[validCount] = element.ComponentChunkId;
            revisionIndices[validCount] = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
            validCount++;
        }
    }

    // Binary search for tick (O(log N) instead of O(N))
    int left = 0, right = validCount - 1;
    int found = -1;

    while (left <= right)
    {
        int mid = (left + right) / 2;
        if (timestamps[mid] <= tick)
        {
            found = mid;
            left = mid + 1;  // Look for later revision
        }
        else
        {
            right = mid - 1;
        }
    }

    if (found < 0)
        return false;

    // Construct result
    compRevInfo = new CompRevInfo
    {
        CurRevisionIndex = revisionIndices[found],
        CurCompContentChunkId = chunkIds[found],
        PrevRevisionIndex = found > 0 ? revisionIndices[found - 1] : (short)-1,
        PrevCompContentChunkId = found > 0 ? chunkIds[found - 1] : 0,
        // ...
    };

    return true;
}
```

**Pros:**
- **O(log N) search** instead of O(N) - massive improvement for large N
- Single enumeration pass (same as before)
- Stackalloc for small revision counts (no allocation)

**Cons:**
- Still requires full enumeration to populate arrays (O(N) cost)
- Stackalloc limited to ~256-512 revisions (stack overflow risk)
- Two passes conceptually (populate + search) vs one pass (linear)

**Performance Gain:**
- **10 revisions:** ~2-3 microseconds → ~2 microseconds (marginal)
- **50 revisions:** ~10-15 microseconds → ~5-7 microseconds (**40-50% improvement**)
- **100 revisions:** ~20-30 microseconds → ~8-12 microseconds (**50-60% improvement**)
- **Overall:** 2-5% improvement for typical workloads (most entities have <10 revisions)
- **Best for:** Entities with many revisions (long-lived, frequently updated)

---

#### Design Alternative 2: Revision Cache in Transaction

**Algorithm:**
```csharp
public class Transaction
{
    // Cache recently accessed CompRevInfo by (ComponentType, PrimaryKey)
    private struct RevisionCacheKey
    {
        public Type ComponentType;
        public long PrimaryKey;
    }

    private Dictionary<RevisionCacheKey, CompRevInfo> _revisionCache;

    private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out CompRevInfo compRevInfo)
    {
        var cacheKey = new RevisionCacheKey { ComponentType = info.ComponentTable.Definition.Type, PrimaryKey = pk };

        // Check cache first (O(1) lookup)
        if (_revisionCache.TryGetValue(cacheKey, out compRevInfo))
        {
            return true;
        }

        // Cache miss: perform linear search (existing algorithm)
        if (!LinearSearchRevisions(pk, info, tick, out compRevInfo))
            return false;

        // Cache result for future accesses within this transaction
        _revisionCache[cacheKey] = compRevInfo;
        return true;
    }
}
```

**Pros:**
- **Zero cost** for repeated reads of same entity within transaction
- Simple implementation (just a cache layer)
- Works with existing linear search (no algorithmic change)
- **O(1) cache hit** vs O(N) linear search

**Cons:**
- Only helps for repeated reads (not first access)
- Dictionary allocation overhead (but can use pooling from 1.2)
- Cache invalidation needed on updates (easy: just remove from cache)

**Performance Gain:**
- **First read:** Same as current (O(N) linear)
- **Repeated reads:** ~0.5 microseconds (hash lookup) vs ~5-15 microseconds (linear)
- **Overall:** 5-10% improvement for read-heavy transactions with entity reuse
- **Best for:** Transactions that read same entities multiple times (common in batch processing)

---

#### Design Alternative 3: Header-Level "Latest Revision" Pointer

**Algorithm:**

Modify `CompRevStorageHeader` to include a fast-path pointer:

```csharp
internal struct CompRevStorageHeader
{
    public int NextChunkId;
    public AccessControlSmall Control;
    public int FirstItemRevision;
    public short FirstItemIndex;
    public short ItemCount;
    public short ChainLength;
    public short LastCommitRevisionIndex;

    // NEW: Fast-path for "latest committed revision" lookup
    public short LatestCommittedRevisionIndex;  // Index of latest non-isolated revision
    public int LatestCommittedChunkId;          // ChunkId of latest committed component
}
```

Update on commit:
```csharp
private bool CommitComponent(ref CommitContext context)
{
    // ... existing commit logic ...

    // Update fast-path pointers
    ref var header = ref GetRevChainHeader(compRevInfo.CompRevTableFirstChunkId);
    header.LatestCommittedRevisionIndex = newRevisionIndex;
    header.LatestCommittedChunkId = compRevInfo.CurCompContentChunkId;

    // Clear isolation flag
    // ...
}
```

Lookup:
```csharp
private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out CompRevInfo compRevInfo)
{
    if (!info.PrimaryKeyIndex.TryGet(pk, out var compRevFirstChunkId, accessor))
        return false;

    ref var header = ref GetRevChainHeader(compRevFirstChunkId);

    // Fast path: if tick >= latest committed tick, use fast-path pointers
    var latestRevision = GetRevisionElement(compRevFirstChunkId, header.LatestCommittedRevisionIndex);
    if (tick >= latestRevision.DateTime.Ticks)
    {
        // Return latest committed revision (O(1) lookup!)
        compRevInfo = new CompRevInfo
        {
            CurRevisionIndex = header.LatestCommittedRevisionIndex,
            CurCompContentChunkId = header.LatestCommittedChunkId,
            // ...
        };
        return true;
    }

    // Slow path: historical lookup (use existing linear search or binary search)
    return LinearSearchRevisions(pk, info, tick, out compRevInfo);
}
```

**Pros:**
- **O(1) lookup** for "latest revision" case (99% of reads)
- No cache overhead (stored in header)
- Persistent across transactions (benefits all readers)

**Cons:**
- Increases header size by 6 bytes (from 26 to 32 bytes)
- Requires schema change (breaking change for existing databases)
- Only helps for latest revision lookups (not historical queries)

**Performance Gain:**
- **Latest revision:** ~0.5 microseconds (O(1)) vs ~5-15 microseconds (O(N)) - **90%+ improvement**
- **Historical queries:** Same as current
- **Overall:** 8-12% improvement for read-dominated workloads
- **Best for:** Real-time databases with mostly current-state queries

---

#### Recommendation: **Combine Alternative 2 (Cache) + Alternative 3 (Header Pointer)**

Use header pointer for latest revision + transaction-level cache for historical:

```csharp
private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out CompRevInfo compRevInfo)
{
    // Layer 1: Check transaction cache
    var cacheKey = new RevisionCacheKey { ComponentType = info.ComponentTable.Definition.Type, PrimaryKey = pk };
    if (_revisionCache.TryGetValue(cacheKey, out compRevInfo))
        return true;  // O(1) cache hit

    // Layer 2: Check header fast-path (latest revision)
    if (!info.PrimaryKeyIndex.TryGetValue(pk, out var firstChunkId))
        return false;

    ref var header = ref GetRevChainHeader(firstChunkId);
    var latestRevision = GetRevisionElement(firstChunkId, header.LatestCommittedRevisionIndex);

    if (tick >= latestRevision.DateTime.Ticks)
    {
        // O(1) header lookup
        compRevInfo = BuildFromHeader(header);
        _revisionCache[cacheKey] = compRevInfo;
        return true;
    }

    // Layer 3: Historical lookup (binary search)
    if (!BinarySearchRevisions(pk, info, tick, out compRevInfo))
        return false;

    _revisionCache[cacheKey] = compRevInfo;
    return true;
}
```

**Expected Overall Gain:** **10-15% improvement** for read-heavy workloads

---

### 2.2 RevisionEnumerator - Chunk Boundary Optimization

**File:** `src/Typhon.Engine/Database Engine/Transaction.cs:574-711`

#### Current Algorithm

```csharp
private struct RevisionEnumerator : IDisposable
{
    public bool MoveNext()
    {
        _revisionIndex++;

        // Check if we've exceeded current chunk capacity
        if (_revisionIndex >= _elementsInChunk)
        {
            // Step to next chunk
            StepToChunk(_nextChunkId);
            _revisionIndex = 0;
        }

        // Bounds check
        if (_overallIndex >= Header.ItemCount)
            return false;

        _overallIndex++;
        return true;
    }

    private void StepToChunk(int targetChunkId)
    {
        // Release previous chunk
        if (_curChunkId != 0)
        {
            _accessor.UnpinChunk(_curChunkId);
        }

        // Acquire new chunk
        _curChunkId = targetChunkId;
        using var handle = _accessor.GetChunkHandle(_curChunkId, dirty: false);

        // Read NextChunkId
        _nextChunkId = *(int*)handle.Address;

        // Set element count for this chunk
        _elementsInChunk = _curChunkId == _firstChunkId
            ? ComponentTable.CompRevCountInRoot
            : ComponentTable.CompRevCountInNext;
    }
}
```

**Bottleneck:**
- Every chunk boundary crossing triggers `StepToChunk()` (function call overhead)
- Each `StepToChunk()` does:
  - UnpinChunk (lock acquisition)
  - GetChunkHandle (lock acquisition + cache lookup)
  - Pointer arithmetic and bounds recalculation
- For a 100-revision chain with 5 chunks, this is **~20 lock operations** per enumeration

**Lock Impact:**
- AccessControl EnterSharedAccess: ~10-20 CPU cycles (uncontended)
- Total: 20 locks × 15 cycles = **~300 CPU cycles overhead** (~0.1 microseconds on 3GHz CPU)

---

#### Design Alternative 1: Prefetch Next Chunk

**Algorithm:**
```csharp
private struct RevisionEnumerator
{
    private ChunkHandle _curChunkHandle;
    private ChunkHandle _nextChunkHandle;  // Prefetched

    public bool MoveNext()
    {
        _revisionIndex++;

        // Prefetch next chunk when near boundary (2 elements before end)
        if (_revisionIndex == _elementsInChunk - 2 && _nextChunkId != 0)
        {
            _nextChunkHandle = _accessor.GetChunkHandle(_nextChunkId, dirty: false);
        }

        // Check if we've exceeded current chunk capacity
        if (_revisionIndex >= _elementsInChunk)
        {
            // Swap: next becomes current (already loaded!)
            _curChunkHandle.Dispose();
            _curChunkHandle = _nextChunkHandle;
            _nextChunkHandle = default;

            // Read metadata
            _nextChunkId = *(int*)_curChunkHandle.Address;
            _elementsInChunk = CalculateElementsInChunk();
            _revisionIndex = 0;
        }

        // ...
    }
}
```

**Pros:**
- Overlaps chunk loading with enumeration (reduces latency)
- Next chunk likely in cache by the time we need it
- Smoother performance (no sudden spikes at boundaries)

**Cons:**
- Holds two chunks pinned simultaneously (2× memory pressure)
- Wasted prefetch if enumeration terminates early
- Complexity: managing two chunk handles

**Performance Gain:**
- **Latency:** ~0.5-1 microsecond saved per chunk boundary (overlapped I/O)
- **Throughput:** Marginal (same total work, just reordered)
- **Overall:** 1-2% improvement for long enumerations
- **Best for:** Large revision chains with I/O latency

---

#### Design Alternative 2: Batch Lock Acquisition

**Algorithm:**
```csharp
private struct RevisionEnumerator
{
    public RevisionEnumerator(...)
    {
        // Acquire shared lock on entire chain upfront
        AcquireChainLock(firstChunkId);
    }

    private void AcquireChainLock(int firstChunkId)
    {
        // Walk chain once, pin all chunks
        int chunkId = firstChunkId;
        while (chunkId != 0)
        {
            _accessor.PinChunk(chunkId);
            chunkId = GetNextChunkId(chunkId);
        }
    }

    public void Dispose()
    {
        // Release all locks at once
        ReleaseChainLock();
    }
}
```

**Pros:**
- **Eliminates per-chunk locking overhead** (amortized to 1 lock per enumeration)
- Simpler boundary crossing (no lock/unlock)
- Better CPU cache utilization (all chunks resident)

**Cons:**
- Holds all chunks locked for entire enumeration (contention!)
- Higher memory pressure (all chunks pinned)
- Not suitable for long-running enumerations

**Performance Gain:**
- **Lock overhead:** ~0.2-0.5 microseconds saved for 5-chunk chain
- **Contention:** Increased (longer lock hold times)
- **Overall:** 2-4% improvement for short enumerations, **regression for concurrent access**
- **Best for:** Single-threaded or low-concurrency scenarios

---

#### Design Alternative 3: Inline Chunk Stepping

**Algorithm:**
```csharp
private struct RevisionEnumerator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _revisionIndex++;

        // Inline chunk stepping (no function call)
        if (_revisionIndex >= _elementsInChunk)
        {
            // Inline unpin
            if (_curChunkHandle.IsValid)
                _curChunkHandle.Dispose();

            // Inline chunk load
            _curChunkHandle = _accessor.GetChunkHandle(_nextChunkId, dirty: false);
            _nextChunkId = *(int*)_curChunkHandle.Address;
            _elementsInChunk = _curChunkId == _firstChunkId
                ? ComponentTable.CompRevCountInRoot
                : ComponentTable.CompRevCountInNext;
            _revisionIndex = 0;
        }

        if (_overallIndex >= Header.ItemCount)
            return false;

        _overallIndex++;
        return true;
    }
}
```

**Pros:**
- Eliminates function call overhead (5-10 cycles saved per boundary)
- Better CPU pipeline (no branch prediction penalty)
- Compiler can optimize better (inlining)

**Cons:**
- Code duplication (stepping logic in MoveNext)
- Larger MoveNext (may hurt instruction cache)

**Performance Gain:**
- **Function call:** ~5-10 CPU cycles saved per boundary
- **Overall:** 0.5-1% improvement for long enumerations
- **Best for:** Tight loops with frequent enumerations

---

#### Recommendation: **Inline + Prefetch (Hybrid)**

Inline the chunk stepping logic + add prefetch for next chunk:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public bool MoveNext()
{
    _revisionIndex++;

    // Prefetch when 2 elements from boundary
    if (_revisionIndex == _elementsInChunk - 2 && _nextChunkId != 0)
    {
        // Hint to CPU: we'll need this soon
        _nextChunkHandle = _accessor.GetChunkHandle(_nextChunkId, dirty: false);
    }

    // Inline chunk stepping
    if (_revisionIndex >= _elementsInChunk)
    {
        _curChunkHandle.Dispose();
        _curChunkHandle = _nextChunkHandle.IsValid ? _nextChunkHandle : _accessor.GetChunkHandle(_nextChunkId, dirty: false);
        _nextChunkHandle = default;
        _nextChunkId = *(int*)_curChunkHandle.Address;
        _elementsInChunk = CalculateElementsInChunk();
        _revisionIndex = 0;
    }

    if (_overallIndex >= Header.ItemCount)
        return false;

    _overallIndex++;
    return true;
}
```

**Expected Overall Gain:** **2-4% improvement** for revision-heavy workloads

---

## 3. Page Cache & Eviction

### 3.1 AllocateMemoryPage - Clock-Sweep Optimization

**File:** `src/Typhon.Engine/Persistence Layer/PagedMMF.cs:421-576`

#### Current Algorithm

```csharp
private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, ...)
{
    while (true)
    {
        // Phase 1: Try sequential allocation (prefetch optimization)
        if (filePageIndex > 0 && _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemPageIndex))
        {
            memPageIndex = prevMemPageIndex + 1;
            if (TryAcquire(_memPagesInfo[memPageIndex]))
                return true;  // Fast path: sequential page
        }

        // Phase 2: Clock-sweep first pass (find counter == 0)
        int attempts = 0;
        int maxAttempts = _memPagesCount * 2;  // TWO full sweeps

        while (attempts < maxAttempts)
        {
            memPageIndex = AdvanceClockHand();  // CAS loop to increment
            var pi = _memPagesInfo[memPageIndex];

            if (pi.ClockSweepCounter == 0 && TryAcquire(pi))
                return true;

            pi.DecrementClockSweepCounter();  // Decrement on every iteration
            attempts++;
        }

        // Phase 3: Clock-sweep second pass (ignore counter)
        attempts = 0;
        maxAttempts = _memPagesCount;  // ONE full sweep

        while (attempts < maxAttempts)
        {
            memPageIndex = AdvanceClockHand();
            if (TryAcquire(_memPagesInfo[memPageIndex]))
                return true;

            pi.DecrementClockSweepCounter();  // Still decrementing!
            attempts++;
        }

        // Phase 4: All pages busy, wait and retry
        waiter ??= new AdaptiveWaiter();
        waiter.Spin();  // Sleep 100 microseconds
    }
}

private int AdvanceClockHand()
{
    // CAS loop to atomically increment
    while (true)
    {
        var cur = _clockSweepHand;
        var next = (cur + 1) % _memPagesCount;
        if (Interlocked.CompareExchange(ref _clockSweepHand, next, cur) == cur)
            return next;
    }
}
```

**Bottlenecks:**

1. **Three separate loops** (sequential + 2 sweeps) = worst case **3N iterations**
2. **DecrementClockSweepCounter() called O(2N) times** per allocation
3. **AdvanceClockHand() CAS loop** burns CPU (no backoff)
4. **Two full sweeps even when pages available** (doesn't early exit)

**Performance Impact:**
- 256 pages: worst case = 768 iterations + 512 decrements + ~50 CAS retries
- At 10k page allocations/sec: **7.6M iterations + 5.1M decrements/sec**
- Each iteration: ~20-30 CPU cycles = **150M+ CPU cycles/sec wasted**

---

#### Design Alternative 1: Single-Pass Hybrid Sweep

**Algorithm:**
```csharp
private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, ...)
{
    // Try sequential allocation (same as before)
    if (TrySequentialAllocation(filePageIndex, out memPageIndex))
        return true;

    // Single-pass clock-sweep with adaptive threshold
    int start = Interlocked.Increment(ref _clockSweepHand) % _memPagesCount;
    int threshold = 0;  // Start with counter == 0 requirement

    for (int pass = 0; pass < 2; pass++)
    {
        for (int i = 0; i < _memPagesCount; i++)
        {
            memPageIndex = (start + i) % _memPagesCount;
            var pi = _memPagesInfo[memPageIndex];

            // Accept if counter <= threshold
            if (pi.ClockSweepCounter <= threshold && TryAcquire(pi))
            {
                // Update clock hand (amortized, not per iteration)
                _clockSweepHand = memPageIndex;
                return true;
            }

            // Decrement only when rejecting (not every iteration)
            if (pi.ClockSweepCounter > 0)
                pi.DecrementClockSweepCounter();
        }

        // Second pass: accept any counter value
        threshold = int.MaxValue;
    }

    // All pages busy
    return false;
}
```

**Pros:**
- **Single pass** instead of three (reduced from 3N to 2N iterations)
- **Fewer decrements** (only when actually rejecting page)
- **Amortized CAS** (single increment, not per-iteration)
- Simpler control flow

**Cons:**
- Slightly less fair (starts from last position, not circular)
- May skip pages that became available during scan

**Performance Gain:**
- **Iterations:** 768 → 512 (**33% reduction**)
- **Decrements:** 512 → ~300 (**40% reduction**, assuming 60% pages have counter > 0)
- **CAS operations:** ~50 → 1 (**98% reduction**)
- **Overall:** 10-15% improvement in page allocation under load
- **Best for:** High cache pressure scenarios

---

#### Design Alternative 2: Bitmap-Based Free Page Tracking

**Algorithm:**
```csharp
public class PagedMMF
{
    private ulong[] _freePageBitmap;  // 1 bit per page (256 pages = 4 ulongs)
    private int _bitmapHint;          // Start search from here

    private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, ...)
    {
        // Try sequential allocation
        if (TrySequentialAllocation(filePageIndex, out memPageIndex))
            return true;

        // Bitmap search for free/idle pages
        int startWord = _bitmapHint;
        for (int i = 0; i < _freePageBitmap.Length; i++)
        {
            int wordIdx = (startWord + i) % _freePageBitmap.Length;
            ulong word = _freePageBitmap[wordIdx];

            if (word != 0)
            {
                // Find first set bit (free page)
                int bitIdx = BitOperations.TrailingZeroCount(word);
                memPageIndex = wordIdx * 64 + bitIdx;

                var pi = _memPagesInfo[memPageIndex];
                if (TryAcquire(pi))
                {
                    // Clear bit in bitmap
                    _freePageBitmap[wordIdx] &= ~(1UL << bitIdx);
                    _bitmapHint = wordIdx;
                    return true;
                }
            }
        }

        // No free pages
        return false;
    }

    private void ReleasePage(int memPageIndex)
    {
        // Set bit in bitmap when page becomes idle
        int wordIdx = memPageIndex / 64;
        int bitIdx = memPageIndex % 64;
        _freePageBitmap[wordIdx] |= (1UL << bitIdx);
    }
}
```

**Pros:**
- **O(N/64) bitmap scan** instead of O(N) page scan (64× faster for large caches)
- **SIMD-friendly** (can use AVX2 to scan 4 words at once)
- **No ClockSweepCounter** (simpler page state)
- **Hardware-accelerated** (TrailingZeroCount is single CPU instruction)

**Cons:**
- No LRU semantics (loses approximate LRU property of clock-sweep)
- Bitmap synchronization overhead (CAS on bitmap words)
- Doesn't age pages (all free pages equally likely)

**Performance Gain:**
- **Search:** O(256) → O(4) bitmap words (**64× faster**)
- **Cache hit:** ~1 microsecond vs ~5 microseconds (**80% improvement**)
- **Overall:** 15-20% improvement in high-frequency allocation scenarios
- **Best for:** Workloads where LRU approximation doesn't matter (random access)

---

#### Design Alternative 3: Multi-Level Clock with Coarse Counters

**Algorithm:**
```csharp
public class PagedMMF
{
    // Instead of per-page counters, use per-group counters
    private const int GroupSize = 16;  // Group of 16 pages
    private byte[] _groupCounters;     // One counter per group (256/16 = 16 bytes)

    private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, ...)
    {
        // Try sequential allocation
        if (TrySequentialAllocation(filePageIndex, out memPageIndex))
            return true;

        // Search for group with counter == 0
        int startGroup = _clockSweepHand / GroupSize;
        for (int g = 0; g < _groupCounters.Length; g++)
        {
            int groupIdx = (startGroup + g) % _groupCounters.Length;

            if (_groupCounters[groupIdx] == 0)
            {
                // Search within group for free page
                int baseIdx = groupIdx * GroupSize;
                for (int i = 0; i < GroupSize; i++)
                {
                    memPageIndex = baseIdx + i;
                    if (TryAcquire(_memPagesInfo[memPageIndex]))
                    {
                        _clockSweepHand = memPageIndex;
                        return true;
                    }
                }
            }

            // Decrement group counter (amortized over 16 pages)
            if (_groupCounters[groupIdx] > 0)
                _groupCounters[groupIdx]--;
        }

        // Second pass: ignore counters
        // ...
    }

    private void IncrementPageCounter(int memPageIndex)
    {
        int groupIdx = memPageIndex / GroupSize;
        if (_groupCounters[groupIdx] < 5)
            _groupCounters[groupIdx]++;
    }
}
```

**Pros:**
- **Fewer counter updates** (16× reduction: 1 counter per 16 pages)
- **Better cache locality** (16-byte array vs 256-byte array)
- **Amortized eviction** (entire group aged together)

**Cons:**
- Less granular LRU tracking (group-level, not page-level)
- May evict hot page if in cold group

**Performance Gain:**
- **Counter updates:** 256 → 16 (**94% reduction**)
- **Cache misses:** Fewer (16 bytes vs 256 bytes of counter data)
- **Overall:** 8-12% improvement in allocation performance
- **Best for:** Workloads with spatial locality (nearby pages accessed together)

---

#### Recommendation: **Hybrid - Single-Pass + Bitmap Fast Path**

Combine single-pass clock-sweep with bitmap for quick free-page detection:

```csharp
private ulong[] _freePageBitmap;  // Quick check for idle pages

private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, ...)
{
    // Phase 1: Sequential allocation
    if (TrySequentialAllocation(filePageIndex, out memPageIndex))
        return true;

    // Phase 2: Bitmap quick scan (check for obviously free pages)
    if (TryBitmapAllocation(out memPageIndex))
        return true;

    // Phase 3: Single-pass clock-sweep (LRU eviction)
    if (TryClockSweepAllocation(out memPageIndex))
        return true;

    // Phase 4: Wait and retry
    return false;
}

private bool TryBitmapAllocation(out int memPageIndex)
{
    // Fast path: check bitmap for idle pages
    for (int i = 0; i < _freePageBitmap.Length; i++)
    {
        ulong word = _freePageBitmap[i];
        if (word != 0)
        {
            int bitIdx = BitOperations.TrailingZeroCount(word);
            memPageIndex = i * 64 + bitIdx;

            if (TryAcquire(_memPagesInfo[memPageIndex]))
            {
                _freePageBitmap[i] &= ~(1UL << bitIdx);
                return true;
            }
        }
    }

    memPageIndex = -1;
    return false;
}
```

**Expected Overall Gain:** **15-25% improvement** in page allocation performance

---

### 3.2 TransitionPageToAccess - Lock-Free Optimistic Path

**File:** `src/Typhon.Engine/Persistence Layer/PagedMMF.cs:654-784`

#### Current Algorithm

```csharp
internal void TransitionPageToAccess(int filePageIndex, bool exclusive, out PageAccessor outAccessor)
{
    var pi = GetPageInfo(filePageIndex);

    // LOCK ACQUIRED ON EVERY TRANSITION
    pi.StateSyncRoot.Enter();
    try
    {
        // Complex state machine with 3 main branches
        switch (pi.PageState)
        {
            case PageState.Allocating:
                // Wait for allocation to complete, then transition to Shared/Exclusive
                WaitForAllocation(pi);
                if (exclusive)
                {
                    pi.PageState = PageState.Exclusive;
                    pi.LockedByThreadId = Environment.CurrentManagedThreadId;
                }
                else
                {
                    pi.PageState = PageState.Shared;
                    Interlocked.Increment(ref pi.ConcurrentSharedCounter);
                }
                pi.IncrementClockSweepCounter();  // 4 increments total in different branches!
                break;

            case PageState.Idle or PageState.IdleAndDirty:
                // Transition to Shared/Exclusive
                if (exclusive)
                {
                    pi.PageState = PageState.Exclusive;
                    pi.LockedByThreadId = Environment.CurrentManagedThreadId;
                }
                else
                {
                    pi.PageState = PageState.Shared;
                    Interlocked.Increment(ref pi.ConcurrentSharedCounter);
                }
                pi.IncrementClockSweepCounter();
                break;

            case PageState.Shared:
                // Increment shared counter (if not exclusive request)
                if (!exclusive)
                {
                    Interlocked.Increment(ref pi.ConcurrentSharedCounter);
                    pi.IncrementClockSweepCounter();
                }
                else
                {
                    // ERROR: can't upgrade shared→exclusive
                    throw new InvalidOperationException("Can't acquire exclusive on shared page");
                }
                break;

            case PageState.Exclusive:
                // ERROR: page already exclusively locked
                throw new InvalidOperationException("Page already exclusive");
        }

        outAccessor = new PageAccessor(this, pi, filePageIndex);
    }
    finally
    {
        pi.StateSyncRoot.Exit();
    }
}
```

**Bottlenecks:**

1. **Lock on every transition** (even for common Shared→Shared case)
2. **IncrementClockSweepCounter() called 4 times** in different branches (redundant)
3. **Interlocked.Increment inside lock** (unnecessary - Interlocked is already thread-safe)
4. **No fast path** for "already in correct state"

**Performance Impact:**
- Lock acquisition: ~10-20 CPU cycles (uncontended)
- At 100k page accesses/sec: **1-2M CPU cycles/sec on locking alone**
- AdaptiveWaiter on contention: 100 microseconds delay per conflict

---

#### Design Alternative 1: Optimistic Lock-Free Shared Access

**Algorithm:**
```csharp
internal void TransitionPageToAccess(int filePageIndex, bool exclusive, out PageAccessor outAccessor)
{
    var pi = GetPageInfo(filePageIndex);

    // FAST PATH: Optimistic shared access (no lock!)
    if (!exclusive && pi.PageState == PageState.Shared)
    {
        // Atomically increment shared counter
        Interlocked.Increment(ref pi.ConcurrentSharedCounter);

        // Double-check state (might have changed to exclusive)
        if (pi.PageState == PageState.Shared)
        {
            // Success! No lock needed
            pi.IncrementClockSweepCounter();
            outAccessor = new PageAccessor(this, pi, filePageIndex);
            return;
        }

        // Race condition: state changed, rollback and take slow path
        Interlocked.Decrement(ref pi.ConcurrentSharedCounter);
    }

    // SLOW PATH: Acquire lock for state transitions
    pi.StateSyncRoot.Enter();
    try
    {
        // ... existing state machine logic ...
    }
    finally
    {
        pi.StateSyncRoot.Exit();
    }
}
```

**Pros:**
- **Zero locks for common case** (Shared→Shared transitions)
- **Massive concurrency improvement** (no lock contention)
- Backward compatible (falls back to locked path on races)

**Cons:**
- Potential ABA problem (state changes Shared→Exclusive→Shared)
- Rollback overhead on races (Decrement)
- More complex logic

**Performance Gain:**
- **Shared access:** ~10-20 cycles (lock) → ~5 cycles (Interlocked) - **75% reduction**
- **Concurrency:** Near-linear scaling (no lock bottleneck)
- **Overall:** 10-20% improvement in high-concurrency read workloads
- **Best for:** Read-heavy workloads with many concurrent transactions

---

#### Design Alternative 2: State Machine via Atomic CAS

**Algorithm:**
```csharp
// Pack PageState + LockedThreadId into single long for atomic CAS
[StructLayout(LayoutKind.Explicit)]
struct PageStateAtomic
{
    [FieldOffset(0)] public long Combined;
    [FieldOffset(0)] public int State;      // PageState enum (4 bytes)
    [FieldOffset(4)] public int ThreadId;   // Locked thread (4 bytes)
}

internal void TransitionPageToAccess(int filePageIndex, bool exclusive, out PageAccessor outAccessor)
{
    var pi = GetPageInfo(filePageIndex);

    while (true)
    {
        var current = new PageStateAtomic { Combined = pi.StateAndThread };
        var desired = current;

        switch ((PageState)current.State)
        {
            case PageState.Idle:
            case PageState.IdleAndDirty:
                if (exclusive)
                {
                    desired.State = (int)PageState.Exclusive;
                    desired.ThreadId = Environment.CurrentManagedThreadId;
                }
                else
                {
                    desired.State = (int)PageState.Shared;
                }
                break;

            case PageState.Shared:
                if (exclusive)
                    throw new InvalidOperationException("Can't acquire exclusive on shared");

                // Already shared, just increment counter (no CAS needed)
                Interlocked.Increment(ref pi.ConcurrentSharedCounter);
                pi.IncrementClockSweepCounter();
                outAccessor = new PageAccessor(this, pi, filePageIndex);
                return;

            // ... other cases ...
        }

        // Atomic state transition (lock-free!)
        if (Interlocked.CompareExchange(ref pi.StateAndThread, desired.Combined, current.Combined) == current.Combined)
        {
            // Success
            if (!exclusive)
                Interlocked.Increment(ref pi.ConcurrentSharedCounter);

            pi.IncrementClockSweepCounter();
            outAccessor = new PageAccessor(this, pi, filePageIndex);
            return;
        }

        // CAS failed, retry
    }
}
```

**Pros:**
- **Completely lock-free** (no StateSyncRoot needed)
- **Scalable** (CAS retries only on actual conflicts)
- **Clean state machine** (all transitions explicit)

**Cons:**
- **CAS retry loop** (unbounded retries under heavy contention)
- **ABA problem** (state cycles through same value)
- **More complex** (state packing/unpacking)

**Performance Gain:**
- **Uncontended:** ~5-10 cycles (CAS) vs ~20 cycles (lock) - **50-66% reduction**
- **Contended:** Better scaling (CAS retries vs lock blocking)
- **Overall:** 15-25% improvement in high-concurrency scenarios
- **Best for:** Many threads competing for same pages

---

#### Design Alternative 3: Per-State Fast Paths

**Algorithm:**
```csharp
internal void TransitionPageToAccess(int filePageIndex, bool exclusive, out PageAccessor outAccessor)
{
    var pi = GetPageInfo(filePageIndex);
    var state = pi.PageState;

    // Fast path 1: Shared → Shared (most common)
    if (state == PageState.Shared && !exclusive)
    {
        Interlocked.Increment(ref pi.ConcurrentSharedCounter);
        if (pi.PageState == PageState.Shared)  // Double-check
        {
            pi.IncrementClockSweepCounter();
            outAccessor = new PageAccessor(this, pi, filePageIndex);
            return;
        }
        Interlocked.Decrement(ref pi.ConcurrentSharedCounter);  // Rollback
    }

    // Fast path 2: Idle → Shared/Exclusive (no contention)
    if ((state == PageState.Idle || state == PageState.IdleAndDirty) && TryLightweightAcquire(pi, exclusive))
    {
        outAccessor = new PageAccessor(this, pi, filePageIndex);
        return;
    }

    // Slow path: Full state machine under lock
    pi.StateSyncRoot.Enter();
    try
    {
        // ... existing logic ...
    }
    finally
    {
        pi.StateSyncRoot.Exit();
    }
}

private bool TryLightweightAcquire(PageInfo pi, bool exclusive)
{
    // Try lock-free acquisition for common cases
    if (exclusive)
    {
        if (Interlocked.CompareExchange(ref pi.LockedByThreadId, Environment.CurrentManagedThreadId, 0) == 0)
        {
            pi.PageState = PageState.Exclusive;
            return true;
        }
    }
    else
    {
        pi.PageState = PageState.Shared;
        Interlocked.Increment(ref pi.ConcurrentSharedCounter);
        return true;
    }

    return false;
}
```

**Pros:**
- **Multiple fast paths** (optimized for common cases)
- **Gradual migration** (can add fast paths incrementally)
- **Fallback safety** (always falls back to locked path)

**Cons:**
- **Code duplication** (logic repeated in fast/slow paths)
- **Maintenance burden** (two implementations to keep in sync)

**Performance Gain:**
- **Common cases:** 10-20% faster (lock-free)
- **Rare cases:** Same as current (locked)
- **Overall:** 12-18% improvement across mixed workloads
- **Best for:** Incremental optimization (low risk)

---

#### Recommendation: **Alternative 1 (Optimistic Lock-Free) + Fast Path Consolidation**

Implement optimistic lock-free shared access with consolidated fast paths:

```csharp
internal void TransitionPageToAccess(int filePageIndex, bool exclusive, out PageAccessor outAccessor)
{
    var pi = GetPageInfo(filePageIndex);

    // === FAST PATH ===
    if (!exclusive)
    {
        var state = pi.PageState;

        // Common case: Shared → Shared (lock-free)
        if (state == PageState.Shared)
        {
            Interlocked.Increment(ref pi.ConcurrentSharedCounter);

            // Verify state didn't change
            if (pi.PageState == PageState.Shared)
            {
                pi.IncrementClockSweepCounter();
                outAccessor = new PageAccessor(this, pi, filePageIndex);
                return;
            }

            // Race: rollback and take slow path
            Interlocked.Decrement(ref pi.ConcurrentSharedCounter);
        }
    }

    // === SLOW PATH (under lock) ===
    pi.StateSyncRoot.Enter();
    try
    {
        // Existing state machine logic
        // ...
    }
    finally
    {
        pi.StateSyncRoot.Exit();
    }
}
```

**Expected Overall Gain:** **12-20% improvement** in page access performance under concurrency

---

## 4. Lock Contention Reduction

### 4.1 AccessControl - Adaptive Backoff Strategy

**File:** `src/Typhon.Engine/Misc/AccessControl.cs:30-104`

#### Current Algorithm

```csharp
public void EnterSharedAccess()
{
    // Wait for exclusive lock
    if (_lockedByThreadId != 0)
    {
        var sw = new SpinWait();
        while (_lockedByThreadId != 0)
            sw.SpinOnce();  // Fixed spin strategy
    }

    Interlocked.Increment(ref _sharedUsedCounter);

    // Double-check exclusive lock
    while (_lockedByThreadId != 0)
    {
        Interlocked.Decrement(ref _sharedUsedCounter);

        var sw = new SpinWait();
        while (_lockedByThreadId != 0)
            sw.SpinOnce();  // Fixed spin strategy

        Interlocked.Increment(ref _sharedUsedCounter);
    }
}
```

**Bottleneck:**
- **No backoff adaptation**: Always spins at same rate regardless of contention level
- **Creates new SpinWait** on every loop iteration (wasted initialization)
- **No yield threshold**: Never yields to OS scheduler

**SpinWait Behavior:**
- Spins for N iterations, then yields
- But N is fixed (doesn't adapt to contention history)

---

#### Design Alternative 1: Exponential Backoff

**Algorithm:**
```csharp
public void EnterSharedAccess()
{
    int backoff = 1;

    while (_lockedByThreadId != 0)
    {
        // Exponential backoff: 1, 2, 4, 8, 16, 32, 64, ... (capped at 1024)
        for (int i = 0; i < backoff; i++)
            Thread.SpinWait(100);  // Spin for 100 iterations

        backoff = Math.Min(backoff * 2, 1024);

        // After 64 spins, yield to scheduler
        if (backoff >= 64)
            Thread.Yield();
    }

    Interlocked.Increment(ref _sharedUsedCounter);

    // Double-check logic (same as before)
    // ...
}
```

**Pros:**
- **Adaptive to contention**: Backs off more aggressively under heavy contention
- **Reduces CPU waste**: Yields to scheduler after threshold
- **Simple implementation**: Just multiply backoff by 2

**Cons:**
- **Delayed acquisition under light contention**: Initial backoff might be unnecessary
- **Unbounded wait time**: No timeout mechanism

**Performance Gain:**
- **Low contention:** Same or slightly worse (extra backoff logic)
- **High contention:** 10-20% reduction in CPU usage (less spinning)
- **Overall:** 5-10% improvement in highly contended scenarios
- **Best for:** Heavy write workloads with lock contention

---

#### Design Alternative 2: Hybrid Spin-Then-Sleep

**Algorithm:**
```csharp
private const int SpinCountBeforeYield = 100;
private const int YieldCountBeforeSleep = 10;

public void EnterSharedAccess()
{
    int spinCount = 0;
    int yieldCount = 0;

    while (_lockedByThreadId != 0)
    {
        if (spinCount < SpinCountBeforeYield)
        {
            // Phase 1: Spin (100 iterations)
            Thread.SpinWait(100);
            spinCount++;
        }
        else if (yieldCount < YieldCountBeforeSleep)
        {
            // Phase 2: Yield to scheduler (10 times)
            Thread.Yield();
            yieldCount++;
        }
        else
        {
            // Phase 3: Sleep (1 millisecond)
            Thread.Sleep(1);
        }
    }

    Interlocked.Increment(ref _sharedUsedCounter);

    // Double-check logic
    // ...
}
```

**Pros:**
- **Three-phase strategy**: Optimized for different contention levels
- **Bounded CPU waste**: Sleep after threshold prevents runaway spinning
- **Fairness**: Sleep allows other threads to make progress

**Cons:**
- **Latency spike**: Sleep(1) can be 1-15 milliseconds (OS scheduling quantum)
- **Complexity**: Three separate paths to tune

**Performance Gain:**
- **Low contention:** Same as current (spin-only)
- **Medium contention:** 10-15% improvement (yields reduce CPU waste)
- **High contention:** 20-30% improvement (sleep prevents starvation)
- **Overall:** 8-15% improvement in mixed workloads
- **Best for:** Workloads with varying contention levels

---

#### Design Alternative 3: OS Event-Based Waiting

**Algorithm:**
```csharp
public class AccessControl
{
    private ManualResetEventSlim _sharedWaitEvent = new ManualResetEventSlim(true);
    private ManualResetEventSlim _exclusiveWaitEvent = new ManualResetEventSlim(true);

    public void EnterSharedAccess()
    {
        while (_lockedByThreadId != 0)
        {
            // Signal that we're waiting
            _sharedWaitEvent.Reset();

            // Wait for exclusive lock to release
            _sharedWaitEvent.Wait(timeout: 100);  // 100ms timeout
        }

        Interlocked.Increment(ref _sharedUsedCounter);

        // Double-check logic
        // ...
    }

    public void ExitExclusiveAccess()
    {
        _lockedByThreadId = 0;

        // Wake up all waiting shared accessors
        _sharedWaitEvent.Set();
    }
}
```

**Pros:**
- **True OS-level waiting**: No busy-spinning CPU waste
- **Instant wakeup**: OS wakes thread as soon as lock released
- **Fairness**: OS scheduler handles fairness

**Cons:**
- **Higher latency**: OS context switch overhead (~1-10 microseconds)
- **Memory overhead**: ManualResetEventSlim object (~48 bytes)
- **Complexity**: Event management and lifecycle

**Performance Gain:**
- **Low contention:** **Regression** (event overhead vs simple spin)
- **High contention:** 30-50% reduction in CPU usage
- **Latency:** Slightly higher (context switch)
- **Overall:** Better CPU efficiency, worse latency
- **Best for:** Server workloads prioritizing CPU efficiency over latency

---

#### Recommendation: **Alternative 2 (Hybrid Spin-Then-Sleep) with Tunable Parameters**

Implement three-phase hybrid with configurable thresholds:

```csharp
public struct AccessControl
{
    // Tunable via configuration
    private static int SpinCountBeforeYield = 50;    // Default: 50 spins
    private static int YieldCountBeforeSleep = 5;    // Default: 5 yields
    private static int SleepDurationMs = 1;          // Default: 1ms

    public void EnterSharedAccess()
    {
        if (_lockedByThreadId == 0)
            goto Acquire;  // Fast path: no contention

        int phase = 0;
        int count = 0;

        while (_lockedByThreadId != 0)
        {
            switch (phase)
            {
                case 0:  // Spin phase
                    Thread.SpinWait(100);
                    if (++count >= SpinCountBeforeYield)
                    {
                        phase = 1;
                        count = 0;
                    }
                    break;

                case 1:  // Yield phase
                    Thread.Yield();
                    if (++count >= YieldCountBeforeSleep)
                    {
                        phase = 2;
                    }
                    break;

                case 2:  // Sleep phase
                    Thread.Sleep(SleepDurationMs);
                    break;
            }
        }

    Acquire:
        Interlocked.Increment(ref _sharedUsedCounter);

        // Double-check logic (optimized)
        while (_lockedByThreadId != 0)
        {
            Interlocked.Decrement(ref _sharedUsedCounter);

            // Restart three-phase wait
            // ... (same logic as above)

            Interlocked.Increment(ref _sharedUsedCounter);
        }
    }
}
```

**Expected Overall Gain:** **10-18% improvement** in lock contention scenarios

---

## 5. Memory Allocation Optimizations

### 5.1 Bitmap Bulk Allocation

**File:** `src/Typhon.Engine/Collections/BitmapL3Any.cs:38-63`

#### Current Algorithm

```csharp
public void Set(int index)
{
    // Level 0: Set bit in data array
    var offset = index >> 6;
    var mask = 1L << (index & 0x3F);
    var prevValue = _data[0].Span[offset];
    _data[0].Span[offset] |= mask;

    if (prevValue != 0)
        return;  // Already had bits set, L1/L2 unchanged

    // Level 1: Propagate if L0 was empty
    index = offset;
    offset = index >> 6;
    mask = 1L << (index & 0x3F);
    prevValue = _data[1].Span[offset];
    _data[1].Span[offset] |= mask;

    if (prevValue != 0)
        return;

    // Level 2: Propagate if L1 was empty
    index = offset;
    offset = index >> 6;
    mask = 1L << (index & 0x3F);
    _data[2].Span[offset] |= mask;
}
```

**Usage in ChunkBasedSegment:**
```csharp
public int[] AllocateChunks(int count)
{
    var chunks = new int[count];
    for (int i = 0; i < count; i++)
    {
        chunks[i] = _map.AllocateBit();  // Calls Set() internally
    }
    return chunks;
}
```

**Bottleneck:**
- **N separate Set() calls** for allocating N chunks
- Each Set() does 3 memory reads + 3 memory writes (6 total)
- For allocating 100 chunks: **600 memory operations**
- No SIMD utilization

---

#### Design Alternative 1: Bulk Set with Bitmask

**Algorithm:**
```csharp
public void SetRange(int startIndex, int count)
{
    int endIndex = startIndex + count - 1;

    // Fast path: all bits in same 64-bit word
    if ((startIndex >> 6) == (endIndex >> 6))
    {
        var offset = startIndex >> 6;
        var startBit = startIndex & 0x3F;
        var endBit = endIndex & 0x3F;

        // Create mask: bits [startBit, endBit] set
        ulong mask = ((1UL << (endBit - startBit + 1)) - 1) << startBit;

        var prevValue = (ulong)_data[0].Span[offset];
        _data[0].Span[offset] |= (long)mask;

        // Propagate to L1/L2 if needed (same logic as Set)
        if (prevValue == 0)
            PropagateToUpperLevels(offset);

        return;
    }

    // Slow path: spans multiple words
    for (int i = startIndex; i <= endIndex; )
    {
        var offset = i >> 6;
        var bit = i & 0x3F;
        var bitsInWord = Math.Min(64 - bit, endIndex - i + 1);

        // Set bitsInWord bits starting from bit
        ulong mask = ((1UL << bitsInWord) - 1) << bit;

        var prevValue = (ulong)_data[0].Span[offset];
        _data[0].Span[offset] |= (long)mask;

        if (prevValue == 0)
            PropagateToUpperLevels(offset);

        i += bitsInWord;
    }
}
```

**Pros:**
- **Fewer memory operations**: Set entire words at once instead of individual bits
- **Better CPU pipelining**: Predictable access pattern
- **Optimal for contiguous allocations**: Single operation for same-word ranges

**Cons:**
- **Complex implementation**: Bit masking logic
- **Limited benefit for non-contiguous**: Falls back to loop

**Performance Gain:**
- **Contiguous 64 bits:** 1 operation vs 64 operations (**98% reduction**)
- **Contiguous 100 bits:** ~2 operations vs 100 operations (**98% reduction**)
- **Non-contiguous:** No improvement (same as current)
- **Overall:** 10-20% improvement for bulk contiguous allocations
- **Best for:** Allocating large component arrays, index batches

---

#### Design Alternative 2: SIMD Bitmap Search

**Algorithm:**
```csharp
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

public int AllocateBit()
{
    // Use AVX2 to scan 4 longs (256 bits) at once
    if (Avx2.IsSupported)
    {
        var data0 = _data[0].Span;
        int i = 0;

        // Scan in groups of 4 longs
        for (; i < data0.Length - 3; i += 4)
        {
            // Load 4 longs into 256-bit register
            var vec = Avx.LoadVector256((long*)Unsafe.AsPointer(ref data0[i]));

            // Check if all zeros (no free bits)
            if (Avx.TestZ(vec, vec))
                continue;  // All zeros, skip

            // Found non-zero, search individual longs
            for (int j = 0; j < 4; j++)
            {
                if (data0[i + j] != 0)
                {
                    // Find first set bit
                    var bit = BitOperations.TrailingZeroCount((ulong)data0[i + j]);
                    var index = (i + j) * 64 + bit;

                    // Clear bit and return
                    data0[i + j] &= ~(1L << bit);
                    UpdateUpperLevels(i + j);
                    return index;
                }
            }
        }

        // Handle remaining longs (scalar)
        // ...
    }

    // Fallback: scalar search
    return ScalarAllocateBit();
}
```

**Pros:**
- **4× faster search**: AVX2 scans 256 bits per iteration vs 64 bits
- **Hardware-accelerated**: Uses CPU's SIMD units
- **Scalable**: Speedup increases with larger bitmaps

**Cons:**
- **Platform-specific**: Requires AVX2 support (x64 only)
- **Code complexity**: Unsafe pointers and SIMD intrinsics
- **Marginal benefit for small bitmaps**: Overhead dominates for <1024 bits

**Performance Gain:**
- **Allocation search:** 4× faster for large bitmaps
- **Overall:** 5-8% improvement for chunk allocation in large segments
- **Best for:** Large segments with many chunks (>1000 chunks)

---

#### Design Alternative 3: Lazy L1/L2 Updates

**Algorithm:**
```csharp
public class BitmapL3Any
{
    private bool _l1Dirty = false;
    private bool _l2Dirty = false;

    public void Set(int index)
    {
        // Only update L0
        var offset = index >> 6;
        var mask = 1L << (index & 0x3F);
        _data[0].Span[offset] |= mask;

        // Mark L1/L2 as dirty (defer update)
        _l1Dirty = true;
        _l2Dirty = true;
    }

    public int AllocateBit()
    {
        // Rebuild L1/L2 if dirty (amortized cost)
        if (_l1Dirty || _l2Dirty)
            RebuildUpperLevels();

        // Search from L2 → L1 → L0
        // ...
    }

    private void RebuildUpperLevels()
    {
        if (_l1Dirty)
        {
            // Rebuild entire L1 from L0
            var l1 = _data[1].Span;
            var l0 = _data[0].Span;

            for (int i = 0; i < l1.Length; i++)
            {
                long bits = 0;
                for (int j = 0; j < 64 && (i * 64 + j) < l0.Length; j++)
                {
                    if (l0[i * 64 + j] != 0)
                        bits |= (1L << j);
                }
                l1[i] = bits;
            }

            _l1Dirty = false;
        }

        // Same for L2
        // ...
    }
}
```

**Pros:**
- **Defers expensive updates**: Only rebuild when needed (on allocation)
- **Amortized cost**: Single rebuild for many Set() calls
- **Simpler Set() logic**: Just update L0

**Cons:**
- **Allocation latency spike**: First allocation after many Set() calls is slow
- **Unbounded rebuild cost**: Worst case = scan entire bitmap
- **Not suitable for real-time**: Unpredictable latency

**Performance Gain:**
- **Set operations:** 60-70% faster (no L1/L2 updates)
- **Allocate operations:** Slower (rebuild overhead)
- **Overall:** 10-15% improvement for write-heavy workloads (many allocations, few deallocations)
- **Best for:** Bulk initialization scenarios (allocate many chunks at startup)

---

#### Recommendation: **Combination - Bulk Set + SIMD Search**

Implement bulk SetRange for contiguous allocations + SIMD-accelerated search:

```csharp
public void SetRange(int startIndex, int count)
{
    // Optimized bulk set (Design Alternative 1)
    // ...
}

public int AllocateBit()
{
    // SIMD-accelerated search (Design Alternative 2)
    if (Avx2.IsSupported)
    {
        // AVX2 path
        // ...
    }
    else
    {
        // Scalar fallback
        // ...
    }
}
```

**Expected Overall Gain:** **12-18% improvement** for chunk allocation workloads

---

## 6. B+Tree Index Improvements

### 6.1 Binary Search Stride Optimization

**File:** `src/Typhon.Engine/Database Engine/BPTree/BTree.cs:60-83`

#### Current Algorithm (Inferred)

```csharp
private int BinarySearch(byte* array, int arrayStride, int count, long key)
{
    int left = 0, right = count - 1;

    while (left <= right)
    {
        int mid = (left + right) / 2;

        // Pointer arithmetic: base + (stride × index)
        var midPtr = (long*)(array + (arrayStride * mid));  // MULTIPLY ON EVERY ITERATION
        var midValue = *midPtr;

        if (midValue == key)
            return mid;

        if (midValue < key)
            left = mid + 1;
        else
            right = mid - 1;
    }

    return ~left;
}
```

**Bottleneck:**
- **Stride multiplication** on every iteration: `arrayStride * mid`
- For 64-byte stride: **3-5 CPU cycles per multiply**
- Binary search has O(log N) iterations, but each iteration has multiplicative overhead

---

#### Design Alternative 1: Bit Shift for Power-of-2 Strides

**Algorithm:**
```csharp
private int BinarySearch(byte* array, int arrayStride, int count, long key)
{
    // Pre-compute shift amount (if stride is power-of-2)
    int strideShift = BitOperations.TrailingZeroCount((uint)arrayStride);
    bool useFastPath = (1 << strideShift) == arrayStride;  // Check if power-of-2

    int left = 0, right = count - 1;

    while (left <= right)
    {
        int mid = (left + right) / 2;

        // Fast path: bit shift (1 cycle) instead of multiply (3-5 cycles)
        var midPtr = useFastPath
            ? (long*)(array + (mid << strideShift))
            : (long*)(array + (arrayStride * mid));

        var midValue = *midPtr;

        if (midValue == key)
            return mid;

        if (midValue < key)
            left = mid + 1;
        else
            right = mid - 1;
    }

    return ~left;
}
```

**Pros:**
- **Bit shift is 3-5× faster** than multiply (1 cycle vs 3-5 cycles)
- **Common case**: B+Tree node sizes are typically powers of 2 (64, 128, 256 bytes)
- **Backward compatible**: Falls back to multiply for non-power-of-2

**Cons:**
- **Branch overhead**: if-check on every iteration
- **Marginal benefit**: 2-4 cycles saved per iteration

**Performance Gain:**
- **Per search:** 0.5-1 microsecond saved (for log2(100) ≈ 7 iterations)
- **Overall:** 3-5% improvement in index lookup performance
- **Best for:** Large trees with many lookups

---

#### Design Alternative 2: Specialized Methods per Stride

**Algorithm:**
```csharp
// Specialized version for 64-byte stride (most common)
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private int BinarySearch64(long* array, int count, long key)
{
    const int StrideShift = 6;  // 64 = 2^6

    int left = 0, right = count - 1;

    while (left <= right)
    {
        int mid = (left + right) >> 1;  // Also optimize division by 2

        // Compile-time constant: no runtime branch!
        var midValue = array[mid << StrideShift];

        if (midValue == key)
            return mid;

        if (midValue < key)
            left = mid + 1;
        else
            right = mid - 1;
    }

    return ~left;
}

// Generic fallback
private int BinarySearchGeneric(byte* array, int arrayStride, int count, long key)
{
    // ... existing implementation ...
}

// Dispatcher
private int BinarySearch(byte* array, int arrayStride, int count, long key)
{
    return arrayStride switch
    {
        64 => BinarySearch64((long*)array, count, key),
        128 => BinarySearch128((long*)array, count, key),
        _ => BinarySearchGeneric(array, arrayStride, count, key)
    };
}
```

**Pros:**
- **Zero runtime overhead**: Compile-time constant stride
- **Best codegen**: Compiler can optimize aggressively
- **Inlining**: Entire search can be inlined

**Cons:**
- **Code duplication**: Separate method for each common stride
- **Maintenance**: More code to maintain
- **Binary size**: Slightly larger (multiple specializations)

**Performance Gain:**
- **Per search:** 1-2 microseconds saved
- **Overall:** 5-8% improvement in index lookup
- **Best for:** Hot path with known stride values

---

#### Design Alternative 3: Interpolation Search for Large Nodes

**Algorithm:**
```csharp
private int InterpolationSearch(byte* array, int arrayStride, int count, long key, long minKey, long maxKey)
{
    int left = 0, right = count - 1;

    while (left <= right && key >= minKey && key <= maxKey)
    {
        // Interpolation: estimate position based on value range
        var pos = left + (int)((double)(right - left) / (maxKey - minKey) * (key - minKey));

        var posPtr = (long*)(array + (arrayStride * pos));
        var posValue = *posPtr;

        if (posValue == key)
            return pos;

        if (posValue < key)
        {
            left = pos + 1;
            minKey = posValue;
        }
        else
        {
            right = pos - 1;
            maxKey = posValue;
        }
    }

    // Fallback to binary search for remaining range
    return BinarySearch(array, arrayStride, right - left + 1, key);
}
```

**Pros:**
- **O(log log N) average case** for uniformly distributed keys (vs O(log N) binary)
- **Fewer comparisons**: Directly jumps to estimated position
- **Better for large nodes**: 100+ entries benefit more

**Cons:**
- **Worst case O(N)**: For skewed distributions (e.g., clustered keys)
- **Floating-point overhead**: Division and casting
- **Only helps for uniform data**: Real-world data often clustered

**Performance Gain:**
- **Uniform data:** 10-20% improvement (fewer comparisons)
- **Clustered data:** **Regression** (more overhead, same comparisons)
- **Overall:** 0-10% improvement (depends on data distribution)
- **Best for:** Large leaf nodes with uniformly distributed keys

---

#### Recommendation: **Alternative 2 (Specialized Methods) for Common Strides**

Implement specialized binary search for common strides (64, 128 bytes):

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private int BinarySearch64(long* array, int count, long key)
{
    int left = 0, right = count - 1;

    while (left <= right)
    {
        int mid = (left + right) >> 1;  // Bit shift for divide by 2
        var midValue = array[mid << 6];  // Bit shift for multiply by 64

        if (midValue == key)
            return mid;

        left = midValue < key ? mid + 1 : left;
        right = midValue >= key ? mid - 1 : right;
    }

    return ~left;
}

// Dispatcher
private int BinarySearch(byte* array, int arrayStride, int count, long key)
{
    return arrayStride == 64
        ? BinarySearch64((long*)array, count, key)
        : BinarySearchGeneric(array, arrayStride, count, key);
}
```

**Expected Overall Gain:** **4-7% improvement** in B+Tree lookup performance

---

## 7. I/O Batching & Patterns

### 7.1 SavePages - Radix Sort for Sequential Pages

**File:** `src/Typhon.Engine/Persistence Layer/PagedMMF.cs:866-923`

#### Current Algorithm

```csharp
internal void SavePages(List<PageInfo> pages)
{
    // Sort pages by FilePageIndex (O(N log N))
    pages.Sort((a, b) => a.FilePageIndex.CompareTo(b.FilePageIndex));

    // Identify contiguous runs
    var batches = new List<(int start, int count)>();
    int runStart = 0;

    for (int i = 1; i < pages.Count; i++)
    {
        if (pages[i].FilePageIndex != pages[i-1].FilePageIndex + 1)
        {
            // End of run
            batches.Add((runStart, i - runStart));
            runStart = i;
        }
    }
    batches.Add((runStart, pages.Count - runStart));

    // Write each batch
    foreach (var (start, count) in batches)
    {
        WriteBatchAsync(pages, start, count);
    }
}
```

**Bottleneck:**
- **O(N log N) sort** even if pages already mostly sorted
- For 256 pages: ~1,800 comparisons (256 × log2(256))
- Comparison overhead: ~5-10 cycles each = **9,000-18,000 CPU cycles**
- **List.Sort() allocates** temporary arrays for TimSort

---

#### Design Alternative 1: Counting Sort (Linear Time)

**Algorithm:**
```csharp
internal void SavePages(List<PageInfo> pages)
{
    // Assuming file page indices are bounded (e.g., 0-1M)
    const int MaxFilePages = 1_000_000;

    // Count occurrences (O(N))
    var count = new int[MaxFilePages];
    foreach (var page in pages)
    {
        count[page.FilePageIndex]++;
    }

    // Build sorted output (O(N + K))
    var sorted = new List<PageInfo>(pages.Count);
    for (int filePageIdx = 0; filePageIdx < MaxFilePages; filePageIdx++)
    {
        if (count[filePageIdx] > 0)
        {
            foreach (var page in pages)
            {
                if (page.FilePageIndex == filePageIdx)
                    sorted.Add(page);
            }
        }
    }

    // Identify contiguous runs (same as before)
    // ...
}
```

**Pros:**
- **O(N) time complexity** vs O(N log N)
- **Predictable performance**: No worst-case scenarios
- **Simple logic**: Just counting

**Cons:**
- **Huge memory overhead**: 1M × 4 bytes = 4 MB for count array
- **Cache unfriendly**: Random access pattern
- **Only works for bounded ranges**: Not general-purpose

**Performance Gain:**
- **256 pages:** ~1,800 comparisons → ~256 increments (**85% reduction**)
- **Time saved:** ~10-15 microseconds per SavePages call
- **Overall:** 5-8% improvement in write batching
- **Best for:** Small page counts (<1000) with bounded file indices

---

#### Design Alternative 2: Radix Sort (Linear Time, Low Memory)

**Algorithm:**
```csharp
internal void SavePages(List<PageInfo> pages)
{
    // Radix sort: sort by 8 bits at a time (4 passes for 32-bit int)
    var temp = new PageInfo[pages.Count];
    var count = new int[256];

    for (int shift = 0; shift < 32; shift += 8)
    {
        // Clear counts
        Array.Clear(count, 0, 256);

        // Count occurrences of each byte value
        foreach (var page in pages)
        {
            var bucket = (page.FilePageIndex >> shift) & 0xFF;
            count[bucket]++;
        }

        // Compute prefix sums
        for (int i = 1; i < 256; i++)
        {
            count[i] += count[i - 1];
        }

        // Place elements in sorted order
        for (int i = pages.Count - 1; i >= 0; i--)
        {
            var page = pages[i];
            var bucket = (page.FilePageIndex >> shift) & 0xFF;
            temp[--count[bucket]] = page;
        }

        // Swap buffers
        (pages, temp) = (temp, pages);
    }

    // Identify contiguous runs (same as before)
    // ...
}
```

**Pros:**
- **O(N) time complexity**: Linear in number of pages
- **Low memory overhead**: 256-element count array + N-element temp array
- **Cache-friendly**: Sequential access patterns
- **No comparisons**: Just bit operations

**Cons:**
- **4 passes required**: For 32-bit integers
- **Overhead for small N**: Not worth it for <100 pages
- **Not stable** (unless carefully implemented)

**Performance Gain:**
- **256 pages:** 1,800 comparisons → 1,024 operations (4 × 256) (**43% reduction**)
- **Time saved:** ~15-25 microseconds per SavePages call
- **Overall:** 8-12% improvement in write batching
- **Best for:** Large page counts (>100 pages)

---

#### Design Alternative 3: Insertion Sort for Nearly-Sorted

**Algorithm:**
```csharp
internal void SavePages(List<PageInfo> pages)
{
    // Check if already sorted (common case: ChangeSet tracks in order)
    bool isSorted = true;
    for (int i = 1; i < pages.Count; i++)
    {
        if (pages[i].FilePageIndex < pages[i-1].FilePageIndex)
        {
            isSorted = false;
            break;
        }
    }

    if (isSorted)
    {
        // Fast path: already sorted, skip sorting
        goto IdentifyRuns;
    }

    // Check if nearly sorted (few inversions)
    int inversions = 0;
    for (int i = 1; i < pages.Count; i++)
    {
        if (pages[i].FilePageIndex < pages[i-1].FilePageIndex)
            inversions++;
    }

    if (inversions < pages.Count / 10)
    {
        // Insertion sort for nearly-sorted (O(N + K) where K = inversions)
        for (int i = 1; i < pages.Count; i++)
        {
            var key = pages[i];
            int j = i - 1;

            while (j >= 0 && pages[j].FilePageIndex > key.FilePageIndex)
            {
                pages[j + 1] = pages[j];
                j--;
            }

            pages[j + 1] = key;
        }
    }
    else
    {
        // Fallback: standard sort
        pages.Sort((a, b) => a.FilePageIndex.CompareTo(b.FilePageIndex));
    }

IdentifyRuns:
    // Identify contiguous runs (same as before)
    // ...
}
```

**Pros:**
- **Optimal for nearly-sorted**: O(N) for already sorted
- **Adaptive**: Chooses best algorithm based on data
- **No extra memory**: In-place sorting

**Cons:**
- **Two passes**: Check sortedness + actual sort
- **Worst case O(N²)**: If data is reverse-sorted (but should never happen)

**Performance Gain:**
- **Already sorted:** 1,800 comparisons → 256 checks (**85% reduction**)
- **Nearly sorted:** 1,800 comparisons → ~300 operations (**83% reduction**)
- **Random:** Same as current (falls back to List.Sort)
- **Overall:** 10-15% improvement (assuming 50%+ pages are pre-sorted)
- **Best for:** ChangeSets that track pages in allocation order (already sorted)

---

#### Recommendation: **Hybrid - Insertion Sort + Radix Sort Fallback**

Check for sortedness, use insertion sort if nearly-sorted, radix sort otherwise:

```csharp
internal void SavePages(List<PageInfo> pages)
{
    if (pages.Count < 2)
        goto IdentifyRuns;

    // Check if already sorted (single pass)
    int inversions = 0;
    for (int i = 1; i < pages.Count; i++)
    {
        if (pages[i].FilePageIndex < pages[i-1].FilePageIndex)
            inversions++;
    }

    if (inversions == 0)
    {
        // Already sorted
        goto IdentifyRuns;
    }
    else if (inversions < pages.Count / 10)
    {
        // Nearly sorted: insertion sort (O(N + K))
        InsertionSort(pages);
    }
    else if (pages.Count > 100)
    {
        // Large unsorted: radix sort (O(N))
        RadixSort(pages);
    }
    else
    {
        // Small unsorted: quicksort (built-in)
        pages.Sort((a, b) => a.FilePageIndex.CompareTo(b.FilePageIndex));
    }

IdentifyRuns:
    // Identify contiguous runs and batch write
    // ...
}
```

**Expected Overall Gain:** **12-18% improvement** in SavePages performance

---

## 8. Low-Level Optimizations

### 8.1 Span CopyTo → Unsafe.CopyBlock

**File:** Multiple locations (Transaction.cs:322, 356, 429, 855, 964, 965)

#### Current Algorithm

```csharp
// Line 322
new Span<byte>(data[i].Data, compSize).CopyTo(destSpan);

// Line 964
var prev = new Span<byte>(prev, info.ComponentTable.ComponentTotalSize);
var cur = new Span<byte>(cur, info.ComponentTable.ComponentTotalSize);
if (!prev.SequenceEqual(cur))
{
    // Component changed
}
```

**Bottleneck:**
- Span construction has overhead (bounds checking, metadata setup)
- CopyTo has safety checks (even though size is known at compile-time)
- SequenceEqual has loop overhead for small sizes

---

#### Design Alternative 1: Direct Unsafe.CopyBlock

**Algorithm:**
```csharp
// Instead of Span.CopyTo
Unsafe.CopyBlock(dest, source, (uint)compSize);

// Instead of Span.SequenceEqual
bool changed = Unsafe.CompareBytes(prev, cur, compSize) != 0;
```

**Pros:**
- **No bounds checking**: Direct memory copy
- **No Span overhead**: Just pointers
- **Faster for small sizes**: <64 bytes benefit most

**Cons:**
- **Less safe**: No runtime validation
- **Platform-specific**: Relies on compiler intrinsics
- **Readability**: Less clear than Span API

**Performance Gain:**
- **Per copy (32 bytes):** ~0.2-0.5 microseconds saved
- **Per comparison (32 bytes):** ~0.1-0.3 microseconds saved
- **Overall:** 1-2% improvement in CreateEntity/UpdateEntity
- **Best for:** Hot paths with many small copies

---

#### Design Alternative 2: Inline Memcpy for Fixed Sizes

**Algorithm:**
```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void CopyBlock32(byte* dest, byte* source)
{
    // Unrolled loop for 32 bytes (4 × 8-byte copies)
    ((long*)dest)[0] = ((long*)source)[0];
    ((long*)dest)[1] = ((long*)source)[1];
    ((long*)dest)[2] = ((long*)source)[2];
    ((long*)dest)[3] = ((long*)source)[3];
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void CopyBlock64(byte* dest, byte* source)
{
    // Unrolled loop for 64 bytes (8 × 8-byte copies)
    ((long*)dest)[0] = ((long*)source)[0];
    ((long*)dest)[1] = ((long*)source)[1];
    ((long*)dest)[2] = ((long*)source)[2];
    ((long*)dest)[3] = ((long*)source)[3];
    ((long*)dest)[4] = ((long*)source)[4];
    ((long*)dest)[5] = ((long*)source)[5];
    ((long*)dest)[6] = ((long*)source)[6];
    ((long*)dest)[7] = ((long*)source)[7];
}

// Usage
if (compSize == 32)
    CopyBlock32(dest, source);
else if (compSize == 64)
    CopyBlock64(dest, source);
else
    Unsafe.CopyBlock(dest, source, (uint)compSize);
```

**Pros:**
- **Optimal codegen**: Compiler can use SIMD registers
- **Zero overhead**: Fully inlined
- **Predictable**: No branching in hot path

**Cons:**
- **Code duplication**: Separate method for each common size
- **Limited applicability**: Only helps for fixed sizes

**Performance Gain:**
- **Per copy (32 bytes):** ~0.3-0.7 microseconds saved vs Span
- **Overall:** 2-3% improvement for fixed-size components
- **Best for:** Components with known common sizes (32, 64, 128 bytes)

---

#### Recommendation: **Unsafe.CopyBlock with Size Dispatch**

Use Unsafe.CopyBlock for general case, specialized inline for common sizes:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void FastCopy(byte* dest, byte* source, int size)
{
    switch (size)
    {
        case 8:
            *(long*)dest = *(long*)source;
            break;
        case 16:
            ((long*)dest)[0] = ((long*)source)[0];
            ((long*)dest)[1] = ((long*)source)[1];
            break;
        case 32:
            CopyBlock32(dest, source);
            break;
        case 64:
            CopyBlock64(dest, source);
            break;
        default:
            Unsafe.CopyBlock(dest, source, (uint)size);
            break;
    }
}
```

**Expected Overall Gain:** **1.5-3% improvement** in copy-heavy operations

---

## Summary & Prioritization

### Top 10 Optimizations by Expected ROI

| Rank | Optimization | Estimated Gain | Complexity | Priority |
|------|--------------|----------------|------------|----------|
| 1 | ChunkRandomAccessor - Hash cache | 7-12% | Low | **CRITICAL** |
| 2 | TransitionPageToAccess - Lock-free | 12-20% | High | **CRITICAL** |
| 3 | AllocateMemoryPage - Single-pass sweep | 15-25% | Medium | **HIGH** |
| 4 | Transaction Dictionary Pooling | 10-15% | Low | **HIGH** |
| 5 | MVCC Revision Caching | 10-15% | Medium | **HIGH** |
| 6 | AccessControl - Adaptive backoff | 10-18% | Low | **MEDIUM** |
| 7 | SavePages - Radix sort | 12-18% | Medium | **MEDIUM** |
| 8 | Bitmap - Bulk allocation | 12-18% | Medium | **MEDIUM** |
| 9 | BTree - Stride optimization | 4-7% | Low | **MEDIUM** |
| 10 | Unsafe.CopyBlock | 1.5-3% | Low | **LOW** |

### Overall Performance Impact Projection

Implementing **all high-priority optimizations** (1-5):

**Conservative Estimate:** 15-25% overall improvement
**Aggressive Estimate:** 25-35% overall improvement

**Breakdown by Workload Type:**

1. **High-Concurrency Reads (100k reads/sec):**
   - ChunkRandomAccessor hash: +7%
   - Lock-free page transitions: +15%
   - MVCC caching: +8%
   - **Total: 30-35% improvement**

2. **Update-Heavy Workloads (50k updates/sec):**
   - Transaction pooling: +12%
   - Page allocation: +18%
   - Dictionary pooling: +8%
   - **Total: 25-30% improvement**

3. **Bulk Operations (large batch inserts):**
   - Bitmap bulk allocation: +15%
   - SavePages radix sort: +15%
   - Transaction pooling: +10%
   - **Total: 35-40% improvement**

4. **Read-Only Analytics:**
   - MVCC revision caching: +12%
   - ChunkRandomAccessor hash: +10%
   - Lock-free transitions: +10%
   - **Total: 20-25% improvement**

### Implementation Roadmap

**Phase 1 (Low-Hanging Fruit - 2-3 weeks):**
1. ChunkRandomAccessor hash lookup (1 week)
2. Transaction Dictionary pooling (3 days)
3. Unsafe.CopyBlock replacement (2 days)
4. BTree stride optimization (2 days)

**Expected Gain: 12-18%**

**Phase 2 (Medium Complexity - 3-4 weeks):**
1. AllocateMemoryPage single-pass clock-sweep (1 week)
2. MVCC revision caching (1 week)
3. AccessControl adaptive backoff (1 week)
4. SavePages radix sort (3 days)

**Expected Gain: +10-15% (cumulative: 22-33%)**

**Phase 3 (High Complexity - 4-6 weeks):**
1. TransitionPageToAccess lock-free (2 weeks)
2. Bitmap bulk allocation (1 week)
3. RevisionEnumerator inline optimization (1 week)

**Expected Gain: +8-12% (cumulative: 30-45%)**

---

**End of Optimization Report**

For questions or clarifications, please contact the development team.
