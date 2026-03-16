using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct CompE_Eng
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompE";
    
    public int A;
    public ComponentCollection<int> Collection;

    public static CompE_Eng Create(Random rand) => new(rand.Next());

    public CompE_Eng(int a)
    {
        A = a;
        Collection = default;
    }
    
    public void Update(Random rand)
    {
        A = rand.Next();
    }

    public override string ToString() => $"A={A}";
}

class ComponentCollectionTests : TestBase<ComponentCollectionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompAEArch>.Touch();
    }

    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompE_Eng>();
        base.RegisterComponents(dbe);
    }

    [Test]
    public void Collection_CreateReadUpdate_Successful()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);

                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }

            entityId = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
            Assert.That(entityId.IsNull, Is.False, "A valid entity id must not be null");

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.Open(entityId);
            var e2 = entity.Read(CompAEArch.E);

            using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);
            Span<int> allItems = stackalloc int[cca.ElementCount];
            cca.GetAllElements(allItems);
            Assert.That(allItems.Length, Is.EqualTo(10), "There should be 10 items");

            for (int i = 0; i < 10; i++)
            {
                var actual = allItems[i];
                Assert.That(allItems.Contains(actual), Is.True);
            }
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.OpenMut(entityId);
            ref var e2 = ref entity.Write(CompAEArch.E);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);

                for (int i = 10; i < 20; i++)
                {
                    cca.Add(i);
                }
            }

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.Open(entityId);
            var e2 = entity.Read(CompAEArch.E);

            using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);
            Span<int> allItems = stackalloc int[cca.ElementCount];
            cca.GetAllElements(allItems);
            for (int i = 0; i < 20; i++)
            {
                Assert.That(allItems.Contains(i), Is.True);
            }
        }
    }

    [Test]
    public void Collection_RefCounter()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);

                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }

            entityId = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
            Assert.That(entityId.IsNull, Is.False, "A valid entity id must not be null");

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.OpenMut(entityId);
            ref var e2 = ref entity.Write(CompAEArch.E);

            // Change A to trigger the creation of a new revision during the Write call above
            e2.A = 12;

            {
                Assert.That(t.GetComponentCollectionRefCounter(ref e2.Collection), Is.EqualTo(2), "RefCounter should be 2, because shared by 2 revisions");
            }

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        // Flush deferred cleanup so the old revision is removed and refcount decremented
        dbe.FlushDeferredCleanups();

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.Open(entityId);
            var e2 = entity.Read(CompAEArch.E);

            Assert.That(t.GetComponentCollectionRefCounter(ref e2.Collection), Is.EqualTo(1), "RefCounter should be 1, because there is now only one revision for this component");
        }
    }
}