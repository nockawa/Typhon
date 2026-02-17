using NUnit.Framework;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Multi-producer stress tests for <see cref="WalWriter"/> verifying concurrent drain correctness.
/// </summary>
[TestFixture]
public class WalWriterConcurrencyTests : AllocatorTestBase
{
    private const int TestCapacity = 256 * 1024; // 256KB for concurrency

    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_conc_test_{Guid.NewGuid():N}");
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

    private (WalCommitBuffer buffer, WalWriter writer, WalSegmentManager segMgr) CreateWriterPipeline()
    {
        var buffer = new WalCommitBuffer(MemoryAllocator, AllocationResource, TestCapacity);

        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 2,
            SegmentSize = 4 * 1024 * 1024, // 4MB for concurrency tests
            PreAllocateSegments = 2,
            StagingBufferSize = 16384, // 16KB staging
            UseFUA = false,
        };

        var segMgr = new WalSegmentManager(_fileIO, _walDir, options.SegmentSize, options.PreAllocateSegments, false);
        segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var writer = new WalWriter(buffer, segMgr, _fileIO, options, MemoryAllocator, AllocationResource);

        return (buffer, writer, segMgr);
    }

    [Test]
    [CancelAfter(5000)]
    public void MultipleProducers_AllRecordsDrained()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            const int producerCount = 4;
            const int recordsPerProducer = 50;
            var totalRecords = producerCount * recordsPerProducer;
            var barrier = new Barrier(producerCount);
            var exceptions = new Exception[producerCount];

            var threads = new Thread[producerCount];
            for (int p = 0; p < producerCount; p++)
            {
                var producerIdx = p;
                threads[p] = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        for (int i = 0; i < recordsPerProducer; i++)
                        {
                            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
                            var claim = buffer.TryClaim(64, 1, ref ctx);
                            if (claim.IsValid)
                            {
                                claim.DataSpan.Fill((byte)(producerIdx + 1));
                                buffer.Publish(ref claim);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions[producerIdx] = ex;
                    }
                });
                threads[p].Start();
            }

            // Wait for all producers to finish
            foreach (var t in threads)
            {
                t.Join(TimeSpan.FromSeconds(3));
            }

            // Check for producer exceptions
            foreach (var ex in exceptions)
            {
                Assert.That(ex, Is.Null, $"Producer exception: {ex?.Message}");
            }

            // Wait for writer to drain everything
            SpinWait.SpinUntil(() => writer.DurableLsn >= totalRecords, 2000);

            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(totalRecords));
            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.HasFatalError, Is.False);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void RapidProduction_WriterKeepsUp()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Rapid-fire production from a single thread
            const int totalFrames = 200;
            for (int i = 0; i < totalFrames; i++)
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
                var claim = buffer.TryClaim(32, 1, ref ctx);
                if (claim.IsValid)
                {
                    claim.DataSpan.Fill((byte)(i & 0xFF));
                    buffer.Publish(ref claim);
                }
            }

            // Wait for drain
            SpinWait.SpinUntil(() => writer.DurableLsn >= totalFrames, 2000);

            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(totalFrames));
            Assert.That(writer.HasFatalError, Is.False);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void LsnOrdering_MonotonicallyIncreasing()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            long prevLsn = 0;
            const int iterations = 100;

            for (int i = 0; i < iterations; i++)
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
                var claim = buffer.TryClaim(64, 1, ref ctx);
                if (claim.IsValid)
                {
                    // Verify LSN is monotonically increasing
                    Assert.That(claim.FirstLSN, Is.GreaterThan(prevLsn));
                    prevLsn = claim.FirstLSN;

                    claim.DataSpan.Fill(0xEE);
                    buffer.Publish(ref claim);
                }
            }

            // Wait for drain
            SpinWait.SpinUntil(() => writer.DurableLsn >= iterations, 2000);

            // DurableLsn should be at or near the last assigned LSN
            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(iterations));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }
}
