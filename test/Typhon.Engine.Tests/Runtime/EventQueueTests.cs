using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class EventQueueTests
{
    [Test]
    public void Push_Drain_RoundTrip()
    {
        var queue = new EventQueue<int>("test", 16);

        queue.Push(0, 1);
        queue.Push(0, 2);
        queue.Push(0, 3);

        Assert.That(queue.Count, Is.EqualTo(3));
        Assert.That(queue.IsEmpty, Is.False);

        Span<int> output = stackalloc int[16];
        var count = queue.Drain(output);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(output[0], Is.EqualTo(1));
        Assert.That(output[1], Is.EqualTo(2));
        Assert.That(output[2], Is.EqualTo(3));
        Assert.That(queue.IsEmpty, Is.True);
    }

    [Test]
    public void Drain_WhenEmpty_ReturnsZero()
    {
        var queue = new EventQueue<int>("test", 16);

        Span<int> output = stackalloc int[16];
        var count = queue.Drain(output);

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Reset_ClearsAllItems()
    {
        var queue = new EventQueue<int>("test", 16);

        queue.Push(0, 10);
        queue.Push(0, 20);
        Assert.That(queue.Count, Is.EqualTo(2));

        queue.Reset();
        Assert.That(queue.Count, Is.EqualTo(0));
        Assert.That(queue.IsEmpty, Is.True);

        // Can push again after reset
        queue.Push(0, 30);
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void IsEmpty_ReflectsState()
    {
        var queue = new EventQueue<int>("test", 16);

        Assert.That(queue.IsEmpty, Is.True);
        queue.Push(0, 1);
        Assert.That(queue.IsEmpty, Is.False);

        Span<int> output = stackalloc int[16];
        queue.Drain(output);
        Assert.That(queue.IsEmpty, Is.True);
    }

    [Test]
    [VerifiesRule("EQ-03")]
    public void Push_WhenAtCeiling_DropsAndCounts_NeverThrows()
    {
        // allowGrowth: false pins the queue at its initial allocation, which is what makes the ceiling reachable in a unit test.
        var queue = new EventQueue<int>("test", 4, allowGrowth: false);

        for (var i = 0; i < 4; i++)
        {
            Assert.That(queue.Push(0, i), Is.True);
        }

        // Never throws: Push runs inside parallel chunks, where an exception becomes a system failure and can abort the tick (#567).
        Assert.That(queue.Push(0, 99), Is.False, "a push at the ceiling reports the drop rather than throwing");

        Assert.Multiple(() =>
        {
            Assert.That(queue.OverflowCount, Is.EqualTo(1u));
            Assert.That(queue.Produced, Is.EqualTo(4u), "a dropped push does not count toward Produced");
            Assert.That(queue.Count, Is.EqualTo(4));
        });
    }

    /// <summary>
    /// A queue sized for N workers: each segment starts at capacity/N, so the doubling path is actually reachable.
    /// </summary>
    /// <remarks>
    /// On the single-slot default, <c>initial == requested == ceiling</c> and the doubling branch is arithmetically dead — growth tests written against
    /// it pass with <c>Array.Resize</c> deleted. Growth only ever runs with two or more slots, which is every real runtime.
    /// </remarks>
    private static EventQueue<int> MultiSlotQueue(string name, int capacity, int slots, bool allowGrowth = true)
    {
        var queue = new EventQueue<int>(name, capacity, allowGrowth);
        queue.BindWorkerSlots(slots);
        return queue;
    }

    [Test]
    public void Push_PastInitialSegmentSize_GrowsRatherThanDropping()
    {
        // 256 over 4 slots -> each segment starts at 64 and must double to hold 256.
        var queue = MultiSlotQueue("test", capacity: 256, slots: 4);
        var initial = queue.AllocatedCapacity;

        for (var i = 0; i < 256; i++)
        {
            Assert.That(queue.Push(0, i), Is.True);
        }

        Assert.Multiple(() =>
        {
            Assert.That(queue.Count, Is.EqualTo(256));
            Assert.That(queue.OverflowCount, Is.Zero, "growth means the ceiling was never reached");
            Assert.That(queue.AllocatedCapacity, Is.GreaterThan(initial), "the segment must actually have grown — otherwise this test proves nothing");
        });

        var output = new int[256];
        Assert.That(queue.Drain(output), Is.EqualTo(256));
        Assert.Multiple(() =>
        {
            Assert.That(output[0], Is.Zero, "events written before the grow survive the copy");
            Assert.That(output[255], Is.EqualTo(255));
        });
    }

    [Test]
    public void GrownCapacity_SurvivesReset()
    {
        var queue = MultiSlotQueue("test", capacity: 256, slots: 4);
        var initial = queue.AllocatedCapacity;
        for (var i = 0; i < 256; i++)
        {
            queue.Push(0, i);
        }

        var grown = queue.AllocatedCapacity;
        Assert.That(grown, Is.GreaterThan(initial), "precondition: the segment grew");

        queue.Reset();

        // The high-water allocation is the point of growth: a workload reaches its working set in a few ticks and then stops allocating.
        Assert.That(queue.AllocatedCapacity, Is.EqualTo(grown));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    [VerifiesRule("EQ-05")]
    public void Capacity_IsTheConstructionConstant_NotTheLiveAllocation()
    {
        // The profiler builds its one-shot EventQueueRecord catalog inside TyphonRuntime.Create — before any push, so before any segment exists.
        // A fold over live buffers reported 0 for every queue in every trace, and the Workbench divides per-tick depth by this.
        var queue = MultiSlotQueue("fresh", capacity: 1024, slots: 8);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Capacity, Is.EqualTo(1024), "Capacity is a static schema fact");
            Assert.That(queue.AllocatedCapacity, Is.Zero, "nothing is allocated until the first push into a slot");
        });
    }

    [Test]
    [VerifiesRule("EQ-01")]
    public void SecondWriterOnASlot_SeesAGrowthPerformedByTheFirst()
    {
        // Regression: EventWriter used to cache the segment's T[] by value. Two live writers on one slot — a second ctx.Writer, or a copy passed to a
        // helper — left one holding an orphaned array once the other grew the segment. The bounds check hides it while Count stays above the stale
        // length, but a Drain resets Count to 0 and the stale writer's fast path then succeeds into the orphan: the event is lost and an
        // already-consumed one is delivered in its place, with Count and Produced both agreeing so telemetry shows nothing.
        var queue = MultiSlotQueue("alias", capacity: 256, slots: 4);

        var stale = queue.GetWriter(0);
        stale.Push(-1);

        // A different writer on the same slot drives the segment past its initial size, replacing the buffer.
        var grower = queue.GetWriter(0);
        for (var i = 0; i < 200; i++)
        {
            grower.Push(i);
        }

        var buf = new int[256];
        var firstBatch = queue.Drain(buf);
        Assert.That(firstBatch, Is.EqualTo(201));

        // The stale writer must land in the CURRENT buffer, not the orphaned one it was constructed against.
        Assert.That(stale.Push(999), Is.True);

        var secondBatch = queue.Drain(buf);
        Assert.Multiple(() =>
        {
            Assert.That(secondBatch, Is.EqualTo(1));
            Assert.That(buf[0], Is.EqualTo(999), "the stale writer wrote into an orphaned buffer — a consumed event was re-delivered");
        });
    }

    [Test]
    [VerifiesRule("EQ-03")]
    public void Drain_IntoAShortSpan_Throws()
    {
        var queue = new EventQueue<int>("test", 16);
        queue.Push(0, 1);
        queue.Push(0, 2);
        queue.Push(0, 3);

        // Truncating instead would be drain-side loss that OverflowCount does not count and the Workbench cannot show.
        var tiny = new int[2];
        Assert.Throws<ArgumentException>(() => queue.Drain(tiny));
        Assert.That(queue.Count, Is.EqualTo(3), "a rejected drain leaves the queue intact");
    }

    [Test]
    public void PeakDepth_SurvivesAFullDrain_AndIsNotSummedAcrossSlots()
    {
        var queue = MultiSlotQueue("peak", capacity: 256, slots: 4);

        // Slot 0 reaches 10, drained; then slot 1 reaches 10, drained. The queue never held more than 10 at once.
        for (var i = 0; i < 10; i++)
        {
            queue.Push(0, i);
        }

        var buf = new int[10];
        queue.Drain(buf);

        for (var i = 0; i < 10; i++)
        {
            queue.Push(1, i);
        }

        queue.Drain(buf);

        Assert.That(queue.PeakDepth, Is.EqualTo(10u), "summing per-slot maxima observed at unrelated instants reported 20");
    }

    [Test]
    public void Name_ReturnsConfiguredName()
    {
        var queue = new EventQueue<int>("LootEvents", 16);
        Assert.That(queue.Name, Is.EqualTo("LootEvents"));
    }

    [Test]
    public void Capacity_MustBePowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new EventQueue<int>("test", 3));
        Assert.Throws<ArgumentException>(() => new EventQueue<int>("test", 0));
        Assert.DoesNotThrow(() => new EventQueue<int>("test", 1));
        Assert.DoesNotThrow(() => new EventQueue<int>("test", 1024));
    }

    [Test]
    public void ReferenceType_ClearsOnDrainAndReset()
    {
        var queue = new EventQueue<string>("test", 8);

        queue.Push(0, "hello");
        queue.Push(0, "world");

        Span<string> output = new string[8];
        var count = queue.Drain(output);

        Assert.That(count, Is.EqualTo(2));
        Assert.That(output[0], Is.EqualTo("hello"));
        Assert.That(output[1], Is.EqualTo("world"));
    }

    [Test]
    public void MultiplePushDrainCycles()
    {
        var queue = new EventQueue<int>("test", 4);

        // Cycle 1
        queue.Push(0, 1);
        queue.Push(0, 2);
        Span<int> output = stackalloc int[4];
        Assert.That(queue.Drain(output), Is.EqualTo(2));

        // Cycle 2 (after drain, can push again)
        queue.Push(0, 3);
        queue.Push(0, 4);
        queue.Push(0, 5);
        Assert.That(queue.Drain(output), Is.EqualTo(3));
        Assert.That(output[0], Is.EqualTo(3));
    }
}
