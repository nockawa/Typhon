using System;
using Typhon.Engine;

namespace Typhon.Engine.Tests;

/// <summary>
/// Helper for test code to create fresh <see cref="WaitContext"/> instances with finite deadlines.
/// Replaces <c>ref WaitContext.Null</c> in multi-threaded test scenarios so that lock contention
/// causes a timeout instead of hanging forever.
/// </summary>
internal static class TestWaitContext
{
    /// <summary>
    /// Default test timeout — generous enough for CI but finite to detect deadlocks.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    [ThreadStatic] private static WaitContext _current;

    /// <summary>
    /// Returns a reference to a fresh <see cref="WaitContext"/> with <see cref="DefaultTimeout"/>.
    /// Each access creates a new deadline, ensuring callers always get a live timer.
    /// </summary>
    public static ref WaitContext Default
    {
        get
        {
            _current = WaitContext.FromTimeout(DefaultTimeout);
            return ref _current;
        }
    }

    /// <summary>
    /// Returns a reference to a fresh <see cref="WaitContext"/> with a custom timeout.
    /// </summary>
    public static ref WaitContext WithTimeout(TimeSpan timeout)
    {
        _current = WaitContext.FromTimeout(timeout);
        return ref _current;
    }
}
