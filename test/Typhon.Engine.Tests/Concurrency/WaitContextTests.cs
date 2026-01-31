#nullable enable

using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Tests;

[TestFixture]
public class WaitContextTests
{
    // ========================================
    // Default Semantics (Fail-Safe)
    // ========================================

    [Test]
    public void Default_ShouldStop_IsTrue()
    {
        // default(WaitContext) has an expired deadline → ShouldStop = true
        WaitContext ctx = default;

        Assert.That(ctx.ShouldStop, Is.True);
    }

    [Test]
    public void Default_Deadline_IsExpired()
    {
        WaitContext ctx = default;

        Assert.That(ctx.Deadline.IsExpired, Is.True);
    }

    [Test]
    public void Default_Token_IsNone()
    {
        WaitContext ctx = default;

        Assert.That(ctx.Token, Is.EqualTo(CancellationToken.None));
        Assert.That(ctx.Token.CanBeCanceled, Is.False);
    }

    [Test]
    public void Default_IsNotUnbounded()
    {
        // default is expired, not unbounded
        WaitContext ctx = default;

        Assert.That(ctx.IsUnbounded, Is.False);
    }

    // ========================================
    // FromTimeout Factory
    // ========================================

    [Test]
    public void FromTimeout_Positive_ShouldStop_IsFalse()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));

        Assert.That(ctx.ShouldStop, Is.False);
        Assert.That(ctx.Deadline.IsExpired, Is.False);
    }

    [Test]
    [CancelAfter(3000)]
    public void FromTimeout_ExpiresAfterDuration()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100));

        Assert.That(ctx.ShouldStop, Is.False, "Should not be stopped immediately");

        Thread.Sleep(150);

        Assert.That(ctx.ShouldStop, Is.True, "Should be stopped after deadline");
        Assert.That(ctx.Deadline.IsExpired, Is.True);
    }

    [Test]
    public void FromTimeout_Zero_ShouldStop_IsTrue()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.Zero);

        Assert.That(ctx.ShouldStop, Is.True);
    }

    [Test]
    public void FromTimeout_Negative_ShouldStop_IsTrue()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(-1));

        Assert.That(ctx.ShouldStop, Is.True);
    }

    [Test]
    public void FromTimeout_InfiniteTimeSpan_NeverExpires()
    {
        var ctx = WaitContext.FromTimeout(Timeout.InfiniteTimeSpan);

        Assert.That(ctx.ShouldStop, Is.False);
        Assert.That(ctx.Deadline.IsInfinite, Is.True);
    }

    // ========================================
    // FromTimeout with Token
    // ========================================

    [Test]
    public void FromTimeout_WithToken_BothFieldsSet()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5), cts.Token);

        Assert.That(ctx.Deadline.IsExpired, Is.False);
        Assert.That(ctx.Token.CanBeCanceled, Is.True);
        Assert.That(ctx.ShouldStop, Is.False);
    }

    [Test]
    public void FromTimeout_WithToken_CancelStops()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(30), cts.Token);

        Assert.That(ctx.ShouldStop, Is.False, "Should not be stopped before cancel");

        cts.Cancel();

        Assert.That(ctx.ShouldStop, Is.True, "Should be stopped after cancel");
        Assert.That(ctx.Token.IsCancellationRequested, Is.True);
    }

    [Test]
    [CancelAfter(3000)]
    public void FromTimeout_WithToken_EitherStops_DeadlineFirst()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100), cts.Token);

        Thread.Sleep(150);

        // Deadline fired, but token is not cancelled
        Assert.That(ctx.ShouldStop, Is.True);
        Assert.That(ctx.Deadline.IsExpired, Is.True);
        Assert.That(ctx.Token.IsCancellationRequested, Is.False);
    }

    [Test]
    public void FromTimeout_WithToken_EitherStops_CancelFirst()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(30), cts.Token);

        cts.Cancel();

        // Token fired, but deadline is not expired
        Assert.That(ctx.ShouldStop, Is.True);
        Assert.That(ctx.Token.IsCancellationRequested, Is.True);
        Assert.That(ctx.Deadline.IsExpired, Is.False);
    }

    // ========================================
    // FromDeadline Factory
    // ========================================

    [Test]
    public void FromDeadline_Infinite_NeverExpires()
    {
        var ctx = WaitContext.FromDeadline(Deadline.Infinite);

        Assert.That(ctx.ShouldStop, Is.False);
        Assert.That(ctx.Deadline.IsInfinite, Is.True);
        Assert.That(ctx.Token, Is.EqualTo(CancellationToken.None));
    }

    [Test]
    public void FromDeadline_Zero_ShouldStop()
    {
        var ctx = WaitContext.FromDeadline(Deadline.Zero);

        Assert.That(ctx.ShouldStop, Is.True);
    }

    [Test]
    public void FromDeadline_CopiesDeadline()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));
        var ctx = WaitContext.FromDeadline(deadline);

        Assert.That(ctx.Deadline, Is.EqualTo(deadline));
    }

    // ========================================
    // FromToken Factory
    // ========================================

    [Test]
    public void FromToken_HasInfiniteDeadline()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromToken(cts.Token);

        Assert.That(ctx.Deadline.IsInfinite, Is.True);
        Assert.That(ctx.Token.CanBeCanceled, Is.True);
    }

    [Test]
    public void FromToken_CancelStops()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromToken(cts.Token);

        Assert.That(ctx.ShouldStop, Is.False, "Should not be stopped before cancel");

        cts.Cancel();

        Assert.That(ctx.ShouldStop, Is.True, "Should be stopped after cancel");
    }

    [Test]
    public void FromToken_None_IsUnbounded()
    {
        // FromToken with CancellationToken.None = infinite deadline + no cancellation
        var ctx = WaitContext.FromToken(CancellationToken.None);

        Assert.That(ctx.IsUnbounded, Is.True);
        Assert.That(ctx.ShouldStop, Is.False);
    }

    // ========================================
    // NullRef Pattern
    // ========================================

    [Test]
    public void NullRef_DetectedByIsNullRef()
    {
        ref WaitContext nullRef = ref Unsafe.NullRef<WaitContext>();

        Assert.That(Unsafe.IsNullRef(ref nullRef), Is.True);
    }

    [Test]
    public void NullRef_NonNullRef_NotDetected()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));

        Assert.That(Unsafe.IsNullRef(ref ctx), Is.False);
    }

    // ========================================
    // IsUnbounded Property
    // ========================================

    [Test]
    public void IsUnbounded_TrueForInfiniteNoToken()
    {
        var ctx = WaitContext.FromDeadline(Deadline.Infinite);

        Assert.That(ctx.IsUnbounded, Is.True);
    }

    [Test]
    public void IsUnbounded_FalseWhenTokenActive()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromToken(cts.Token);

        // Has infinite deadline but token can be cancelled
        Assert.That(ctx.IsUnbounded, Is.False);
    }

    [Test]
    public void IsUnbounded_FalseWhenDeadlineSet()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));

        Assert.That(ctx.IsUnbounded, Is.False);
    }

    // ========================================
    // Remaining Property
    // ========================================

    [Test]
    public void Remaining_DelegatesToDeadline()
    {
        var ctx = WaitContext.FromDeadline(Deadline.Infinite);

        Assert.That(ctx.Remaining, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Remaining_ZeroWhenExpired()
    {
        var ctx = WaitContext.FromDeadline(Deadline.Zero);

        Assert.That(ctx.Remaining, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Remaining_ApproximatelyMatchesTimeout()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));

        var remaining = ctx.Remaining;

        // Should be close to 2s (within 100ms)
        Assert.That(remaining.TotalMilliseconds, Is.GreaterThan(1800));
        Assert.That(remaining.TotalMilliseconds, Is.LessThanOrEqualTo(2000));
    }

    // ========================================
    // ToString
    // ========================================

    [Test]
    public void ToString_IncludesState_NoToken()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));

        var str = ctx.ToString();

        Assert.That(str, Does.Contain("WaitContext"));
        Assert.That(str, Does.Contain("Deadline="));
        Assert.That(str, Does.Contain("Token=none"));
        Assert.That(str, Does.Contain("ShouldStop=False"));
    }

    [Test]
    public void ToString_IncludesState_WithToken()
    {
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5), cts.Token);

        var str = ctx.ToString();

        Assert.That(str, Does.Contain("Token=active"));
    }

    [Test]
    public void ToString_IncludesState_Expired()
    {
        var ctx = WaitContext.FromDeadline(Deadline.Zero);

        var str = ctx.ToString();

        Assert.That(str, Does.Contain("ShouldStop=True"));
    }

    // ========================================
    // Struct Size Verification
    // ========================================

    [Test]
    public void StructSize_Is16Bytes()
    {
        // WaitContext should be exactly 16 bytes (8 for Deadline + 8 for CancellationToken)
        Assert.That(Unsafe.SizeOf<WaitContext>(), Is.EqualTo(16));
    }

    // ========================================
    // Stress Tests
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public void HighFrequency_ShouldStop_Consistent()
    {
        // A context with 2s deadline should not report ShouldStop during first second
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var oneSecondTicks = System.Diagnostics.Stopwatch.Frequency;

        int checkCount = 0;
        while (System.Diagnostics.Stopwatch.GetTimestamp() - start < oneSecondTicks)
        {
            Assert.That(ctx.ShouldStop, Is.False,
                $"ShouldStop should not be true during first second (check #{checkCount})");
            checkCount++;
        }

        Assert.That(checkCount, Is.GreaterThan(1000),
            "Should have performed many checks in one second");
    }

    [Test]
    [CancelAfter(3000)]
    public void CancellationRace_NoFalseNegatives()
    {
        // After cancellation, ShouldStop must always return true
        using var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(30), cts.Token);

        cts.Cancel();

        // Rapid-fire check that cancellation is visible
        for (int i = 0; i < 100_000; i++)
        {
            Assert.That(ctx.ShouldStop, Is.True,
                $"ShouldStop must be true after cancellation (iteration {i})");
        }
    }
}
