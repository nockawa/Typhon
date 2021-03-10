// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Typhon.Engine
{
    public enum FieldType
    {
        None = 0,
        Boolean = 1,
        Byte = 2,
        Short = 3,
        Int = 4,
        Long = 5,
        UByte = 256 + 2,
        UShort = 256 + 3,
        UInt = 256 + 4,
        ULong = 256 + 5,
        Float = 6,
        Double = 7,
        Char = 8,
        String = 9,
        String64 = 10,
        String1024 = 11,
        Point2F = 12,
        Point3F = 13,
        Point4F = 14,
        Point2D = 512 + 12,
        Point3D = 512 + 13,
        Point4D = 512 + 14,
        QuaternionF = 15,
        QuaternionD = 512 + 15,
    }

    public class DBComponentDefinition
    {
        public string Name { get; set; }

        private readonly Dictionary<string, Field> _fieldsByName;
        private Field[] _fieldsById;

        public IReadOnlyDictionary<string, Field> FieldsByName => _fieldsByName;

        public Field this[int index] => _fieldsById[index];

        public int RowSize { get; private set; }

        public int IndicesCount { get; private set; }

        [DebuggerDisplay("Id: {FieldId}, Name: {Name}, Type: {Type}, OffsetInRow: {OffsetInRow}")]
        public class Field
        {
            private bool _isPrimaryKey;

            public Field(int fieldId, string name, FieldType type, int offsetInRow)
            {
                FieldId = fieldId;
                Name = name;
                Type = type;
                OffsetInRow = offsetInRow;
            }

            public int FieldId { get; }

            public string Name { get; }

            public FieldType Type { get; }

            public int OffsetInRow { get; }

            public bool IsStatic { get; set; }

            public bool IsPrimaryKey
            {
                get => _isPrimaryKey;
                set
                {
                    HasIndex = value;
                    _isPrimaryKey = value;
                }
            }

            public bool HasIndex { get; set; }
            
            public bool IndexAllowMultiple { get; set; }
            
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

            public int SizeInRow => Type.SizeInRow() * (IsArray ? ArrayLength : 1);
        }

        internal DBComponentDefinition(string name)
        {
            Name = name;
            _fieldsByName = new Dictionary<string, Field>();
        }

        public Field CreateField(int fieldId, string name, FieldType type, int offset)
        {
            if (_fieldsByName.ContainsKey(name))
            {
                throw new ArgumentException($"The field name '{name}' is already taken", nameof(name));
            }
            var field = new Field(fieldId, name, type, offset);
            _fieldsByName.Add(name, field);
            return field;
        }

        internal void Build()
        {
            var fields = _fieldsByName.Values;
            var max = fields.Where(f => f.IsStatic == false).Max(v => v.FieldId);
            var hasPK = fields.Any(f => f.IsPrimaryKey);

            _fieldsById = new Field[max+1];

            var ids = new HashSet<int>();
            var names = new HashSet<string>();
            var offsets = new Dictionary<int, Field>();

            Field lastField = null;
            var indicesCount = 0;

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

                if (offsets.ContainsKey(field.OffsetInRow))
                {
                    throw new Exception($"Duplicate Field's offset {field.OffsetInRow}, declare in both {field.Name} and {offsets[field.OffsetInRow].Name}. Each field must have a different.");
                }
                offsets.Add(field.OffsetInRow, field);

                // By default, if not specified by the user, the primary key is set to the field of ID[0]
                if (hasPK == false && field.FieldId == 0) field.IsPrimaryKey = true;

                _fieldsById[field.FieldId] = field;

                if (field.HasIndex) ++indicesCount;

                if (lastField == null || lastField.OffsetInRow < field.OffsetInRow)
                {
                    lastField = field;
                }
            }

            if (lastField == null) throw new Exception("We didn't detect at least one field... Fields must be public field (not property), not static and of a compatible data type.");

            RowSize = lastField.OffsetInRow + lastField.SizeInRow;
        }
    }

    public static class DatabaseSchemaExtensions
    {
        public static FieldType FromType<T>() => FromType(typeof(T));

        public static FieldType FromType(Type t)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: return FieldType.Boolean;

                case TypeCode.Byte: return FieldType.UByte;
                case TypeCode.SByte: return FieldType.Byte;
                case TypeCode.Char: return FieldType.Char;

                case TypeCode.Double: return FieldType.Double;

                case TypeCode.Int16: return FieldType.Short;
                case TypeCode.Int32: return FieldType.Int;
                case TypeCode.Int64: return FieldType.Long;
                case TypeCode.UInt16: return FieldType.UShort;
                case TypeCode.UInt32: return FieldType.UInt;
                case TypeCode.UInt64: return FieldType.ULong;
            }

            if (t == typeof(float)) return FieldType.Float;
            if (t == typeof(String64)) return FieldType.String64;
            if (t == typeof(String1024)) return FieldType.String1024;
            if (t == typeof(VarString)) return FieldType.String;

            if (t == typeof(Point2F)) return FieldType.Point2F;
            if (t == typeof(Point3F)) return FieldType.Point3F;
            if (t == typeof(Point4F)) return FieldType.Point4F;

            if (t == typeof(Point2D)) return FieldType.Point2D;
            if (t == typeof(Point3D)) return FieldType.Point3D;
            if (t == typeof(Point4D)) return FieldType.Point4D;

            if (t == typeof(QuaternionF)) return FieldType.QuaternionF;
            if (t == typeof(QuaternionD)) return FieldType.QuaternionD;

            return FieldType.None;

        }

        public static int SizeInRow(this FieldType field)
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

                default: return 0;
            }
        }
    }
}