// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 4 bytes of data.
/// </summary>
/// <remarks>
/// <para>This is the compact version of <see cref="AccessControl"/> (4 bytes vs 8 bytes).
/// Use this when memory is constrained and you don't need waiter tracking.</para>
/// <para>For blocking operations, pass <c>ref WaitContext</c> to control timeout and cancellation.
/// Use <c>ref WaitContext.Null</c> for infinite wait with zero overhead.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct AccessControlSmall
{
    // ═══════════════════════════════════════════════════════════════════════
    // Bit Layout Constants
    // ═══════════════════════════════════════════════════════════════════════
    // Bit layout, from least to most significant:
    // 15 Shared counter        (bits 0-14, max 32,767)
    //  1 Contention flag       (bit 15)
    // 16 Thread Id             (bits 16-31)

    private const int ThreadIdShift = 16;
    private const int SharedUsedCounterMask = 0x0000_7FFF;  // Bits 0-14 (15 bits, max 32,767)
    private const int ContentionFlagMask    = 0x0000_8000;  // Bit 15

    /// <summary>Resets the lock to initial state.</summary>
    public void Reset() => _data = 0;

    private int _data;

    /// <summary>True if the current thread holds exclusive access.</summary>
    public bool IsLockedByCurrentThread => Environment.CurrentManagedThreadId == LockedByThreadId;

    /// <summary>Thread ID holding exclusive access, or 0 if not held.</summary>
    public int LockedByThreadId => _data >> ThreadIdShift;

    /// <summary>Current shared access count.</summary>
    public int SharedUsedCounter => _data & SharedUsedCounterMask;

    /// <summary>
    /// Returns true if this lock has ever experienced contention (a thread had to wait).
    /// This flag is sticky - once set, it remains set until <see cref="Reset"/> is called.
    /// </summary>
    public bool WasContended => (_data & ContentionFlagMask) != 0;

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

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidOperationException(string msg) => throw new InvalidOperationException(msg);

    // ═══════════════════════════════════════════════════════════════════════
    // AtomicChange Helper
    // ═══════════════════════════════════════════════════════════════════════

    private ref struct AtomicChange
    {
        /// <summary>
        /// Constructor for blocking operations that may need to wait (Enter, Promote).
        /// </summary>
        public AtomicChange(ref int source, ref WaitContext ctx)
        {
            _source = ref source;
            _spinWait = new SpinWait();
            _ctx = ref ctx;
            _isNullRef = Unsafe.IsNullRef(ref ctx);
            Fetch();
        }

        /// <summary>
        /// Constructor for non-blocking operations (Exit, ForceCommit) that don't need WaitContext.
        /// </summary>
        public AtomicChange(ref int source)
        {
            _source = ref source;
            _spinWait = new SpinWait();
            _ctx = ref Unsafe.NullRef<WaitContext>();
            _isNullRef = true;
            Fetch();
        }

        public int Initial;
        public int NewValue;

        private readonly ref int _source;
        private SpinWait _spinWait;
        private readonly ref WaitContext _ctx;
        private readonly bool _isNullRef;

        /// <summary>
        /// True if the wait should stop: deadline expired OR cancellation requested.
        /// Returns false (continue waiting) when NullRef is passed (infinite wait).
        /// </summary>
        public bool ShouldStop
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !_isNullRef && _ctx.ShouldStop;
        }

        public bool Commit() => Interlocked.CompareExchange(ref _source, NewValue, Initial) == Initial;

        public void ForceCommit(Func<int, int> valueToCommit)
        {
            while (true)
            {
                Fetch();
                NewValue = valueToCommit(Initial);
                if (Commit())
                {
                    return;
                }
            }
        }

        public void Fetch() => Initial = _source;

        public bool Wait()
        {
            if (ShouldStop)
            {
                return false;
            }

            _spinWait.SpinOnce();

            Fetch();
            return true;
        }

        public bool WaitFor(Func<int, bool> predicate)
        {
            Fetch();
            while (true)
            {
                if (predicate(Initial))
                {
                    return true;
                }

                if (!Wait())
                {
                    return false;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared Access
    // ═══════════════════════════════════════════════════════════════════════

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

        var ac = new AtomicChange(ref _data, ref ctx);

        while (true)
        {
            // Wait for no exclusive holder (ThreadId == 0)
            if ((ac.Initial >> ThreadIdShift) != 0)
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.SharedWaitStart, 0);
                    }

                    // Set contention flag (sticky, atomic) - we had to wait
                    Interlocked.Or(ref _data, ContentionFlagMask);
                }

                if (!ac.WaitFor(d => (d >> ThreadIdShift) == 0))
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(hadToWait ? LockOperation.TimedOut : LockOperation.Canceled,
                            hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }
                    return false;
                }
            }

            if ((ac.Initial & SharedUsedCounterMask) >= SharedUsedCounterMask)
            {
                ThrowInvalidOperationException("Too many concurrent shared accesses");
            }

            ac.NewValue = ac.Initial + 1;
            if (ac.Commit())
            {
                // Success - record telemetry
                if (hadToWait && level >= TelemetryLevel.Light)
                {
                    target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.SharedAcquired,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return true;
            }

            // CAS failed, re-fetch and retry
            ac.Fetch();
        }
    }

    /// <summary>
    /// Exits shared (reader) access.
    /// </summary>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public void ExitSharedAccess(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;

        var ac = new AtomicChange(ref _data);
        ac.ForceCommit(d =>
        {
            var counter = d & SharedUsedCounterMask;
            if (counter == 0)
            {
                ThrowInvalidOperationException("Exiting shared access without entering it first");
            }
            return d - 1;
        });

        // Record telemetry
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.SharedReleased, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Exclusive Access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enters exclusive (writer) access. Only one thread can hold exclusive access.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if access was acquired; false if timed out or cancelled.</returns>
    public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;

        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data, ref ctx);

        if ((ac.Initial & ~SharedUsedCounterMask) == ct)
        {
            ThrowInvalidOperationException("Cannot enter exclusive access while already holding it");
        }

        // Fast path - try immediate acquisition (idle state is 0 or just contention flag)
        var initialMasked = ac.Initial & ~ContentionFlagMask;
        if (initialMasked == 0)
        {
            ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
            if (ac.Commit())
            {
                // Success without waiting - record telemetry
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.ExclusiveAcquired, 0);
                }
                return true;
            }
        }

        // Slow path - need to wait (hadToWait is always true from here on)
        var waitStartTicks = Stopwatch.GetTimestamp();
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.ExclusiveWaitStart, 0);
        }

        // Set contention flag (sticky, atomic) - we had to wait
        Interlocked.Or(ref _data, ContentionFlagMask);

        while (true)
        {
            // Wait for idle state (ignoring contention flag)
            if (!ac.WaitFor(d => (d & ~ContentionFlagMask) == 0))
            {
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                }
                return false;
            }

            ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
            if (ac.Commit())
            {
                // Success - record telemetry
                if (level >= TelemetryLevel.Light)
                {
                    target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.ExclusiveAcquired, ComputeElapsedUs(waitStartTicks));
                }
                return true;
            }
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

        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data);

        // Check for idle state (ignoring contention flag)
        if ((ac.Initial & ~ContentionFlagMask) != 0)
        {
            return false;
        }

        ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
        if (!ac.Commit())
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

        var ac = new AtomicChange(ref _data);
        var expectedThread = Environment.CurrentManagedThreadId << ThreadIdShift;
        ac.ForceCommit(d =>
        {
            if ((d & ~SharedUsedCounterMask & ~ContentionFlagMask) != expectedThread)
            {
                ThrowInvalidOperationException("ExitExclusiveAccess called by a thread that doesn't own the lock");
            }
            return d & ContentionFlagMask;  // Preserve only contention flag
        });

        // Record telemetry
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.ExclusiveReleased, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Promotion
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tries to promote from shared to exclusive access.
    /// Caller must already hold shared access.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if promotion succeeded; false if timed out, cancelled, or other shared holders exist.</returns>
    public bool TryPromoteToExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        long waitStartTicks = 0;
        bool hadToWait = false;

        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data, ref ctx);

        while (true)
        {
            var counter = ac.Initial & SharedUsedCounterMask;

            // Must be in shared mode to promote (counter > 0)
            if (counter == 0)
            {
                ThrowInvalidOperationException("Cannot promote to exclusive without holding shared access");
            }

            // We can only promote if we are the only user (counter == 1)
            if (counter != 1)
            {
                // Other shared holders exist - cannot promote
                if (hadToWait && level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                }
                return false;
            }

            ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
            if (ac.Commit())
            {
                // Success - record telemetry
                if (hadToWait && level >= TelemetryLevel.Light)
                {
                    target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.PromoteToExclusiveAcquired,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return true;
            }

            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.PromoteToExclusiveStart, 0);
                }

                // Set contention flag (sticky, atomic) - we had to wait
                Interlocked.Or(ref _data, ContentionFlagMask);
            }

            if (!ac.Wait())
            {
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.TimedOut, ComputeElapsedUs(waitStartTicks));
                }
                return false;
            }
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

        var ac = new AtomicChange(ref _data);
        var expectedThread = Environment.CurrentManagedThreadId << ThreadIdShift;

        ac.ForceCommit(d =>
        {
            if ((d & ~SharedUsedCounterMask) != expectedThread)
            {
                ThrowInvalidOperationException("DemoteFromExclusiveAccess called by a thread that doesn't own the lock");
            }
            // Clear thread ID and set shared counter to 1
            return 1;
        });

        // Record telemetry
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.DemoteToShared, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Convenience Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enters either shared or exclusive access based on the parameter.
    /// </summary>
    /// <param name="exclusive">True for exclusive, false for shared.</param>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    /// <returns>True if access was acquired; false if timed out or cancelled.</returns>
    public bool Enter(bool exclusive, ref WaitContext ctx, IContentionTarget target = null)
        => exclusive ? EnterExclusiveAccess(ref ctx, target) : EnterSharedAccess(ref ctx, target);

    /// <summary>
    /// Exits either shared or exclusive access based on the parameter.
    /// </summary>
    /// <param name="exclusive">True for exclusive, false for shared.</param>
    /// <param name="target">Optional telemetry target for contention tracking.</param>
    public void Exit(bool exclusive, IContentionTarget target = null)
    {
        if (exclusive)
        {
            ExitExclusiveAccess(target);
        }
        else
        {
            ExitSharedAccess(target);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct StateSnapshot(int data)
    {
        internal readonly int Data = data;
    }

    internal StateSnapshot SnapshotInternalState() => new(_data & ~ContentionFlagMask);

    internal bool CheckInternalState(in StateSnapshot snapshot) => (_data & ~ContentionFlagMask) == snapshot.Data;
}
