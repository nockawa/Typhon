using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Metadata for a registered archetype: identity, component slots, parent-child graph, and per-archetype entity storage.
/// Populated during static initialization, immutable after <see cref="ArchetypeRegistry.Freeze"/>.
/// </summary>
internal class ArchetypeMetadata
{
    /// <summary>Globally unique archetype ID from [Archetype(Id = N)] attribute. Embedded in EntityId.</summary>
    public ushort ArchetypeId;

    /// <summary>Schema revision from [Archetype(Id, Revision)] attribute.</summary>
    public int Revision;

    /// <summary>Total component count (own + inherited). Max 16.</summary>
    public byte ComponentCount;

    /// <summary>Parent archetype ID. <see cref="NoParent"/> (0xFFFF) for root archetypes.</summary>
    public ushort ParentArchetypeId = NoParent;

    /// <summary>Sentinel value indicating no parent archetype (root). Outside the valid 12-bit ArchetypeId range.</summary>
    public const ushort NoParent = 0xFFFF;

    /// <summary>Direct children (mutable during registration, frozen after).</summary>
    public readonly List<ushort> ChildArchetypeIds = [];

    /// <summary>Self + all descendants (populated during Freeze).</summary>
    public ushort[] SubtreeArchetypeIds;

    /// <summary>CLR type of the archetype class.</summary>
    public Type ArchetypeType;

    // ═══════════════════════════════════════════════════════════════════════
    // Slot mapping
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>[slotIndex] → ComponentTypeId. Length == ComponentCount.</summary>
    internal int[] _componentTypeIds;

    /// <summary>ComponentTypeId → slotIndex (reverse lookup).</summary>
    internal Dictionary<int, byte> _typeIdToSlot = new();

    // ═══════════════════════════════════════════════════════════════════════
    // Component → ComponentTable mapping (initialized by DatabaseEngine)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>[slotIndex] → ComponentTable that stores this component type. Length == ComponentCount.</summary>
    internal ComponentTable[] _slotToComponentTable;

    /// <summary>[slotIndex] → CLR Type of the component at this slot. Length == ComponentCount.</summary>
    internal Type[] _slotToComponentType;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype entity storage (initialized by DatabaseEngine)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype HashMap storing EntityRecords keyed by EntityKey (long).</summary>
    internal RawValueHashMap<long, PersistentStore> _entityMap;

    /// <summary>Monotonic entity key counter. Use Interlocked.Increment for thread-safe generation.</summary>
    internal long _nextEntityKey;

    /// <summary>Cached entity record size: 14 + ComponentCount * 4 bytes.</summary>
    internal int _entityRecordSize;

    // ═══════════════════════════════════════════════════════════════════════
    // Cascade delete graph (populated during Freeze)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Children that should be cascade-deleted when an entity of this archetype is destroyed.
    /// Null or empty if no cascade targets. Populated during <see cref="ArchetypeRegistry.Freeze"/>.
    /// </summary>
    internal List<CascadeTarget> _cascadeTargets;

    /// <summary>Get the slot index for a component type ID. Returns the slot index (0..ComponentCount-1).</summary>
    public byte GetSlot(int componentTypeId) => _typeIdToSlot[componentTypeId];

    /// <summary>Check whether this archetype has a component with the given type ID.</summary>
    public bool HasComponent(int componentTypeId) => _typeIdToSlot.ContainsKey(componentTypeId);
}

/// <summary>
/// Describes a cascade delete edge: when a parent entity is destroyed, find and destroy children in the specified child archetype via the FK index.
/// </summary>
internal struct CascadeTarget
{
    /// <summary>Archetype ID of the child that should be cascade-deleted.</summary>
    public ushort ChildArchetypeId;

    /// <summary>CLR Type of the child archetype (for logging).</summary>
    public Type ChildArchetypeType;

    /// <summary>Slot index of the component containing the FK field on the child archetype.</summary>
    public byte FkSlotIndex;
}
