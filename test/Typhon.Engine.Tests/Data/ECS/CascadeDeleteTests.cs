using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Cascade test component + archetype types
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.BagData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct BagData
{
    public int Capacity;
    public int _pad;
}

[Component("Typhon.Test.ECS.ItemData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct ItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<CascadeBag> Owner;
    public int Weight;
}

[Archetype(700)]
class CascadeBag : Archetype<CascadeBag>
{
    public static readonly Comp<BagData> Bag = Register<BagData>();
}

[Archetype(701)]
class CascadeItem : Archetype<CascadeItem>
{
    public static readonly Comp<ItemData> Item = Register<ItemData>();
}

[NonParallelizable]
class CascadeDeleteTests : TestBase<CascadeDeleteTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CascadeBag>.Touch();
        Archetype<CascadeItem>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<BagData>();
        dbe.RegisterComponentFromAccessor<ItemData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Graph validation tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CascadeGraph_BagHasCascadeTargets()
    {
        // Build cascade graph (requires InitializeArchetypes or explicit call)
        using var dbe = SetupEngine();

        var bagMeta = ArchetypeRegistry.GetMetadata(700);
        Assert.That(bagMeta, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets.Count, Is.GreaterThanOrEqualTo(1));

        var target = bagMeta._cascadeTargets[0];
        Assert.That(target.ChildArchetypeId, Is.EqualTo(701)); // CascadeItem
    }

    [Test]
    public void CascadeGraph_ItemHasNoCascadeTargets()
    {
        using var dbe = SetupEngine();
        var itemMeta = ArchetypeRegistry.GetMetadata(701);
        Assert.That(itemMeta, Is.Not.Null);
        // Item has no children with cascade delete
        Assert.That(itemMeta._cascadeTargets == null || itemMeta._cascadeTargets.Count == 0, Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cascade delete execution tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_BagWithPendingItems_CascadeDeletesItems()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Spawn a bag
        var bagData = new BagData { Capacity = 10 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        // Spawn items pointing to the bag
        var item1Data = new ItemData { Owner = bagId, Weight = 5 };
        var item2Data = new ItemData { Owner = bagId, Weight = 3 };
        var item1Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
        var item2Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));

        // Destroy bag — should cascade to items
        t.Destroy(bagId);

        // All should be marked for destruction
        Assert.That(t.TryOpen(bagId, out _), Is.False, "Bag should be destroyed");
        Assert.That(t.TryOpen(item1Id, out _), Is.False, "Item 1 should be cascade-destroyed");
        Assert.That(t.TryOpen(item2Id, out _), Is.False, "Item 2 should be cascade-destroyed");
    }

    [Test]
    public void Destroy_BagWithoutItems_NoError()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var bagData = new BagData { Capacity = 5 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        // Destroy bag with no items — should work fine
        Assert.DoesNotThrow(() => t.Destroy(bagId));
        Assert.That(t.TryOpen(bagId, out _), Is.False);
    }

    [Test]
    public void Destroy_BagWithCommittedItems_CascadeDeletesItems()
    {
        using var dbe = SetupEngine();

        // Spawn bag + items and COMMIT them
        EntityId bagId, item1Id, item2Id;
        using (var t1 = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 10 };
            bagId = t1.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

            var item1Data = new ItemData { Owner = bagId, Weight = 5 };
            var item2Data = new ItemData { Owner = bagId, Weight = 3 };
            item1Id = t1.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
            item2Id = t1.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));

            t1.Commit();
        }

        // Now destroy the bag in a new transaction — cascade should find committed items
        using var t2 = dbe.CreateQuickTransaction();
        t2.Destroy(bagId);

        Assert.That(t2.TryOpen(bagId, out _), Is.False, "Bag should be destroyed");
        Assert.That(t2.TryOpen(item1Id, out _), Is.False, "Item 1 should be cascade-destroyed");
        Assert.That(t2.TryOpen(item2Id, out _), Is.False, "Item 2 should be cascade-destroyed");
    }

    [Test]
    public void Destroy_BagWithMixedItems_OnlyOwnerItemsDeleted()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Create two bags
        var bag1Data = new BagData { Capacity = 10 };
        var bag2Data = new BagData { Capacity = 20 };
        var bag1Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bag1Data));
        var bag2Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bag2Data));

        // Create items for bag1
        var item1Data = new ItemData { Owner = bag1Id, Weight = 5 };
        var item2Data = new ItemData { Owner = bag1Id, Weight = 3 };
        var item1Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
        var item2Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));

        // Create items for bag2
        var item3Data = new ItemData { Owner = bag2Id, Weight = 7 };
        var item4Data = new ItemData { Owner = bag2Id, Weight = 1 };
        var item3Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item3Data));
        var item4Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item4Data));

        // Destroy bag1 — only bag1's items should be cascade-deleted
        t.Destroy(bag1Id);

        Assert.That(t.TryOpen(bag1Id, out _), Is.False, "Bag1 should be destroyed");
        Assert.That(t.TryOpen(item1Id, out _), Is.False, "Item 1 (bag1) should be cascade-destroyed");
        Assert.That(t.TryOpen(item2Id, out _), Is.False, "Item 2 (bag1) should be cascade-destroyed");

        Assert.That(t.TryOpen(bag2Id, out _), Is.True, "Bag2 should survive");
        Assert.That(t.TryOpen(item3Id, out _), Is.True, "Item 3 (bag2) should survive");
        Assert.That(t.TryOpen(item4Id, out _), Is.True, "Item 4 (bag2) should survive");
    }

    [Test]
    public void Destroy_BagWithUnrelatedItems_UnrelatedSurvive()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Create a bag
        var bagData = new BagData { Capacity = 10 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        // Create items owned by the bag
        var ownedItemData = new ItemData { Owner = bagId, Weight = 5 };
        var ownedItemId = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in ownedItemData));

        // Create items with null owner (unrelated)
        var unrelatedItemData = new ItemData { Owner = EntityId.Null, Weight = 9 };
        var unrelatedItemId = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in unrelatedItemData));

        // Destroy bag — only owned items should be cascade-deleted
        t.Destroy(bagId);

        Assert.That(t.TryOpen(bagId, out _), Is.False, "Bag should be destroyed");
        Assert.That(t.TryOpen(ownedItemId, out _), Is.False, "Owned item should be cascade-destroyed");
        Assert.That(t.TryOpen(unrelatedItemId, out _), Is.True, "Unrelated item (null owner) should survive");
    }

}
