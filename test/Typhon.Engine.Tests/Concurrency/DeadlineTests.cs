#nullable enable

using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Concurrency;

[TestFixture]
public class DeadlineTests
{
    // ========================================
    // Default / Zero Semantics (Fail-Safe)
    // ========================================

    [Test]
    public void Default_IsExpired()
    {
        Deadline d = default;

        Assert.That(d.IsExpired, Is.True);
    }

    [Test]
    public void Default_IsNotInfinite()
    {
        Deadline d = default;

        Assert.That(d.IsInfinite, Is.False);
    }

    [Test]
    public void Zero_IsExpired()
    {
        Assert.That(Deadline.Zero.IsExpired, Is.True);
    }

    [Test]
    public void Zero_EqualsDefault()
    {
        Deadline d = default;

        Assert.That(Deadline.Zero, Is.EqualTo(d));
        Assert.That(Deadline.Zero.IsExpired, Is.EqualTo(d.IsExpired));
        Assert.That(Deadline.Zero.IsInfinite, Is.EqualTo(d.IsInfinite));
    }

    [Test]
    public void Zero_Remaining_IsZero()
    {
        Assert.That(Deadline.Zero.Remaining, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Zero_RemainingMilliseconds_IsZero()
    {
        Assert.That(Deadline.Zero.RemainingMilliseconds, Is.EqualTo(0));
    }

    // ========================================
    // Infinite Sentinel
    // ========================================

    [Test]
    public void Infinite_NeverExpires()
    {
        Assert.That(Deadline.Infinite.IsExpired, Is.False);
    }

    [Test]
    public void Infinite_IsInfinite()
    {
        Assert.That(Deadline.Infinite.IsInfinite, Is.True);
    }

    [Test]
    public void Infinite_Remaining_IsInfiniteTimeSpan()
    {
        Assert.That(Deadline.Infinite.Remaining, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Infinite_RemainingMilliseconds_IsMinusOne()
    {
        Assert.That(Deadline.Infinite.RemainingMilliseconds, Is.EqualTo(-1));
    }

    // ========================================
    // FromTimeout Factory
    // ========================================

    [Test]
    public void FromTimeout_Positive_NotExpired()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));

        Assert.That(deadline.IsExpired, Is.False);
        Assert.That(deadline.IsInfinite, Is.False);
    }

    [Test]
    public void FromTimeout_Zero_IsExpired()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.Zero);

        Assert.That(deadline.IsExpired, Is.True);
    }

    [Test]
    public void FromTimeout_Negative_IsExpired()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(-1));

        Assert.That(deadline.IsExpired, Is.True);
    }

    [Test]
    public void FromTimeout_InfiniteTimeSpan_IsInfinite()
    {
        var deadline = Deadline.FromTimeout(Timeout.InfiniteTimeSpan);

        Assert.That(deadline.IsInfinite, Is.True);
        Assert.That(deadline.IsExpired, Is.False);
    }

    [Test]
    [CancelAfter(3000)]
    public void FromTimeout_ExpiresAfterDuration()
    {
        // Use a short timeout so the test completes quickly
        var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(20));

        Assert.That(deadline.IsExpired, Is.False, "Should not be expired immediately");

        // Wait for it to expire
        Thread.Sleep(30);

        Assert.That(deadline.IsExpired, Is.True, "Should be expired after duration");
    }

    // ========================================
    // Remaining Property
    // ========================================

    [Test]
    [CancelAfter(3000)]
    public void Remaining_DecreasesOverTime()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));

        var remaining1 = deadline.Remaining;
        Thread.Sleep(50);
        var remaining2 = deadline.Remaining;

        Assert.That(remaining2, Is.LessThan(remaining1));
    }

    [Test]
    [CancelAfter(3000)]
    public void Remaining_ZeroWhenExpired()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(10));

        Thread.Sleep(20);

        Assert.That(deadline.Remaining, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Remaining_ApproximatelyMatchesTimeout()
    {
        var timeout = TimeSpan.FromSeconds(2);
        var deadline = Deadline.FromTimeout(timeout);

        var remaining = deadline.Remaining;

        // Should be close to 2s (within 100ms to account for test execution time)
        Assert.That(remaining.TotalMilliseconds, Is.GreaterThan(1800));
        Assert.That(remaining.TotalMilliseconds, Is.LessThanOrEqualTo(2000));
    }

    // ========================================
    // RemainingMilliseconds Property
    // ========================================

    [Test]
    public void RemainingMilliseconds_ApproximatelyMatchesTimeout()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(2));

        var ms = deadline.RemainingMilliseconds;

        // Should be close to 2000ms (within 100ms)
        Assert.That(ms, Is.GreaterThan(1800));
        Assert.That(ms, Is.LessThanOrEqualTo(2000));
    }

    [Test]
    [CancelAfter(3000)]
    public void RemainingMilliseconds_ZeroWhenExpired()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(10));

        Thread.Sleep(20);

        Assert.That(deadline.RemainingMilliseconds, Is.EqualTo(0));
    }

    // ========================================
    // Min Utility
    // ========================================

    [Test]
    public void Min_ReturnsTighter()
    {
        var shorter = Deadline.FromTimeout(TimeSpan.FromSeconds(1));
        var longer = Deadline.FromTimeout(TimeSpan.FromSeconds(10));

        var result = Deadline.Min(shorter, longer);

        Assert.That(result, Is.EqualTo(shorter));
    }

    [Test]
    public void Min_InfiniteVsFinite_ReturnsFinite()
    {
        var finite = Deadline.FromTimeout(TimeSpan.FromSeconds(5));

        var result = Deadline.Min(Deadline.Infinite, finite);

        Assert.That(result, Is.EqualTo(finite));
        Assert.That(result.IsInfinite, Is.False);
    }

    [Test]
    public void Min_BothInfinite_ReturnsInfinite()
    {
        var result = Deadline.Min(Deadline.Infinite, Deadline.Infinite);

        Assert.That(result.IsInfinite, Is.True);
    }

    [Test]
    public void Min_ZeroVsFinite_ReturnsZero()
    {
        var finite = Deadline.FromTimeout(TimeSpan.FromSeconds(5));

        var result = Deadline.Min(Deadline.Zero, finite);

        Assert.That(result, Is.EqualTo(Deadline.Zero));
        Assert.That(result.IsExpired, Is.True);
    }

    [Test]
    public void Min_IsCommutative()
    {
        var a = Deadline.FromTimeout(TimeSpan.FromSeconds(1));
        var b = Deadline.FromTimeout(TimeSpan.FromSeconds(10));

        Assert.That(Deadline.Min(a, b), Is.EqualTo(Deadline.Min(b, a)));
    }

    // ========================================
    // ToCancellationToken
    // ========================================

    [Test]
    public void ToCancellationToken_Infinite_ReturnsNone()
    {
        var token = Deadline.Infinite.ToCancellationToken();

        Assert.That(token, Is.EqualTo(CancellationToken.None));
        Assert.That(token.CanBeCanceled, Is.False);
    }

    [Test]
    public void ToCancellationToken_Expired_ReturnsCancelledToken()
    {
        var token = Deadline.Zero.ToCancellationToken();

        Assert.That(token.IsCancellationRequested, Is.True);
    }

    [Test]
    [CancelAfter(3000)]
    public void ToCancellationToken_Normal_CancelsAfterDeadline()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(50));
        var token = deadline.ToCancellationToken();

        Assert.That(token.CanBeCanceled, Is.True);
        Assert.That(token.IsCancellationRequested, Is.False, "Should not be cancelled yet");

        Thread.Sleep(100);

        Assert.That(token.IsCancellationRequested, Is.True, "Should be cancelled after deadline");
    }

    // ========================================
    // TickRatio Validation
    // ========================================

    [Test]
    public void TickRatio_IsPositiveInteger()
    {
        Assert.That(Deadline.TickRatio, Is.GreaterThan(0));
        Assert.That(Stopwatch.Frequency % TimeSpan.TicksPerSecond, Is.EqualTo(0),
            "Stopwatch.Frequency must be an integer multiple of TimeSpan.TicksPerSecond");
    }

    [Test]
    public void TickRatio_RoundTripConversion()
    {
        // A round-trip through TickRatio should preserve values (no precision loss)
        var timeoutTicks = TimeSpan.FromSeconds(5).Ticks;
        var stopwatchTicks = timeoutTicks * Deadline.TickRatio;
        var roundTripped = stopwatchTicks / Deadline.TickRatio;

        Assert.That(roundTripped, Is.EqualTo(timeoutTicks));
    }

    [Test]
    public void RemainingMs_IntegerConsistency()
    {
        // RemainingMilliseconds should approximately match Remaining.TotalMilliseconds
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(3));

        var remainingMs = deadline.RemainingMilliseconds;
        var remainingFromTimeSpan = (int)deadline.Remaining.TotalMilliseconds;

        // Allow ±1ms difference due to time elapsed between the two reads
        Assert.That(remainingMs, Is.InRange(remainingFromTimeSpan - 1, remainingFromTimeSpan + 1));
    }

    // ========================================
    // Equality
    // ========================================

    [Test]
    public void Equality_SameDeadlines_AreEqual()
    {
        var inf1 = Deadline.Infinite;
        var inf2 = Deadline.Infinite;
        var zero1 = Deadline.Zero;
        var zero2 = Deadline.Zero;

        Assert.That(inf1, Is.EqualTo(inf2));
        Assert.That(zero1, Is.EqualTo(zero2));
        Assert.That(inf1 == inf2, Is.True);
        Assert.That(zero1 == zero2, Is.True);
    }

    [Test]
    public void Equality_DifferentDeadlines_AreNotEqual()
    {
        Assert.That(Deadline.Infinite, Is.Not.EqualTo(Deadline.Zero));
        Assert.That(Deadline.Infinite != Deadline.Zero, Is.True);
    }

    [Test]
    public void GetHashCode_SameDeadlines_SameHash()
    {
        Assert.That(Deadline.Infinite.GetHashCode(), Is.EqualTo(Deadline.Infinite.GetHashCode()));
        Assert.That(Deadline.Zero.GetHashCode(), Is.EqualTo(Deadline.Zero.GetHashCode()));
    }

    // ========================================
    // ToString
    // ========================================

    [Test]
    public void ToString_Zero_ShowsExpired()
    {
        Assert.That(Deadline.Zero.ToString(), Is.EqualTo("Deadline(Expired)"));
    }

    [Test]
    public void ToString_Infinite_ShowsInfinite()
    {
        Assert.That(Deadline.Infinite.ToString(), Is.EqualTo("Deadline(Infinite)"));
    }

    [Test]
    public void ToString_Normal_ShowsRemaining()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));

        var str = deadline.ToString();

        Assert.That(str, Does.StartWith("Deadline(Remaining="));
    }

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_IsExpired_ThreadSafe()
    {
        // Multiple threads checking the same deadline should all see consistent results
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(2));
        var barrier = new Barrier(4);
        var errors = 0;

        var tasks = new Task[4];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int j = 0; j < 100_000; j++)
                {
                    // All threads should see not-expired (deadline is 2s in the future)
                    if (deadline.IsExpired)
                    {
                        Interlocked.Increment(ref errors);
                        break;
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        Assert.That(errors, Is.EqualTo(0), "No thread should see premature expiry");
    }

    [Test]
    [CancelAfter(3000)]
    public void MonotonicTime_NeverGoesBackward()
    {
        long previous = Stopwatch.GetTimestamp();
        int checks = 0;

        for (int i = 0; i < 10_000; i++)
        {
            long current = Stopwatch.GetTimestamp();
            Assert.That(current, Is.GreaterThanOrEqualTo(previous),
                $"Timestamp went backward at iteration {i}: {current} < {previous}");
            previous = current;
            checks++;
        }

        Assert.That(checks, Is.EqualTo(10_000));
    }
}
