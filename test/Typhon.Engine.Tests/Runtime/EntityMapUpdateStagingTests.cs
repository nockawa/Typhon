using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <c>EntityMapUpdateStaging</c> — the per-Migrate-chunk buffers feeding the tick fence's EntityMapUpdate phase (#872 step 7).
/// </summary>
/// <remarks>
/// <b>This fixture exists because review found the multi-run merge was dead under test.</b> A probe throwing on <c>runCount &gt; 1</c> ran the entire
/// 5 694-test suite with no failure: reaching the staging through a fence tick produces ONE run at unit-test batch sizes, so <c>MergeTwo</c>, the odd-run
/// carry, the ping-pong buffer swap and <c>EnsureRunArrays</c> were never executed. They are the subtlest code in the change, and a wrong result there
/// surfaces as an entity resolving to a cluster slot it no longer occupies — a silent wrong answer, not a crash.
/// </remarks>
[TestFixture]
class EntityMapUpdateStagingTests
{
    private static EntityLocationUpdate Entry(long key, int bucket, int dstChunkId, int dstSlot) => new()
    {
        EntityKey = key,
        Bucket = bucket,
        DstChunkId = dstChunkId,
        DstSlot = dstSlot,
    };

    /// <summary>Fills <paramref name="runs"/> chunks with interleaved buckets, sorts each as its Migrate worker would, and merges.</summary>
    private static EntityMapUpdateStaging StageAndMerge(int runs, int perRun, out int merged)
    {
        var staging = new EntityMapUpdateStaging();
        staging.BeginTick(runs);

        for (var r = 0; r < runs; r++)
        {
            for (var i = 0; i < perRun; i++)
            {
                // Interleaved so no run is a prefix of the result: a merge that merely concatenated would be caught.
                staging.Add(r, Entry(key: r * 1000 + i, bucket: i * runs + r, dstChunkId: r, dstSlot: i));
            }

            staging.SortChunk(r);
        }

        merged = staging.MergeAndPartition(desiredParts: 4);
        return staging;
    }

    [Test]
    [CancelAfter(15_000)]
    public void MergeAndPartition_FiveRuns_ProducesOneRunSortedByBucket()
    {
        // Five, not four: an odd run count is what exercises the carry branch, where the leftover run is copied across untouched so the next pass sees it as
        // one run. An even count never reaches it.
        const int Runs = 5;
        const int PerRun = 7;

        var staging = StageAndMerge(Runs, PerRun, out var merged);
        Assert.That(merged, Is.EqualTo(Runs * PerRun), "the merge must neither drop nor duplicate an entry");

        var batch = staging.Prepared;
        var seen = new List<(long Key, int Bucket)>();
        for (var i = 0; i < merged; i++)
        {
            if (i > 0)
            {
                Assert.That(batch[i].Bucket, Is.GreaterThanOrEqualTo(batch[i - 1].Bucket),
                    $"the merged batch must be non-decreasing by bucket — the apply's run detection reads exactly this. Broke at index {i}.");
            }

            seen.Add((batch[i].EntityKey, batch[i].Bucket));
        }

        var expected = new List<(long Key, int Bucket)>();
        for (var r = 0; r < Runs; r++)
        {
            for (var i = 0; i < PerRun; i++)
            {
                expected.Add((r * 1000 + i, i * Runs + r));
            }
        }

        expected.Sort((a, b) => a.Key.CompareTo(b.Key));
        var sortedSeen = new List<(long Key, int Bucket)>(seen);
        sortedSeen.Sort((a, b) => a.Key.CompareTo(b.Key));
        Assert.That(sortedSeen, Is.EqualTo(expected), "every staged entry must survive the merge with its fields intact");
    }

    [Test]
    [CancelAfter(15_000)]
    public void MergeAndPartition_EntriesSharingABucket_KeepTheirRunOrder()
    {
        // Stability. The merge does not NEED it — an entity migrates at most once per tick, so no two entries share a key — but the file's own argument is
        // that a stable merge makes the batch a pure function of the runs rather than of the chunk count, which is what keeps a result reproducible across
        // worker counts. Nothing else asserts it.
        var staging = new EntityMapUpdateStaging();
        staging.BeginTick(4);
        for (var r = 0; r < 4; r++)
        {
            staging.Add(r, Entry(key: r, bucket: 42, dstChunkId: r, dstSlot: 0));
            staging.SortChunk(r);
        }

        var merged = staging.MergeAndPartition(desiredParts: 2);
        Assert.That(merged, Is.EqualTo(4));

        var batch = staging.Prepared;
        for (var i = 0; i < merged; i++)
        {
            Assert.That(batch[i].EntityKey, Is.EqualTo(i),
                "entries sharing a bucket must come out in the order their runs were gathered, so the batch does not depend on the chunk count");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void MergeAndPartition_SingleRun_IsReturnedWithoutMerging()
    {
        // The shortcut the multi-run path must not regress: one non-empty run is already sorted, so it is published as-is.
        var staging = new EntityMapUpdateStaging();
        staging.BeginTick(3);
        staging.Add(1, Entry(key: 7, bucket: 9, dstChunkId: 1, dstSlot: 3));
        staging.Add(1, Entry(key: 8, bucket: 2, dstChunkId: 1, dstSlot: 4));
        staging.SortChunk(1);

        var merged = staging.MergeAndPartition(desiredParts: 1);

        Assert.That(merged, Is.EqualTo(2));
        Assert.That(staging.Prepared[0].Bucket, Is.EqualTo(2));
        Assert.That(staging.Prepared[1].Bucket, Is.EqualTo(9));
    }

    [Test]
    [CancelAfter(15_000)]
    public void BeginTick_ClearsLastTicksEntries()
    {
        // The phase reads StagedCount without a tick stamp, so a tick that stages nothing must see nothing. If this ever regresses the previous tick's
        // location patches are re-applied against slots that may since have been reused.
        var staging = new EntityMapUpdateStaging();
        staging.BeginTick(2);
        staging.Add(0, Entry(key: 1, bucket: 1, dstChunkId: 0, dstSlot: 0));
        staging.Add(1, Entry(key: 2, bucket: 2, dstChunkId: 1, dstSlot: 0));
        Assert.That(staging.MergeAndPartition(1), Is.EqualTo(2), "sanity: the first tick staged two entries");

        staging.BeginTick(2);
        Assert.That(staging.StagedCount(), Is.Zero, "a fresh tick must start empty");
        Assert.That(staging.MergeAndPartition(1), Is.Zero, "an empty tick must produce nothing to dispatch");
        Assert.That(staging.PartCount, Is.Zero);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Add_PastThisTicksChunkCapacity_ThrowsRatherThanDroppingTheMigrant()
    {
        // Silent otherwise: the entry lands in a slot StagedCount never sums, so the migrant keeps a stale EntityMap record and nothing reports it.
        var staging = new EntityMapUpdateStaging();
        staging.BeginTick(2);

        Assert.Throws<InvalidOperationException>(() => staging.Add(5, Entry(key: 1, bucket: 1, dstChunkId: 0, dstSlot: 0)));
    }

    [Test]
    [CancelAfter(15_000)]
    public void StagingPath_AllocatesNothingOnceWarm()
    {
        // AC-7.4 covers UpdateValuesBulk; this covers the half that actually runs once per migrant per tick. Every buffer here grows geometrically, so a
        // steady-state tick must settle to zero — a non-geometric grow would reallocate ~2.3 MB of LOH per tick at the design's batch size.
        const int Runs = 8;
        const int PerRun = 64;

        var staging = new EntityMapUpdateStaging();

        // Three ticks at N, then ONE at N+1. That last one legitimately allocates — the ping-pong buffers were sized to exactly N by their first allocation,
        // so N+1 doubles them to 2N. Measuring there would fail against correct geometric code; measuring at N+2, inside the headroom the doubling just
        // bought, is what separates "grows geometrically" from "sizes to exactly what is needed and reallocates on every tick that grows at all".
        for (var warm = 0; warm < 4; warm++)
        {
            var n = warm < 3 ? PerRun : PerRun + 1;
            staging.BeginTick(Runs);
            for (var r = 0; r < Runs; r++)
            {
                for (var i = 0; i < n; i++)
                {
                    staging.Add(r, Entry(key: r * 1000 + i, bucket: i * Runs + r, dstChunkId: r, dstSlot: i));
                }

                staging.SortChunk(r);
            }

            staging.MergeAndPartition(4);
        }

        // The measured tick is larger again, and sits inside the headroom the growth tick above bought. Measuring the SAME size twice would pass under both
        // implementations and prove nothing.
        var before = GC.GetAllocatedBytesForCurrentThread();
        staging.BeginTick(Runs);
        for (var r = 0; r < Runs; r++)
        {
            for (var i = 0; i < PerRun + 2; i++)
            {
                staging.Add(r, Entry(key: r * 1000 + i, bucket: i * Runs + r, dstChunkId: r, dstSlot: i));
            }

            staging.SortChunk(r);
        }

        staging.MergeAndPartition(4);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, $"a steady-state tick must allocate nothing on the staging path — got {allocated} bytes");
    }
}
