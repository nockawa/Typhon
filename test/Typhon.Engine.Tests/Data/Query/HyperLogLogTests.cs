using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

class HyperLogLogTests
{
    [Test]
    public void Empty_ReturnsZero()
    {
        var hll = new HyperLogLog();
        Assert.That(hll.EstimateCardinality(), Is.EqualTo(0));
    }

    [Test]
    public void SingleValue_NDV1()
    {
        var hll = new HyperLogLog();
        for (int i = 0; i < 100; i++)
        {
            hll.Add(42);
        }

        Assert.That(hll.EstimateCardinality(), Is.EqualTo(1));
    }

    [Test]
    public void Cardinality_10K_Unique_Within5Percent()
    {
        var hll = new HyperLogLog();
        const int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            hll.Add(i);
        }

        long estimate = hll.EstimateCardinality();
        double errorPct = Math.Abs(estimate - count) / (double)count;
        Assert.That(errorPct, Is.LessThan(0.05), $"Estimate {estimate} for {count} unique values, error {errorPct:P2}");
    }

    [Test]
    public void Cardinality_100K_Unique_Within5Percent()
    {
        var hll = new HyperLogLog();
        const int count = 100_000;
        for (int i = 0; i < count; i++)
        {
            hll.Add(i);
        }

        long estimate = hll.EstimateCardinality();
        double errorPct = Math.Abs(estimate - count) / (double)count;
        Assert.That(errorPct, Is.LessThan(0.05), $"Estimate {estimate} for {count} unique values, error {errorPct:P2}");
    }

    [Test]
    public void Cardinality_ZipfSkewed_ReasonableEstimate()
    {
        // Zipf-like: 1000 distinct values with heavy repetition
        var hll = new HyperLogLog();
        var rand = new Random(12345);
        const int distinctValues = 1000;
        const int totalInserts = 50_000;

        for (int i = 0; i < totalInserts; i++)
        {
            // Zipf-like: lower values appear more frequently
            int rank = rand.Next(1, distinctValues + 1);
            long value = (long)(distinctValues / (double)rank);
            hll.Add(value);
        }

        long estimate = hll.EstimateCardinality();
        // Zipf maps many ranks to the same value (floor division), so actual distinct << 1000
        // Just verify the estimate is reasonable (not 0, not totalInserts)
        Assert.That(estimate, Is.GreaterThan(10));
        Assert.That(estimate, Is.LessThan(distinctValues));
    }

    [Test]
    public void AllUnique_MatchesActualCount()
    {
        var hll = new HyperLogLog();
        const int count = 5000;
        for (int i = 0; i < count; i++)
        {
            hll.Add(i * 7L + 31); // Spread out values
        }

        long estimate = hll.EstimateCardinality();
        double errorPct = Math.Abs(estimate - count) / (double)count;
        Assert.That(errorPct, Is.LessThan(0.05), $"Estimate {estimate} for {count} unique, error {errorPct:P2}");
    }

    [Test]
    public void Merge_DisjointSets_CorrectCombinedEstimate()
    {
        var hll1 = new HyperLogLog();
        var hll2 = new HyperLogLog();

        // Set 1: values 0..4999
        for (int i = 0; i < 5000; i++)
        {
            hll1.Add(i);
        }

        // Set 2: values 5000..9999 (disjoint)
        for (int i = 5000; i < 10000; i++)
        {
            hll2.Add(i);
        }

        hll1.Merge(hll2);
        long estimate = hll1.EstimateCardinality();
        double errorPct = Math.Abs(estimate - 10000) / 10000.0;
        Assert.That(errorPct, Is.LessThan(0.05), $"Merged estimate {estimate} for 10K unique, error {errorPct:P2}");
    }

    [Test]
    public void Clear_ResetsToZero()
    {
        var hll = new HyperLogLog();
        for (int i = 0; i < 1000; i++)
        {
            hll.Add(i);
        }

        Assert.That(hll.EstimateCardinality(), Is.GreaterThan(0));

        hll.Clear();
        Assert.That(hll.EstimateCardinality(), Is.EqualTo(0));
    }
}
