using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Typhon.Engine")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]

namespace Typhon.Schema.Definition;

[AttributeUsage(AttributeTargets.Struct)]
[PublicAPI]
public sealed class ComponentAttribute : Attribute
{
    public string Name { get; }
    public int Revision { get; }
    public bool AllowMultiple { get; }

    public string PreviousName { get; set; }

    /// <summary>Storage mode for this component. Default is <see cref="StorageMode.Versioned"/> (full MVCC).</summary>
    public StorageMode StorageMode { get; set; } = StorageMode.Versioned;

    public ComponentAttribute(string name, int revision, bool allowMultiple = false)
    {
        Name = name;
        Revision = revision;
        AllowMultiple = allowMultiple;
    }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class FieldAttribute : Attribute
{
    public int? FieldId { get; set; }
    public string Name { get; set; }
    public string PreviousName { get; set; }
}

/// <summary>Cascade action when a parent entity is deleted.</summary>
[PublicAPI]
public enum CascadeAction
{
    /// <summary>No cascade — children are unaffected.</summary>
    None = 0,

    /// <summary>Delete all children whose FK points to the destroyed parent.</summary>
    Delete = 1,
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class IndexAttribute : Attribute
{
    public bool AllowMultiple { get; set; }

    /// <summary>
    /// Cascade action when the parent entity (referenced by an <see cref="EntityLink{T}"/> FK field) is deleted.
    /// Only applicable to indexed EntityLink fields. Default is <see cref="CascadeAction.None"/>.
    /// </summary>
    public CascadeAction OnParentDelete { get; set; } = CascadeAction.None;
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class ForeignKeyAttribute : Attribute
{
    public Type TargetComponentType { get; }

    public ForeignKeyAttribute(Type targetComponentType)
    {
        ArgumentNullException.ThrowIfNull(targetComponentType);
        TargetComponentType = targetComponentType;
    }
}

/// <summary>
/// Marks a class as an ECS archetype with a globally unique, immutable identifier.
/// The Id is embedded in every EntityId (12-bit field, max 4095) and must never change once assigned.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[PublicAPI]
public sealed class ArchetypeAttribute : Attribute
{
    /// <summary>Globally unique archetype identifier (0-4095). Embedded in persisted EntityIds — immutable once assigned.</summary>
    public ushort Id { get; }

    /// <summary>Schema revision. Increment when the component set changes (add/remove components).</summary>
    public int Revision { get; }

    public ArchetypeAttribute(ushort id, int revision = 1)
    {
        Id = id;
        Revision = revision;
    }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class SpatialIndexAttribute : Attribute
{
    public float Margin { get; }
    public float CellSize { get; }

    public SpatialIndexAttribute(float margin, float cellSize = 0f)
    {
        Margin = margin;
        CellSize = cellSize;
    }
}
