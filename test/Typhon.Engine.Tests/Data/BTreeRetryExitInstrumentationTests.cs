using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Guards the accounting of <see cref="InsertRetryExit"/> — that every no-progress return from <c>InsertIterative</c> names itself, and that the histogram
/// adds up to the restart counter it explains.
/// </summary>
/// <remarks>
/// The instrumentation exists because #738 produced nightly records saying a restart storm happened and nothing about where. That is only worth having if it
/// cannot quietly go wrong in the same way: a bail added later without a reason code would tally into <see cref="InsertRetryExit.Unknown"/> and the histogram
/// would go back to saying "somewhere". Both assertions below are cheap and total, so the failure mode is a red test rather than a diagnostic that has lost
/// its resolution without telling anyone.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeRetryExitInstrumentationTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var name = $"btrei_{TestContext.CurrentContext.Test.Name}";
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>
    /// Every pessimistic retry is attributed to exactly one named exit, on a single-threaded workload that drives the split path hard.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public unsafe void InsertRetryExits_SingleThreadedSplits_AccountForEveryPessimisticRestart()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Descending keys, so every insert prepends and the leaf-full path — the one that owns the back half of the exit table — runs on nearly all
            // of them.
            for (int i = 2000; i >= 1; i--)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            AssertExitsAccountForRestarts(tree);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// The same invariant under contention, which is the only way most of the exit codes are reachable at all.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT assert that restarts occurred. Whether an interleaving produces one is the scheduler's business and asserting it would buy a
    /// flaky test in exchange for nothing: the accounting invariant is what this fixture is for, and it holds at zero restarts as informatively as at
    /// thousands. The observed count is logged instead, which is the number a reader of a nightly record wants anyway.
    /// </remarks>
    [Test]
    [CancelAfter(10_000)]
    public unsafe void InsertRetryExits_ConcurrentDisjointInserts_AccountForEveryPessimisticRestart()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        // Oversubscribed on purpose: more writers than cores is what preempts a thread inside the read/validate window and makes the bails fire.
        int threadCount = Math.Clamp(Environment.ProcessorCount * 2, 4, 16);
        const int keysPerThread = 400;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var setupAccessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            setupAccessor.Dispose();

            using var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var d = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();
                        int start = tid * keysPerThread + 1;
                        for (int i = start; i < start + keysPerThread; i++)
                        {
                            tree.Add(i, i * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(d);
                    }
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);

            Assert.That(tree.EntryCount, Is.EqualTo(threadCount * keysPerThread), "every disjoint key should be present");
            AssertExitsAccountForRestarts(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ============================================================================================================================================
    // The nightly's renderer. Driven here with synthetic arrays because the branch that calls it fires only on a real stall — which a 32-core dev box does
    // not produce, and CI produces a handful of times a month. Shipping it unexercised means discovering a formatting bug inside the one record that was
    // supposed to explain the stall.
    // ============================================================================================================================================

    /// <summary>Deltas win over totals, and the largest bucket is named first.</summary>
    [Test]
    public void DescribeExitDeltas_WhenBucketsMoved_RanksThemByDelta()
    {
        var first = NewCounters();
        var deltas = NewCounters();
        deltas[OlcBTreeRaceStressTests.CtrExitBase + InsertRetryExit.PathLockFailed] = 12;
        deltas[OlcBTreeRaceStressTests.CtrExitBase + InsertRetryExit.DescentNodeLocked] = 870;

        var text = OlcBTreeRaceStressTests.DescribeExitDeltas(first, deltas);

        Assert.That(text, Does.Contain("retry exits (+delta)"));
        Assert.That(text, Does.Contain("DescentNodeLocked=870"));
        Assert.That(text, Does.Contain("PathLockFailed=12"));
        Assert.That(
            text.IndexOf("DescentNodeLocked", StringComparison.Ordinal),
            Is.LessThan(text.IndexOf("PathLockFailed", StringComparison.Ordinal)),
            "the biggest mover is the one the reader needs first");
    }

    /// <summary>A stall in which every thread is already parked shows no movement; the totals still name where they are parked.</summary>
    [Test]
    public void DescribeExitDeltas_WhenNothingMovedButTotalsExist_FallsBackToTotals()
    {
        var first = NewCounters();
        var deltas = NewCounters();
        first[OlcBTreeRaceStressTests.CtrExitBase + InsertRetryExit.MovedRightLeafFull] = 8616;

        var text = OlcBTreeRaceStressTests.DescribeExitDeltas(first, deltas);

        Assert.That(text, Does.Contain("nothing moved in the window"));
        Assert.That(text, Does.Contain("MovedRightLeafFull=8,616"));
    }

    /// <summary>Restarts with an empty histogram are the Remove or Move path, and the record says so rather than showing a blank.</summary>
    [Test]
    public void DescribeExitDeltas_WhenHistogramIsEmpty_SaysTheRestartsCameFromElsewhere()
    {
        var text = OlcBTreeRaceStressTests.DescribeExitDeltas(NewCounters(), NewCounters());

        Assert.That(text, Does.Contain("did not come from InsertIterative"));
    }

    private static long[] NewCounters() => new long[OlcBTreeRaceStressTests.ScalarCounterCount + InsertRetryExit.Count];

    /// <summary>
    /// The two assertions that make the histogram trustworthy: nothing lands in <see cref="InsertRetryExit.Unknown"/>, and the buckets sum to
    /// <c>PessimisticRestarts</c>.
    /// </summary>
    private static void AssertExitsAccountForRestarts(IntSingleBTree<PersistentStore> tree)
    {
        long sum = 0;
        var breakdown = new StringBuilder();
        for (int i = 0; i < InsertRetryExit.Count; i++)
        {
            long count = tree.InsertRetryExitCount(i);
            sum += count;
            if (count != 0)
            {
                breakdown.Append(breakdown.Length == 0 ? "" : " ").Append(InsertRetryExit.Names[i]).Append('=').Append(count);
            }
        }

        TestContext.Out.WriteLine(
            $"OptRestarts={tree.OptimisticRestarts} PessRestarts={tree.PessimisticRestarts} Fallbacks={tree.PessimisticFallbacks} " +
            $"Splits={tree.SplitCount} exits=[{(breakdown.Length == 0 ? "none" : breakdown.ToString())}]");

        Assert.That(
            tree.InsertRetryExitCount(InsertRetryExit.Unknown),
            Is.Zero,
            "a no-progress return from InsertIterative did not set retryExit — add its InsertRetryExit code, or the histogram is back to saying 'somewhere'");

        Assert.That(
            sum,
            Is.EqualTo(tree.PessimisticRestarts),
            "the exit histogram must account for every pessimistic restart; a mismatch means a retry was tallied without a reason or vice versa");
    }
}
