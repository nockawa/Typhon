using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

class SelectivityEstimatorTests : TestBase<SelectivityEstimatorTests>
{
    private static long FloatThreshold(float v)
    {
        var bits = Unsafe.As<float, int>(ref v);
        return (long)bits;
    }

    private static long DoubleThreshold(double v) => Unsafe.As<double, long>(ref v);

    private static long CreateAndCommit(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var pk = t.CreateEntity(ref d);
        t.Commit();
        return pk;
    }

    private static long CreateAndCommitCompF(DatabaseEngine dbe, int gold, int rank)
    {
        using var t = dbe.CreateQuickTransaction();
        var f = new CompF(gold, rank);
        var pk = t.CreateEntity(ref f);
        t.Commit();
        return pk;
    }

    #region IndexStatistics Tests

    [Test]
    public void IndexStats_EmptyIndex_ZeroCounts()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // CompD has 3 indexed fields: A(float), B(int), C(double)
        Assert.That(ct.IndexStats.Length, Is.EqualTo(3));

        for (var i = 0; i < ct.IndexStats.Length; i++)
        {
            Assert.That(ct.IndexStats[i].EntryCount, Is.EqualTo(0));
            Assert.That(ct.IndexStats[i].MinValue, Is.EqualTo(0));
            Assert.That(ct.IndexStats[i].MaxValue, Is.EqualTo(0));
        }
    }

    [Test]
    public void IndexStats_SingleEntry_MinEqualsMax()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        CreateAndCommit(dbe, 1.5f, 42, 3.14);

        // Field B (int, index 1): min and max should both be 42
        var stats = ct.IndexStats[1];
        Assert.That(stats.EntryCount, Is.EqualTo(1));
        Assert.That(stats.MinValue, Is.EqualTo(42));
        Assert.That(stats.MaxValue, Is.EqualTo(42));
    }

    [Test]
    public void IndexStats_MultipleEntries_CorrectMinMax()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        CreateAndCommit(dbe, 1.0f, 10, 1.0);
        CreateAndCommit(dbe, 2.0f, 50, 5.0);
        CreateAndCommit(dbe, 3.0f, 30, 3.0);

        // Field B (int, index 1): min=10, max=50
        var stats = ct.IndexStats[1];
        Assert.That(stats.EntryCount, Is.EqualTo(3));
        Assert.That(stats.MinValue, Is.EqualTo(10));
        Assert.That(stats.MaxValue, Is.EqualTo(50));
    }

    [Test]
    public void IndexStats_DistinctValues_ReturnsMinusOne()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        Assert.That(ct.IndexStats[0].DistinctValues, Is.EqualTo(-1));
    }

    [Test]
    public void IndexStats_HistogramInitiallyNull()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        Assert.That(ct.IndexStats[0].Histogram, Is.Null);
        Assert.That(ct.IndexStats[1].Histogram, Is.Null);
        Assert.That(ct.IndexStats[2].Histogram, Is.Null);
    }

    #endregion

    #region BasicSelectivityEstimator Tests

    [Test]
    public void Basic_EmptyIndex_ReturnsZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = BasicSelectivityEstimator.Instance;

        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 42), Is.EqualTo(0));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 42), Is.EqualTo(0));
    }

    [Test]
    public void Basic_Equality_UniqueIndex_ExactCount()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = BasicSelectivityEstimator.Instance;

        CreateAndCommit(dbe, 1.0f, 10, 1.0);
        CreateAndCommit(dbe, 2.0f, 20, 2.0);
        CreateAndCommit(dbe, 3.0f, 30, 3.0);

        // B is unique index: exactly 1 match for B==20
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 20), Is.EqualTo(1));

        // No match for B==99
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 99), Is.EqualTo(0));
    }

    [Test]
    public void Basic_Equality_MultiValueIndex_ExactCount()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompF>();
        var estimator = BasicSelectivityEstimator.Instance;

        // Gold is AllowMultiple: insert 3 entities with same Gold=100
        CreateAndCommitCompF(dbe, 100, 1);
        CreateAndCommitCompF(dbe, 100, 2);
        CreateAndCommitCompF(dbe, 100, 3);
        CreateAndCommitCompF(dbe, 200, 4);

        // Gold index is field 0
        Assert.That(estimator.EstimateCardinality(ct, 0, CompareOp.Equal, 100), Is.EqualTo(3));
        Assert.That(estimator.EstimateCardinality(ct, 0, CompareOp.Equal, 200), Is.EqualTo(1));
        Assert.That(estimator.EstimateCardinality(ct, 0, CompareOp.Equal, 999), Is.EqualTo(0));
    }

    [Test]
    public void Basic_NotEqual_ReturnsTotal_MinusExact()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = BasicSelectivityEstimator.Instance;

        CreateAndCommit(dbe, 1.0f, 10, 1.0);
        CreateAndCommit(dbe, 2.0f, 20, 2.0);
        CreateAndCommit(dbe, 3.0f, 30, 3.0);

        // NotEqual to B==20: 3 total - 1 = 2
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.NotEqual, 20), Is.EqualTo(2));
    }

    [Test]
    public void Basic_Range_UniformEstimate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = BasicSelectivityEstimator.Instance;

        // Insert 100 entities with B from 0 to 99
        for (var i = 0; i < 100; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        // B > 49 should estimate ~50 (uniform distribution: 50 out of 99 range)
        var gt = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 49);
        Assert.That(gt, Is.InRange(40, 60));

        // B < 50 should estimate ~50
        var lt = estimator.EstimateCardinality(ct, 1, CompareOp.LessThan, 50);
        Assert.That(lt, Is.InRange(40, 60));

        // B >= 0 should estimate ~total
        var gte = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThanOrEqual, 0);
        Assert.That(gte, Is.InRange(90, 100));

        // B <= 99 should estimate ~total
        var lte = estimator.EstimateCardinality(ct, 1, CompareOp.LessThanOrEqual, 99);
        Assert.That(lte, Is.InRange(90, 100));
    }

    [Test]
    public void Basic_SingleEntry_AllOrNothing()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = BasicSelectivityEstimator.Instance;

        CreateAndCommit(dbe, 1.0f, 42, 1.0);

        // min == max == 42: degenerate case
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 42), Is.EqualTo(1));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 99), Is.EqualTo(0));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThanOrEqual, 42), Is.EqualTo(1));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 42), Is.EqualTo(0));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.LessThanOrEqual, 42), Is.EqualTo(1));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.LessThan, 42), Is.EqualTo(0));
    }

    #endregion

    #region Histogram Tests

    [Test]
    public void Histogram_UniformData_RoughlyEqualBuckets()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 1000 entities with B from 0 to 999
        for (var i = 0; i < 1000; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();
        var histogram = ct.IndexStats[1].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(1000));
        Assert.That(histogram.MinValue, Is.EqualTo(0));
        Assert.That(histogram.MaxValue, Is.EqualTo(999));

        // Verify bucket counts sum to total scanned entries
        var sumCounts = 0;
        for (var i = 0; i < Histogram.BucketCount; i++)
        {
            sumCounts += histogram.BucketCounts[i];
        }
        Assert.That(sumCounts, Is.EqualTo(1000));

        // Interior buckets (0..98) should have roughly 9-10 entries each
        // The last bucket absorbs the integer-division remainder, so skip it
        for (var i = 0; i < Histogram.BucketCount - 1; i++)
        {
            Assert.That(histogram.BucketCounts[i], Is.InRange(5, 15),
                $"Bucket {i} has {histogram.BucketCounts[i]} entries — expected ~10 for uniform data");
        }
    }

    [Test]
    public void Histogram_SkewedData_NonUniformBuckets()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Skewed: 80 values in [0..79], 20 values in [9980..9999]
        // This creates two clusters with a big gap in the middle
        for (var i = 0; i < 80; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }
        for (var i = 0; i < 20; i++)
        {
            CreateAndCommit(dbe, 1.0f, 9980 + i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();
        var histogram = ct.IndexStats[1].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(100));

        // min=0, max=9999, BucketWidth = max(1, 9999/100) = 99
        // Bucket 0 covers [0, 99) → should contain all 80 low-cluster values
        Assert.That(histogram.BucketCounts[0], Is.GreaterThanOrEqualTo(50),
            "Low cluster should dominate bucket 0");

        // Interior buckets (1..98) should be mostly empty
        var interiorSum = 0;
        for (var i = 1; i < Histogram.BucketCount - 1; i++)
        {
            interiorSum += histogram.BucketCounts[i];
        }
        Assert.That(interiorSum, Is.LessThan(30), "Interior buckets should be mostly empty for skewed data");
    }

    [Test]
    public void Histogram_RangeEstimation_UniformData_LowError()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 1000 entities with B from 0 to 999
        for (var i = 0; i < 1000; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();
        var histogram = ct.IndexStats[1].Histogram;

        // Range [250, 750] should estimate ~500 entries for uniform data
        long estimate = histogram.EstimateRange(250, 750);
        long expected = 500;
        double errorPct = Math.Abs(estimate - expected) / (double)expected;
        Assert.That(errorPct, Is.LessThan(0.05), $"Range estimate {estimate} too far from expected {expected}");
    }

    [Test]
    public void Histogram_EqualityEstimation_Reasonable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        for (var i = 0; i < 1000; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();
        var histogram = ct.IndexStats[1].Histogram;

        // Equality for any single value in a uniform distribution should estimate ~1
        long estimate = histogram.EstimateEquality(500);
        Assert.That(estimate, Is.GreaterThanOrEqualTo(1));
        Assert.That(estimate, Is.LessThan(20));
    }

    [Test]
    public void Histogram_BucketWidthZero_NoCrash()
    {
        // Create histogram where min == max (all same value)
        var bucketCounts = new int[Histogram.BucketCount];
        bucketCounts[0] = 50;
        var histogram = new Histogram(42, 42, bucketCounts, 50);

        Assert.That(histogram.BucketWidth, Is.EqualTo(0));
        Assert.That(histogram.GetBucket(42), Is.EqualTo(0));
        Assert.That(histogram.EstimateEquality(42), Is.EqualTo(50));
        Assert.That(histogram.EstimateEquality(99), Is.EqualTo(0));
        Assert.That(histogram.EstimateRange(40, 50), Is.EqualTo(50));
        Assert.That(histogram.EstimateRange(43, 50), Is.EqualTo(0));
    }

    [Test]
    public void Histogram_EmptyIndex_NullHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        ct.IndexStats[1].RebuildHistogram();
        Assert.That(ct.IndexStats[1].Histogram, Is.Null);
    }

    [Test]
    public void Histogram_AllSameValue_Degenerate_ViaIndex()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Single entity — min == max → degenerate histogram
        CreateAndCommit(dbe, 1.0f, 42, 1.0);

        ct.IndexStats[1].RebuildHistogram();
        var histogram = ct.IndexStats[1].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(1));
        Assert.That(histogram.BucketWidth, Is.EqualTo(0));
        Assert.That(histogram.EstimateEquality(42), Is.EqualTo(1));
        Assert.That(histogram.EstimateEquality(0), Is.EqualTo(0));
    }

    [Test]
    public void Histogram_AllowMultiple_CountsEntities_NotKeys()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompF>();

        // Gold is AllowMultiple: 50 entities with Gold=10, 30 with Gold=50, 20 with Gold=90
        for (var i = 0; i < 50; i++)
        {
            CreateAndCommitCompF(dbe, 10, i);
        }
        for (var i = 0; i < 30; i++)
        {
            CreateAndCommitCompF(dbe, 50, 50 + i);
        }
        for (var i = 0; i < 20; i++)
        {
            CreateAndCommitCompF(dbe, 90, 80 + i);
        }

        // Gold is field index 0
        ct.IndexStats[0].RebuildHistogram();
        var histogram = ct.IndexStats[0].Histogram;

        Assert.That(histogram, Is.Not.Null);
        // TotalCount must be 100 (entities), not 3 (distinct keys)
        Assert.That(histogram.TotalCount, Is.EqualTo(100));

        // The bucket containing Gold=10 should have weight 50
        int bucket10 = histogram.GetBucket(10);
        Assert.That(histogram.BucketCounts[bucket10], Is.EqualTo(50));

        // The bucket containing Gold=50 should have weight 30
        int bucket50 = histogram.GetBucket(50);
        Assert.That(histogram.BucketCounts[bucket50], Is.EqualTo(30));

        // The bucket containing Gold=90 should have weight 20
        int bucket90 = histogram.GetBucket(90);
        Assert.That(histogram.BucketCounts[bucket90], Is.EqualTo(20));
    }

    [Test]
    public void Basic_Range_AllowMultiple_UsesHistogramTotal()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompF>();
        var estimator = BasicSelectivityEstimator.Instance;

        // 50 entities with Gold=10, 50 entities with Gold=90
        for (var i = 0; i < 50; i++)
        {
            CreateAndCommitCompF(dbe, 10, i);
        }
        for (var i = 0; i < 50; i++)
        {
            CreateAndCommitCompF(dbe, 90, 50 + i);
        }

        // Without histogram: EntryCount = 2 (distinct keys), range estimate is tiny
        var beforeHistogram = estimator.EstimateCardinality(ct, 0, CompareOp.GreaterThan, 10);
        // EntryCount=2, range estimate = 2 * (90-11)/(90-10) ≈ 1 — severely underestimates
        Assert.That(beforeHistogram, Is.LessThanOrEqualTo(2));

        // Build histogram: now TotalCount = 100 (entities)
        ct.IndexStats[0].RebuildHistogram();
        var afterHistogram = estimator.EstimateCardinality(ct, 0, CompareOp.GreaterThan, 10);
        // With histogram total=100, range estimate = 100 * (90-11)/(90-10) ≈ 98
        Assert.That(afterHistogram, Is.GreaterThan(40));
    }

    #endregion

    #region AdvancedSelectivityEstimator Tests (Histogram fallback)

    [Test]
    public void HistoEstimator_FallsBackToBasic_WhenNoHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        CreateAndCommit(dbe, 1.0f, 10, 1.0);
        CreateAndCommit(dbe, 2.0f, 20, 2.0);
        CreateAndCommit(dbe, 3.0f, 30, 3.0);

        // No histogram built — should fall back to BasicSelectivityEstimator
        var result = estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 20);
        Assert.That(result, Is.EqualTo(1)); // Exact point lookup via basic fallback
    }

    [Test]
    public void HistoEstimator_UsesHistogram_WhenAvailable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        for (var i = 0; i < 100; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();

        // With histogram: B > 49 should estimate ~50
        var result = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 49);
        Assert.That(result, Is.InRange(30, 70));

        // B <= 49 should estimate ~50
        var lte = estimator.EstimateCardinality(ct, 1, CompareOp.LessThanOrEqual, 49);
        Assert.That(lte, Is.InRange(30, 70));
    }

    [Test]
    public void HistoEstimator_Equality_WithHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        for (var i = 0; i < 1000; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();

        // Equality estimate should be reasonable (≥1 since bucket is non-empty)
        var result = estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 500);
        Assert.That(result, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void HistoEstimator_NotEqual_WithHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        for (var i = 0; i < 100; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, 1.0);
        }

        ct.IndexStats[1].RebuildHistogram();

        // NotEqual should be close to total minus equality estimate
        var result = estimator.EstimateCardinality(ct, 1, CompareOp.NotEqual, 50);
        Assert.That(result, Is.InRange(90, 100));
    }

    #endregion

    #region Float/Double Histogram + Estimator Tests

    [Test]
    public void Histogram_FloatField_OrderPreserving_CorrectBuckets()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with A from 1.0f to 200.0f (all positive — OP encoding is monotonic)
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, i + 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var histogram = ct.IndexStats[0].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(200));

        // Estimate the upper half [100.0f, 200.0f] using order-preserving encoded bounds
        var lo = StatisticsRebuilder.ToOrderPreserving(FloatThreshold(100.0f), KeyType.Float);
        var hi = StatisticsRebuilder.ToOrderPreserving(FloatThreshold(200.0f), KeyType.Float);
        var estimate = histogram.EstimateRange(lo, hi);
        Assert.That(estimate, Is.InRange(50, 150));
    }

    [Test]
    public void Histogram_DoubleField_OrderPreserving_CorrectBuckets()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with C from 0.5 to 100.0 (i*0.5 + 0.5, all positive — OP encoding is monotonic)
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, i * 0.5 + 0.5);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var histogram = ct.IndexStats[2].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(200));

        // Estimate the upper half [50.0, 100.0] using order-preserving encoded bounds
        var lo = StatisticsRebuilder.ToOrderPreserving(DoubleThreshold(50.0), KeyType.Double);
        var hi = StatisticsRebuilder.ToOrderPreserving(DoubleThreshold(100.0), KeyType.Double);
        var estimate = histogram.EstimateRange(lo, hi);
        Assert.That(estimate, Is.InRange(50, 150));
    }

    [Test]
    public void Advanced_FloatField_RangeEstimate_WithHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Insert 200 entities with A from 1.0f to 200.0f (all positive — OP encoding is monotonic)
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, i + 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Estimate A > 100.0f — should be ~100 values in the upper half
        var result = estimator.EstimateCardinality(ct, 0, CompareOp.GreaterThan, FloatThreshold(100.0f));
        Assert.That(result, Is.InRange(50, 150));
    }

    [Test]
    public void Advanced_DoubleField_RangeEstimate_WithHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Insert 200 entities with C from 0.5 to 100.0 (i*0.5 + 0.5, all positive — OP encoding is monotonic)
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, i * 0.5 + 0.5);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Estimate C >= 50.0 — should be ~100 values in the upper half
        var result = estimator.EstimateCardinality(ct, 2, CompareOp.GreaterThanOrEqual, DoubleThreshold(50.0));
        Assert.That(result, Is.InRange(50, 150));
    }

    [Test]
    public void Advanced_FloatField_NegativeRange_WithHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Insert 200 entities with A from 1.0f to 200.0f (all positive — OP encoding is monotonic)
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, i + 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Estimate A < 101.0f — should be ~100 values in the lower half
        var result = estimator.EstimateCardinality(ct, 0, CompareOp.LessThan, FloatThreshold(101.0f));
        Assert.That(result, Is.InRange(50, 150));
    }

    [Test]
    public void Advanced_FloatField_Equality_UsesMCV_NotHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Insert 200 entities: 80 with A=42.0f, 120 with A=99.0f
        for (var i = 0; i < 80; i++)
        {
            CreateAndCommit(dbe, 42.0f, i, 1.0);
        }
        for (var i = 0; i < 120; i++)
        {
            CreateAndCommit(dbe, 99.0f, 80 + i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // MCV should have exact count for A=42.0f
        var result = estimator.EstimateCardinality(ct, 0, CompareOp.Equal, FloatThreshold(42.0f));
        Assert.That(result, Is.EqualTo(80));
    }

    [Test]
    public void Advanced_BoundaryOverflow_GreaterThanMaxLong_ReturnsZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        CreateAndCommit(dbe, 1.0f, 10, 1.0);
        CreateAndCommit(dbe, 2.0f, 20, 2.0);

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // B > long.MaxValue is impossible — should return 0
        var result = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, long.MaxValue);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void Advanced_BoundaryOverflow_LessThanMinLong_ReturnsZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        CreateAndCommit(dbe, 1.0f, 10, 1.0);
        CreateAndCommit(dbe, 2.0f, 20, 2.0);

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // B < long.MinValue is impossible — should return 0
        var result = estimator.EstimateCardinality(ct, 1, CompareOp.LessThan, long.MinValue);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void Histogram_FloatField_AllNegative_CorrectBuckets()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 100 entities with A from -100.0f to -1.0f
        for (var i = 0; i < 100; i++)
        {
            CreateAndCommit(dbe, i - 100.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var histogram = ct.IndexStats[0].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(100));

        // Order-preserving encoding should handle all-negative ranges correctly
        // Verify bucket counts sum to total
        var sumCounts = 0;
        for (var i = 0; i < Histogram.BucketCount; i++)
        {
            sumCounts += histogram.BucketCounts[i];
        }
        Assert.That(sumCounts, Is.EqualTo(100));
    }

    [Test]
    public void Histogram_FloatField_SingleValue_DegenerateCase()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 50 entities all with A=3.14f
        for (var i = 0; i < 50; i++)
        {
            CreateAndCommit(dbe, 3.14f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var histogram = ct.IndexStats[0].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(50));
        Assert.That(histogram.BucketWidth, Is.EqualTo(0));
    }

    [Test]
    public void Histogram_FloatField_CrossZero_CorrectOrdering()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with A spanning [-100.0f, +99.0f]
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, i - 100.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var histogram = ct.IndexStats[0].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(200));

        // Order-preserving min < max in signed long space
        Assert.That(histogram.MinValue, Is.LessThan(histogram.MaxValue),
            "OP-encoded min (-100.0f) must be < OP-encoded max (99.0f) in signed long comparison");

        // Bucket counts must sum to total
        var sumCounts = 0;
        for (var i = 0; i < Histogram.BucketCount; i++)
        {
            sumCounts += histogram.BucketCounts[i];
        }
        Assert.That(sumCounts, Is.EqualTo(200));

        // Range estimate for upper half [0.0f, 99.0f] should be ~100
        var lo = StatisticsRebuilder.ToOrderPreserving(FloatThreshold(0.0f), KeyType.Float);
        var hi = StatisticsRebuilder.ToOrderPreserving(FloatThreshold(99.0f), KeyType.Float);
        var estimate = histogram.EstimateRange(lo, hi);
        Assert.That(estimate, Is.InRange(50, 150));
    }

    [Test]
    public void Histogram_DoubleField_CrossZero_CorrectOrdering()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with C spanning [-50.0, +49.5]
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, 1.0f, i, i * 0.5 - 50.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var histogram = ct.IndexStats[2].Histogram;

        Assert.That(histogram, Is.Not.Null);
        Assert.That(histogram.TotalCount, Is.EqualTo(200));

        // Order-preserving min < max in signed long space
        Assert.That(histogram.MinValue, Is.LessThan(histogram.MaxValue),
            "OP-encoded min (-50.0) must be < OP-encoded max (49.5) in signed long comparison");

        // Range estimate for positive half [0.0, 49.5] should be ~100
        var lo = StatisticsRebuilder.ToOrderPreserving(DoubleThreshold(0.0), KeyType.Double);
        var hi = StatisticsRebuilder.ToOrderPreserving(DoubleThreshold(49.5), KeyType.Double);
        var estimate = histogram.EstimateRange(lo, hi);
        Assert.That(estimate, Is.InRange(50, 150));
    }

    [Test]
    public void Advanced_FloatField_CrossZero_RangeEstimate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Insert 200 entities with A spanning [-100.0f, +99.0f]
        for (var i = 0; i < 200; i++)
        {
            CreateAndCommit(dbe, i - 100.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Estimate A > 0.0f — should be ~100 values in the positive half
        var result = estimator.EstimateCardinality(ct, 0, CompareOp.GreaterThan, FloatThreshold(0.0f));
        Assert.That(result, Is.InRange(50, 150));

        // Estimate A < 0.0f — should be ~100 values in the negative half
        var resultNeg = estimator.EstimateCardinality(ct, 0, CompareOp.LessThan, FloatThreshold(0.0f));
        Assert.That(resultNeg, Is.InRange(50, 150));
    }

    #endregion

    #region Multi-predicate ordering

    [Test]
    public void MultiPredicate_LowestCardinalityFirstIdentified()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = BasicSelectivityEstimator.Instance;

        // B values: 10, 20, 30, 40, 50
        for (var i = 1; i <= 5; i++)
        {
            CreateAndCommit(dbe, (float)i, i * 10, (double)i);
        }

        // Predicate 1: B > 10 → should match 4 entities (20, 30, 40, 50)
        var cardGt10 = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 10);

        // Predicate 2: B == 30 → should match exactly 1
        var cardEq30 = estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 30);

        // The equality predicate should have lower cardinality
        Assert.That(cardEq30, Is.LessThan(cardGt10));
    }

    #endregion
}
