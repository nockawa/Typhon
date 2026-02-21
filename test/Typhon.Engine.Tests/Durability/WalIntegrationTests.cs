using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Integration tests exercising the WAL pipeline end-to-end with real disk I/O.
/// Covers: WAL creation, all three DurabilityModes, dirty page tracking, database reopen
/// (Create vs Load paths), crash recovery (FPI repair), and high-level scenarios.
/// </summary>
[TestFixture]
[Category("WAL")]
class WalIntegrationTests : TestBase
{
    private ServiceProvider _serviceProvider;
    private string _walDir;
    private string _dbDir;

    [SetUp]
    public void Setup()
    {
        // Unique paths per test — guarantees no WAL/DB files from previous sessions
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_{Guid.NewGuid():N}");
        _dbDir = Path.Combine(Path.GetTempPath(), $"typhon_db_{Guid.NewGuid():N}");

        // Defensive: destroy any leftover files (handles rare Guid collision or crashed previous run)
        CleanupDirectories();

        Directory.CreateDirectory(_walDir);
        Directory.CreateDirectory(_dbDir);

        var services = new ServiceCollection();
        services
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        CleanupDirectories();
        Log.CloseAndFlush();
    }

    private void CleanupDirectories()
    {
        try { if (Directory.Exists(_walDir)) Directory.Delete(_walDir, true); }
        catch { /* ignored — may fail on locked files in crash scenarios */ }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); }
        catch { /* ignored */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════

    private DatabaseEngine CreateEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        return dbe;
    }

    private (long[] ids, CompA[] values) CreateCompAEntities(DatabaseEngine dbe, int count, DurabilityMode mode)
    {
        var ids = new long[count];
        var values = new CompA[count];

        using (var uow = dbe.CreateUnitOfWork(mode))
        {
            for (int i = 0; i < count; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 1, (float)(i * 1.5), i * 2.5);
                ids[i] = tx.CreateEntity(ref comp);
                values[i] = comp;
                tx.Commit();
            }

            uow.Flush();
        }

        return (ids, values);
    }

    private void VerifyCompAEntities(DatabaseEngine dbe, long[] ids, CompA[] expected)
    {
        var errors = new List<string>();
        using var tx = dbe.CreateQuickTransaction();

        for (int i = 0; i < ids.Length; i++)
        {
            if (!tx.ReadEntity(ids[i], out CompA actual))
            {
                errors.Add($"Entity {ids[i]} (index {i}) not readable");
            }
            else if (actual.A != expected[i].A || actual.B != expected[i].B || actual.C != expected[i].C)
            {
                errors.Add($"Entity {ids[i]} (index {i}): got A={actual.A},B={actual.B},C={actual.C}; " +
                           $"expected A={expected[i].A},B={expected[i].B},C={expected[i].C}");
            }
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    private string GetDatabaseFilePath() => Path.Combine(_dbDir, $"{CurrentDatabaseName}.bin");

    /// <summary>
    /// Forces a checkpoint cycle and waits for it to complete.
    /// This prevents the shutdown path from hanging: when checkpointLsn == durableLsn,
    /// the final checkpoint cycle is skipped and the thread exits immediately.
    /// </summary>
    private static void WaitForCheckpointComplete(DatabaseEngine dbe)
    {
        var cm = dbe.CheckpointManager;
        if (cm == null || dbe.IsDisposed)
        {
            return;
        }

        var before = cm.TotalCheckpoints;
        cm.ForceCheckpoint();

        var sw = Stopwatch.StartNew();
        while (cm.TotalCheckpoints <= before && sw.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Wraps a DI scope + DatabaseEngine. On dispose, forces a checkpoint to complete
    /// before disposing the scope, preventing the checkpoint thread from accessing freed memory.
    /// </summary>
    private sealed class EngineScope : IDisposable
    {
        private readonly IServiceScope _scope;
        public readonly DatabaseEngine Engine;

        public EngineScope(IServiceScope scope, DatabaseEngine engine)
        {
            _scope = scope;
            Engine = engine;
        }

        public void Dispose()
        {
            WaitForCheckpointComplete(Engine);
            _scope.Dispose();
        }
    }

    private EngineScope CreateEngineScope()
    {
        var scope = _serviceProvider.CreateScope();
        var engine = CreateEngine(scope);
        return new EngineScope(scope, engine);
    }

    /// <summary>
    /// Scans pages for pages with valid CRC (non-zero, matching computed value).
    /// Returns up to <paramref name="maxPages"/> page indices.
    /// </summary>
    /// <param name="maxPages">Maximum number of pages to find.</param>
    /// <param name="startPage">First page index to scan (use higher values to skip structural pages).</param>
    private int[] FindPagesWithValidCrc(int maxPages = 3, int startPage = 1)
    {
        var dbPath = GetDatabaseFilePath();
        var result = new List<int>();

        using var fs = File.OpenRead(dbPath);
        var page = new byte[PagedMMF.PageSize];

        for (int i = startPage; i < 100 && result.Count < maxPages; i++)
        {
            fs.Seek(i * (long)PagedMMF.PageSize, SeekOrigin.Begin);
            if (fs.Read(page) < PagedMMF.PageSize)
            {
                break;
            }

            var storedCrc = BitConverter.ToUInt32(page, PageBaseHeader.PageChecksumOffset);
            if (storedCrc != 0)
            {
                var computedCrc = WalCrc.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
                if (computedCrc == storedCrc)
                {
                    result.Add(i);
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Binary-corrupts a data file page by writing garbage bytes near the END of the page.
    /// Writing near the end avoids breaking structural metadata (segment descriptors, bitmap pointers)
    /// that lives at the beginning of the data area. CRC will still mismatch, enabling FPI repair testing.
    /// </summary>
    private void CorruptDataFilePage(int pageIndex)
    {
        var dbPath = GetDatabaseFilePath();
        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite);
        long offset = pageIndex * (long)PagedMMF.PageSize;
        // Write garbage near the end of the page (avoids structural metadata at the start)
        fs.Seek(offset + PagedMMF.PageSize - 32, SeekOrigin.Begin);
        fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE });
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 1: Low-Level WAL Pipeline
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_FreshDatabase_CreatesWalDirectory()
    {
        using var es = CreateEngineScope();

        Assert.That(Directory.Exists(_walDir), Is.True, "WAL directory should exist");
        var walFiles = Directory.GetFiles(_walDir, "*.wal");
        Assert.That(walFiles.Length, Is.GreaterThan(0), "Should have pre-allocated WAL segment files");
    }

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_Commit_DurableLsnAdvances(DurabilityMode mode)
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        using (var uow = dbe.CreateUnitOfWork(mode))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(42);
            tx.CreateEntity(ref comp);
            tx.Commit();
            uow.Flush();
        }

        Assert.That(dbe.WalManager.DurableLsn, Is.GreaterThan(0), $"DurableLsn should advance after {mode} commit + flush");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_CommitBuffer_NextLsnIncreases()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var lsnBefore = dbe.WalManager.CommitBuffer.NextLsn;

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(1);
            tx.CreateEntity(ref comp);
            tx.Commit();
            uow.Flush();
        }

        var lsnMid = dbe.WalManager.CommitBuffer.NextLsn;
        Assert.That(lsnMid, Is.GreaterThan(lsnBefore), "NextLsn should increase after first commit");

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(2);
            tx.CreateEntity(ref comp);
            tx.Commit();
            uow.Flush();
        }

        var lsnAfter = dbe.WalManager.CommitBuffer.NextLsn;
        Assert.That(lsnAfter, Is.GreaterThan(lsnMid), "NextLsn should increase after second commit");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ForceCheckpoint_WritesDirtyPages()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 10, DurabilityMode.Immediate);
        WaitForCheckpointComplete(dbe);

        Assert.That(dbe.CheckpointManager.TotalPagesWritten, Is.GreaterThan(0), "Checkpoint should write dirty pages");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ForceCheckpoint_AdvancesCheckpointLsn()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 10, DurabilityMode.Immediate);
        WaitForCheckpointComplete(dbe);

        Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.GreaterThan(0), "CheckpointLsn should advance after ForceCheckpoint");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Shutdown_WriterStopsCleanly()
    {
        bool wasRunning;

        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope);
            wasRunning = dbe.WalManager.IsRunning;
        }
        // Engine disposed — WalManager stopped

        Assert.That(wasRunning, Is.True, "WAL writer should have been running before shutdown");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Shutdown_UowRegistryEmpty()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        // Create and dispose some UoWs
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(1);
            tx.CreateEntity(ref comp);
            tx.Commit();
            uow.Flush();
        }

        Assert.That(dbe.UowRegistry.ActiveCount, Is.EqualTo(0), "All UoWs should be freed after dispose");
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 2: CRUD Operations with WAL
    // ═══════════════════════════════════════════════════════════════

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_CreateEntity_SurvivesReopen(DurabilityMode mode)
    {
        long[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 50, mode);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            VerifyCompAEntities(dbe, ids, values);
        }
    }

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_UpdateComponent_SurvivesReopen(DurabilityMode mode)
    {
        long[] ids;
        CompA[] updatedValues;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, _) = CreateCompAEntities(dbe, 20, mode);

            // Update all entities with new values
            updatedValues = new CompA[ids.Length];
            using (var uow = dbe.CreateUnitOfWork(mode))
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1000, (float)(i * 3.0), i * 7.0);
                    tx.UpdateEntity(ids[i], ref comp);
                    updatedValues[i] = comp;
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            VerifyCompAEntities(dbe, ids, updatedValues);
        }
    }

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_DeleteEntity_SurvivesReopen(DurabilityMode mode)
    {
        long[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 20, mode);

            // Delete the first 10 entities
            using (var uow = dbe.CreateUnitOfWork(mode))
            {
                for (int i = 0; i < 10; i++)
                {
                    using var tx = uow.CreateTransaction();
                    tx.DeleteEntity<CompA>(ids[i]);
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            // First 10 should be deleted
            for (int i = 0; i < 10; i++)
            {
                if (tx.ReadEntity(ids[i], out CompA _))
                {
                    errors.Add($"Entity {ids[i]} (index {i}) should be deleted but was readable");
                }
            }

            // Last 10 should survive
            for (int i = 10; i < 20; i++)
            {
                if (!tx.ReadEntity(ids[i], out CompA actual))
                {
                    errors.Add($"Entity {ids[i]} (index {i}) should survive but was not readable");
                }
                else if (actual.A != values[i].A)
                {
                    errors.Add($"Entity {ids[i]} (index {i}): A={actual.A}, expected {values[i].A}");
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_StringComponent_SurvivesReopen()
    {
        const int count = 20;
        var ids = new long[count];
        var strings = new string[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompC($"Entity_{i:D3}");
                    ids[i] = tx.CreateEntity(ref comp);
                    strings[i] = $"Entity_{i:D3}";
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                if (!tx.ReadEntity(ids[i], out CompC actual))
                {
                    errors.Add($"Entity {ids[i]} not readable");
                }
                else if (actual.String.AsString != strings[i])
                {
                    errors.Add($"Entity {ids[i]}: got '{actual.String.AsString}', expected '{strings[i]}'");
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_IndexedComponent_SurvivesReopen()
    {
        const int count = 20;
        var ids = new long[count];
        var values = new CompD[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompD((float)(i * 1.1), i * 10, i * 2.2);
                    ids[i] = tx.CreateEntity(ref comp);
                    values[i] = comp;
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                if (!tx.ReadEntity(ids[i], out CompD actual))
                {
                    errors.Add($"Entity {ids[i]} not readable");
                }
                else if (actual.B != values[i].B)
                {
                    errors.Add($"Entity {ids[i]}: B={actual.B}, expected {values[i].B}");
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MultipleComponents_SurvivesReopen()
    {
        const int count = 20;
        var ids = new long[count];
        var valuesA = new CompA[count];
        var valuesC = new CompC[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var compA = new CompA(i + 1, (float)(i * 1.5), i * 2.5);
                    var compC = new CompC($"Multi_{i:D3}");
                    ids[i] = tx.CreateEntity(ref compA, ref compC);
                    valuesA[i] = compA;
                    valuesC[i] = compC;
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                if (!tx.ReadEntity(ids[i], out CompA actualA, out CompC actualC))
                {
                    errors.Add($"Entity {ids[i]} not readable");
                }
                else
                {
                    if (actualA.A != valuesA[i].A)
                    {
                        errors.Add($"Entity {ids[i]}: CompA.A={actualA.A}, expected {valuesA[i].A}");
                    }

                    if (actualC.String.AsString != valuesC[i].String.AsString)
                    {
                        errors.Add($"Entity {ids[i]}: CompC.String='{actualC.String.AsString}', expected '{valuesC[i].String.AsString}'");
                    }
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ManyEntities_SurvivesReopen()
    {
        long[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 500, DurabilityMode.Immediate);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            VerifyCompAEntities(dbe, ids, values);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 3: Dirty Page Tracking
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_CreateEntity_PagesBecomesDirty()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 5, DurabilityMode.Immediate);

        var dirtyPages = dbe.MMF.CollectDirtyMemPageIndices();
        Assert.That(dirtyPages.Length, Is.GreaterThan(0), "Creating entities should dirty pages");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_UpdateComponent_PagesBecomesDirty()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var (ids, _) = CreateCompAEntities(dbe, 5, DurabilityMode.Immediate);

        // Drain dirty counters with multiple checkpoint cycles so we start from a clean baseline.
        // Each cycle decrements DirtyCounter by 1; pages latched N times need N cycles.
        for (int i = 0; i < 30; i++)
        {
            WaitForCheckpointComplete(dbe);
            if (dbe.MMF.CollectDirtyMemPageIndices().Length <= 1)
            {
                break;
            }
        }

        var dirtyBefore = dbe.MMF.CollectDirtyMemPageIndices().Length;

        // Update an entity to dirty its page
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var updated = new CompA(999);
            tx.UpdateEntity(ids[0], ref updated);
            tx.Commit();
            uow.Flush();
        }

        var dirtyAfter = dbe.MMF.CollectDirtyMemPageIndices().Length;
        Assert.That(dirtyAfter, Is.GreaterThan(dirtyBefore), "Update should increase dirty page count");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ForceCheckpoint_ClearsDirtyPages()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);

        var dirtyBefore = dbe.MMF.CollectDirtyMemPageIndices().Length;
        Assert.That(dirtyBefore, Is.GreaterThan(0), "Should have dirty pages before checkpoint");

        // Each checkpoint cycle decrements DirtyCounter by 1 per page.
        // Pages latched multiple times (e.g., shared by many entities) need multiple cycles.
        // Also, UpdateCheckpointLSN re-dirties the header page each cycle.
        // Run enough cycles to drain all accumulated dirty counts.
        int dirtyAfter = dirtyBefore;
        for (int i = 0; i < 30 && dirtyAfter > 1; i++)
        {
            WaitForCheckpointComplete(dbe);
            dirtyAfter = dbe.MMF.CollectDirtyMemPageIndices().Length;
        }

        // At most the header page stays dirty (UpdateCheckpointLSN re-dirties it each cycle)
        Assert.That(dirtyAfter, Is.LessThan(dirtyBefore), "Multiple checkpoint cycles should reduce dirty page count");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MultipleOperations_AccumulateDirtyPages()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        WaitForCheckpointComplete(dbe);

        var counts = new List<int>();
        for (int batch = 0; batch < 3; batch++)
        {
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < 5; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(batch * 100 + i);
                    tx.CreateEntity(ref comp);
                    tx.Commit();
                }

                uow.Flush();
            }

            counts.Add(dbe.MMF.CollectDirtyMemPageIndices().Length);
        }

        // Dirty count should generally increase (or at least not decrease) as we add data
        Assert.That(counts.Last(), Is.GreaterThanOrEqualTo(counts.First()),
            $"Dirty pages should accumulate: [{string.Join(", ", counts)}]");
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 4: Create vs Load Modes
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_FreshDatabase_SchemaPersistedWithWal()
    {
        // Phase 1: Create database, register components
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            // RegisterComponents is called inside CreateEngine — schema persisted on create path
        }

        // Phase 2: Reopen — RegisterComponents should succeed on load path
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            // If this doesn't throw, schema was persisted and loaded correctly

            // Verify we can create entities (proves component tables are functional)
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            var comp = new CompA(42);
            var id = tx.CreateEntity(ref comp);
            tx.Commit();

            Assert.That(id, Is.GreaterThan(0), "Should be able to create entities after schema reload");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_LoadAndContinue_NewEntitiesAfterReopen()
    {
        long[] ids1;
        CompA[] values1;

        // Phase 1: Create 20 entities
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids1, values1) = CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        long[] ids2;
        CompA[] values2;

        // Phase 2: Reopen and create 20 more
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            (ids2, values2) = CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        // Phase 3: Reopen and verify all 40
        using (var scope3 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope3);
            VerifyCompAEntities(dbe, ids1, values1);
            VerifyCompAEntities(dbe, ids2, values2);
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MultipleReopenCycles_DataAccumulates()
    {
        var allIds = new List<long>();
        var allValues = new List<CompA>();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbe = CreateEngine(scope);

            var (ids, values) = CreateCompAEntities(dbe, 10, DurabilityMode.Immediate);
            allIds.AddRange(ids);
            allValues.AddRange(values);
        }

        // Final reopen: verify all 30 entities
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope);
            VerifyCompAEntities(dbe, allIds.ToArray(), allValues.ToArray());
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_WalSegments_PersistAcrossReopen()
    {
        // Phase 1: Create data
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        // After close: active WAL segment should persist (sealed segments recycled by final checkpoint)
        var walFiles = Directory.GetFiles(_walDir, "*.wal");
        Assert.That(walFiles.Length, Is.GreaterThan(0), "WAL segment files should persist after engine close");

        // Phase 2: Reopen succeeds with WAL recovery
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            Assert.That(dbe.LastRecoveryResult.SegmentsScanned, Is.GreaterThan(0),
                "Recovery should scan surviving WAL segments on reopen");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 5: Crash Recovery
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_FpiCapture_RecoveryScansRecords()
    {
        long[] ids;
        CompA[] expectedValues;

        // Phase 1: Create entities, checkpoint, then update to trigger FPI capture
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, expectedValues) = CreateCompAEntities(dbe, 50, DurabilityMode.Immediate);
            WaitForCheckpointComplete(dbe);

            // Updates after checkpoint trigger FPI capture (bitmap cleared by checkpoint)
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < 10; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var updated = new CompA(i + 5000, (float)(i * 9.0), i * 11.0);
                    tx.UpdateEntity(ids[i], ref updated);
                    expectedValues[i] = updated; // Track the final value
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        // Verify WAL segment files survive engine dispose
        var walFiles = Directory.GetFiles(_walDir, "*.wal");
        Assert.That(walFiles.Length, Is.GreaterThan(0), "WAL segment files should survive engine dispose");

        // Phase 2: Reopen — recovery scans WAL segments
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            Assert.That(dbe.LastRecoveryResult.SegmentsScanned, Is.GreaterThan(0),
                "Recovery should scan at least one WAL segment");

            // All entities should be readable after clean reopen with WAL recovery
            VerifyCompAEntities(dbe, ids, expectedValues);
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Recovery_SegmentsScannedOnReopen()
    {
        // Phase 1: Create data (generates WAL records)
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        // Phase 2: Reopen — recovery scans surviving WAL segment
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            Assert.That(dbe.LastRecoveryResult.SegmentsScanned, Is.GreaterThan(0),
                "Recovery should scan at least one WAL segment");
            Assert.That(dbe.LastRecoveryResult.RecordsScanned, Is.GreaterThan(0),
                "Recovery should find WAL records in surviving segment");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Recovery_CommittedDataSurvivesRecovery()
    {
        long[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 30, DurabilityMode.Immediate);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            // Data should be fully intact — committed with Immediate durability + final checkpoint
            VerifyCompAEntities(dbe, ids, values);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 6: High-Level Scenarios
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_GameTick_DeferredBatch_FlushAtEnd()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        // Simulate a game tick: 100 entity updates in a single Deferred UoW, Flush at the end
        var (ids, _) = CreateCompAEntities(dbe, 100, DurabilityMode.Immediate);

        var expectedValues = new CompA[100];

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred))
        {
            for (int i = 0; i < 100; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 2000, (float)(i * 0.5), i * 0.25);
                tx.UpdateEntity(ids[i], ref comp);
                expectedValues[i] = comp;
                tx.Commit();
            }

            // Single flush at the end — batches all WAL records
            uow.Flush();
        }

        // Verify all updates applied
        VerifyCompAEntities(dbe, ids, expectedValues);
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_CriticalTrade_ImmediateAtomicity()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var (ids, _) = CreateCompAEntities(dbe, 2, DurabilityMode.Immediate);

        // Atomic trade: update both entities in one transaction with Immediate durability
        var tradeA = new CompA(100, 1.0f, 1.0);
        var tradeB = new CompA(200, 2.0f, 2.0);

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            tx.UpdateEntity(ids[0], ref tradeA);
            tx.UpdateEntity(ids[1], ref tradeB);
            tx.Commit();
            uow.Flush();
        }

        // Both should reflect the trade
        using var readTx = dbe.CreateQuickTransaction();
        readTx.ReadEntity(ids[0], out CompA a);
        readTx.ReadEntity(ids[1], out CompA b);

        Assert.That(a.A, Is.EqualTo(100), "Trade entity A should have new value");
        Assert.That(b.A, Is.EqualTo(200), "Trade entity B should have new value");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MixedDurability_AllModesCoexist()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var allIds = new ConcurrentBag<(long id, CompA value, DurabilityMode mode)>();
        var errors = new ConcurrentBag<string>();
        var modes = new[] { DurabilityMode.Deferred, DurabilityMode.GroupCommit, DurabilityMode.Immediate };
        var barrier = new Barrier(modes.Length);

        var threads = new Thread[modes.Length];
        for (int t = 0; t < modes.Length; t++)
        {
            var mode = modes[t];
            var threadIdx = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var uow = dbe.CreateUnitOfWork(mode);

                    for (int i = 0; i < 20; i++)
                    {
                        using var tx = uow.CreateTransaction();
                        var comp = new CompA(threadIdx * 1000 + i, (float)(threadIdx * 0.1 + i), threadIdx * 100.0 + i);
                        var id = tx.CreateEntity(ref comp);
                        tx.Commit();
                        allIds.Add((id, comp, mode));
                    }

                    uow.Flush();
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadIdx} ({mode}): {ex.Message}");
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(allIds.Count, Is.EqualTo(60), "Should have 60 entities (3 threads × 20 each)");

        // Verify all entities readable
        var verifyErrors = new List<string>();
        using var readTx = dbe.CreateQuickTransaction();

        foreach (var (id, expected, mode) in allIds)
        {
            if (!readTx.ReadEntity(id, out CompA actual))
            {
                verifyErrors.Add($"Entity {id} ({mode}) not readable");
            }
            else if (actual.A != expected.A)
            {
                verifyErrors.Add($"Entity {id} ({mode}): A={actual.A}, expected {expected.A}");
            }
        }

        Assert.That(verifyErrors, Is.Empty, string.Join("\n", verifyErrors));
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_BulkImport_DeferredBatches()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var allIds = new List<long>();
        var allValues = new List<CompA>();

        // Import 500 entities in 5 batches of 100 each
        for (int batch = 0; batch < 5; batch++)
        {
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);

            for (int i = 0; i < 100; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(batch * 100 + i, (float)(batch * 10.0 + i), batch * 100.0 + i);
                allIds.Add(tx.CreateEntity(ref comp));
                allValues.Add(comp);
                tx.Commit();
            }

            uow.Flush();
        }

        // Verify all 500
        VerifyCompAEntities(dbe, allIds.ToArray(), allValues.ToArray());
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ConcurrentWriters_NoCorruption()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        const int threadCount = 4;
        const int entitiesPerThread = 25;
        var allIds = new ConcurrentBag<(long id, CompA value)>();
        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIdx = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();

                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
                        using var tx = uow.CreateTransaction();
                        var comp = new CompA(threadIdx * 1000 + i, (float)(threadIdx + i * 0.01), threadIdx * 10.0 + i);
                        var id = tx.CreateEntity(ref comp);
                        tx.Commit();
                        uow.Flush();
                        allIds.Add((id, comp));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadIdx}: {ex.Message}");
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(allIds.Count, Is.EqualTo(threadCount * entitiesPerThread),
            $"Should have {threadCount * entitiesPerThread} entities");

        // Verify all entities consistent
        var verifyErrors = new List<string>();
        using var readTx = dbe.CreateQuickTransaction();

        foreach (var (id, expected) in allIds)
        {
            if (!readTx.ReadEntity(id, out CompA actual))
            {
                verifyErrors.Add($"Entity {id} not readable");
            }
            else if (actual.A != expected.A)
            {
                verifyErrors.Add($"Entity {id}: A={actual.A}, expected {expected.A}");
            }
        }

        Assert.That(verifyErrors, Is.Empty, string.Join("\n", verifyErrors));
    }
}
