using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="FpiBitmap"/> — concurrent bitmap tracking FPI-written page cache slots.
/// </summary>
[TestFixture]
public class FpiBitmapTests
{
    #region Constructor

    [Test]
    public void Constructor_ValidBitCount_Creates()
    {
        var bitmap = new FpiBitmap(128);
        Assert.That(bitmap.BitCount, Is.EqualTo(128));
    }

    [Test]
    public void Constructor_NonMultipleOf64_Creates()
    {
        // 100 bits → 2 words (128 bits allocated, but only 100 addressable)
        var bitmap = new FpiBitmap(100);
        Assert.That(bitmap.BitCount, Is.EqualTo(100));
    }

    [Test]
    public void Constructor_SingleBit_Creates()
    {
        var bitmap = new FpiBitmap(1);
        Assert.That(bitmap.BitCount, Is.EqualTo(1));
    }

    [Test]
    public void Constructor_ZeroBitCount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FpiBitmap(0));

    [Test]
    public void Constructor_NegativeBitCount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FpiBitmap(-1));

    #endregion

    #region TestAndSet

    [Test]
    public void TestAndSet_FirstCall_ReturnsFalse()
    {
        var bitmap = new FpiBitmap(64);

        var wasAlreadySet = bitmap.TestAndSet(0);

        Assert.That(wasAlreadySet, Is.False);
    }

    [Test]
    public void TestAndSet_SecondCall_ReturnsTrue()
    {
        var bitmap = new FpiBitmap(64);

        bitmap.TestAndSet(0);
        var wasAlreadySet = bitmap.TestAndSet(0);

        Assert.That(wasAlreadySet, Is.True);
    }

    [Test]
    public void TestAndSet_DifferentBitsSameWord_Independent()
    {
        var bitmap = new FpiBitmap(64);

        var first0 = bitmap.TestAndSet(0);
        var first63 = bitmap.TestAndSet(63);

        Assert.That(first0, Is.False);
        Assert.That(first63, Is.False);
    }

    [Test]
    public void TestAndSet_DifferentWords_Independent()
    {
        var bitmap = new FpiBitmap(256);

        var firstInWord0 = bitmap.TestAndSet(10);
        var firstInWord1 = bitmap.TestAndSet(70);
        var firstInWord2 = bitmap.TestAndSet(130);
        var firstInWord3 = bitmap.TestAndSet(200);

        Assert.That(firstInWord0, Is.False);
        Assert.That(firstInWord1, Is.False);
        Assert.That(firstInWord2, Is.False);
        Assert.That(firstInWord3, Is.False);
    }

    #endregion

    #region IsSet

    [Test]
    public void IsSet_InitialState_ReturnsFalse()
    {
        var bitmap = new FpiBitmap(64);

        Assert.That(bitmap.IsSet(0), Is.False);
        Assert.That(bitmap.IsSet(63), Is.False);
    }

    [Test]
    public void IsSet_AfterTestAndSet_ReturnsTrue()
    {
        var bitmap = new FpiBitmap(64);
        bitmap.TestAndSet(42);

        Assert.That(bitmap.IsSet(42), Is.True);
        Assert.That(bitmap.IsSet(41), Is.False);
    }

    #endregion

    #region Clear

    [Test]
    public void Clear_SingleBit_ClearsBit()
    {
        var bitmap = new FpiBitmap(64);
        bitmap.TestAndSet(10);

        bitmap.Clear(10);

        Assert.That(bitmap.IsSet(10), Is.False);
    }

    [Test]
    public void Clear_NoInterferenceWithNeighbors()
    {
        var bitmap = new FpiBitmap(64);
        bitmap.TestAndSet(9);
        bitmap.TestAndSet(10);
        bitmap.TestAndSet(11);

        bitmap.Clear(10);

        Assert.That(bitmap.IsSet(9), Is.True);
        Assert.That(bitmap.IsSet(10), Is.False);
        Assert.That(bitmap.IsSet(11), Is.True);
    }

    [Test]
    public void Clear_AfterClear_TestAndSetReturnsFalse()
    {
        var bitmap = new FpiBitmap(64);
        bitmap.TestAndSet(5);
        bitmap.Clear(5);

        var wasAlreadySet = bitmap.TestAndSet(5);

        Assert.That(wasAlreadySet, Is.False);
    }

    #endregion

    #region ClearAll

    [Test]
    public void ClearAll_ResetsAllBits()
    {
        var bitmap = new FpiBitmap(256);
        for (var i = 0; i < 256; i++)
        {
            bitmap.TestAndSet(i);
        }

        bitmap.ClearAll();

        for (var i = 0; i < 256; i++)
        {
            Assert.That(bitmap.IsSet(i), Is.False, $"Bit {i} should be clear after ClearAll");
        }
    }

    [Test]
    public void ClearAll_NonMultipleOf64_ResetsAllBits()
    {
        var bitmap = new FpiBitmap(100);
        for (var i = 0; i < 100; i++)
        {
            bitmap.TestAndSet(i);
        }

        bitmap.ClearAll();

        for (var i = 0; i < 100; i++)
        {
            Assert.That(bitmap.IsSet(i), Is.False, $"Bit {i} should be clear after ClearAll");
        }
    }

    #endregion

    #region Concurrency

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_SameBit_ExactlyOneGetsFalse()
    {
        const int threadCount = 8;
        var bitmap = new FpiBitmap(64);
        var results = new bool[threadCount];
        using var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        var exceptions = new Exception[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    results[index] = bitmap.TestAndSet(0);
                }
                catch (Exception ex)
                {
                    exceptions[index] = ex;
                }
            });
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        for (var i = 0; i < threadCount; i++)
        {
            Assert.That(exceptions[i], Is.Null, $"Thread {i} threw: {exceptions[i]}");
        }

        // Exactly one thread should have gotten false (the first to set the bit)
        var falseCount = 0;
        var trueCount = 0;
        foreach (var r in results)
        {
            if (r)
            {
                trueCount++;
            }
            else
            {
                falseCount++;
            }
        }

        Assert.That(falseCount, Is.EqualTo(1), "Exactly one thread should be the first to set the bit");
        Assert.That(trueCount, Is.EqualTo(threadCount - 1));
    }

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_DifferentBits_AllGetFalse()
    {
        const int threadCount = 8;
        var bitmap = new FpiBitmap(256);
        var results = new bool[threadCount];
        using var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        var exceptions = new Exception[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            // Each thread targets a different bit (spread across words)
            var bitIndex = index * 30; // 0, 30, 60, 90, 120, 150, 180, 210
            threads[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    results[index] = bitmap.TestAndSet(bitIndex);
                }
                catch (Exception ex)
                {
                    exceptions[index] = ex;
                }
            });
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        for (var i = 0; i < threadCount; i++)
        {
            Assert.That(exceptions[i], Is.Null, $"Thread {i} threw: {exceptions[i]}");
        }

        // All threads target different bits, so all should get false
        foreach (var r in results)
        {
            Assert.That(r, Is.False);
        }
    }

    #endregion
}
