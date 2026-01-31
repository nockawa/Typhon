using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

class TransactionTests : TestBase<TransactionTests>
{
    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL2), [2])]
    public void CreateComp_SingleTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction, bool rollback)
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long e1;
            var a = new CompA(2);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");

            long[] noiseIds = null;
            if (noiseMode >= 1)
            {
                noiseIds = CreateNoiseCompA(dbe);
            }

            {
                using var t = dbe.CreateTransaction();

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }
                
                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a revision of 1");

                if (rollback)
                {
                    var res = t.Rollback();
                    Assert.That(res, Is.True, "Transaction should be rollbacked successfully");
                    Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Rolling back three components should lead to at least three operations");
                }
                else
                {
                    var res = t.Commit();
                    Assert.That(res, Is.True, "Transaction should be successful");
                    Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least three operations");
                }
            }

            if (rollback)
            {
                using var t = dbe.CreateTransaction();
                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.False, "Entity read on a rolled back component should not be successful");
            }
            else
            {
                using var t = dbe.CreateTransaction();
                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.True, "Entity read on an existing component should be successful");
                Assert.That(ar.A, Is.EqualTo(a.A), $"Component should have a value of {a.A}");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Component should have a revision of 1 as we only created it.");
            }
        }
    }
    
    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), [2])]
    public void ReadComp_SingleTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction)
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long[] noiseIds = null;
            if (noiseMode >= 1)
            {
                noiseIds = CreateNoiseCompA(dbe);
            }

            using var t = dbe.CreateTransaction();

            var a = new CompA(2);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            
            var e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a revision of 1");

            if (noiseMode >= 2)
            {
                UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
            }

            var res = t.ReadEntity(e1, out CompA ar);
            Assert.That(res, Is.True, "Entity read on an existing component should be successful");
            Assert.That(ar.A, Is.EqualTo(a.A), $"The read component should have a value of {a.A}");
            
            res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
            Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least 3 operations");
        }
    }
    
    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), [2])]
    public void ReadComp_SeparateTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction)
    {
        var a = new CompA(3);
        var b = new CompB(1, 1.2f);
        var c = new CompC("Porcupine Tree");
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long[] noiseIds = null;
            long e1;
            int arev;
            {
                using var t = dbe.CreateTransaction();

                if (noiseMode >= 1)
                {
                    noiseIds = CreateNoiseCompA(dbe, noiseOwnTransaction ? null : t);
                }

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                var createRev = t.GetComponentRevision<CompA>(e1);
                Assert.That(createRev, Is.EqualTo(1), "Creating a component should lead to a revision of 1");
            
                var res = t.Commit();
                Assert.That(res, Is.True, "Transaction commit should be successful");
                Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least three operations");
                arev = t.GetComponentRevision<CompA>(e1);
                Assert.That(arev, Is.EqualTo(createRev), "Committing shouldn't alter the component revision");
            }

            {
                using var t = dbe.CreateTransaction();

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }
                
                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.True, "Reading an existing component should be succesful");
                Assert.That(ar.A, Is.EqualTo(a.A), $"The read value should be {a.A}");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(arev), "Reading a component shouldn't alter its revision");
            }
        }
    }
    
    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), [2])]
    public void UpdateComp_SingleTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction)
    {
        var a = new CompA(1);
        var b = new CompB(1, 1.2f);
        var c = new CompC("Porcupine Tree");
        var aChanged = 12;
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long[] noiseIds = null;
            long e1;
            {
                using var t = dbe.CreateTransaction();

                if (noiseMode >= 1)
                {
                    noiseIds = CreateNoiseCompA(dbe, noiseOwnTransaction ? null : t);
                }

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }
                
                a.A = aChanged;
                t.UpdateEntity(e1, ref a);
                
                var res = t.Commit();
                Assert.That(res, Is.True, "Transaction commit should be successful");
                Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least three operations");
                Assert.That(a.A, Is.EqualTo(aChanged), "Update after create in the same transaction should have the updated value");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Update after create in the same transaction should lead to one revision only");
            }
            
            {
                using var t = dbe.CreateTransaction();

                if (noiseMode >= 3)
                {
                    ReadNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }
                
                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.True, "Entity read on an existing component should be successful");
                Assert.That(ar.A, Is.EqualTo(aChanged), $"Component should have a value of {aChanged}");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Component should have a revision of 1 as we only created it.");
            }
        }
    }

    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL2), [3])]
    public void UpdateComp_SeparateTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction, bool readBeforeUpdate)
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            var a = new CompA(2);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            long e1;
            {
                using var t = dbe.CreateTransaction();

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                var res = t.Commit();
                Assert.That(res, Is.True);
            }
            
            long[] noiseIds = null;
            {
                using var t = dbe.CreateTransaction();

                if (noiseMode >= 1)
                {
                    noiseIds = CreateNoiseCompA(dbe, noiseOwnTransaction ? null : t);
                }
                
                if (readBeforeUpdate)
                {
                    var rr = t.ReadEntity(e1, out CompA ar);
                    Assert.That(rr, Is.True);
                    Assert.That(ar.A, Is.EqualTo(a.A), "Read in the second transaction should retrieve the component created in the earlier one");
                }
                
                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }
                
                var a2 = new CompA(12);
                t.UpdateEntity(e1, ref a2);

                if (noiseMode >= 3)
                {
                    ReadNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                var res = t.ReadEntity(e1, out CompA ar2);
                Assert.That(res, Is.True);
                Assert.That(ar2.A, Is.EqualTo(a2.A), "Read after update should reflect the updated value");
                
                res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(1), "Committing three components should lead to at least one operation");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2), "Update after create in different transaction should lead to two distinct revisions");
                Assert.That(t.GetRevisionCount<CompA>(e1), Is.EqualTo(1), "Committing an update should remove the previous revision (as the transaction is alone).");
            }
            
            {
                using var t = dbe.CreateTransaction();

                var res = t.ReadEntity(e1, out CompA a2);
                Assert.That(res, Is.True);
                Assert.That(a2.A, Is.EqualTo(12), "Read after update should reflect the updated value");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2), "Update after create in different transaction should lead to two distinct revisions");
            }
        }
    }

    [Test]
    public void CreateReadUpdate_MultipleComponent_SuccessfulOperation()
    {
        long e1;
        var a = new CompA(1);
        Span<CompE> eList = stackalloc CompE[16];
        for (int i = 0; i < eList.Length; i++)
        {
            eList[i] = new CompE(12.0f + i, i, 3*i);
        }
        
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            {
                using var t = dbe.CreateTransaction();

                e1 = t.CreateEntity(ref a, eList);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                var res = t.Commit();
                Assert.That(res, Is.True);
            }
            
            {
                using var t = dbe.CreateTransaction();

                var res = t.ReadEntity(e1, out a, out CompE[] eList2);
                Assert.That(res, Is.True);
                Assert.That(eList2.Length, Is.EqualTo(16));

                for (int i = 0; i < 16; i++)
                {
                    Assert.That(eList[i], Is.EqualTo(eList2[i]));
                }
            }
            
        }
    }    

    [Test]
    public void ComponentRevisionTortureTest()
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            var curRevisionCount = 0;
            long e1;
            {
                using var t = dbe.CreateTransaction();

                var a = new CompA(2, 3, 4);
                e1 = t.CreateEntity(ref a);
                curRevisionCount++;
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                var res = t.Commit();
                Assert.That(res, Is.True);
            }

            // Let's keep a long-running transaction that will prevent cleanup of old revisions
            var longRunningValue = new CompA(200, 300, 400);
            var longRunningTransaction = dbe.CreateTransaction();
            {
                longRunningTransaction.UpdateEntity(e1, ref longRunningValue);
                curRevisionCount++;
            }
            
            // Generate an array storing ranges of commit and rollback operations totalling 100 operations
            int[] operations = [12, 5, 20, 3, 15, 10, 8, 7, 20];
            
            var commit = true;
            var rbCount = 0;
            // var revisions = new List<(bool, CompA)>(operations.Sum());
            foreach (int opCount in operations)
            {
                if (!commit)
                {
                    rbCount += opCount;
                }
                
                for (int i = 0; i < opCount; i++)
                {
                    using var t = dbe.CreateTransaction();

                    var a = CompA.Create(Rand);
                    t.UpdateEntity(e1, ref a);
                    curRevisionCount++;
                    
                    // revisions.Add((commit, a));
                    
                    var res = commit ? t.Commit() : t.Rollback();
                    Assert.That(res, Is.True);
                }

                commit = !commit;
            }

            // Commit the long-running transaction, this should trigger a cleanup of old revisions, keeping only the last one which is the long running one
            {
                using var readTransaction = dbe.CreateTransaction();
                Assert.That(readTransaction.GetRevisionCount<CompA>(e1), Is.EqualTo(curRevisionCount-rbCount), "The number of revisions stored should match the number of committed updates plus the original creation");
                var res = longRunningTransaction.Commit();
                Assert.That(res, Is.True);
                longRunningTransaction.Dispose();
                Assert.That(readTransaction.GetRevisionCount<CompA>(e1), Is.EqualTo(1), "After committing the long-running transaction, only one revision should remain");
            }

            // Create a transaction to read and check
            {
                using var readTransaction = dbe.CreateTransaction();
                readTransaction.ReadEntity(e1, out CompA aFinal);
                Assert.That(aFinal, Is.EqualTo(longRunningValue), "The last committed revision should be the one remaining");
            }
            
        }
    }

    /*
    [Test]
    public void CreateAndReadInsideSameTransaction()
    {
        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            var e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.ReadEntity(e1, out CompA ca, out CompB cb, out CompC cc);

            Assert.That(a, Is.EqualTo(ca));
            Assert.That(b, Is.EqualTo(cb));
            Assert.That(c, Is.EqualTo(cc));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void CreateAndReadInsideDifferentTransaction()
    {
        var a = new CompA(1);
        var b = new CompB(1, 1.2f);
        var c = new CompC("Porcupine Tree");
        long e1;

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);

            t.Commit();
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompA ca, out CompB cb, out CompC cc);

            Assert.That(a, Is.EqualTo(ca));
            Assert.That(b, Is.EqualTo(cb));
            Assert.That(c, Is.EqualTo(cc));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void CreateAndUpdateInsideSameTransaction()
    {
        long e1;
        var ca = new CompA(2);
        var cb = new CompB(3, 4.2f);
        var cc = new CompC("Porcupine Tree");

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.UpdateEntity(e1, ref ca, ref cb, ref cc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompA a, out CompB b, out CompC c);

            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));
            Assert.That(a, Is.EqualTo(ca));
            Assert.That(b, Is.EqualTo(cb));
            Assert.That(c, Is.EqualTo(cc));
        }
    }

    [Test]
    public void CreateAndUpdateInsideDifferentTransaction()
    {
        long e1;
        var ca = new CompA(2);
        var cb = new CompB(3, 4.2f);
        var cc = new CompC("Porcupine Tree");

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            t.UpdateEntity(e1, ref ca, ref cb, ref cc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompA a, out CompB b, out CompC c);

            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));
            Assert.That(a, Is.EqualTo(ca));
            Assert.That(b, Is.EqualTo(cb));
            Assert.That(c, Is.EqualTo(cc));
        }
    }

    [Test]
    public void CreateAndDeleteInsideSameTransaction()
    {
        long e1;

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.DeleteEntity<CompA, CompB, CompC>(e1);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            var res = t.ReadEntity(e1, out CompA _, out CompB _, out CompC _);

            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(-1));
            Assert.That(res, Is.False);
        }
    }

    [Test]
    public void CreateAndDeleteInsideDifferentTransaction()
    {
        long e1;

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.DeleteEntity<CompA, CompB, CompC>(e1);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            var res = t.ReadEntity(e1, out CompA _, out CompB _, out CompC _);

            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(-1));
            Assert.That(res, Is.False);
        }
    }

    [Test]
    public void CreateAndReadInsideSameTransactionRollbacked()
    {
        long e1;
        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.ReadEntity(e1, out CompA ca, out CompB cb, out CompC cc);

            Assert.That(a, Is.EqualTo(ca));
            Assert.That(b, Is.EqualTo(cb));
            Assert.That(c, Is.EqualTo(cc));

            var res = t.Rollback();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }
        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var res = t.ReadEntity(e1, out CompA _, out CompB _, out CompC _);
            Assert.That(res, Is.False);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(-1));
        }
    }

    [Test]
    public void CreateAndReadInsideDifferentTransactionRollbacked()
    {
        var a = new CompA(1);
        var b = new CompB(1, 1.2f);
        var c = new CompC("Porcupine Tree");
        long e1;

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.Commit();
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompA ca, out CompB cb, out CompC cc);

            Assert.That(ca, Is.EqualTo(a));
            Assert.That(cb, Is.EqualTo(b));
            Assert.That(cc, Is.EqualTo(c));

            var res = t.Rollback();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }
        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var res = t.ReadEntity(e1, out CompA ba, out CompB bb, out CompC bc);
            Assert.That(res, Is.True);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));
            Assert.That(ba, Is.EqualTo(a));
            Assert.That(bb, Is.EqualTo(b));
            Assert.That(bc, Is.EqualTo(c));
        }
    }

    [Test]
    public void CreateAndUpdateInsideSameTransactionRollbacked()
    {
        long e1;
        var oa = new CompA(1);
        var ob = new CompB(1, 1.2f);
        var oc = new CompC("Porcupine Tree");

        var ba = new CompA(2);
        var bb = new CompB(3, 4.2f);
        var bc = new CompC("Porcupine Tree");

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            e1 = t.CreateEntity(ref oa, ref ob, ref oc);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.UpdateEntity(e1, ref ba, ref bb, ref bc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Rollback();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            var res = t.ReadEntity(e1, out CompA _, out CompB _, out CompC _);

            Assert.That(res, Is.False);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(-1));
        }
    }

    [Test]
    public void CreateAndUpdateInsideDifferentTransactionRollbacked()
    {
        long e1;
        var oa = new CompA(1);
        var ob = new CompB(1, 1.2f);
        var oc = new CompC("Porcupine Tree");

        var ba = new CompA(2);
        var bb = new CompB(3, 4.2f);
        var bc = new CompC("Porcupine Tree");

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            e1 = t.CreateEntity(ref oa, ref ob, ref oc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            t.UpdateEntity(e1, ref ba, ref bb, ref bc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Rollback();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompA ra, out CompB rb, out CompC rc);

            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));
            Assert.That(oa, Is.EqualTo(ra));
            Assert.That(ob, Is.EqualTo(rb));
            Assert.That(oc, Is.EqualTo(rc));
        }
    }

    [Test]
    public void CreateAndDeleteInsideSameTransactionRollbacked()
    {
        long e1;
        var oa = new CompA(1);
        var ob = new CompB(1, 1.2f);
        var oc = new CompC("Porcupine Tree");

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            e1 = t.CreateEntity(ref oa, ref ob, ref oc);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.DeleteEntity<CompA, CompB, CompC>(e1);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Rollback();
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(-1));
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            var res = t.ReadEntity(e1, out CompA _, out CompB _, out CompC _);

            Assert.That(res, Is.False);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(-1));
        }
    }

    [Test]
    public void CreateAndDeleteInsideDifferentTransactionRollbacked()
    {
        long e1;
        var oa = new CompA(1);
        var ob = new CompB(1, 1.2f);
        var oc = new CompC("Porcupine Tree");

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            e1 = t.CreateEntity(ref oa, ref ob, ref oc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            t.DeleteEntity<CompA, CompB, CompC>(e1);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Rollback();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompA ra, out CompB rb, out CompC rc);

            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));
            Assert.That(oa, Is.EqualTo(ra));
            Assert.That(ob, Is.EqualTo(rb));
            Assert.That(oc, Is.EqualTo(rc));
        }
    }

    [Test]
    public void IndexTest()
    {
        long e1;
        var ca = new CompD(11.0f, 12, 13.0);

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            var a = new CompD(1.0f, 2, 3.0);
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompD>(e1), Is.EqualTo(1));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(1));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);

            t.UpdateEntity(e1, ref ca);
            Assert.That(t.GetComponentRevision<CompD>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(1));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            using var t = dbe.CreateTransaction(true);
            t.ReadEntity(e1, out CompD a);

            Assert.That(t.GetComponentRevision<CompD>(e1), Is.EqualTo(2));
            Assert.That(a, Is.EqualTo(ca));
        }
    }
    */
    
    [Test]
    public void TransactionNodeTest()
    {
        /*var n1 = Transaction.Transactions.PushHead(1, 1);
        Assert.That(Transaction.Transactions.HeadNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.TailNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetNextNode(n1), Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.GetPrevNode(n1), Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(1));

        var n2 = Transaction.Transactions.PushHead(2, 2);
        Assert.That(Transaction.Transactions.HeadNodeId, Is.EqualTo(n2));
        Assert.That(Transaction.Transactions.TailNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetNextNode(n2), Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.GetPrevNode(n2), Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(1));

        var n3 = Transaction.Transactions.PushHead(3, 3);
        Assert.That(Transaction.Transactions.HeadNodeId, Is.EqualTo(n3));
        Assert.That(Transaction.Transactions.TailNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetNextNode(n3), Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.GetPrevNode(n3), Is.EqualTo(n2));
        Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(1));

        Transaction.Transactions.RemoveNode(n2);
        Assert.That(Transaction.Transactions.HeadNodeId, Is.EqualTo(n3));
        Assert.That(Transaction.Transactions.TailNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetNextNode(n1), Is.EqualTo(n3));
        Assert.That(Transaction.Transactions.GetPrevNode(n3), Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(1));

        Transaction.Transactions.RemoveNode(n3);
        Assert.That(Transaction.Transactions.HeadNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.TailNodeId, Is.EqualTo(n1));
        Assert.That(Transaction.Transactions.GetNextNode(n1), Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(1));

        Transaction.Transactions.RemoveNode(n1);
        Assert.That(Transaction.Transactions.HeadNodeId, Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.TailNodeId, Is.EqualTo(-1));
        Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(0));*/
    }

    [Test]
    public void CompRevTest()
    {
        const int stage0    = 0;
        const int stage1    = 1;
        const int stage2    = 2;
        const int thread1   = 0;
        const int thread2   = 1;
        
        long e1;
        var aR1 = new CompA(1);
        var bR1 = new CompB(1, 1.2f);
        var cR1 = new CompC("Porcupine Tree");

        var bR2 = new CompB(2, 2.4f);

        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create the entity e1, revision R1
        {
            using var t1 = dbe.CreateTransaction();
            Logger.LogInformation("T1 creation time {tick}", t1.TSN);

            e1 = t1.CreateEntity(ref aR1, ref bR1, ref cR1);

            t1.Commit();
        }
        
        using var tw = new ThreadWorkers(Logger);

        Transaction t2 = null;

        // Create transaction T2 early
        tw.AddStage(stage0, thread1, _ =>
        {
            // ReSharper disable once AccessToDisposedClosure
            t2 = dbe.CreateTransaction();
            Logger.LogInformation("T2 creation time {tick}", t2.TSN);
        });

        // Change the entity to create a new revision
        tw.AddStage(stage1, thread2, _ =>
        {
            // ReSharper disable once AccessToDisposedClosure
            using var t3 = dbe.CreateTransaction();
            Logger.LogInformation("T3 creation time {tick}", t3.TSN);
            t3.ReadEntity<CompB>(e1, out var lbR2);

            lbR2 = bR2;

            t3.UpdateEntity(e1, ref lbR2);
            t3.Commit();
        });
        
        // Check that T2 has the first revision of CompB
        tw.AddStage(stage2, thread1, _ =>
        {
            t2.ReadEntity<CompB>(e1, out var lbR1);
            
            Assert.That(t2.GetComponentRevision<CompB>(e1), Is.EqualTo(1));
            Assert.That(lbR1.A, Is.EqualTo(bR1.A));
            Assert.That(lbR1.B, Is.EqualTo(bR1.B));
        });
        
        tw.Run();
        
        dbe.Dispose();
    }
    
    [Test]
    public void MultiThreadTest()
    {
        var t = new ThreadWorkers(Logger);
        t.AddStage(0, 0, c =>
        {
            Thread.Sleep(100);
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
        });
        t.AddStage(0, 1, c =>
        {
            Thread.Sleep(200);
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
            Thread.Sleep(100);
        });

        t.AddStage(1, 0, c =>
        {
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
        });
        t.AddStage(1, 1, c =>
        {
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
        });

        t.Run();

    }

    /// <summary>
    /// Tests that when a component is deleted and all its revisions are cleaned up,
    /// the primary key index entry should be removed.
    /// This test verifies the expected behavior - currently the cleanup is not implemented (TOFIX in Transaction.cs).
    /// </summary>
    [Test]
    public void DeleteComponent_WhenLastRevisionCleanedUp_PrimaryKeyIndexShouldBeRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create entity
        {
            using var t = dbe.CreateTransaction();
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero, "Entity ID should be non-zero");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity exists in primary key index
        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct, Is.Not.Null, "ComponentTable should exist");

        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var exists = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(exists, Is.True, "Entity should exist in primary key index after creation");
            accessor.Dispose();
        }

        // Delete entity - since this is the only transaction, cleanup should happen immediately
        {
            using var t = dbe.CreateTransaction();
            var deleted = t.DeleteEntity<CompA>(e1);
            Assert.That(deleted, Is.True, "Delete should succeed");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity is no longer readable
        {
            using var t = dbe.CreateTransaction();
            var readable = t.ReadEntity(e1, out CompA _);
            Assert.That(readable, Is.False, "Entity should not be readable after deletion");
        }

        // Check primary key index - entry should be removed after cleanup
        // NOTE: This assertion documents the EXPECTED behavior.
        // Currently this will FAIL because the cleanup code is commented out (TOFIX in Transaction.cs line ~971)
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var existsAfterDelete = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(existsAfterDelete, Is.False,
                "Primary key index entry should be removed when component is deleted and all revisions cleaned up");
            
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Tests that when a component is created in one transaction and deleted in another,
    /// the primary key index entry should be removed after cleanup.
    /// </summary>
    [Test]
    public void CreateInOneTxn_DeleteInAnother_PrimaryKeyIndexShouldBeRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create entity in first transaction
        {
            using var t = dbe.CreateTransaction();
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero, "Entity ID should be non-zero");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct, Is.Not.Null, "ComponentTable should exist");

        // Verify entity exists in primary key index after creation
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var exists = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(exists, Is.True, "Entity should exist in primary key index after creation");
            
            accessor.Dispose();
        }

        // Delete entity in second transaction
        {
            using var t = dbe.CreateTransaction();
            var deleted = t.DeleteEntity<CompA>(e1);
            Assert.That(deleted, Is.True, "Delete should succeed");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity is not readable
        {
            using var t = dbe.CreateTransaction();
            var readable = t.ReadEntity(e1, out CompA _);
            Assert.That(readable, Is.False, "Entity should not be readable after deletion");
        }

        // Check primary key index - entry should be removed after cleanup
        // NOTE: This assertion documents the EXPECTED behavior.
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var exists = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(exists, Is.False,
                "Primary key index should not contain entry after entity is deleted");
            
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Tests that when a component is deleted but there's a long-running transaction keeping old revisions,
    /// the primary key index entry should remain until cleanup happens.
    /// </summary>
    [Test]
    [Ignore("Still WIP")]
    public void DeleteComponent_WithLongRunningTransaction_PrimaryKeyIndexRemainsUntilCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create entity
        {
            using var t = dbe.CreateTransaction();
            e1 = t.CreateEntity(ref a);
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        var ct = dbe.GetComponentTable<CompA>();

        // Start a long-running transaction to prevent cleanup
        var longRunningTxn = dbe.CreateTransaction();

        // Read in long-running transaction to establish snapshot
        longRunningTxn.ReadEntity(e1, out CompA _);

        // Delete entity in a separate transaction
        {
            using var t = dbe.CreateTransaction();
            var deleted = t.DeleteEntity<CompA>(e1);
            Assert.That(deleted, Is.True, "Delete should succeed");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // The primary key index should still have the entry because long-running transaction prevents cleanup
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var existsDuringLongTxn = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(existsDuringLongTxn, Is.True,
                "Primary key index should retain entry while long-running transaction holds old revisions");
            
            accessor.Dispose();
        }

        // Verify multiple revisions exist (create + delete)
        {
            using var t = dbe.CreateTransaction();
            var revCount = t.GetRevisionCount<CompA>(e1);
            Assert.That(revCount, Is.GreaterThanOrEqualTo(2),
                "Should have at least 2 revisions (create and delete) while long-running transaction exists");
        }

        // Complete the long-running transaction - this should trigger cleanup
        longRunningTxn.Commit();
        longRunningTxn.Dispose();

        // After cleanup, primary key index entry should be removed
        // NOTE: This assertion documents the EXPECTED behavior.
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var existsAfterCleanup = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(existsAfterCleanup, Is.False,
                "Primary key index entry should be removed after long-running transaction completes and cleanup runs");
            
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Tests that multiple create-delete cycles properly clean up primary key index entries.
    /// </summary>
    [Test]
    public void MultipleCreateDeleteCycles_PrimaryKeyIndexShouldBeCleanedUp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompA>();
        var entityIds = new long[5];

        // Create multiple entities
        for (int i = 0; i < 5; i++)
        {
            using var t = dbe.CreateTransaction();
            var a = new CompA(i * 10);
            entityIds[i] = t.CreateEntity(ref a);
            t.Commit();
        }

        // Verify all entities exist in index
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            for (int i = 0; i < 5; i++)
            {
                var exists = ct.PrimaryKeyIndex.TryGet(entityIds[i], out _, ref accessor);
                Assert.That(exists, Is.True, $"Entity {i} should exist in primary key index");
            }
            accessor.Dispose();
        }

        // Delete entities 0, 2, 4 (odd indices in the array)
        for (int i = 0; i < 5; i += 2)
        {
            using var t = dbe.CreateTransaction();
            t.DeleteEntity<CompA>(entityIds[i]);
            t.Commit();
        }

        // Verify deleted entities are not readable
        for (int i = 0; i < 5; i += 2)
        {
            using var t = dbe.CreateTransaction();
            var readable = t.ReadEntity(entityIds[i], out CompA _);
            Assert.That(readable, Is.False, $"Entity {i} should not be readable after deletion");
        }

        // Verify remaining entities are still readable
        for (int i = 1; i < 5; i += 2)
        {
            using var t = dbe.CreateTransaction();
            var readable = t.ReadEntity(entityIds[i], out CompA _);
            Assert.That(readable, Is.True, $"Entity {i} should still be readable");
        }

        // Check primary key index state
        // NOTE: The assertions for deleted entities document EXPECTED behavior.
        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            // Remaining entities should exist
            for (int i = 1; i < 5; i += 2)
            {
                var exists = ct.PrimaryKeyIndex.TryGet(entityIds[i], out _, ref accessor);
                Assert.That(exists, Is.True, $"Entity {i} should exist in primary key index");
            }

            // Deleted entities should not exist (expected behavior after cleanup)
            for (int i = 0; i < 5; i += 2)
            {
                var exists = ct.PrimaryKeyIndex.TryGet(entityIds[i], out _, ref accessor);
                Assert.That(exists, Is.False,
                    $"Entity {i} should be removed from primary key index after deletion and cleanup");
            }
            
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Tests that rolling back a create operation does not leave an entry in the primary key index.
    /// </summary>
    [Test]
    public void RollbackCreate_PrimaryKeyIndexShouldNotContainEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create and rollback
        {
            using var t = dbe.CreateTransaction();
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Rollback();
            Assert.That(res, Is.True, "Rollback should succeed");
        }

        // Verify entity is not readable
        {
            using var t = dbe.CreateTransaction();
            var readable = t.ReadEntity(e1, out CompA _);
            Assert.That(readable, Is.False, "Entity should not be readable after rollback");
        }

        // Check primary key index - entry should not exist after rollback
        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct, Is.Not.Null, "ComponentTable should exist");

        {
            var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
            var exists = ct.PrimaryKeyIndex.TryGet(e1, out _, ref accessor);
            Assert.That(exists, Is.False,
                "Primary key index should not contain entry for rolled back creation");
            
            accessor.Dispose();
        }
    }
}