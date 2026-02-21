using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="StagingBufferPool"/> — pre-allocated pool of page-sized staging buffers.
/// </summary>
[TestFixture]
public class StagingBufferPoolTests : AllocatorTestBase
{
    #region Constructor

    [Test]
    public void Constructor_ValidCapacity_CreatesPool()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, 64);
        Assert.That(pool.PoolCapacity, Is.EqualTo(64));
    }

    [Test]
    public void Constructor_BelowMinCapacity_ClampsToMin()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, 1);
        Assert.That(pool.PoolCapacity, Is.EqualTo(StagingBufferPool.MinCapacity));
    }

    [Test]
    public void Constructor_AboveMaxCapacity_ClampsToMax()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, 10000);
        Assert.That(pool.PoolCapacity, Is.EqualTo(StagingBufferPool.MaxCapacity));
    }

    [Test]
    public void Constructor_DefaultCapacity_Uses512()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability);
        Assert.That(pool.PoolCapacity, Is.EqualTo(StagingBufferPool.DefaultCapacity));
    }

    [Test]
    public void Constructor_NullAllocator_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new StagingBufferPool(null, ResourceRegistry.Durability));

    [Test]
    public void Constructor_NullParent_Throws() =>
        Assert.That(() => new StagingBufferPool(MemoryAllocator, null), Throws.TypeOf<NullReferenceException>());

    #endregion

    #region Rent

    [Test]
    public void Rent_ReturnsValidBuffer()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, StagingBufferPool.MinCapacity);

        using var buffer = pool.Rent();

        Assert.That(buffer.IsValid, Is.True);
        Assert.That(buffer.Span.Length, Is.EqualTo(StagingBufferPool.BufferSize));
    }

    [Test]
    public unsafe void Rent_Pointer_Is4096ByteAligned()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, StagingBufferPool.MinCapacity);

        using var buffer = pool.Rent();

        Assert.That((long)buffer.Pointer % 4096, Is.EqualTo(0), "Buffer pointer should be 4096-byte aligned");
    }

    [Test]
    public void Rent_MultipleBuffers_NonOverlapping()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, StagingBufferPool.MinCapacity);

        using var buf1 = pool.Rent();
        using var buf2 = pool.Rent();

        // Write distinct patterns
        buf1.Span.Fill(0xAA);
        buf2.Span.Fill(0xBB);

        // Verify no overlap: buf1 should still be all 0xAA
        for (var i = 0; i < buf1.Span.Length; i++)
        {
            Assert.That(buf1.Span[i], Is.EqualTo(0xAA), $"buf1[{i}] was corrupted");
        }

        for (var i = 0; i < buf2.Span.Length; i++)
        {
            Assert.That(buf2.Span[i], Is.EqualTo(0xBB), $"buf2[{i}] was corrupted");
        }
    }

    #endregion

    #region Return

    [Test]
    public void Return_BufferCanBeReRented()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, StagingBufferPool.MinCapacity);

        // Rent and return
        var buf = pool.Rent();
        buf.Dispose();

        // Should succeed (slot was returned)
        using var buf2 = pool.Rent();
        Assert.That(buf2.IsValid, Is.True);
    }

    #endregion

    #region Exhaustion / Back-Pressure

    [Test]
    [CancelAfter(5000)]
    public void Rent_PoolExhausted_BlocksThenUnblocks()
    {
        const int capacity = StagingBufferPool.MinCapacity;
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, capacity);

        // Rent all buffers, track slot indices for manual return (ref struct can't go in List)
        var slotIndices = new int[capacity];
        for (var i = 0; i < capacity; i++)
        {
            var buf = pool.Rent();
            slotIndices[i] = buf.SlotIndex;
        }

        // Next rent should block; use a background thread
        using var rentCompleted = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            using var extra = pool.Rent();
            rentCompleted.Set();
        });
        thread.Start();

        // Verify it's blocked
        Assert.That(rentCompleted.Wait(200), Is.False, "Rent should be blocked when pool is exhausted");

        // Return one buffer to unblock
        pool.Return(slotIndices[0]);

        // Now the blocked rent should complete
        Assert.That(rentCompleted.Wait(2000), Is.True, "Rent should unblock after a buffer is returned");

        thread.Join();

        // Cleanup remaining
        for (var i = 1; i < capacity; i++)
        {
            pool.Return(slotIndices[i]);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Rent_CancellationToken_ThrowsOperationCanceled()
    {
        const int capacity = StagingBufferPool.MinCapacity;
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, capacity);

        // Exhaust the pool, track slot indices for manual return
        var slotIndices = new int[capacity];
        for (var i = 0; i < capacity; i++)
        {
            var buf = pool.Rent();
            slotIndices[i] = buf.SlotIndex;
        }

        using var cts = new CancellationTokenSource(100);

        Assert.Throws<OperationCanceledException>(() => pool.Rent(cts.Token));

        // Cleanup
        for (var i = 0; i < capacity; i++)
        {
            pool.Return(slotIndices[i]);
        }
    }

    #endregion

    #region Dispose

    [Test]
    public void Dispose_SubsequentRent_ThrowsObjectDisposed()
    {
        var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, StagingBufferPool.MinCapacity);
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
    }

    [Test]
    public void Dispose_DoubleDispose_IsSafe()
    {
        var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, StagingBufferPool.MinCapacity);
        pool.Dispose();

        Assert.DoesNotThrow(() => pool.Dispose());
    }

    #endregion

    #region Metrics / Debug Properties

    [Test]
    public void GetDebugProperties_ReflectsCorrectState()
    {
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, 32);

        // Rent two buffers
        var buf1 = pool.Rent();
        var buf2 = pool.Rent();

        var props = pool.GetDebugProperties();

        Assert.That(props["Pool.Capacity"], Is.EqualTo(32));
        Assert.That(props["Pool.BufferSize"], Is.EqualTo(StagingBufferPool.BufferSize));
        Assert.That(props["Rents.Current"], Is.EqualTo(2));
        Assert.That(props["Rents.Total"], Is.EqualTo(2L));
        Assert.That(props["IsDisposed"], Is.EqualTo(false));

        buf1.Dispose();
        buf2.Dispose();

        var propsAfter = pool.GetDebugProperties();
        Assert.That(propsAfter["Rents.Current"], Is.EqualTo(0));
        Assert.That(propsAfter["Rents.Total"], Is.EqualTo(2L));
    }

    #endregion

    #region Concurrency

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_RentReturn_NoCorruption()
    {
        const int threadCount = 4;
        const int iterations = 100;
        // Pool sized to force contention (fewer buffers than total concurrent demand)
        const int capacity = threadCount * 2;
        using var pool = new StagingBufferPool(MemoryAllocator, ResourceRegistry.Durability, capacity);
        using var barrier = new Barrier(threadCount);
        var errors = new int[1];

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    using var buf = pool.Rent();
                    if (!buf.IsValid || buf.Span.Length != StagingBufferPool.BufferSize)
                    {
                        Interlocked.Increment(ref errors[0]);
                        return;
                    }

                    // Fill with unique pattern and verify
                    var pattern = (byte)((threadId * 37 + i) & 0xFF);
                    buf.Span.Fill(pattern);

                    // Small delay to increase overlap with other threads
                    Thread.SpinWait(10);

                    // Verify pattern is still intact (no corruption from other threads)
                    for (var j = 0; j < buf.Span.Length; j++)
                    {
                        if (buf.Span[j] != pattern)
                        {
                            Interlocked.Increment(ref errors[0]);
                            return;
                        }
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(errors[0], Is.EqualTo(0), "Data corruption detected in concurrent rent/return");
        Assert.That(pool.CurrentRents, Is.EqualTo(0), "All buffers should be returned");
    }

    #endregion
}
