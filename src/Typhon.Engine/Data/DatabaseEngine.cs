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
    public int TailIndexSPI;

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

    /// <summary>
    /// WAL writer configuration. Null disables WAL durability (in-memory only).
    /// </summary>
    public WalWriterOptions Wal { get; set; }
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
    private readonly IWalFileIO                 _walFileIO;
    private readonly IResource                  _durabilityNode;
    private WalRecoveryResult                   _lastRecoveryResult;
    internal WalRecoveryResult                  LastRecoveryResult => _lastRecoveryResult;
    private StagingBufferPool                   _stagingBufferPool;

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
    private ConcurrentDictionary<ushort, ComponentTable> _componentTableByWalTypeId;
    private long _curPrimaryKey;
    private Dictionary<string, (long PK, ComponentR1 Comp)> _persistedComponents;
    private Dictionary<string, FieldR1[]> _persistedFieldsByComponent;
    private ConcurrentDictionary<int, ChunkBasedSegment> _componentCollectionSegmentByStride;
    private ConcurrentDictionary<Type, VariableSizedBufferSegmentBase> _componentCollectionVSBSByType;

    public DatabaseDefinitions DBD { get; }
    public ManagedPagedMMF MMF { get; }
    public EpochManager EpochManager { get; private set; }
    public DeadlineWatchdog Watchdog { get; }

    internal TransactionChain TransactionChain { get; }
    internal DeferredCleanupManager DeferredCleanupManager { get; }

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
        var effectiveTimeout = timeout == TimeSpan.Zero ? TimeoutOptions.Current.DefaultUowTimeout : timeout;
        var wc = WaitContext.FromTimeout(effectiveTimeout);

        // For Deferred/GroupCommit: create the ChangeSet early so AllocateUowId can track
        // the registry page mutation in it (avoiding a synchronous SaveChanges).
        var changeSet = durabilityMode != DurabilityMode.Immediate ? MMF.CreateChangeSet() : null;

        // Back-pressure: if registry is full, wait for a slot to be freed.
        // The admission check is a fast-path optimization — AllocateUowId's CAS provides the real atomicity (TOCTOU by design).
        var uowId = UowRegistry.AllocateUowId(ref wc, changeSet);

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
        else
        {
            LoadSystemSchemaR1();
        }

        InitializeWalManager();
        InitializeCheckpointManager();
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
            // Checkpoint must dispose first: runs final cycle, writes pages + advances LSN before WAL shuts down
            CheckpointManager?.Dispose();
            CheckpointManager = null;

            // Dispose staging pool after checkpoint manager (checkpoint may use it during final cycle)
            _stagingBufferPool?.Dispose();
            _stagingBufferPool = null;

            // Persist final TSN counter and flush all dirty pages to disk. This ensures:
            // 1. TSN counter survives restart (MVCC visibility)
            // 2. All committed transaction data is on disk even without WAL/checkpoint
            PersistEngineState();

            WalManager?.Dispose();
            WalManager = null;
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
            MMF.RequestPageEpoch(0, guard.Epoch, out var memPageIdx);
            var page = MMF.GetPage(memPageIdx);
            ref var header = ref page.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);
            initialCheckpointLsn = header.CheckpointLSN;
        }

        _stagingBufferPool = new StagingBufferPool(_memoryAllocator, _durabilityNode);

        // Enable FPI capture — creates FpiBitmap internally using cache page count
        MMF.EnableFpiCapture(WalManager, _options.Wal?.EnableFpiCompression ?? false);

        // Activate CRC verification mode — recovery is complete, so OnLoad checks are now safe
        MMF.SetPageChecksumVerification(_options.Resources.PageChecksumVerification);

        CheckpointManager = new CheckpointManager(MMF, UowRegistry, WalManager, _options.Resources, EpochManager, _stagingBufferPool, _durabilityNode,
            initialCheckpointLsn);
        CheckpointManager.Start();
    }

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
            var offset = LogicalSegment.RootHeaderIndexSectionLength;
            page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
            MMF.UnlatchPageExclusive(memPageIdx);

            // Write SPI to root header
            MMF.RequestPageEpoch(0, epoch, out var rootMemPageIdx);
            var rootLatched = MMF.TryLatchPageExclusive(rootMemPageIdx);
            Debug.Assert(rootLatched, "TryLatchPageExclusive failed on root page during registry init");
            var rootPage = MMF.GetPage(rootMemPageIdx);
            cs.AddByMemPageIndex(rootMemPageIdx);
            ref var header = ref rootPage.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);
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
            ref var header = ref rootPage.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);
            var spi = header.UowRegistrySPI;
            var checkpointLSN = header.CheckpointLSN;
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

    internal VariableSizedBufferSegment<T> GetComponentCollectionVSBS<T>() where T : unmanaged =>
        (VariableSizedBufferSegment<T>)_componentCollectionVSBSByType.GetOrAdd(typeof(T),
            _ => new VariableSizedBufferSegment<T>(GetComponentCollectionSegment<T>()));

    internal VariableSizedBufferSegmentBase GetComponentCollectionVSBS(Type itemType, ChangeSet changeSet = null) =>
        _componentCollectionVSBSByType.GetOrAdd(itemType,
            type =>
            {
                // Create the type for ComponentCollection<T>
                var ctType = typeof(VariableSizedBufferSegment<>).MakeGenericType(type);
                // Use the actual struct size (Marshal.SizeOf) to match sizeof(T) in the generic overload.
                // DatabaseSchemaExtensions.FromType() maps [Component]-attributed types to FieldType.Component (8 bytes),// which is the storage size of a
                // component *reference*, not the struct itself.
                var fieldSize = Marshal.SizeOf(type);
                var segment = GetComponentCollectionSegment(fieldSize, changeSet);
                return (VariableSizedBufferSegmentBase)Activator.CreateInstance(ctType, segment);
            });

    unsafe internal ChunkBasedSegment GetComponentCollectionSegment<T>() where T : unmanaged =>
        _componentCollectionSegmentByStride.GetOrAdd(
            RoundToStandardStride(Math.Max(sizeof(T) * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader))),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    unsafe internal ChunkBasedSegment GetComponentCollectionSegment(int itemSize, ChangeSet changeSet = null) =>
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
        RegisterComponentFromAccessor<FieldR1>(cs);
        RegisterComponentFromAccessor<ComponentR1>(cs);

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
        cs.AddByMemPageIndex(memPageIdx);
        ref var rootFileHeader = ref page.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);

        rootFileHeader.SystemSchemaRevision = 1;
        rootFileHeader.FieldTableSPI = _fieldsTable.ComponentSegment.RootPageIndex;
        rootFileHeader.ComponentTableSPI = _componentsTable.ComponentSegment.RootPageIndex;

        rootFileHeader.FieldTableVersionSPI            = _fieldsTable.CompRevTableSegment.RootPageIndex;
        rootFileHeader.FieldTableDefaultIndexSPI       = _fieldsTable.DefaultIndexSegment.RootPageIndex;
        rootFileHeader.FieldTableString64IndexSPI      = _fieldsTable.String64IndexSegment.RootPageIndex;
        rootFileHeader.ComponentTableVersionSPI        = _componentsTable.CompRevTableSegment.RootPageIndex;
        rootFileHeader.ComponentTableDefaultIndexSPI   = _componentsTable.DefaultIndexSegment.RootPageIndex;
        rootFileHeader.ComponentTableString64IndexSPI  = _componentsTable.String64IndexSegment.RootPageIndex;
        rootFileHeader.NextFreeTSN = TransactionChain.NextFreeId;

        MMF.UnlatchPageExclusive(memPageIdx);

        // Pre-allocate the FieldR1 ComponentCollection segment with the structural ChangeSet so its pages// are tracked and flushed to disk. Without this,
        // the segment would be lazily allocated in SaveInSystemSchema without change tracking, leaving its root page dirty-but-untracked in the page cache.
        GetComponentCollectionSegment(sizeof(FieldR1), cs);

        // Now save the system components schema in the database (to load them next time we open the database)
        // These use transactions internally which have their own ChangeSets
        SaveInSystemSchema(_fieldsTable);
        SaveInSystemSchema(_componentsTable);

        // Persist the ComponentCollection segment SPI for FieldR1 so we can reload it on reopen.
        // The segment was lazily allocated during SaveInSystemSchema when FieldR1 entries were written.
        {
            MMF.RequestPageEpoch(0, epoch, out var rootMemPageIdx2);
            var latched2 = MMF.TryLatchPageExclusive(rootMemPageIdx2);
            Debug.Assert(latched2, "TryLatchPageExclusive failed on root page during FieldCollection SPI save");
            var page2 = MMF.GetPage(rootMemPageIdx2);
            cs.AddByMemPageIndex(rootMemPageIdx2);
            ref var h2 = ref page2.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);
            h2.FieldCollectionSegmentSPI = GetComponentCollectionSegment<FieldR1>().RootPageIndex;
            MMF.UnlatchPageExclusive(rootMemPageIdx2);
        }

        cs.SaveChanges();
        MMF.FlushToDisk();
    }

    private void SaveInSystemSchema(ComponentTable table)
    {
        var definition = table.Definition;
        using var t = this.CreateQuickTransaction(DurabilityMode.Immediate);

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

        t.CreateEntity(ref comp);
        t.Commit();
    }

    /// <summary>
    /// Persists schema changes (renames, new fields, removed fields) for a component after the resolver detects that the runtime field layout differs from
    /// Rthe persisted FieldR1 entries.
    /// </summary>
    /// <param name="pk">Primary key of the existing ComponentR1 entity.</param>
    /// <param name="definition">The resolved component definition with updated field IDs and names.</param>
    private void PersistSchemaChanges(long pk, DBComponentDefinition definition)
    {
        using var t = this.CreateQuickTransaction(DurabilityMode.Immediate);

        t.ReadEntity<ComponentR1>(pk, out var comp);

        // Reset the Fields collection — we rebuild it entirely with the resolved definitions.
        // The old buffer's chunks become orphaned (acceptable for schema-change frequency).
        comp.Fields = default;

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
                    ArrayLength = field.ArrayLength
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

        MMF.RequestPageEpoch(0, epoch, out var memPageIdx);
        var page = MMF.GetPage(memPageIdx);
        ref var h = ref page.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);

        // Restore the TSN counter so MVCC visibility works for entities from previous sessions
        if (h.NextFreeTSN > 0)
        {
            TransactionChain.SetNextFreeId(h.NextFreeTSN);
        }

        if (h.SystemSchemaRevision == 0)
        {
            return;
        }

        // Register system type definitions in DBD
        DBD.CreateFromAccessor<FieldR1>();
        DBD.CreateFromAccessor<ComponentR1>();

        var fieldDef = DBD.GetComponent(FieldR1.SchemaName, 1);
        var compDef  = DBD.GetComponent(ComponentR1.SchemaName, 1);

        // Load system tables using the persisted SPIs
        _fieldsTable = new ComponentTable(this, fieldDef, this, h.FieldTableSPI, h.FieldTableVersionSPI, h.FieldTableDefaultIndexSPI, h.FieldTableString64IndexSPI);
        _componentsTable = new ComponentTable(this, compDef, this, h.ComponentTableSPI, h.ComponentTableVersionSPI, h.ComponentTableDefaultIndexSPI, h.ComponentTableString64IndexSPI);

        _componentTableByType.TryAdd(typeof(FieldR1), _fieldsTable);
        _componentTableByType.TryAdd(typeof(ComponentR1), _componentsTable);

        var fieldsWalTypeId = (ushort)_fieldsTable.ComponentSegment.RootPageIndex;
        _fieldsTable.WalTypeId = fieldsWalTypeId;
        _componentTableByWalTypeId.TryAdd(fieldsWalTypeId, _fieldsTable);

        var compsWalTypeId = (ushort)_componentsTable.ComponentSegment.RootPageIndex;
        _componentsTable.WalTypeId = compsWalTypeId;
        _componentTableByWalTypeId.TryAdd(compsWalTypeId, _componentsTable);

        // Load the ComponentCollection segment for FieldR1 so we can read persisted field definitions.
        // This segment was persisted as FieldCollectionSegmentSPI in the root header during creation.
        if (h.FieldCollectionSegmentSPI != 0)
        {
            unsafe
            {
                var stride = RoundToStandardStride(
                    Math.Max(sizeof(FieldR1) * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader)));
                var segment = MMF.LoadChunkBasedSegment(h.FieldCollectionSegmentSPI, stride);
                _componentCollectionSegmentByStride.TryAdd(stride, segment);
            }
        }

        // Read all ComponentR1 entries to build the persisted components dictionary
        _persistedComponents = new Dictionary<string, (long, ComponentR1)>();
        _persistedFieldsByComponent = new Dictionary<string, FieldR1[]>();
        var entryCount = _componentsTable.PrimaryKeyIndex.EntryCount;
        if (entryCount > 0)
        {
            long maxPk = 0;
            int found = 0;

            using var tx = this.CreateQuickTransaction();
            for (long pk = 1; found < entryCount; pk++)
            {
                if (pk > entryCount * 10)
                {
                    break;
                }

                if (!tx.ReadEntity<ComponentR1>(pk, out var comp))
                {
                    continue;
                }

                found++;
                if (pk > maxPk)
                {
                    maxPk = pk;
                }

                // Key by schema name (logical identity) for matching during registration
                var schemaName = comp.Name.AsString;
                _persistedComponents[schemaName] = (pk, comp);
            }

            // Read FieldR1 entries from each persisted component's Fields collection
            if (h.FieldCollectionSegmentSPI != 0)
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

            // Update _curPrimaryKey from both system tables
            UpdateCurPrimaryKey(maxPk);

            if (_fieldsTable.PrimaryKeyIndex.EntryCount > 0)
            {
                var fieldsMaxPk = _fieldsTable.PrimaryKeyIndex.GetMaxKey();
                UpdateCurPrimaryKey(fieldsMaxPk);
            }
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
        ref var header = ref page.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);
        header.NextFreeTSN = TransactionChain.NextFreeId;

        MMF.UnlatchPageExclusive(memPageIdx);

        cs.SaveChanges();
        MMF.FlushToDisk();
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

    public bool RegisterComponentFromAccessor<T>(ChangeSet changeSet = null) where T : unmanaged
    {
        // Look up persisted fields for the resolver (keyed by component schema name)
        FieldIdResolver resolver = null;
        var componentAttr = typeof(T).GetCustomAttribute<ComponentAttribute>();
        var schemaName = componentAttr?.Name ?? typeof(T).Name;

        if (_persistedFieldsByComponent != null && _persistedFieldsByComponent.TryGetValue(schemaName, out FieldR1[] persistedFields))
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
            // Load path: restore from saved SPIs
            componentTable = new ComponentTable(this, definition, this, persisted.Comp.ComponentSPI, persisted.Comp.VersionSPI, persisted.Comp.DefaultIndexSPI, 
                persisted.Comp.String64IndexSPI, persisted.Comp.TailIndexSPI);

            // Update _curPrimaryKey from loaded PK index
            if (componentTable.PrimaryKeyIndex.EntryCount > 0)
            {
                var maxPk = componentTable.PrimaryKeyIndex.GetMaxKey();
                UpdateCurPrimaryKey(maxPk);
            }

            // Persist schema changes if the resolver detected renames, additions, or removals
            if (resolver != null && resolver.HasChanges)
            {
                PersistSchemaChanges(persisted.PK, definition);
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
                SaveInSystemSchema(componentTable);
                cs.SaveChanges();
                MMF.FlushToDisk();
            }
        }

        _componentTableByType.TryAdd(typeof(T), componentTable);

        // Assign a stable WAL type ID derived from the component segment's persistent root page index
        var walTypeId = (ushort)componentTable.ComponentSegment.RootPageIndex;
        componentTable.WalTypeId = walTypeId;
        _componentTableByWalTypeId.TryAdd(walTypeId, componentTable);

        return true;
    }

    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    public ComponentTable GetComponentTable(Type type) => _componentTableByType.GetValueOrDefault(type);

    /// <summary>
    /// Looks up a <see cref="ComponentTable"/> by its WAL type ID (derived from <see cref="ChunkBasedSegment.RootPageIndex"/>).
    /// Returns null if the type ID is unknown.
    /// </summary>
    internal ComponentTable GetComponentTableByWalTypeId(ushort id) => _componentTableByWalTypeId.GetValueOrDefault(id);

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

    internal void LogDeferredUowNotFlushed(ushort uowId, int committedCount) =>
        _log?.LogWarning("Deferred UoW #{UowId} disposed with {Count} committed transaction(s) without Flush/FlushAsync. " +
                         "Data relies on engine shutdown safety net.", uowId, committedCount);

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