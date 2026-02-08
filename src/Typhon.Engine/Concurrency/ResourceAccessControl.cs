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
/// A 32-bit synchronization primitive for resource lifecycle management with 3 modes:
/// ACCESSING (multiple concurrent), MODIFY (single holder, compatible with ACCESSING), and DESTROY (terminal, exclusive).
/// </summary>
/// <remarks>
/// <para><b>Key difference from RW locks</b>: MODIFY is compatible with ACCESSING. Modifiers can execute while accessors are active
/// (for append-only/extend-only operations). Only DESTROY is truly exclusive.</para>
///
/// <para><b>Bit layout (32 bits)</b>:</para>
/// <list type="bullet">
/// <item>Bits 0-7: ACCESSING count (0-255)</item>
/// <item>Bits 8-23: MODIFY holder ThreadId (16 bits, 0 = not held)</item>
/// <item>Bit 24: MODIFY_PENDING flag (fairness)</item>
/// <item>Bit 25: DESTROY flag (terminal, never cleared)</item>
/// <item>Bit 26: CONTENTION flag (sticky, cleared by Reset)</item>
/// <item>Bits 27-31: Reserved (5 bits)</item>
/// </list>
///
/// <para><b>Compatibility matrix</b>:</para>
/// <code>
///              ACCESSING   MODIFY   DESTROY
/// ACCESSING       ✓          ✓         ✗
/// MODIFY          ✓          ✗         ✗
/// DESTROY         ✗          ✗         ✗
/// </code>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public struct ResourceAccessControl
{
    // ═══════════════════════════════════════════════════════════════════════
    // Bit Layout Constants
    // ═══════════════════════════════════════════════════════════════════════

    private const int AccessingCountMask  = 0x0000_00FF;  // Bits 0-7
    private const int ThreadIdMask        = 0x00FF_FF00;  // Bits 8-23
    private const int ModifyPendingFlag   = 0x0100_0000;  // Bit 24
    private const int DestroyFlag         = 0x0200_0000;  // Bit 25
    private const int ContentionFlag      = 0x0400_0000;  // Bit 26

    private const int ThreadIdShift       = 8;
    private const int MaxAccessingCount   = 255;
    private const int ThreadIdBitsMask    = 0xFFFF;       // 16 bits for thread ID

    // ═══════════════════════════════════════════════════════════════════════
    // State Field
    // ═══════════════════════════════════════════════════════════════════════

    private int _state;

    // ═══════════════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetAccessingCount(int state) => state & AccessingCountMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetThreadId(int state) => (state & ThreadIdMask) >> ThreadIdShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsModifyHeld(int state) => GetThreadId(state) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasModifyPending(int state) => (state & ModifyPendingFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasDestroyFlag(int state) => (state & DestroyFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasPendingOrDestroy(int state) => (state & (ModifyPendingFlag | DestroyFlag)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetCurrentThreadIdBits() => Environment.CurrentManagedThreadId & ThreadIdBitsMask;

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
    private static void ThrowInvalidOperation(string message) => throw new InvalidOperationException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTimeout() => ThrowHelper.ThrowLockTimeout("ResourceAccessControl", TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════════════════════
    // ACCESSING Mode - Multiple concurrent, prevents destruction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to enter ACCESSING mode without blocking.
    /// </summary>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if MODIFY_PENDING or DESTROY is set or max count reached.</returns>
    public bool TryEnterAccessing(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;

        int state = _state;

        // Check if blocked by pending/destroy
        if (HasPendingOrDestroy(state))
        {
            return false;
        }

        // Check overflow
        int count = GetAccessingCount(state);
        if (count >= MaxAccessingCount)
        {
            ThrowInvalidOperation("Max ACCESSING count (1023) exceeded.");
        }

        int newState = state + 1; // Increment ACCESSING count

        if (Interlocked.CompareExchange(ref _state, newState, state) != state)
        {
            return false;
        }

        // Record telemetry on success
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.AccessingAcquired, 0);
        }

        return true;
    }

    /// <summary>
    /// Enters ACCESSING mode, spinning if necessary.
    /// Spins while MODIFY_PENDING or DESTROY is set.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if deadline expired or cancellation requested.</returns>
    public bool EnterAccessing(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        long waitStartTicks = 0;
        bool hadToWait = false;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(hadToWait ? LockOperation.TimedOut : LockOperation.Canceled,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return false;
            }

            int state = _state;

            if (HasPendingOrDestroy(state))
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.AccessingWaitStart, 0);
                    }

                    // Set contention flag (sticky, atomic) - we had to wait
                    Interlocked.Or(ref _state, ContentionFlag);
                }
                spin.SpinOnce();
                continue;
            }

            int count = GetAccessingCount(state);
            if (count >= MaxAccessingCount)
            {
                ThrowInvalidOperation("Max ACCESSING count (1023) exceeded.");
            }

            int newState = state + 1;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                // Success - record telemetry
                if (hadToWait && level >= TelemetryLevel.Light)
                {
                    target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.AccessingAcquired,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return true;
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Exits ACCESSING mode. Must be called once per successful enter.
    /// </summary>
    /// <param name="target">Optional telemetry target (should match Enter call).</param>
    public void ExitAccessing(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        SpinWait spin = default;

        while (true)
        {
            int state = _state;

            if (GetAccessingCount(state) == 0)
            {
                ThrowInvalidOperation("ExitAccessing called without matching EnterAccessing.");
            }

            int newState = state - 1; // Decrement ACCESSING count

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                // Record telemetry
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.AccessingReleased, 0);
                }
                return;
            }

            spin.SpinOnce();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODIFY Mode - Single holder, compatible with ACCESSING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to enter MODIFY mode without blocking.
    /// </summary>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired immediately, false if ACCESSING holders exist,
    /// another MODIFY is held, or DESTROY is set.</returns>
    public bool TryEnterModify(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;

        int state = _state;

        // Cannot acquire if destroyed
        if (HasDestroyFlag(state))
        {
            return false;
        }

        // Cannot acquire if another thread holds MODIFY
        if (IsModifyHeld(state))
        {
            return false;
        }

        // Cannot acquire if there are ACCESSING holders
        if (GetAccessingCount(state) > 0)
        {
            return false;
        }

        int threadId = GetCurrentThreadIdBits();
        int newState = (state & ~ThreadIdMask) | (threadId << ThreadIdShift);

        if (Interlocked.CompareExchange(ref _state, newState, state) != state)
        {
            return false;
        }

        // Record telemetry on success
        if (level >= TelemetryLevel.Deep)
        {
            target?.LogLockOperation(LockOperation.ModifyAcquired, 0);
        }

        return true;
    }

    /// <summary>
    /// Enters MODIFY mode.
    /// Sets MODIFY_PENDING and spins until ACCESSING count reaches zero.
    /// Spins while DESTROY is set or another MODIFY is held.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if deadline expired or cancellation requested.</returns>
    public bool EnterModify(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        int threadId = GetCurrentThreadIdBits();
        long waitStartTicks = 0;
        bool hadToWait = false;
        bool weSetPending = false;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                // If we set MODIFY_PENDING, try to clear it before returning
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(hadToWait ? LockOperation.TimedOut : LockOperation.Canceled,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return false;
            }

            int state = _state;

            // Cannot proceed if DESTROY is set
            if (HasDestroyFlag(state))
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.ModifyWaitStart, 0);
                    }

                    // Set contention flag (sticky, atomic) - we had to wait
                    Interlocked.Or(ref _state, ContentionFlag);
                }
                spin.SpinOnce();
                continue;
            }

            // Cannot proceed if another MODIFY is held
            if (IsModifyHeld(state))
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.ModifyWaitStart, 0);
                    }

                    // Set contention flag (sticky, atomic) - we had to wait
                    Interlocked.Or(ref _state, ContentionFlag);
                }
                spin.SpinOnce();
                continue;
            }

            // If ACCESSING count is zero, try to acquire directly
            if (GetAccessingCount(state) == 0)
            {
                // Clear MODIFY_PENDING (if set) and set ThreadId
                int newState = (state & ~(ModifyPendingFlag | ThreadIdMask)) | (threadId << ThreadIdShift);

                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    // Success - record telemetry
                    if (hadToWait && level >= TelemetryLevel.Light)
                    {
                        target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                    }
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.ModifyAcquired,
                            hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }
                    return true;
                }

                spin.SpinOnce();
                continue;
            }

            // ACCESSING holders exist - set MODIFY_PENDING to block new ones
            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.ModifyWaitStart, 0);
                }

                // Set contention flag (sticky, atomic) - we had to wait
                Interlocked.Or(ref _state, ContentionFlag);
            }

            if (!HasModifyPending(state))
            {
                int newState = state | ModifyPendingFlag;
                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    weSetPending = true;
                }
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Exits MODIFY mode.
    /// </summary>
    /// <param name="target">Optional telemetry target (should match Enter call).</param>
    public void ExitModify(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        SpinWait spin = default;
        int expectedThreadId = GetCurrentThreadIdBits();

        while (true)
        {
            int state = _state;

            if (GetThreadId(state) != expectedThreadId)
            {
                ThrowInvalidOperation("ExitModify called by thread that doesn't hold MODIFY.");
            }

            int newState = state & ~ThreadIdMask; // Clear ThreadId

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                // Record telemetry
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.ModifyReleased, 0);
                }
                return;
            }

            spin.SpinOnce();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Promotion/Demotion - ACCESSING ↔ MODIFY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to promote from ACCESSING to MODIFY.
    /// Caller must hold ACCESSING. On success, caller holds MODIFY instead.
    /// Sets MODIFY_PENDING to block new ACCESSING, waits for count to drain to 1.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if promoted, false if deadline expired, cancellation requested, or DESTROY is set.</returns>
    public bool TryPromoteToModify(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        int threadId = GetCurrentThreadIdBits();
        long waitStartTicks = 0;
        bool hadToWait = false;
        bool weSetPending = false;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                // If we set MODIFY_PENDING, try to clear it before returning
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(hadToWait ? LockOperation.TimedOut : LockOperation.Canceled,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return false;
            }

            int state = _state;
            int count = GetAccessingCount(state);

            // Must hold ACCESSING to promote
            if (count == 0)
            {
                ThrowInvalidOperation("TryPromoteToModify called without holding ACCESSING.");
            }

            // Cannot promote if DESTROY is set
            if (HasDestroyFlag(state))
            {
                // Clear MODIFY_PENDING if we set it
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                return false;
            }

            // Cannot promote if another MODIFY is held
            if (IsModifyHeld(state))
            {
                // Clear MODIFY_PENDING if we set it
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                return false;
            }

            // If we're the only ACCESSING holder, promote
            if (count == 1)
            {
                // Atomic: ACCESSING -= 1, ThreadId = current, clear MODIFY_PENDING
                int newState = (state - 1) & ~(ModifyPendingFlag | ThreadIdMask) | (threadId << ThreadIdShift);     // Decrement ACCESSING

                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    // Success - record telemetry
                    if (hadToWait && level >= TelemetryLevel.Light)
                    {
                        target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                    }
                    if (level >= TelemetryLevel.Deep)
                    {
                        target?.LogLockOperation(LockOperation.PromoteToModifyAcquired,
                            hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                    }
                    return true;
                }

                spin.SpinOnce();
                continue;
            }

            // Other ACCESSING holders exist - set MODIFY_PENDING and wait
            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.PromoteToModifyStart, 0);
                }

                // Set contention flag (sticky, atomic) - we had to wait
                Interlocked.Or(ref _state, ContentionFlag);
            }

            if (!HasModifyPending(state))
            {
                int newState = state | ModifyPendingFlag;
                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    weSetPending = true;
                }
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Attempts to clear the MODIFY_PENDING flag (best-effort).
    /// Used when a promotion or modify fails to avoid deadlock.
    /// </summary>
    private void TryClearModifyPending()
    {
        SpinWait spin = default;
        for (int i = 0; i < 10; i++) // Limited retries
        {
            int state = _state;
            if (!HasModifyPending(state))
            {
                return; // Already cleared
            }

            int newState = state & ~ModifyPendingFlag;
            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                return;
            }
            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Demotes from MODIFY back to ACCESSING.
    /// Caller must hold MODIFY. On return, caller holds ACCESSING instead.
    /// </summary>
    /// <param name="target">Optional telemetry target (should match Enter call).</param>
    public void DemoteFromModify(IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        SpinWait spin = default;
        int expectedThreadId = GetCurrentThreadIdBits();

        while (true)
        {
            int state = _state;

            if (GetThreadId(state) != expectedThreadId)
            {
                ThrowInvalidOperation("DemoteFromModify called by thread that doesn't hold MODIFY.");
            }

            // Atomic: clear ThreadId, increment ACCESSING
            int newState = (state & ~ThreadIdMask) + 1;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                // Record telemetry
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.DemoteToAccessing, 0);
                }
                return;
            }

            spin.SpinOnce();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DESTROY Mode - Exclusive, terminal
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enters DESTROY mode.
    /// Sets DESTROY flag and spins until ACCESSING=0 and MODIFY not held.
    /// This is a terminal operation - the primitive cannot be reused after success.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target. Receives callbacks on contention.</param>
    /// <returns>True if acquired, false if deadline expired or cancellation requested.</returns>
    /// <remarks>
    /// <para><b>Warning</b>: If cancelled after setting DESTROY, the flag remains set and the resource
    /// is effectively dead. This is documented behavior - destruction was requested but couldn't complete.</para>
    /// </remarks>
    public bool EnterDestroy(ref WaitContext ctx, IContentionTarget target = null)
    {
        var level = target?.TelemetryLevel ?? TelemetryLevel.None;
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        long waitStartTicks = 0;
        bool hadToWait = false;
        bool destroyFlagSet = false;

        // Phase 1: Set DESTROY flag
        while (!destroyFlagSet)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(hadToWait ? LockOperation.TimedOut : LockOperation.Canceled,
                        hadToWait ? ComputeElapsedUs(waitStartTicks) : 0);
                }
                return false;
            }

            int state = _state;

            if (HasDestroyFlag(state))
            {
                destroyFlagSet = true;
                break; // Already set (shouldn't happen in normal use but handle gracefully)
            }

            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.DestroyWaitStart, 0);
                }

                // Set contention flag (sticky, atomic) - we had to wait
                Interlocked.Or(ref _state, ContentionFlag);
            }

            int newState = state | DestroyFlag;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                destroyFlagSet = true;
            }
            else
            {
                spin.SpinOnce();
            }
        }

        // Phase 2: Wait for ACCESSING=0 and MODIFY not held
        spin = default;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                // Note: DESTROY flag remains set - primitive is now in a broken state
                // This is acceptable as destruction was requested but couldn't complete
                if (level >= TelemetryLevel.Deep)
                {
                    // Distinguish timeout vs cancellation for accurate telemetry
                    var op = ctx.Token.IsCancellationRequested ? LockOperation.Canceled : LockOperation.TimedOut;
                    target?.LogLockOperation(op, ComputeElapsedUs(waitStartTicks));
                }
                return false;
            }

            int state = _state;

            if (GetAccessingCount(state) == 0 && !IsModifyHeld(state))
            {
                // Success - DESTROY complete, primitive is terminal
                if (hadToWait && level >= TelemetryLevel.Light)
                {
                    target?.RecordContention(ComputeElapsedUs(waitStartTicks));
                }
                if (level >= TelemetryLevel.Deep)
                {
                    target?.LogLockOperation(LockOperation.DestroyAcquired, ComputeElapsedUs(waitStartTicks));
                }
                return true;
            }

            spin.SpinOnce();
        }
    }

    // No ExitDestroy - destruction is final

    // ═══════════════════════════════════════════════════════════════════════
    // Scoped Guards
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enters ACCESSING and returns a disposable guard that exits on dispose.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target.</param>
    /// <returns>A guard that calls ExitAccessing on dispose.</returns>
    /// <exception cref="TimeoutException">If deadline expires before acquisition.</exception>
    public unsafe AccessingGuard EnterAccessingScoped(ref WaitContext ctx, IContentionTarget target = null)
    {
        if (!EnterAccessing(ref ctx, target))
        {
            ThrowTimeout();
        }
        fixed (int* ptr = &_state)
        {
            return new AccessingGuard(ptr, target);
        }
    }

    /// <summary>
    /// Enters MODIFY and returns a disposable guard that exits on dispose.
    /// </summary>
    /// <param name="ctx">Wait context (deadline + cancellation). Pass <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <param name="target">Optional telemetry target.</param>
    /// <returns>A guard that calls ExitModify on dispose.</returns>
    /// <exception cref="TimeoutException">If deadline expires before acquisition.</exception>
    public unsafe ModifyGuard EnterModifyScoped(ref WaitContext ctx, IContentionTarget target = null)
    {
        if (!EnterModify(ref ctx, target))
        {
            ThrowTimeout();
        }
        fixed (int* ptr = &_state)
        {
            return new ModifyGuard(ptr, target);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resets the primitive to initial state.
    /// WARNING: Only call when no threads are using this instance.
    /// </summary>
    public void Reset() => _state = 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostic Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True if MODIFY is held by the current thread.</summary>
    public bool IsModifyHeldByCurrentThread
    {
        get
        {
            int threadId = GetThreadId(_state);
            return threadId != 0 && threadId == GetCurrentThreadIdBits();
        }
    }

    /// <summary>Thread ID holding MODIFY (truncated to 10 bits), or 0 if not held.</summary>
    public int ModifyHolderThreadId => GetThreadId(_state);

    /// <summary>Current ACCESSING count.</summary>
    public int AccessingCount => GetAccessingCount(_state);

    /// <summary>True if MODIFY_PENDING is set (a thread is waiting for MODIFY).</summary>
    public bool IsModifyPending => HasModifyPending(_state);

    /// <summary>True if DESTROY has been acquired (terminal state).</summary>
    public bool IsDestroyed => HasDestroyFlag(_state);

    /// <summary>
    /// Returns true if this lock has ever experienced contention (a thread had to wait).
    /// This flag is sticky - once set, it remains set until <see cref="Reset"/> is called.
    /// </summary>
    public bool WasContended => (_state & ContentionFlag) != 0;

    /// <summary>Gets a complete diagnostic state snapshot.</summary>
    public ResourceAccessControlState GetDiagnosticState()
    {
        int state = _state;
        return new ResourceAccessControlState
        {
            AccessingCount = GetAccessingCount(state),
            ModifyHolderThreadId = GetThreadId(state),
            ModifyPending = HasModifyPending(state),
            Destroyed = HasDestroyFlag(state),
            RawState = state
        };
    }

    private string DebuggerDisplay
    {
        get
        {
            var state = GetDiagnosticState();
            var contention = WasContended ? ", CONTENDED" : "";
            return $"Accessing={state.AccessingCount}, ModifyHolder={state.ModifyHolderThreadId}, " +
                   $"Pending={state.ModifyPending}, Destroyed={state.Destroyed}{contention}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct StateSnapshot(int state)
    {
        internal readonly int State = state;
    }

    internal StateSnapshot SnapshotInternalState() => new(_state & ~ContentionFlag);

    internal bool CheckInternalState(in StateSnapshot snapshot) => (_state & ~ContentionFlag) == snapshot.State;

    // ═══════════════════════════════════════════════════════════════════════
    // Scoped Guard Structs
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A disposable guard that holds ACCESSING mode and releases it on dispose.
    /// </summary>
    [PublicAPI]
    public readonly unsafe ref struct AccessingGuard
    {
        private readonly int* _statePtr;
        private readonly IContentionTarget _target;

        internal AccessingGuard(int* state, IContentionTarget target)
        {
            _statePtr = state;
            _target = target;
        }

        /// <summary>Releases the ACCESSING lock.</summary>
        public void Dispose()
        {
            if (_statePtr == null)
            {
                return;
            }

            var level = _target?.TelemetryLevel ?? TelemetryLevel.None;
            SpinWait spin = default;

            while (true)
            {
                int state = *_statePtr;

                if (GetAccessingCount(state) == 0)
                {
                    ThrowInvalidOperation("AccessingGuard.Dispose called without matching EnterAccessing.");
                }

                int newState = state - 1;

                if (Interlocked.CompareExchange(ref *_statePtr, newState, state) == state)
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        _target?.LogLockOperation(LockOperation.AccessingReleased, 0);
                    }
                    return;
                }

                spin.SpinOnce();
            }
        }
    }

    /// <summary>
    /// A disposable guard that holds MODIFY mode and releases it on dispose.
    /// </summary>
    [PublicAPI]
    public readonly unsafe ref struct ModifyGuard
    {
        private readonly int* _statePtr;
        private readonly IContentionTarget _target;

        internal ModifyGuard(int* state, IContentionTarget target)
        {
            _statePtr = state;
            _target = target;
        }

        /// <summary>Releases the MODIFY lock.</summary>
        public void Dispose()
        {
            if (_statePtr == null)
            {
                return;
            }

            var level = _target?.TelemetryLevel ?? TelemetryLevel.None;
            SpinWait spin = default;
            int expectedThreadId = GetCurrentThreadIdBits();

            while (true)
            {
                int state = *_statePtr;

                if (GetThreadId(state) != expectedThreadId)
                {
                    ThrowInvalidOperation("ModifyGuard.Dispose called by thread that doesn't hold MODIFY.");
                }

                int newState = state & ~ThreadIdMask;

                if (Interlocked.CompareExchange(ref *_statePtr, newState, state) == state)
                {
                    if (level >= TelemetryLevel.Deep)
                    {
                        _target?.LogLockOperation(LockOperation.ModifyReleased, 0);
                    }
                    return;
                }

                spin.SpinOnce();
            }
        }
    }
}

/// <summary>
/// Diagnostic snapshot of a <see cref="ResourceAccessControl"/>'s state.
/// </summary>
[PublicAPI]
public readonly struct ResourceAccessControlState
{
    /// <summary>Current ACCESSING count.</summary>
    public int AccessingCount { get; init; }

    /// <summary>Thread ID holding MODIFY (truncated to 10 bits), or 0 if not held.</summary>
    public int ModifyHolderThreadId { get; init; }

    /// <summary>True if MODIFY_PENDING is set.</summary>
    public bool ModifyPending { get; init; }

    /// <summary>True if DESTROY has been acquired.</summary>
    public bool Destroyed { get; init; }

    /// <summary>Raw 32-bit state value.</summary>
    public int RawState { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"Accessing={AccessingCount}, ModifyHolder={ModifyHolderThreadId}, " +
        $"ModifyPending={ModifyPending}, Destroyed={Destroyed}";
}
