// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

// ReSharper disable RedundantNullableFlowAttribute

namespace Typhon.Engine;

/// <summary>
/// Synchronization primitive that allows multiple concurrent shared access or one exclusive.
/// Uses spin-waiting with <see cref="SpinWait"/>. Costs 8 bytes of data.
/// </summary>
/// <remarks>
/// <para>For blocking operations, pass <c>ref WaitContext</c> to control timeout and cancellation.
/// Use <c>ref WaitContext.Null</c> for infinite wait with zero overhead.</para>
/// <para>Non-blocking operations (Exit*, TryEnter*, Demote*) don't require WaitContext.</para>
/// </remarks>
[PublicAPI]
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public partial struct AccessControl
{
    private ulong _data;

    // ═══════════════════════════════════════════════════════════════════════
    // Bit Layout Constants
    // ═══════════════════════════════════════════════════════════════════════
    // Bit layout, from least to most significant:
    //  8 Shared Usage          (bits 0-7)
    //  8 Shared waiters        (bits 8-15)
    //  8 Exclusive waiters     (bits 16-23)
    //  8 Promoter waiters      (bits 24-31)
    // 16 Exclusive thread Id   (bits 32-47)
    //  1 Contention flag       (bit 48)
    // 13 Reserved              (bits 49-61)
    //  2 States                (bits 62-63)

    private const ulong SharedCounterMask     = 0x0000_0000_0000_00FF;
    private const ulong SharedWaitersMask     = 0x0000_0000_0000_FF00;
    private const ulong ExclusiveWaitersMask  = 0x0000_0000_00FF_0000;
    private const ulong PromoterWaitersMask   = 0x0000_0000_FF00_0000;
    private const ulong ThreadIdMask          = 0x0000_FFFF_0000_0000;
    private const ulong ContentionFlagMask    = 0x0001_0000_0000_0000;  // Bit 48
    private const ulong StateMask             = 0xC000_0000_0000_0000;
    private const ulong IdleState             = 0x0000_0000_0000_0000;
    private const ulong SharedState           = 0x8000_0000_0000_0000;
    private const ulong ExclusiveState        = 0x4000_0000_0000_0000;

    private const int SharedWaitersShift    = 8;
    private const int ExclusiveWaitersShift = 16;
    private const int PromoterWaitersShift  = 24;
    private const int ThreadIdShift         = 32;

    // ═══════════════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes elapsed time in microseconds from a Stopwatch start tick.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeElapsedUs(long startTicks)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTicks;
        return (elapsed * 1_000_000) / Stopwatch.Frequency;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidOperation(string message) => throw new InvalidOperationException(message);

    // ═══════════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Resets the lock to initial state.</summary>
    public void Reset() => _data = 0;

    /// <summary>
    /// Enters shared (reader) access. Multiple threads can hold shared access simultaneously.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if access was acquired; false if timed out or cancelled.</returns>
    public bool EnterSharedAccess(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;

        var ld = new LockData(ref _data, ref ctx);

        while (true)
        {
            switch (ld.State)
            {
                // Switch from Idle to Shared
                case IdleState:
                    // We can start shared only if there are no waiting promoters or exclusives
                    if (ld.CanShareStart)
                    {
                        ld.State = SharedState;
                        ld.SharedCounter = 1;
                        break;
                    }

                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.SharedWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Shared))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                // Already in Shared, increment counter
                case SharedState:
                    ld.SharedCounter++;
                    break;

                // Can't enter shared because we are in exclusive, wait for idle and loop
                case ExclusiveState:
                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.SharedWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Shared))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

            if (!ld.TryUpdate())
            {
                if (ld.ShouldStop)
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.Canceled, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }

                    return false;
                }

                continue;
            }

            // Succeed - record telemetry
            if (hadToWait && level >= TelemetryLevel.Light)
            {
                target?.RecordContention(ComputeElapsedUs(waitStartTicks));
            }
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.SharedAcquired, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
            }

            return true;
        }
    }

    /// <summary>
    /// Exits shared (reader) access.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public void ExitSharedAccess(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref _data);

        while (true)
        {
            switch (ld.State)
            {
                // Either stay in Shared or switch to idle
                case SharedState:
                    // Stay Shared counter -1
                    --ld.SharedCounter;

                    // If counter becomes 0 switch to Idle
                    if (ld.SharedCounter == 0)
                    {
                        ld.State = IdleState;
                    }

                    break;

                case ExclusiveState:
                    break;

                case IdleState:
                    break;
            }

            if (!ld.TryUpdate())
            {
                continue;
            }

            // Record telemetry
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.SharedReleased, 0);
            }

            return;
        }
    }

    /// <summary>
    /// Enters exclusive (writer) access. Only one thread can hold exclusive access.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if access was acquired; false if timed out or cancelled.</returns>
    public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;

        var ld = new LockData(ref _data, ref ctx);

        while (true)
        {
            switch (ld.State)
            {
                // Switch from Idle to Exclusive
                case IdleState:
                    // We can start shared only if there are no waiting promoters or exclusives
                    if (ld.CanExclusiveStart)
                    {
                        // Switch from Idle to Exclusive
                        ld.State = ExclusiveState;
                        ld.ThreadId = Environment.CurrentManagedThreadId;
                        break;
                    }

                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Exclusive))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                // Shared access is active, wait for it to become idle then retry
                case SharedState:
                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Exclusive))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;

                case ExclusiveState:
                    // We have to wait our turn
                    if (!hadToWait)
                    {
                        hadToWait = true;
                        waitStartTicks = Stopwatch.GetTimestamp();
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
                        }
                    }

                    if (!ld.WaitForIdleState(LockData.WaitFor.Exclusive))
                    {
                        if (level >= TelemetryLevel.Deep)
                        {
                            target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                        }

                        return false;
                    }

                    // Fetch the updated state after waiting
                    ld.Fetch();
                    continue;
            }

            if (!ld.TryUpdate())
            {
                if (ld.ShouldStop)
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.Canceled, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }

                    return false;
                }

                continue;
            }

            // Succeed - record telemetry
            if (hadToWait && level >= TelemetryLevel.Light)
            {
                target?.RecordContention(ComputeElapsedUs(waitStartTicks));
            }
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.ExclusiveAcquired, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
            }

            return true;
        }
    }

    /// <summary>
    /// Tries to enter exclusive (writer) access without waiting.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if access was acquired; false if lock is not available.</returns>
    public bool TryEnterExclusiveAccess(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref _data);

        if (ld.State != IdleState)
        {
            return false;
        }

        // Switch from Idle to Exclusive
        ld.State = ExclusiveState;
        ld.ThreadId = Environment.CurrentManagedThreadId;

        if (!ld.TryUpdate())
        {
            return false;
        }

        // Record telemetry
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.ExclusiveAcquired, 0);
        }

        return true;
    }

    /// <summary>
    /// Exits exclusive (writer) access.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public void ExitExclusiveAccess(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref _data);

        while (true)
        {
            switch (ld.State)
            {
                // Switch back to idle
                case ExclusiveState:
                    var curThread = Environment.CurrentManagedThreadId;
                    if (ld.ThreadId != curThread)
                    {
                        Debug.Assert(false);
                    }

                    ld.State = IdleState;
                    ld.ThreadId = 0;
                    break;

                case SharedState:
                    break;

                case IdleState:
                    break;
            }

            if (!ld.TryUpdate())
            {
                continue;
            }

            // Record telemetry
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.ExclusiveReleased, 0);
            }

            return;
        }
    }

    /// <summary>
    /// Tries to promote from shared to exclusive access.
    /// Caller must already hold shared access.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if promotion succeeded; false if timed out or cancelled.</returns>
    public bool TryPromoteToExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;
        var ld = new LockData(ref _data, ref ctx);

        while (true)
        {
            if (ld.State != SharedState)
            {
                ThrowInvalidOperation("Can't promote to exclusive because it's not shared.");
            }

            if (!ld.CanPromoteToExclusive)
            {
                // We have to wait our turn
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.PromoteToExclusiveStart, 0);
                    }
                }

                if (!ld.WaitForIdleState(LockData.WaitFor.Promote))
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                    }

                    return false;
                }

                // Fetch the updated state after waiting
                ld.Fetch();
                continue;
            }

            ld.State = ExclusiveState;
            ld.SharedCounter = 1;
            ld.ThreadId = Environment.CurrentManagedThreadId;

            if (!ld.TryUpdate())
            {
                if (ld.ShouldStop)
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.Canceled, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }

                    return false;
                }

                continue;
            }

            // Succeed - record telemetry
            if (hadToWait && level >= TelemetryLevel.Light)
            {
                target?.RecordContention(ComputeElapsedUs(waitStartTicks));
            }
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.PromoteToExclusiveAcquired, hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
            }

            return true;
        }
    }

    /// <summary>
    /// Demotes from exclusive to shared access.
    /// Caller must hold exclusive access.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public void DemoteFromExclusiveAccess(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        var ld = new LockData(ref _data);

        while (true)
        {
            switch (ld.State)
            {
                // Switch back to idle
                case ExclusiveState:
                    var curThread = Environment.CurrentManagedThreadId;
                    if (ld.ThreadId != curThread)
                    {
                        Debug.Assert(false);
                    }

                    ld.ThreadId = 0;
                    ld.State = SharedState;
                    break;

                case SharedState:
                    break;

                case IdleState:
                    break;
            }

            if (!ld.TryUpdate())
            {
                continue;
            }

            // Record telemetry
            if (level >= TelemetryLevel.Deep)
            {
                target?.LogLockOperation(LockOperation.DemoteToShared, 0);
            }

            return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True if the current thread holds exclusive access.</summary>
    public bool IsLockedByCurrentThread
    {
        get
        {
            var threadId = (int)((_data & ThreadIdMask) >> ThreadIdShift);
            return threadId == Environment.CurrentManagedThreadId;
        }
    }

    internal int LockedByThreadId => (int)((_data & ThreadIdMask) >> ThreadIdShift);

    internal int SharedUsedCounter => (int)(_data & SharedCounterMask);

    /// <summary>
    /// Returns true if this lock has ever experienced contention (a thread had to wait).
    /// This flag is sticky - once set, it remains set until <see cref="Reset"/> is called.
    /// </summary>
    public bool WasContended => (_data & ContentionFlagMask) != 0;

    private string DebuggerDisplay
    {
        get
        {
            var shared = (_data & SharedState) != 0 ? "Shared Used Counter" : "Shared Waiters";
            var contention = WasContended ? ", CONTENDED" : "";
            return $"State: {GetStateName(_data)}, ThreadId: {LockedByThreadId}, {shared}: {SharedUsedCounter}, " +
                   $"Promoter Waiters: {(_data & PromoterWaitersMask) >> PromoterWaitersShift}, " +
                   $"Exclusive Waiters: {(_data & ExclusiveWaitersMask) >> ExclusiveWaitersShift}{contention}";
        }
    }

    private static string GetStateName(ulong state) =>
        (state & StateMask) switch
        {
            IdleState => "Idle",
            SharedState => "Shared",
            ExclusiveState => "Exclusive",
            _ => "Unknown"
        };

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct StateSnapshot(ulong data)
    {
        internal readonly ulong Data = data;
    }

    internal StateSnapshot SnapshotInternalState() => new(_data & ~ContentionFlagMask);

    internal bool CheckInternalState(in StateSnapshot snapshot) => (_data & ~ContentionFlagMask) == snapshot.Data;
}
