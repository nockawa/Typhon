// unset

using JetBrains.Annotations;
using System;
using System.Reflection;

namespace Typhon.Schema.Definition;

[PublicAPI]
[Flags]
public enum FieldType
{
    None        = 0,
    Boolean     = 1,
    Byte        = 2,
    Short       = 3,
    Int         = 4,
    Long        = 5,
    UByte       = Unsigned | Byte,
    UShort      = Unsigned | Short,
    UInt        = Unsigned | Int,
    ULong       = Unsigned | Long,
    Float       = 6,
    Double      = DoubleFloat | Float,
    Char        = 7,
    String64    = 8,
    String1024  = 9,
    String      = 10,
    Variant     = String64 | 11,            // Use the Variant type, a String64 of the form "tt:data" storing data of a given type
    Point2F     = 12,
    Point3F     = 13,
    Point4F     = 14,
    Point2D     = DoubleFloat | Point2F,
    Point3D     = DoubleFloat | Point3F,
    Point4D     = DoubleFloat | Point4F,
    QuaternionF = 15,
    QuaternionD = DoubleFloat |  15,

    Collection  = 16,
    Component   = 17,

    AABB2F      = 18,
    AABB3F      = 19,
    BSphere2F   = 20,
    BSphere3F   = 21,
    AABB2D      = DoubleFloat | AABB2F,
    AABB3D      = DoubleFloat | AABB3F,
    BSphere2D   = DoubleFloat | BSphere2F,
    BSphere3D   = DoubleFloat | BSphere3F,

    Unsigned    = 256,
    DoubleFloat = 512
}

public static class DatabaseSchemaExtensions
{
    public static (FieldType field, FieldType under) FromType<T>() => FromType(typeof(T));

    public static (FieldType field, FieldType under) FromType(Type t)
    {
        switch (Type.GetTypeCode(t))
        {
            case TypeCode.Boolean: return (FieldType.Boolean, FieldType.None);

            case TypeCode.Byte: return (FieldType.UByte, FieldType.None);
            case TypeCode.SByte: return (FieldType.Byte, FieldType.None);
            case TypeCode.Char: return (FieldType.Char, FieldType.None);

            case TypeCode.Double: return (FieldType.Double, FieldType.None);

            case TypeCode.Int16: return (FieldType.Short, FieldType.None);
            case TypeCode.Int32: return (FieldType.Int, FieldType.None);
            case TypeCode.Int64: return (FieldType.Long, FieldType.None);
            case TypeCode.UInt16: return (FieldType.UShort, FieldType.None);
            case TypeCode.UInt32: return (FieldType.UInt, FieldType.None);
            case TypeCode.UInt64: return (FieldType.ULong, FieldType.None);
        }

        var ca = t.GetCustomAttribute<ComponentAttribute>();
        if (ca != null)
        {
            return (FieldType.Component, FieldType.None);
        }

        if (t == typeof(float)) return (FieldType.Float, FieldType.None);
        if (t == typeof(String64)) return (FieldType.String64, FieldType.None);
        if (t == typeof(String1024)) return (FieldType.String1024, FieldType.None);
        if (t == typeof(VarString)) return (FieldType.String, FieldType.None);

        if (t == typeof(Point2F)) return (FieldType.Point2F, FieldType.None);
        if (t == typeof(Point3F)) return (FieldType.Point3F, FieldType.None);
        if (t == typeof(Point4F)) return (FieldType.Point4F, FieldType.None);

        if (t == typeof(Point2D)) return (FieldType.Point2D, FieldType.None);
        if (t == typeof(Point3D)) return (FieldType.Point3D, FieldType.None);
        if (t == typeof(Point4D)) return (FieldType.Point4D, FieldType.None);

        if (t == typeof(QuaternionF)) return (FieldType.QuaternionF, FieldType.None);
        if (t == typeof(QuaternionD)) return (FieldType.QuaternionD, FieldType.None);

        if (t == typeof(AABB2F)) return (FieldType.AABB2F, FieldType.None);
        if (t == typeof(AABB3F)) return (FieldType.AABB3F, FieldType.None);
        if (t == typeof(BSphere2F)) return (FieldType.BSphere2F, FieldType.None);
        if (t == typeof(BSphere3F)) return (FieldType.BSphere3F, FieldType.None);
        if (t == typeof(AABB2D)) return (FieldType.AABB2D, FieldType.None);
        if (t == typeof(AABB3D)) return (FieldType.AABB3D, FieldType.None);
        if (t == typeof(BSphere2D)) return (FieldType.BSphere2D, FieldType.None);
        if (t == typeof(BSphere3D)) return (FieldType.BSphere3D, FieldType.None);

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ComponentCollection<>))
        {
            return (FieldType.Collection, FromType(t.GenericTypeArguments[0]).field);
        }

        // EntityLink<T> is an 8-byte FK reference (wraps EntityId) — index as Long.
        // Check by name since EntityLink<> is in Typhon.Engine, not Typhon.Schema.Definition.
        if (t.IsGenericType && t.GetGenericTypeDefinition().Name == "EntityLink`1")
        {
            return (FieldType.Long, FieldType.None);
        }

        return (FieldType.None, FieldType.None);
    }

    public static int FieldSizeInComp(this FieldType field)
    {
        switch (field)
        {
            case FieldType.UByte:
            case FieldType.Byte:
            case FieldType.Boolean: return 1;

            case FieldType.Char:
            case FieldType.Short:
            case FieldType.UShort: return 2;

            case FieldType.Float:
            case FieldType.UInt:
            case FieldType.Int: return 4;

            case FieldType.Double:
            case FieldType.ULong:
            case FieldType.Long: return 8;

            case FieldType.Point2F: return 8;
            case FieldType.Point3F: return 12;
            case FieldType.Point4F: return 16;

            case FieldType.Point2D: return 16;
            case FieldType.Point3D: return 24;
            case FieldType.Point4D: return 32;

            case FieldType.String: return 32;  // Don't count the overflow in VSB
            case FieldType.String64: return 64;
            case FieldType.String1024: return 1024;

            case FieldType.QuaternionF: return 16;
            case FieldType.QuaternionD: return 32;

            case FieldType.AABB2F: return 16;
            case FieldType.AABB3F: return 24;
            case FieldType.BSphere2F: return 12;
            case FieldType.BSphere3F: return 16;
            case FieldType.AABB2D: return 32;
            case FieldType.AABB3D: return 48;
            case FieldType.BSphere2D: return 24;
            case FieldType.BSphere3D: return 32;

            case FieldType.Collection: return 4;
            case FieldType.Component: return 8;

            default: return 0;
        }
    }
}
