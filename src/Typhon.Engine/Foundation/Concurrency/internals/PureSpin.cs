using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Exponential PAUSE-only backoff for a wait that is known to be short — one thread waiting on another to finish a nanosecond-scale job. Issues
/// <see cref="Thread.SpinWait(int)"/> and nothing else: never <c>Thread.Yield</c>, <c>Thread.Sleep(0)</c> or <c>Thread.Sleep(1)</c>.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="AdaptiveWaiter"/>, and the choice between them is a statement about what is being waited FOR, not about how long the
/// caller is willing to wait. <see cref="AdaptiveWaiter"/> delegates to <see cref="SpinWait.SpinOnce()"/> and rides the full ladder out to
/// <c>Sleep(1)</c> — correct when the holder may be doing something slow, such as I/O. This type is for the other case: the holder is a handful of
/// instructions from releasing, and handing the core to the scheduler to wait that out is never the right trade.
/// <para>
/// Unlike <see cref="AdaptiveWaiter"/> there is no spin-to-yield transition, so there is no <c>Concurrency:AdaptiveWaiter:YieldOrSleep</c> event to
/// emit — a PureSpin wait that runs long is invisible to that channel by construction. If you need to know a wait is running long, count it at the
/// call site.
/// </para>
/// <para>
/// <b>The motivating case: the B+Tree index, and the rule there.</b> An OLC wait is for another thread to publish a version, or to drop a latch it
/// holds across a handful of node writes. That is tens of nanoseconds, and handing the core to the OS scheduler to wait that out is never the right
/// trade, so the restart loops use this type and never sleep.
/// </para>
/// <para>
/// The exception is a wait that may be queued behind an <b>IOP</b>, which lives in a different dimension entirely — a page fault is microseconds to
/// milliseconds, and spinning through one burns a core for nothing. Those waits are allowed to yield. In this subsystem that means exactly the two
/// latch-acquisition helpers, <c>SpinWriteLock</c> and <c>SpinWriteLockOnSmoPath</c>: <c>PreDirtyForWrite</c> is page-cache admission and two sites call
/// it while already holding a latch — the B-link move-right loop, and Phase 3's spill siblings — so the holder those two wait on can be inside a fault.
/// Both still spin purely for 64 iterations first, which covers the ordinary OLC case; only past that do they yield. Neither enables <c>Sleep(1)</c>.
/// </para>
/// <para>
/// <b>Why <see cref="SpinWait"/> is not used for the OLC waits.</b> <c>SpinOnce()</c> escalates through <c>Yield</c> to <c>Sleep(0)</c> and
/// <c>Sleep(1)</c>; <c>SpinOnce(-1)</c> only disables the <c>Sleep(1)</c> tier and still yields and sleeps zero. The <c>Sleep(1)</c> tier costs a full
/// Windows timer tick, about 15 ms, and it was not hypothetical: the 4-core reproduction of #738 measured the pessimistic insert retry loop at <b>64
/// retries per second</b>. The loop was not computing, it was sleeping out timer ticks — which stretched the time to reach <c>MaxPessimisticRestarts</c>
/// from under a second to 156 s, long past the stress harness's 10 s deadline. The escalation did not merely slow the wait; it <b>hid the bound that was
/// supposed to make the failure loud</b>, which is why every #738 record ever captured says DEADLINE and none names the throw.
/// </para>
/// <para>
/// <b>The cost, stated plainly.</b> A PAUSE-only spinner never gives its core back voluntarily; on a box with more runnable threads than cores it spins
/// until preempted rather than handing the core to the holder. That is accepted here because an OLC wait that is not microseconds is a defect to be found,
/// not a cost to be slept through. The cap below bounds the burn per step; it is not an escape hatch.
/// </para>
/// </remarks>
internal struct PureSpin
{
    private int _count;

    /// <summary>Doubling ceiling. 2^10 PAUSEs is roughly a microsecond on current x64 — long enough to matter, short enough to stay responsive.</summary>
    private const int MaxShift = 10;

    /// <summary>One backoff step: PAUSE for 2^n iterations, n growing to <see cref="MaxShift"/> and then holding.</summary>
    /// <remarks>
    /// Exponential rather than flat so an uncontended-but-slow case does not pay a long spin on its first pass, and a genuinely contended one stops issuing
    /// back-to-back atomic probes at the cache line every other waiter is also probing.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Once()
    {
        int shift = _count < MaxShift ? _count : MaxShift;
        Thread.SpinWait(1 << shift);
        if (_count < MaxShift)
        {
            _count++;
        }
    }

    /// <summary>Number of backoff steps taken, for the diagnostic counters that report spin volume.</summary>
    public readonly int Steps => _count;
}
