using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests.Database_Engine;

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

        long e1;
        {
            using var t = dbe.CreateTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);

                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }
            
            e1 = t.CreateEntity(ref a, ref e);
            Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a revision of 1");
            
            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateTransaction();

            var res = t.ReadEntity(e1, out CompE_Eng e2);
            Assert.That(res, Is.True);
            
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
            using var t = dbe.CreateTransaction();

            var res = t.ReadEntity(e1, out CompE_Eng e2);
            Assert.That(res, Is.True);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);

                for (int i = 10; i < 20; i++)
                {
                    cca.Add(i);
                }
            }
            
            res = t.UpdateEntity(e1, ref e2);
            Assert.That(res, Is.True, "Updated entity should be successful");
            
            res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }
        
        {
            using var t = dbe.CreateTransaction();

            var res = t.ReadEntity(e1, out CompE_Eng e2);
            Assert.That(res, Is.True);

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

        long e1;
        {
            using var t = dbe.CreateTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);

                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }
            
            e1 = t.CreateEntity(ref a, ref e);
            Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a revision of 1");
            
            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateTransaction();

            var res = t.ReadEntity(e1, out CompE_Eng e2);
            Assert.That(res, Is.True);

            // Change A to trigger the creation of a new revision during the Update call below
            e2.A = 12;
            
            res = t.UpdateEntity(e1, ref e2);
            Assert.That(res, Is.True, "Updated entity should be successful");

            {
                Assert.That(t.GetComponentCollectionRefCounter(ref e2.Collection), Is.EqualTo(2), "RefCounter should be 2, because shared by 2 revisions");
            }
            
            res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }
        
        {
            using var t = dbe.CreateTransaction();

            var res = t.ReadEntity(e1, out CompE_Eng e2);
            Assert.That(res, Is.True);

            Assert.That(t.GetComponentCollectionRefCounter(ref e2.Collection), Is.EqualTo(1), "RefCounter should be 1, because there is now only one revision for this component");
        }
    }    
}