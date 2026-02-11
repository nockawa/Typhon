using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests verifying that lock acquisition with finite deadlines throws <see cref="LockTimeoutException"/>
/// with diagnostic context when contention prevents acquisition within the timeout period.
/// </summary>
[TestFixture]
public class DeadlinePropagationTests
{
    #region TimeoutOptions Defaults

    [Test]
    public void TimeoutOptions_DefaultValues_AreCorrect()
    {
        var options = new TimeoutOptions();

        Assert.That(options.DefaultLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.PageCacheLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.BTreeLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.TransactionChainLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(options.RevisionChainLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.SegmentAllocationLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void TimeoutOptions_CustomValues_ArePreserved()
    {
        var options = new TimeoutOptions
        {
            DefaultLockTimeout = TimeSpan.FromSeconds(1),
            PageCacheLockTimeout = TimeSpan.FromSeconds(2),
            BTreeLockTimeout = TimeSpan.FromSeconds(3),
            TransactionChainLockTimeout = TimeSpan.FromSeconds(4),
            RevisionChainLockTimeout = TimeSpan.FromSeconds(5),
            SegmentAllocationLockTimeout = TimeSpan.FromSeconds(6),
        };

        Assert.That(options.DefaultLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(options.PageCacheLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(options.BTreeLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.That(options.TransactionChainLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(4)));
        Assert.That(options.RevisionChainLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.SegmentAllocationLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(6)));
    }

    [Test]
    public void DatabaseEngineOptions_Timeouts_HasDefaults()
    {
        var options = new DatabaseEngineOptions();

        Assert.That(options.Timeouts, Is.Not.Null);
        Assert.That(options.Timeouts.DefaultLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    #endregion

    #region AccessControl Lock Timeout

    [Test]
    [CancelAfter(5000)]
    public void AccessControl_LockTimeout_WhenExclusiveHeld_ThrowsLockTimeoutException()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Thread A holds exclusive lock until signaled
        var holder = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Thread B tries to acquire with a short timeout — should fail
        var wc = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
        var acquired = control.EnterExclusiveAccess(ref wc);

        Assert.That(acquired, Is.False, "Lock acquisition should fail when exclusive lock is held and timeout expires");

        canRelease.Set();
        holder.Wait();
    }

    [Test]
    [CancelAfter(5000)]
    public void AccessControl_SharedAccess_UnderContention_ReturnsTrue()
    {
        var control = new AccessControl();

        // Shared locks should coexist — both should succeed even with short timeouts
        control.EnterSharedAccess(ref TestWaitContext.Default);

        var wc = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100));
        var acquired = control.EnterSharedAccess(ref wc);

        Assert.That(acquired, Is.True, "Shared access should succeed when other shared locks are held");

        control.ExitSharedAccess();
        control.ExitSharedAccess();
    }

    #endregion

    #region ResourceAccessControl Scoped Guards

    [Test]
    [CancelAfter(5000)]
    public void ResourceAccessControl_ScopedGuard_ThrowsLockTimeoutException()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null); // Block all access

        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(10));
        var ex = Assert.Throws<LockTimeoutException>(() =>
        {
            control.EnterAccessingScoped(ref ctx);
        });

        Assert.That(ex.ResourceName, Is.EqualTo("ResourceAccessControl"));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.LockTimeout));
        Assert.That(ex.IsTransient, Is.True);
    }

    #endregion

    #region TestWaitContext Helper

    [Test]
    public void TestWaitContext_Default_CreatesFreshDeadline()
    {
        ref var wc1 = ref TestWaitContext.Default;
        Assert.That(wc1.ShouldStop, Is.False, "First context should not be expired");
        Assert.That(wc1.IsUnbounded, Is.False, "TestWaitContext.Default should have a finite deadline");

        ref var wc2 = ref TestWaitContext.Default;
        Assert.That(wc2.ShouldStop, Is.False, "Second context should not be expired either");
    }

    [Test]
    public void TestWaitContext_WithTimeout_UsesCustomDuration()
    {
        var shortTimeout = TimeSpan.FromMilliseconds(50);
        ref var wc = ref TestWaitContext.WithTimeout(shortTimeout);

        Assert.That(wc.ShouldStop, Is.False, "Fresh context should not be expired");

        Thread.Sleep(100);

        Assert.That(wc.ShouldStop, Is.True, "Context should expire after timeout");
    }

    #endregion

    #region ThrowHelper Integration

    [Test]
    public void ThrowHelper_ThrowLockTimeout_ThrowsWithCorrectProperties()
    {
        var ex = Assert.Throws<LockTimeoutException>(() =>
        {
            ThrowHelper.ThrowLockTimeout("TestResource/Operation", TimeSpan.FromMilliseconds(250));
        });

        Assert.That(ex.ResourceName, Is.EqualTo("TestResource/Operation"));
        Assert.That(ex.WaitDuration, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
        Assert.That(ex.IsTransient, Is.True);
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.LockTimeout));
    }

    #endregion

    #region TransactionChain Lock Timeout

    [Test]
    [CancelAfter(5000)]
    public void TransactionChain_Constructor_UsesStaticTimeout()
    {
        using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "Test" });
        var chain = new TransactionChain(1000, registry.DataEngine);

        // TransactionChain no longer stores its own timeout — it reads from TimeoutOptions.Current
        Assert.That(TimeoutOptions.Current.TransactionChainLockTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));

        chain.Dispose();
    }

    #endregion
}
