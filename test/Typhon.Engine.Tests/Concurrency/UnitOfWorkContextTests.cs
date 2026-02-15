using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Tests;

[TestFixture]
public class UnitOfWorkContextTests
{
    // ========================================
    // Construction & Defaults
    // ========================================

    [Test]
    public void StructSize_Is24Bytes()
    {
        // 16B WaitContext + 2B UowId + 2B padding + 4B holdoffCount = 24
        Assert.That(Unsafe.SizeOf<UnitOfWorkContext>(), Is.EqualTo(24));
    }

    [Test]
    public void Default_IsExpired()
    {
        // default(UnitOfWorkContext) is fail-safe: already expired
        UnitOfWorkContext ctx = default;

        Assert.That(ctx.IsExpired, Is.True);
    }

    [Test]
    public void Default_ThrowIfCancelled_Throws()
    {
        UnitOfWorkContext ctx = default;

        Assert.Throws<TyphonTimeoutException>(() => ctx.ThrowIfCancelled());
    }

    [Test]
    public void None_HasInfiniteDeadline()
    {
        var ctx = UnitOfWorkContext.None;

        Assert.That(ctx.IsExpired, Is.False);
        Assert.That(ctx.Deadline.IsInfinite, Is.True);
        Assert.That(ctx.IsCancellationRequested, Is.False);
    }

    [Test]
    public void None_ThrowIfCancelled_DoesNotThrow()
    {
        var ctx = UnitOfWorkContext.None;

        Assert.DoesNotThrow(() => ctx.ThrowIfCancelled());
    }

    [Test]
    public void FromTimeout_CreatesValidContext()
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));

        Assert.That(ctx.IsExpired, Is.False);
        Assert.That(ctx.IsCancellationRequested, Is.False);
    }

    [Test]
    public void FromTimeout_WithToken_CarriesBoth()
    {
        using var cts = new CancellationTokenSource();
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10), cts.Token);

        Assert.That(ctx.IsExpired, Is.False);
        Assert.That(ctx.IsCancellationRequested, Is.False);

        cts.Cancel();

        Assert.That(ctx.IsCancellationRequested, Is.True);
    }

    [Test]
    public void Constructor_WithUowId_PreservesId()
    {
        var ctx = new UnitOfWorkContext(WaitContext.FromTimeout(TimeSpan.FromSeconds(1)), uowId: 42);

        Assert.That(ctx.UowId, Is.EqualTo(42));
    }

    [Test]
    public void Constructor_DeadlineAndToken_SetsWaitContext()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var ctx = new UnitOfWorkContext(deadline, cts.Token, uowId: 7);

        Assert.That(ctx.Deadline, Is.EqualTo(deadline));
        Assert.That(ctx.UowId, Is.EqualTo(7));
    }

    // ========================================
    // ThrowIfCancelled
    // ========================================

    [Test]
    public void ThrowIfCancelled_ExpiredDeadline_ThrowsTyphonTimeoutException()
    {
        var ctx = new UnitOfWorkContext(Deadline.Zero, CancellationToken.None);

        var ex = Assert.Throws<TyphonTimeoutException>(() => ctx.ThrowIfCancelled());
        Assert.That(ex!.ErrorCode, Is.EqualTo(TyphonErrorCode.TransactionTimeout));
    }

    [Test]
    public void ThrowIfCancelled_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = new UnitOfWorkContext(Deadline.Infinite, cts.Token);

        Assert.Throws<OperationCanceledException>(() => ctx.ThrowIfCancelled());
    }

    [Test]
    public void ThrowIfCancelled_ValidContext_DoesNotThrow()
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));

        Assert.DoesNotThrow(() => ctx.ThrowIfCancelled());
    }

    [Test]
    public void ThrowIfCancelled_InHoldoff_DoesNotThrow_EvenIfExpired()
    {
        var ctx = new UnitOfWorkContext(Deadline.Zero, CancellationToken.None);
        ctx.BeginHoldoff();

        Assert.DoesNotThrow(() => ctx.ThrowIfCancelled());
    }

    [Test]
    public void ThrowIfCancelled_InHoldoff_DoesNotThrow_EvenIfCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = new UnitOfWorkContext(Deadline.Infinite, cts.Token);
        ctx.BeginHoldoff();

        Assert.DoesNotThrow(() => ctx.ThrowIfCancelled());
    }

    [Test]
    public void ThrowIfCancelled_AfterHoldoffExit_Throws()
    {
        var ctx = new UnitOfWorkContext(Deadline.Zero, CancellationToken.None);
        ctx.BeginHoldoff();

        // In holdoff — no throw
        Assert.DoesNotThrow(() => ctx.ThrowIfCancelled());

        ctx.EndHoldoff();

        // Out of holdoff — throws
        Assert.Throws<TyphonTimeoutException>(() => ctx.ThrowIfCancelled());
    }

    // ========================================
    // Holdoff & HoldoffScope
    // ========================================

    [Test]
    public void BeginHoldoff_SetsIsInHoldoff()
    {
        var ctx = UnitOfWorkContext.None;

        Assert.That(ctx.IsInHoldoff, Is.False);

        ctx.BeginHoldoff();

        Assert.That(ctx.IsInHoldoff, Is.True);

        ctx.EndHoldoff();

        Assert.That(ctx.IsInHoldoff, Is.False);
    }

    [Test]
    public void NestedHoldoffs_RequireBothExits()
    {
        var ctx = UnitOfWorkContext.None;

        ctx.BeginHoldoff();
        ctx.BeginHoldoff();

        Assert.That(ctx.IsInHoldoff, Is.True);

        ctx.EndHoldoff(); // inner
        Assert.That(ctx.IsInHoldoff, Is.True); // still in holdoff

        ctx.EndHoldoff(); // outer
        Assert.That(ctx.IsInHoldoff, Is.False);
    }

    [Test]
    public void EnterHoldoff_Scope_IncrementsAndDecrements()
    {
        var ctx = UnitOfWorkContext.None;

        Assert.That(ctx.IsInHoldoff, Is.False);

        using (ctx.EnterHoldoff())
        {
            Assert.That(ctx.IsInHoldoff, Is.True);
        }

        Assert.That(ctx.IsInHoldoff, Is.False);
    }

    [Test]
    public void HoldoffScope_DoubleDispose_IsSafe()
    {
        var ctx = UnitOfWorkContext.None;

        var scope = ctx.EnterHoldoff();
        Assert.That(ctx.IsInHoldoff, Is.True);

        scope.Dispose();
        Assert.That(ctx.IsInHoldoff, Is.False);

        // Second dispose — should be a no-op, not underflow
        scope.Dispose();
        Assert.That(ctx.IsInHoldoff, Is.False);
    }

    [Test]
    public void HoldoffScope_Nested_WorksCorrectly()
    {
        var ctx = UnitOfWorkContext.None;

        using (ctx.EnterHoldoff())
        {
            Assert.That(ctx.IsInHoldoff, Is.True);

            using (ctx.EnterHoldoff())
            {
                Assert.That(ctx.IsInHoldoff, Is.True);
            }

            // Inner scope exited, outer still active
            Assert.That(ctx.IsInHoldoff, Is.True);
        }

        Assert.That(ctx.IsInHoldoff, Is.False);
    }

    // ========================================
    // WaitContext Integration
    // ========================================

    [Test]
    public void WaitContext_FromUnitOfWorkContext_ReturnsCopy()
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(5));

        var wc = WaitContext.FromUnitOfWorkContext(ref ctx);

        Assert.That(wc.Deadline, Is.EqualTo(ctx.Deadline));
    }

    [Test]
    public void Properties_DelegateToEmbeddedWaitContext()
    {
        using var cts = new CancellationTokenSource();
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(10));
        var ctx = new UnitOfWorkContext(deadline, cts.Token);

        Assert.That(ctx.Deadline, Is.EqualTo(deadline));
        Assert.That(ctx.Token, Is.EqualTo(cts.Token));
        Assert.That(ctx.IsExpired, Is.False);
        Assert.That(ctx.IsCancellationRequested, Is.False);
        Assert.That(ctx.Remaining, Is.GreaterThan(TimeSpan.Zero));
    }
}
