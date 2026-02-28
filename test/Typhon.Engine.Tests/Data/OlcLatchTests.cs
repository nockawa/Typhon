using NUnit.Framework;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.BPTree;

namespace Typhon.Engine.Tests;

[TestFixture]
public class OlcLatchTests
{
    // ========================================
    // OlcLatch basic operations
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void TryWriteLock_Unlocked_ReturnsTrue()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        Assert.That(latch.TryWriteLock(), Is.True);
        Assert.That(latch.IsLocked, Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void TryWriteLock_AlreadyLocked_ReturnsFalse()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        latch.TryWriteLock();

        // Second lock attempt should fail
        var latch2 = new OlcLatch(ref version);
        Assert.That(latch2.TryWriteLock(), Is.False);
    }

    [Test]
    [CancelAfter(1000)]
    public void WriteUnlock_IncrementsVersion()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        // Lock and unlock should increment version
        Assert.That(latch.TryWriteLock(), Is.True);
        latch.WriteUnlock();
        Assert.That(latch.IsLocked, Is.False);

        // Version should now be 4 (1 << 2, since version is in bits 2-31)
        Assert.That(version, Is.EqualTo(4));
    }

    [Test]
    [CancelAfter(1000)]
    public void WriteUnlock_MultipleCycles_VersionIncrements()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        for (int i = 0; i < 10; i++)
        {
            Assert.That(latch.TryWriteLock(), Is.True);
            latch.WriteUnlock();
        }

        // 10 lock/unlock cycles, each increments version by 1 in bits 2-31
        // So value should be 10 << 2 = 40
        Assert.That(version, Is.EqualTo(40));
    }

    [Test]
    [CancelAfter(1000)]
    public void ReadVersion_Unlocked_ReturnsVersion()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        // Initial version: 0, not locked, not obsolete → returns 0
        var v = latch.ReadVersion();
        Assert.That(v, Is.EqualTo(0));

        // Lock, unlock → version increments
        latch.TryWriteLock();
        latch.WriteUnlock();

        var v2 = latch.ReadVersion();
        Assert.That(v2, Is.EqualTo(4)); // version 1 << 2 = 4
    }

    [Test]
    [CancelAfter(1000)]
    public void ReadVersion_Locked_ReturnsZero()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        latch.TryWriteLock();

        // While locked, ReadVersion should return 0 (signal to restart)
        var latch2 = new OlcLatch(ref version);
        Assert.That(latch2.ReadVersion(), Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void ReadVersion_Obsolete_ReturnsZero()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        latch.TryWriteLock();
        latch.MarkObsolete();
        latch.WriteUnlock();

        // Obsolete bit is set → ReadVersion returns 0
        var latch2 = new OlcLatch(ref version);
        Assert.That(latch2.ReadVersion(), Is.EqualTo(0));
        Assert.That(latch2.IsObsolete, Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void ValidateVersion_Unchanged_ReturnsTrue()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        // Do a lock/unlock cycle to get a non-zero version
        latch.TryWriteLock();
        latch.WriteUnlock();

        var snapshot = latch.ReadVersion();
        Assert.That(latch.ValidateVersion(snapshot), Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void ValidateVersion_Changed_ReturnsFalse()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        latch.TryWriteLock();
        latch.WriteUnlock();

        var snapshot = latch.ReadVersion();

        // Another write cycle changes the version
        latch.TryWriteLock();
        latch.WriteUnlock();

        Assert.That(latch.ValidateVersion(snapshot), Is.False);
    }

    [Test]
    [CancelAfter(1000)]
    public void MarkObsolete_PreservesLockAndVersion()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        latch.TryWriteLock();
        latch.MarkObsolete();

        Assert.That(latch.IsLocked, Is.True);
        Assert.That(latch.IsObsolete, Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void WriteUnlock_PreservesObsolete()
    {
        int version = 0;
        var latch = new OlcLatch(ref version);

        latch.TryWriteLock();
        latch.MarkObsolete();
        latch.WriteUnlock();

        // After unlock: lock cleared, obsolete preserved, version incremented
        Assert.That(latch.IsLocked, Is.False);
        Assert.That(latch.IsObsolete, Is.True);

        // Version bits (2-31) should be 1 (one increment), obsolete bit (1) set
        Assert.That(version, Is.EqualTo(0b110)); // version=1 << 2 | obsolete=0b10 = 6
    }

    // ========================================
    // Concurrent OlcLatch test
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_ReadersAndWriter_VersionConsistency()
    {
        int version = 0;
        var writerDone = 0;
        const int writeIterations = 10_000;
        var readValidations = 0;
        var readRestarts = 0;
        using var startBarrier = new ManualResetEventSlim(false);

        // Writer: lock, do work, unlock — incrementing version each time
        var writerTask = Task.Run(() =>
        {
            startBarrier.Wait();
            for (int i = 0; i < writeIterations; i++)
            {
                var latch = new OlcLatch(ref version);
                while (!latch.TryWriteLock())
                {
                    Thread.SpinWait(1);
                }
                // Simulate brief work
                Thread.SpinWait(50);
                latch.WriteUnlock();
            }
            Interlocked.Exchange(ref writerDone, 1);
        });

        // Readers: read version, validate it hasn't changed
        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            startBarrier.Wait();
            while (Volatile.Read(ref writerDone) == 0)
            {
                var latch = new OlcLatch(ref version);
                var v = latch.ReadVersion();
                if (v == 0)
                {
                    // Locked or obsolete — restart
                    Interlocked.Increment(ref readRestarts);
                    continue;
                }

                // Simulate brief read work
                Thread.SpinWait(5);

                // Validate the version hasn't changed
                if (latch.ValidateVersion(v))
                {
                    Interlocked.Increment(ref readValidations);
                }
                else
                {
                    Interlocked.Increment(ref readRestarts);
                }
            }
        })).ToArray();

        // Release all threads simultaneously
        startBarrier.Set();

        Task.WaitAll([writerTask, .. readerTasks]);

        // After all writes, version should be writeIterations << 2
        Assert.That(version, Is.EqualTo(writeIterations << 2));
        Assert.That(readValidations + readRestarts, Is.GreaterThan(0), "Readers should have observed the writer");
    }

    // ========================================
    // Chunk size verification
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public unsafe void Index32Chunk_Size_Is256Bytes()
    {
        Assert.That(sizeof(Index32Chunk), Is.EqualTo(256));
    }

    [Test]
    [CancelAfter(1000)]
    public unsafe void Index16Chunk_Size_Is256Bytes()
    {
        Assert.That(sizeof(Index16Chunk), Is.EqualTo(256));
    }

    [Test]
    [CancelAfter(1000)]
    public unsafe void Index64Chunk_Size_Is256Bytes()
    {
        Assert.That(sizeof(Index64Chunk), Is.EqualTo(256));
    }

    [Test]
    [CancelAfter(1000)]
    public void Index32Chunk_Capacity_Is29()
    {
        Assert.That(Index32Chunk.Capacity, Is.EqualTo(29));
    }

    [Test]
    [CancelAfter(1000)]
    public void PaddedEpochSlot_Size_Is64Bytes()
    {
        Assert.That(Unsafe.SizeOf<PaddedEpochSlot>(), Is.EqualTo(64));
    }
}
