using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Covers the per-kind suppression deny-list on <see cref="TyphonEvent"/>. Verifies the default seeded state, the runtime mutation API
/// (<see cref="TyphonEvent.SuppressKind"/> / <see cref="TyphonEvent.UnsuppressKind"/>), and the hot-path short-circuit that returns
/// <c>default(T)</c> from suppressed <c>Begin*</c> factories.
/// </summary>
/// <remarks>
/// <b>Why [NonParallelizable]:</b> the suppression array is static global state on <see cref="TyphonEvent"/>. Running this fixture in
/// parallel with any other profiler test could cause cross-test contamination — e.g., this fixture suppresses <c>BTreeInsert</c> briefly,
/// and a concurrent <c>FileExporterIntegrationTests</c> run would see zero BTreeInsert records during that window. Serializing the fixture
/// is the simplest correct answer; the tests themselves run in a few ms total.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class TyphonEventKindSuppressionTests
{
    /// <summary>
    /// Snapshot of the deny-list state we restore in <see cref="TearDown"/>. Covers every kind each test might have touched so the fixture
    /// leaves the profiler in the shipped default when it exits.
    /// </summary>
    private static readonly TraceEventKind[] TrackedKinds =
    [
        TraceEventKind.PageCacheFetch,
        TraceEventKind.PageCacheDiskRead,
        TraceEventKind.PageCacheDiskWrite,
        TraceEventKind.PageCacheAllocatePage,
        TraceEventKind.PageCacheFlush,
        TraceEventKind.PageEvicted,
        TraceEventKind.PageCacheDiskReadCompleted,
        TraceEventKind.PageCacheDiskWriteCompleted,
        TraceEventKind.PageCacheFlushCompleted,
        TraceEventKind.PageCacheBackpressure,
        TraceEventKind.BTreeInsert,
        TraceEventKind.TransactionCommit,
    ];

    [TearDown]
    public void TearDown()
    {
        // Restore the shipped defaults: 10 PageCache.* suppressed, everything else open.
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheFetch);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheDiskRead);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheDiskWrite);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheAllocatePage);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheFlush);
        TyphonEvent.SuppressKind(TraceEventKind.PageEvicted);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheDiskReadCompleted);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheDiskWriteCompleted);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheFlushCompleted);
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheBackpressure);
        TyphonEvent.UnsuppressKind(TraceEventKind.BTreeInsert);
        TyphonEvent.UnsuppressKind(TraceEventKind.TransactionCommit);
    }

    [Test]
    public void DefaultState_PageCacheKindsSuppressed_OthersOpen()
    {
        Assert.Multiple(() =>
        {
            // All 9 PageCache.* kinds ship in the deny-list — the 5 original kickoff kinds, the PageEvicted marker which rides on the same
            // opt-in flag as the enclosing AllocatePage span, and the 3 async-completion kinds (DiskRead/DiskWrite/Flush Completed) which
            // cost extra allocations per tracked I/O and must be opted in explicitly for device-stress investigations.
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheFetch), Is.True, "PageCacheFetch default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskRead), Is.True, "PageCacheDiskRead default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskWrite), Is.True, "PageCacheDiskWrite default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheAllocatePage), Is.True, "PageCacheAllocatePage default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheFlush), Is.True, "PageCacheFlush default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageEvicted), Is.True, "PageEvicted default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskReadCompleted), Is.True, "PageCacheDiskReadCompleted default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskWriteCompleted), Is.True, "PageCacheDiskWriteCompleted default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheFlushCompleted), Is.True, "PageCacheFlushCompleted default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheBackpressure), Is.True, "PageCacheBackpressure default-suppressed");

            // Everything else is open by default.
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeInsert), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeDelete), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.TransactionCommit), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.EcsSpawn), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.ClusterMigration), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.SchedulerChunk), Is.False);
        });
    }

    [Test]
    public void SuppressedPageCacheFetch_BeginFactory_ReturnsDefault()
    {
        // Default state: PageCacheFetch is suppressed. The Begin* factory must short-circuit before Stopwatch / slot acquisition and
        // return default(T), which has SpanId == 0.
        using var scope = TyphonEvent.BeginPageCacheFetch(filePageIndex: 42);
        Assert.That(scope.SpanId, Is.EqualTo(0UL), "Suppressed kind must return default with SpanId=0 — Dispose then no-ops");
    }

    [Test]
    public void UnsuppressedPageCacheFetch_BeginFactory_ReturnsRealSpan()
    {
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheFetch);
        try
        {
            // ProfilerActive is true in the test project's typhon.telemetry.json, and the slot registry has room for one more thread, so
            // the Begin factory should produce a real span with a non-zero SpanId.
            using var scope = TyphonEvent.BeginPageCacheFetch(filePageIndex: 42);
            Assert.That(scope.SpanId, Is.Not.EqualTo(0UL), "Unsuppressed kind must produce a real span");
            Assert.That(scope.FilePageIndex, Is.EqualTo(42));
        }
        finally
        {
            TyphonEvent.SuppressKind(TraceEventKind.PageCacheFetch);
        }
    }

    [Test]
    public void SuppressKind_DeniesArbitraryKindAtRuntime()
    {
        // BTreeInsert is open by default; suppressing it at runtime should short-circuit its Begin factory immediately.
        TyphonEvent.SuppressKind(TraceEventKind.BTreeInsert);
        try
        {
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeInsert), Is.True);

            using var scope = TyphonEvent.BeginBTreeInsert();
            Assert.That(scope.SpanId, Is.EqualTo(0UL), "Runtime-suppressed kind must skip emission");
        }
        finally
        {
            TyphonEvent.UnsuppressKind(TraceEventKind.BTreeInsert);
        }

        // Unsuppressing restores the default emission path.
        Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeInsert), Is.False);
        using var openScope = TyphonEvent.BeginBTreeInsert();
        Assert.That(openScope.SpanId, Is.Not.EqualTo(0UL), "Kind open again after UnsuppressKind");
    }

    [Test]
    public void SuppressKind_AcceptsEveryDefinedKind()
    {
        // Flip every defined kind on and off to prove the array indexing covers the full enum range (0..60 plus 200). If this ever throws
        // IndexOutOfRangeException, the deny-list array was sized smaller than the largest enum value.
        TraceEventKind[] allKinds =
        [
            TraceEventKind.TickStart, TraceEventKind.TickEnd,
            TraceEventKind.PhaseStart, TraceEventKind.PhaseEnd,
            TraceEventKind.SystemReady, TraceEventKind.SystemSkipped,
            TraceEventKind.Instant,
            TraceEventKind.SchedulerChunk,
            TraceEventKind.TransactionCommit, TraceEventKind.TransactionRollback, TraceEventKind.TransactionCommitComponent,
            TraceEventKind.EcsSpawn, TraceEventKind.EcsDestroy,
            TraceEventKind.EcsQueryExecute, TraceEventKind.EcsQueryCount, TraceEventKind.EcsQueryAny,
            TraceEventKind.EcsViewRefresh,
            TraceEventKind.BTreeInsert, TraceEventKind.BTreeDelete,
            TraceEventKind.BTreeNodeSplit, TraceEventKind.BTreeNodeMerge,
            TraceEventKind.PageCacheFetch, TraceEventKind.PageCacheDiskRead, TraceEventKind.PageCacheDiskWrite,
            TraceEventKind.PageCacheAllocatePage, TraceEventKind.PageCacheFlush,
            TraceEventKind.PageEvicted,
            TraceEventKind.PageCacheDiskReadCompleted, TraceEventKind.PageCacheDiskWriteCompleted,
            TraceEventKind.PageCacheFlushCompleted,
            TraceEventKind.PageCacheBackpressure,
            TraceEventKind.ClusterMigration,
            TraceEventKind.TransactionPersist,
            TraceEventKind.WalFlush, TraceEventKind.WalSegmentRotate, TraceEventKind.WalWait,
            TraceEventKind.CheckpointCycle, TraceEventKind.CheckpointCollect, TraceEventKind.CheckpointWrite,
            TraceEventKind.CheckpointFsync, TraceEventKind.CheckpointTransition, TraceEventKind.CheckpointRecycle,
            TraceEventKind.StatisticsRebuild,
            TraceEventKind.NamedSpan,
        ];

        foreach (var kind in allKinds)
        {
            var original = TyphonEvent.IsKindSuppressed(kind);
            TyphonEvent.SuppressKind(kind);
            Assert.That(TyphonEvent.IsKindSuppressed(kind), Is.True, $"SuppressKind({kind})");
            TyphonEvent.UnsuppressKind(kind);
            Assert.That(TyphonEvent.IsKindSuppressed(kind), Is.False, $"UnsuppressKind({kind})");

            // Restore original state
            if (original)
            {
                TyphonEvent.SuppressKind(kind);
            }
        }
    }
}
