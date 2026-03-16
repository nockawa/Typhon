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

[Archetype(200)]
class CascadeBag : Archetype<CascadeBag>
{
    public static readonly Comp<BagData> Bag = Register<BagData>();
}

[Archetype(201)]
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

        var bagMeta = ArchetypeRegistry.GetMetadata(200);
        Assert.That(bagMeta, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets.Count, Is.GreaterThanOrEqualTo(1));

        var target = bagMeta._cascadeTargets[0];
        Assert.That(target.ChildArchetypeId, Is.EqualTo(201)); // CascadeItem
    }

    [Test]
    public void CascadeGraph_ItemHasNoCascadeTargets()
    {
        using var dbe = SetupEngine();
        var itemMeta = ArchetypeRegistry.GetMetadata(201);
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

    // ═══════════════════════════════════════════════════════════════════════
    // CascadeAction enum tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CascadeAction_DefaultIsNone()
    {
        var attr = new IndexAttribute();
        Assert.That(attr.OnParentDelete, Is.EqualTo(CascadeAction.None));
    }

    [Test]
    public void CascadeAction_CanSetDelete()
    {
        var attr = new IndexAttribute { OnParentDelete = CascadeAction.Delete };
        Assert.That(attr.OnParentDelete, Is.EqualTo(CascadeAction.Delete));
    }
}
