using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class DagBuilderTests
{
    private static readonly Action NoOp = () => { };
    private static readonly Action<int, int> NoOpChunk = (_, _) => { };

    [Test]
    public void LinearChain_CorrectPredecessorsAndSuccessors()
    {
        var (systems, topo) = new DagBuilder()
            .AddCallback("A", NoOp)
            .AddCallback("B", NoOp)
            .AddCallback("C", NoOp)
            .AddEdge("A", "B")
            .AddEdge("B", "C")
            .Build();

        Assert.That(systems, Has.Length.EqualTo(3));

        // A: no predecessors, successor is B
        Assert.That(systems[0].PredecessorCount, Is.EqualTo(0));
        Assert.That(systems[0].Successors, Is.EqualTo(new[] { 1 }));

        // B: 1 predecessor (A), successor is C
        Assert.That(systems[1].PredecessorCount, Is.EqualTo(1));
        Assert.That(systems[1].Successors, Is.EqualTo(new[] { 2 }));

        // C: 1 predecessor (B), no successors
        Assert.That(systems[2].PredecessorCount, Is.EqualTo(1));
        Assert.That(systems[2].Successors, Is.Empty);

        // Topological order: A, B, C
        Assert.That(topo, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void FanOutFanIn_CorrectEdgeStructure()
    {
        var (systems, topo) = new DagBuilder()
            .AddCallback("A", NoOp)
            .AddCallback("B", NoOp)
            .AddCallback("C", NoOp)
            .AddCallback("D", NoOp)
            .AddCallback("E", NoOp)
            .AddEdge("A", "B")
            .AddEdge("A", "C")
            .AddEdge("A", "D")
            .AddEdge("B", "E")
            .AddEdge("C", "E")
            .AddEdge("D", "E")
            .Build();

        Assert.That(systems, Has.Length.EqualTo(5));

        // A fans out to B, C, D
        Assert.That(systems[0].PredecessorCount, Is.EqualTo(0));
        Assert.That(systems[0].Successors, Has.Length.EqualTo(3));

        // B, C, D each have 1 predecessor
        Assert.That(systems[1].PredecessorCount, Is.EqualTo(1));
        Assert.That(systems[2].PredecessorCount, Is.EqualTo(1));
        Assert.That(systems[3].PredecessorCount, Is.EqualTo(1));

        // E has 3 predecessors
        Assert.That(systems[4].PredecessorCount, Is.EqualTo(3));
        Assert.That(systems[4].Successors, Is.Empty);
    }

    [Test]
    public void CycleDetection_ThrowsOnCycle()
    {
        var builder = new DagBuilder()
            .AddCallback("A", NoOp)
            .AddCallback("B", NoOp)
            .AddCallback("C", NoOp)
            .AddEdge("A", "B")
            .AddEdge("B", "C")
            .AddEdge("C", "A");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Test]
    public void DuplicateNames_Throws()
    {
        var builder = new DagBuilder()
            .AddCallback("A", NoOp);

        Assert.Throws<InvalidOperationException>(() => builder.AddCallback("A", NoOp));
    }

    [Test]
    public void SingleNode_NoEdges_Succeeds()
    {
        var (systems, topo) = new DagBuilder()
            .AddCallback("Only", NoOp)
            .Build();

        Assert.That(systems, Has.Length.EqualTo(1));
        Assert.That(systems[0].Name, Is.EqualTo("Only"));
        Assert.That(systems[0].PredecessorCount, Is.EqualTo(0));
        Assert.That(systems[0].Successors, Is.Empty);
        Assert.That(topo, Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void EmptyDag_Returns_EmptyArray()
    {
        var (systems, topo) = new DagBuilder().Build();

        Assert.That(systems, Is.Empty);
        Assert.That(topo, Is.Empty);
    }

    [Test]
    public void PatateSystem_PreservesChunkCount()
    {
        var (systems, _) = new DagBuilder()
            .AddPatate("Physics", NoOpChunk, 200)
            .Build();

        Assert.That(systems[0].Type, Is.EqualTo(SystemType.Patate));
        Assert.That(systems[0].TotalChunks, Is.EqualTo(200));
        Assert.That(systems[0].PatateChunkAction, Is.Not.Null);
    }

    [Test]
    public void CallbackSystem_HasTotalChunksOne()
    {
        var (systems, _) = new DagBuilder()
            .AddCallback("Input", NoOp)
            .Build();

        Assert.That(systems[0].Type, Is.EqualTo(SystemType.Callback));
        Assert.That(systems[0].TotalChunks, Is.EqualTo(1));
        Assert.That(systems[0].CallbackAction, Is.Not.Null);
    }

    [Test]
    public void EdgeToUnknownSystem_Throws()
    {
        var builder = new DagBuilder()
            .AddCallback("A", NoOp);

        Assert.Throws<InvalidOperationException>(() => builder.AddEdge("A", "B"));
    }

    [Test]
    public void EdgeFromUnknownSystem_Throws()
    {
        var builder = new DagBuilder()
            .AddCallback("A", NoOp);

        Assert.Throws<InvalidOperationException>(() => builder.AddEdge("X", "A"));
    }

    [Test]
    public void SystemIndex_AssignedSequentially()
    {
        var (systems, _) = new DagBuilder()
            .AddCallback("A", NoOp)
            .AddPatate("B", NoOpChunk, 10)
            .AddCallback("C", NoOp)
            .Build();

        Assert.That(systems[0].Index, Is.EqualTo(0));
        Assert.That(systems[1].Index, Is.EqualTo(1));
        Assert.That(systems[2].Index, Is.EqualTo(2));
    }

    [Test]
    public void Priority_PreservedInSystemDefinition()
    {
        var (systems, _) = new DagBuilder()
            .AddCallback("Critical", NoOp, SystemPriority.Critical)
            .AddPatate("Low", NoOpChunk, 10, SystemPriority.Low)
            .Build();

        Assert.That(systems[0].Priority, Is.EqualTo(SystemPriority.Critical));
        Assert.That(systems[1].Priority, Is.EqualTo(SystemPriority.Low));
    }

    [Test]
    public void TopologicalOrder_RespectsEdges_DiamondDAG()
    {
        // Diamond: A → (B, C) → D
        var (_, topo) = new DagBuilder()
            .AddCallback("A", NoOp)
            .AddCallback("B", NoOp)
            .AddCallback("C", NoOp)
            .AddCallback("D", NoOp)
            .AddEdge("A", "B")
            .AddEdge("A", "C")
            .AddEdge("B", "D")
            .AddEdge("C", "D")
            .Build();

        // A must be before B and C; B and C must be before D
        var posA = Array.IndexOf(topo, 0);
        var posB = Array.IndexOf(topo, 1);
        var posC = Array.IndexOf(topo, 2);
        var posD = Array.IndexOf(topo, 3);

        Assert.That(posA, Is.LessThan(posB));
        Assert.That(posA, Is.LessThan(posC));
        Assert.That(posB, Is.LessThan(posD));
        Assert.That(posC, Is.LessThan(posD));
    }
}
