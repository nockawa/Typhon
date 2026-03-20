using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Page-backed hash map with unmanaged key type and runtime-configured value size.
/// Identical to <see cref="HashMap{TKey,TValue,TStore}"/> except values are accessed as raw <c>byte*</c>
/// pointers with <see cref="Unsafe.CopyBlock"/> instead of typed <c>ref TValue</c>.
/// <para>
/// Designed for entity records whose size varies per archetype (14 + componentCount × 4 bytes).
/// Bucket layout: [Header 12B] [Key₀..Key_{cap-1}] [Val₀..Val_{cap-1}].
/// </para>
/// </summary>
unsafe class RawValueHashMap<TKey, TStore> : HashMapBase<TStore> where TKey : unmanaged, IEquatable<TKey> where TStore : struct, IPageStore
{
    // ═══════════════════════════════════════════════════════════════════════
    // Layout fields (computed once at construction)
    // ═══════════════════════════════════════════════════════════════════════

    private readonly int _valueSize;
    private readonly int _bucketCapacity;
    private readonly int _keysOffset;
    private readonly int _valuesOffset;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    private RawValueHashMap(ChunkBasedSegment<TStore> segment, int n0, int valueSize) : base(segment, n0)
    {
        Debug.Assert(valueSize > 0, "Value size must be positive");
        _valueSize = valueSize;
        _bucketCapacity = (segment.Stride - sizeof(HashMapBucketHeader)) / (sizeof(TKey) + valueSize);
        Debug.Assert(_bucketCapacity >= 1, $"Stride {segment.Stride} too small for entry size {sizeof(TKey) + valueSize}");
        _keysOffset = sizeof(HashMapBucketHeader);
        _valuesOffset = sizeof(HashMapBucketHeader) + _bucketCapacity * sizeof(TKey);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    public override int BucketCapacity => _bucketCapacity;

    /// <summary>Configured value size in bytes.</summary>
    public int ValueSize => _valueSize;

    // ═══════════════════════════════════════════════════════════════════════
    // Static helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the smallest supported stride (256 or 512) that yields at least <paramref name="minCapacity"/> entries per bucket.
    /// </summary>
    public static int RecommendedStride(int valueSize, int minCapacity = 4)
    {
        int entrySize = sizeof(TKey) + valueSize;
        if ((256 - sizeof(HashMapBucketHeader)) / entrySize >= minCapacity)
        {
            return 256;
        }
        if ((512 - sizeof(HashMapBucketHeader)) / entrySize >= minCapacity)
        {
            return 512;
        }
        throw new ArgumentException($"Entry size {entrySize}B too large for supported strides (need {minCapacity} entries)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pointer access helpers
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref HashMapBucketHeader GetHeader(byte* chunkAddr) => ref Unsafe.AsRef<HashMapBucketHeader>(chunkAddr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TKey* KeysPtr(byte* chunkAddr) => (TKey*)(chunkAddr + _keysOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ValueAt(byte* chunkAddr, int index)
    {
        Debug.Assert(index >= 0 && index < _bucketCapacity, $"ValueAt index {index} out of range [0, {_bucketCapacity})");
        return chunkAddr + _valuesOffset + index * _valueSize;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hash function — JIT-specialized by sizeof(TKey)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeHash(TKey key)
    {
        if (sizeof(TKey) == 4)
        {
            return WangJenkins32(Unsafe.As<TKey, uint>(ref key));
        }
        if (sizeof(TKey) == 8)
        {
            return XxHash32_8Bytes(Unsafe.As<TKey, long>(ref key));
        }
        return XxHash32_Bytes((byte*)Unsafe.AsPointer(ref key), sizeof(TKey));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint WangJenkins32(uint h)
    {
        h = (h ^ 61) ^ (h >> 16);
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint XxHash32_8Bytes(long key)
    {
        // ReSharper disable InconsistentNaming
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;
        const uint Prime2 = 2246822519u;
        // ReSharper restore InconsistentNaming

        uint lo = (uint)key;
        uint hi = (uint)(key >> 32);

        uint h = Prime5 + 8u;
        h += lo * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;
        h += hi * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint XxHash32_Bytes(byte* input, int len)
    {
        // ReSharper disable InconsistentNaming
        const uint Prime1 = 2654435761u;
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;
        // ReSharper restore InconsistentNaming

        uint h = Prime5 + (uint)len;
        byte* p = input;
        byte* end = input + len;

        while (p + 4 <= end)
        {
            h += *(uint*)p * Prime3;
            h = ((h << 17) | (h >> 15)) * Prime4;
            p += 4;
        }
        while (p < end)
        {
            h += *p * Prime5;
            h = ((h << 11) | (h >> 21)) * Prime1;
            p++;
        }

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    internal static uint ComputeHashForTest(TKey key) => ComputeHash(key);

    // ═══════════════════════════════════════════════════════════════════════
    // Bucket initialization
    // ═══════════════════════════════════════════════════════════════════════

    protected override void InitializeBucket(int chunkId, ref ChunkAccessor<TStore> accessor)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        header.OlcVersion = 4;          // version=1, locked=false, obsolete=false
        header.EntryCount = 0;
        header.Flags = 0;
        header.Reserved = 0;
        header.OverflowChunkId = -1;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Read path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scan a bucket chain for a key. Returns index + chunk if found, -1 if not.
    /// When found, <paramref name="foundChunkId"/> and <paramref name="foundIndex"/> indicate the location.
    /// </summary>
    private bool ScanChain(int startChunkId, TKey key, ref ChunkAccessor<TStore> accessor, out int foundChunkId, out int foundIndex)
    {
        int chunkId = startChunkId;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId);
            ref readonly var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    foundChunkId = chunkId;
                    foundIndex = i;
                    return true;
                }
            }

            chunkId = header.OverflowChunkId;
        }

        foundChunkId = -1;
        foundIndex = -1;
        return false;
    }

    /// <summary>
    /// Look up a key using the OLC read protocol. Copies value bytes to <paramref name="valueOut"/>.
    /// </summary>
    public bool TryGet(TKey key, byte* valueOut, ref ChunkAccessor<TStore> accessor)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = _packedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, _n0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId);
            ref var header = ref GetHeader(addr);

            var latch = new OlcLatch(ref header.OlcVersion);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            bool found = ScanChain(chunkId, key, ref accessor, out int fChunkId, out int fIndex);
            if (found)
            {
                byte* fAddr = accessor.GetChunkAddress(fChunkId);
                Unsafe.CopyBlock(valueOut, ValueAt(fAddr, fIndex), (uint)_valueSize);
            }

            if (!latch.ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && _packedMeta != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            return found;
        }
    }

    /// <summary>
    /// Look up a key and return a pointer to the value in-place. The pointer is valid only while
    /// the chunk stays pinned (before OLC validation). Use <see cref="TryGet"/> for safe access.
    /// Returns null if the key is not found.
    /// </summary>
    public byte* TryGetPtr(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = _packedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, _n0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId);
            ref var header = ref GetHeader(addr);

            var latch = new OlcLatch(ref header.OlcVersion);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            bool found = ScanChain(chunkId, key, ref accessor, out int fChunkId, out int fIndex);
            byte* result = found ? ValueAt(accessor.GetChunkAddress(fChunkId), fIndex) : null;

            if (!latch.ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && _packedMeta != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Write helpers (private)
    // ═══════════════════════════════════════════════════════════════════════

    private void AppendEntry(int startChunkId, TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        int chunkId = startChunkId;

        while (true)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);

            if (header.EntryCount < _bucketCapacity)
            {
                int idx = header.EntryCount;
                KeysPtr(addr)[idx] = key;
                Unsafe.CopyBlock(ValueAt(addr, idx), value, (uint)_valueSize);
                header.EntryCount = (byte)(idx + 1);
                return;
            }

            if (header.OverflowChunkId != -1)
            {
                chunkId = header.OverflowChunkId;
                continue;
            }

            // Allocate overflow — re-fetch current chunk after (AllocateChunk may remap segment)
            int overflowChunkId = _segment.AllocateChunk(true, changeSet);
            addr = accessor.GetChunkAddress(chunkId, true);
            GetHeader(addr).OverflowChunkId = overflowChunkId;

            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            ovHeader.OlcVersion = 0;
            ovHeader.EntryCount = 1;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;
            KeysPtr(ovAddr)[0] = key;
            Unsafe.CopyBlock(ValueAt(ovAddr, 0), value, (uint)_valueSize);
            return;
        }
    }

    private bool RemoveFromChain(int startChunkId, TKey key, ref ChunkAccessor<TStore> accessor)
    {
        int chunkId = startChunkId;
        int prevChunkId = -1;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    // Swap with last entry in this chunk (no holes)
                    int lastIdx = count - 1;
                    if (i != lastIdx)
                    {
                        keys[i] = keys[lastIdx];
                        Unsafe.CopyBlock(ValueAt(addr, i), ValueAt(addr, lastIdx), (uint)_valueSize);
                    }
                    header.EntryCount = (byte)(count - 1);

                    // If overflow chunk became empty, unlink and free it
                    if (header.EntryCount == 0 && prevChunkId != -1)
                    {
                        int nextOverflow = header.OverflowChunkId;
                        byte* prevAddr = accessor.GetChunkAddress(prevChunkId, true);
                        GetHeader(prevAddr).OverflowChunkId = nextOverflow;
                        _segment.FreeChunk(chunkId);
                    }

                    return true;
                }
            }

            prevChunkId = chunkId;
            chunkId = header.OverflowChunkId;
        }

        return false;
    }

    private bool UpdateInChain(int startChunkId, TKey key, byte* newValue, ref ChunkAccessor<TStore> accessor)
    {
        int chunkId = startChunkId;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    Unsafe.CopyBlock(ValueAt(addr, i), newValue, (uint)_valueSize);
                    return true;
                }
            }

            chunkId = header.OverflowChunkId;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Write API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Insert a key-value pair. Returns true if inserted, false if key already exists.
    /// </summary>
    public bool Insert(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = _packedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, _n0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (_packedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            // Check for duplicate
            if (ScanChain(chunkId, key, ref accessor, out _, out _))
            {
                latch.AbortWriteLock();
                return false;
            }

            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            // Re-fetch primary for unlock after potential allocation
            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();

            TrySplitIfNeeded(ref accessor, changeSet);
            return true;
        }
    }

    /// <summary>
    /// Insert a key-value pair, skipping duplicate detection. Caller guarantees the key does not exist.
    /// Used for batch inserts of known-unique keys (e.g., freshly generated EntityKeys in FinalizeSpawns).
    /// </summary>
    public void InsertNew(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = _packedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, _n0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (_packedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();

            TrySplitIfNeeded(ref accessor, changeSet);
            return;
        }
    }

    /// <summary>
    /// Insert or update a key-value pair. Returns true if inserted, false if updated.
    /// </summary>
    public bool Upsert(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = _packedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, _n0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (_packedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            if (UpdateInChain(chunkId, key, value, ref accessor))
            {
                latch.WriteUnlock();
                return false;
            }

            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();

            TrySplitIfNeeded(ref accessor, changeSet);
            return true;
        }
    }

    /// <summary>
    /// Remove a key. Returns true if found and removed.
    /// </summary>
    public bool Remove(TKey key, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = _packedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, _n0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (_packedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            if (RemoveFromChain(chunkId, key, ref accessor))
            {
                Interlocked.Decrement(ref _entryCount);
                byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
                new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
                return true;
            }

            latch.AbortWriteLock();

            if (_packedMeta != packed)
            {
                continue;
            }

            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split
    // ═══════════════════════════════════════════════════════════════════════

    protected override void ExecuteSplit(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        var (level, next, bucketCount) = ReadMeta();
        int mod = _n0 << level;
        int newMod = mod << 1;
        int oldBucketId = next;
        int newBucketId = next + mod;

        int oldChunkId = GetBucketChunkId(oldBucketId, ref accessor);
        byte* oldAddr = accessor.GetChunkAddress(oldChunkId, true);
        SpinUntilWriteLock(ref GetHeader(oldAddr).OlcVersion);

        // Single-pass: classify entries into keep/move buffers while walking the chain.
        // Upper-bound: 8 chunks worth of entries covers all realistic chains (primary + 7 overflow).
        int maxEntries = _bucketCapacity * 8;
        int entrySize = sizeof(TKey) + _valueSize;
        byte* keepBuf = stackalloc byte[maxEntries * entrySize];
        byte* moveBuf = stackalloc byte[maxEntries * entrySize];
        TKey* keepKeys = (TKey*)keepBuf;
        byte* keepValues = keepBuf + maxEntries * sizeof(TKey);
        TKey* moveKeys = (TKey*)moveBuf;
        byte* moveValues = moveBuf + maxEntries * sizeof(TKey);
        int keepCount = 0, moveCount = 0;

        Span<int> overflowIds = stackalloc int[8];
        int overflowCount = 0;

        int walkId = oldChunkId;
        while (walkId != -1)
        {
            byte* wAddr = accessor.GetChunkAddress(walkId);
            ref readonly var wHeader = ref GetHeader(wAddr);
            TKey* wKeys = KeysPtr(wAddr);
            int count = wHeader.EntryCount;
            int nextId = wHeader.OverflowChunkId;

            for (int i = 0; i < count; i++)
            {
                TKey key = wKeys[i];
                uint hash = ComputeHash(key);
                int targetBucket = (int)(hash & (uint)(newMod - 1));

                if (targetBucket == oldBucketId)
                {
                    if (keepCount >= maxEntries)
                    {
                        throw new InvalidOperationException($"ExecuteSplit: keep buffer overflow ({keepCount} >= {maxEntries}). Overflow chain exceeds expected capacity.");
                    }
                    keepKeys[keepCount] = key;
                    Unsafe.CopyBlock(keepValues + keepCount * _valueSize, ValueAt(wAddr, i), (uint)_valueSize);
                    keepCount++;
                }
                else
                {
                    if (moveCount >= maxEntries)
                    {
                        throw new InvalidOperationException($"ExecuteSplit: move buffer overflow ({moveCount} >= {maxEntries}). Overflow chain exceeds expected capacity.");
                    }
                    moveKeys[moveCount] = key;
                    Unsafe.CopyBlock(moveValues + moveCount * _valueSize, ValueAt(wAddr, i), (uint)_valueSize);
                    moveCount++;
                }
            }

            if (walkId != oldChunkId)
            {
                if (overflowCount < overflowIds.Length)
                {
                    overflowIds[overflowCount] = walkId;
                }
                overflowCount++;
            }

            walkId = nextId;
        }

        // Rewrite old bucket
        RewriteBucket(oldChunkId, keepKeys, keepValues, keepCount, ref accessor, changeSet);

        // Allocate and write new bucket
        int newChunkId = _segment.AllocateChunk(true, changeSet);
        WriteBucket(newChunkId, moveKeys, moveValues, moveCount, ref accessor, changeSet);

        EnsureDirectoryCapacity(newBucketId, ref accessor, changeSet);
        SetBucketChunkId(newBucketId, newChunkId, ref accessor);

        // Free overflow chunks (up to what we tracked; excess overflows are rare)
        int freeCount = Math.Min(overflowCount, overflowIds.Length);
        for (int i = 0; i < freeCount; i++)
        {
            _segment.FreeChunk(overflowIds[i]);
        }

        int newNext = next + 1;
        int newLevel = level;
        if (newNext >= mod)
        {
            newNext = 0;
            newLevel = level + 1;
        }
        _packedMeta = PackMeta(newLevel, newNext, bucketCount + 1);
        FlushMetaToChunk(ref accessor);

        byte* unlockAddr = accessor.GetChunkAddress(oldChunkId, true);
        new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
    }

    private void RewriteBucket(int chunkId, TKey* keys, byte* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        int count = Math.Min(entryCount, _bucketCapacity);
        header.EntryCount = (byte)count;
        header.OverflowChunkId = -1;

        TKey* dstKeys = KeysPtr(addr);
        for (int i = 0; i < count; i++)
        {
            dstKeys[i] = keys[i];
            Unsafe.CopyBlock(ValueAt(addr, i), values + i * _valueSize, (uint)_valueSize);
        }

        if (entryCount > count)
        {
            WriteOverflowChain(chunkId, keys + count, values + count * _valueSize, entryCount - count, ref accessor, changeSet);
        }
    }

    private void WriteBucket(int chunkId, TKey* keys, byte* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        int count = Math.Min(entryCount, _bucketCapacity);
        header.OlcVersion = 4;
        header.EntryCount = (byte)count;
        header.Flags = 0;
        header.Reserved = 0;
        header.OverflowChunkId = -1;

        TKey* dstKeys = KeysPtr(addr);
        for (int i = 0; i < count; i++)
        {
            dstKeys[i] = keys[i];
            Unsafe.CopyBlock(ValueAt(addr, i), values + i * _valueSize, (uint)_valueSize);
        }

        if (entryCount > count)
        {
            WriteOverflowChain(chunkId, keys + count, values + count * _valueSize, entryCount - count, ref accessor, changeSet);
        }
    }

    private void WriteOverflowChain(int parentChunkId, TKey* keys, byte* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        if (entryCount == 0)
        {
            return;
        }

        int prevChunkId = parentChunkId;
        int offset = 0;

        while (offset < entryCount)
        {
            int overflowChunkId = _segment.AllocateChunk(true, changeSet);

            byte* prevAddr = accessor.GetChunkAddress(prevChunkId, true);
            GetHeader(prevAddr).OverflowChunkId = overflowChunkId;

            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            int writeCount = Math.Min(entryCount - offset, _bucketCapacity);
            ovHeader.OlcVersion = 0;
            ovHeader.EntryCount = (byte)writeCount;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;

            TKey* dstKeys = KeysPtr(ovAddr);
            for (int i = 0; i < writeCount; i++)
            {
                dstKeys[i] = keys[offset + i];
                Unsafe.CopyBlock(ValueAt(ovAddr, i), values + (offset + i) * _valueSize, (uint)_valueSize);
            }

            prevChunkId = overflowChunkId;
            offset += writeCount;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Rebuild support
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Insert without OLC or duplicate check. Single-threaded rebuild/recovery only. Triggers splits.
    /// </summary>
    internal void InsertDuringRebuild(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);
        var (level, next, _) = UnpackMeta(_packedMeta);
        int bucket = ResolveBucket(hash, level, next, _n0);
        int chunkId = GetBucketChunkId(bucket, ref accessor);

        AppendEntry(chunkId, key, value, ref accessor, changeSet);
        _entryCount++;
        TrySplitIfNeeded(ref accessor, changeSet);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Factory methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Create a new raw value hash map, allocating meta + directory + initial buckets.</summary>
    public static RawValueHashMap<TKey, TStore> Create(ChunkBasedSegment<TStore> segment, int n0, int valueSize, ChangeSet changeSet = null)
    {
        Debug.Assert(n0 > 0 && BitOperations.IsPow2(n0), "N0 must be a positive power of 2");

        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        var map = new RawValueHashMap<TKey, TStore>(segment, n0, valueSize);
        map.InitializeCreate(n0, changeSet);
        return map;
    }

    /// <summary>Open an existing raw value hash map by reading meta from chunk 0.</summary>
    public static RawValueHashMap<TKey, TStore> Open(ChunkBasedSegment<TStore> segment, int n0, int valueSize)
    {
        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        var map = new RawValueHashMap<TKey, TStore>(segment, n0, valueSize);
        map.InitializeOpen();
        return map;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumeration (broad scan)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Callback interface for zero-overhead iteration via JIT specialization.
    /// </summary>
    internal interface IEntryAction<in TK> where TK : unmanaged
    {
        /// <summary>Process one entry. Return false to stop iteration.</summary>
        bool Process(TK key, byte* value);
    }

    /// <summary>
    /// Iterate all live entries in the hash map, calling <paramref name="action"/> for each.
    /// Uses OLC read protocol for thread safety. Entries are visited in bucket order (cache-friendly).
    /// </summary>
    /// <returns>Number of entries visited.</returns>
    internal int ForEachEntry<TAction>(ref ChunkAccessor<TStore> accessor, ref TAction action) where TAction : struct, IEntryAction<TKey>
    {
        int visited = 0;
        var (_, _, bucketCount) = ReadMeta();

        for (int b = 0; b < bucketCount; b++)
        {
            int chunkId = GetBucketChunkId(b, ref accessor);

            while (chunkId != -1)
            {
                byte* addr = accessor.GetChunkAddress(chunkId);
                ref readonly var header = ref GetHeader(addr);
                int count = header.EntryCount;
                TKey* keys = KeysPtr(addr);

                for (int i = 0; i < count; i++)
                {
                    byte* valuePtr = ValueAt(addr, i);
                    if (!action.Process(keys[i], valuePtr))
                    {
                        return visited;
                    }
                    visited++;
                }

                chunkId = header.OverflowChunkId;
            }
        }

        return visited;
    }

}
