using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests.Database_Engine
{
    [Component(SchemaName)]
    [StructLayout(LayoutKind.Sequential)]
    public struct CompA
    {
        [Field(IsPrimaryKey = true)]
        public int Id;

        public const string SchemaName = "Typhon.Schema.UnitTest.CompA";
        public int A;

        public CompA(int a)
        {
            Id = default;
            A = a;
        }
    }

    [Component(SchemaName)]
    [StructLayout(LayoutKind.Sequential)]
    public struct CompB
    {
        [Field(IsPrimaryKey = true)]
        public int Id;

        public const string SchemaName = "Typhon.Schema.UnitTest.CompB";
        public int A;
        public float B;

        public CompB(int a, float b)
        {
            Id = default;
            A = a;
            B = b;
        }
    }

    [Component(SchemaName)]
    [StructLayout(LayoutKind.Sequential)]
    public struct CompC
    {
        [Field(IsPrimaryKey = true)]
        public int Id;

        public const string SchemaName = "Typhon.Schema.UnitTest.CompC";
        public String64 String;

        public CompC(string str)
        {
            Id = default;
            String.AsString = str;
        }
    }

    class TransactionTests
    {
        private IServiceProvider _serviceProvider;
        private ServiceCollection _serviceCollection;
        private DatabaseEngine _dbe;

        private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

        [SetUp]
        public void Setup()
        {
            var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
            var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize") : (int)VirtualDiskManager.MinimumCacheSize;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .Enrich.WithCurrentFrame()
                .WriteTo.Seq("http://localhost:5341", compact: true)
                .CreateLogger();

            var serviceCollection = new ServiceCollection();
            _serviceCollection = serviceCollection;
            _serviceCollection
                .AddTyphon(builder =>
                {
                    builder.ConfigureDatabase(dc =>
                    {
                        dc.DatabaseName = CurrentDatabaseName;
                        dc.RecreateDatabase = true;
                        dc.DeleteDatabaseOnDispose = true;
                        dc.DatabaseCacheSize = (ulong)dcs;
                    });
                })

                .AddLogging(builder =>
                {
                    builder.AddSerilog(dispose: true);
                    builder.AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.IncludeScopes = true;
                        options.TimestampFormat = "mm:ss.fff ";
                    });
                });

            _serviceProvider = _serviceCollection.BuildServiceProvider();

            _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            _dbe.Initialize();

            _dbe.RegisterComponentFromRowAccessor<CompA>();
            _dbe.RegisterComponentFromRowAccessor<CompB>();
            _dbe.RegisterComponentFromRowAccessor<CompC>();
        }

        [TearDown]
        public void TearDown()
        {
            _dbe?.Dispose();
            _dbe = null;

            Log.CloseAndFlush();
        }

        [Test]
        public void CreateAndReadInsideSameTransaction()
        {
            {
                using var t = _dbe.NewTransaction(true);

                var a = new CompA(1);
                var b = new CompB(1, 1.2f);
                var c = new CompC("Porcupine Tree");
                var e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                var ca = new CompA();
                var cb = new CompB();
                var cc = new CompC();
                t.ReadEntity(e1, out ca, out cb, out cc);

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
                using var t = _dbe.NewTransaction(true);

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);
                
                t.Commit();
            }

            {
                using var t = _dbe.NewTransaction(true);
                var ca = new CompA();
                var cb = new CompB();
                var cc = new CompC();
                t.ReadEntity(e1, out ca, out cb, out cc);

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
                using var t = _dbe.NewTransaction(true);

                var a = new CompA(1);
                var b = new CompB(1, 1.2f);
                var c = new CompC("Porcupine Tree");
                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                t.UpdateEntity(e1, ref ca, ref cb, ref cc);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var a = new CompA();
                var b = new CompB();
                var c = new CompC();
                t.ReadEntity(e1, out a, out b, out c);

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
                using var t = _dbe.NewTransaction(true);

                var a = new CompA(1);
                var b = new CompB(1, 1.2f);
                var c = new CompC("Porcupine Tree");
                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);

                t.UpdateEntity(e1, ref ca, ref cb, ref cc);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var a = new CompA();
                var b = new CompB();
                var c = new CompC();
                t.ReadEntity(e1, out a, out b, out c);

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
                using var t = _dbe.NewTransaction(true);

                var a = new CompA(1);
                var b = new CompB(1, 1.2f);
                var c = new CompC("Porcupine Tree");
                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                t.DeleteEntity<CompA, CompB, CompC>(e1);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var a = new CompA();
                var b = new CompB();
                var c = new CompC();
                var res = t.ReadEntity(e1, out a, out b, out c);

                Assert.That(res, Is.False);
            }
        }

        [Test]
        public void CreateAndDeleteInsideDifferentTransaction()
        {
            long e1;

            {
                using var t = _dbe.NewTransaction(true);

                var a = new CompA(1);
                var b = new CompB(1, 1.2f);
                var c = new CompC("Porcupine Tree");
                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                t.DeleteEntity<CompA, CompB, CompC>(e1);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var a = new CompA();
                var b = new CompB();
                var c = new CompC();
                var res = t.ReadEntity(e1, out a, out b, out c);

                Assert.That(res, Is.False);
            }
        }

        [Test]
        public void CreateAndReadInsideSameTransactionRollbacked()
        {
            long e1;
            {
                using var t = _dbe.NewTransaction(true);

                var a = new CompA(1);
                var b = new CompB(1, 1.2f);
                var c = new CompC("Porcupine Tree");
                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                var ca = new CompA();
                var cb = new CompB();
                var cc = new CompC();
                t.ReadEntity(e1, out ca, out cb, out cc);

                Assert.That(a, Is.EqualTo(ca));
                Assert.That(b, Is.EqualTo(cb));
                Assert.That(c, Is.EqualTo(cc));

                var res = t.Rollback();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }
            {
                using var t = _dbe.NewTransaction(true);

                var res = t.ReadEntity(e1, out CompA a, out CompB b, out CompC c);
                Assert.That(res, Is.False);
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
                using var t = _dbe.NewTransaction(true);

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero);

                t.Commit();
            }

            {
                using var t = _dbe.NewTransaction(true);
                var ca = new CompA();
                var cb = new CompB();
                var cc = new CompC();
                t.ReadEntity(e1, out ca, out cb, out cc);

                Assert.That(ca, Is.EqualTo(a));
                Assert.That(cb, Is.EqualTo(b));
                Assert.That(cc, Is.EqualTo(c));

                var res = t.Rollback();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }
            {
                using var t = _dbe.NewTransaction(true);

                var res = t.ReadEntity(e1, out CompA ba, out CompB bb, out CompC bc);
                Assert.That(res, Is.True);
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
                using var t = _dbe.NewTransaction(true);

                e1 = t.CreateEntity(ref oa, ref ob, ref oc);
                Assert.That(e1, Is.Not.Zero);

                t.UpdateEntity(e1, ref ba, ref bb, ref bc);

                var res = t.Rollback();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var ra = new CompA();
                var rb = new CompB();
                var rc = new CompC();
                var res = t.ReadEntity(e1, out ra, out rb, out rc);

                Assert.That(res, Is.False);
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
                using var t = _dbe.NewTransaction(true);

                e1 = t.CreateEntity(ref oa, ref ob, ref oc);
                
                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(e1, Is.Not.Zero);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);

                t.UpdateEntity(e1, ref ba, ref bb, ref bc);

                var res = t.Rollback();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var ra = new CompA();
                var rb = new CompB();
                var rc = new CompC();
                t.ReadEntity(e1, out ra, out rb, out rc);

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
                using var t = _dbe.NewTransaction(true);

                e1 = t.CreateEntity(ref oa, ref ob, ref oc);
                Assert.That(e1, Is.Not.Zero);

                t.DeleteEntity<CompA, CompB, CompC>(e1);

                var res = t.Rollback();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var ra = new CompA();
                var rb = new CompB();
                var rc = new CompC();
                var res = t.ReadEntity(e1, out ra, out rb, out rc);

                Assert.That(res, Is.False);
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
                using var t = _dbe.NewTransaction(true);

                e1 = t.CreateEntity(ref oa, ref ob, ref oc);

                var res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(e1, Is.Not.Zero);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);

                t.DeleteEntity<CompA, CompB, CompC>(e1);

                var res = t.Rollback();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
            }

            {
                using var t = _dbe.NewTransaction(true);
                var ra = new CompA();
                var rb = new CompB();
                var rc = new CompC();
                t.ReadEntity(e1, out ra, out rb, out rc);

                Assert.That(oa, Is.EqualTo(ra));
                Assert.That(ob, Is.EqualTo(rb));
                Assert.That(oc, Is.EqualTo(rc));
            }
        }

        [Test]
        public void TransactionNodeTest()
        {
            var n1 = Transaction.Transactions.PushHead(1, 1);
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
            Assert.That(Transaction.Transactions.GetMinTick(), Is.EqualTo(0));
        }
    }
}
