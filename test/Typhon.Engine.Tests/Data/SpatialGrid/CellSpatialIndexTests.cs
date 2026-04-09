using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class CellSpatialIndexTests
{
    private static ClusterSpatialAabb Aabb(float minX, float minY, float maxX, float maxY, uint cat = 1u) =>
        new() { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, CategoryMask = cat };

    [Test]
    public void NewIndex_IsEmpty_WithDefaultCapacity()
    {
        var index = new CellSpatialIndex();
        Assert.That(index.ClusterCount, Is.EqualTo(0));
        Assert.That(index.Capacity, Is.EqualTo(CellSpatialIndex.DefaultInitialCapacity));
        Assert.That(index.ClusterIds.Length, Is.EqualTo(CellSpatialIndex.DefaultInitialCapacity));
        Assert.That(index.MinX.Length, Is.EqualTo(CellSpatialIndex.DefaultInitialCapacity));
    }

    [Test]
    public void Constructor_ClampsZeroCapacity_ToOne()
    {
        var index = new CellSpatialIndex(initialCapacity: 0);
        Assert.That(index.Capacity, Is.EqualTo(1));
    }

    [Test]
    public void Add_FirstCluster_ReturnsSlotZero()
    {
        var index = new CellSpatialIndex();
        int slot = index.Add(clusterChunkId: 42, Aabb(0, 0, 10, 10, cat: 0x7u));

        Assert.That(slot, Is.EqualTo(0));
        Assert.That(index.ClusterCount, Is.EqualTo(1));
        Assert.That(index.ClusterIds[0], Is.EqualTo(42));
        Assert.That(index.MinX[0], Is.EqualTo(0f));
        Assert.That(index.MaxX[0], Is.EqualTo(10f));
        Assert.That(index.CategoryMasks[0], Is.EqualTo(0x7u));
    }

    [Test]
    public void Add_ManyClusters_AssignsSequentialSlots()
    {
        var index = new CellSpatialIndex();
        for (int i = 0; i < 10; i++)
        {
            int slot = index.Add(clusterChunkId: 100 + i, Aabb(i, i, i + 1, i + 1));
            Assert.That(slot, Is.EqualTo(i));
        }
        Assert.That(index.ClusterCount, Is.EqualTo(10));
        for (int i = 0; i < 10; i++)
        {
            Assert.That(index.ClusterIds[i], Is.EqualTo(100 + i));
            Assert.That(index.MinX[i], Is.EqualTo((float)i));
        }
    }

    [Test]
    public void Add_BeyondInitialCapacity_GrowsByDoubling()
    {
        var index = new CellSpatialIndex(initialCapacity: 4);
        Assert.That(index.Capacity, Is.EqualTo(4));

        for (int i = 0; i < 5; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }
        // 5th add should have triggered growth (4 → 8).
        Assert.That(index.Capacity, Is.EqualTo(8));
        Assert.That(index.ClusterCount, Is.EqualTo(5));
        Assert.That(index.ClusterIds[4], Is.EqualTo(14));

        // Fill the rest and trigger another growth.
        for (int i = 5; i < 9; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }
        Assert.That(index.Capacity, Is.EqualTo(16));
        Assert.That(index.ClusterCount, Is.EqualTo(9));
    }

    [Test]
    public void UpdateAt_OverwritesAabbAndMask()
    {
        var index = new CellSpatialIndex();
        int slot = index.Add(clusterChunkId: 7, Aabb(0, 0, 5, 5, cat: 0x1u));
        index.UpdateAt(slot, Aabb(-10, -10, 20, 20, cat: 0xFu));

        Assert.That(index.MinX[slot], Is.EqualTo(-10f));
        Assert.That(index.MinY[slot], Is.EqualTo(-10f));
        Assert.That(index.MaxX[slot], Is.EqualTo(20f));
        Assert.That(index.MaxY[slot], Is.EqualTo(20f));
        Assert.That(index.CategoryMasks[slot], Is.EqualTo(0xFu));
        // Back-ref unchanged
        Assert.That(index.ClusterIds[slot], Is.EqualTo(7));
    }

    [Test]
    public void RemoveAt_LastEntry_NoSwap_ReturnsMinusOne()
    {
        var index = new CellSpatialIndex();
        int slot0 = index.Add(clusterChunkId: 11, Aabb(0, 0, 1, 1));
        int slot1 = index.Add(clusterChunkId: 22, Aabb(1, 1, 2, 2));

        int swapped = index.RemoveAt(slot1);
        Assert.That(swapped, Is.EqualTo(-1),
            "removing the last entry leaves nothing to swap in → no back-pointer fixup needed");
        Assert.That(index.ClusterCount, Is.EqualTo(1));
        // Slot 0 untouched
        Assert.That(index.ClusterIds[0], Is.EqualTo(11));
    }

    [Test]
    public void RemoveAt_MiddleEntry_SwapsLastIntoSlot_ReturnsMovedClusterId()
    {
        var index = new CellSpatialIndex();
        index.Add(clusterChunkId: 100, Aabb(0, 0, 1, 1, cat: 0x1u));   // slot 0
        index.Add(clusterChunkId: 200, Aabb(2, 2, 3, 3, cat: 0x2u));   // slot 1  ← removed
        index.Add(clusterChunkId: 300, Aabb(4, 4, 5, 5, cat: 0x4u));   // slot 2  ← will move to slot 1

        int swapped = index.RemoveAt(slot: 1);

        Assert.That(swapped, Is.EqualTo(300), "cluster 300 was moved from slot 2 into slot 1");
        Assert.That(index.ClusterCount, Is.EqualTo(2));
        Assert.That(index.ClusterIds[0], Is.EqualTo(100));
        Assert.That(index.ClusterIds[1], Is.EqualTo(300), "slot 1 now holds the cluster that was at slot 2");
        // The AABB was moved too
        Assert.That(index.MinX[1], Is.EqualTo(4f));
        Assert.That(index.CategoryMasks[1], Is.EqualTo(0x4u));
    }

    [Test]
    public void RemoveAt_AllEntriesInReverse_LeavesEmptyIndex()
    {
        var index = new CellSpatialIndex();
        for (int i = 0; i < 5; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }
        for (int i = 4; i >= 0; i--)
        {
            index.RemoveAt(i);
        }
        Assert.That(index.ClusterCount, Is.EqualTo(0));
    }

    [Test]
    public void AddRemoveAdd_ReusesSlotAfterSwapWithLast()
    {
        var index = new CellSpatialIndex();
        index.Add(clusterChunkId: 10, Aabb(0, 0, 1, 1));
        index.Add(clusterChunkId: 20, Aabb(2, 2, 3, 3));

        int swappedIdOnRemove = index.RemoveAt(slot: 0);
        Assert.That(swappedIdOnRemove, Is.EqualTo(20));
        Assert.That(index.ClusterIds[0], Is.EqualTo(20));

        int newSlot = index.Add(clusterChunkId: 30, Aabb(4, 4, 5, 5));
        Assert.That(newSlot, Is.EqualTo(1), "add after removal fills the tail, not the vacated slot");
        Assert.That(index.ClusterCount, Is.EqualTo(2));
        Assert.That(index.ClusterIds[0], Is.EqualTo(20));
        Assert.That(index.ClusterIds[1], Is.EqualTo(30));
    }

    [Test]
    public void ClusterSpatialAabb_Empty_HasInfiniteMinAndNegativeInfiniteMax()
    {
        var aabb = ClusterSpatialAabb.Empty;
        Assert.That(aabb.MinX, Is.EqualTo(float.PositiveInfinity));
        Assert.That(aabb.MinY, Is.EqualTo(float.PositiveInfinity));
        Assert.That(aabb.MaxX, Is.EqualTo(float.NegativeInfinity));
        Assert.That(aabb.MaxY, Is.EqualTo(float.NegativeInfinity));
        Assert.That(aabb.CategoryMask, Is.EqualTo(0u));
    }

    [Test]
    public void ClusterSpatialAabb_Union_WithOneEntity_ExactlyMatchesEntityBounds()
    {
        var aabb = ClusterSpatialAabb.Empty;
        aabb.Union(entityMinX: 5f, entityMinY: 10f, entityMaxX: 15f, entityMaxY: 20f, entityCategoryMask: 0x3u);

        Assert.That(aabb.MinX, Is.EqualTo(5f));
        Assert.That(aabb.MinY, Is.EqualTo(10f));
        Assert.That(aabb.MaxX, Is.EqualTo(15f));
        Assert.That(aabb.MaxY, Is.EqualTo(20f));
        Assert.That(aabb.CategoryMask, Is.EqualTo(0x3u));
    }

    [Test]
    public void ClusterSpatialAabb_Union_MultipleEntities_EnclosesAllAndCombinesMasks()
    {
        var aabb = ClusterSpatialAabb.Empty;
        aabb.Union(0f, 0f, 10f, 10f, 0x1u);
        aabb.Union(-5f, 20f, 5f, 30f, 0x2u);
        aabb.Union(15f, -3f, 25f, 7f, 0x8u);

        Assert.That(aabb.MinX, Is.EqualTo(-5f));
        Assert.That(aabb.MinY, Is.EqualTo(-3f));
        Assert.That(aabb.MaxX, Is.EqualTo(25f));
        Assert.That(aabb.MaxY, Is.EqualTo(30f));
        Assert.That(aabb.CategoryMask, Is.EqualTo(0xBu));
    }
}
