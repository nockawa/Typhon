using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class CellClusterPoolTests
{
    [Test]
    public void NewPool_EmptyCell_HasZeroClusters()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        Assert.That(pool.GetClusters(in cell).Length, Is.EqualTo(0));
    }

    [Test]
    public void AddCluster_FirstEntry_AllocatesSegment()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };

        pool.AddCluster(ref cell, cellKey: 5, clusterChunkId: 42);

        Assert.That(cell.ClusterCount, Is.EqualTo(1));
        Assert.That(cell.ClusterListHead, Is.GreaterThanOrEqualTo(0));
        var span = pool.GetClusters(in cell);
        Assert.That(span.Length, Is.EqualTo(1));
        Assert.That(span[0], Is.EqualTo(42));
    }

    [Test]
    public void AddCluster_ManyEntries_InSameCell_PreservesOrder()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        for (int i = 0; i < 20; i++)
        {
            pool.AddCluster(ref cell, cellKey: 3, clusterChunkId: 100 + i);
        }
        Assert.That(cell.ClusterCount, Is.EqualTo(20));
        var span = pool.GetClusters(in cell);
        for (int i = 0; i < 20; i++)
        {
            Assert.That(span[i], Is.EqualTo(100 + i));
        }
    }

    [Test]
    public void AddCluster_MultipleCells_Independent()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var a = new CellDescriptor { ClusterListHead = -1 };
        var b = new CellDescriptor { ClusterListHead = -1 };

        pool.AddCluster(ref a, cellKey: 1, clusterChunkId: 10);
        pool.AddCluster(ref b, cellKey: 2, clusterChunkId: 20);
        pool.AddCluster(ref a, cellKey: 1, clusterChunkId: 11);
        pool.AddCluster(ref b, cellKey: 2, clusterChunkId: 21);

        Assert.That(pool.GetClusters(in a).ToArray(), Is.EqualTo(new[] { 10, 11 }));
        Assert.That(pool.GetClusters(in b).ToArray(), Is.EqualTo(new[] { 20, 21 }));
    }

    [Test]
    public void RemoveCluster_SwapWithLast_RemovesMiddleEntry()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 1);
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 2);
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 3);
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 4);

        bool removed = pool.RemoveCluster(ref cell, clusterChunkId: 2);
        Assert.That(removed, Is.True);
        Assert.That(cell.ClusterCount, Is.EqualTo(3));

        // Swap-with-last: 4 should have moved into slot 1 (where 2 was)
        var span = pool.GetClusters(in cell);
        Assert.That(span.ToArray(), Is.EquivalentTo(new[] { 1, 3, 4 }));
        // Specifically: first entry should still be 1, order of the remainder is swap-with-last.
        Assert.That(span[0], Is.EqualTo(1));
    }

    [Test]
    public void RemoveCluster_Missing_ReturnsFalse()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 1);
        bool removed = pool.RemoveCluster(ref cell, clusterChunkId: 999);
        Assert.That(removed, Is.False);
        Assert.That(cell.ClusterCount, Is.EqualTo(1));
    }

    [Test]
    public void RemoveCluster_AllEntries_LeavesEmptySegment()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 1);
        pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: 2);
        pool.RemoveCluster(ref cell, clusterChunkId: 1);
        pool.RemoveCluster(ref cell, clusterChunkId: 2);
        Assert.That(cell.ClusterCount, Is.EqualTo(0));
        Assert.That(pool.GetClusters(in cell).Length, Is.EqualTo(0));
    }

    [Test]
    public void Grow_BeyondInitialCapacity_Succeeds()
    {
        var pool = new CellClusterPool(cellCount: 16, initialPoolCapacity: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        // Force multiple capacity doublings
        for (int i = 0; i < 100; i++)
        {
            pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: i);
        }
        Assert.That(cell.ClusterCount, Is.EqualTo(100));
        Assert.That(pool.PoolCapacity, Is.GreaterThanOrEqualTo(100));
        var span = pool.GetClusters(in cell);
        for (int i = 0; i < 100; i++)
        {
            Assert.That(span[i], Is.EqualTo(i));
        }
    }

    [Test]
    public void Reset_ClearsAllCellCapacities_AndPoolTail()
    {
        var pool = new CellClusterPool(cellCount: 16);
        var cell = new CellDescriptor { ClusterListHead = -1 };
        for (int i = 0; i < 10; i++)
        {
            pool.AddCluster(ref cell, cellKey: 0, clusterChunkId: i);
        }
        int tailBefore = pool.PoolTail;
        Assert.That(tailBefore, Is.GreaterThan(0));

        pool.Reset();

        Assert.That(pool.PoolTail, Is.EqualTo(0));

        // After reset, a fresh add starts at offset 0 again
        var newCell = new CellDescriptor { ClusterListHead = -1 };
        pool.AddCluster(ref newCell, cellKey: 0, clusterChunkId: 99);
        Assert.That(newCell.ClusterListHead, Is.EqualTo(0));
    }
}
