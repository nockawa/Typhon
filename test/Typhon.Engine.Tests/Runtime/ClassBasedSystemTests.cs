using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[NonParallelizable]
[TestFixture]
class ClassBasedSystemTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ClassBasedTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    // ── Test system implementations ──

    class CountingCallback : CallbackSystem
    {
        public int ExecuteCount;

        protected override void Configure(SystemBuilder b)
        {
            b.Name("Counter");
        }

        protected override void Execute(TickContext ctx) => Interlocked.Increment(ref ExecuteCount);
    }

    class DependentCallback : CallbackSystem
    {
        private readonly string _name;
        private readonly string _after;
        public int ExecuteCount;

        public DependentCallback(string name, string after)
        {
            _name = name;
            _after = after;
        }

        protected override void Configure(SystemBuilder b)
        {
            b.Name(_name);
            if (_after != null)
            {
                b.After(_after);
            }
        }

        protected override void Execute(TickContext ctx) => Interlocked.Increment(ref ExecuteCount);
    }

    class CountingQuery : QuerySystem
    {
        public int ExecuteCount;

        protected override void Configure(SystemBuilder b)
        {
            b.Name("Query");
            b.Priority(SystemPriority.High);
        }

        protected override void Execute(TickContext ctx) => Interlocked.Increment(ref ExecuteCount);
    }

    class TestCompound : CompoundSystem
    {
        public readonly CountingCallback Cb1 = new();
        public readonly DependentCallback Cb2;

        public TestCompound()
        {
            Cb2 = new DependentCallback("Second", "Counter");
        }

        protected override void Configure()
        {
            Add(Cb1);
            Add(Cb2);
        }
    }

    class NoNameSystem : CallbackSystem
    {
        protected override void Configure(SystemBuilder b) { } // forgot b.Name()
        protected override void Execute(TickContext ctx) { }
    }

    class StubPipeline : PipelineSystem
    {
        protected override void Configure(SystemBuilder b)
        {
            b.Name("Stub");
        }
    }

    [Test]
    public void Add_CallbackSystem_ExecutesEveryTick()
    {
        var system = new CountingCallback();
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(system);
        using var scheduler = schedule.Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(system.ExecuteCount, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Add_QuerySystem_ExecutesEveryTick()
    {
        var system = new CountingQuery();
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(system);
        using var scheduler = schedule.Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(system.ExecuteCount, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Add_CompoundSystem_ExpandsSubSystems()
    {
        var compound = new TestCompound();
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(compound);
        using var scheduler = schedule.Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(compound.Cb1.ExecuteCount, Is.GreaterThanOrEqualTo(3));
        Assert.That(compound.Cb2.ExecuteCount, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Add_MixedLambdaAndClass_BothExecute()
    {
        var classSystem = new CountingCallback();
        var lambdaCount = 0;

        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(classSystem);
        schedule.CallbackSystem("Lambda", _ => Interlocked.Increment(ref lambdaCount), after: "Counter");
        using var scheduler = schedule.Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(classSystem.ExecuteCount, Is.GreaterThanOrEqualTo(3));
        Assert.That(lambdaCount, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Add_SystemWithoutName_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        Assert.Throws<InvalidOperationException>(() => schedule.Add(new NoNameSystem()));
    }

    [Test]
    public void Add_PipelineSystem_ThrowsNotSupported()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        Assert.Throws<NotSupportedException>(() => schedule.Add(new StubPipeline()));
    }

    [Test]
    public void Add_NullCallbackSystem_ThrowsArgumentNull()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        Assert.Throws<ArgumentNullException>(() => schedule.Add((CallbackSystem)null));
    }

    [Test]
    public void Add_NullQuerySystem_ThrowsArgumentNull()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        Assert.Throws<ArgumentNullException>(() => schedule.Add((QuerySystem)null));
    }

    [Test]
    public void Add_NullCompoundSystem_ThrowsArgumentNull()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        Assert.Throws<ArgumentNullException>(() => schedule.Add((CompoundSystem)null));
    }

    [Test]
    public void Add_NullPipelineSystem_ThrowsArgumentNull()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        Assert.Throws<ArgumentNullException>(() => schedule.Add((PipelineSystem)null));
    }

    [Test]
    public void Add_AfterBuild_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.CallbackSystem("Noop", _ => { });
        schedule.Build(_registry.Runtime).Dispose();

        Assert.Throws<InvalidOperationException>(() => schedule.Add(new CountingCallback()));
    }
}
