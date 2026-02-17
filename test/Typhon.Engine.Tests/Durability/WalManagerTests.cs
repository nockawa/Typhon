using NUnit.Framework;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Integration tests for <see cref="WalManager"/> — the top-level WAL orchestrator.
/// Verifies end-to-end flow: produce → drain → write → durable LSN advance.
/// </summary>
[TestFixture]
public class WalManagerTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_mgr_test_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    private WalManager CreateManager(int commitBufferCapacity = 64 * 1024)
    {
        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 2,
            SegmentSize = 1024 * 1024, // 1MB
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };

        return new WalManager(options, MemoryAllocator, _fileIO, AllocationResource, commitBufferCapacity);
    }

    #region Lifecycle

    [Test]
    public void Constructor_CreatesCommitBuffer()
    {
        using var mgr = CreateManager();

        Assert.That(mgr.CommitBuffer, Is.Not.Null);
    }

    [Test]
    public void Initialize_SetsUpSegmentManager()
    {
        using var mgr = CreateManager();

        mgr.Initialize();

        Assert.That(Directory.Exists(_walDir), Is.True);
    }

    [Test]
    public void Initialize_DoubleCall_Throws()
    {
        using var mgr = CreateManager();
        mgr.Initialize();

        Assert.Throws<InvalidOperationException>(() => mgr.Initialize());
    }

    [Test]
    public void Start_WithoutInitialize_Throws()
    {
        using var mgr = CreateManager();

        Assert.Throws<InvalidOperationException>(() => mgr.Start());
    }

    [Test]
    public void Start_AfterInitialize_Succeeds()
    {
        using var mgr = CreateManager();
        mgr.Initialize();

        mgr.Start();
        SpinWait.SpinUntil(() => mgr.IsRunning, 2000);

        Assert.That(mgr.IsRunning, Is.True);
    }

    #endregion

    #region End-to-End

    [Test]
    [CancelAfter(5000)]
    public void EndToEnd_ProduceDrainVerify()
    {
        using var mgr = CreateManager();
        mgr.Initialize();
        mgr.Start();

        // Produce records through the commit buffer
        var buffer = mgr.CommitBuffer;
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        var claim = buffer.TryClaim(64, 1, ref ctx);
        Assert.That(claim.IsValid, Is.True);

        claim.DataSpan.Fill(0xAA);
        buffer.Publish(ref claim);

        // Wait for writer to drain
        SpinWait.SpinUntil(() => mgr.DurableLsn > 0, 2000);

        Assert.That(mgr.DurableLsn, Is.GreaterThan(0));
        Assert.That(mgr.HasFatalError, Is.False);
    }

    [Test]
    [CancelAfter(5000)]
    public void EndToEnd_MultipleRecords()
    {
        using var mgr = CreateManager();
        mgr.Initialize();
        mgr.Start();

        var buffer = mgr.CommitBuffer;

        for (int i = 0; i < 10; i++)
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(128, 2, ref ctx);
            Assert.That(claim.IsValid, Is.True);

            claim.DataSpan.Fill((byte)(i + 1));
            buffer.Publish(ref claim);
        }

        // 10 frames × 2 records = 20 records → LSNs 1-20
        SpinWait.SpinUntil(() => mgr.DurableLsn >= 20, 2000);

        Assert.That(mgr.DurableLsn, Is.GreaterThanOrEqualTo(20));
    }

    [Test]
    [CancelAfter(5000)]
    public void WaitForDurable_BlocksUntilDurable()
    {
        using var mgr = CreateManager();
        mgr.Initialize();
        mgr.Start();

        var buffer = mgr.CommitBuffer;
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        var claim = buffer.TryClaim(64, 1, ref ctx);
        var targetLsn = claim.FirstLSN;
        claim.DataSpan.Fill(0xBB);
        buffer.Publish(ref claim);

        // Wait for this specific LSN to become durable
        var waitCtx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        mgr.WaitForDurable(targetLsn, ref waitCtx);

        Assert.That(mgr.DurableLsn, Is.GreaterThanOrEqualTo(targetLsn));
    }

    #endregion

    #region Properties

    [Test]
    public void InitialState_DefaultValues()
    {
        using var mgr = CreateManager();

        Assert.That(mgr.DurableLsn, Is.EqualTo(0));
        Assert.That(mgr.IsRunning, Is.False);
        Assert.That(mgr.HasFatalError, Is.False);
    }

    #endregion

    #region Dispose

    [Test]
    [CancelAfter(5000)]
    public void Dispose_StopsWriter()
    {
        var mgr = CreateManager();
        mgr.Initialize();
        mgr.Start();
        SpinWait.SpinUntil(() => mgr.IsRunning, 2000);

        mgr.Dispose(); // Internally joins the writer thread

        Assert.That(mgr.IsRunning, Is.False);
    }

    [Test]
    public void Dispose_Idempotent()
    {
        var mgr = CreateManager();
        mgr.Initialize();

        mgr.Dispose();
        Assert.DoesNotThrow(() => mgr.Dispose());
    }

    #endregion
}
