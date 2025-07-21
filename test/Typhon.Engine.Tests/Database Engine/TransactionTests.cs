using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Database_Engine;

[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct CompA
{
    public const string SchemaName = "Typhon.Schema.UnitTest.CompA";
    public long A;

    public CompA(long a)
    {
        A = a;
    }
}

[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct CompB
{
    public const string SchemaName = "Typhon.Schema.UnitTest.CompB";
    public int A;
    public float B;

    public CompB(int a, float b)
    {
        A = a;
        B = b;
    }
}

[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct CompC
{
    public const string SchemaName = "Typhon.Schema.UnitTest.CompC";
    public String64 String;

    public CompC(string str)
    {
        String.AsString = str;
    }
}

[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct CompD
{
    public const string SchemaName = "Typhon.Schema.UnitTest.CompD";

    [Index(AllowMultiple = true)]
    public float A;
    [Index]
    public int B;
    [Index(AllowMultiple = true)]
    public double C;

    public CompD(float a, int b, double c)
    {
        A = a;
        B = b;
        C = c;
    }
}

class TransactionTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize")! : (int)PMMF.MinimumCacheSize;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            /*
            .MinimumLevel.Override(typeof(LogicalSegmentManager).FullName, LogEventLevel.Verbose)
            */
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithCurrentFrame()
            .WriteTo.Seq("http://localhost:5341", compact: true)
            .CreateLogger();

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(options =>
            {
            });
        
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        Log.CloseAndFlush();
    }

    private static void RegisterComponents(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromRowAccessor<CompA>();
        dbe.RegisterComponentFromRowAccessor<CompB>();
        dbe.RegisterComponentFromRowAccessor<CompC>();
        dbe.RegisterComponentFromRowAccessor<CompD>();
    }
    
    [Test]
    public void CreateAndReadInsideSameTransaction()
    {
        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            var e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

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
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);

            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
                
            t.Commit();
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);
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
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);
            var a = new CompA();
            var b = new CompB();
            var c = new CompC();
            t.ReadEntity(e1, out a, out b, out c);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);

            t.UpdateEntity(e1, ref ca, ref cb, ref cc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);
            var a = new CompA();
            var b = new CompB();
            var c = new CompC();
            t.ReadEntity(e1, out a, out b, out c);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);
            var a = new CompA();
            var b = new CompB();
            var c = new CompC();
            var res = t.ReadEntity(e1, out a, out b, out c);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);
            t.DeleteEntity<CompA, CompB, CompC>(e1);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);
            var a = new CompA();
            var b = new CompB();
            var c = new CompC();
            var res = t.ReadEntity(e1, out a, out b, out c);

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
            
            using var t = dbe.NewTransaction(true);

            var a = new CompA(1);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

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
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);

            var res = t.ReadEntity(e1, out CompA a, out CompB b, out CompC c);
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
            
            using var t = dbe.NewTransaction(true);

            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1));

            t.Commit();
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);
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
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);
            var ra = new CompA();
            var rb = new CompB();
            var rc = new CompC();
            var res = t.ReadEntity(e1, out ra, out rb, out rc);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);

            t.UpdateEntity(e1, ref ba, ref bb, ref bc);
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2));

            var res = t.Rollback();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(3));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);
            var ra = new CompA();
            var rb = new CompB();
            var rc = new CompC();
            t.ReadEntity(e1, out ra, out rb, out rc);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);
            var ra = new CompA();
            var rb = new CompB();
            var rc = new CompC();
            var res = t.ReadEntity(e1, out ra, out rb, out rc);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);
            var ra = new CompA();
            var rb = new CompB();
            var rc = new CompC();
            t.ReadEntity(e1, out ra, out rb, out rc);

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
            
            using var t = dbe.NewTransaction(true);

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
            
            using var t = dbe.NewTransaction(true);

            t.UpdateEntity(e1, ref ca);
            Assert.That(t.GetComponentRevision<CompD>(e1), Is.EqualTo(2));

            var res = t.Commit();
            Assert.That(res, Is.True);
            Assert.That(t.CommittedOperationCount, Is.EqualTo(1));
        }

        {
            using var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            
            using var t = dbe.NewTransaction(true);
            var a = new CompD();
            t.ReadEntity(e1, out a);

            Assert.That(t.GetComponentRevision<CompD>(e1), Is.EqualTo(2));
            Assert.That(a, Is.EqualTo(ca));
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


    public class ThreadWorkers
    {
        public class Context
        {
            public int Stage;
            public int ThreadId;
            public object UserContext;
        }

        private object _locker;
        private Dictionary<int, Action<Context>[]> _stages;
        private readonly int _stageCount;

        public ThreadWorkers(int stageCount)
        {
            _locker = new object();
            _stages = new Dictionary<int, Action<Context>[]>();
            _stageCount = stageCount;
        }

        public void AddStage(int stageNumber, int threadIdStart, Action<Context> action, int threadCount=1)
        {
            for (int i = 0; i < threadCount; i++)
            {
                if (_stages.TryGetValue(threadIdStart + i, out var stages) == false)
                {
                    stages = new Action<Context>[_stageCount];
                    _stages.Add(threadIdStart + i, stages);
                }
                stages[stageNumber] = action;
            }
        }

        public void Run()
        {
            var contexts = new Dictionary<int, Context>();
            var tasks = new List<Task>();
            for (int i = 0; i < _stageCount; i++)
            {
                var stage = i;
                Console.WriteLine($"[{DateTime.UtcNow}] Run Stage {i}");
                tasks.Clear();

                foreach (var kvp in _stages)
                {
                    var threadId = kvp.Key;

                    if (!contexts.TryGetValue(kvp.Key, out var c))
                    {
                        c = new Context();
                        c.UserContext = null;
                        contexts.Add(kvp.Key, c);
                    }

                    c.ThreadId = threadId;
                    c.Stage = stage;

                    var actions = kvp.Value;
                    if (actions[i] != null)
                    {
                        tasks.Add(Task.Run(() => actions[stage](c)));
                    }
                }

                Task.WaitAll(tasks.ToArray());
            }
        }
    }

    [Test]
    public void MultiThreadTest()
    {
        var t = new ThreadWorkers(2);
        t.AddStage(0, 0, (c) =>
        {
            Thread.Sleep(100);
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
        });
        t.AddStage(0, 1, (c) =>
        {
            Thread.Sleep(200);
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
            Thread.Sleep(100);
        });

        t.AddStage(1, 0, (c) =>
        {
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
        });
        t.AddStage(1, 1, (c) =>
        {
            Console.WriteLine($"Thread {c.ThreadId}, stage {c.Stage}");
        });

        t.Run();

    }
}