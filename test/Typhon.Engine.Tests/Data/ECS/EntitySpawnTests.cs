using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// ECS test component types (must have [Component] for ComponentTable registration)
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsPosition
{
    public float X, Y, Z;

    public EcsPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
}

[Component("Typhon.Test.ECS.Velocity", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsVelocity
{
    public float Dx, Dy, Dz;

    public EcsVelocity(float dx, float dy, float dz) { Dx = dx; Dy = dy; Dz = dz; }
}

[Component("Typhon.Test.ECS.Health", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsHealth
{
    public int Current, Max;

    public EcsHealth(int current, int max) { Current = current; Max = max; }
}

// ═══════════════════════════════════════════════════════════════════════
// ECS test archetypes
// ═══════════════════════════════════════════════════════════════════════

[Archetype(100)]
class EcsUnit : Archetype<EcsUnit>
{
    public static readonly Comp<EcsPosition> Position = Register<EcsPosition>();
    public static readonly Comp<EcsVelocity> Velocity = Register<EcsVelocity>();
}

[Archetype(101)]
class EcsSoldier : Archetype<EcsSoldier, EcsUnit>
{
    public static readonly Comp<EcsHealth> Health = Register<EcsHealth>();
}

[NonParallelizable]
class EntitySpawnTests : TestBase<EntitySpawnTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        // Ensure archetypes are finalized
        Archetype<EcsUnit>.Touch();
        Archetype<EcsSoldier>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        // Register ECS component types as ComponentTables
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();

        // Initialize archetypes — connects slots to ComponentTables
        dbe.InitializeArchetypes();

        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_SingleEntity_ReturnsValidEntityId()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(id.IsNull, Is.False);
        Assert.That(id.ArchetypeId, Is.EqualTo(100));
        Assert.That(id.EntityKey, Is.GreaterThan(0));
    }

    [Test]
    public void Spawn_ThenOpen_ReadsComponentData()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 0, 0);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        // Open within same transaction (before commit — reads from pending map)
        var entity = t.Open(id);
        Assert.That(entity.IsValid, Is.True);

        ref readonly var readPos = ref entity.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(10f));
        Assert.That(readPos.Y, Is.EqualTo(20f));
        Assert.That(readPos.Z, Is.EqualTo(30f));

        ref readonly var readVel = ref entity.Read(EcsUnit.Velocity);
        Assert.That(readVel.Dx, Is.EqualTo(1f));
    }

    [Test]
    public void Spawn_CommitThenRead_InNewTransaction()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(100, 200, 300);
            var vel = new EcsVelocity(0, 0, 0);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // New transaction should see the committed entity
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.True);

            var entity = t.Open(id);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.EqualTo(100f));
            Assert.That(pos.Y, Is.EqualTo(200f));
            Assert.That(pos.Z, Is.EqualTo(300f));
        }
    }

    [Test]
    public void Spawn_WithInheritedArchetype_HasParentAndOwnComponents()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 1, 1);
        var vel = new EcsVelocity(2, 2, 2);
        var hp = new EcsHealth(100, 100);

        var id = t.Spawn<EcsSoldier>(
            EcsUnit.Position.Set(in pos),
            EcsUnit.Velocity.Set(in vel),
            EcsSoldier.Health.Set(in hp));

        Assert.That(id.ArchetypeId, Is.EqualTo(101));

        var entity = t.Open(id);

        // Inherited components (from EcsUnit)
        ref readonly var readPos = ref entity.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(1f));

        // Own component
        ref readonly var readHp = ref entity.Read(EcsSoldier.Health);
        Assert.That(readHp.Current, Is.EqualTo(100));
        Assert.That(readHp.Max, Is.EqualTo(100));
    }

    [Test]
    public void Spawn_OpenMut_WritesComponentData()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(0, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var entity = t.OpenMut(id);
        ref var writePos = ref entity.Write(EcsUnit.Position);
        writePos.X = 999;
        writePos.Y = 888;

        // Re-open and verify write persisted
        var entity2 = t.Open(id);
        ref readonly var readPos = ref entity2.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(999f));
        Assert.That(readPos.Y, Is.EqualTo(888f));
    }

    [Test]
    public void Spawn_MultipleEntities_UniqueIds()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(0, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);

        var id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id3 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(id1, Is.Not.EqualTo(id2));
        Assert.That(id2, Is.Not.EqualTo(id3));
        Assert.That(id1.ArchetypeId, Is.EqualTo(id2.ArchetypeId));
    }

    [Test]
    public void TryOpen_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var fakeId = new EntityId(999999, 100);
        bool found = t.TryOpen(fakeId, out var entity);
        Assert.That(found, Is.False);
        Assert.That(entity.IsValid, Is.False);
    }

    [Test]
    public void IsAlive_NullEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Assert.That(t.IsAlive(EntityId.Null), Is.False);
    }

    [Test]
    public void Spawn_ThenRollback_EntityNotVisible()
    {
        using var dbe = SetupEngine();

        EntityId spawnedId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(0, 0, 0);
            spawnedId = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

            // Verify visible within the creating transaction
            Assert.That(t.IsAlive(spawnedId), Is.True);

            // Don't commit — implicit rollback on dispose
        }

        // New transaction should NOT see the rolled-back entity
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(spawnedId), Is.False, "Rolled-back entity should be invisible");
            Assert.That(t.TryOpen(spawnedId, out _), Is.False);
        }
    }
}
