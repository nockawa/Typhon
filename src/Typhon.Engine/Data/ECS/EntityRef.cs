using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Zero-copy entity accessor. A ref struct (~96 bytes) that copies the EntityRecord from the per-archetype LinearHash and provides typed component access
/// via cached Location ChunkIds.
/// </summary>
/// <remarks>
/// <para>Created by <see cref="Transaction.Open"/> or <see cref="Transaction.OpenMut"/>. Must not outlive the creating transaction.</para>
/// <para>Read/Write operations delegate to the Transaction for chunk accessor management.</para>
/// </remarks>
[PublicAPI]
public unsafe ref struct EntityRef
{
    internal readonly EntityId _id;
    internal readonly ArchetypeMetadata _archetype;
    internal readonly ArchetypeEngineState _engineState;
    internal readonly Transaction _tx;
    internal ushort _enabledBits;
    internal readonly bool _writable;
    private fixed int _locations[16];

    internal EntityRef(EntityId id, ArchetypeMetadata archetype, ArchetypeEngineState engineState, Transaction tx, ushort enabledBits, bool writable)
    {
        _id = id;
        _archetype = archetype;
        _engineState = engineState;
        _tx = tx;
        _enabledBits = enabledBits;
        _writable = writable;
    }

    /// <summary>Copy locations from a raw EntityRecord byte pointer into this ref struct.</summary>
    internal void CopyLocationsFrom(byte* recordPtr, int componentCount)
    {
        for (int i = 0; i < componentCount; i++)
        {
            _locations[i] = EntityRecordAccessor.GetLocation(recordPtr, i);
        }
    }

    /// <summary>Override the chunkId at a specific slot. Used by ResolveEntity for MVCC revision chain resolution.</summary>
    internal void SetLocation(int slot, int chunkId) => _locations[slot] = chunkId;

    /// <summary>Copy locations from a managed byte array (for pending spawns).</summary>
    internal void CopyLocationsFrom(byte[] recordBytes, int componentCount)
    {
        fixed (byte* ptr = recordBytes)
        {
            CopyLocationsFrom(ptr, componentCount);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The entity's unique identifier.</summary>
    public EntityId Id => _id;

    /// <summary>The archetype ID of this entity.</summary>
    public ushort ArchetypeId => _id.ArchetypeId;

    /// <summary>True if this EntityRef refers to a valid entity.</summary>
    public bool IsValid => !_id.IsNull;

    /// <summary>True if this EntityRef allows writes.</summary>
    public bool IsWritable => _writable;

    // ═══════════════════════════════════════════════════════════════════════
    // Component access — by handle (O(1), preferred)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read a component by handle. Zero-copy — returns a ref into the chunk page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        Debug.Assert(slot < _archetype.ComponentCount, $"Slot {slot} out of range for archetype with {_archetype.ComponentCount} components");
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component at slot {slot} is disabled");

        int chunkId = _locations[slot];
        var table = _engineState.SlotToComponentTable[slot];
        return ref _tx.ReadEcsComponentData<T>(table, chunkId);
    }

    /// <summary>Write a component by handle. Returns a mutable ref into the chunk page.
    /// For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Write<T>(Comp<T> comp) where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only — use OpenMut for writes");
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        Debug.Assert(slot < _archetype.ComponentCount);
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component at slot {slot} is disabled");

        int chunkId = _locations[slot];
        var table = _engineState.SlotToComponentTable[slot];

        if (table.StorageMode == StorageMode.Versioned)
        {
            var (newChunkId, rawPtr) = _tx.EcsVersionedCopyOnWrite(typeof(T), _id, table);
            _locations[slot] = newChunkId;
            return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
        }

        return ref _tx.WriteEcsComponentData<T>(table, chunkId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component access — by type (slot lookup, slower)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read a component by type. Resolves slot via archetype metadata.</summary>
    public ref readonly T Read<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component type {typeof(T).Name} not registered");
        byte slot = _archetype.GetSlot(typeId);
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component {typeof(T).Name} at slot {slot} is disabled");

        int chunkId = _locations[slot];
        var table = _engineState.SlotToComponentTable[slot];
        return ref _tx.ReadEcsComponentData<T>(table, chunkId);
    }

    /// <summary>Write a component by type. Resolves slot via archetype metadata.
    /// For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers).</summary>
    public ref T Write<T>() where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only — use OpenMut for writes");
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component type {typeof(T).Name} not registered");
        byte slot = _archetype.GetSlot(typeId);
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component {typeof(T).Name} at slot {slot} is disabled");

        int chunkId = _locations[slot];
        var table = _engineState.SlotToComponentTable[slot];

        if (table.StorageMode == StorageMode.Versioned)
        {
            var (newChunkId, rawPtr) = _tx.EcsVersionedCopyOnWrite(typeof(T), _id, table);
            _locations[slot] = newChunkId;
            return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
        }

        return ref _tx.WriteEcsComponentData<T>(table, chunkId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enable/Disable
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Check if a component at the given slot is enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(byte slotIndex) => (_enabledBits & (1 << slotIndex)) != 0;

    /// <summary>Check if a component is enabled by handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        return (_enabledBits & (1 << slot)) != 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Optional component access — TryRead
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to read a component by type. Returns false if the archetype doesn't declare the component or it's disabled.
    /// Returns a copy (not ref) since out parameters can't be ref readonly.
    /// For zero-copy, use <c>if (entity.IsEnabled(comp)) { ref readonly var v = ref entity.Read(comp); }</c>.
    /// </summary>
    public bool TryRead<T>(out T value) where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (typeId < 0 || !_archetype.TryGetSlot(typeId, out byte slot))
        {
            value = default;
            return false;
        }
        if ((_enabledBits & (1 << slot)) == 0)
        {
            value = default;
            return false;
        }
        int chunkId = _locations[slot];
        var table = _engineState.SlotToComponentTable[slot];
        value = _tx.ReadEcsComponentData<T>(table, chunkId);
        return true;
    }

    /// <summary>Disable a component by handle. Stages the change for commit.</summary>
    public void Disable<T>(Comp<T> comp) where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only");
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        _enabledBits &= (ushort)~(1 << slot);
        _tx.StageEnableDisable(_id, _enabledBits);
    }

    /// <summary>Enable a component by handle. Stages the change for commit.</summary>
    public void Enable<T>(Comp<T> comp) where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only");
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        _enabledBits |= (ushort)(1 << slot);
        _tx.StageEnableDisable(_id, _enabledBits);
    }
}
