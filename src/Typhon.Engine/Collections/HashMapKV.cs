using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// High-performance in-memory hash map using open addressing with linear probing.
/// Single flat entry array — no chains, no overflow, no pointer indirection.
/// Backward-shift deletion avoids tombstone accumulation.
/// <para>
/// JIT-specialized dual path via <see cref="RuntimeHelpers.IsReferenceOrContainsReferences{T}"/>:
/// <list type="bullet">
///   <item>Unmanaged TValue: values stored inline in entry array. Zero GC pressure.</item>
///   <item>Managed TValue: keys in entry array, values in parallel <c>TValue[]</c>.</item>
/// </list>
/// </para>
/// Replaces <see cref="Dictionary{TKey,TValue}"/> on hot paths.
/// </summary>
public unsafe class HashMap<TKey, TValue> : IResource where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

    // ═══════════════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════════════

    private readonly IMemoryAllocator _allocator;
    private readonly int _entryStride;
    private readonly int _valueOffset; // byte offset of value within entry (unmanaged path only)

    private PinnedMemoryBlock _block;
    private byte* _entries;
    private int _capacity;
    private int _mask;
    private int _count;
    private int _resizeThreshold;
    private bool _disposed;

    private TValue[] _managedValues; // managed TValue path only

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    public HashMap(string id, IResource parent, IMemoryAllocator allocator, int initialCapacity = 64)
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

        // Entry layout: [uint hash | TKey key | TValue value(unmanaged only)]
        _valueOffset = 4 + sizeof(TKey);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _entryStride = (4 + sizeof(TKey) + Unsafe.SizeOf<TValue>() + 3) & ~3;
        }
        else
        {
            _entryStride = (4 + sizeof(TKey) + 3) & ~3;
            _managedValues = new TValue[_capacity];
        }

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

    /// <summary>Number of key-value pairs in the map.</summary>
    public int Count => _count;

    /// <summary>Current internal capacity (power of 2). Grows automatically when load factor exceeds 0.75.</summary>
    public int Capacity => _capacity;

    // ═══════════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Add a key-value pair if the key is not already present.</summary>
    /// <returns><c>true</c> if the pair was added; <c>false</c> if the key already existed (existing value unchanged).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, TValue value)
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
                WriteValue(entry, idx, value);
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

    /// <summary>Look up the value associated with <paramref name="key"/>.</summary>
    /// <returns><c>true</c> if found; <c>false</c> if the key is not present.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, out TValue value)
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
                value = default;
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                value = ReadValue(entry, idx);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Remove the entry for <paramref name="key"/> and return its value. Uses backward-shift deletion.</summary>
    /// <returns><c>true</c> if the key was found and removed; <c>false</c> if not present.</returns>
    public bool TryRemove(TKey key, out TValue value)
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
                value = default;
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                value = ReadValue(entry, idx);
                _count--;
                BackwardShiftDelete(idx);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Return the existing value for <paramref name="key"/>, or add <paramref name="value"/> and return it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, TValue value)
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
                WriteValue(entry, idx, value);
                _count++;
                return value;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                return ReadValue(entry, idx);
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Update the value for an existing <paramref name="key"/>. Does not add if missing.</summary>
    /// <returns><c>true</c> if the key was found and the value updated; <c>false</c> if the key was not present.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryUpdate(TKey key, TValue newValue)
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
                WriteValue(entry, idx, newValue);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Update the value for <paramref name="key"/> only if the current value equals <paramref name="comparisonValue"/>.</summary>
    /// <returns><c>true</c> if the key was found and the value matched and was replaced; <c>false</c> otherwise.</returns>
    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
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
                if (!EqualityComparer<TValue>.Default.Equals(ReadValue(entry, idx), comparisonValue))
                {
                    return false;
                }
                WriteValue(entry, idx, newValue);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    /// <summary>Get or set the value for <paramref name="key"/>. Getter throws <see cref="KeyNotFoundException"/> if missing. Setter adds or overwrites.</summary>
    public TValue this[TKey key]
    {
        get
        {
            if (!TryGetValue(key, out TValue value))
            {
                throw new KeyNotFoundException($"Key not found: {key}");
            }
            return value;
        }
        set
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
                    WriteValue(entry, idx, value);
                    _count++;
                    return;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    WriteValue(entry, idx, value);
                    return;
                }

                idx = (idx + 1) & _mask;
            }
        }
    }

    /// <summary>Remove all entries. Clears managed value references if applicable. Does not shrink the buffer.</summary>
    public void Clear()
    {
        new Span<byte>(_entries, _capacity * _entryStride).Clear();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Array.Clear(_managedValues);
        }
        _count = 0;
    }

    /// <summary>Grow internal capacity so the map can hold at least <paramref name="minimumEntries"/> without resizing.</summary>
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

    /// <summary>Returns a <see langword="ref struct"/> enumerator over all key-value pairs.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Value-type enumerator yielding <c>(TKey Key, TValue Value)</c> tuples in memory order.</summary>
    public ref struct Enumerator
    {
        private readonly HashMap<TKey, TValue> _map;
        private int _index;

        internal Enumerator(HashMap<TKey, TValue> map)
        {
            _map = map;
            _index = -1;
        }

        public (TKey Key, TValue Value) Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (++_index < _map._capacity)
            {
                byte* entry = _map._entries + (long)_index * stride;
                if (*(uint*)entry != 0)
                {
                    TKey key = *(TKey*)(entry + 4);
                    TValue value = _map.ReadValue(entry, _index);
                    Current = (key, value);
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
        _managedValues = null;
        Parent.RemoveChild(this);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — value access (JIT-specialized)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TValue ReadValue(byte* entry, int idx)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            return Unsafe.Read<TValue>(entry + _valueOffset);
        }
        return _managedValues[idx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue(byte* entry, int idx, TValue value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Unsafe.Write(entry + _valueOffset, value);
        }
        else
        {
            _managedValues[idx] = value;
        }
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

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    _managedValues[idx] = _managedValues[j];
                }

                idx = j;
            }

            j = (j + 1) & _mask;
        }

        // Clear the gap
        *(uint*)(_entries + (long)idx * stride) = 0;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _managedValues[idx] = default;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — resize
    // ═══════════════════════════════════════════════════════════════════════

    private void Resize(int newCapacity)
    {
        int stride = _entryStride;
        var newBlock = _allocator.AllocatePinned($"{Id}/Entries", this, newCapacity * stride, true, 64);
        byte* newEntries = newBlock.DataAsPointer;
        int newMask = newCapacity - 1;

        TValue[] newManagedValues = null;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            newManagedValues = new TValue[newCapacity];
        }

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

                if (newManagedValues != null)
                {
                    newManagedValues[idx] = _managedValues[i];
                }
            }
        }

        var oldBlock = _block;
        _block = newBlock;
        _entries = newEntries;
        _capacity = newCapacity;
        _mask = newMask;
        _resizeThreshold = (int)(newCapacity * MaxLoadFactor);

        if (newManagedValues != null)
        {
            _managedValues = newManagedValues;
        }

        oldBlock.Dispose();
    }
}
