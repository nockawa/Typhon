using System;
using System.Collections.Concurrent;
using System.Linq;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
public sealed class ResourceRegistryMutationTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void Setup() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "MutationTests" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    [Test]
    public void RegisterChild_RaisesAddedEvent()
    {
        var events = new ConcurrentBag<ResourceMutationEventArgs>();
        _registry.NodeMutated += events.Add;

        var child = new ResourceNode("child-a", ResourceType.ComponentTable, _registry.DataEngine);

        var evt = events.FirstOrDefault(e => e.NodeId == "child-a");
        Assert.That(evt.Kind, Is.EqualTo(ResourceMutationKind.Added));
        Assert.That(evt.ParentId, Is.EqualTo(_registry.DataEngine.Id));
        Assert.That(evt.Type, Is.EqualTo(ResourceType.ComponentTable));
        Assert.That(evt.Timestamp, Is.Not.EqualTo(default(DateTime)));
        _ = child; // suppress unused
    }

    [Test]
    public void RemoveChild_RaisesRemovedEvent()
    {
        var events = new ConcurrentBag<ResourceMutationEventArgs>();
        var child = new ResourceNode("child-b", ResourceType.Segment, _registry.Storage);
        _registry.NodeMutated += events.Add; // subscribe AFTER creation

        _registry.Storage.RemoveChild(child);

        var evt = events.FirstOrDefault(e => e.NodeId == "child-b");
        Assert.That(evt.Kind, Is.EqualTo(ResourceMutationKind.Removed));
        Assert.That(evt.ParentId, Is.EqualTo(_registry.Storage.Id));
        Assert.That(evt.Type, Is.EqualTo(ResourceType.Segment));
    }

    [Test]
    public void DuplicateRegisterChild_DoesNotRaiseSecondEvent()
    {
        var events = new ConcurrentBag<ResourceMutationEventArgs>();
        _registry.NodeMutated += events.Add;

        _ = new ResourceNode("dup", ResourceType.Segment, _registry.Storage);
        // Attempt to re-register the same id — TryAdd returns false, event must not fire again
        var fake = new ResourceNode("other", ResourceType.Segment, _registry.Storage);
        var beforeCount = events.Count;
        _registry.Storage.RegisterChild(fake); // fake already registered by its ctor; this is a duplicate
        var afterCount = events.Count;

        Assert.That(afterCount, Is.EqualTo(beforeCount), "Duplicate RegisterChild should be a no-op and not raise a second event");
    }

    [Test]
    public void ThrowingSubscriber_DoesNotBreakOtherSubscribers()
    {
        var received = 0;
        _registry.NodeMutated += _ => throw new InvalidOperationException("bad subscriber");
        _registry.NodeMutated += _ => received++;

        _ = new ResourceNode("resilient", ResourceType.Segment, _registry.Storage);

        Assert.That(received, Is.EqualTo(1), "Healthy subscriber must still be invoked despite the faulty one");
    }

    [Test]
    public void UnsubscribedHandler_DoesNotReceiveFurtherEvents()
    {
        var received = 0;
        void handler(ResourceMutationEventArgs _) => received++;
        _registry.NodeMutated += handler;

        _ = new ResourceNode("sub-a", ResourceType.Segment, _registry.Storage);
        _registry.NodeMutated -= handler;
        _ = new ResourceNode("sub-b", ResourceType.Segment, _registry.Storage);

        Assert.That(received, Is.EqualTo(1));
    }

    [Test]
    public void Count_DefaultsToNullOnStructuralNodes()
    {
        // Structural nodes (Root, Storage, DataEngine, …) and plain user-created ResourceNode
        // instances don't surface a count — only subclasses that wrap a countable primitive
        // (ComponentTable, index, segment folder) override Count. This guards the default
        // so the Workbench's "entityCount?" DTO field stays null for non-countable rows.
        Assert.That(_registry.Root.Count, Is.Null);
        Assert.That(_registry.Storage.Count, Is.Null);
        Assert.That(_registry.DataEngine.Count, Is.Null);

        var plain = new ResourceNode("plain", ResourceType.Node, _registry.Storage);
        Assert.That(plain.Count, Is.Null);
    }

    [Test]
    public void Count_SubclassOverrideIsSurfaced()
    {
        // A subclass can override Count to expose a live scalar (ComponentTable does this for
        // EstimatedEntityCount). This test pins the contract with a simple test-only subclass so
        // the override path can't silently regress.
        var node = new CountingResourceNode("counting", _registry.DataEngine, initialCount: 42);
        Assert.That(node.Count, Is.EqualTo(42));

        node.SetCount(7);
        Assert.That(node.Count, Is.EqualTo(7));
    }

    private sealed class CountingResourceNode : ResourceNode
    {
        private int _count;
        public CountingResourceNode(string id, IResource parent, int initialCount)
            : base(id, ResourceType.ComponentTable, parent) => _count = initialCount;
        public override int? Count => _count;
        public void SetCount(int value) => _count = value;
    }
}
