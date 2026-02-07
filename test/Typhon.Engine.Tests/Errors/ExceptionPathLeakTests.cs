using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests verifying that exception paths in lock-acquisition code properly dispose
/// <see cref="ChunkHandle"/> and <see cref="ChunkAccessor"/> resources before throwing,
/// preventing page-cache pin leaks under contention.
/// </summary>
[TestFixture]
class ExceptionPathLeakTests : TestBase<ExceptionPathLeakTests>
{
    private DatabaseEngine _dbe;
    private long _entityId;
    private TimeoutOptions _savedTimeouts;

    [SetUp]
    public override void Setup()
    {
        base.Setup();

        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(_dbe);

        // Create and commit an entity so revision chains are populated
        var comp = new CompA(42);
        using var t = _dbe.CreateTransaction();
        _entityId = t.CreateEntity(ref comp);
        t.Commit();

        // Override timeouts AFTER DatabaseEngine creation (its ctor sets TimeoutOptions.Current)
        _savedTimeouts = TimeoutOptions.Current;
        TimeoutOptions.Current = new TimeoutOptions
        {
            RevisionChainLockTimeout = TimeSpan.FromMilliseconds(50),
            SegmentAllocationLockTimeout = TimeSpan.FromMilliseconds(50),
            DefaultLockTimeout = _savedTimeouts.DefaultLockTimeout,
            PageCacheLockTimeout = _savedTimeouts.PageCacheLockTimeout,
            BTreeLockTimeout = _savedTimeouts.BTreeLockTimeout,
            TransactionChainLockTimeout = _savedTimeouts.TransactionChainLockTimeout,
        };
    }

    [TearDown]
    public override void TearDown()
    {
        _dbe?.Dispose();
        TimeoutOptions.Current = _savedTimeouts;
        base.TearDown();
    }

    #region Test 1: RevisionEnumerator constructor

    [Test]
    [CancelAfter(5000)]
    public void RevisionEnumerator_Constructor_WhenLockTimeout_DoesNotLeakChunkHandle()
    {
        var ct = _dbe.GetComponentTable<CompA>();
        var segment = ct.CompRevTableSegment;
        var firstChunkId = LookupRevisionChunkId(ct, _entityId);

        // Create the accessor that the main thread will use
        var accessor = segment.CreateChunkAccessor();
        var snapshot = accessor.SnapshotInternalState();

        var acquired = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Background thread holds the revision chain lock exclusively
        var holder = Task.Run(() =>
        {
            var holderAccessor = segment.CreateChunkAccessor();
            ref var header = ref holderAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, false);
            header.EnterControlLockForTest();
            acquired.Set();
            canRelease.Wait();
            header.ExitControlLockForTest();
            holderAccessor.Dispose();
        });

        acquired.Wait();

        // Main thread: attempt to create RevisionEnumerator — should throw LockTimeoutException
        try
        {
            var enumerator = new RevisionEnumerator(ref accessor, firstChunkId, true, true);
            enumerator.Dispose();
            Assert.Fail("Expected LockTimeoutException was not thrown");
        }
        catch (LockTimeoutException)
        {
            // Expected — now verify no resources leaked
        }

        Assert.That(accessor.CheckInternalState(in snapshot), Is.True,
            "ChunkAccessor pin counters should be unchanged after RevisionEnumerator timeout — a leaked ChunkHandle would leave a pin");

        canRelease.Set();
        holder.Wait();
        accessor.Dispose();
    }

    #endregion

    #region Test 2: GetRevisionElement chain-walk path

    [Test]
    [CancelAfter(5000)]
    public void GetRevisionElement_WhenLockTimeout_DoesNotLeakChunkHandles()
    {
        var ct = _dbe.GetComponentTable<CompA>();
        var segment = ct.CompRevTableSegment;
        var firstChunkId = LookupRevisionChunkId(ct, _entityId);

        var accessor = segment.CreateChunkAccessor();
        var snapshot = accessor.SnapshotInternalState();

        var acquired = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Background thread holds the revision chain lock exclusively
        var holder = Task.Run(() =>
        {
            var holderAccessor = segment.CreateChunkAccessor();
            ref var header = ref holderAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, false);
            header.EnterControlLockForTest();
            acquired.Set();
            canRelease.Wait();
            header.ExitControlLockForTest();
            holderAccessor.Dispose();
        });

        acquired.Wait();

        // Request a revision index >= CompRevCountInRoot to trigger the chain-walk path
        // which acquires two ChunkHandles (firstHandle + curHandle) before attempting the lock
        var revisionIndex = (short)ComponentRevisionManager.CompRevCountInRoot;

        try
        {
            ComponentRevisionManager.GetRevisionElement(ref accessor, firstChunkId, revisionIndex);
            Assert.Fail("Expected LockTimeoutException was not thrown");
        }
        catch (LockTimeoutException)
        {
            // Expected — now verify no resources leaked
        }

        Assert.That(accessor.CheckInternalState(in snapshot), Is.True,
            "ChunkAccessor pin counters should be unchanged after GetRevisionElement timeout — leaked ChunkHandles would leave pins");

        canRelease.Set();
        holder.Wait();
        accessor.Dispose();
    }

    #endregion

    #region Test 3: VariableSizedBufferAccessor constructor

    [Test]
    [CancelAfter(5000)]
    public void VariableSizedBufferAccessor_Constructor_WhenLockTimeout_DoesNotLeakResources()
    {
        // Create a VariableSizedBufferSegment and allocate a buffer to get a valid rootChunkId
        var ct = _dbe.GetComponentTable<CompA>();
        var segment = ct.CompRevTableSegment;
        var vsbs = new VariableSizedBufferSegment<int>(segment);

        var setupAccessor = segment.CreateChunkAccessor();
        var rootChunkId = vsbs.AllocateBuffer(ref setupAccessor);
        setupAccessor.Dispose();

        // Pre-warm: touch the root chunk page so it transitions to Idle in the page cache.
        // Without this, the failed VSBS constructor would leave the page in a different state
        // (Free→Idle) even if it properly cleans up, causing a false positive in the snapshot check.
        var warmupAccessor = segment.CreateChunkAccessor();
        _ = warmupAccessor.GetChunkHandle(rootChunkId, false);
        warmupAccessor.Dispose();

        var acquired = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Background thread holds the buffer's AccessControl lock exclusively
        var holder = Task.Run(() =>
        {
            var holderAccessor = segment.CreateChunkAccessor();
            ref var header = ref holderAccessor.GetChunk<VariableSizedBufferRootHeader>(rootChunkId, false);
            header.EnterBufferLockForTest();
            acquired.Set();
            canRelease.Wait();
            header.ExitBufferLockForTest();
            holderAccessor.Dispose();
        });

        acquired.Wait();

        // Snapshot AFTER the holder has acquired the lock — we only want to detect extra pins
        // leaked by the failed GetReadOnlyAccessor call, not the holder's pins
        var mmfSnapshot = _dbe.MMF.SnapshotInternalState();

        // Attempt to create a read-only accessor — should throw due to lock contention
        try
        {
            var bufferAccessor = vsbs.GetReadOnlyAccessor(rootChunkId);
            bufferAccessor.Dispose();
            Assert.Fail("Expected LockTimeoutException was not thrown");
        }
        catch (LockTimeoutException)
        {
            // Expected — now verify no resources leaked
        }

        Assert.That(_dbe.MMF.CheckInternalState(in mmfSnapshot), Is.True,
            "PagedMMF page state should be unchanged after VariableSizedBufferAccessor timeout — leaked handles would leave pages pinned");

        canRelease.Set();
        holder.Wait();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Looks up the revision chain's first chunk ID for a given entity via the PrimaryKeyIndex.
    /// </summary>
    private static int LookupRevisionChunkId(ComponentTable ct, long entityId)
    {
        var indexAccessor = ct.DefaultIndexSegment.CreateChunkAccessor();
        var result = ct.PrimaryKeyIndex.TryGet(entityId, ref indexAccessor);
        indexAccessor.Dispose();
        Assert.That(result.IsSuccess, Is.True, "Entity should exist in PrimaryKeyIndex");
        return result.Value;
    }

    #endregion
}
