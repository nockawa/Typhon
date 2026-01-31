using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Resources;

[TestFixture]
public class ResourceTreeTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void Setup() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TestRegistry" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    #region Basic Tree Structure Tests

    [Test]
    public void Registry_HasCorrectSubsystemNodes()
    {
        Assert.That(_registry.Root, Is.Not.Null);
        Assert.That(_registry.Storage, Is.Not.Null);
        Assert.That(_registry.DataEngine, Is.Not.Null);
        Assert.That(_registry.Durability, Is.Not.Null);
        Assert.That(_registry.Allocation, Is.Not.Null);
    }

    [Test]
    public void Root_HasFourChildren()
    {
        var children = _registry.Root.Children.ToList();
        Assert.That(children, Has.Count.EqualTo(4));
        Assert.That(children.Select(c => c.Id), Is.EquivalentTo(new[] { "Storage", "DataEngine", "Durability", "Allocation" }));
    }

    [Test]
    public void SubsystemNodes_HaveRootAsParent()
    {
        Assert.That(_registry.Storage.Parent, Is.SameAs(_registry.Root));
        Assert.That(_registry.DataEngine.Parent, Is.SameAs(_registry.Root));
        Assert.That(_registry.Durability.Parent, Is.SameAs(_registry.Root));
        Assert.That(_registry.Allocation.Parent, Is.SameAs(_registry.Root));
    }

    [Test]
    public void Root_HasNoParent() => Assert.That(_registry.Root.Parent, Is.Null);

    #endregion

    #region GetSubsystem Tests

    [Test]
    public void GetSubsystem_ReturnsCorrectNode()
    {
        Assert.That(_registry.GetSubsystem(ResourceSubsystem.Storage), Is.SameAs(_registry.Storage));
        Assert.That(_registry.GetSubsystem(ResourceSubsystem.DataEngine), Is.SameAs(_registry.DataEngine));
        Assert.That(_registry.GetSubsystem(ResourceSubsystem.Durability), Is.SameAs(_registry.Durability));
        Assert.That(_registry.GetSubsystem(ResourceSubsystem.Allocation), Is.SameAs(_registry.Allocation));
    }

    [Test]
    public void GetSubsystem_InvalidSubsystem_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => _registry.GetSubsystem((ResourceSubsystem)999));

    #endregion

    #region Registration Tests

    [Test]
    public void Register_AddsResourceToSubsystem()
    {
        var node = new ResourceNode("TestNode", ResourceType.Node, _registry.DataEngine);
        _registry.Register(node, ResourceSubsystem.DataEngine);

        Assert.That(_registry.DataEngine.Children, Contains.Item(node));
    }

    [Test]
    public void RegisterChild_AddsToParent()
    {
        var parent = new ResourceNode("Parent", ResourceType.Node, _registry.DataEngine);
        var child = new ResourceNode("Child", ResourceType.Node, parent);

        parent.RegisterChild(child);

        Assert.That(parent.Children, Contains.Item(child));
    }

    [Test]
    public void RemoveChild_RemovesFromParent()
    {
        var parent = new ResourceNode("Parent", ResourceType.Node, _registry.DataEngine);
        var child = new ResourceNode("Child", ResourceType.Node, parent);
        parent.RegisterChild(child);

        parent.RemoveChild(child);

        Assert.That(parent.Children, Does.Not.Contain(child));
    }

    #endregion

    #region GetAncestors Tests

    [Test]
    public void GetAncestors_ReturnsCorrectChain()
    {
        var child = new ResourceNode("Child", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        var ancestors = child.GetAncestors().ToList();

        Assert.That(ancestors, Has.Count.EqualTo(2));
        Assert.That(ancestors[0], Is.SameAs(_registry.DataEngine));
        Assert.That(ancestors[1], Is.SameAs(_registry.Root));
    }

    [Test]
    public void GetAncestors_RootHasNoAncestors()
    {
        var ancestors = _registry.Root.GetAncestors().ToList();
        Assert.That(ancestors, Is.Empty);
    }

    [Test]
    public void GetAncestors_ThrowsOnNull()
    {
        IResource resource = null;
        Assert.Throws<ArgumentNullException>(() => resource.GetAncestors().ToList());
    }

    #endregion

    #region GetDescendants Tests

    [Test]
    public void GetDescendants_ReturnsAllDescendants()
    {
        var child1 = new ResourceNode("Child1", ResourceType.Node, _registry.DataEngine);
        var child2 = new ResourceNode("Child2", ResourceType.Node, _registry.DataEngine);
        var grandchild = new ResourceNode("Grandchild", ResourceType.Node, child1);

        _registry.DataEngine.RegisterChild(child1);
        _registry.DataEngine.RegisterChild(child2);
        child1.RegisterChild(grandchild);

        var descendants = _registry.DataEngine.GetDescendants().ToList();

        Assert.That(descendants, Has.Count.EqualTo(3));
        Assert.That(descendants, Contains.Item(child1));
        Assert.That(descendants, Contains.Item(child2));
        Assert.That(descendants, Contains.Item(grandchild));
    }

    [Test]
    public void GetDescendants_EmptyForLeaf()
    {
        var leaf = new ResourceNode("Leaf", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(leaf);

        var descendants = leaf.GetDescendants().ToList();

        Assert.That(descendants, Is.Empty);
    }

    #endregion

    #region GetPath Tests

    [Test]
    public void GetPath_ReturnsCorrectPath()
    {
        var child = new ResourceNode("MyChild", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        var path = child.GetPath();

        Assert.That(path, Is.EqualTo("Root/DataEngine/MyChild"));
    }

    [Test]
    public void GetPath_RootReturnsRootId()
    {
        var path = _registry.Root.GetPath();
        Assert.That(path, Is.EqualTo("Root"));
    }

    [Test]
    public void GetPath_CustomSeparator()
    {
        var child = new ResourceNode("MyChild", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        var path = child.GetPath(".");

        Assert.That(path, Is.EqualTo("Root.DataEngine.MyChild"));
    }

    #endregion

    #region FindByPath Tests

    [Test]
    public void FindByPath_FromRoot_FindsDescendant()
    {
        var child = new ResourceNode("MyChild", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        var found = _registry.Root.FindByPath("DataEngine/MyChild");

        Assert.That(found, Is.SameAs(child));
    }

    [Test]
    public void FindByPath_NotFound_ReturnsNull()
    {
        var found = _registry.Root.FindByPath("DataEngine/NonExistent");
        Assert.That(found, Is.Null);
    }

    [Test]
    public void FindByPath_EmptyPath_ReturnsSelf()
    {
        var found = _registry.Root.FindByPath("");
        Assert.That(found, Is.SameAs(_registry.Root));
    }

    [Test]
    public void Registry_FindByPath_FullPath()
    {
        var child = new ResourceNode("MyChild", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        var found = _registry.FindByPath("Root/DataEngine/MyChild");

        Assert.That(found, Is.SameAs(child));
    }

    [Test]
    public void Registry_FindByPath_InvalidRoot_ReturnsNull()
    {
        var found = _registry.FindByPath("InvalidRoot/DataEngine");
        Assert.That(found, Is.Null);
    }

    #endregion

    #region GetDepth Tests

    [Test]
    public void GetDepth_RootIsZero() => Assert.That(_registry.Root.GetDepth(), Is.EqualTo(0));

    [Test]
    public void GetDepth_SubsystemIsOne() => Assert.That(_registry.DataEngine.GetDepth(), Is.EqualTo(1));

    [Test]
    public void GetDepth_ChildOfSubsystem()
    {
        var child = new ResourceNode("Child", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        Assert.That(child.GetDepth(), Is.EqualTo(2));
    }

    #endregion

    #region IsAncestorOf / IsDescendantOf Tests

    [Test]
    public void IsAncestorOf_ReturnsTrue()
    {
        var child = new ResourceNode("Child", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        Assert.That(_registry.Root.IsAncestorOf(child), Is.True);
        Assert.That(_registry.DataEngine.IsAncestorOf(child), Is.True);
    }

    [Test]
    public void IsAncestorOf_ReturnsFalse()
    {
        var child = new ResourceNode("Child", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        Assert.That(child.IsAncestorOf(_registry.Root), Is.False);
        Assert.That(_registry.Storage.IsAncestorOf(child), Is.False);
    }

    [Test]
    public void IsDescendantOf_ReturnsTrue()
    {
        var child = new ResourceNode("Child", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        Assert.That(child.IsDescendantOf(_registry.Root), Is.True);
        Assert.That(child.IsDescendantOf(_registry.DataEngine), Is.True);
    }

    [Test]
    public void IsDescendantOf_ReturnsFalse()
    {
        var child = new ResourceNode("Child", ResourceType.Node, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(child);

        Assert.That(_registry.Root.IsDescendantOf(child), Is.False);
        Assert.That(child.IsDescendantOf(_registry.Storage), Is.False);
    }

    #endregion

    #region FindAll / FindFirst Tests

    [Test]
    public void FindAll_ReturnsMatchingDescendants()
    {
        var node1 = new ResourceNode("Node1", ResourceType.Service, _registry.DataEngine);
        var node2 = new ResourceNode("Node2", ResourceType.Node, _registry.DataEngine);
        var node3 = new ResourceNode("Node3", ResourceType.Service, _registry.DataEngine);

        _registry.DataEngine.RegisterChild(node1);
        _registry.DataEngine.RegisterChild(node2);
        _registry.DataEngine.RegisterChild(node3);

        var services = _registry.DataEngine.FindAll(r => r.Type == ResourceType.Service).ToList();

        Assert.That(services, Has.Count.EqualTo(2));
        Assert.That(services, Contains.Item(node1));
        Assert.That(services, Contains.Item(node3));
    }

    [Test]
    public void FindFirst_ReturnsFirstMatch()
    {
        var node1 = new ResourceNode("Node1", ResourceType.Service, _registry.DataEngine);
        var node2 = new ResourceNode("Node2", ResourceType.Service, _registry.DataEngine);

        _registry.DataEngine.RegisterChild(node1);
        _registry.DataEngine.RegisterChild(node2);

        var found = _registry.DataEngine.FindFirst(r => r.Type == ResourceType.Service);

        Assert.That(found, Is.Not.Null);
        Assert.That(found.Type, Is.EqualTo(ResourceType.Service));
    }

    [Test]
    public void FindFirst_NoMatch_ReturnsNull()
    {
        var found = _registry.DataEngine.FindFirst(r => r.Type == ResourceType.File);
        Assert.That(found, Is.Null);
    }

    #endregion

    #region GetDescendantCount Tests

    [Test]
    public void GetDescendantCount_ReturnsCorrectCount()
    {
        var child1 = new ResourceNode("Child1", ResourceType.Node, _registry.DataEngine);
        var child2 = new ResourceNode("Child2", ResourceType.Node, _registry.DataEngine);
        var grandchild = new ResourceNode("Grandchild", ResourceType.Node, child1);

        _registry.DataEngine.RegisterChild(child1);
        _registry.DataEngine.RegisterChild(child2);
        child1.RegisterChild(grandchild);

        Assert.That(_registry.DataEngine.GetDescendantCount(), Is.EqualTo(3));
        Assert.That(child1.GetDescendantCount(), Is.EqualTo(1));
        Assert.That(child2.GetDescendantCount(), Is.EqualTo(0));
    }

    #endregion

    #region Thread Safety Tests

    [Test]
    public void RegisterChild_ThreadSafe()
    {
        const int threadCount = 10;
        const int nodesPerThread = 100;

        var threads = new System.Threading.Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new System.Threading.Thread(() =>
            {
                for (int i = 0; i < nodesPerThread; i++)
                {
                    var node = new ResourceNode($"Node_{threadId}_{i}", ResourceType.Node, _registry.DataEngine);
                    _registry.DataEngine.RegisterChild(node);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.That(_registry.DataEngine.Children.Count(), Is.EqualTo(threadCount * nodesPerThread));
    }

    #endregion
}
