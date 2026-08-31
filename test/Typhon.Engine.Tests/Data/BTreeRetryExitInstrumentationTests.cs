using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
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
    /// An uncontended workload that splits heavily retries ZERO times — no restart is self-inflicted.
    /// </summary>
    /// <remarks>
    /// This replaces an assertion that could not fail. It used to check the accounting invariant (histogram sum == PessimisticRestarts) on a single-threaded
    /// run, where both sides are 0 and no reachable state makes 0 != 0 — the exact "test that cannot fail" this fixture exists to guard against, reproduced
    /// inside the guard. The honest property here is stronger and CAN fail: with one thread there is no contention, so every bail in InsertIterative is
    /// either unreachable or a defect, and any future change that makes an uncontended split restart shows up as a non-zero count naming its own cause.
    /// </remarks>
    [Test]
    [CancelAfter(5000)]
    public unsafe void InsertRetryExits_SingleThreadedSplits_NeverRetry()
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
            Assert.That(
                tree.PessimisticRestarts,
                Is.Zero,
                $"an uncontended insert workload must never retry; exits=[{tree.DescribeInsertRetryExits()}]");
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
    [VerifiesRule("IXW-05")]
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
                        // Timeout, not the bare overload: a worker that throws before signalling would otherwise park every other participant forever,
                        // Task.WaitAll would never return, and [CancelAfter] cannot abort a test that takes no CancellationToken — the fixture would hang
                        // rather than fail.
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("barrier timed out — a worker failed before reaching it");
                        }
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

            // IXW-05. CheckConsistency's ValidateLeafDepths is what catches a level-mixing root split, and ValidateDescentAndChainAgree is what catches the
            // orphaned leaf that follows from one. Asserted rather than logged: this fixture's whole subject is a defect that reported PASSED for months
            // because the checker's result was discarded.
            var checkAccessor = segment.CreateChunkAccessor();
            try
            {
                tree.CheckConsistency(ref checkAccessor);
            }
            finally
            {
                checkAccessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ============================================================================================================================================
    // IXW-05, driven deterministically. The race it guards is measured at 1 in 7,162 root splits, so a concurrency test verifies it with probability near
    // zero; these force the exact interleaving with two event handoffs and no sleeps.
    // ============================================================================================================================================

    /// <summary>
    /// A writer parked on a validated root-leaf, while another writer root-splits underneath it, restarts instead of building a second root.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-05")]
    public unsafe void RootSplitsUnderAParkedWriter_TheParkedWriterRestartsInsteadOfBuildingASecondRoot()
    {
        RunStaleRootScenario(out var tree, out var segment, out var exceptions);

        Assert.That(exceptions, Is.Empty, "neither writer should throw");
        Assert.That(
            tree.InsertRetryExitCount(InsertRetryExit.RootMovedUnderDescent),
            Is.GreaterThanOrEqualTo(1),
            "the parked writer's path top stopped being the root, so IXW-05's guard must have sent it back to re-descend");

        // This assertion is the mutant, not a separate test: disabling the guard in BTree.Insert.cs makes the count above 0 and this test red in 19 ms,
        // measured. A second test asserting only CheckConsistency was tried and deleted — it passed with the guard disabled too, so it discriminated
        // nothing, which is the failure mode this whole fixture exists to prevent.

        var accessor = segment.CreateChunkAccessor();
        try
        {
            tree.CheckConsistency(ref accessor);
        }
        finally
        {
            accessor.Dispose();
            _serviceProvider.GetRequiredService<EpochManager>().ExitScope(_scenarioScope);
        }
    }

    private static void CheckConsistencyOn(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor)
        => tree.CheckConsistency(ref accessor);

    /// <summary>
    /// The detector: a tree whose root gains a LEAF child while its other children are internal — IXW-05's exact <c>on_violation</c> shape — is reported.
    /// </summary>
    /// <remarks>
    /// The verifier above proves the GUARD fires; this proves the CHECK that would catch a regression can fail. The corrupt shape is built directly rather
    /// than raced for: re-pointing one of the root's separators at a leaf from two levels down is precisely what Phase 4 does when it runs `SetLeft(Root)`
    /// against a Root that has become internal while promoting a leaf, and it is what `ValidateLeafDepths` exists to report.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("IXW-05")]
    public unsafe void Mutant_ARootWithChildrenAtDifferingDepths_IsReported()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 1; i <= 3000; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }

            // Green first — otherwise the mutant proves nothing about the mutation.
            Assert.That(tree.Height, Is.GreaterThanOrEqualTo(3), "need at least three levels for a leaf to be attachable at the wrong one");
            Assert.DoesNotThrow(() => CheckConsistencyOn(tree, ref accessor), "the unmutated tree must be clean, or this mutant tests nothing");

            // The mutation: point one of the root's separators at a leaf that lives two levels down, so leaves sit at differing depths.
            var root = tree.DiagnosticRoot;
            var midChild = root.GetChild(0, ref accessor);
            var deepLeaf = midChild.GetChild(0, ref accessor);
            Assert.That(deepLeaf.GetIsLeaf(ref accessor), Is.True, "expected a leaf two levels below the root");

            var separator = root.GetFirst(ref accessor);
            root.SetFirst(new IntSingleBTree<PersistentStore>.KeyValueItem(separator.Key, deepLeaf.ChunkId), ref accessor);

            // Reduced to a report string first, matching the IXW-04 mutant: AssertDetects wants the verifier's ASSERTION to fail, and CheckConsistency
            // signals by throwing.
            string report = null;
            try
            {
                CheckConsistencyOn(tree, ref accessor);
            }
            catch (Exception ex)
            {
                report = ex.Message;
            }
            accessor.Dispose();

            RuleMutants.AssertDetects("IXW-05", "differing depths", () => Assert.That(report, Is.Null, report));
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Parks writer A on a validated, write-locked ROOT-LEAF, lets writer B split that root and publish a new one, then releases A into the guard.
    /// </summary>
    /// <remarks>
    /// A is held at <c>OnLeafLockedBeforeRootCheck</c>, which fires after A has taken and validated the leaf's write lock and before the IXW-05 check. B
    /// cannot take that leaf's lock while A holds it, so B is released only once A is parked and B's own insert then drives the root split through the
    /// pessimistic path. Two <see cref="ManualResetEventSlim"/> handoffs, no sleeps, no dependence on the scheduler.
    /// </remarks>
    private unsafe void RunStaleRootScenario(out IntSingleBTree<PersistentStore> tree, out ChunkBasedSegment<PersistentStore> segment,
                                             out ConcurrentBag<Exception> exceptions)
    {
        var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var seg = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        segment = seg;
        exceptions = new ConcurrentBag<Exception>();
        var errors = exceptions;

        var setupDepth = epochManager.EnterScope();
        var setup = segment.CreateChunkAccessor();
        var built = new IntSingleBTree<PersistentStore>(segment);

        // Fill the root leaf to exactly capacity so the very next insert must split it, and the tree is still a single leaf: ctx.Depth == 0.
        for (int i = 1; i <= Index32Chunk.Capacity; i++)
        {
            built.Add(i * 10, i, ref setup);
        }
        setup.Dispose();
        tree = built;

        using var aParked = new ManualResetEventSlim(false);
        using var bDone = new ManualResetEventSlim(false);
        int hookArmed = 0;

        OlcDescentTrace.OnDescentComplete = (leafChunkId, depth) =>
        {
            // Once, and only for a descent that found the root to be a leaf — that is the Depth == 0 shape IXW-05 guards. B must pass through this same hook
            // freely to perform its split, so the arming is one-shot.
            if (depth != 0 || Interlocked.CompareExchange(ref hookArmed, 1, 0) != 0)
            {
                return;
            }
            aParked.Set();
            bDone.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            var a = Task.Factory.StartNew(() =>
            {
                var d = epochManager.EnterScope();
                try
                {
                    var wa = seg.CreateChunkAccessor();
                    built.Add(5, 5, ref wa);   // routes into the root leaf, forcing the split path
                    wa.CommitChanges();
                    wa.Dispose();
                }
                catch (Exception ex) { errors.Add(ex); }
                finally { epochManager.ExitScope(d); }
            }, TaskCreationOptions.LongRunning);

            Assert.That(aParked.Wait(TimeSpan.FromSeconds(10)), Is.True, "writer A never reached the pre-guard seam");

            var b = Task.Factory.StartNew(() =>
            {
                var d = epochManager.EnterScope();
                try
                {
                    var wb = seg.CreateChunkAccessor();
                    for (int i = 1; i <= 6; i++)
                    {
                        built.Add(i * 10 + 5, i, ref wb);   // splits the root leaf and publishes a new root
                    }
                    wb.CommitChanges();
                    wb.Dispose();
                }
                catch (Exception ex) { errors.Add(ex); }
                finally { epochManager.ExitScope(d); bDone.Set(); }
            }, TaskCreationOptions.LongRunning);

            Assert.That(Task.WaitAll([a, b], TimeSpan.FromSeconds(20)), Is.True, "a writer never completed");
        }
        finally
        {
            OlcDescentTrace.OnDescentComplete = null;
        }

        // Scope deliberately left OPEN: the caller creates a ChunkAccessor to run CheckConsistency, and that requires one.
        _scenarioScope = setupDepth;
    }

    private int _scenarioScope;

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
    /// Every reason code has a name. Without this, adding a code and forgetting its name turns the MaxPessimisticRestarts liveness report into an
    /// IndexOutOfRangeException raised while building that report's own message — losing the bug report and the bug with it.
    /// </summary>
    [Test]
    public void InsertRetryExit_EveryCodeHasAName()
    {
        Assert.That(InsertRetryExit.Names, Has.Length.EqualTo(InsertRetryExit.Count));
        Assert.That(InsertRetryExit.Names, Has.None.Null.And.None.Empty);
    }

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
