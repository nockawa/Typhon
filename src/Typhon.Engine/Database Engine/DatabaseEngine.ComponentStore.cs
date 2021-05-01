using System;
using System.Collections.Concurrent;
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
        private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;
        private long _curPrimaryKey;

        private void ConstructComponentStore()
        {
            _componentTableByType = new ConcurrentDictionary<Type, ComponentTable>();
            _curPrimaryKey = 0;
            
        }

        internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

        private unsafe void CreateComponentStore(RootFileHeader* rootFileHeader)
        {
            RegisterComponentFromRowAccessor<FieldRow>();
            RegisterComponentFromRowAccessor<ComponentRow>();

            _fieldsTable = GetComponentTable<FieldRow>();
            _componentsTable = GetComponentTable<ComponentRow>();

            rootFileHeader->DatabaseEngine = SerializeSettings();
        }

        public bool RegisterComponentFromRowAccessor<T>() where T : unmanaged
        {
            var dcd = _dbd.CreateFromRowAccessor<T>();
            if (dcd == null) return false;

            var componentTable = new ComponentTable();
            componentTable.Create(this, _dbd.GetComponent(dcd.Name));
            _componentTableByType.TryAdd(typeof(T), componentTable);

            return true;
        }

        public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

        public ComponentTable GetComponentTable(Type type)
        {
            if (_componentTableByType.TryGetValue(type, out var ct) == false)
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
