using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class TickTelemetryRingTests
{
    [Test]
    public void NewRing_HasNoTicks()
    {
        var ring = new TickTelemetryRing(16, 3);

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(0));
        Assert.That(ring.OldestAvailableTick, Is.EqualTo(-1));
        Assert.That(ring.NewestTick, Is.EqualTo(-1));
    }

    [Test]
    public void RecordAndRead_SingleTick()
    {
        var ring = new TickTelemetryRing(16, 2);
        var tick = new TickTelemetry { TickNumber = 0, ActualDurationMs = 1.5f, ActiveSystemCount = 2 };
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[2];
        systems[0] = new SystemTelemetry { SystemIndex = 0, TransitionLatencyUs = 0.3f };
        systems[1] = new SystemTelemetry { SystemIndex = 1, DurationUs = 100f };

        ring.Record(in tick, systems);

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(1));
        Assert.That(ring.OldestAvailableTick, Is.EqualTo(0));
        Assert.That(ring.NewestTick, Is.EqualTo(0));

        ref readonly var readTick = ref ring.GetTick(0);
        Assert.That(readTick.ActualDurationMs, Is.EqualTo(1.5f));
        Assert.That(readTick.ActiveSystemCount, Is.EqualTo(2));

        var readSystems = ring.GetSystemMetrics(0);
        Assert.That(readSystems.Length, Is.EqualTo(2));
        Assert.That(readSystems[0].TransitionLatencyUs, Is.EqualTo(0.3f));
        Assert.That(readSystems[1].DurationUs, Is.EqualTo(100f));
    }

    [Test]
    public void RingWraps_OldestOverwritten()
    {
        const int capacity = 4;
        var ring = new TickTelemetryRing(capacity, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];

        // Write 6 ticks into a ring of capacity 4
        for (var i = 0; i < 6; i++)
        {
            var tick = new TickTelemetry { TickNumber = i, ActualDurationMs = i * 1.0f };
            systems[0] = new SystemTelemetry { SystemIndex = 0, DurationUs = i * 10f };
            ring.Record(in tick, systems);
        }

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(6));
        Assert.That(ring.OldestAvailableTick, Is.EqualTo(2)); // 6 - 4 = 2
        Assert.That(ring.NewestTick, Is.EqualTo(5));

        // Ticks 0 and 1 are overwritten
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(1));

        // Ticks 2-5 are available
        for (var i = 2; i <= 5; i++)
        {
            ref readonly var t = ref ring.GetTick(i);
            Assert.That(t.TickNumber, Is.EqualTo(i));
            Assert.That(t.ActualDurationMs, Is.EqualTo(i * 1.0f));
        }
    }

    [Test]
    public void MultipleTicks_ReadBackConsistent()
    {
        var ring = new TickTelemetryRing(64, 3);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[3];

        for (var i = 0; i < 50; i++)
        {
            var tick = new TickTelemetry
            {
                TickNumber = i,
                ActualDurationMs = i * 0.5f,
                TargetDurationMs = 16.67f,
                OverrunRatio = (i * 0.5f) / 16.67f,
                ActiveWorkerCount = 8,
                ActiveSystemCount = 3
            };

            for (var s = 0; s < 3; s++)
            {
                systems[s] = new SystemTelemetry
                {
                    SystemIndex = s,
                    TransitionLatencyUs = s * 0.1f + i * 0.01f,
                    DurationUs = s * 100f + i
                };
            }

            ring.Record(in tick, systems);
        }

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(50));

        // Verify last tick
        ref readonly var last = ref ring.GetTick(49);
        Assert.That(last.TickNumber, Is.EqualTo(49));
        Assert.That(last.ActiveSystemCount, Is.EqualTo(3));

        var lastSystems = ring.GetSystemMetrics(49);
        Assert.That(lastSystems[2].SystemIndex, Is.EqualTo(2));
    }

    [Test]
    public void GetTick_FutureTick_Throws()
    {
        var ring = new TickTelemetryRing(16, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];
        ring.Record(new TickTelemetry { TickNumber = 0 }, systems);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(1));
    }

    [Test]
    public void GetTick_NoTicksRecorded_Throws()
    {
        var ring = new TickTelemetryRing(16, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(0));
    }

    [Test]
    public void Capacity_MustBePowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new TickTelemetryRing(3, 1));
        Assert.Throws<ArgumentException>(() => new TickTelemetryRing(0, 1));
        Assert.DoesNotThrow(() => new TickTelemetryRing(1, 1));
        Assert.DoesNotThrow(() => new TickTelemetryRing(1024, 1));
    }

    [Test]
    public void ExactCapacityFill_AllReadable()
    {
        const int capacity = 8;
        var ring = new TickTelemetryRing(capacity, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];

        for (var i = 0; i < capacity; i++)
        {
            ring.Record(new TickTelemetry { TickNumber = i }, systems);
        }

        Assert.That(ring.OldestAvailableTick, Is.EqualTo(0));
        Assert.That(ring.NewestTick, Is.EqualTo(7));

        for (var i = 0; i < capacity; i++)
        {
            Assert.That(ring.GetTick(i).TickNumber, Is.EqualTo(i));
        }
    }
}
