using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #889 lead F. The Migrate phase's Prepare orders the pending queue by destination cell with <see cref="ArchetypeClusterState.RadixSortByDestCellKey"/>,
/// an LSD radix sort, where it used to run <c>Array.Sort</c> with a comparer. The sort's contract is what the slice planner carves on — ascending
/// <see cref="MigrationRequest.DestCellKey"/>, and now stable — and these pin it against a reference that has both properties.
/// </summary>
[TestFixture]
[NonParallelizable]   // one test flips the static sort switch
class MigrationDestCellRadixSortTests : TestBase<MigrationDestCellRadixSortTests>
{
    private ArchetypeClusterState NewState()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DirKeyAlpha>();
        dbe.RegisterComponentFromAccessor<DirKeyBeta>();
        dbe.InitializeArchetypes();
        return dbe._archetypeStates[DirKeyArch.Metadata.ArchetypeId].ClusterState;
    }

    private static MigrationRequest[] Build(Random rng, int count, Func<Random, int> key)
    {
        var items = new MigrationRequest[count + 7];   // slack past `count`, which the sort must leave untouched
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new MigrationRequest(sourceClusterChunkId: i, sourceSlotIndex: i & 63, destCellKey: key(rng), destClusterChunkId: rng.Next(100));
        }

        return items;
    }

    /// <summary>The reference: a STABLE order by key — LINQ's OrderBy is documented stable — so equal keys keep their enqueue order.</summary>
    private static MigrationRequest[] Reference(MigrationRequest[] items, int count)
        => items.Take(count).OrderBy(r => r.DestCellKey).ToArray();

    private static void AssertSameSequence(MigrationRequest[] actual, MigrationRequest[] expected, int count, string what)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.That(actual[i].DestCellKey, Is.EqualTo(expected[i].DestCellKey), $"{what}: key at {i}");
            Assert.That(actual[i].SourceClusterChunkId, Is.EqualTo(expected[i].SourceClusterChunkId),
                $"{what}: a stable sort keeps the enqueue order inside cell {expected[i].DestCellKey}, position {i}");
            Assert.That(actual[i].DestClusterChunkId, Is.EqualTo(expected[i].DestClusterChunkId), $"{what}: the whole request travels with its key at {i}");
        }
    }

    /// <summary>Keys spanning one, two and three 11-bit digits, so the pass loop runs one, two and three times and the ping-pong lands on either
    /// side.</summary>
    [TestCase(2_000, 1 << 11, TestName = "Keys_Within_One_Digit")]
    [TestCase(2_000, 1 << 20, TestName = "Keys_Within_Two_Digits")]
    [TestCase(2_000, int.MaxValue, TestName = "Keys_Over_The_Whole_Positive_Range")]
    [TestCase(50, 1 << 12, TestName = "A_Small_Queue")]
    public void Sorts_By_DestCellKey_Stably(int count, int keyRange)
    {
        var state = NewState();
        var rng = new Random(889 + keyRange);
        var items = Build(rng, count, r => r.Next(keyRange));
        var expected = Reference(items, count);
        var tail = items[count..];

        state.RadixSortByDestCellKey(items, count);

        AssertSameSequence(items, expected, count, $"range {keyRange}");
        for (var i = 0; i < tail.Length; i++)
        {
            Assert.That(items[count + i].SourceClusterChunkId, Is.EqualTo(tail[i].SourceClusterChunkId),
                $"entry {count + i}, past `count`, is not the sort's to touch");
            Assert.That(items[count + i].DestCellKey, Is.EqualTo(tail[i].DestCellKey), $"entry {count + i}, past `count`, is not the sort's to touch");
        }
    }

    /// <summary>
    /// Stability, witnessed where it matters: eight values per digit over three digits give 512 distinct keys for 4 000 requests, so nearly every key is
    /// shared and every pass has ties to keep in order. The range cases above prove ORDER; this one proves the enqueue order survives inside each cell.
    /// </summary>
    [Test]
    public void Colliding_Keys_Keep_Their_Enqueue_Order_Through_Every_Pass()
    {
        var state = NewState();
        var rng = new Random(512);
        var items = Build(rng, 4_000, r => (r.Next(8) << 22) | (r.Next(8) << 11) | r.Next(8));
        var expected = Reference(items, 4_000);

        state.RadixSortByDestCellKey(items, 4_000);

        AssertSameSequence(items, expected, 4_000, "colliding keys");
    }

    /// <summary>Negative keys order before positive ones, exactly as <see cref="int.CompareTo(int)"/> does — the sign flip is what makes a radix sort
    /// signed.</summary>
    [Test]
    public void Negative_Keys_Order_Before_Positive_Ones()
    {
        var state = NewState();
        var rng = new Random(7);
        var items = Build(rng, 3_000, r => r.Next(int.MinValue, int.MaxValue));
        var expected = Reference(items, 3_000);

        state.RadixSortByDestCellKey(items, 3_000);

        AssertSameSequence(items, expected, 3_000, "signed");
        Assert.That(items[0].DestCellKey, Is.LessThan(0), "sanity: the seed produced negative keys and they came first");
    }

    /// <summary>A queue whose requests all target one cell is already in order, and stability means it is left exactly as it was.</summary>
    [Test]
    public void One_Cell_Is_A_NoOp()
    {
        var state = NewState();
        var items = Build(new Random(1), 500, _ => 4242);
        var snapshot = (MigrationRequest[])items.Clone();

        state.RadixSortByDestCellKey(items, 500);

        for (var i = 0; i < 500; i++)
        {
            Assert.That(items[i].SourceClusterChunkId, Is.EqualTo(snapshot[i].SourceClusterChunkId), $"position {i}");
        }
    }

    /// <summary>Keys that share every low digit and differ only high up: the low passes are skipped, the high one still sorts.</summary>
    [Test]
    public void Passes_Whose_Digit_Is_Constant_Are_Skipped_Without_Losing_The_Order()
    {
        var state = NewState();
        var rng = new Random(3);
        var items = Build(rng, 1_000, r => (r.Next(64) << 22) | 0x1555);   // low 22 bits identical, only the top digit varies
        var expected = Reference(items, 1_000);

        state.RadixSortByDestCellKey(items, 1_000);

        AssertSameSequence(items, expected, 1_000, "high-digit-only");
    }

    /// <summary>The queue grows across ticks; the sort's scratch must follow it rather than sort against a stale, shorter partner.</summary>
    [Test]
    public void Scratch_Grows_With_The_Queue()
    {
        var state = NewState();
        var rng = new Random(11);
        var small = Build(rng, 100, r => r.Next(1 << 16));
        state.RadixSortByDestCellKey(small, 100);
        AssertSameSequence(small, Reference(small, 100), 100, "first, small");

        var large = Build(rng, 20_000, r => r.Next(1 << 16));
        var expected = Reference(large, 20_000);
        state.RadixSortByDestCellKey(large, 20_000);
        AssertSameSequence(large, expected, 20_000, "then, twenty times larger");
    }

    /// <summary>Through the public entry point: the queue itself, and the switch that keeps the comparison sort for the harness A/B.</summary>
    [Test]
    public void SortPendingMigrationsByDestCellKey_OrdersTheQueue_UnderEitherSort()
    {
        var state = NewState();
        var rng = new Random(5);
        var was = ArchetypeClusterState.UseRadixDestCellSort;
        try
        {
            foreach (var radix in new[] { true, false })
            {
                ArchetypeClusterState.UseRadixDestCellSort = radix;
                var items = Build(rng, 1_500, r => r.Next(1 << 18));
                state.PendingMigrations = items;
                state.PendingMigrationCount = 1_500;

                state.SortPendingMigrationsByDestCellKey();

                var keys = new List<int>(1_500);
                for (var i = 0; i < 1_500; i++)
                {
                    keys.Add(state.PendingMigrations[i].DestCellKey);
                }

                Assert.That(keys, Is.Ordered.Ascending, $"radix={radix}: the queue must be ascending by destination cell");
            }
        }
        finally
        {
            ArchetypeClusterState.UseRadixDestCellSort = was;
            state.PendingMigrations = null;
            state.PendingMigrationCount = 0;
        }
    }
}
