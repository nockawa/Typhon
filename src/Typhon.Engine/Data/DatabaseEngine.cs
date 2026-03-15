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
using System.Reflection;
using System.Linq.Expressions;
using Typhon.Schema.Definition;

[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]
[assembly: InternalsVisibleTo("tsh")]

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct FieldR1
{
    public const string SchemaName = "Typhon.Schema.Field";

    public String64 Name;

    public int FieldId;
    public FieldType Type;
    public FieldType UnderlyingType;
    public uint IndexSPI;
    public bool IsStatic;
    public bool HasIndex;
    public bool IndexAllowMultiple;
    public int ArrayLength;
    public int OffsetInComponentStorage;
    public int SizeInComponentStorage;
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
    public int TailIndexSPI;

    public ComponentCollection<FieldR1> Fields;

    public int SchemaRevision;
    public int FieldCount;
}

/// <summary>
/// Persisted archetype schema. One entity per registered archetype.
/// Enables load-time validation: mismatch between persisted and runtime archetype definitions → hard error.
/// </summary>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct ArchetypeR1
{
    public const string SchemaName = "Typhon.Schema.Archetype";

    /// <summary>Archetype CLR type name (e.g., "Building").</summary>
    public String64 Name;

    /// <summary>Globally unique archetype ID from [Archetype(Id = N)].</summary>
    public ushort ArchetypeId;

    /// <summary>Parent archetype ID (0xFFFF = no parent).</summary>
    public ushort ParentArchetypeId;

    /// <summary>Total component count (own + inherited).</summary>
    public byte ComponentCount;

    public byte _pad0, _pad1, _pad2;

    /// <summary>Schema revision from [Archetype(Id, Revision)].</summary>
    public int Revision;

    /// <summary>Component schema names in slot order, stored in VSBS.</summary>
    public ComponentCollection<String64> ComponentNames;

    public const ushort NoParent = 0xFFFF;
}

/// <summary>
/// Describes the kind of schema change recorded in the audit trail.
/// </summary>
[PublicAPI]
public enum SchemaChangeKind
{
    Compatible,
    Migration,
    SystemUpgrade,
}

/// <summary>
/// Audit trail entry for schema changes. One entity is created for each component schema change (add/remove/widen fields, migration function execution, etc.).
/// </summary>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct SchemaHistoryR1
{
    public const string SchemaName = "Typhon.Schema.History";

    public long Timestamp;
    public String64 ComponentName;
    public int FromRevision;
    public int ToRevision;
    public int FieldsAdded;
    public int FieldsRemoved;
    public int FieldsTypeChanged;
    public int EntitiesMigrated;
    public int ElapsedMilliseconds;
    public SchemaChangeKind Kind;
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

    /// <summary>
    /// WAL writer configuration. Null disables WAL durability (in-memory only).
    /// </summary>
    public WalWriterOptions Wal { get; set; }

    /// <summary>
    /// Background statistics rebuild configuration (HyperLogLog, MCV, Histogram).
    /// Null disables the background statistics worker (statistics can still be rebuilt manually).
    /// </summary>
    public StatisticsOptions Statistics { get; set; }
}

/// <summary>
/// The main database engine class providing transaction-based access to component data.
/// </summary>
/// <remarks>
/// <para>
/// DatabaseEngine registers itself under the <see cref="ResourceSubsystem.DataEngine"/> subsystem in the resource tree. ComponentTables are registered
/// as children of this engine.
/// </para>
/// </remarks>
[PublicAPI]
public partial class DatabaseEngine : ResourceNode, IMetricSource, IDebugPropertiesProvider
{
    private readonly DatabaseEngineOptions      _options;
    private readonly ILogger<DatabaseEngine>    _log;
    private readonly IMemoryAllocator           _memoryAllocator;
    internal IMemoryAllocator                   MemoryAllocator => _memoryAllocator;
    private readonly IWalFileIO                 _walFileIO;
    private readonly IResource                  _durabilityNode;
    private WalRecoveryResult                   _lastRecoveryResult;
    internal WalRecoveryResult                  LastRecoveryResult => _lastRecoveryResult;
    private StagingBufferPool                   _stagingBufferPool;

    // Bootstrap dictionary keys (engine layer)
    // ReSharper disable InconsistentNaming
    internal const string BK_SystemSchemaRevision   = "SystemSchemaRevision";
    internal const string BK_SysComponentR1         = "sys.ComponentR1";
    internal const string BK_SysSchemaHistory       = "sys.SchemaHistory";
    internal const string BK_NextFreeTSN            = "NextFreeTSN";
    internal const string BK_UowRegistrySPI         = "UowRegistrySPI";
    internal const string BK_CollectionFieldR1      = "collection.FieldR1";
    internal const string BK_UserSchemaVersion      = "UserSchemaVersion";
    // ReSharper restore InconsistentNaming

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

    private ComponentTable _componentsTable;
    private ComponentTable _schemaHistoryTable;
    private ComponentTable _archetypesTable;
    private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;
    private ConcurrentDictionary<ushort, ComponentTable> _componentTableByWalTypeId;
    private long _curPrimaryKey;
    private Dictionary<string, (long PK, ComponentR1 Comp)> _persistedComponents;
    private Dictionary<ushort, (long PK, ArchetypeR1 Arch)> _persistedArchetypes;
    private Dictionary<string, FieldR1[]> _persistedFieldsByComponent;
    private ConcurrentDictionary<int, ChunkBasedSegment<PersistentStore>> _componentCollectionSegmentByStride;
    private ConcurrentDictionary<Type, VariableSizedBufferSegmentBase<PersistentStore>> _componentCollectionVSBSByType;
    private MigrationRegistry _migrationRegistry;

    /// <summary>Raised during schema migration to report progress to subscribers.</summary>
    [PublicAPI]
    public event EventHandler<MigrationProgressEventArgs> OnMigrationProgress;

    internal void RaiseMigrationProgress(MigrationProgressEventArgs args) => OnMigrationProgress?.Invoke(this, args);

    /// <summary>Exposes persisted component metadata for operational tooling (Inspect, tsh commands).</summary>
    internal IReadOnlyDictionary<string, (long PK, ComponentR1 Comp)> PersistedComponents => _persistedComponents;

    /// <summary>Exposes persisted field definitions per component for operational tooling.</summary>
    internal IReadOnlyDictionary<string, FieldR1[]> PersistedFieldsByComponent => _persistedFieldsByComponent;

    /// <summary>Exposes the migration registry for dry-run validation.</summary>
    internal MigrationRegistry MigrationRegistry => _migrationRegistry;

    public DatabaseDefinitions DBD { get; }
    public ManagedPagedMMF MMF { get; }
    public EpochManager EpochManager { get; private set; }
    public DeadlineWatchdog Watchdog { get; }

    internal TransactionChain TransactionChain { get; }
    internal DeferredCleanupManager DeferredCleanupManager { get; }

    /// <summary>Engine-level MVCC exception dictionary for ECS EnabledBits.</summary>
    internal EnabledBitsOverrides EnabledBitsOverrides { get; } = new();

    /// <summary>
    /// Process all pending deferred cleanups. Intended for test/diagnostic use.
    /// Creates its own ChangeSet and processes ALL queued entries regardless of blockingTSN.
    /// </summary>
    /// <param name="nextMinTSN">Cutoff TSN for revision cleanup. 0 = use TransactionChain.NextFreeId + 1 (clean everything eligible).</param>
    /// <returns>Number of entities cleaned up.</returns>
    internal int FlushDeferredCleanups(long nextMinTSN = 0)
    {
        if (nextMinTSN == 0)
        {
            nextMinTSN = TransactionChain.NextFreeId + 1;
        }

        var changeSet = new ChangeSet(MMF);
        var result = DeferredCleanupManager.ProcessDeferredCleanups(long.MaxValue, nextMinTSN, this, changeSet);
        changeSet.SaveChanges();
        return result;
    }

    internal UowRegistry UowRegistry { get; private set; }

    /// <summary>
    /// Optional WAL manager for durability. Null when WAL is not configured.
    /// </summary>
    internal WalManager WalManager { get; private set; }

    /// <summary>
    /// Optional checkpoint manager. Null when WAL is not configured. Periodically flushes dirty data pages
    /// and advances CheckpointLSN to enable WAL segment recycling.
    /// </summary>
    internal CheckpointManager CheckpointManager { get; private set; }
    internal StatisticsWorker StatisticsWorker { get; private set; }

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
        LogUowLifecycle("CreateUnitOfWork enter");
        var effectiveTimeout = timeout == TimeSpan.Zero ? TimeoutOptions.Current.DefaultUowTimeout : timeout;
        var wc = WaitContext.FromTimeout(effectiveTimeout);

        // For Deferred/GroupCommit: create the ChangeSet early so AllocateUowId can track
        // the registry page mutation in it (avoiding a synchronous SaveChanges).
        var changeSet = durabilityMode != DurabilityMode.Immediate ? MMF.CreateChangeSet() : null;
        LogUowLifecycle("ChangeSet created");

        // Back-pressure: if registry is full, wait for a slot to be freed.
        // The admission check is a fast-path optimization — AllocateUowId's CAS provides the real atomicity (TOCTOU by design).
        var uowId = UowRegistry.AllocateUowId(ref wc, changeSet);
        LogUowIdAllocated(uowId);

        return new UnitOfWork(this, durabilityMode, uowId, effectiveTimeout, changeSet);
    }

    /// <summary>Records that a transaction was created (for observability counters).</summary>
    internal void RecordTransactionCreated() => Interlocked.Increment(ref _transactionsCreated);

    /// <summary>
    /// Triggers an immediate checkpoint cycle. Flushes all dirty data pages, advances CheckpointLSN, and recycles WAL segments.
    /// No-op if WAL/checkpoint is not configured.
    /// </summary>
    public void ForceCheckpoint() => CheckpointManager?.ForceCheckpoint();

    public DatabaseEngine(IResourceRegistry resourceRegistry, EpochManager epochManager, DeadlineWatchdog watchdog, ManagedPagedMMF mmf, 
        IMemoryAllocator memoryAllocator, DatabaseEngineOptions options, ILogger<DatabaseEngine> log, IWalFileIO walFileIO = null, string name = null) 
        : base(name ?? $"DatabaseEngine_{Guid.NewGuid():N}", ResourceType.Engine, resourceRegistry.DataEngine)
    {
        // Engine initialization
        MMF = mmf;
        EpochManager = epochManager;
        Watchdog = watchdog;
        _log = log;
        _options = options;
        _memoryAllocator = memoryAllocator;
        _walFileIO = walFileIO;
        _durabilityNode = resourceRegistry.Durability;
        TimeoutOptions.Current = _options.Timeouts;
        _componentCollectionSegmentByStride = new ConcurrentDictionary<int, ChunkBasedSegment<PersistentStore>>();
        _componentCollectionVSBSByType = new ConcurrentDictionary<Type, VariableSizedBufferSegmentBase<PersistentStore>>();
        TransactionChain = new TransactionChain(_options.Resources.MaxActiveTransactions, this);
        DeferredCleanupManager = new DeferredCleanupManager(_options.DeferredCleanup, _log);

        DBD = new DatabaseDefinitions();
        ConstructComponentStore();
        InitializeUowRegistry();

        if (MMF.IsDatabaseFileCreating)
        {
            CreateSystemSchemaR1();
        }
        else
        {
            LoadSystemSchemaR1();
        }

        InitializeWalManager();
        InitializeCheckpointManager();
        InitializeStatisticsWorker();
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
            // Statistics worker must stop before checkpoint (it holds epoch guards during scans)
            StatisticsWorker?.Dispose();
            StatisticsWorker = null;

            _log?.LogInformation("Engine disposing: CheckpointManager");
            // Checkpoint must dispose first: runs final cycle, writes pages + advances LSN before WAL shuts down
            CheckpointManager?.Dispose();
            CheckpointManager = null;

            // Dispose staging pool after checkpoint manager (checkpoint may use it during final cycle)
            _stagingBufferPool?.Dispose();
            _stagingBufferPool = null;

            _log?.LogInformation("Engine disposing: PersistEngineState");
            // Persist final TSN counter and flush all dirty pages to disk. This ensures:
            // 1. TSN counter survives restart (MVCC visibility)
            // 2. All committed transaction data is on disk even without WAL/checkpoint
            PersistEngineState();

            _log?.LogInformation("Engine disposing: WalManager");
            WalManager?.Dispose();
            WalManager = null;
            _log?.LogInformation("Engine disposing: TransactionChain + cleanup");
            TransactionChain.Dispose();
            UowRegistry?.Dispose();
            MMF.Dispose();
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
    
    private void InitializeWalManager()
    {
        var walOptions = _options.Wal;
        if (walOptions == null || _walFileIO == null)
        {
            return;
        }

        var commitBufferCapacity = _options.Resources.WalRingBufferSizeBytes / 2;
        WalManager = new WalManager(walOptions, _memoryAllocator, _walFileIO, _durabilityNode, commitBufferCapacity);

        // Determine continuation point from recovery or fresh start
        var lastLSN = _lastRecoveryResult.LastValidLSN;
        var lastSegmentId = 0L; // Segment continuity is handled by WalSegmentManager scanning existing files
        WalManager.Initialize(lastSegmentId, lastLSN > 0 ? lastLSN + 1 : 1);
        WalManager.Logger = _log;
        WalManager.Start();
    }

    private void InitializeCheckpointManager()
    {
        if (WalManager == null)
        {
            return;
        }

        // Read initial CheckpointLSN from file header
        long initialCheckpointLsn;
        using (var guard = EpochGuard.Enter(EpochManager))
        {
            initialCheckpointLsn = MMF.Bootstrap.GetLong(ManagedPagedMMF.BK_CheckpointLSN);
        }

        _stagingBufferPool = new StagingBufferPool(_memoryAllocator, _durabilityNode);

        // Enable FPI capture — creates FpiBitmap internally using cache page count
        MMF.EnableFpiCapture(WalManager, _options.Wal?.EnableFpiCompression ?? false);

        // Activate CRC verification mode — recovery is complete, so OnLoad checks are now safe
        MMF.SetPageChecksumVerification(_options.Resources.PageChecksumVerification);

        CheckpointManager = new CheckpointManager(MMF, UowRegistry, WalManager, _options.Resources, EpochManager, _stagingBufferPool, _durabilityNode,
            initialCheckpointLsn);
        CheckpointManager.Start();

        // Wire demand-driven flush: when page cache backpressure fires, immediately wake
        // the checkpoint thread instead of waiting for the 30s timer interval.
        MMF.OnBackpressure = () => CheckpointManager?.ForceCheckpoint();
    }

    private void InitializeStatisticsWorker()
    {
        var opts = _options.Statistics;
        if (opts == null || !opts.Enabled)
        {
            return;
        }

        StatisticsWorker = new StatisticsWorker(this, opts, EpochManager, this);
        StatisticsWorker.Start();
    }

    /// <summary>
    /// Returns all registered ComponentTables. Used by <see cref="StatisticsWorker"/> to iterate tables.
    /// </summary>
    internal IEnumerable<ComponentTable> GetAllComponentTables() => _componentTableByType.Values;

    private void ConstructComponentStore()
    {
        _componentTableByType = new ConcurrentDictionary<Type, ComponentTable>();
        _componentTableByWalTypeId = new ConcurrentDictionary<ushort, ComponentTable>();
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
            var offset = LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength;
            page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
            MMF.UnlatchPageExclusive(memPageIdx);

            // Write SPI to root header
            MMF.RequestPageEpoch(0, epoch, out var rootMemPageIdx);
            MMF.Bootstrap.SetInt(BK_UowRegistrySPI, segment.RootPageIndex);
            MMF.SaveBootstrap(cs);

            cs.SaveChanges();

            UowRegistry = new UowRegistry(segment, MMF, EpochManager, _memoryAllocator, this);
            UowRegistry.Initialize();
        }
        else
        {
            // Loading path: read SPIs from bootstrap
            var spi = MMF.Bootstrap.GetInt(BK_UowRegistrySPI);
            var checkpointLSN = MMF.Bootstrap.GetLong(ManagedPagedMMF.BK_CheckpointLSN);
            var segment = MMF.GetSegment(spi);
            UowRegistry = new UowRegistry(segment, MMF, EpochManager, _memoryAllocator, this);

            var walDir = _options.Wal?.WalDirectory;
            if (walDir != null && _walFileIO != null && System.IO.Directory.Exists(walDir) && System.IO.Directory.GetFiles(walDir, "*.wal").Length > 0)
            {
                // Two-phase WAL recovery: LoadFromDiskRaw preserves Pending entries for WAL cross-referencing
                UowRegistry.LoadFromDiskRaw();
                using var recovery = new WalRecovery(_walFileIO, walDir, MMF);
                // Pass null for dbe: replay is deferred until component tables are registered (system schema auto-loading, #57)
                _lastRecoveryResult = recovery.Recover(UowRegistry, checkpointLSN, null);
            }
            else
            {
                // No WAL segments — original path (voids all Pending entries)
                UowRegistry.LoadFromDisk();
            }
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

    internal VariableSizedBufferSegment<T, PersistentStore> GetComponentCollectionVSBS<T>() where T : unmanaged =>
        (VariableSizedBufferSegment<T, PersistentStore>)_componentCollectionVSBSByType.GetOrAdd(typeof(T),
            _ => new VariableSizedBufferSegment<T, PersistentStore>(GetComponentCollectionSegment<T>()));

    internal VariableSizedBufferSegmentBase<PersistentStore> GetComponentCollectionVSBS(Type itemType, ChangeSet changeSet = null) =>
        _componentCollectionVSBSByType.GetOrAdd(itemType,
            type =>
            {
                // Create the type for ComponentCollection<T>
                var ctType = typeof(VariableSizedBufferSegment<,>).MakeGenericType(type, typeof(PersistentStore));
                // Use the actual struct size (Marshal.SizeOf) to match sizeof(T) in the generic overload.
                // DatabaseSchemaExtensions.FromType() maps [Component]-attributed types to FieldType.Component (8 bytes),// which is the storage size of a
                // component *reference*, not the struct itself.
                var fieldSize = Marshal.SizeOf(type);
                var segment = GetComponentCollectionSegment(fieldSize, changeSet);
                return (VariableSizedBufferSegmentBase<PersistentStore>)Activator.CreateInstance(ctType, segment);
            });

    unsafe internal ChunkBasedSegment<PersistentStore> GetComponentCollectionSegment<T>() where T : unmanaged =>
        _componentCollectionSegmentByStride.GetOrAdd(
            RoundToStandardStride(Math.Max(sizeof(T) * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader))),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    unsafe internal ChunkBasedSegment<PersistentStore> GetComponentCollectionSegment(int itemSize, ChangeSet changeSet = null) =>
        _componentCollectionSegmentByStride.GetOrAdd(
            RoundToStandardStride(Math.Max(itemSize * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader))),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride, changeSet));

    internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

    // Create the first revision of the system schema
    private unsafe void CreateSystemSchemaR1()
    {
        // Single ChangeSet tracks all structural pages (segments, BTree directories, occupancy bitmaps)
        // allocated during component registration. This replaces the old FlushAllCachedPages() nuclear approach.
        var cs = MMF.CreateChangeSet();

        // Register the system components, passing the ChangeSet so all structural mutations are tracked
        RegisterComponentFromAccessor<ComponentR1>(cs);
        RegisterComponentFromAccessor<SchemaHistoryR1>(cs);
        RegisterComponentFromAccessor<ArchetypeR1>(cs);

        // Get their tables
        _componentsTable = GetComponentTable<ComponentR1>();
        _schemaHistoryTable = GetComponentTable<SchemaHistoryR1>();
        _archetypesTable = GetComponentTable<ArchetypeR1>();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        MMF.RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = MMF.TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during schema save");
        var page = MMF.GetPage(memPageIdx);

        // Save the entry points in the bootstrap dictionary
        cs.AddByMemPageIndex(memPageIdx);

        var bootstrap = MMF.Bootstrap;
        bootstrap.SetInt(BK_SystemSchemaRevision, 1);
        bootstrap.Set(BK_SysComponentR1, BootstrapDictionary.Value.FromInt4(
            _componentsTable.ComponentSegment.RootPageIndex,
            _componentsTable.CompRevTableSegment.RootPageIndex,
            _componentsTable.DefaultIndexSegment.RootPageIndex,
            _componentsTable.String64IndexSegment.RootPageIndex));
        bootstrap.Set(BK_SysSchemaHistory, BootstrapDictionary.Value.FromInt4(
            _schemaHistoryTable.ComponentSegment.RootPageIndex,
            _schemaHistoryTable.CompRevTableSegment.RootPageIndex,
            _schemaHistoryTable.DefaultIndexSegment.RootPageIndex,
            _schemaHistoryTable.String64IndexSegment.RootPageIndex));
        bootstrap.SetLong(BK_NextFreeTSN, TransactionChain.NextFreeId);

        MMF.UnlatchPageExclusive(memPageIdx);

        // Pre-allocate the FieldR1 ComponentCollection segment
        GetComponentCollectionSegment(sizeof(FieldR1), cs);

        // Save the system components schema in the database
        SaveInSystemSchema(_componentsTable);
        SaveInSystemSchema(_schemaHistoryTable);

        // Persist the FieldCollection SPI in bootstrap
        bootstrap.SetInt(BK_CollectionFieldR1, GetComponentCollectionSegment<FieldR1>().RootPageIndex);

        // Save bootstrap to page 0
        MMF.SaveBootstrap(cs);

        cs.SaveChanges();
        MMF.FlushToDisk();
    }

    private (long PK, ComponentR1 Comp, FieldR1[] Fields) SaveInSystemSchema(ComponentTable table)
    {
        var definition = table.Definition;
        using var t = this.CreateQuickTransaction(DurabilityMode.Immediate);

        var nonStaticCount = 0;
        foreach (var kvp in definition.FieldsByName)
        {
            if (!kvp.Value.IsStatic)
            {
                nonStaticCount++;
            }
        }

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
            TailIndexSPI        = table.TailIndexSegment?.RootPageIndex ?? 0,
            SchemaRevision      = definition.Revision,
            FieldCount          = nonStaticCount,
        };

        var fieldList = new List<FieldR1>();
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
                    UnderlyingType = field.UnderlyingType,
                    ArrayLength = field.ArrayLength,
                    IsStatic = field.IsStatic,
                    HasIndex = field.HasIndex,
                    IndexAllowMultiple = field.IndexAllowMultiple,
                    OffsetInComponentStorage = field.OffsetInComponentStorage,
                    SizeInComponentStorage = field.SizeInComponentStorage,
                };

                a.Add(f);
                fieldList.Add(f);
            }
        }

        var pk = t.CreateEntity(ref comp);
        t.Commit();
        return (pk, comp, fieldList.ToArray());
    }

    /// <summary>
    /// Persists schema changes (renames, new fields, removed fields) for a component after the resolver detects that the runtime field layout differs from
    /// the persisted FieldR1 entries. When a migration has occurred, also updates the segment SPIs and component sizes.
    /// </summary>
    /// <param name="pk">Primary key of the existing ComponentR1 entity.</param>
    /// <param name="definition">The resolved component definition with updated field IDs and names.</param>
    /// <param name="migrationResult">Optional migration result containing new segment SPIs.</param>
    private void PersistSchemaChanges(long pk, DBComponentDefinition definition, MigrationResult? migrationResult = null)
    {
        using var t = this.CreateQuickTransaction(DurabilityMode.Immediate);

        t.ReadEntity<ComponentR1>(pk, out var comp);

        // Reset the Fields collection — we rebuild it entirely with the resolved definitions.
        // The old buffer's chunks become orphaned (acceptable for schema-change frequency).
        comp.Fields = default;

        var nonStaticCount = 0;
        foreach (var kvp in definition.FieldsByName)
        {
            if (!kvp.Value.IsStatic)
            {
                nonStaticCount++;
            }
        }

        comp.SchemaRevision = definition.Revision;
        comp.FieldCount = nonStaticCount;

        // Update SPIs and sizes if migration ran
        if (migrationResult.HasValue)
        {
            comp.ComponentSPI = migrationResult.Value.NewComponentSPI;
            comp.VersionSPI = migrationResult.Value.NewVersionSPI;
            comp.CompSize = definition.ComponentStorageSize;
            comp.CompOverhead = definition.ComponentStorageOverhead;
        }

        {
            using var a = t.CreateComponentCollectionAccessor(ref comp.Fields);

            foreach (var kvp in definition.FieldsByName)
            {
                var field = kvp.Value;
                var f = new FieldR1
                {
                    Name = (String64)field.Name,
                    FieldId = field.FieldId,
                    Type = field.Type,
                    UnderlyingType = field.UnderlyingType,
                    ArrayLength = field.ArrayLength,
                    IsStatic = field.IsStatic,
                    HasIndex = field.HasIndex,
                    IndexAllowMultiple = field.IndexAllowMultiple,
                    OffsetInComponentStorage = field.OffsetInComponentStorage,
                    SizeInComponentStorage = field.SizeInComponentStorage,
                };

                a.Add(f);
            }
        }

        t.UpdateEntity(pk, ref comp);
        t.Commit();
    }

    /// <summary>
    /// Restores the system schema (FieldR1 and ComponentR1 tables) from persisted SPIs on database reopen.
    /// Populates <see cref="_persistedComponents"/> so that subsequent <see cref="RegisterComponentFromAccessor{T}"/> calls load existing segments instead
    /// of allocating fresh ones.
    /// </summary>
    private void LoadSystemSchemaR1()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        // Read bootstrap dictionary (already loaded by MMF.OnFileLoading)
        var bootstrap = MMF.Bootstrap;

        // Restore the TSN counter so MVCC visibility works for entities from previous sessions
        var nextFreeTSN = bootstrap.GetLong(BK_NextFreeTSN);
        if (nextFreeTSN > 0)
        {
            TransactionChain.SetNextFreeId(nextFreeTSN);
        }

        if (bootstrap.GetInt(BK_SystemSchemaRevision) == 0)
        {
            return;
        }

        // Register system type definitions in DBD
        DBD.CreateFromAccessor<ComponentR1>();
        DBD.CreateFromAccessor<SchemaHistoryR1>();

        var compDef    = DBD.GetComponent(ComponentR1.SchemaName, 1);
        var historyDef = DBD.GetComponent(SchemaHistoryR1.SchemaName, 1);

        // Load system tables using SPIs from bootstrap
        var compSPIs = bootstrap.Get(BK_SysComponentR1);
        var historySPIs = bootstrap.Get(BK_SysSchemaHistory);

        _componentsTable = new ComponentTable(this, compDef, this, compSPIs.GetInt(0), compSPIs.GetInt(1), compSPIs.GetInt(2), compSPIs.GetInt(3));
        _schemaHistoryTable = new ComponentTable(this, historyDef, this, historySPIs.GetInt(0), historySPIs.GetInt(1), historySPIs.GetInt(2), historySPIs.GetInt(3));

        _componentTableByType.TryAdd(typeof(ComponentR1), _componentsTable);
        _componentTableByType.TryAdd(typeof(SchemaHistoryR1), _schemaHistoryTable);

        var compsWalTypeId = (ushort)_componentsTable.ComponentSegment.RootPageIndex;
        _componentsTable.WalTypeId = compsWalTypeId;
        _componentTableByWalTypeId.TryAdd(compsWalTypeId, _componentsTable);

        var historyWalTypeId = (ushort)_schemaHistoryTable.ComponentSegment.RootPageIndex;
        _schemaHistoryTable.WalTypeId = historyWalTypeId;
        _componentTableByWalTypeId.TryAdd(historyWalTypeId, _schemaHistoryTable);

        // Load the ComponentCollection segment for FieldR1
        int fieldCollectionSPI = bootstrap.GetInt(BK_CollectionFieldR1);
        if (fieldCollectionSPI != 0)
        {
            unsafe
            {
                var stride = RoundToStandardStride(
                    Math.Max(sizeof(FieldR1) * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader)));
                var segment = MMF.LoadChunkBasedSegment(fieldCollectionSPI, stride);
                _componentCollectionSegmentByStride.TryAdd(stride, segment);
            }
        }

        // Read all ComponentR1 entries to build the persisted components dictionary
        _persistedComponents = new Dictionary<string, (long, ComponentR1)>();
        _persistedFieldsByComponent = new Dictionary<string, FieldR1[]>();
        var pkIndex = _componentsTable.PrimaryKeyIndex;
        if (pkIndex.EntryCount > 0)
        {
            using var tx = this.CreateQuickTransaction();

            foreach (var kv in pkIndex.EnumerateLeaves())
            {
                if (tx.ReadEntity<ComponentR1>(kv.Key, out var comp))
                {
                    var schemaName = comp.Name.AsString;
                    _persistedComponents[schemaName] = (kv.Key, comp);
                }
            }

            // Read FieldR1 entries from each persisted component's Fields collection
            if (fieldCollectionSPI != 0)
            {
                var vsbs = GetComponentCollectionVSBS<FieldR1>();
                foreach (var kvp in _persistedComponents)
                {
                    var comp = kvp.Value.Comp;
                    if (comp.Fields._bufferId != 0)
                    {
                        var fields = new List<FieldR1>();
                        foreach (var f in vsbs.EnumerateBuffer(comp.Fields._bufferId))
                        {
                            fields.Add(f);
                        }
                        _persistedFieldsByComponent[kvp.Key] = fields.ToArray();
                    }
                }
            }

            // Update _curPrimaryKey from system table
            UpdateCurPrimaryKey(pkIndex.GetMaxKey());
        }
    }

    /// <summary>
    /// Persists critical engine state to disk during Dispose:
    /// 1. Flushes any dirty pages left by unflushed Deferred UoWs (safety net)
    /// 2. Writes the current TSN counter to the root file header (MVCC visibility on reopen)
    /// 3. Flushes ALL changes to stable storage via SaveChanges + FlushToDisk
    /// </summary>
    private void PersistEngineState()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        var cs = MMF.CreateChangeSet();

        // Safety net: collect dirty pages left by unflushed Deferred UoWs and include
        // them in this ChangeSet so they are persisted during the final SaveChanges.
        var dirtyPages = MMF.CollectDirtyMemPageIndices();
        if (dirtyPages.Length > 0)
        {
            _log?.LogWarning("Engine shutdown: flushing {Count} dirty page(s) to disk", dirtyPages.Length);
            foreach (var idx in dirtyPages)
            {
                cs.AddByMemPageIndex(idx);
            }
        }

        // Write TSN counter to root file header
        MMF.RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = MMF.TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during engine state save");
        var page = MMF.GetPage(memPageIdx);

        cs.AddByMemPageIndex(memPageIdx);

        // Update bootstrap with current TSN
        MMF.Bootstrap.SetLong(BK_NextFreeTSN, TransactionChain.NextFreeId);
        MMF.SaveBootstrap(cs);

        MMF.UnlatchPageExclusive(memPageIdx);

        cs.SaveChanges();
        MMF.FlushToDisk();
    }

    /// <summary>
    /// Increments the UserSchemaVersion counter in the bootstrap dictionary.
    /// Called after any user component schema change is persisted.
    /// </summary>
    private void IncrementUserSchemaVersion()
    {
        var currentVersion = MMF.Bootstrap.GetInt(BK_UserSchemaVersion);
        MMF.Bootstrap.SetInt(BK_UserSchemaVersion, currentVersion + 1);
        MMF.SaveBootstrap();
    }

    /// <summary>
    /// Records a schema change in the <see cref="SchemaHistoryR1"/> audit trail.
    /// Called during <see cref="RegisterComponentFromAccessor{T}"/> after schema persistence.
    /// </summary>
    private void RecordSchemaHistory(string componentName, SchemaDiff diff, MigrationResult? migrationResult, int fromRevision, int toRevision)
    {
        if (_schemaHistoryTable == null)
        {
            return;
        }

        var added = 0;
        var removed = 0;
        var typeChanged = 0;

        if (diff != null)
        {
            foreach (var fc in diff.FieldChanges)
            {
                switch (fc.Kind)
                {
                    case FieldChangeKind.Added:
                        added++;
                        break;
                    case FieldChangeKind.Removed:
                        removed++;
                        break;
                    case FieldChangeKind.TypeChanged:
                    case FieldChangeKind.TypeWidened:
                        typeChanged++;
                        break;
                }
            }
        }

        var kind = diff != null && diff.HasBreakingChanges ? SchemaChangeKind.Migration : SchemaChangeKind.Compatible;

        var entry = new SchemaHistoryR1
        {
            Timestamp = DateTime.UtcNow.Ticks,
            ComponentName = (String64)componentName,
            FromRevision = fromRevision,
            ToRevision = toRevision,
            FieldsAdded = added,
            FieldsRemoved = removed,
            FieldsTypeChanged = typeChanged,
            EntitiesMigrated = migrationResult?.EntitiesMigrated ?? 0,
            ElapsedMilliseconds = (int)(migrationResult?.ElapsedMs ?? 0),
            Kind = kind,
        };

        using var t = this.CreateQuickTransaction(DurabilityMode.Immediate);
        t.CreateEntity(ref entry);
        t.Commit();
    }

    /// <summary>
    /// Returns all schema history entries from the audit trail, ordered by primary key (chronological).
    /// </summary>
    [PublicAPI]
    public IReadOnlyList<SchemaHistoryR1> GetSchemaHistory()
    {
        if (_schemaHistoryTable == null)
        {
            return [];
        }

        var pkIndex = _schemaHistoryTable.PrimaryKeyIndex;
        if (pkIndex.EntryCount == 0)
        {
            return [];
        }

        var result = new List<SchemaHistoryR1>(pkIndex.EntryCount);
        using var tx = this.CreateQuickTransaction();

        foreach (var kv in pkIndex.EnumerateLeaves())
        {
            if (tx.ReadEntity<SchemaHistoryR1>(kv.Key, out var entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private void UpdateCurPrimaryKey(long pk)
    {
        long current;
        do
        {
            current = _curPrimaryKey;
        }
        while (pk > current && Interlocked.CompareExchange(ref _curPrimaryKey, pk, current) != current);
    }

    public bool RegisterComponentFromAccessor<T>(ChangeSet changeSet = null, SchemaValidationMode schemaValidation = SchemaValidationMode.Enforce)
        where T : unmanaged
    {
        // Look up persisted fields for the resolver (keyed by component schema name)
        FieldIdResolver resolver = null;
        var componentAttr = typeof(T).GetCustomAttribute<ComponentAttribute>();
        var schemaName = componentAttr?.Name ?? typeof(T).Name;

        FieldR1[] persistedFields = null;
        if (_persistedFieldsByComponent != null && _persistedFieldsByComponent.TryGetValue(schemaName, out persistedFields))
        {
            resolver = new FieldIdResolver(persistedFields);
        }

        var definition = DBD.CreateFromAccessor<T>(resolver);
        if (definition == null)
        {
            return false;
        }

        ComponentTable componentTable;

        if (_persistedComponents != null && _persistedComponents.TryGetValue(schemaName, out var persisted))
        {
            // Schema validation: compare persisted vs runtime before loading data
            SchemaDiff diff = null;
            MigrationResult? migrationResult = null;
            HashSet<int> newIndexFieldIds = null;

            if (persistedFields != null)
            {
                diff = SchemaValidator.ComputeDiff(schemaName, persistedFields, persisted.Comp, definition, 
                    resolver.Renames ?? (IReadOnlyList<(string, string, int)>)[]);

                if (diff.HasBreakingChanges && schemaValidation != SchemaValidationMode.Skip)
                {
                    // Breaking changes detected — check migration registry for a user-provided migration path
                    var targetRevision = componentAttr?.Revision ?? 1;
                    var persistedRevision = persisted.Comp.SchemaRevision;

                    // Backward compat: databases created before Phase 4 have SchemaRevision=0.
                    // Try the persisted value first, then fall back to searching the registry.
                    var chain = _migrationRegistry?.GetChain(schemaName, persistedRevision, targetRevision);
                    if (chain == null && persistedRevision == 0 && _migrationRegistry != null)
                    {
                        // Legacy database: SchemaRevision was auto-incremented, not attribute-based.
                        // Scan for a viable chain by trying common starting revisions.
                        chain = _migrationRegistry.GetChain(schemaName, 1, targetRevision);
                    }

                    if (chain == null)
                    {
                        throw new SchemaValidationException(diff);
                    }

                    _log?.LogInformation(
                        "Breaking schema change for '{Name}': {Summary}. Migration chain registered ({StepCount} step(s))",
                        schemaName, diff.Summary, chain.Value.StepCount);

                    migrationResult = SchemaEvolutionEngine.MigrateWithFunction(
                        MMF, EpochManager, diff, persistedFields, persisted.Comp, definition, chain.Value, _log, RaiseMigrationProgress);
                }

                if (!diff.IsIdentical)
                {
                    switch (diff.Level)
                    {
                        case CompatibilityLevel.CompatibleWidening:
                            _log?.LogWarning("Schema widening for '{Name}': {Summary}", schemaName, diff.Summary);
                            break;
                        case CompatibilityLevel.Breaking:
                            // Already handled above via migration function
                            break;
                        case >= CompatibilityLevel.Compatible:
                            _log?.LogInformation("Schema evolution for '{Name}': {Summary}", schemaName, diff.Summary);
                            break;
                        case CompatibilityLevel.InformationOnly:
                            _log?.LogInformation("Schema renames for '{Name}': {Summary}", schemaName, diff.Summary);
                            break;
                    }

                    // For compatible changes (non-breaking), use the field-map migration path
                    if (!diff.HasBreakingChanges)
                    {
                        var oldStride = persisted.Comp.CompSize + persisted.Comp.CompOverhead;
                        var newStride = definition.ComponentStorageTotalSize;

                        if (SchemaEvolutionEngine.NeedsMigration(diff, oldStride, newStride))
                        {
                            migrationResult = SchemaEvolutionEngine.Migrate(MMF, EpochManager, diff, persistedFields, persisted.Comp, definition, _log,
                                RaiseMigrationProgress);
                        }
                    }

                    newIndexFieldIds = SchemaEvolutionEngine.GetNewIndexFieldIds(diff);
                }
            }

            // Load path: use migration constructor if migration ran (segments already in MMF registry), otherwise standard load constructor from persisted SPIs
            var migrationChangeSet = (migrationResult.HasValue || newIndexFieldIds != null) ? MMF.CreateChangeSet() : null;

            if (migrationResult.HasValue)
            {
                componentTable = new ComponentTable(this, definition, this, migrationResult.Value.NewComponentSegment, migrationResult.Value.NewRevisionSegment,
                    persisted.Comp.DefaultIndexSPI, persisted.Comp.String64IndexSPI, persisted.Comp.TailIndexSPI, newIndexFieldIds: newIndexFieldIds, 
                    changeSet: migrationChangeSet);
            }
            else
            {
                componentTable = new ComponentTable(this, definition, this, persisted.Comp.ComponentSPI, persisted.Comp.VersionSPI, persisted.Comp.DefaultIndexSPI,
                    persisted.Comp.String64IndexSPI, persisted.Comp.TailIndexSPI, newIndexFieldIds: newIndexFieldIds, changeSet: migrationChangeSet);
            }

            // Populate newly created indexes by scanning entities
            if (newIndexFieldIds != null)
            {
                componentTable.PopulateNewIndexes(newIndexFieldIds, migrationChangeSet);
                migrationChangeSet?.SaveChanges();
                MMF.FlushToDisk();
            }

            // Update _curPrimaryKey from loaded PK index
            if (componentTable.PrimaryKeyIndex.EntryCount > 0)
            {
                var maxPk = componentTable.PrimaryKeyIndex.GetMaxKey();
                UpdateCurPrimaryKey(maxPk);
            }

            // Persist schema changes if the resolver detected changes or migration ran
            if ((resolver != null && resolver.HasChanges) || migrationResult.HasValue)
            {
                PersistSchemaChanges(persisted.PK, definition, migrationResult);
                IncrementUserSchemaVersion();

                // Record in schema history audit trail
                RecordSchemaHistory(schemaName, diff, migrationResult, persisted.Comp.SchemaRevision, definition.Revision);
            }
        }
        else
        {
            // Create path: use the provided ChangeSet, or create a new one for standalone registration
            var cs = changeSet ?? MMF.CreateChangeSet();
            componentTable = new ComponentTable(this, definition, this, changeSet: cs);

            // Save metadata for future reload (skip during initial CreateSystemSchemaR1)
            if (_componentsTable != null)
            {
                var saved = SaveInSystemSchema(componentTable);
                cs.SaveChanges();
                MMF.FlushToDisk();

                // Populate persisted dictionaries so schema commands work on first-run databases
                _persistedComponents ??= new Dictionary<string, (long, ComponentR1)>();
                _persistedFieldsByComponent ??= new Dictionary<string, FieldR1[]>();
                _persistedComponents[schemaName] = (saved.PK, saved.Comp);
                _persistedFieldsByComponent[schemaName] = saved.Fields;
            }
        }

        _componentTableByType.TryAdd(typeof(T), componentTable);

        // Assign a stable WAL type ID derived from the component segment's persistent root page index
        var walTypeId = (ushort)componentTable.ComponentSegment.RootPageIndex;
        componentTable.WalTypeId = walTypeId;
        _componentTableByWalTypeId.TryAdd(walTypeId, componentTable);

        return true;
    }

    /// <summary>
    /// Registers a strongly-typed migration function that transforms component data from <typeparamref name="TOld"/> to <typeparamref name="TNew"/>.
    /// Both types must have [Component] attributes with the same Name but different Revisions.
    /// Must be called before <see cref="RegisterComponentFromAccessor{T}"/> for the target component.
    /// </summary>
    public void RegisterMigration<TOld, TNew>(MigrationFunc<TOld, TNew> func) where TOld : unmanaged where TNew : unmanaged
    {
        _migrationRegistry ??= new MigrationRegistry();
        _migrationRegistry.Register(func);
    }

    /// <summary>
    /// Registers a byte-level migration function for scenarios where the old struct type is no longer available in code.
    /// Must be called before <see cref="RegisterComponentFromAccessor{T}"/> for the target component.
    /// </summary>
    public void RegisterByteMigration(string componentName, int fromRevision, int toRevision, int oldSize, int newSize, ByteMigrationFunc func)
    {
        _migrationRegistry ??= new MigrationRegistry();
        _migrationRegistry.RegisterByte(componentName, fromRevision, toRevision, oldSize, newSize, func);
    }

    public QueryBuilder<T> Query<T>() where T : unmanaged => new(this);

    public QueryBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged => new(this);

    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    public ComponentTable GetComponentTable(Type type) => _componentTableByType.GetValueOrDefault(type);

    /// <summary>
    /// Looks up a <see cref="ComponentTable"/> by its WAL type ID (derived from <see cref="ChunkBasedSegment<PersistentStore>.RootPageIndex"/>).
    /// Returns null if the type ID is unknown.
    /// </summary>
    internal ComponentTable GetComponentTableByWalTypeId(ushort id) => _componentTableByWalTypeId.GetValueOrDefault(id);

    /// <summary>
    /// Initialize ECS archetype storage. For each registered archetype, allocates a per-archetype RawValueHashMap and connects component slots to their
    /// ComponentTables. Must be called after all components are registered.
    /// </summary>
    public void InitializeArchetypes()
    {
        ArchetypeRegistry.Freeze();

        // Ensure ArchetypeR1 system component is registered (may not be if this is a database reopen
        // — CreateSystemSchemaR1 only runs on new databases, LoadSystemSchemaR1 doesn't restore ArchetypeR1)
        if (GetComponentTable<ArchetypeR1>() == null)
        {
            RegisterComponentFromAccessor<ArchetypeR1>();
        }

        // Load persisted archetype schemas for validation
        _persistedArchetypes ??= new Dictionary<ushort, (long, ArchetypeR1)>();
        LoadPersistedArchetypes();

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            // Connect slots to ComponentTables — skip archetypes with unregistered component types
            if (meta._slotToComponentType == null || meta.ComponentCount == 0)
            {
                continue;
            }

            meta._slotToComponentTable = new ComponentTable[meta.ComponentCount];
            bool allComponentsRegistered = true;
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var compType = meta._slotToComponentType[slot];
                if (compType == null)
                {
                    allComponentsRegistered = false;
                    break;
                }
                var table = GetComponentTable(compType);
                if (table == null)
                {
                    allComponentsRegistered = false;
                    break;
                }
                meta._slotToComponentTable[slot] = table;
            }

            if (!allComponentsRegistered)
            {
                meta._slotToComponentTable = null;
                continue;
            }

            // Schema validation: compare runtime archetype against persisted schema
            ValidateArchetypeSchema(meta);

            // Allocate per-archetype entity storage (RawValueHashMap)
            {
                int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(meta._entityRecordSize);
                var segment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
                meta._entityMap = RawValueHashMap<long, PersistentStore>.Create(segment, 16, meta._entityRecordSize);
                meta._nextEntityKey = 0;
            }
        }

        // Build and validate cascade delete graph (after all slots connected)
        ArchetypeRegistry.BuildAndValidateCascadeGraph();

        // Persist any new archetypes not yet in the database
        PersistNewArchetypes();
    }

    private void LoadPersistedArchetypes()
    {
        var archetypesTable = GetComponentTable<ArchetypeR1>();
        if (archetypesTable == null)
        {
            return;
        }

        var pkIndex = archetypesTable.PrimaryKeyIndex;
        if (pkIndex.EntryCount == 0)
        {
            return;
        }

        using var tx = this.CreateQuickTransaction();
        foreach (var kv in pkIndex.EnumerateLeaves())
        {
            if (tx.ReadEntity<ArchetypeR1>(kv.Key, out var arch))
            {
                _persistedArchetypes[arch.ArchetypeId] = (kv.Key, arch);
            }
        }
    }

    private void ValidateArchetypeSchema(ArchetypeMetadata meta)
    {
        if (!_persistedArchetypes.TryGetValue(meta.ArchetypeId, out var persisted))
        {
            return; // new archetype, not persisted yet — OK
        }

        var arch = persisted.Arch;

        // Component count mismatch
        if (arch.ComponentCount != meta.ComponentCount)
        {
            throw new InvalidOperationException(
                $"Schema mismatch for archetype '{meta.ArchetypeType.Name}' (Id={meta.ArchetypeId}): " +
                $"persisted with {arch.ComponentCount} components, runtime has {meta.ComponentCount}. " +
                $"Run 'tsh migrate <dbpath>' to upgrade.");
        }

        // Revision mismatch
        if (arch.Revision != meta.Revision)
        {
            throw new InvalidOperationException(
                $"Schema mismatch for archetype '{meta.ArchetypeType.Name}' (Id={meta.ArchetypeId}): " +
                $"persisted revision {arch.Revision}, runtime revision {meta.Revision}. " +
                $"Run 'tsh migrate <dbpath>' to upgrade.");
        }

        // Component name mismatch (per slot)
        if (arch.ComponentNames._bufferId != 0)
        {
            var vsbs = GetComponentCollectionVSBS<String64>();
            var persistedNames = new List<String64>();
            foreach (var name in vsbs.EnumerateBuffer(arch.ComponentNames._bufferId))
            {
                persistedNames.Add(name);
            }

            var runtimeNames = GetArchetypeComponentNames(meta);
            for (int slot = 0; slot < Math.Min(persistedNames.Count, runtimeNames.Length); slot++)
            {
                if (!persistedNames[slot].Equals(runtimeNames[slot]))
                {
                    throw new InvalidOperationException(
                        $"Schema mismatch for archetype '{meta.ArchetypeType.Name}' (Id={meta.ArchetypeId}), slot {slot}: " +
                        $"persisted component '{persistedNames[slot].AsString}', runtime component '{runtimeNames[slot].AsString}'. " +
                        $"Run 'tsh migrate <dbpath>' to upgrade.");
                }
            }
        }
    }

    private unsafe void PersistNewArchetypes()
    {
        bool anyNew = false;

        using var t = this.CreateQuickTransaction(DurabilityMode.Immediate);

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (meta._slotToComponentTable == null)
            {
                continue;
            }

            if (_persistedArchetypes.ContainsKey(meta.ArchetypeId))
            {
                continue;
            }

            // Build and persist the ArchetypeR1 entity
            var arch = BuildArchetypeR1(meta);

            // Populate ComponentNames collection via VSBS
            var names = GetArchetypeComponentNames(meta);
            var cs = MMF.CreateChangeSet();
            var vsbs = GetComponentCollectionVSBS<String64>();
            using (var cca = new ComponentCollectionAccessor<String64>(cs, vsbs, ref arch.ComponentNames))
            {
                foreach (var name in names)
                {
                    cca.Add(name);
                }
            }
            cs.SaveChanges();

            t.CreateEntity(ref arch);
            _persistedArchetypes[meta.ArchetypeId] = (0, arch); // PK not tracked, just mark as persisted
            anyNew = true;
        }

        if (anyNew)
        {
            t.Commit();
        }
    }

    /// <summary>Build an ArchetypeR1 header from runtime metadata. ComponentNames must be populated separately via VSBS.</summary>
    internal static ArchetypeR1 BuildArchetypeR1(ArchetypeMetadata meta) => new()
    {
        Name = meta.ArchetypeType.Name,
        ArchetypeId = meta.ArchetypeId,
        ParentArchetypeId = meta.ParentArchetypeId,
        ComponentCount = meta.ComponentCount,
        Revision = meta.Revision,
    };

    /// <summary>Get the component schema names for an archetype's slots (for validation/persistence).</summary>
    internal static String64[] GetArchetypeComponentNames(ArchetypeMetadata meta)
    {
        var names = new String64[meta.ComponentCount];
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            var compType = meta._slotToComponentType[slot];
            var compAttr = compType.GetCustomAttribute<ComponentAttribute>();
            names[slot] = compAttr != null ? compAttr.Name : compType.Name;
        }
        return names;
    }

    /// <summary>
    /// Returns an <see cref="IndexRef"/> for the primary key index of component <typeparamref name="T"/>.
    /// Resolve once (cold path), reuse many times at zero cost (hot path).
    /// </summary>
    public IndexRef GetPKIndexRef<T>() where T : unmanaged
    {
        var ct = GetComponentTable<T>() ?? throw new InvalidOperationException($"Component '{typeof(T).Name}' is not registered.");
        return new IndexRef(-1, ct, ct.IndexLayoutVersion);
    }

    /// <summary>
    /// Returns an <see cref="IndexRef"/> for a secondary indexed field of component <typeparamref name="T"/>.
    /// Resolve once (cold path), reuse many times at zero cost (hot path).
    /// </summary>
    public IndexRef GetIndexRef<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        var ct = GetComponentTable<T>() ?? throw new InvalidOperationException($"Component '{typeof(T).Name}' is not registered.");
        var fieldName = ExpressionParser.ExtractFieldName(keySelector);
        if (!ct.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on '{ct.Definition.Name}'.");
        }

        if (!field.HasIndex)
        {
            throw new InvalidOperationException($"Field '{fieldName}' is not indexed.");
        }

        var fieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition, field);
        return new IndexRef(fieldIndex, ct, ct.IndexLayoutVersion);
    }

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

    [LoggerMessage(LogLevel.Warning, "Deferred UoW #{uowId} disposed with {count} committed transaction(s) without Flush/FlushAsync. Data relies on engine shutdown safety net.")]
    internal partial void LogDeferredUowNotFlushed(ushort uowId, int count);

    [LoggerMessage(LogLevel.Debug, "UoW #{uowId} ({mode}) flush: waiting for WAL durable LSN {targetLsn}")]
    internal partial void LogUowFlushStart(ushort uowId, DurabilityMode mode, long targetLsn);

    [LoggerMessage(LogLevel.Debug, "UoW #{uowId} flush complete")]
    internal partial void LogUowFlushComplete(ushort uowId);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit start: {count} component types")]
    internal partial void LogCommitStart(long tsn, int count);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: {phase}")]
    internal partial void LogCommitPhase(long tsn, string phase);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} dispose: {phase}")]
    internal partial void LogTxDispose(long tsn, string phase);

    [LoggerMessage(LogLevel.Debug, "UoW: {phase}")]
    internal partial void LogUowLifecycle(string phase);

    [LoggerMessage(LogLevel.Debug, "UoW: UowId allocated: {uowId}")]
    internal partial void LogUowIdAllocated(ushort uowId);

    [LoggerMessage(LogLevel.Debug, "Tx.Init #{tsn}: {phase}")]
    internal partial void LogTxInitPhase(long tsn, string phase);

    [LoggerMessage(LogLevel.Debug, "CreateQuickTransaction: Tx #{tsn} created")]
    internal partial void LogQuickTxCreated(long tsn);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: CreateComponent<{componentName}> pk={pk}: {step}")]
    internal partial void LogCommitCreateComponent(long tsn, string componentName, long pk, string step);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: CommitComponent {componentName} ({entryCount} entries)")]
    internal partial void LogCommitComponentEntries(long tsn, string componentName, int entryCount);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: CommitComponent {componentName} done")]
    internal partial void LogCommitComponentDone(long tsn, string componentName);

    [LoggerMessage(LogLevel.Debug, "Cascade delete: following FK on child archetype {childArchetype} slot {slotIndex} from parent {parentId}")]
    internal partial void LogCascadeStep(string childArchetype, int slotIndex, EntityId parentId);

    [LoggerMessage(LogLevel.Information, "Cascade delete complete: root {rootId}, total destroyed {totalDestroyed}")]
    internal partial void LogCascadeSummary(EntityId rootId, int totalDestroyed);

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