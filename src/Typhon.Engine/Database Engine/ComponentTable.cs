// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;

namespace Typhon.Engine;

internal struct RowVersionStorageHeader
{
    public int NextChunkId;
    public AccessControlSmall Control;
    public int Revision;
    public short FirstItemIndex;
    public short ItemCount;
    public int ChainLength;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RowVersionStorageElement
{
    public long Tick;
    public int RowChunkId;
}

[DebuggerDisplay("Offset: {OffsetToField} Size: {Size}")]
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

        var mmf = DBE.MMF;
        ComponentSegment    = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, RowTotalSize);
        VersionTableSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, RowVersionDataChunkSize);
            
        // This segment will be used for all kind of index types except String64 which needs a dedicated one because its chunk size is different (all others are 64 bytes)
        DefaultIndexSegment  = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));
        String64IndexSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(IndexString64Chunk));

        PrimaryKeyIndex = new LongSingleBTree(DefaultIndexSegment, ChunkRandomAccessor.GetFromPool(DefaultIndexSegment, 8));

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
        var a = ChunkRandomAccessor.GetFromPool(s, 8);
        switch (field.Type)
        {
            case FieldType.Byte:        return field.IndexAllowMultiple ? new ByteMultipleBTree(s, a)     : new ByteSingleBTree(s, a);
            case FieldType.Short:       return field.IndexAllowMultiple ? new ShortMultipleBTree(s, a)    : new ShortSingleBTree(s, a);
            case FieldType.Int:         return field.IndexAllowMultiple ? new IntMultipleBTree(s, a)      : new IntSingleBTree(s, a);
            case FieldType.Long:        return field.IndexAllowMultiple ? new LongMultipleBTree(s, a)     : new LongSingleBTree(s, a);
            case FieldType.UByte:       return field.IndexAllowMultiple ? new UByteMultipleBTree(s, a)    : new UByteSingleBTree(s, a);
            case FieldType.UShort:      return field.IndexAllowMultiple ? new UShortMultipleBTree(s, a)   : new UShortSingleBTree(s, a);
            case FieldType.UInt:        return field.IndexAllowMultiple ? new UIntMultipleBTree(s, a)     : new UIntSingleBTree(s, a);
            case FieldType.ULong:       return field.IndexAllowMultiple ? new ULongMultipleBTree(s, a)    : new ULongSingleBTree(s, a);
            case FieldType.Float:       return field.IndexAllowMultiple ? new FloatMultipleBTree(s, a)    : new FloatSingleBTree(s, a);
            case FieldType.Double:      return field.IndexAllowMultiple ? new DoubleMultipleBTree(s, a)   : new DoubleSingleBTree(s, a);
            case FieldType.Char:        return field.IndexAllowMultiple ? new CharMultipleBTree(s, a)     : new CharSingleBTree(s, a);
            case FieldType.String64:    return field.IndexAllowMultiple ? new String64MultipleBTree(s, a) : new String64SingleBTree(s, a);
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

    /*
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
*/
}