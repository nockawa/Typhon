// unset

using System.Diagnostics;
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
}
