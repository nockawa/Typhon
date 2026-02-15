using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Text;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct CompA
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompA";
    public int A;
    public float B;
    public double C;

    public static CompA Create(Random rand) => new() { A = rand.Next(), B = (float)rand.NextDouble(), C = rand.NextDouble() };

    public CompA(int a, float b=1.234f, double c=5.678)
    {
        A = a;
        B = b;
        C = c;
    }
    
    public void Update(Random rand)
    {
        A = rand.Next();
        B = (float)rand.NextDouble();
        C = rand.NextDouble();
    }

    public override string ToString() => $"A={A}, B={B}, C={C}";
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompB
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompB";
    public int A;
    public float B;

    public CompB(int a, float b)
    {
        A = a;
        B = b;
    }
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompC
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompC";
    public String64 String;

    public CompC(string str)
    {
        String.AsString = str;
    }
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompD
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompD";

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

[Component(SchemaName, 1, true)]
[StructLayout(LayoutKind.Sequential)]
public struct CompE
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompE";

    public float A;
    public int B;
    public double C;

    public CompE(float a, int b, double c)
    {
        A = a;
        B = b;
        C = c;
    }
}

[PublicAPI]
public abstract class TestBase{
    protected readonly Random Rand;
    private static readonly char[] CharToRemove = ['(', ')', ','];
    private static readonly (string, string)[] WordsToReplace = [("true", "t"), ("false", "f")];
    public virtual bool UseSeq => false;
    public virtual Action<LoggerConfiguration> ExtraLoggerConf => null;
    protected static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;

            foreach (var c in CharToRemove)
            {
                testName = testName.Replace(c, '_');
            }
            foreach ((string oldWord, string newWord) in WordsToReplace)
            {
                testName = testName.Replace(oldWord, newWord);
            }
            var databaseName = $"T_{testName}_db";
            if (Encoding.UTF8.GetByteCount(databaseName) > PagedMMFOptions.DatabaseNameMaxUtf8Size)
            {
                databaseName = $"T_{testName.Substring(testName.Length - (PagedMMFOptions.DatabaseNameMaxUtf8Size - 5))}_db";
            }
            return databaseName;
        }
    }

    protected TestBase()
    {
        Rand = new Random(123456789);
    }

    protected static System.Collections.IEnumerable BuildNoiseCasesL1(int maxNoiseMode = 2)
    {
        for (int noiseMode = 0; noiseMode <= maxNoiseMode; noiseMode++)
        {
            foreach (bool l1 in (bool[])[true, false])
            {
                yield return new object[] { noiseMode, l1};
            }
        }
    }
    
    protected static System.Collections.IEnumerable BuildNoiseCasesL2(int maxNoiseMode = 2)
    {
        for (int noiseMode = 0; noiseMode <= maxNoiseMode; noiseMode++)
        {
            foreach (bool l1 in (bool[])[true, false])
            {
                foreach (bool l2 in (bool[])[true, false])
                {
                    yield return new object[] { noiseMode, l1, l2 };
                }
            }
        }
    }    
    protected virtual void RegisterComponents(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompB>();
        dbe.RegisterComponentFromAccessor<CompC>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.RegisterComponentFromAccessor<CompE>();
    }
    
    protected long[] CreateNoiseCompA(DatabaseEngine dbe, Transaction t = null, int count = 10)
    {
        RegisterComponents(dbe);
        
        var cur = t ?? dbe.CreateQuickTransaction();

        var res = new long[count];
        for (int i = 0; i < count; i++)
        {
            var a = CompA.Create(Rand);
            res[i] = cur.CreateEntity(ref a);
        }

        if (t == null)
        {
            cur.Commit();
            cur.Dispose();
        }
        
        return res;
    }
    
    protected void UpdateNoiseCompA(DatabaseEngine dbe, Transaction t, long[] pks)
    {
        RegisterComponents(dbe);

        var cur = t ?? dbe.CreateQuickTransaction();
        
        for (var i = 0; i < pks.Length; i++)
        {
            long pk = pks[i];

            CompA a = default;
            if ((i & 1) != 0)
            {
                cur.ReadEntity(pk, out a);
            }
            
            a.Update(Rand);
            cur.UpdateEntity(pk, ref a);
        }
        
        if (t == null)
        {
            cur.Commit();
            cur.Dispose();
        }
    }
    
    protected void ReadNoiseCompA(DatabaseEngine dbe, Transaction t, long[] pks)
    {
        RegisterComponents(dbe);

        var cur = t ?? dbe.CreateQuickTransaction();
        
        for (var i = 0; i < pks.Length; i++)
        {
            long pk = pks[i];

            cur.ReadEntity(pk, out CompA a);
        }
        
        if (t == null)
        {
            cur.Dispose();
        }
    }
}


[PublicAPI]
abstract class TestBase<T> : TestBase
{
    protected IServiceProvider ServiceProvider;
    protected ServiceCollection ServiceCollection;
    protected ILogger<T> Logger;

    // Convenience accessors for DI-provided resources
    protected IResourceRegistry ResourceRegistry => ServiceProvider.GetRequiredService<IResourceRegistry>();
    protected IMemoryAllocator MemoryAllocator => ServiceProvider.GetRequiredService<IMemoryAllocator>();
    protected IResource AllocationResource => ResourceRegistry.Allocation;

    [SetUp]
    public virtual void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize")! : (int)PagedMMF.MinimumCacheSize;

        var serviceCollection = new ServiceCollection();
        ServiceCollection = serviceCollection;
        ServiceCollection
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(_ =>
            {
            });
        
        ServiceProvider = ServiceCollection.BuildServiceProvider();
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        Logger = ServiceCollection.BuildServiceProvider().GetRequiredService<ILogger<T>>();
    }

    [TearDown]
    public virtual void TearDown() => Log.CloseAndFlush();
}