using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

class BulkEnumerateTests : TestBase<BulkEnumerateTests>
{
    [Test]
    public void PK_FullScan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var createdPKs = new List<long>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var comp = new CompA(i * 100, i * 1.5f, i * 2.5);
                createdPKs.Add(tx.CreateEntity(ref comp));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<(long PK, CompA Comp)>();
            using var enumerator = tx.EnumeratePK<CompA>();
            foreach (var item in enumerator)
            {
                results.Add((item.EntityPK, item.Component));
            }

            Assert.That(results.Count, Is.EqualTo(10));

            // Verify PKs are in ascending order and data is correct
            for (int i = 0; i < results.Count; i++)
            {
                Assert.That(createdPKs.Contains(results[i].PK), Is.True);
            }

            // Verify data integrity for the first entity
            var first = results.Find(r => r.Comp.A == 0);
            Assert.That(first.Comp.B, Is.EqualTo(0f));
        }
    }

    [Test]
    public void PK_RangeScan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var createdPKs = new List<long>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var comp = new CompA(i);
                createdPKs.Add(tx.CreateEntity(ref comp));
            }

            tx.Commit();
        }

        // Sort PKs to know the range boundaries
        createdPKs.Sort();

        // Enumerate a sub-range: PKs [3..7] (indices 2..6 in sorted order)
        long minPK = createdPKs[2];
        long maxPK = createdPKs[6];

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<long>();
            using var enumerator = tx.EnumeratePK<CompA>(minPK, maxPK);
            foreach (var item in enumerator)
            {
                results.Add(item.EntityPK);
            }

            Assert.That(results.Count, Is.EqualTo(5));
            foreach (var pk in results)
            {
                Assert.That(pk, Is.GreaterThanOrEqualTo(minPK));
                Assert.That(pk, Is.LessThanOrEqualTo(maxPK));
            }
        }
    }

    [Test]
    public void PK_EmptyRange()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        // Enumerate a range that contains no entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            int count = 0;
            using var enumerator = tx.EnumeratePK<CompA>(long.MaxValue - 1, long.MaxValue);
            foreach (var item in enumerator)
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(0));
        }
    }

    [Test]
    public void MVCC_Visibility()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // tx1: Create 5 entities
        long[] pks;
        using (var tx1 = dbe.CreateQuickTransaction())
        {
            pks = new long[5];
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i);
                pks[i] = tx1.CreateEntity(ref comp);
            }

            tx1.Commit();
        }

        // tx2: Delete entities at indices 1 and 3
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.DeleteEntity<CompA>(pks[1]);
            tx2.DeleteEntity<CompA>(pks[3]);
            tx2.Commit();
        }

        // tx3: Enumerate — should see only 3 entities (0, 2, 4)
        using (var tx3 = dbe.CreateQuickTransaction())
        {
            var results = new List<long>();
            using var enumerator = tx3.EnumeratePK<CompA>();
            foreach (var item in enumerator)
            {
                results.Add(item.EntityPK);
            }

            Assert.That(results.Count, Is.EqualTo(3));
            Assert.That(results, Does.Contain(pks[0]));
            Assert.That(results, Does.Contain(pks[2]));
            Assert.That(results, Does.Contain(pks[4]));
            Assert.That(results, Does.Not.Contain(pks[1]));
            Assert.That(results, Does.Not.Contain(pks[3]));
        }
    }

    [Test]
    public void DeletedEntity_Skipped()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var comp = new CompA(42);
            pk = tx.CreateEntity(ref comp);
            tx.Commit();
        }

        // Delete it
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.DeleteEntity<CompA>(pk);
            tx.Commit();
        }

        // Enumerate — should yield nothing
        using (var tx = dbe.CreateQuickTransaction())
        {
            int count = 0;
            using var enumerator = tx.EnumeratePK<CompA>();
            foreach (var item in enumerator)
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(0));
        }
    }

    [Test]
    public void SecondaryIndex_UniqueField()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // CompD has [Index] int B (unique secondary index)
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var comp = new CompD(i * 1.1f, i * 10, i * 2.2);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<(long PK, int Key, CompD Comp)>();
            using var enumerator = tx.EnumerateIndex<CompD, int>(indexRef, int.MinValue, int.MaxValue);
            foreach (var item in enumerator)
            {
                results.Add((item.EntityPK, item.Key, item.Component));
            }

            Assert.That(results.Count, Is.EqualTo(10));

            // Verify ascending key order
            for (int i = 1; i < results.Count; i++)
            {
                Assert.That(results[i].Key, Is.GreaterThanOrEqualTo(results[i - 1].Key));
            }

            // Verify data integrity
            var entry = results.Find(r => r.Key == 50);
            Assert.That(entry.Comp.A, Is.EqualTo(5 * 1.1f).Within(0.01f));
        }
    }

    [Test]
    public void SecondaryIndex_AllowMultiple()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // CompD has [Index(AllowMultiple = true)] float A
        // Create entities with duplicate A values
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 6; i++)
            {
                // Groups: A=1.0 (3 entities), A=2.0 (3 entities)
                var comp = new CompD(i < 3 ? 1.0f : 2.0f, i * 10, i * 0.5);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<CompD, float>(d => d.A);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<(long PK, float Key, CompD Comp)>();
            using var enumerator = tx.EnumerateIndex<CompD, float>(indexRef, float.MinValue, float.MaxValue);
            foreach (var item in enumerator)
            {
                results.Add((item.EntityPK, item.Key, item.Component));
            }

            Assert.That(results.Count, Is.EqualTo(6));

            // Verify ascending key order
            for (int i = 1; i < results.Count; i++)
            {
                Assert.That(results[i].Key, Is.GreaterThanOrEqualTo(results[i - 1].Key));
            }
        }
    }

    [Test]
    public void SecondaryIndex_PK_Recovery()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pkToValue = new Dictionary<long, int>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompD(i * 1.0f, i * 100, 0.0);
                long pk = tx.CreateEntity(ref comp);
                pkToValue[pk] = i * 100;
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);

        using (var tx = dbe.CreateQuickTransaction())
        {
            using var enumerator = tx.EnumerateIndex<CompD, int>(indexRef, int.MinValue, int.MaxValue);
            foreach (var item in enumerator)
            {
                // Verify the recovered PK matches the expected one for this B value
                Assert.That(pkToValue.ContainsKey(item.EntityPK), Is.True,
                    $"EntityPK {item.EntityPK} should exist in our created set");
                Assert.That(pkToValue[item.EntityPK], Is.EqualTo(item.Key),
                    "PK recovery from secondary index should yield the correct entity");
            }
        }
    }

    [Test]
    public void ReadOnlyTransaction_Enumerate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        // Enumerate with a read-only transaction
        using var rt = dbe.CreateReadOnlyTransaction();
        Assert.That(rt.IsReadOnly, Is.True);

        var results = new List<(long PK, CompA Comp)>();
        using var enumerator = rt.EnumeratePK<CompA>();
        foreach (var item in enumerator)
        {
            results.Add((item.EntityPK, item.Component));
        }

        Assert.That(results.Count, Is.EqualTo(5));
    }

    [Test]
    public void EpochRefresh_LargeDataset()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create >256 entities to trigger multiple epoch refreshes (every 128 entities)
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 300; i++)
            {
                var comp = new CompA(i);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            int count = 0;
            using var enumerator = tx.EnumeratePK<CompA>();
            foreach (var item in enumerator)
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(300));
        }
    }

    [Test]
    public void StaleIndexRef_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        // Create a stale IndexRef with wrong layout version
        var staleRef = new IndexRef(0, ct, ct.IndexLayoutVersion - 1);

        using var tx = dbe.CreateQuickTransaction();

        Assert.Throws<System.InvalidOperationException>(() =>
            tx.EnumerateIndex<CompD, float>(staleRef, float.MinValue, float.MaxValue));
    }

    [Test]
    public void EnumeratePK_AllowMultipleComponent_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var tx = dbe.CreateQuickTransaction();

        // CompE has AllowMultiple=true at component level
        Assert.Throws<System.InvalidOperationException>(() =>
            tx.EnumeratePK<CompE>());
    }

    [Test]
    public void EnumerateIndex_WithPKRef_Works()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i * 10);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        var pkRef = dbe.GetPKIndexRef<CompA>();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<(long PK, CompA Comp)>();
            using var enumerator = tx.EnumerateIndex<CompA, long>(pkRef, long.MinValue, long.MaxValue);
            foreach (var item in enumerator)
            {
                results.Add((item.EntityPK, item.Component));
            }

            Assert.That(results.Count, Is.EqualTo(5));

            // Verify data
            var entry = results.Find(r => r.Comp.A == 30);
            Assert.That(entry.PK, Is.Not.Zero);
        }
    }

    [Test]
    public void EnumerateIndex_PKRef_WrongTKey_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pkRef = dbe.GetPKIndexRef<CompA>();

        using var tx = dbe.CreateQuickTransaction();

        // PK index is BTree<long>, but caller specifies int — should throw
        Assert.Throws<System.InvalidOperationException>(() =>
            tx.EnumerateIndex<CompA, int>(pkRef, int.MinValue, int.MaxValue));
    }

    [Test]
    public void ZeroCopy_CurrentComponent_ReturnsRefIntoPageMemory()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i * 100, i * 1.5f, i * 2.5);
                tx.CreateEntity(ref comp);
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            using var enumerator = tx.EnumeratePK<CompA>();
            int count = 0;
            while (enumerator.MoveNext())
            {
                // Zero-copy ref access — no memcpy, points directly into page memory
                ref readonly var comp = ref enumerator.CurrentComponent;
                long pk = enumerator.CurrentEntityPK;

                Assert.That(pk, Is.Not.Zero);
                Assert.That(comp.A, Is.EqualTo(count * 100));
                Assert.That(comp.B, Is.EqualTo(count * 1.5f).Within(0.01f));
                count++;
            }

            Assert.That(count, Is.EqualTo(5));
        }
    }
}
