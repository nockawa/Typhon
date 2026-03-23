using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only SV component with indexed field for stress tests (361)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.SVStress.SvStressData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SvStressData
{
    [Index(AllowMultiple = true)]
    public int Category;
    public int Value;
    public SvStressData(int cat, int val) { Category = cat; Value = val; }
}

[Archetype(361)]
class SvStressArch : Archetype<SvStressArch>
{
    public static readonly Comp<SvStressData> Data = Register<SvStressData>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: SV concurrent mutations stress
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class SvStressTests : TestBase<SvStressTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup() => Archetype<SvStressArch>.Touch();

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvStressData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnSv(Transaction tx, int category, int value = 0)
    {
        var d = new SvStressData(category, value);
        return tx.Spawn<SvStressArch>(SvStressArch.Data.Set(in d));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1 — Concurrent mutations across threads, all updates reflected
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConcurrentMutations_SvIndexed_AllUpdatesReflected()
    {
        using var dbe = SetupEngine();
        var comp = SvStressArch.Data;
        const int entityCount = 8;

        // Spawn entities with Category=0
        var ids = new EntityId[entityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                ids[i] = SpawnSv(tx, 0, i);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Each thread mutates its own entity: Category → thread index + 1
        var barrier = new Barrier(entityCount);
        var threads = new Thread[entityCount];
        for (int t = 0; t < entityCount; t++)
        {
            int threadIdx = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(ids[threadIdx]).Write(comp) = new SvStressData(threadIdx + 1, threadIdx * 10);
                tx.Commit();
            });
            threads[t].Start();
        }
        foreach (var t in threads)
        {
            t.Join();
        }
        dbe.WriteTickFence(2);

        // All entities should have unique Category 1..8
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 0).Count(), Is.EqualTo(0));
            for (int i = 1; i <= entityCount; i++)
            {
                Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == i).Count(), Is.EqualTo(1), $"Category {i}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2 — High mutation rate with shadow buffer accumulation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void HighMutationRate_ShadowBufferAccumulates()
    {
        using var dbe = SetupEngine();
        var comp = SvStressArch.Data;
        const int mutations = 200;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 0, 0);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Many mutations in one tick — shadow only captures first, all mutations in-place
        for (int i = 0; i < mutations; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.OpenMut(id).Write(comp) = new SvStressData(i + 1, i);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        // Only final value should be in index
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 0).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == mutations).Count(), Is.EqualTo(1));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3 — Spawn + mutate + destroy in same tick → index entry removed
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnMutateThenDestroy_IndexClean()
    {
        using var dbe = SetupEngine();
        var comp = SvStressArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 50, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate + destroy in same tick
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new SvStressData(99, 200);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 50).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 99).Count(), Is.EqualTo(0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4 — Readers see consistent state while writers mutate
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConcurrentQueryDuringMutations_ConsistentResults()
    {
        using var dbe = SetupEngine();
        var comp = SvStressArch.Data;
        const int entityCount = 20;

        // Spawn entities with Category=1
        var ids = new EntityId[entityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                ids[i] = SpawnSv(tx, 1, i);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Writer thread: mutate half to Category=2
        var writerDone = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            for (int i = 0; i < entityCount / 2; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(ids[i]).Write(comp) = new SvStressData(2, i * 10);
                tx.Commit();
            }
            writerDone.Set();
        });

        // Reader thread: query counts should always be consistent (cat1 + cat2 == total)
        bool readError = false;
        var reader = new Thread(() =>
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                using var tx = dbe.CreateQuickTransaction();
                // Before tick fence, index still has old values — total Category=1 count is still entityCount
                int cat1 = tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 1).Count();
                if (cat1 != entityCount)
                {
                    readError = true;
                    break;
                }
            }
        });

        writer.Start();
        reader.Start();
        writer.Join();
        reader.Join();
        writerDone.Dispose();

        // Before tick fence: index still shows all as Category=1 (shadow captures old, index not yet updated)
        Assert.That(readError, Is.False, "Reader saw inconsistent index count before tick fence");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5 — Multiple tick fences → index converges correctly
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultipleTickFences_IndexConverges()
    {
        using var dbe = SetupEngine();
        var comp = SvStressArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 1, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Tick 2: mutate 1→2
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new SvStressData(2, 200);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 1).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 2).Count(), Is.EqualTo(1));
        }

        // Tick 3: mutate 2→3
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new SvStressData(3, 300);
            tx.Commit();
        }
        dbe.WriteTickFence(3);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 2).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 3).Count(), Is.EqualTo(1));
        }

        // Tick 4: mutate 3→4
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new SvStressData(4, 400);
            tx.Commit();
        }
        dbe.WriteTickFence(4);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 3).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<SvStressArch>().WhereField<SvStressData>(d => d.Category == 4).Count(), Is.EqualTo(1));
        }
    }
}
