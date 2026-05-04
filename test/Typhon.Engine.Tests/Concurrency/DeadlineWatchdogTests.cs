using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="DeadlineWatchdog"/> (instance-based ResourceNode, PriorityQueue + shared timer).
/// Covers: registration, short-circuits, timing, disposal, concurrency, resource tree placement.
/// </summary>
[TestFixture]
public class DeadlineWatchdogTests
{
    private ResourceRegistry _registry;
    private HighResolutionSharedTimerService _sharedTimer;
    private DeadlineWatchdog _watchdog;

    [SetUp]
    public void Setup()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "WatchdogTest" });
        _sharedTimer = new HighResolutionSharedTimerService(_registry.Timer);
        _watchdog = new DeadlineWatchdog(_registry, _sharedTimer);
    }

    [TearDown]
    public void TearDown()
    {
        _watchdog.Dispose();
        _sharedTimer.Dispose();
        _registry.Dispose();
    }

    #region Registration Short-Circuits

    [Test]
    public void Register_InfiniteDeadline_ReturnsCancellationTokenNone()
    {
        var token = _watchdog.Register(Deadline.Infinite);

        Assert.That(token, Is.EqualTo(CancellationToken.None));
    }

    [Test]
    public void Register_ExpiredDeadline_ReturnsAlreadyCancelledToken()
    {
        // Deadline.Zero has _ticks=0, always expired
        var token = _watchdog.Register(Deadline.Zero);

        Assert.That(token.IsCancellationRequested, Is.True);
    }

    [Test]
    public void Register_ValidDeadline_ReturnsUncancelledToken()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(10));

        var token = _watchdog.Register(deadline);

        Assert.That(token.IsCancellationRequested, Is.False);
        Assert.That(token.CanBeCanceled, Is.True);
    }

    [Test]
    public void Register_ValidDeadline_TokenCanBeCanceled()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(10));

        var token = _watchdog.Register(deadline);

        Assert.That(token.CanBeCanceled, Is.True);
    }

    #endregion

    #region Timing Tests

    [Test]
    [Category("Timing")]
    public void Register_ShortDeadline_TokenCancelledAfterExpiry()
    {
        var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(50));

        var token = _watchdog.Register(deadline);

        Assert.That(token.IsCancellationRequested, Is.False, "Should not be cancelled immediately");

        // Wait up to 500ms — the 50ms deadline should fire well within this
        var cancelled = token.WaitHandle.WaitOne(500);

        Assert.That(cancelled, Is.True, "Token should have been cancelled after deadline expired");
        Assert.That(token.IsCancellationRequested, Is.True);
    }

    [Test]
    [Category("Timing")]
    public void Register_MultipleDeadlines_AllFire()
    {
        var d1 = Deadline.FromTimeout(TimeSpan.FromMilliseconds(30));
        var d2 = Deadline.FromTimeout(TimeSpan.FromMilliseconds(60));
        var d3 = Deadline.FromTimeout(TimeSpan.FromMilliseconds(90));

        var t1 = _watchdog.Register(d1);
        var t2 = _watchdog.Register(d2);
        var t3 = _watchdog.Register(d3);

        // Wait for all to fire (generous timeout)
        var all = WaitHandle.WaitAll([t1.WaitHandle, t2.WaitHandle, t3.WaitHandle], 2000);

        Assert.That(all, Is.True, "All three deadline tokens should have been canceled");
        Assert.That(t1.IsCancellationRequested, Is.True);
        Assert.That(t2.IsCancellationRequested, Is.True);
        Assert.That(t3.IsCancellationRequested, Is.True);
    }

    [Test]
    [Category("Timing")]
    public void Register_50msDeadline_CancelledWithinTolerance()
    {
        var sw = Stopwatch.StartNew();
        var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(50));

        var token = _watchdog.Register(deadline);

        // Wait up to 500ms
        token.WaitHandle.WaitOne(500);
        sw.Stop();

        Assert.That(token.IsCancellationRequested, Is.True, "Token should be cancelled");
        // Generous tolerance: 50ms deadline should fire between 40ms and 300ms
        // (accounting for timer resolution + CI jitter)
        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(40), "Should not fire too early");
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(300), "Should fire within reasonable time");
    }

    #endregion

    #region Disposal

    [Test]
    public void Dispose_CancelsAllRemainingDeadlines()
    {
        var tokens = new List<CancellationToken>();
        for (int i = 0; i < 5; i++)
        {
            tokens.Add(_watchdog.Register(Deadline.FromTimeout(TimeSpan.FromSeconds(30))));
        }

        // Verify none are cancelled yet
        foreach (var t in tokens)
        {
            Assert.That(t.IsCancellationRequested, Is.False);
        }

        _watchdog.Dispose();

        // All should now be cancelled
        foreach (var t in tokens)
        {
            Assert.That(t.IsCancellationRequested, Is.True, "All remaining tokens should be cancelled on dispose");
        }
    }

    [Test]
    public void Dispose_WhenNeverRegistered_DoesNotThrow()
    {
        // Create a fresh watchdog that never had Register() called
        using var freshWatchdog = new DeadlineWatchdog(_registry, _sharedTimer);

        Assert.DoesNotThrow(() => freshWatchdog.Dispose());
    }

    [Test]
    public void Register_AfterDispose_Throws()
    {
        _watchdog.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            _watchdog.Register(Deadline.FromTimeout(TimeSpan.FromSeconds(1))));
    }

    #endregion

    #region Concurrency

    [Test]
    [Category("Timing")]
    [CancelAfter(10000)]
    public void ConcurrentRegistration_100Deadlines_AllFireCorrectly()
    {
        const int count = 50;
        var tokens = new CancellationToken[count];
        var threads = new Thread[count];

        for (int i = 0; i < count; i++)
        {
            var idx = i;
            threads[i] = new Thread(() =>
            {
                // Stagger deadlines: 30ms to 130ms
                var ms = 30 + (idx % 5) * 25;
                tokens[idx] = _watchdog.Register(Deadline.FromTimeout(TimeSpan.FromMilliseconds(ms)));
            });
        }

        foreach (var t in threads)
        {
            t.Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        // Wait for all to fire (max deadline is 130ms, 250ms gives ~2× margin)
        Thread.Sleep(250);

        int cancelledCount = 0;
        foreach (var token in tokens)
        {
            if (token.IsCancellationRequested)
            {
                cancelledCount++;
            }
        }

        Assert.That(cancelledCount, Is.EqualTo(count), $"All {count} deadlines should have fired");
    }

    #endregion

    #region Resource Tree

    [Test]
    public void ResourceTree_RegistersUnderDataEngine()
    {
        Assert.That(_watchdog.Parent, Is.SameAs(_registry.DataEngine));
        Assert.That(_watchdog.Type, Is.EqualTo(ResourceType.Service));
        Assert.That(_watchdog.Id, Is.EqualTo("DeadlineWatchdog"));
    }

    #endregion

    #region Constructor Validation

    [Test]
    public void Constructor_NullSharedTimer_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeadlineWatchdog(_registry, null));
    }

    #endregion
}
