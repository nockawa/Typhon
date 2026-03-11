using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

class MostCommonValuesTests
{
    [Test]
    public void Build_Empty_ZeroCounts()
    {
        var freq = new Dictionary<long, int>();
        var mcv = MostCommonValues.Build(freq, 0);

        Assert.That(mcv.TotalEntities, Is.EqualTo(0));
        Assert.That(mcv.RemainingEntries, Is.EqualTo(0));
        Assert.That(mcv.TryGetCount(42, out _), Is.False);
    }

    [Test]
    public void Build_IdentifiesTop100_ZipfDistribution()
    {
        var freq = new Dictionary<long, int>();
        const int totalDistinct = 500;
        long totalEntities = 0;

        // Zipf-like: value i has count ~1000/i
        for (int i = 1; i <= totalDistinct; i++)
        {
            int count = 1000 / i;
            if (count > 0)
            {
                freq[i] = count;
                totalEntities += count;
            }
        }

        var mcv = MostCommonValues.Build(freq, totalEntities);

        // Top value (1) should have count 1000
        Assert.That(mcv.TryGetCount(1, out long count1), Is.True);
        Assert.That(count1, Is.EqualTo(1000));

        // Value 2 should have count 500
        Assert.That(mcv.TryGetCount(2, out long count2), Is.True);
        Assert.That(count2, Is.EqualTo(500));

        Assert.That(mcv.TotalEntities, Is.EqualTo(totalEntities));
    }

    [Test]
    public void TryGetCount_TopKValue_ReturnsExactCount()
    {
        var freq = new Dictionary<long, int>
        {
            [10] = 50,
            [20] = 30,
            [30] = 20,
        };

        var mcv = MostCommonValues.Build(freq, 100);

        Assert.That(mcv.TryGetCount(10, out long c10), Is.True);
        Assert.That(c10, Is.EqualTo(50));

        Assert.That(mcv.TryGetCount(20, out long c20), Is.True);
        Assert.That(c20, Is.EqualTo(30));

        Assert.That(mcv.TryGetCount(30, out long c30), Is.True);
        Assert.That(c30, Is.EqualTo(20));
    }

    [Test]
    public void TryGetCount_NonTopKValue_ReturnsFalse()
    {
        var freq = new Dictionary<long, int>
        {
            [10] = 50,
            [20] = 30,
        };

        var mcv = MostCommonValues.Build(freq, 100);

        Assert.That(mcv.TryGetCount(99, out _), Is.False);
    }

    [Test]
    public void Build_LessThanCapacity_AllIncluded()
    {
        var freq = new Dictionary<long, int>
        {
            [1] = 10,
            [2] = 20,
            [3] = 30,
        };

        var mcv = MostCommonValues.Build(freq, 60, capacity: 100);

        Assert.That(mcv.TryGetCount(1, out _), Is.True);
        Assert.That(mcv.TryGetCount(2, out _), Is.True);
        Assert.That(mcv.TryGetCount(3, out _), Is.True);
        Assert.That(mcv.RemainingEntries, Is.EqualTo(0));
    }

    [Test]
    public void Build_ScaleFactor_ScalesCounts()
    {
        var freq = new Dictionary<long, int>
        {
            [10] = 5,
            [20] = 3,
        };

        // Scale by 10x (simulating 10x sampling ratio)
        var mcv = MostCommonValues.Build(freq, 80, scaleFactor: 10.0);

        Assert.That(mcv.TryGetCount(10, out long c10), Is.True);
        Assert.That(c10, Is.EqualTo(50));

        Assert.That(mcv.TryGetCount(20, out long c20), Is.True);
        Assert.That(c20, Is.EqualTo(30));

        Assert.That(mcv.TotalEntities, Is.EqualTo(80));
    }

    [Test]
    public void RemainingEntries_CorrectCalculation()
    {
        var freq = new Dictionary<long, int>
        {
            [1] = 40,
            [2] = 30,
        };

        var mcv = MostCommonValues.Build(freq, 100);

        // 100 - (40 + 30) = 30 remaining
        Assert.That(mcv.RemainingEntries, Is.EqualTo(30));
    }
}
