using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Per-tick telemetry accumulators on <see cref="EventQueueBase"/> (#311). Push/Drain update PeakDepth, Produced,
/// Consumed, OverflowCount; Reset clears them at tick start.
/// </summary>
[TestFixture]
public sealed class EventQueueTelemetryTests
{
    private struct DamageEvent { public int Amount; }

    [Test]
    public void StartsZero_OnFreshQueue()
    {
        var q = new EventQueue<DamageEvent>("Damage", capacity: 64);
        Assert.That(q.PeakDepth, Is.EqualTo(0u));
        Assert.That(q.Produced, Is.EqualTo(0u));
        Assert.That(q.Consumed, Is.EqualTo(0u));
        Assert.That(q.OverflowCount, Is.EqualTo(0u));
    }

    [Test]
    public void Push_BumpsProduced_AndPeakDepth()
    {
        var q = new EventQueue<DamageEvent>("Damage", capacity: 64);
        q.Push(0, new DamageEvent { Amount = 1 });
        q.Push(0, new DamageEvent { Amount = 2 });
        q.Push(0, new DamageEvent { Amount = 3 });
        Assert.That(q.Produced, Is.EqualTo(3u));
        Assert.That(q.PeakDepth, Is.EqualTo(3u));
    }

    [Test]
    public void Drain_BumpsConsumed_DoesNotResetPeak()
    {
        var q = new EventQueue<DamageEvent>("Damage", capacity: 64);
        for (var i = 0; i < 5; i++) q.Push(0, new DamageEvent { Amount = i });
        var buf = new DamageEvent[5];
        var drained = q.Drain(buf);
        Assert.That(drained, Is.EqualTo(5));
        Assert.That(q.Consumed, Is.EqualTo(5u));
        Assert.That(q.PeakDepth, Is.EqualTo(5u), "peak persists for the whole tick — only Reset clears it");
        Assert.That(q.Produced, Is.EqualTo(5u));
    }

    [Test]
    public void Push_AfterDrainInSameTick_ContinuesToTrackPeak()
    {
        var q = new EventQueue<DamageEvent>("Damage", capacity: 64);
        for (var i = 0; i < 3; i++) q.Push(0, new DamageEvent { Amount = i });
        var buf = new DamageEvent[3];
        q.Drain(buf);
        // After drain, push more — peak should NOT regress to current count.
        q.Push(0, new DamageEvent { Amount = 100 });
        Assert.That(q.PeakDepth, Is.EqualTo(3u), "peak is high-water-mark across the tick, not a snapshot of current depth");
    }

    [Test]
    public void Push_AtCeiling_BumpsOverflowCount_WithoutThrowing()
    {
        var q = new EventQueue<DamageEvent>("Tiny", capacity: 2, allowGrowth: false);
        q.Push(0, new DamageEvent { Amount = 1 });
        q.Push(0, new DamageEvent { Amount = 2 });

        Assert.That(q.Push(0, new DamageEvent { Amount = 3 }), Is.False);
        Assert.That(q.OverflowCount, Is.EqualTo(1u), "OverflowCount still means 'events were lost' — the Workbench DAG paints it red");
        Assert.That(q.Produced, Is.EqualTo(2u), "a dropped push does not count toward Produced");
    }

    [Test]
    public void Reset_ClearsAllAccumulators()
    {
        var q = new EventQueue<DamageEvent>("Damage", capacity: 4, allowGrowth: false);
        q.Push(0, new DamageEvent());
        q.Push(0, new DamageEvent());
        q.Push(0, new DamageEvent());
        q.Push(0, new DamageEvent());
        q.Push(0, new DamageEvent());   // dropped at the ceiling — Reset must clear OverflowCount too
        var buf = new DamageEvent[4];
        q.Drain(buf);

        q.Reset();
        Assert.That(q.PeakDepth, Is.EqualTo(0u));
        Assert.That(q.Produced, Is.EqualTo(0u));
        Assert.That(q.Consumed, Is.EqualTo(0u));
        Assert.That(q.OverflowCount, Is.EqualTo(0u));
    }
}
