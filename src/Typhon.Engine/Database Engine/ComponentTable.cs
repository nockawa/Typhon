// unset

using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;

namespace Typhon.Engine
{
    internal struct RowVersionStorageHeader
    {
        public int NextChunkId;
        public AccessControlSmall Control;
        public int Revision;
        public short FirstItemIndex;
        public short ItemCount;
        public int ChainLength;
    }

    internal struct RowVersionStorageElement
    {
        public long Tick;
        public int RowChunkId;
    }

    [DebuggerDisplay("Offset: {OffsetToField} Size: {Size} HasIndex {HasIndex}")]
    internal struct IndexedFieldInfo
    {
        public int OffsetToField;
        public int Size;

        public int OffsetToIndexElementId;
        public IBTree Index;
    }

    public unsafe class ComponentTable : IDisposable
    {
        private const int ComponentSegmentStartingSize = 4;
        private const int MainIndexSegmentStartingSize = 4;

        internal const int RowVersionCountPerChunk = 8;
        internal static readonly int RowVersionDataChunkSize = sizeof(RowVersionStorageHeader) + (RowVersionCountPerChunk * sizeof(RowVersionStorageElement));

        public ChunkBasedSegment ComponentSegment { get; private set; }
        public ChunkBasedSegment VersionTableSegment { get; private set; }
        public ChunkBasedSegment DefaultIndexSegment { get; private set; }
        public ChunkBasedSegment String64IndexSegment { get; private set; }
        public LongSingleBTree PrimaryKeyIndex { get; private set; }
        public int ComponentRowSize => _definition.RowSize;

        internal DatabaseEngine DBE { get; private set; }
        private DBComponentDefinition _definition;
        internal int RowOverhead => _definition.MultipleIndicesCount * sizeof(int);
        internal int RowTotalSize => _definition.RowSize + RowOverhead;
        internal IndexedFieldInfo[] IndexedFieldInfos { get; private set; }

        public void Create(DatabaseEngine dbe, DBComponentDefinition definition)
        {
            DBE = dbe;
            _definition = definition;

            var lsm = DBE.LSM;

            ComponentSegment    = lsm.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, RowTotalSize);
            VersionTableSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, RowVersionDataChunkSize);
            
            // This segment will be used for all kind of index types except String64 which needs a dedicated one because its chunk size is different (all others are 64 bytes)
            DefaultIndexSegment  = lsm.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));
            String64IndexSegment = lsm.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(IndexString64Chunk));

            PrimaryKeyIndex = new LongSingleBTree(DefaultIndexSegment, null);

            BuildIndexedFieldInfo();
        }

        private void BuildIndexedFieldInfo()
        {
            var l = new List<IndexedFieldInfo>();

            var ro = RowOverhead;

            for (int i = 0, j = 0; i < _definition.MaxFieldId; i++)
            {
                var f = _definition[i];
                if (f == null || !f.HasIndex) continue;

                var fi = new IndexedFieldInfo
                {
                    OffsetToField = ro + f.OffsetInRow, 
                    Size          = f.SizeInRow, 
                    Index         = CreateIndexForField(f),
                };
                fi.OffsetToIndexElementId = fi.Index.AllowMultiple ? (j++ * sizeof(int)) : 0;
                l.Add(fi);
            }

            IndexedFieldInfos = l.ToArray();
        }

        private IBTree CreateIndexForField(DBComponentDefinition.Field field)
        {
            var s = field.Type == FieldType.String64 ? String64IndexSegment : DefaultIndexSegment;

            switch (field.Type)
            {
                case FieldType.Byte:        return field.IndexAllowMultiple ? new ByteMultipleBTree(s, null)     : new ByteSingleBTree(s, null);
                case FieldType.Short:       return field.IndexAllowMultiple ? new ShortMultipleBTree(s, null)    : new ShortSingleBTree(s, null);
                case FieldType.Int:         return field.IndexAllowMultiple ? new IntMultipleBTree(s, null)      : new IntSingleBTree(s, null);
                case FieldType.Long:        return field.IndexAllowMultiple ? new LongMultipleBTree(s, null)     : new LongSingleBTree(s, null);
                case FieldType.UByte:       return field.IndexAllowMultiple ? new UByteMultipleBTree(s, null)    : new UByteSingleBTree(s, null);
                case FieldType.UShort:      return field.IndexAllowMultiple ? new UShortMultipleBTree(s, null)   : new UShortSingleBTree(s, null);
                case FieldType.UInt:        return field.IndexAllowMultiple ? new UIntMultipleBTree(s, null)     : new UIntSingleBTree(s, null);
                case FieldType.ULong:       return field.IndexAllowMultiple ? new ULongMultipleBTree(s, null)    : new ULongSingleBTree(s, null);
                case FieldType.Float:       return field.IndexAllowMultiple ? new FloatMultipleBTree(s, null)    : new FloatSingleBTree(s, null);
                case FieldType.Double:      return field.IndexAllowMultiple ? new DoubleMultipleBTree(s, null)   : new DoubleSingleBTree(s, null);
                case FieldType.Char:        return field.IndexAllowMultiple ? new CharMultipleBTree(s, null)     : new CharSingleBTree(s, null);
                case FieldType.String64:    return field.IndexAllowMultiple ? new String64MultipleBTree(s, null) : new String64SingleBTree(s, null);
                default:                    return null;
            }
        }

        public void Dispose()
        {
            if (ComponentSegment == null) return;

            String64IndexSegment?.Dispose();
            DefaultIndexSegment.Dispose();
            VersionTableSegment.Dispose();
            ComponentSegment.Dispose();

            ComponentSegment = null;
        }

        internal struct SerializationData
        {
            public LogicalSegment.SerializationData ComponentSegment;
            public LogicalSegment.SerializationData VersionTableSegment;
            public LogicalSegment.SerializationData DefaultIndexSegment;
            public LogicalSegment.SerializationData String64IndexSegment;
        }
        internal SerializationData SerializeSettings() =>
            new()
            {
                ComponentSegment     = ComponentSegment.SerializeSettings(),
                VersionTableSegment  = VersionTableSegment.SerializeSettings(),
                DefaultIndexSegment  = DefaultIndexSegment.SerializeSettings(),
                String64IndexSegment = String64IndexSegment.SerializeSettings()
            };
    }
}