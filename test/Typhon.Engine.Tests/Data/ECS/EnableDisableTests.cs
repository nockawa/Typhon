using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[NonParallelizable]
class EnableDisableTests : TestBase<EnableDisableTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void EnabledBits_AllEnabled_AfterSpawnWithAllValues()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var entity = t.Open(id);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
    }

    [Test]
    public void EnabledBits_PartiallyEnabled_WhenNotAllValuesProvided()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        // Only provide Position, not Velocity
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        var entity = t.Open(id);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
    }

    [Test]
    public void Disable_Component_BecomesDisabled()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var entity = t.OpenMut(id);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);

        entity.Disable(EcsUnit.Velocity);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True); // still enabled
    }

    [Test]
    public void Enable_DisabledComponent_BecomesEnabled()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos)); // Velocity not provided = disabled

        var entity = t.OpenMut(id);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);

        entity.Enable(EcsUnit.Velocity);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
    }

    [Test]
    public void EnableDisable_PersistsAfterCommit()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Disable Velocity in new transaction
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Velocity);
            t.Commit();
        }

        // Verify Velocity is disabled in new transaction
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
        }
    }

    [Test]
    public void EnabledBitsOverrides_FastPath_NoOverheadWhenNoOverrides()
    {
        // Verify the fast-path optimization: when OverrideCount == 0,
        // ResolveEnabledBits returns the inline bits directly
        var overrides = new EnabledBitsOverrides();
        Assert.That(overrides._overrideCount, Is.EqualTo(0));

        ushort result = overrides.ResolveEnabledBits(42L, 0b1111, 100);
        Assert.That(result, Is.EqualTo(0b1111));
    }

    [Test]
    public void EnabledBitsHistory_ResolveAt_ReturnsOldBitsForOlderTx()
    {
        var history = new EnabledBitsHistory();
        // At TSN=10, bits changed from 0b11 to 0b01
        history.Record(10, 0b11);

        // A transaction at TSN=5 (before the change) should see the old bits
        ushort resolved = history.ResolveAt(5, currentBits: 0b01);
        Assert.That(resolved, Is.EqualTo(0b11));

        // A transaction at TSN=15 (after the change) should see current bits
        ushort resolvedAfter = history.ResolveAt(15, currentBits: 0b01);
        Assert.That(resolvedAfter, Is.EqualTo(0b01));
    }

    [Test]
    public void EnabledBitsHistory_TryPrune_RemovesOldEntries()
    {
        var history = new EnabledBitsHistory();
        history.Record(5, 0b11);
        history.Record(10, 0b10);
        history.Record(15, 0b01);

        Assert.That(history.Count, Is.EqualTo(3));

        // Prune entries at or below TSN=10
        bool fullyPruned = history.TryPrune(10);
        Assert.That(fullyPruned, Is.False);
        Assert.That(history.Count, Is.EqualTo(1)); // only TSN=15 remains

        // Prune remaining
        fullyPruned = history.TryPrune(20);
        Assert.That(fullyPruned, Is.True);
    }

    [Test]
    public void EnableDisable_MVCC_OlderTxSeesOriginalBits()
    {
        using var dbe = SetupEngine();

        // Phase 1: Spawn entity with both components enabled
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Phase 2: Open tx1 (older snapshot) — keeps its TSN
        using var tx1 = dbe.CreateQuickTransaction();

        // Phase 3: tx2 disables Velocity and commits (newer TSN)
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var entity = tx2.OpenMut(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
            entity.Disable(EcsUnit.Velocity);
            tx2.Commit();
        }

        // Phase 4: tx1 (older snapshot) should still see Velocity as ENABLED
        // because the disable happened at a TSN > tx1.TSN
        var entityFromTx1 = tx1.Open(id);
        Assert.That(entityFromTx1.IsEnabled(EcsUnit.Position), Is.True, "Position should be enabled for old tx");
        Assert.That(entityFromTx1.IsEnabled(EcsUnit.Velocity), Is.True, "Velocity should still be enabled for old tx (MVCC)");

        // Phase 5: New tx (newest snapshot) should see Velocity as DISABLED
        using (var tx3 = dbe.CreateQuickTransaction())
        {
            var entityFromTx3 = tx3.Open(id);
            Assert.That(entityFromTx3.IsEnabled(EcsUnit.Velocity), Is.False, "Velocity should be disabled for new tx");
        }
    }
}
