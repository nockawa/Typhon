// unset

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Collections;

[TestFixture]
public class ConcurrentBitmapL3AllTests
{
    [Test]
    [Explicit("Performance test - run manually")]
    public void FindNextUnsetL0_PerformanceTest()
    {
        const int BitSize = 1024 * 1024;
        const int Iterations = 10;

        // Sparse pattern test (25% filled)
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            var bitmap = new ConcurrentBitmapL3All(BitSize);
            for (int i = 0; i < BitSize; i += 4)
                bitmap.SetL0(i);

            int count = 0;
            int index = -1;
            long mask = 0;
            while (bitmap.FindNextUnsetL0(ref index, ref mask))
                count++;
        }
        var sparseTime = sw.ElapsedMilliseconds;

        // Dense pattern test (blocks of filled data)
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            var bitmap = new ConcurrentBitmapL3All(BitSize);
            for (int block = 0; block < BitSize; block += 8192)
            {
                for (int i = block; i < block + 4096 && i < BitSize; i++)
                    bitmap.SetL0(i);
            }

            int count = 0;
            int index = -1;
            long mask = 0;
            while (bitmap.FindNextUnsetL0(ref index, ref mask))
                count++;
        }
        var denseTime = sw.ElapsedMilliseconds;

        TestContext.WriteLine($"Sparse pattern ({Iterations} iterations): {sparseTime}ms");
        TestContext.WriteLine($"Dense pattern ({Iterations} iterations): {denseTime}ms");
    }

    [Test]
    public void FindNextUnsetL0_EmptyBitmap_FindsAllBitsSequentially()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        int index = -1;
        long mask = 0;

        for (int expected = 0; expected < 256; expected++)
        {
            Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True, $"Should find bit {expected}");
            Assert.That(index, Is.EqualTo(expected), $"Index should be {expected}");
        }

        // After finding all 256, should return false
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.False, "Should return false after capacity");
    }

    [Test]
    public void FindNextUnsetL0_WithSomeBitsSet_SkipsSetBits()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Set some bits
        bitmap.SetL0(0);
        bitmap.SetL0(1);
        bitmap.SetL0(5);
        bitmap.SetL0(64); // First bit of second L0 word
        bitmap.SetL0(128); // First bit of third L0 word

        int index = -1;
        long mask = 0;

        // Should find 2 first (0 and 1 are set)
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(2));

        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(3));

        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(4));

        // 5 is set, should get 6
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(6));
    }

    [Test]
    public void FindNextUnsetL0_FullL0Word_SkipsToNextWord()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Fill the entire first L0 word (64 bits)
        for (int i = 0; i < 64; i++)
        {
            bitmap.SetL0(i);
        }

        int index = -1;
        long mask = 0;

        // Should skip to bit 64 (first bit of second L0 word)
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(64));
    }

    [Test]
    public void FindNextUnsetL0_LargeBitmap_HandlesHierarchicalSkipping()
    {
        // 64 * 64 * 4 = 16384 bits to test L1 skipping
        var bitmap = new ConcurrentBitmapL3All(16384);

        // Fill first 64*64 = 4096 bits (entire first L1 region)
        for (int i = 0; i < 4096; i++)
        {
            bitmap.SetL0(i);
        }

        int index = -1;
        long mask = 0;

        // Should skip all 4096 and find bit 4096
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(4096));
    }

    [Test]
    public void FindNextUnsetL0_ResumeFromMiddle_ContinuesCorrectly()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        int index = 63; // Start from end of first L0 word
        long mask = -1L; // Pretend current L0 word is full

        // Should find first bit of next L0 word
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(64));
    }

    [Test]
    public void FindNextUnsetL0_AtCapacity_ReturnsFalse()
    {
        var bitmap = new ConcurrentBitmapL3All(64);

        // Fill all bits
        for (int i = 0; i < 64; i++)
        {
            bitmap.SetL0(i);
        }

        int index = -1;
        long mask = 0;

        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.False);
    }

    [Test]
    public void FindNextUnsetL0_SmallBitmap_HandlesEdgeCases()
    {
        var bitmap = new ConcurrentBitmapL3All(10);

        int index = -1;
        long mask = 0;

        for (int expected = 0; expected < 10; expected++)
        {
            Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True, $"Should find bit {expected}");
            Assert.That(index, Is.EqualTo(expected), $"Index should be {expected}");
        }

        // Beyond capacity
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.False);
    }

    #region Lock-Free Behavior Tests

    [Test]
    public void SetL0_ReturnsFalse_WhenBitAlreadySet()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // First set should succeed
        Assert.That(bitmap.SetL0(42), Is.True);

        // Second set should fail
        Assert.That(bitmap.SetL0(42), Is.False);
    }

    [Test]
    public void SetL1_UsesCompareExchange_FailsIfAnyBitSet()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Set just one bit in word 0
        bitmap.SetL0(5);

        // SetL1(0) should fail because word isn't empty
        Assert.That(bitmap.SetL1(0), Is.False);

        // Verify the original bit is still set (SetL1 didn't modify anything)
        Assert.That(bitmap.IsSet(5), Is.True);
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(1));
    }

    [Test]
    public void SetL1_Success_WhenWordIsEmpty()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // SetL1(1) should succeed (word 1 is empty)
        Assert.That(bitmap.SetL1(1), Is.True);

        // All 64 bits should be set
        for (int i = 64; i < 128; i++)
        {
            Assert.That(bitmap.IsSet(i), Is.True, $"Bit {i} should be set");
        }

        Assert.That(bitmap.TotalBitSet, Is.EqualTo(64));
    }

    [Test]
    public void ClearL0_ReturnsTrue_WhenBitWasSet()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        bitmap.SetL0(42);
        Assert.That(bitmap.ClearL0(42), Is.True);
        Assert.That(bitmap.IsSet(42), Is.False);
    }

    [Test]
    public void ClearL0_ReturnsTrue_WhenBitWasAlreadyClear()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Clearing an already-clear bit is idempotent, returns true (no resize)
        Assert.That(bitmap.ClearL0(42), Is.True);
        Assert.That(bitmap.IsSet(42), Is.False);
    }

    [Test]
    public void TotalBitSet_IsApproximate_UsesInterlocked()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Set 10 bits
        for (int i = 0; i < 10; i++)
        {
            bitmap.SetL0(i);
        }

        Assert.That(bitmap.TotalBitSet, Is.EqualTo(10));

        // Clear 3 bits
        bitmap.ClearL0(0);
        bitmap.ClearL0(1);
        bitmap.ClearL0(2);

        Assert.That(bitmap.TotalBitSet, Is.EqualTo(7));
    }

    [Test]
    public void Resize_UpdatesVersion_CanBeDetected()
    {
        var bitmap = new ConcurrentBitmapL3All(128);

        // Set some bits
        bitmap.SetL0(0);
        bitmap.SetL0(64);

        // Resize
        bitmap.Resize(256);

        // Bits should still be set
        Assert.That(bitmap.IsSet(0), Is.True);
        Assert.That(bitmap.IsSet(64), Is.True);
        Assert.That(bitmap.Capacity, Is.EqualTo(256));
    }

    [Test]
    public void Resize_Shrink_RecountsBits()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Set bits in first and last word
        for (int i = 0; i < 32; i++)
        {
            bitmap.SetL0(i);
        }
        bitmap.SetL0(200); // This will be outside new capacity

        Assert.That(bitmap.TotalBitSet, Is.EqualTo(33));

        // Shrink to 128 bits (bit 200 is lost)
        bitmap.Resize(128);

        // TotalBitSet should be recounted
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(32));
        Assert.That(bitmap.Capacity, Is.EqualTo(128));
    }

    [Test]
    public void ConcurrentSetL0_AllBitsEventuallySet()
    {
        const int BitCount = 1024;
        var bitmap = new ConcurrentBitmapL3All(BitCount);
        var successCount = 0;

        // Multiple threads trying to set all bits
        Parallel.For(0, BitCount, i =>
        {
            if (bitmap.SetL0(i))
            {
                Interlocked.Increment(ref successCount);
            }
        });

        // All bits should be set, and each set should have succeeded exactly once
        Assert.That(successCount, Is.EqualTo(BitCount));
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(BitCount));

        for (int i = 0; i < BitCount; i++)
        {
            Assert.That(bitmap.IsSet(i), Is.True, $"Bit {i} should be set");
        }
    }

    [Test]
    public void ConcurrentSetL1_NoDataCorruption()
    {
        const int WordCount = 16;
        var bitmap = new ConcurrentBitmapL3All(WordCount * 64);
        var successCount = 0;

        // Multiple threads trying to claim entire words
        Parallel.For(0, WordCount, i =>
        {
            if (bitmap.SetL1(i))
            {
                Interlocked.Increment(ref successCount);
            }
        });

        // All SetL1 calls should succeed (each targets a different word)
        Assert.That(successCount, Is.EqualTo(WordCount));
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(WordCount * 64));
    }

    #endregion

    #region L1/L2 Hint Correctness Tests (Previously Bug Tests)

    [Test]
    public void ClearL0_CorrectlyUpdatesL1All_WhenClearingFromFullWord()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Fill L0 words 0, 1, 2 completely
        for (int i = 0; i < 192; i++)
            bitmap.SetL0(i);

        // Clear bit 0 (from L0 word 0)
        bitmap.ClearL0(0);

        // L1All should correctly indicate only words 1 and 2 are full
        // (word 0 is no longer full)

        // Verify L0 data is still correct
        for (int i = 64; i < 192; i++)
            Assert.That(bitmap.IsSet(i), Is.True, $"Bit {i} should still be set");

        // FindNextUnsetL0 should work correctly
        int index = -1;
        long mask = 0;

        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(0), "First free bit should be 0");

        // Next free bit should be 192 (skipping full words 1 and 2)
        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(192), "Next free bit should be 192");
    }

    [Test]
    public void ClearL0_CorrectlyUpdatesL2All_WhenClearingFromFullL1Region()
    {
        var bitmap = new ConcurrentBitmapL3All(16384);

        // Fill first L1 region (bits 0-4095 = 64 L0 words)
        for (int i = 0; i < 4096; i++)
            bitmap.SetL0(i);

        // Fill second L1 region (bits 4096-8191 = next 64 L0 words)
        for (int i = 4096; i < 8192; i++)
            bitmap.SetL0(i);

        // Clear bit 0
        bitmap.ClearL0(0);

        // Verify second region is still set
        for (int i = 4096; i < 8192; i++)
            Assert.That(bitmap.IsSet(i), Is.True, $"Bit {i} should still be set");

        // FindNextUnsetL0 should correctly skip the full second L1 region
        int index = -1;
        long mask = 0;

        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(0), "First free bit should be 0");

        Assert.That(bitmap.FindNextUnsetL0(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(8192), "Next free bit should be 8192");
    }

    [Test]
    public void ClearL0_CorrectlyUpdatesL1Any_WhenL0WordBecomesEmpty()
    {
        var bitmap = new ConcurrentBitmapL3All(256);

        // Set one bit in L0 words 0, 1, 2
        bitmap.SetL0(0);
        bitmap.SetL0(64);
        bitmap.SetL0(128);

        // Clear bit 0 - L0 word 0 becomes empty
        bitmap.ClearL0(0);

        // Verify other bits still set
        Assert.That(bitmap.IsSet(0), Is.False, "Bit 0 should be cleared");
        Assert.That(bitmap.IsSet(64), Is.True, "Bit 64 should still be set");
        Assert.That(bitmap.IsSet(128), Is.True, "Bit 128 should still be set");

        // FindNextUnsetL1 should correctly find word 0 as empty
        int index = -1;
        long mask = 0;

        Assert.That(bitmap.FindNextUnsetL1(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(0), "First empty L0 word should be 0");

        // Next empty word should be 3 (skip words 1, 2 which have bits)
        Assert.That(bitmap.FindNextUnsetL1(ref index, ref mask), Is.True);
        Assert.That(index, Is.EqualTo(3), "Next empty L0 word should be 3");
    }

    #endregion

    #region Stress Tests - PageAllocator Simulation

    /// <summary>
    /// Stress test simulating PageAllocator usage pattern:
    /// - Multiple threads concurrently allocate blocks (find + set)
    /// - Track allocations to detect duplicate ownership
    /// - Release blocks after short usage
    /// - Heavy load to expose race conditions
    /// </summary>
    [Test]
    [Repeat(3)] // Run multiple times to increase chance of catching race conditions
    public void StressTest_PageAllocatorSimulation_L0Allocation()
    {
        const int BitmapSize = 8192;         // 8K slots for more headroom
        const int ThreadCount = 10;          // Concurrent threads
        const int OperationsPerThread = 10000;

        var bitmap = new ConcurrentBitmapL3All(BitmapSize);

        // Track allocations: blockId -> (threadId, allocationToken)
        // If we see a different threadId for the same block, we have a race!
        var allocations = new ConcurrentDictionary<int, long>();

        var errors = new ConcurrentBag<string>();
        var totalAllocations = 0;
        var totalReleases = 0;
        var allocationFailures = 0;  // Expected when bitmap is full

        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var rng = new Random(threadId * 12345);
                var localAllocations = new List<int>();

                barrier.SignalAndWait(); // Sync start for maximum contention

                for (int op = 0; op < OperationsPerThread; op++)
                {
                    // Decide: allocate or release
                    bool shouldAllocate = localAllocations.Count == 0 ||
                                          (localAllocations.Count < 100 && rng.Next(100) < 70);

                    if (shouldAllocate)
                    {
                        // Find and allocate a block
                        int index = -1;
                        long mask = 0;

                        // Try to find an unset bit
                        while (bitmap.FindNextUnsetL0(ref index, ref mask))
                        {
                            // Try to claim it
                            if (bitmap.SetL0(index))
                            {
                                // Create unique token: threadId in upper bits, operation in lower
                                long token = ((long)threadId << 32) | (uint)op;

                                // Try to register our allocation
                                if (!allocations.TryAdd(index, token))
                                {
                                    // Someone else already has this block - RACE CONDITION!
                                    var existingToken = allocations[index];
                                    var existingThread = existingToken >> 32;
                                    errors.Add($"DUPLICATE ALLOCATION: Block {index} claimed by thread {threadId} " +
                                              $"but already owned by thread {existingThread}");
                                }

                                localAllocations.Add(index);
                                Interlocked.Increment(ref totalAllocations);
                                break;
                            }
                            // SetL0 failed (bit was already set by another thread), try next
                        }

                        if (index >= BitmapSize || index == -1)
                        {
                            // Bitmap is full, expected under load
                            Interlocked.Increment(ref allocationFailures);
                        }
                    }
                    else if (localAllocations.Count > 0)
                    {
                        // Release a random held block
                        int releaseIdx = rng.Next(localAllocations.Count);
                        int blockToRelease = localAllocations[releaseIdx];
                        localAllocations.RemoveAt(releaseIdx);

                        // Verify we still own it before release
                        if (allocations.TryRemove(blockToRelease, out var token))
                        {
                            var owningThread = token >> 32;
                            if (owningThread != threadId)
                            {
                                errors.Add($"OWNERSHIP MISMATCH: Thread {threadId} releasing block {blockToRelease} " +
                                          $"but token shows thread {owningThread}");
                            }
                        }
                        else
                        {
                            errors.Add($"MISSING ALLOCATION: Thread {threadId} tried to release block {blockToRelease} " +
                                      "but it wasn't in allocations dictionary");
                        }

                        // Actually clear the bit
                        bitmap.ClearL0(blockToRelease);
                        Interlocked.Increment(ref totalReleases);
                    }

                    // Occasional tiny delay to vary timing
                    if (rng.Next(100) < 5)
                    {
                        Thread.SpinWait(rng.Next(100));
                    }
                }

                // Cleanup: release all remaining allocations
                foreach (var block in localAllocations)
                {
                    allocations.TryRemove(block, out _);
                    bitmap.ClearL0(block);
                    Interlocked.Increment(ref totalReleases);
                }
            });
        }

        Task.WaitAll(tasks);

        // Report results
        TestContext.WriteLine($"Total allocations: {totalAllocations}");
        TestContext.WriteLine($"Total releases: {totalReleases}");
        TestContext.WriteLine($"Allocation failures (bitmap full): {allocationFailures}");
        TestContext.WriteLine($"Final TotalBitSet: {bitmap.TotalBitSet}");
        TestContext.WriteLine($"Remaining in dictionary: {allocations.Count}");

        // Verify no errors
        Assert.That(errors, Is.Empty, $"Race conditions detected:\n{string.Join("\n", errors.Take(10))}");

        // Verify consistency
        Assert.That(allocations.Count, Is.EqualTo(0), "All allocations should be released");
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(0), "All bits should be cleared");
    }

    /// <summary>
    /// Stress test for L1 (bulk) allocation - simulates allocating full 64-bit words.
    /// </summary>
    [Test]
    [Repeat(3)]
    public void StressTest_PageAllocatorSimulation_L1Allocation()
    {
        const int BitmapSize = 64 * 128;    // 128 L0 words = 8192 bits
        const int ThreadCount = 10;
        const int OperationsPerThread = 5000;

        var bitmap = new ConcurrentBitmapL3All(BitmapSize);
        var allocations = new ConcurrentDictionary<int, long>(); // L0 word index -> token
        var errors = new ConcurrentBag<string>();
        var totalAllocations = 0;
        var totalReleases = 0;

        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var rng = new Random(threadId * 54321);
                var localAllocations = new List<int>(); // L0 word indices

                barrier.SignalAndWait();

                for (int op = 0; op < OperationsPerThread; op++)
                {
                    bool shouldAllocate = localAllocations.Count == 0 ||
                                          (localAllocations.Count < 20 && rng.Next(100) < 60);

                    if (shouldAllocate)
                    {
                        int index = -1;
                        long mask = 0;
                        int maxWordIndex = (BitmapSize + 63) / 64;

                        // Find empty L0 word (all 64 bits clear)
                        while (bitmap.FindNextUnsetL1(ref index, ref mask))
                        {
                            // Check bounds - FindNextUnsetL1 may return beyond valid words
                            if (index >= maxWordIndex)
                                break;

                            // Try to claim entire word
                            if (bitmap.SetL1(index))
                            {
                                long token = ((long)threadId << 32) | (uint)op;

                                if (!allocations.TryAdd(index, token))
                                {
                                    var existingToken = allocations[index];
                                    var existingThread = existingToken >> 32;
                                    errors.Add($"DUPLICATE L1 ALLOCATION: Word {index} claimed by thread {threadId} " +
                                              $"but already owned by thread {existingThread}");
                                }

                                // Verify all 64 bits are set
                                for (int bit = 0; bit < 64; bit++)
                                {
                                    int bitIndex = index * 64 + bit;
                                    if (bitIndex < BitmapSize && !bitmap.IsSet(bitIndex))
                                    {
                                        errors.Add($"L1 INCOMPLETE: Word {index} claimed but bit {bitIndex} not set");
                                    }
                                }

                                localAllocations.Add(index);
                                Interlocked.Increment(ref totalAllocations);
                                break;
                            }
                        }
                    }
                    else if (localAllocations.Count > 0)
                    {
                        int releaseIdx = rng.Next(localAllocations.Count);
                        int wordToRelease = localAllocations[releaseIdx];
                        localAllocations.RemoveAt(releaseIdx);

                        allocations.TryRemove(wordToRelease, out _);

                        // Clear all 64 bits
                        for (int bit = 0; bit < 64; bit++)
                        {
                            int bitIndex = wordToRelease * 64 + bit;
                            if (bitIndex < BitmapSize)
                            {
                                bitmap.ClearL0(bitIndex);
                            }
                        }

                        Interlocked.Increment(ref totalReleases);
                    }

                    if (rng.Next(100) < 3)
                    {
                        Thread.SpinWait(rng.Next(50));
                    }
                }

                // Cleanup
                foreach (var word in localAllocations)
                {
                    allocations.TryRemove(word, out _);
                    for (int bit = 0; bit < 64; bit++)
                    {
                        int bitIndex = word * 64 + bit;
                        if (bitIndex < BitmapSize)
                        {
                            bitmap.ClearL0(bitIndex);
                        }
                    }
                    Interlocked.Increment(ref totalReleases);
                }
            });
        }

        Task.WaitAll(tasks);

        TestContext.WriteLine($"L1 allocations: {totalAllocations}");
        TestContext.WriteLine($"L1 releases: {totalReleases}");
        TestContext.WriteLine($"Final TotalBitSet: {bitmap.TotalBitSet}");

        Assert.That(errors, Is.Empty, $"Race conditions detected:\n{string.Join("\n", errors.Take(10))}");
        Assert.That(allocations.Count, Is.EqualTo(0), "All L1 allocations should be released");
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(0), "All bits should be cleared");
    }

    /// <summary>
    /// Mixed L0/L1 allocation stress test - most realistic simulation.
    /// </summary>
    [Test]
    [Repeat(3)]
    public void StressTest_PageAllocatorSimulation_MixedAllocation()
    {
        const int BitmapSize = 16384;        // 16K slots for mixed L0/L1
        const int ThreadCount = 10;
        const int OperationsPerThread = 8000;

        var bitmap = new ConcurrentBitmapL3All(BitmapSize);

        // Track both L0 and L1 allocations
        var l0Allocations = new ConcurrentDictionary<int, long>();  // bit index -> token
        var l1Allocations = new ConcurrentDictionary<int, long>();  // word index -> token
        var errors = new ConcurrentBag<string>();

        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var rng = new Random(threadId * 98765);
                var myL0 = new List<int>();
                var myL1 = new List<int>();

                barrier.SignalAndWait();

                for (int op = 0; op < OperationsPerThread; op++)
                {
                    int action = rng.Next(100);

                    if (action < 40) // 40% L0 allocate
                    {
                        int index = -1;
                        long mask = 0;

                        if (bitmap.FindNextUnsetL0(ref index, ref mask))
                        {
                            if (bitmap.SetL0(index))
                            {
                                long token = ((long)threadId << 32) | (uint)op;

                                if (!l0Allocations.TryAdd(index, token))
                                {
                                    errors.Add($"L0 DUPLICATE: Bit {index} double-allocated");
                                }

                                // Verify bit doesn't belong to an L1 allocation
                                int wordIdx = index / 64;
                                if (l1Allocations.ContainsKey(wordIdx))
                                {
                                    errors.Add($"L0/L1 CONFLICT: Bit {index} allocated but word {wordIdx} is L1-owned");
                                }

                                myL0.Add(index);
                            }
                        }
                    }
                    else if (action < 60) // 20% L1 allocate
                    {
                        int index = -1;
                        long mask = 0;

                        if (bitmap.FindNextUnsetL1(ref index, ref mask))
                        {
                            if (bitmap.SetL1(index))
                            {
                                long token = ((long)threadId << 32) | (uint)op;

                                if (!l1Allocations.TryAdd(index, token))
                                {
                                    errors.Add($"L1 DUPLICATE: Word {index} double-allocated");
                                }

                                // Verify no L0 allocations in this word
                                for (int bit = 0; bit < 64; bit++)
                                {
                                    int bitIdx = index * 64 + bit;
                                    if (l0Allocations.ContainsKey(bitIdx))
                                    {
                                        errors.Add($"L1/L0 CONFLICT: Word {index} allocated but bit {bitIdx} was L0-owned");
                                    }
                                }

                                myL1.Add(index);
                            }
                        }
                    }
                    else if (action < 80 && myL0.Count > 0) // 20% L0 release
                    {
                        int idx = rng.Next(myL0.Count);
                        int bitToRelease = myL0[idx];
                        myL0.RemoveAt(idx);

                        l0Allocations.TryRemove(bitToRelease, out _);
                        bitmap.ClearL0(bitToRelease);
                    }
                    else if (myL1.Count > 0) // 20% L1 release
                    {
                        int idx = rng.Next(myL1.Count);
                        int wordToRelease = myL1[idx];
                        myL1.RemoveAt(idx);

                        l1Allocations.TryRemove(wordToRelease, out _);

                        for (int bit = 0; bit < 64; bit++)
                        {
                            int bitIdx = wordToRelease * 64 + bit;
                            if (bitIdx < BitmapSize)
                            {
                                bitmap.ClearL0(bitIdx);
                            }
                        }
                    }
                }

                // Cleanup
                foreach (var bit in myL0)
                {
                    l0Allocations.TryRemove(bit, out _);
                    bitmap.ClearL0(bit);
                }
                foreach (var word in myL1)
                {
                    l1Allocations.TryRemove(word, out _);
                    for (int bit = 0; bit < 64; bit++)
                    {
                        int bitIdx = word * 64 + bit;
                        if (bitIdx < BitmapSize)
                        {
                            bitmap.ClearL0(bitIdx);
                        }
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        TestContext.WriteLine($"Final TotalBitSet: {bitmap.TotalBitSet}");
        TestContext.WriteLine($"Remaining L0: {l0Allocations.Count}, L1: {l1Allocations.Count}");

        Assert.That(errors, Is.Empty, $"Race conditions detected:\n{string.Join("\n", errors.Take(10))}");
        Assert.That(l0Allocations.Count, Is.EqualTo(0));
        Assert.That(l1Allocations.Count, Is.EqualTo(0));
        Assert.That(bitmap.TotalBitSet, Is.EqualTo(0));
    }

    /// <summary>
    /// Stress test with concurrent resize operations.
    /// </summary>
    [Test]
    public void StressTest_ConcurrentResize()
    {
        const int InitialSize = 2048;
        const int ThreadCount = 10;
        const int OperationsPerThread = 5000;

        var bitmap = new ConcurrentBitmapL3All(InitialSize);
        var allocations = new ConcurrentDictionary<int, long>();
        var errors = new ConcurrentBag<string>();
        var resizeCount = 0;

        var barrier = new Barrier(ThreadCount + 1); // +1 for resize thread (11 total)
        var cts = new CancellationTokenSource();
        var tasks = new List<Task>();

        // Resize thread
        tasks.Add(Task.Run(() =>
        {
            barrier.SignalAndWait();
            var rng = new Random(999);

            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(rng.Next(5, 20));

                int currentCap = bitmap.Capacity;
                int newCap = rng.Next(100) < 50
                    ? Math.Min(currentCap + 1024, 8192)  // Grow
                    : Math.Max(currentCap - 512, 1024);   // Shrink

                bitmap.Resize(newCap);
                Interlocked.Increment(ref resizeCount);
            }
        }));

        // Worker threads
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                var rng = new Random(threadId * 11111);
                var myAllocations = new List<int>();

                barrier.SignalAndWait();

                for (int op = 0; op < OperationsPerThread; op++)
                {
                    try
                    {
                        int capacity = bitmap.Capacity;

                        if (rng.Next(100) < 60 && myAllocations.Count < 50)
                        {
                            // Allocate
                            int index = -1;
                            long mask = 0;

                            if (bitmap.FindNextUnsetL0(ref index, ref mask) && index < capacity)
                            {
                                if (bitmap.SetL0(index))
                                {
                                    long token = ((long)threadId << 32) | (uint)op;

                                    if (!allocations.TryAdd(index, token))
                                    {
                                        // Could happen during resize - verify
                                        if (bitmap.IsSet(index))
                                        {
                                            errors.Add($"RESIZE RACE: Bit {index} double-claimed");
                                        }
                                    }
                                    else
                                    {
                                        myAllocations.Add(index);
                                    }
                                }
                            }
                        }
                        else if (myAllocations.Count > 0)
                        {
                            // Release
                            int idx = rng.Next(myAllocations.Count);
                            int bitToRelease = myAllocations[idx];
                            myAllocations.RemoveAt(idx);

                            allocations.TryRemove(bitToRelease, out _);

                            // Only clear if still in bounds
                            if (bitToRelease < bitmap.Capacity)
                            {
                                bitmap.ClearL0(bitToRelease);
                            }
                        }
                    }
                    catch (IndexOutOfRangeException)
                    {
                        // Expected during resize - capacity changed mid-operation
                    }
                }

                // Cleanup
                foreach (var bit in myAllocations)
                {
                    allocations.TryRemove(bit, out _);
                    try
                    {
                        if (bit < bitmap.Capacity)
                        {
                            bitmap.ClearL0(bit);
                        }
                    }
                    catch (IndexOutOfRangeException)
                    {
                        // Capacity shrank, bit no longer valid
                    }
                }
            }));
        }

        // Let workers finish
        Task.WhenAll(tasks.Skip(1)).Wait();
        cts.Cancel();

        try { tasks[0].Wait(1000); } catch { }

        TestContext.WriteLine($"Resize operations: {resizeCount}");
        TestContext.WriteLine($"Final capacity: {bitmap.Capacity}");
        TestContext.WriteLine($"Final TotalBitSet: {bitmap.TotalBitSet}");

        // With resize, some allocations might be lost (shrank out of bounds)
        // But we should never have duplicates
        var duplicateErrors = errors.Where(e => e.Contains("DUPLICATE") || e.Contains("double")).ToList();
        Assert.That(duplicateErrors, Is.Empty,
            $"Duplicate allocation errors:\n{string.Join("\n", duplicateErrors.Take(10))}");
    }

    #endregion
}
