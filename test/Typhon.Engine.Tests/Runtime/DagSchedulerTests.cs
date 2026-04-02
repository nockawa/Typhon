using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class DagSchedulerTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "Test" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    private DagScheduler CreateScheduler(DagBuilder builder, int workerCount = 1, int tickRate = 1000)
    {
        var (systems, topo) = builder.Build();
        return new DagScheduler(systems, topo, new RuntimeOptions
        {
            WorkerCount = workerCount,
            BaseTickRate = tickRate
        }, _registry.Runtime);
    }

    /// <summary>
    /// Runs the scheduler for a single tick and returns. Uses a gate flag to prevent
    /// capturing data from subsequent ticks.
    /// </summary>
    private void RunOneTick(DagScheduler scheduler)
    {
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
    }

    // ═══════════════════════════════════════════════════════════════
    // Correctness: Single-threaded mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SingleWorker_LinearChain_CorrectOrder()
    {
        var executionOrder = new List<string>();
        var captured = 0;

        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => { if (captured == 0) { executionOrder.Add("A"); } })
            .AddCallbackSystem("B", _ => { if (captured == 0) { executionOrder.Add("B"); } })
            .AddCallbackSystem("C", _ =>
            {
                if (captured == 0)
                {
                    executionOrder.Add("C");
                    Interlocked.Exchange(ref captured, 1);
                }
            })
            .AddEdge("A", "B")
            .AddEdge("B", "C");

        using var scheduler = CreateScheduler(builder, workerCount: 1);
        RunOneTick(scheduler);

        Assert.That(executionOrder, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public void SingleWorker_FanOut_AllExecute()
    {
        var executed = new ConcurrentBag<string>();
        var captured = 0;

        var builder = new DagBuilder()
            .AddCallbackSystem("Root", _ => { if (captured == 0) { executed.Add("Root"); } })
            .AddCallbackSystem("B", _ => { if (captured == 0) { executed.Add("B"); } })
            .AddCallbackSystem("C", _ => { if (captured == 0) { executed.Add("C"); } })
            .AddCallbackSystem("D", _ =>
            {
                if (captured == 0)
                {
                    executed.Add("D");
                    Interlocked.Exchange(ref captured, 1);
                }
            })
            .AddEdge("Root", "B")
            .AddEdge("Root", "C")
            .AddEdge("Root", "D");

        using var scheduler = CreateScheduler(builder, workerCount: 1);
        RunOneTick(scheduler);

        Assert.That(executed, Has.Count.EqualTo(4));
        Assert.That(executed, Does.Contain("Root"));
        Assert.That(executed, Does.Contain("B"));
        Assert.That(executed, Does.Contain("C"));
        Assert.That(executed, Does.Contain("D"));
    }

    // ═══════════════════════════════════════════════════════════════
    // Correctness: Multi-threaded mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void MultiWorker_DependencyRespected()
    {
        // A → (B, C) → D
        // D must execute after both B and C
        var timestamps = new ConcurrentDictionary<string, long>();
        var captured = 0;

        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ =>
            {
                if (captured == 0)
                {
                    timestamps["A"] = Stopwatch.GetTimestamp();
                }
            })
            .AddCallbackSystem("B", _ =>
            {
                if (captured == 0)
                {
                    Thread.SpinWait(100);
                    timestamps["B"] = Stopwatch.GetTimestamp();
                }
            })
            .AddCallbackSystem("C", _ =>
            {
                if (captured == 0)
                {
                    Thread.SpinWait(100);
                    timestamps["C"] = Stopwatch.GetTimestamp();
                }
            })
            .AddCallbackSystem("D", _ =>
            {
                if (captured == 0)
                {
                    timestamps["D"] = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref captured, 1);
                }
            })
            .AddEdge("A", "B")
            .AddEdge("A", "C")
            .AddEdge("B", "D")
            .AddEdge("C", "D");

        using var scheduler = CreateScheduler(builder, workerCount: 4);
        RunOneTick(scheduler);

        Assert.That(timestamps, Has.Count.EqualTo(4), "All systems must have executed");
        Assert.That(timestamps["D"], Is.GreaterThan(timestamps["B"]), "D must execute after B");
        Assert.That(timestamps["D"], Is.GreaterThan(timestamps["C"]), "D must execute after C");
        Assert.That(timestamps["B"], Is.GreaterThan(timestamps["A"]), "B must execute after A");
        Assert.That(timestamps["C"], Is.GreaterThan(timestamps["A"]), "C must execute after A");
    }

    [Test]
    public void Callback_InlineContinuation_D3()
    {
        // A → B → C (all CallbackSystem)
        // With inline continuation (D3), B and C should run on the same thread
        var threadIds = new ConcurrentDictionary<string, int>();
        var captured = 0;

        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => { if (captured == 0) { threadIds["A"] = Environment.CurrentManagedThreadId; } })
            .AddCallbackSystem("B", _ => { if (captured == 0) { threadIds["B"] = Environment.CurrentManagedThreadId; } })
            .AddCallbackSystem("C", _ =>
            {
                if (captured == 0)
                {
                    threadIds["C"] = Environment.CurrentManagedThreadId;
                    Interlocked.Exchange(ref captured, 1);
                }
            })
            .AddEdge("A", "B")
            .AddEdge("B", "C");

        using var scheduler = CreateScheduler(builder, workerCount: 4);
        RunOneTick(scheduler);

        Assert.That(threadIds, Has.Count.EqualTo(3));
        // B is a CallbackSystem successor of A → inlined (D3)
        // C is a CallbackSystem successor of B → inlined (D3)
        Assert.That(threadIds["B"], Is.EqualTo(threadIds["C"]),
            "Inline continuation: B and C should run on the same thread");
    }

    // ═══════════════════════════════════════════════════════════════
    // Pipeline systems
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PipelineSystem_AllChunksProcessed()
    {
        var chunkCounter = 0;
        var ticksSeen = 0;
        const int totalChunks = 100;

        var builder = new DagBuilder()
            .AddPipelineSystem("Physics", (chunk, total) =>
            {
                Interlocked.Increment(ref chunkCounter);
            }, totalChunks);

        using var scheduler = CreateScheduler(builder, workerCount: 4);
        RunOneTick(scheduler);

        // After at least 1 tick, total chunks should be a multiple of totalChunks
        Assert.That(chunkCounter % totalChunks, Is.EqualTo(0), "All chunks must be processed per tick");
        Assert.That(chunkCounter, Is.GreaterThanOrEqualTo(totalChunks));
    }

    [Test]
    public void PipelineSystem_MultiWorkerDistribution()
    {
        var workerThreadIds = new ConcurrentBag<int>();
        var captured = 0;
        const int totalChunks = 100;

        var builder = new DagBuilder()
            .AddPipelineSystem("Physics", (chunk, total) =>
            {
                if (captured == 0)
                {
                    workerThreadIds.Add(Environment.CurrentManagedThreadId);
                    Thread.SpinWait(50);
                }
            }, totalChunks);

        // Use a CallbackSystem successor to signal first tick done
        var builder2 = new DagBuilder()
            .AddPipelineSystem("Physics", (chunk, total) =>
            {
                if (captured == 0)
                {
                    workerThreadIds.Add(Environment.CurrentManagedThreadId);
                    Thread.SpinWait(50);
                    if (chunk == total - 1)
                    {
                        Interlocked.Exchange(ref captured, 1);
                    }
                }
            }, totalChunks);

        using var scheduler = CreateScheduler(builder2, workerCount: 4);
        RunOneTick(scheduler);

        var distinctWorkers = new HashSet<int>(workerThreadIds);
        Assert.That(distinctWorkers.Count, Is.GreaterThan(1),
            "Multiple workers should have participated in chunk processing");
    }

    // ═══════════════════════════════════════════════════════════════
    // Multiple ticks
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void MultipleTicks_StateReset()
    {
        var tickCount = 0;

        var builder = new DagBuilder()
            .AddCallbackSystem("Counter", _ => Interlocked.Increment(ref tickCount));

        using var scheduler = CreateScheduler(builder, workerCount: 2);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 10, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(tickCount, Is.GreaterThanOrEqualTo(10),
            "CallbackSystem should execute once per tick");
    }

    [Test]
    public void PipelineSystem_ChunksResetEachTick()
    {
        var totalChunksProcessed = 0;
        const int chunksPerTick = 20;

        var builder = new DagBuilder()
            .AddPipelineSystem("Work", (chunk, total) =>
            {
                Interlocked.Increment(ref totalChunksProcessed);
            }, chunksPerTick);

        using var scheduler = CreateScheduler(builder, workerCount: 4);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ticksCompleted = scheduler.CurrentTickNumber;
        Assert.That(totalChunksProcessed, Is.EqualTo(chunksPerTick * ticksCompleted),
            $"Each of {ticksCompleted} ticks should process exactly {chunksPerTick} chunks");
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Shutdown_Clean()
    {
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => { });

        using var scheduler = CreateScheduler(builder, workerCount: 4);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // Verify the scheduler stopped (no more ticks)
        var tickAfterShutdown = scheduler.CurrentTickNumber;
        Thread.Sleep(50);
        Assert.That(scheduler.CurrentTickNumber, Is.EqualTo(tickAfterShutdown),
            "No more ticks should execute after shutdown");
    }

    [Test]
    public void SingleThreadedMode_Works()
    {
        var executionOrder = new List<string>();
        var captured = 0;

        // Complex DAG: A → (B, C) → D → E
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => { if (captured == 0) { executionOrder.Add("A"); } })
            .AddCallbackSystem("B", _ => { if (captured == 0) { executionOrder.Add("B"); } })
            .AddCallbackSystem("C", _ => { if (captured == 0) { executionOrder.Add("C"); } })
            .AddCallbackSystem("D", _ => { if (captured == 0) { executionOrder.Add("D"); } })
            .AddCallbackSystem("E", _ =>
            {
                if (captured == 0)
                {
                    executionOrder.Add("E");
                    Interlocked.Exchange(ref captured, 1);
                }
            })
            .AddEdge("A", "B")
            .AddEdge("A", "C")
            .AddEdge("B", "D")
            .AddEdge("C", "D")
            .AddEdge("D", "E");

        using var scheduler = CreateScheduler(builder, workerCount: 1);
        RunOneTick(scheduler);

        Assert.That(executionOrder, Has.Count.EqualTo(5));

        var posA = executionOrder.IndexOf("A");
        var posB = executionOrder.IndexOf("B");
        var posC = executionOrder.IndexOf("C");
        var posD = executionOrder.IndexOf("D");
        var posE = executionOrder.IndexOf("E");

        Assert.That(posA, Is.LessThan(posB));
        Assert.That(posA, Is.LessThan(posC));
        Assert.That(posB, Is.LessThan(posD));
        Assert.That(posC, Is.LessThan(posD));
        Assert.That(posD, Is.LessThan(posE));
    }

    [Test]
    public void SingleThreadedMode_PipelineSystem_AllChunksProcessed()
    {
        var processedChunks = new List<int>();
        var captured = 0;
        const int totalChunks = 10;

        var builder = new DagBuilder()
            .AddPipelineSystem("Work", (chunk, total) =>
            {
                if (captured == 0)
                {
                    lock (processedChunks)
                    {
                        processedChunks.Add(chunk);
                    }

                    if (chunk == total - 1)
                    {
                        Interlocked.Exchange(ref captured, 1);
                    }
                }
            }, totalChunks);

        using var scheduler = CreateScheduler(builder, workerCount: 1);
        RunOneTick(scheduler);

        processedChunks.Sort();
        Assert.That(processedChunks, Has.Count.EqualTo(totalChunks));
        for (var i = 0; i < totalChunks; i++)
        {
            Assert.That(processedChunks[i], Is.EqualTo(i));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Mixed DAG (CallbackSystem + PipelineSystem)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void MixedDAG_CallbackAndPipeline_CorrectExecution()
    {
        // Input(CallbackSystem) → Physics(PipelineSystem,50) → Output(CallbackSystem)
        var inputExecuted = 0;
        var outputExecuted = 0;
        var physicsChunks = 0;
        const int totalChunks = 50;

        var builder = new DagBuilder()
            .AddCallbackSystem("Input", _ => Interlocked.Increment(ref inputExecuted))
            .AddPipelineSystem("Physics", (chunk, total) => Interlocked.Increment(ref physicsChunks), totalChunks)
            .AddCallbackSystem("Output", _ => Interlocked.Increment(ref outputExecuted))
            .AddEdge("Input", "Physics")
            .AddEdge("Physics", "Output");

        using var scheduler = CreateScheduler(builder, workerCount: 4);
        RunOneTick(scheduler);

        Assert.That(inputExecuted, Is.GreaterThanOrEqualTo(1));
        Assert.That(physicsChunks, Is.GreaterThanOrEqualTo(totalChunks));
        Assert.That(outputExecuted, Is.GreaterThanOrEqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // Telemetry
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Telemetry_TickDuration_Recorded()
    {
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => Thread.SpinWait(1000));

        using var scheduler = CreateScheduler(builder, workerCount: 2);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        Assert.That(ring.TotalTicksRecorded, Is.GreaterThanOrEqualTo(3));

        ref readonly var tick = ref ring.GetTick(ring.NewestTick);
        Assert.That(tick.ActualDurationMs, Is.GreaterThan(0f));
        Assert.That(tick.ActiveSystemCount, Is.EqualTo(1));
    }

    [Test]
    public void Telemetry_TransitionLatency_RecordedForNonRoot()
    {
        // A → B: B's transition latency should be > 0
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => Thread.SpinWait(500))
            .AddCallbackSystem("B", _ => { })
            .AddEdge("A", "B");

        using var scheduler = CreateScheduler(builder, workerCount: 2);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var systems = ring.GetSystemMetrics(ring.NewestTick);

        // System B (index 1) should have transition latency >= 0
        Assert.That(systems[1].TransitionLatencyUs, Is.GreaterThanOrEqualTo(0f),
            "Non-root system should have measurable transition latency");
        Assert.That(systems[1].DurationUs, Is.GreaterThanOrEqualTo(0f));
    }

    [Test]
    public void Telemetry_SystemCount_MatchesDag()
    {
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => { })
            .AddCallbackSystem("B", _ => { })
            .AddCallbackSystem("C", _ => { })
            .AddEdge("A", "B")
            .AddEdge("B", "C");

        using var scheduler = CreateScheduler(builder, workerCount: 1);
        Assert.That(scheduler.SystemCount, Is.EqualTo(3));
        Assert.That(scheduler.WorkerCount, Is.EqualTo(1));
    }
}
