using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// High-performance in-memory hash set using open addressing with linear probing.
/// Single flat entry array — no chains, no overflow, no pointer indirection.
/// Backward-shift deletion avoids tombstone accumulation.
/// Replaces <see cref="HashSet{T}"/> on hot paths.
/// </summary>
public unsafe class InMemoryHashMap<TKey> : IResource where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

    // ═══════════════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════════════

    private readonly IMemoryAllocator _allocator;
    private readonly int _entryStride; // bytes per entry: (4 + sizeof(TKey)) aligned to 4

    private PinnedMemoryBlock _block;
    private byte* _entries;
    private int _capacity;     // power of 2
    private int _mask;         // _capacity - 1
    private int _count;
    private int _resizeThreshold;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    public InMemoryHashMap(string id, IResource parent, IMemoryAllocator allocator, int initialCapacity = 64)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(allocator);

        _capacity = Math.Max(4, initialCapacity);
        if (!BitOperations.IsPow2(_capacity))
        {
            _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)_capacity);
        }

        Id = id ?? Guid.NewGuid().ToString();
        Parent = parent;
        Owner = parent.Owner;
        CreatedAt = DateTime.UtcNow;
        _allocator = allocator;

        _entryStride = (4 + sizeof(TKey) + 3) & ~3; // 4-byte aligned
        _mask = _capacity - 1;
        _resizeThreshold = (int)(_capacity * MaxLoadFactor);

        _block = allocator.AllocatePinned($"{id}/Entries", this, _capacity * _entryStride, true, 64);
        _entries = _block.DataAsPointer;

        parent.RegisterChild(this);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IResource
    // ═══════════════════════════════════════════════════════════════════════

    public string Id { get; }
    public ResourceType Type => ResourceType.None;
    public IResource Parent { get; }

    public IEnumerable<IResource> Children
    {
        get
        {
            if (_block != null)
            {
                yield return _block;
            }
        }
    }

    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;
    public bool RemoveChild(IResource resource) => false;

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Number of keys in the set.</summary>
    public int Count => _count;

    /// <summary>Current internal capacity (power of 2). Grows automatically when load factor exceeds 0.75.</summary>
    public int Capacity => _capacity;

    // ═══════════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Add <paramref name="key"/> to the set if not already present.</summary>
    /// <returns><c>true</c> if the key was added; <c>false</c> if it already existed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key)
    {
        if (_count >= _resizeThreshold)
        {
            Resize(_capacity * 2);
        }

        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int idx = (int)(hash & (uint)_mask);
        int stride = _entryStride;

        while (true)
        {
            byte* entry = _entries + (long)idx * stride;
            uint h = *(uint*)entry;

            if (h == 0)
            {
                *(uint*)entry = hash;
                *(TKey*)(entry + 4) = key;
                _count++;
                return true;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                return false;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Check whether <paramref name="key"/> exists in the set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(TKey key)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int idx = (int)(hash & (uint)_mask);
        int stride = _entryStride;

        while (true)
        {
            byte* entry = _entries + (long)idx * stride;
            uint h = *(uint*)entry;

            if (h == 0)
            {
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Remove <paramref name="key"/> from the set. Uses backward-shift deletion to maintain probe chains.</summary>
    /// <returns><c>true</c> if the key was found and removed; <c>false</c> if it was not present.</returns>
    public bool TryRemove(TKey key)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int idx = (int)(hash & (uint)_mask);
        int stride = _entryStride;

        while (true)
        {
            byte* entry = _entries + (long)idx * stride;
            uint h = *(uint*)entry;

            if (h == 0)
            {
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                _count--;
                BackwardShiftDelete(idx);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Remove all keys from the set. Does not shrink the underlying buffer.</summary>
    public void Clear()
    {
        new Span<byte>(_entries, _capacity * _entryStride).Clear();
        _count = 0;
    }

    /// <summary>Grow internal capacity so the set can hold at least <paramref name="minimumEntries"/> without resizing.</summary>
    public void EnsureCapacity(int minimumEntries)
    {
        int needed = (int)(minimumEntries / MaxLoadFactor) + 1;
        needed = (int)BitOperations.RoundUpToPowerOf2((uint)needed);
        if (needed > _capacity)
        {
            Resize(needed);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumerator
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Returns a <see langword="ref struct"/> enumerator over all keys in the set.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Value-type enumerator. Iterates occupied entry slots in memory order.</summary>
    public ref struct Enumerator
    {
        private readonly InMemoryHashMap<TKey> _map;
        private int _index;

        internal Enumerator(InMemoryHashMap<TKey> map)
        {
            _map = map;
            _index = -1;
        }

        public TKey Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (++_index < _map._capacity)
            {
                byte* entry = _map._entries + (long)_index * stride;
                if (*(uint*)entry != 0)
                {
                    Current = *(TKey*)(entry + 4);
                    return true;
                }
            }
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _block?.Dispose();
        _block = null;
        _entries = null;
        Parent.RemoveChild(this);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — backward shift deletion
    // ═══════════════════════════════════════════════════════════════════════

    private void BackwardShiftDelete(int idx)
    {
        int stride = _entryStride;
        int j = (idx + 1) & _mask;

        while (true)
        {
            byte* entryJ = _entries + (long)j * stride;
            uint hj = *(uint*)entryJ;

            if (hj == 0)
            {
                break;
            }

            int homeJ = (int)(hj & (uint)_mask);
            int distI = (idx - homeJ + _capacity) & _mask;
            int distJ = (j - homeJ + _capacity) & _mask;

            if (distI < distJ)
            {
                Unsafe.CopyBlock(_entries + (long)idx * stride, entryJ, (uint)stride);
                idx = j;
            }

            j = (j + 1) & _mask;
        }

        *(uint*)(_entries + (long)idx * stride) = 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — resize
    // ═══════════════════════════════════════════════════════════════════════

    private void Resize(int newCapacity)
    {
        var newBlock = _allocator.AllocatePinned($"{Id}/Entries", this, newCapacity * _entryStride, true, 64);
        byte* newEntries = newBlock.DataAsPointer;
        int newMask = newCapacity - 1;
        int stride = _entryStride;

        for (int i = 0; i < _capacity; i++)
        {
            byte* entry = _entries + (long)i * stride;
            uint h = *(uint*)entry;
            if (h != 0)
            {
                int idx = (int)(h & (uint)newMask);
                while (*(uint*)(newEntries + (long)idx * stride) != 0)
                {
                    idx = (idx + 1) & newMask;
                }
                Unsafe.CopyBlock(newEntries + (long)idx * stride, entry, (uint)stride);
            }
        }

        var oldBlock = _block;
        _block = newBlock;
        _entries = newEntries;
        _capacity = newCapacity;
        _mask = newMask;
        _resizeThreshold = (int)(newCapacity * MaxLoadFactor);

        oldBlock.Dispose();
    }
}
