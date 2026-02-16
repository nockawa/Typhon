using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]
[assembly: InternalsVisibleTo("tsh")]

namespace Typhon.Engine;

[Component(SchemaName, 1, true)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct FieldR1
{
    public const string SchemaName = "Typhon.Schema.Field";

    public String64 Name;

    public int FieldId;
    public FieldType Type;
    public uint IndexSPI;
    public bool IsStatic;
    public bool HasIndex;
    public bool IndexAllowMultiple;
    public int ArrayLength;
    public bool IsArray => ArrayLength > 0;
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct ComponentR1
{
    public const string SchemaName = "Typhon.Schema.Component";

    public String64 Name;
    public String64 POCOType;
    public int CompSize;
    public int CompOverhead;

    public int ComponentSPI;
    public int VersionSPI;
    public int DefaultIndexSPI;
    public int String64IndexSPI;

    public ComponentCollection<FieldR1> Fields;
}

/// <summary>
/// Configuration options for <see cref="DatabaseEngine"/>.
/// </summary>
[PublicAPI]
public class DatabaseEngineOptions
{
    /// <summary>
    /// Resource budget and limit configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contains settings for page cache size, transaction limits, WAL configuration,
    /// checkpoint behavior, and overall memory budget.
    /// </para>
    /// <para>
    /// Call <see cref="ResourceOptions.Validate"/> to verify configuration before engine creation.
    /// </para>
    /// </remarks>
    public ResourceOptions Resources { get; set; } = new();

    /// <summary>
    /// Lock acquisition timeout configuration for all engine subsystems.
    /// </summary>
    public TimeoutOptions Timeouts { get; set; } = new();

    /// <summary>
    /// Deferred cleanup subsystem configuration for MVCC revision management.
    /// </summary>
    public DeferredCleanupOptions DeferredCleanup { get; set; } = new();
}

/// <summary>
/// The main database engine class providing transaction-based access to component data.
/// </summary>
/// <remarks>
/// <para>
/// DatabaseEngine registers itself under the <see cref="ResourceSubsystem.DataEngine"/> subsystem
/// in the resource tree. ComponentTables are registered as children of this engine.
/// </para>
/// </remarks>
[PublicAPI]
public class DatabaseEngine : ResourceNode, IMetricSource, IDebugPropertiesProvider
{
    private readonly DatabaseEngineOptions      _options;
    private readonly ILogger<DatabaseEngine>    _log;
    private readonly IMemoryAllocator           _memoryAllocator;

    // Transaction counters for observability
    private long _transactionsCreated;
    private long _transactionsCommitted;
    private long _transactionsRolledBack;
    private long _transactionConflicts;

    // Commit duration tracking
    private long _commitLastUs;
    private long _commitSumUs;
    private long _commitCount;
    private long _commitMaxUs;

    private ComponentTable _fieldsTable;
    private ComponentTable _componentsTable;
    private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;
    private long _curPrimaryKey;
    private ConcurrentDictionary<int, ChunkBasedSegment> _componentCollectionSegmentByStride;
    private ConcurrentDictionary<Type, VariableSizedBufferSegmentBase> _componentCollectionVSBSByType;

    public DatabaseDefinitions DBD { get; }
    public ManagedPagedMMF MMF { get; }
    public EpochManager EpochManager { get; private set; }
    public DeadlineWatchdog Watchdog { get; }

    internal TransactionChain TransactionChain { get; }
    internal DeferredCleanupManager DeferredCleanupManager { get; }
    internal UowRegistry UowRegistry { get; private set; }

    /// <summary>
    /// Creates a new Unit of Work — the durability boundary for user operations. All transactions must be created through a UoW.
    /// </summary>
    /// <param name="durabilityMode">Controls when WAL records become crash-safe. Default is <see cref="DurabilityMode.Deferred"/>.</param>
    /// <param name="timeout">Lifetime timeout for this UoW. Default uses <see cref="TimeoutOptions.DefaultUowTimeout"/>.</param>
    /// <returns>A new <see cref="UnitOfWork"/> in <see cref="UnitOfWorkState.Pending"/> state.</returns>
    /// <exception cref="ResourceExhaustedException">All UoW registry slots are in use and the deadline expired.</exception>
    [return: TransfersOwnership]
    public UnitOfWork CreateUnitOfWork(DurabilityMode durabilityMode = DurabilityMode.Deferred, TimeSpan timeout = default)
    {
        var effectiveTimeout = timeout == default ? TimeoutOptions.Current.DefaultUowTimeout : timeout;
        var wc = WaitContext.FromTimeout(effectiveTimeout);

        // Back-pressure: if registry is full, wait for a slot to be freed.
        // The admission check is a fast-path optimization — AllocateUowId's CAS provides the real atomicity (TOCTOU by design).
        var uowId = UowRegistry.AllocateUowId(ref wc);

        return new UnitOfWork(this, durabilityMode, uowId, effectiveTimeout);
    }

    /// <summary>Records that a transaction was created (for observability counters).</summary>
    internal void RecordTransactionCreated() => Interlocked.Increment(ref _transactionsCreated);

    public DatabaseEngine(IResourceRegistry resourceRegistry, EpochManager epochManager, DeadlineWatchdog watchdog,
        ManagedPagedMMF mmf, IMemoryAllocator memoryAllocator, DatabaseEngineOptions options, ILogger<DatabaseEngine> log, string name = null) :
        base(name ?? $"DatabaseEngine_{Guid.NewGuid():N}", ResourceType.Engine, resourceRegistry.DataEngine)
    {
        // Engine initialization
        MMF = mmf;
        EpochManager = epochManager;
        Watchdog = watchdog;
        _log = log;
        _options = options;
        _memoryAllocator = memoryAllocator;
        TimeoutOptions.Current = _options.Timeouts;
        _componentCollectionSegmentByStride = new ConcurrentDictionary<int, ChunkBasedSegment>();
        _componentCollectionVSBSByType = new ConcurrentDictionary<Type, VariableSizedBufferSegmentBase>();
        TransactionChain = new TransactionChain(_options.Resources.MaxActiveTransactions, this);
        DeferredCleanupManager = new DeferredCleanupManager(_options.DeferredCleanup, _log);

        DBD = new DatabaseDefinitions();
        ConstructComponentStore();
        InitializeUowRegistry();

        if (MMF.IsDatabaseFileCreating)
        {
            CreateSystemSchemaR1();
        }
    }

    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            TransactionChain.Dispose();
            UowRegistry?.Dispose();
            MMF.Dispose();
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
    
    private void ConstructComponentStore()
    {
        _componentTableByType = new ConcurrentDictionary<Type, ComponentTable>();
        _curPrimaryKey = 0;
    }

    private void InitializeUowRegistry()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        if (MMF.IsDatabaseFileCreating)
        {
            // Creating path: allocate a 1-page segment for the registry (150 entries)
            var cs = MMF.CreateChangeSet();
            var segment = MMF.AllocateSegment(PageBlockType.None, 1, cs);

            // Clear the data area so all entries start as Free (State = 0)
            var page = segment.GetPageExclusive(0, epoch, out var memPageIdx);
            cs.AddByMemPageIndex(memPageIdx);
            var offset = LogicalSegment.RootHeaderIndexSectionLength;
            page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
            MMF.UnlatchPageExclusive(memPageIdx);

            // Write SPI to root header
            MMF.RequestPageEpoch(0, epoch, out var rootMemPageIdx);
            var rootLatched = MMF.TryLatchPageExclusive(rootMemPageIdx);
            Debug.Assert(rootLatched, "TryLatchPageExclusive failed on root page during registry init");
            var rootPage = MMF.GetPage(rootMemPageIdx);
            cs.AddByMemPageIndex(rootMemPageIdx);
            ref var header = ref rootPage.As<RootFileHeader>();
            header.UowRegistrySPI = segment.RootPageIndex;
            MMF.UnlatchPageExclusive(rootMemPageIdx);

            cs.SaveChanges();

            UowRegistry = new UowRegistry(segment, MMF, EpochManager, _memoryAllocator, this);
            UowRegistry.Initialize();
        }
        else
        {
            // Loading path: read SPI from root header
            MMF.RequestPageEpoch(0, epoch, out var rootMemPageIdx);
            var rootPage = MMF.GetPage(rootMemPageIdx);
            ref var header = ref rootPage.As<RootFileHeader>();
            var spi = header.UowRegistrySPI;
            var segment = MMF.GetSegment(spi);
            UowRegistry = new UowRegistry(segment, MMF, EpochManager, _memoryAllocator, this);
            UowRegistry.LoadFromDisk();
        }
    }

    private static int RoundToStandardStride(int size) =>
        size switch
        {
            <= 16 => 16,
            <= 32 => 32,
            <= 64 => 64,
            _ => (int)BitOperations.RoundUpToPowerOf2((uint)size)
        };

    private const int ComponentCollectionItemCountPerChunk      = 8;
    private const int ComponentCollectionSegmentStartingSize    = 8;

    internal VariableSizedBufferSegment<T> GetComponentCollectionVSBS<T>() where T : unmanaged =>
        (VariableSizedBufferSegment<T>)_componentCollectionVSBSByType.GetOrAdd(typeof(T),
            _ => new VariableSizedBufferSegment<T>(GetComponentCollectionSegment<T>()));

    internal VariableSizedBufferSegmentBase GetComponentCollectionVSBS(Type itemType) =>
        _componentCollectionVSBSByType.GetOrAdd(itemType,
            type =>
            {
                // Create the type for ComponentCollection<T>
                var ctType = typeof(VariableSizedBufferSegment<>).MakeGenericType(type);
                var fieldSize = DatabaseSchemaExtensions.FromType(type).field.SizeInComp();
                var segment = GetComponentCollectionSegment(fieldSize);
                return (VariableSizedBufferSegmentBase)Activator.CreateInstance(ctType, segment);
            });

    unsafe internal ChunkBasedSegment GetComponentCollectionSegment<T>() where T : unmanaged =>
        _componentCollectionSegmentByStride.GetOrAdd(RoundToStandardStride(sizeof(T) * 8),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    internal ChunkBasedSegment GetComponentCollectionSegment(int itemSize) =>
        _componentCollectionSegmentByStride.GetOrAdd(RoundToStandardStride(itemSize * 8),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

    // Create the first revision of the system schema
    private void CreateSystemSchemaR1()
    {
        // Register the system components
        RegisterComponentFromAccessor<FieldR1>();
        RegisterComponentFromAccessor<ComponentR1>();

        // Get their table
        _fieldsTable = GetComponentTable<FieldR1>();
        _componentsTable = GetComponentTable<ComponentR1>();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        MMF.RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = MMF.TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during schema save");
        var page = MMF.GetPage(memPageIdx);

        // Save the entry points in the file header
        var cs = MMF.CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);
        ref var rootFileHeader = ref page.As<RootFileHeader>();

        rootFileHeader.SystemSchemaRevision = 1;
        rootFileHeader.FieldTableSPI = _fieldsTable.ComponentSegment.RootPageIndex;
        rootFileHeader.ComponentTableSPI = _componentsTable.ComponentSegment.RootPageIndex;

        MMF.UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();
        
        // Now save the system components schema in the database (to load them next time we open the database)
        SaveInSystemSchema(_fieldsTable);
        SaveInSystemSchema(_componentsTable);
    }

    private void SaveInSystemSchema(ComponentTable table)
    {
        var definition = table.Definition;
        using var t = this.CreateQuickTransaction();

        var comp = new ComponentR1
        {
            Name                = (String64)definition.Name, 
            POCOType            = (String64)definition.POCOType.FullName,
            CompSize             = definition.ComponentStorageSize,
            CompOverhead         = definition.ComponentStorageOverhead,
            ComponentSPI        = table.ComponentSegment.RootPageIndex,
            VersionSPI          = table.CompRevTableSegment.RootPageIndex,
            DefaultIndexSPI     = table.DefaultIndexSegment.RootPageIndex,
            String64IndexSPI    = table.String64IndexSegment.RootPageIndex,
        };

        {
            using var a = t.CreateComponentCollectionAccessor(ref comp.Fields);

            foreach (var kvp in table.Definition.FieldsByName)
            {
                var field = kvp.Value;
                var f = new FieldR1
                {
                    Name = (String64)field.Name,
                    FieldId = field.FieldId,
                    Type = field.Type,
                    ArrayLength = field.ArrayLength
                };
            
                a.Add(f);
            }
        }
        
        t.Commit();
    }

    public bool RegisterComponentFromAccessor<T>() where T : unmanaged
    {
        var definition = DBD.CreateFromAccessor<T>();
        if (definition == null)
        {
            return false;
        }

        var componentTable = new ComponentTable(this, definition, this);
        _componentTableByType.TryAdd(typeof(T), componentTable);

        return true;
    }

    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    public ComponentTable GetComponentTable(Type type) => _componentTableByType.GetValueOrDefault(type);

    #region Instrumentation Methods

    internal void RecordCommitDuration(long durationUs)
    {
        _commitLastUs = durationUs;

        if (durationUs > _commitMaxUs)
        {
            _commitMaxUs = durationUs;
        }

        Interlocked.Add(ref _commitSumUs, durationUs);
        Interlocked.Increment(ref _commitCount);
        Interlocked.Increment(ref _transactionsCommitted);
    }

    internal void RecordRollback() => Interlocked.Increment(ref _transactionsRolledBack);

    internal void RecordConflict() => Interlocked.Increment(ref _transactionConflicts);

    #endregion

    #region IMetricSource Implementation

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Capacity: active transactions
        long activeCount = TransactionChain.ActiveCount;
        long maxCount = _options?.Resources?.MaxActiveTransactions ?? 1000;
        writer.WriteCapacity(activeCount, maxCount);

        // Throughput: transaction lifecycle
        writer.WriteThroughput("Created", _transactionsCreated);
        writer.WriteThroughput("Committed", _transactionsCommitted);
        writer.WriteThroughput("RolledBack", _transactionsRolledBack);
        writer.WriteThroughput("Conflicts", _transactionConflicts);

        // Duration: commit timing
        var avgUs = _commitCount > 0 ? _commitSumUs / _commitCount : 0;
        writer.WriteDuration("Commit", _commitLastUs, avgUs, _commitMaxUs);

        // Deferred cleanup throughput
        writer.WriteThroughput("Cleanup.Enqueued", DeferredCleanupManager.EnqueuedTotal);
        writer.WriteThroughput("Cleanup.Processed", DeferredCleanupManager.ProcessedTotal);
        writer.WriteThroughput("Cleanup.LazyTriggered", DeferredCleanupManager.LazyCleanupTotal);
    }

    /// <inheritdoc />
    public void ResetPeaks()
    {
        _commitMaxUs = 0;
        _commitSumUs = 0;
        _commitCount = 0;
    }

    #endregion

    #region IDebugPropertiesProvider Implementation

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["TransactionChain.ActiveCount"] = TransactionChain.ActiveCount,
            ["TransactionChain.MinTSN"] = TransactionChain.MinTSN,
            ["TransactionChain.CurrentTSN"] = TransactionChain.NextFreeId,
            ["ComponentTables.Count"] = _componentTableByType?.Count ?? 0,
            ["Schema.ComponentCount"] = DBD.ComponentCount,
            ["Schema.Components"] = string.Join(", ", DBD.ComponentNames),
            ["PrimaryKey.Current"] = _curPrimaryKey,
            ["Transactions.Created"] = _transactionsCreated,
            ["Transactions.Committed"] = _transactionsCommitted,
            ["Transactions.RolledBack"] = _transactionsRolledBack,
            ["Transactions.Conflicts"] = _transactionConflicts,
            ["Commit.LastUs"] = _commitLastUs,
            ["Commit.MaxUs"] = _commitMaxUs,
            ["Commit.Count"] = _commitCount,
            ["DeferredCleanup.QueueSize"] = DeferredCleanupManager.QueueSize,
            ["DeferredCleanup.EnqueuedTotal"] = DeferredCleanupManager.EnqueuedTotal,
            ["DeferredCleanup.ProcessedTotal"] = DeferredCleanupManager.ProcessedTotal,
        };

    #endregion
}