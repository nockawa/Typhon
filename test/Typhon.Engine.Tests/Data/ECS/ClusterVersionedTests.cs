using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Phase 5 test components and archetypes — mixed SV + Versioned cluster storage
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClV5.SvPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClV5SvPos
{
    public float X, Y;
    public ClV5SvPos(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Test.ClV5.VHealth", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClV5Health
{
    public int Current, Max;
    public ClV5Health(int current, int max) { Current = current; Max = max; }
}

[Component("Typhon.Test.ClV5.SvMovement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClV5SvMov
{
    public float Vx, Vy;
    public ClV5SvMov(float vx, float vy) { Vx = vx; Vy = vy; }
}

/// <summary>Mixed SV + Versioned archetype for cluster Phase 5 tests.</summary>
[Archetype(610)]
partial class ClVMixed : Archetype<ClVMixed>
{
    public static readonly Comp<ClV5SvPos> Pos = Register<ClV5SvPos>();
    public static readonly Comp<ClV5Health> Health = Register<ClV5Health>();
}

/// <summary>Two SV + one Versioned for multi-component cluster testing.</summary>
[Archetype(611)]
partial class ClVTriple : Archetype<ClVTriple>
{
    public static readonly Comp<ClV5SvPos> Pos = Register<ClV5SvPos>();
    public static readonly Comp<ClV5SvMov> Mov = Register<ClV5SvMov>();
    public static readonly Comp<ClV5Health> Health = Register<ClV5Health>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class ClusterVersionedTests : TestBase<ClusterVersionedTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClVMixed>.Touch();
        Archetype<ClVTriple>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClV5SvPos>();
        dbe.RegisterComponentFromAccessor<ClV5Health>();
        dbe.RegisterComponentFromAccessor<ClV5SvMov>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Eligibility & Layout
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MixedArchetype_IsClusterEligible()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClVMixed>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Mixed SV+V archetype should be cluster-eligible");
        Assert.That(meta.VersionedSlotMask, Is.Not.EqualTo(0), "Should have Versioned slots");
        Assert.That(meta.VersionedSlotCount, Is.EqualTo(1), "One Versioned component (Health)");

        var layout = meta.ClusterLayout;
        Assert.That(layout, Is.Not.Null);
        Assert.That(layout.SlotToVersionedIndex, Is.Not.Null);
    }

    [Test]
    public void ClusterEntityRecord_CorrectSize()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClVMixed>.Metadata;
        // Base 19 + 1 Versioned slot * 4 = 23 bytes
        Assert.That(meta._entityRecordSize, Is.EqualTo(23));
    }

    [Test]
    public void TripleArchetype_TwoSvOneVersioned()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClVTriple>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True);
        Assert.That(meta.VersionedSlotCount, Is.EqualTo(1));
        // Base 19 + 1 * 4 = 23 bytes
        Assert.That(meta._entityRecordSize, Is.EqualTo(23));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Spawn & Read HEAD
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnAndRead_VersionedHead_CorrectValue()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(10, 20);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        // Read in a new transaction
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            Assert.That(entity.IsValid, Is.True);

            ref readonly var pos = ref entity.Read(ClVMixed.Pos);
            Assert.That(pos.X, Is.EqualTo(10));
            Assert.That(pos.Y, Is.EqualTo(20));

            ref readonly var hp = ref entity.Read(ClVMixed.Health);
            Assert.That(hp.Current, Is.EqualTo(100));
            Assert.That(hp.Max, Is.EqualTo(200));
        }
    }

    [Test]
    public void SpawnAndRead_ViaArchetypeAccessor_CorrectValue()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(5, 15);
            var hp = new ClV5Health(50, 100);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            using var accessor = tx.For<ClVMixed>();
            var entity = accessor.Open(id);

            ref readonly var pos = ref entity.Read(ClVMixed.Pos);
            Assert.That(pos.X, Is.EqualTo(5));

            ref readonly var hp = ref entity.Read(ClVMixed.Health);
            Assert.That(hp.Current, Is.EqualTo(50));
            Assert.That(hp.Max, Is.EqualTo(100));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Write & Commit
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WriteVersioned_CommitUpdatesClusterSlot()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(1, 2);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        // Write Versioned component
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(id);
            ref var hp = ref entity.Write(ClVMixed.Health);
            hp.Current = 75;
            tx.Commit();
        }

        // Read in new transaction — should see updated value
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            ref readonly var hp = ref entity.Read(ClVMixed.Health);
            Assert.That(hp.Current, Is.EqualTo(75));
            Assert.That(hp.Max, Is.EqualTo(200));
        }
    }

    [Test]
    public void WriteSvComponent_InMixedArchetype_InPlaceUpdate()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(10, 20);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        // Write SV component (should be in-place cluster update)
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(id);
            ref var pos = ref entity.Write(ClVMixed.Pos);
            pos.X = 30;
            pos.Y = 40;
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            ref readonly var pos = ref entity.Read(ClVMixed.Pos);
            Assert.That(pos.X, Is.EqualTo(30));
            Assert.That(pos.Y, Is.EqualTo(40));

            // Versioned component should be unchanged
            ref readonly var hp = ref entity.Read(ClVMixed.Health);
            Assert.That(hp.Current, Is.EqualTo(100));
        }
    }

    [Test]
    public void MultipleWritesSameTransaction_FinalValuePersists()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(1, 1);
            var hp = new ClV5Health(100, 100);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(id);
            ref var hp = ref entity.Write(ClVMixed.Health);
            hp.Current = 80;
            // Write again
            ref var hp2 = ref entity.Write(ClVMixed.Health);
            hp2.Current = 60;
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            ref readonly var hp = ref entity.Read(ClVMixed.Health);
            Assert.That(hp.Current, Is.EqualTo(60));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Bulk Iteration via GetClusterEnumerator
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BulkIteration_ReadsVersionedHeadFromCluster()
    {
        using var dbe = SetupEngine();
        const int count = 20;
        var ids = new EntityId[count];

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var pos = new ClV5SvPos(i, i * 10);
                var hp = new ClV5Health(100 + i, 200);
                ids[i] = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            }
            tx.Commit();
        }

        // Iterate via cluster enumerator — should read HEAD from SoA
        using (var tx = dbe.CreateQuickTransaction())
        {
            using var accessor = tx.For<ClVMixed>();
            int entityCount = 0;
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ulong bits = cluster.OccupancyBits;
                var positions = cluster.GetReadOnlySpan(ClVMixed.Pos);
                var healths = cluster.GetReadOnlySpan(ClVMixed.Health);

                while (bits != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    // SV component from cluster SoA
                    Assert.That(positions[idx].Y, Is.EqualTo(positions[idx].X * 10));

                    // Versioned HEAD from cluster SoA
                    Assert.That(healths[idx].Current, Is.GreaterThanOrEqualTo(100));
                    Assert.That(healths[idx].Max, Is.EqualTo(200));

                    entityCount++;
                }
            }
            Assert.That(entityCount, Is.EqualTo(count));
        }
    }

    [Test]
    public void BulkIteration_AfterVersionedWrite_SeesNewHead()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(1, 1);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        // Write Versioned component
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(id);
            ref var hp = ref entity.Write(ClVMixed.Health);
            hp.Current = 42;
            tx.Commit();
        }

        // Bulk iterate — should see updated HEAD
        using (var tx = dbe.CreateQuickTransaction())
        {
            using var accessor = tx.For<ClVMixed>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ulong bits = cluster.OccupancyBits;
                var healths = cluster.GetReadOnlySpan(ClVMixed.Health);
                while (bits != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    Assert.That(healths[idx].Current, Is.EqualTo(42));
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Multiple Entities & Destroy
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultipleEntities_AllHeadsCorrect()
    {
        using var dbe = SetupEngine();
        const int count = 50;
        var ids = new EntityId[count];

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var pos = new ClV5SvPos(i, i);
                var hp = new ClV5Health(i * 10, 1000);
                ids[i] = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            }
            tx.Commit();
        }

        // Write to every other entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i += 2)
            {
                var entity = tx.OpenMut(ids[i]);
                ref var hp = ref entity.Write(ClVMixed.Health);
                hp.Current = 999;
            }
            tx.Commit();
        }

        // Verify all values
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var entity = tx.Open(ids[i]);
                ref readonly var hp = ref entity.Read(ClVMixed.Health);
                int expected = (i % 2 == 0) ? 999 : i * 10;
                Assert.That(hp.Current, Is.EqualTo(expected), $"Entity {i} has wrong Health");
            }
        }
    }

    [Test]
    public void Destroy_VersionedClusterEntity_SlotFreed()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(1, 2);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        // Entity should not be found
        using (var tx = dbe.CreateQuickTransaction())
        {
            bool found = tx.TryOpen(id, out var entity);
            Assert.That(found, Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Triple Archetype (2 SV + 1 Versioned)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TripleArchetype_AllComponentsAccessible()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(10, 20);
            var mov = new ClV5SvMov(1, -1);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVTriple>(ClVTriple.Pos.Set(in pos), ClVTriple.Mov.Set(in mov), ClVTriple.Health.Set(in hp));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            Assert.That(entity.Read(ClVTriple.Pos).X, Is.EqualTo(10));
            Assert.That(entity.Read(ClVTriple.Mov).Vx, Is.EqualTo(1));
            Assert.That(entity.Read(ClVTriple.Health).Current, Is.EqualTo(100));
        }
    }

    [Test]
    public void TripleArchetype_WriteSvAndVersioned_BothUpdate()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClV5SvPos(10, 20);
            var mov = new ClV5SvMov(1, -1);
            var hp = new ClV5Health(100, 200);
            id = tx.Spawn<ClVTriple>(ClVTriple.Pos.Set(in pos), ClVTriple.Mov.Set(in mov), ClVTriple.Health.Set(in hp));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(id);
            ref var pos = ref entity.Write(ClVTriple.Pos);
            pos.X = 30;
            ref var hp = ref entity.Write(ClVTriple.Health);
            hp.Current = 50;
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            Assert.That(entity.Read(ClVTriple.Pos).X, Is.EqualTo(30));
            Assert.That(entity.Read(ClVTriple.Mov).Vx, Is.EqualTo(1)); // Unchanged
            Assert.That(entity.Read(ClVTriple.Health).Current, Is.EqualTo(50));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. Cluster State Verification
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterState_HasCorrectVersionedLayout()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClVMixed>.Metadata;
        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState, Is.Not.Null);

        var layout = es.ClusterState.Layout;
        Assert.That(layout.SlotToVersionedIndex, Is.Not.Null);

        // Slot 0 = ClV5SvPos (SV) → -1
        // Slot 1 = ClV5Health (Versioned) → 0
        Assert.That(layout.SlotToVersionedIndex[0], Is.EqualTo(-1));
        Assert.That(layout.SlotToVersionedIndex[1], Is.EqualTo(0));
    }

    [Test]
    public void ManyEntities_AcrossClusters_AllCorrect()
    {
        using var dbe = SetupEngine();
        const int count = 200; // Should span multiple clusters
        var ids = new EntityId[count];

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var pos = new ClV5SvPos(i, i);
                var hp = new ClV5Health(i, 1000);
                ids[i] = tx.Spawn<ClVMixed>(ClVMixed.Pos.Set(in pos), ClVMixed.Health.Set(in hp));
            }
            tx.Commit();
        }

        // Verify all values
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var entity = tx.Open(ids[i]);
                Assert.That(entity.Read(ClVMixed.Health).Current, Is.EqualTo(i), $"Entity {i} wrong Health");
                Assert.That(entity.Read(ClVMixed.Pos).X, Is.EqualTo(i), $"Entity {i} wrong Pos.X");
            }
        }

        // Write to all and verify
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var entity = tx.OpenMut(ids[i]);
                ref var hp = ref entity.Write(ClVMixed.Health);
                hp.Current = count - i;
            }
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var entity = tx.Open(ids[i]);
                Assert.That(entity.Read(ClVMixed.Health).Current, Is.EqualTo(count - i));
            }
        }
    }
}
