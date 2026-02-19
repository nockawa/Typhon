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
/// of <see cref="CompRevStorageElement"/> elements (currently 3 with 12-byte elements).
/// The following chunks in the chain have just an integer as header (giving the next chunk in the chain) and can
/// store <see cref="ComponentRevisionManager.CompRevCountInNext"/> number of <see cref="CompRevStorageElement"/> elements (currently 5).
/// </p>
/// <p>
/// The chain is a circular buffer, location of the first item is given through <see cref="FirstItemIndex"/>
/// </p>
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

    /// Monotonically increasing counter incremented on every commit to this entity.
    /// Used for conflict detection — immune to revision index ordering and cleanup compaction.
    public int CommitSequence;

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
/// Stores the information of a component revision element.
/// </summary>
/// <remarks>
/// 12 bytes (Pack=2, divisible by 4 per ADR-027). Layout:
/// <code>
/// Offset  Size  Field
///   0      4    ComponentChunkId
///   4      4    _packedTickHigh     (upper 32 bits of TSN)
///   8      2    _packedTickLow      (full 16 bits of TSN)
///  10      2    _packedUowId        (bits 0-14: UowId, bit 15: IsolationFlag)
/// </code>
/// Root chunk: 3 elements ((64 − 20) / 12). Overflow chunks: 5 elements (64 / 12).
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct CompRevStorageElement
{
    private const ushort IsolationBit = 1 << 15;        // bit 15 of _packedUowId
    private const ushort UowIdMask = 0x7FFF;            // bits 0-14 of _packedUowId

    public int ComponentChunkId;
    private uint _packedTickHigh;
    private ushort _packedTickLow;
    private ushort _packedUowId;

    public void Void()
    {
        ComponentChunkId = 0;
        _packedTickHigh = 0;
        _packedTickLow = 0;
        _packedUowId = 0;
    }

    public bool IsVoid => ComponentChunkId == 0 && _packedTickHigh == 0 && _packedTickLow == 0 && _packedUowId == 0;

    public bool IsolationFlag
    {
        get => (_packedUowId & IsolationBit) != 0;
        set => _packedUowId = (ushort)(value ? (_packedUowId | IsolationBit) : (_packedUowId & ~IsolationBit));
    }

    /// <summary>UoW ID that created this revision (15 bits, max 32,767). 0 until UoW Registry (#51) lands.</summary>
    public ushort UowId
    {
        get => (ushort)(_packedUowId & UowIdMask);
        set => _packedUowId = (ushort)((_packedUowId & IsolationBit) | (value & UowIdMask));
    }

    public long TSN
    {
        get => (long)((ulong)_packedTickHigh << 16 | _packedTickLow);
        set
        {
            _packedTickHigh = (uint)(value >> 16);
            _packedTickLow = (ushort)(value & 0xFFFF);
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
public unsafe class ComponentTable : ResourceNode, IMetricSource, IContentionTarget, IDebugPropertiesProvider
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

    /// <summary>
    /// Stable WAL type identifier derived from <see cref="LogicalSegment.RootPageIndex"/>. Set during registration.
    /// Used to identify component types in WAL records for crash recovery replay.
    /// </summary>
    internal ushort WalTypeId { get; set; }
    internal IndexedFieldInfo[] IndexedFieldInfos { get; private set; }

    internal Dictionary<int, VariableSizedBufferSegmentBase> ComponentCollectionVSBSByOffset { get; private set; }

    private ComponentTableFlags _flags;

    // Contention tracking (aggregated from all latches)
    private long _contentionWaitCount;
    private long _contentionTotalWaitUs;
    private long _contentionMaxWaitUs;
    
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
    public void ResetPeaks() => _contentionMaxWaitUs = 0;

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
    
    public ComponentTable(DatabaseEngine dbe, DBComponentDefinition definition, IResource parent, ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None) : 
        base($"ComponentTable_{definition.Name}", ResourceType.ComponentTable, parent, exhaustionPolicy)
    {
        DBE = dbe;
        Definition = definition;
        
        var mmf = DBE.MMF;
        ComponentSegment    = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentTotalSize);
        CompRevTableSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentRevisionManager.CompRevChunkSize);

        // This segment will be used for all kinds of index types except String64 which needs a dedicated one because its chunk size is different (all others are 64 bytes)
        DefaultIndexSegment  = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk));
        String64IndexSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(IndexString64Chunk));

        // PK index uses stableId -1 on DefaultIndexSegment; secondary indexes use Field.FieldId
        if (definition.AllowMultiple)
        {
            PrimaryKeyIndex = new LongMultipleBTree(DefaultIndexSegment, stableId: -1);
        }
        else
        {
            PrimaryKeyIndex = new LongSingleBTree(DefaultIndexSegment, stableId: -1);
        }

        BuildIndexedFieldInfo();
        BuildComponentCollectionInfo();
    }

    private void BuildIndexedFieldInfo()
    {
        var l = new List<IndexedFieldInfo>();

        var ro = ComponentOverhead;

        // Each secondary index uses Field.FieldId as its stable directory key.
        // This is order-independent and survives schema evolution (FieldIds are immutable once assigned).
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
                Index         = CreateIndexForField(f, (short)f.FieldId),
            };
            fi.OffsetToIndexElementId = fi.Index.AllowMultiple ? (j++ * sizeof(int)) : 0;
            l.Add(fi);
        }

        IndexedFieldInfos = l.ToArray();
    }

    private void BuildComponentCollectionInfo()
    {
        ComponentCollectionVSBSByOffset = new Dictionary<int, VariableSizedBufferSegmentBase>();
        foreach (var field in Definition.FieldsByName.Values)
        {
            if (field.Type != FieldType.Collection)
            {
                continue;
            }

            var vsbs = DBE.GetComponentCollectionVSBS(field.DotNetUnderlyingType);
            ComponentCollectionVSBSByOffset.Add(field.OffsetInComponentStorage, vsbs);
            _flags |= ComponentTableFlags.HasCollections;
        }
    }

    private IBTree CreateIndexForField(DBComponentDefinition.Field field, short stableId)
    {
        var s = field.Type == FieldType.String64 ? String64IndexSegment : DefaultIndexSegment;
        IBTree index = field.Type switch
        {
            FieldType.Byte     => field.IndexAllowMultiple ? new ByteMultipleBTree(s, stableId: stableId)     : new ByteSingleBTree(s, stableId: stableId),
            FieldType.Short    => field.IndexAllowMultiple ? new ShortMultipleBTree(s, stableId: stableId)    : new ShortSingleBTree(s, stableId: stableId),
            FieldType.Int      => field.IndexAllowMultiple ? new IntMultipleBTree(s, stableId: stableId)      : new IntSingleBTree(s, stableId: stableId),
            FieldType.Long     => field.IndexAllowMultiple ? new LongMultipleBTree(s, stableId: stableId)     : new LongSingleBTree(s, stableId: stableId),
            FieldType.UByte    => field.IndexAllowMultiple ? new UByteMultipleBTree(s, stableId: stableId)    : new UByteSingleBTree(s, stableId: stableId),
            FieldType.UShort   => field.IndexAllowMultiple ? new UShortMultipleBTree(s, stableId: stableId)   : new UShortSingleBTree(s, stableId: stableId),
            FieldType.UInt     => field.IndexAllowMultiple ? new UIntMultipleBTree(s, stableId: stableId)     : new UIntSingleBTree(s, stableId: stableId),
            FieldType.ULong    => field.IndexAllowMultiple ? new ULongMultipleBTree(s, stableId: stableId)    : new ULongSingleBTree(s, stableId: stableId),
            FieldType.Float    => field.IndexAllowMultiple ? new FloatMultipleBTree(s, stableId: stableId)    : new FloatSingleBTree(s, stableId: stableId),
            FieldType.Double   => field.IndexAllowMultiple ? new DoubleMultipleBTree(s, stableId: stableId)   : new DoubleSingleBTree(s, stableId: stableId),
            FieldType.Char     => field.IndexAllowMultiple ? new CharMultipleBTree(s, stableId: stableId)     : new CharSingleBTree(s, stableId: stableId),
            FieldType.String64 => field.IndexAllowMultiple ? new String64MultipleBTree(s, stableId: stableId) : new String64SingleBTree(s, stableId: stableId),
            _                  => null
        };
        return index;
    }

    protected override void Dispose(bool disposing)
    {
        if (ComponentSegment == null)
        {
            return;
        }

        if (disposing)
        {
            String64IndexSegment?.Dispose();
            DefaultIndexSegment.Dispose();
            CompRevTableSegment.Dispose();
            ComponentSegment.Dispose();

            ComponentSegment = null;
        }
        base.Dispose(disposing);
    }
}