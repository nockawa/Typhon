using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class ComponentAttribute : Attribute
    {
        public string Name { get; }

        public ComponentAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class FieldAttribute : Attribute
    {
        public int? FieldId { get; set; }
        public string Name { get; set; }
        public bool IsPrimaryKey { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class IndexAttribute : Attribute
    {
        public bool AllowMultiple { get; set; }
    }

    [Component(SchemaName)]
    [StructLayout(LayoutKind.Sequential)]
    public struct FieldRow
    {
        public const string SchemaName = "Typhon.Schema.Field";

        [Field(IsPrimaryKey = true)]
        public int Id;

        public String64 Name;
        
        [Index(AllowMultiple = true)]
        public int ComponentFK;
        
        public int FieldId;
        public FieldType Type;
        public uint IndexSPI;
        public bool IsStatic;
        public bool HasIndex;
        public bool IndexAllowMultiple;
        public int ArrayLength;
        public bool IsArray => ArrayLength > 0;
    }

    [Component(SchemaName)]
    [StructLayout(LayoutKind.Sequential)]
    public struct ComponentRow
    {
        public const string SchemaName = "Typhon.Schema.Component";

        [Field(IsPrimaryKey = true)]
        public int Id;

        public String64 Name;
        public String64 POCOType;
        public int RowSize;

        public uint TableSPI;

        public ComponentCollection<FieldRow> Fields;
    }

    public struct ComponentCollection<T> where T : unmanaged
    {

    }

    partial class DatabaseEngine
    {
        private ComponentTable _fieldsTable;
        private ComponentTable _componentsTable;
        private ConcurrentDictionary<string, ComponentTable> _componentTableByTypeName;
        private long _curPrimaryKey;

        private void ConstructComponentStore()
        {
            _componentTableByTypeName = new ConcurrentDictionary<string, ComponentTable>();
            _curPrimaryKey = 0;
            
        }

        internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

        private unsafe void CreateComponentStore(RootFileHeader* rootFileHeader)
        {
            _databaseDefinitions.CreateFromRowAccessor<FieldRow>();
            _databaseDefinitions.CreateFromRowAccessor<ComponentRow>();

            _fieldsTable = new ComponentTable();
            _fieldsTable.Create(this, _databaseDefinitions.GetComponent(FieldRow.SchemaName));

            _componentsTable = new ComponentTable();
            _componentsTable.Create(this, _databaseDefinitions.GetComponent(ComponentRow.SchemaName));

            rootFileHeader->DatabaseEngine = SerializeSettings();
        }

        public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

        public ComponentTable GetComponentTable(Type type)
        {
            string rowAccessorTypeName = type.FullName;
            if (_componentTableByTypeName.TryGetValue(rowAccessorTypeName, out var ct) == false)
            {
                return null;
            }
            
            return ct;
        }
        internal struct SerializationData
        {
            public ComponentTable.SerializationData FieldsTable;
            public ComponentTable.SerializationData ComponentsTable;
        }

        internal SerializationData SerializeSettings() =>
            new()
            {
                FieldsTable = _fieldsTable.SerializeSettings(),
                ComponentsTable = _componentsTable.SerializeSettings()
            };
    }
}
