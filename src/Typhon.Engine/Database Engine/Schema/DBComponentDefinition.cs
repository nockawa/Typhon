// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Typhon.Engine;

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
    
    Unsigned    = 256,
    DoubleFloat = 512
}

[PublicAPI]
public class DBComponentDefinition
{
    public string Name { get; private set; }
    public int Revision { get; private set; }
    public Type POCOType { get; internal set; }
    public string FullName => FormatFullName(Name, Revision);
    
    internal static string FormatFullName(string componentName, int revision) => $"{componentName}:R{revision}";

    private readonly Dictionary<string, Field> _fieldsByName;
    private Field[] _fieldsById;

    public IReadOnlyDictionary<string, Field> FieldsByName => _fieldsByName;

    public int MaxFieldId { get; private set; }
    public Field this[int index] => _fieldsById[index];

    public int GetFieldId(string fieldName)
    {
        if (!_fieldsByName.TryGetValue(fieldName, out var field))
        {
            return -1;
        }

        return field.FieldId;
    }

    public int ComponentStorageSize { get; private set; }
    public int ComponentStorageOverhead => MultipleIndicesCount * sizeof(int);
    public int ComponentStorageTotalSize => ComponentStorageSize + ComponentStorageOverhead;

    public int IndicesCount { get; private set; }
    public int MultipleIndicesCount { get; private set; }

    [DebuggerDisplay("Id: {FieldId}, Name: {Name}, Type: {Type}, OffsetInComponentStorage: {OffsetInComponentStorage}")]
    [PublicAPI]
    public class Field
    {
        public Field(int fieldId, string name, FieldType type, FieldType underlyingType, int offsetInComponentStorage, Type dotNetType)
        {
            FieldId = fieldId;
            Name = name;
            Type = type;
            DotNetType = dotNetType;
            if (Type == FieldType.Collection)
            {
                DotNetUnderlyingType = dotNetType.GenericTypeArguments[0];
            }
            FieldSize = Type.SizeInComp();
            UnderlyingType = underlyingType;
            OffsetInComponentStorage = offsetInComponentStorage;
        }
        
        public int FieldId { get; }

        public string Name { get; }

        public FieldType Type { get; }
        public FieldType UnderlyingType { get; }

        public int OffsetInComponentStorage { get; }
        public Type DotNetType { get; }
        public Type DotNetUnderlyingType { get; }
        public int FieldSize { get; }

        public bool IsStatic { get; set; }

        public bool HasIndex { get; set; }
            
        public bool IndexAllowMultiple { get; set; }

        public bool IsIndexAuto { get; set; }
            
        public bool IsArray => ArrayLength > 0;
            
        public int ArrayLength { get; set; }

        public static void CheckName(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || new Regex("^[A-Za-z]+$").IsMatch(fieldName) == false)
            {
                throw new ArgumentException("Field name must be a single word of UTF8 size not exceeding 64 bytes", nameof(fieldName));
            }
            if (Encoding.UTF8.GetByteCount(fieldName) > 63)
            {
                throw new ArgumentException($"The given field name '{fieldName}' is exceeding the size limit of 64 bytes", nameof(fieldName));
            }
        }

        public static void CheckType(FieldType fieldType)
        {
            if (Enum.IsDefined(fieldType) == false || fieldType==FieldType.None)
            {
                throw new ArgumentException($"The given field type is not valid");
            }
        }

        public int SizeInComponentStorage => Type.SizeInComp() * (IsArray ? ArrayLength : 1);
        public bool DoesFieldTypeSupportIndex() => (Type >= FieldType.Byte) && ((FieldType)((int)Type&0xFF) <= FieldType.String64);
    }

    internal DBComponentDefinition(string name, int revision)
    {
        Name = name;
        Revision = revision;
        _fieldsByName = new Dictionary<string, Field>();
    }

    public Field CreateField(int fieldId, string name, FieldType type, FieldType underlyingType, int offset, Type dotNetType)
    {
        if (_fieldsByName.ContainsKey(name))
        {
            throw new ArgumentException($"The field name '{name}' is already taken", nameof(name));
        }
        var field = new Field(fieldId, name, type, underlyingType, offset, dotNetType);
        _fieldsByName.Add(name, field);
        return field;
    }

    internal void Build()
    {
        var fields = _fieldsByName.Values;
        var max = fields.Where(f => f.IsStatic == false).Max(v => v.FieldId);

        MaxFieldId = max + 1;
        _fieldsById = new Field[MaxFieldId];

        var ids = new HashSet<int>();
        var names = new HashSet<string>();
        var offsets = new Dictionary<int, Field>();

        Field lastField = null;
        IndicesCount = 0;

        foreach (var field in fields.Where(f => f.IsStatic == false))
        {
            if (ids.Add(field.FieldId) == false)
            {
                throw new Exception($"Duplicate FieldId {field.FieldId}, defined on both {field.Name} and {_fieldsById[field.FieldId].Name}. Each field must have a unique FieldId.");
            }

            if (names.Add(field.Name) == false)
            {
                throw new Exception($"Duplicate Field's name {field.Name}. Each field must have a unique name.");
            }

            if (offsets.TryGetValue(field.OffsetInComponentStorage, out Field offset))
            {
                throw new Exception($"Duplicate Field's offset {field.OffsetInComponentStorage}, declare in both {field.Name} and {offset.Name}. Each field must have a different.");
            }
            offsets.Add(field.OffsetInComponentStorage, field);

            _fieldsById[field.FieldId] = field;

            if (field.HasIndex)
            {
                if (!field.DoesFieldTypeSupportIndex())
                {
                    throw new Exception($"The field type {field.Type} does not support index for field {field.Name}.");
                }
                ++IndicesCount;
                if (field.IndexAllowMultiple)
                {
                    ++MultipleIndicesCount;
                }
            }

            if (lastField == null || lastField.OffsetInComponentStorage < field.OffsetInComponentStorage)
            {
                lastField = field;
            }
        }

        if (lastField == null)
        {
            throw new Exception("We didn't detect at least one field... Fields must be public field (not property), not static and of a compatible data type.");
        }

        ComponentStorageSize = lastField.OffsetInComponentStorage + lastField.SizeInComponentStorage;
    }
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

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ComponentCollection<>))
        {
            return (FieldType.Collection, FromType(t.GenericTypeArguments[0]).field);
        }

        return (FieldType.None, FieldType.None);
    }

    public static int SizeInComp(this FieldType field)
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
            
            case FieldType.Collection: return 4;

            default: return 0;
        }
    }
}