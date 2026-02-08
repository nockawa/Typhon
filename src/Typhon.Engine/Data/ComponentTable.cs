// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.BPTree;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Header structure for a chunk of the Version table
/// </summary>
/// <remarks>
/// <p>
/// The <see cref="ComponentTable.CompRevTableSegment"/> is a <see cref="ChunkBasedSegment"/> with chunks of <see cref="ComponentRevisionManager.CompRevChunkSize"/> bytes.
/// Data is stored as a chain of chunks, the first one contains this header and is followed by <see cref="ComponentRevisionManager.CompRevCountInRoot"/> number
/// of <see cref="CompRevStorageElement"/> elements.
/// The following chunks in the chain have just an integer as header (giving the next chunk in the chain) and can
/// store <see cref="ComponentRevisionManager.CompRevCountInNext"/> number of <see cref="CompRevStorageElement"/> elements.
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

    internal void EnterControlLockForTest() => Control.EnterExclusiveAccess(ref WaitContext.Null);
    internal void ExitControlLockForTest() => Control.ExitExclusiveAccess();

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int chunkIndex, int indexInChunk) GetRevisionLocation(int revisionIndex)
    {
        if (revisionIndex < ComponentRevisionManager.CompRevCountInRoot)
        {
            return (0, revisionIndex);
        }
        var chunkIndex = Math.DivRem(revisionIndex-ComponentRevisionManager.CompRevCountInRoot, ComponentRevisionManager.CompRevCountInNext, out var indexInChunk) + 1;
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

/// <summary>
/// Stores all instances of a single component type with MVCC revision tracking.
/// </summary>
/// <remarks>
/// <para>
/// ComponentTable registers as a child of its owning <see cref="DatabaseEngine"/> in the resource tree.
/// Segments (ComponentSegment, CompRevTableSegment, etc.) are NOT registered as children —
/// they follow the "Owner Aggregates" pattern where ComponentTable will aggregate their metrics.
/// </para>
/// </remarks>
[PublicAPI]
public unsafe class ComponentTable : IResource, IMetricSource, IContentionTarget, IDebugPropertiesProvider
{
    private const int ComponentSegmentStartingSize = 4;
    private const int MainIndexSegmentStartingSize = 4;

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

    internal class ComponentCollectionInfo
    {
        public VariableSizedBufferSegmentBase VSBS;
        public ChunkAccessor Accessor;

        public ComponentCollectionInfo(VariableSizedBufferSegmentBase vsbs)
        {
            VSBS = vsbs;
            Accessor = VSBS.Segment.CreateChunkAccessor();
        }
    }

    internal Dictionary<int, ComponentCollectionInfo> ComponentCollectionVSBSByOffset { get; private set; }

    private ComponentTableFlags _flags;

    // Contention tracking (aggregated from all latches)
    private long _contentionWaitCount;
    private long _contentionTotalWaitUs;
    private long _contentionMaxWaitUs;

    #region IResource Implementation

    /// <inheritdoc />
    public string Id { get; private set; }

    /// <inheritdoc />
    public ResourceType Type => ResourceType.Node;

    /// <inheritdoc />
    public IResource Parent { get; private set; }

    /// <inheritdoc />
    public IEnumerable<IResource> Children => [];  // Segments are aggregated, not children

    /// <inheritdoc />
    public DateTime CreatedAt { get; private set; }

    /// <inheritdoc />
    public IResourceRegistry Owner { get; private set; }

    /// <inheritdoc />
    public bool RegisterChild(IResource child) => false;  // No children allowed

    /// <inheritdoc />
    public bool RemoveChild(IResource resource) => false;  // No children allowed

    #endregion

    #region IContentionTarget Implementation

    /// <inheritdoc />
    public TelemetryLevel TelemetryLevel => TelemetryLevel.Light;

    /// <inheritdoc />
    public IResource OwningResource => this;

    /// <inheritdoc />
    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalWaitUs, waitUs);

        // Plain check-and-write for high-water mark (occasional lost max is acceptable)
        if (waitUs > _contentionMaxWaitUs)
            _contentionMaxWaitUs = waitUs;
    }

    /// <inheritdoc />
    public void LogLockOperation(LockOperation operation, long durationUs)
    {
        // Light mode only - no operation logging
    }

    #endregion

    #region IMetricSource Implementation

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Aggregate capacity from all segments
        long totalAllocatedChunks =
            ComponentSegment.AllocatedChunkCount +
            CompRevTableSegment.AllocatedChunkCount +
            DefaultIndexSegment.AllocatedChunkCount +
            (String64IndexSegment?.AllocatedChunkCount ?? 0);

        long totalCapacityChunks =
            ComponentSegment.ChunkCapacity +
            CompRevTableSegment.ChunkCapacity +
            DefaultIndexSegment.ChunkCapacity +
            (String64IndexSegment?.ChunkCapacity ?? 0);

        writer.WriteCapacity(totalAllocatedChunks, totalCapacityChunks);

        // Report contention from latches
        writer.WriteContention(
            _contentionWaitCount,
            _contentionTotalWaitUs,
            _contentionMaxWaitUs,
            0);  // No timeout tracking yet
    }

    /// <inheritdoc />
    public void ResetPeaks()
    {
        _contentionMaxWaitUs = 0;
    }

    #endregion

    #region IDebugPropertiesProvider Implementation

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var props = new Dictionary<string, object>
        {
            // ComponentSegment breakdown
            ["ComponentSegment.AllocatedChunks"] = ComponentSegment.AllocatedChunkCount,
            ["ComponentSegment.Capacity"] = ComponentSegment.ChunkCapacity,
            ["ComponentSegment.ChunkSize"] = ComponentTotalSize,

            // CompRevTableSegment breakdown
            ["CompRevTableSegment.AllocatedChunks"] = CompRevTableSegment.AllocatedChunkCount,
            ["CompRevTableSegment.Capacity"] = CompRevTableSegment.ChunkCapacity,

            // DefaultIndexSegment breakdown
            ["DefaultIndexSegment.AllocatedChunks"] = DefaultIndexSegment.AllocatedChunkCount,
            ["DefaultIndexSegment.Capacity"] = DefaultIndexSegment.ChunkCapacity,

            // Contention details
            ["Contention.WaitCount"] = _contentionWaitCount,
            ["Contention.TotalWaitUs"] = _contentionTotalWaitUs,
            ["Contention.MaxWaitUs"] = _contentionMaxWaitUs,
        };

        // String64IndexSegment (may be null if no String64 indexes)
        if (String64IndexSegment != null)
        {
            props["String64IndexSegment.AllocatedChunks"] = String64IndexSegment.AllocatedChunkCount;
            props["String64IndexSegment.Capacity"] = String64IndexSegment.ChunkCapacity;
        }

        return props;
    }

    #endregion
    
    public void Create(DatabaseEngine dbe, DBComponentDefinition definition)
    {
        DBE = dbe;
        Definition = definition;

        // IResource initialization - register as child of DatabaseEngine
        Id = $"ComponentTable_{definition.Name}";
        Parent = dbe;
        Owner = dbe.Owner;
        CreatedAt = DateTime.UtcNow;
        Parent.RegisterChild(this);

        var mmf = DBE.MMF;
        ComponentSegment    = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentTotalSize);
        CompRevTableSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentRevisionManager.CompRevChunkSize);

        // This segment will be used for all kinds of index types except String64 which needs a dedicated one because its chunk size is different (all others are 64 bytes)
        DefaultIndexSegment  = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));
        String64IndexSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(IndexString64Chunk));

        if (definition.AllowMultiple)
        {
            PrimaryKeyIndex = new LongMultipleBTree(DefaultIndexSegment);
        }
        else
        {
            PrimaryKeyIndex = new LongSingleBTree(DefaultIndexSegment);
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
        ComponentCollectionVSBSByOffset = new Dictionary<int, ComponentCollectionInfo>();
        foreach (var field in Definition.FieldsByName.Values)
        {
            if (field.Type != FieldType.Collection)
            {
                continue;
            }

            var vsbs = DBE.GetComponentCollectionVSBS(field.DotNetUnderlyingType);
            ComponentCollectionVSBSByOffset.Add(field.OffsetInComponentStorage, new ComponentCollectionInfo(vsbs));
            _flags |= ComponentTableFlags.HasCollections;
        }
    }

    private IBTree CreateIndexForField(DBComponentDefinition.Field field)
    {
        var s = field.Type == FieldType.String64 ? String64IndexSegment : DefaultIndexSegment;
        IBTree index = field.Type switch
        {
            FieldType.Byte     => field.IndexAllowMultiple ? new ByteMultipleBTree(s)     : new ByteSingleBTree(s),
            FieldType.Short    => field.IndexAllowMultiple ? new ShortMultipleBTree(s)    : new ShortSingleBTree(s),
            FieldType.Int      => field.IndexAllowMultiple ? new IntMultipleBTree(s)      : new IntSingleBTree(s),
            FieldType.Long     => field.IndexAllowMultiple ? new LongMultipleBTree(s)     : new LongSingleBTree(s),
            FieldType.UByte    => field.IndexAllowMultiple ? new UByteMultipleBTree(s)    : new UByteSingleBTree(s),
            FieldType.UShort   => field.IndexAllowMultiple ? new UShortMultipleBTree(s)   : new UShortSingleBTree(s),
            FieldType.UInt     => field.IndexAllowMultiple ? new UIntMultipleBTree(s)     : new UIntSingleBTree(s),
            FieldType.ULong    => field.IndexAllowMultiple ? new ULongMultipleBTree(s)    : new ULongSingleBTree(s),
            FieldType.Float    => field.IndexAllowMultiple ? new FloatMultipleBTree(s)    : new FloatSingleBTree(s),
            FieldType.Double   => field.IndexAllowMultiple ? new DoubleMultipleBTree(s)   : new DoubleSingleBTree(s),
            FieldType.Char     => field.IndexAllowMultiple ? new CharMultipleBTree(s)     : new CharSingleBTree(s),
            FieldType.String64 => field.IndexAllowMultiple ? new String64MultipleBTree(s) : new String64SingleBTree(s),
            _                  => null
        };
        return index;
    }

    public void Dispose()
    {
        if (ComponentSegment == null) return;

        // Unregister from parent (DatabaseEngine)
        Parent?.RemoveChild(this);

        // Dispose ComponentCollectionInfo accessors to release page references
        if (ComponentCollectionVSBSByOffset != null)
        {
            foreach (var info in ComponentCollectionVSBSByOffset.Values)
            {
                info.Accessor.Dispose();
            }
            ComponentCollectionVSBSByOffset.Clear();
        }

        String64IndexSegment?.Dispose();
        DefaultIndexSegment.Dispose();
        CompRevTableSegment.Dispose();
        ComponentSegment.Dispose();

        ComponentSegment = null;
    }
}