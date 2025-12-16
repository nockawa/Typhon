// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine.BPTree;

namespace Typhon.Engine;

/// <summary>
/// Header structure for a chunk of the Version table
/// </summary>
/// <remarks>
/// <p>
/// The <see cref="ComponentTable.CompRevTableSegment"/> is a <see cref="ChunkBasedSegment"/> with chunks of <see cref="ComponentTable.CompRevChunkSize"/> bytes.
/// Data is stored as a chain of chunks, the first one contains this header and is followed by <see cref="ComponentTable.CompRevCountInRoot"/> number
/// of <see cref="CompRevStorageElement"/> elements.
/// The following chunks in the chain have just an integer as header (giving the next chunk in the chain) and can
/// store <see cref="ComponentTable.CompRevCountInNext"/> number of <see cref="CompRevStorageElement"/> elements.
/// </p>
/// <p>
/// The chain is a circular buffer, location of the first item is given through <see cref="FirstItemIndex"/> 
/// </p>
/// 
/// </remarks>
internal struct CompRevStorageHeader
{
    /// ID of the next chunk in the chain. MUST BE THE FIRST FIELD OF THIS STRUCTURE !
    public int NextChunkId;
    
    /// Access control to be thread-safe
    public AccessControlSmall Control;
    
    /// Revision of the first item, the revision of the following ones is computed from this revision + the position of the item in the chain
    public int FirstItemRevision;
    
    /// The whole chain is a circular buffer because we remove the oldest revisions and add the new ones in chronological order. This is the index
    /// of the first item in the chain (e.g. 18 would be 3rd chunk, 2nd entry for 8 entries per chunk)
    public short FirstItemIndex;
    
    /// Number of items in the chain
    public short ItemCount;
    
    /// Total length of the chain
    public short ChainLength;

    /// Index in the chain of the last committed revision, allows us to detect concurrency conflicts
    public short LastCommitRevisionIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int chunkIndex, int indexInChunk) GetRevisionLocation(int revisionIndex)
    {
        if (revisionIndex < ComponentTable.CompRevCountInRoot)
        {
            return (0, revisionIndex);
        }
        var chunkIndex = Math.DivRem(revisionIndex-ComponentTable.CompRevCountInRoot, ComponentTable.CompRevCountInNext, out var indexInChunk) + 1;
        return (chunkIndex, indexInChunk);
    }
}

/// <summary>
/// Stores the information of a component revision element
/// </summary>
/// <remarks>
/// This structure is 10 bytes long, so we can store 6 of them in a 64 bytes chunk with a 4 bytes header.
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct CompRevStorageElement
{
    private const ushort CompRevTransactionIsolatedFlag = 1;
    private const ushort CompRevTransactionIsolatedMask = 0xFFFE;

    public int ComponentChunkId;
    private uint _packedTickHigh;
    private ushort _packedTickLow;

    public void Void()
    {
        ComponentChunkId = 0;
        _packedTickHigh = 0;
        _packedTickLow = 0;
    }
    
    public bool IsVoid => ComponentChunkId == 0 &&  _packedTickHigh == 0 && _packedTickLow == 0;
    
    public bool IsolationFlag
    {
        get
        {
            return (_packedTickLow & CompRevTransactionIsolatedFlag) != 0;
        }
        set
        {
            _packedTickLow = (ushort)((_packedTickLow & CompRevTransactionIsolatedMask) | (value ? CompRevTransactionIsolatedFlag : 0));
        }
    }

    public long TSN
    {
        get
        {
            return (long)((ulong)_packedTickHigh << 16 | (uint)(_packedTickLow & CompRevTransactionIsolatedMask));
        }
        set
        {
            _packedTickHigh = (uint)(value >> 16);
            _packedTickLow = (ushort)((value & CompRevTransactionIsolatedMask) | (uint)(_packedTickLow & CompRevTransactionIsolatedFlag));
        }
    }
}

[DebuggerDisplay("Offset: {OffsetToField} Size: {Size}")]
internal struct IndexedFieldInfo
{
    public int OffsetToField;
    public int Size;

    public int OffsetToIndexElementId;
    public IBTree Index;
}

[PublicAPI]
[Flags]
public enum ComponentTableFlags
{
    None                = 0x00,
    HasCollections      = 0x01
}

[PublicAPI]
public unsafe class ComponentTable : IDisposable
{
    private const int ComponentSegmentStartingSize = 4;
    private const int MainIndexSegmentStartingSize = 4;

    internal const int CompRevChunkSize = 64;
    internal static readonly int CompRevCountInRoot = (CompRevChunkSize - sizeof(CompRevStorageHeader)) / sizeof(CompRevStorageElement);
    internal static readonly int CompRevCountInNext = (CompRevChunkSize / sizeof(CompRevStorageElement));

    public ChunkBasedSegment ComponentSegment { get; private set; }
    public ChunkBasedSegment CompRevTableSegment { get; private set; }
    public ChunkBasedSegment DefaultIndexSegment { get; private set; }
    public ChunkBasedSegment String64IndexSegment { get; private set; }
    public BTree<long> PrimaryKeyIndex { get; private set; }
    public int ComponentStorageSize => Definition.ComponentStorageSize;
    public DBComponentDefinition Definition { get; private set; }

    public ComponentTableFlags Flags => _flags;
    public bool HasCollections => (_flags & ComponentTableFlags.HasCollections) != 0;
    
    internal DatabaseEngine DBE { get; private set; }
    internal int ComponentOverhead => Definition.MultipleIndicesCount * sizeof(int);
    internal int ComponentTotalSize => Definition.ComponentStorageTotalSize;
    internal IndexedFieldInfo[] IndexedFieldInfos { get; private set; }
    internal Dictionary<int, (VariableSizedBufferSegmentBase, ChunkRandomAccessor)> ComponentCollectionVSBSByOffset { get; private set; }

    private ComponentTableFlags _flags;
    
    public void Create(DatabaseEngine dbe, DBComponentDefinition definition)
    {
        DBE = dbe;
        Definition = definition;

        var mmf = DBE.MMF;
        ComponentSegment    = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentTotalSize);
        CompRevTableSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, CompRevChunkSize);
            
        // This segment will be used for all kinds of index types except String64 which needs a dedicated one because its chunk size is different (all others are 64 bytes)
        DefaultIndexSegment  = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));
        String64IndexSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(IndexString64Chunk));

        if (definition.AllowMultiple)
        {
            PrimaryKeyIndex = new LongMultipleBTree(DefaultIndexSegment, ChunkRandomAccessor.GetFromPool(DefaultIndexSegment, 8));
        }
        else
        {
            PrimaryKeyIndex = new LongSingleBTree(DefaultIndexSegment, ChunkRandomAccessor.GetFromPool(DefaultIndexSegment, 8));
        }

        BuildIndexedFieldInfo();
        BuildComponentCollectionInfo();
    }

    private void BuildIndexedFieldInfo()
    {
        var l = new List<IndexedFieldInfo>();

        var ro = ComponentOverhead;

        for (int i = 0, j = 0; i < Definition.MaxFieldId; i++)
        {
            var f = Definition[i];
            if (f == null || !f.HasIndex)
            {
                continue;
            }

            var fi = new IndexedFieldInfo
            {
                OffsetToField = ro + f.OffsetInComponentStorage, 
                Size          = f.SizeInComponentStorage, 
                Index         = CreateIndexForField(f),
            };
            fi.OffsetToIndexElementId = fi.Index.AllowMultiple ? (j++ * sizeof(int)) : 0;
            l.Add(fi);
        }

        IndexedFieldInfos = l.ToArray();
    }

    private void BuildComponentCollectionInfo()
    {
        ComponentCollectionVSBSByOffset = new Dictionary<int, (VariableSizedBufferSegmentBase, ChunkRandomAccessor)>();
        foreach (var field in Definition.FieldsByName.Values)
        {
            if (field.Type != FieldType.Collection)
            {
                continue;
            }

            var vsbs = DBE.GetComponentCollectionVSBS(field.DotNetUnderlyingType);
            ComponentCollectionVSBSByOffset.Add(field.OffsetInComponentStorage, (vsbs, vsbs.Segment.CreateChunkRandomAccessor(8)));
            _flags |= ComponentTableFlags.HasCollections;
        }
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
        CompRevTableSegment.Dispose();
        ComponentSegment.Dispose();

        ComponentSegment = null;
    }
}