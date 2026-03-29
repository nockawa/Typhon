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

        queue.Push(1);
        queue.Push(2);
        queue.Push(3);

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
    public void Push_WhenFull_Throws()
    {
        var queue = new EventQueue<int>("test", 4);

        queue.Push(1);
        queue.Push(2);
        queue.Push(3);
        queue.Push(4);

        Assert.Throws<InvalidOperationException>(() => queue.Push(5));
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

        queue.Push(10);
        queue.Push(20);
        Assert.That(queue.Count, Is.EqualTo(2));

        queue.Reset();
        Assert.That(queue.Count, Is.EqualTo(0));
        Assert.That(queue.IsEmpty, Is.True);

        // Can push again after reset
        queue.Push(30);
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void IsEmpty_ReflectsState()
    {
        var queue = new EventQueue<int>("test", 16);

        Assert.That(queue.IsEmpty, Is.True);
        queue.Push(1);
        Assert.That(queue.IsEmpty, Is.False);

        Span<int> output = stackalloc int[16];
        queue.Drain(output);
        Assert.That(queue.IsEmpty, Is.True);
    }

    [Test]
    public void AsSpan_ReturnsCurrentContents()
    {
        var queue = new EventQueue<int>("test", 16);

        queue.Push(42);
        queue.Push(99);

        var span = queue.AsSpan();
        Assert.That(span.Length, Is.EqualTo(2));
        Assert.That(span[0], Is.EqualTo(42));
        Assert.That(span[1], Is.EqualTo(99));
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

        queue.Push("hello");
        queue.Push("world");

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
        queue.Push(1);
        queue.Push(2);
        Span<int> output = stackalloc int[4];
        Assert.That(queue.Drain(output), Is.EqualTo(2));

        // Cycle 2 (after drain, can push again)
        queue.Push(3);
        queue.Push(4);
        queue.Push(5);
        Assert.That(queue.Drain(output), Is.EqualTo(3));
        Assert.That(output[0], Is.EqualTo(3));
    }
}
