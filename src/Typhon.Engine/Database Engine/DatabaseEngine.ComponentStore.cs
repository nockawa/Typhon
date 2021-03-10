using System;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;

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
        public IndexAttribute()
        {
        }
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
        public int RowSize;

        public uint TableSPI;

        public ComponentCollection<FieldRow> Fields;
    }

    public struct ComponentCollection<T> where T : unmanaged
    {

    }

    internal struct ComponentRowHeader
    {
        public long Revision;
    }

    public class ComponentTable : IDisposable
    {
        private const int ComponentSegmentStartingSize = 4;
        private const int MainIndexSegmentStartingSize = 4;

        private DatabaseEngine _dbe;
        private ChunkBasedSegmentAccessorPool _componentAccessPool;
        private ChunkBasedSegmentAccessorPool _indexAccessPool;

        public ChunkBasedSegment ComponentSegment { get; private set; }
        public ChunkBasedSegment MainIndexSegment { get; private set; }

        private BTree<long> _mainIndex;
        private DBComponentDefinition _definition;
        private unsafe int _rowOverhead => sizeof(ComponentRowHeader) + (_definition.IndicesCount * sizeof(int));
        private int _rowTotalSize => _definition.RowSize + _rowOverhead;

        unsafe public void Create(DatabaseEngine dbe, DBComponentDefinition definition)
        {
            _dbe = dbe;
            _definition = definition;

            var lsm = _dbe.LSM;

            ComponentSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, _rowTotalSize);
            MainIndexSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));

            _componentAccessPool = new ChunkBasedSegmentAccessorPool(ComponentSegment, 4, 4);
            _indexAccessPool = new ChunkBasedSegmentAccessorPool(MainIndexSegment, 4, 4);

            _mainIndex = new LongSingleBTree(MainIndexSegment, _indexAccessPool);
        }

        public void Dispose()
        {
            if (ComponentSegment == null) return;

            _indexAccessPool.Dispose();
            _componentAccessPool.Dispose();

            MainIndexSegment.Dispose();
            ComponentSegment.Dispose();

            ComponentSegment = null;
        }
    }

    partial class DatabaseEngine
    {
        private ComponentTable _fieldsTable;
        private ComponentTable _componentsTable;

        private unsafe void CreateComponentStore(RootFileHeader* rootFileHeader)
        {
            _databaseDefinitions.CreateFromRowAccessor<FieldRow>();
            _databaseDefinitions.CreateFromRowAccessor<ComponentRow>();

            _fieldsTable = new ComponentTable();
            _fieldsTable.Create(this, _databaseDefinitions.GetComponent(FieldRow.SchemaName));

            _componentsTable = new ComponentTable();
            _componentsTable.Create(this, _databaseDefinitions.GetComponent(ComponentRow.SchemaName));

            rootFileHeader->SchemaFieldTableCSPI = _fieldsTable.ComponentSegment.RootPageId;
            rootFileHeader->SchemaFieldTableISPI = _fieldsTable.MainIndexSegment.RootPageId;
            rootFileHeader->SchemaComponentTableCSPI = _componentsTable.ComponentSegment.RootPageId;
            rootFileHeader->SchemaComponentTableISPI = _componentsTable.MainIndexSegment.RootPageId;
        }
    }
}
