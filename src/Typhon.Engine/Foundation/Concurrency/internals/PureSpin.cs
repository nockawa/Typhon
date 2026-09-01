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
/// milliseconds, and spinning through one burns a core for nothing. Those waits yield: the two latch-acquisition helpers, <c>SpinWriteLock</c> and
/// <c>SpinWriteLockOnSmoPath</c>. <c>PreDirtyForWrite</c> is page-cache admission and several sites call it while ALREADY holding a latch — the B-link
/// move-right loop, Phase 3's spill siblings, the remove path's neighbour acquisition — so the holder those two wait on can be inside a fault. Both still
/// spin purely for 64 iterations first, covering the ordinary OLC case; only past that do they yield, and neither enables <c>Sleep(1)</c>.
/// <para>
/// <b>Everything else uses this type, including the unbounded lookup re-descent loops, and that placement is empirical.</b> The tidier-sounding rule —
/// "every UNBOUNDED wait yields, because a pure spinner holding a core against the thread it waits for is a livelock risk" — was tried and measured worse:
/// moving <c>TryGetPessimistic</c>, <c>TryGetMultiplePessimistic</c> and the remove path's obsolete re-descent to the yielding tier took the 4-core stress
/// harness from 0 problem runs in 34 to 1 in 6. The argument is still reasonable; it is simply not what the machine does. Re-measure before acting on it.
/// </para>
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

    /// <summary>Doubling ceiling: 2^10 iterations, MEASURED at 38.2 us per saturated step. Empirical — every attempt to lower it regressed.</summary>
    /// <remarks>
    /// This originally read "2^10 PAUSEs is roughly a microsecond", which was a guess and wrong by a factor of ~38. `Thread.SpinWait(n)` does not issue n raw
    /// `pause` instructions: CoreCLR routes it through `YieldProcessorNormalized`, which issues however many raw PAUSEs the CPU needs to hit a fixed
    /// wall-clock target. Measured on a 7950X at 37.4 ns per iteration, dead flat from n=1 to n=4096 — and that flatness is the proof of normalisation, since
    /// a raw PAUSE loop on Zen 4 would measure ~13-16 ns and would differ on Intel. So 2^10 is 38.2 us per saturated step.
    /// <para>
    /// <b>By argument that is indefensible, and the argument is wrong.</b> The reasoning says the cap should be a small multiple of what is being waited for
    /// — OLC latch holds are documented at 100-500 ns, making 2^10 a 76x overshoot in a subsystem with microsecond latency targets. Both lower values that
    /// reasoning recommends were tried, on a 4-core reproduction of the CI runner, against `OlcBTreeRaceStressTests`:
    /// </para>
    /// <para>
    /// 2^10: <b>0 problem runs in 8</b>. 2^6: 2 in 6. 2^5: <b>5 in 8</b>. Every failure is the same signature — the REMOVE pessimistic loop burning its full
    /// 10,000-retry bound, never a deadline, never the insert path.
    /// </para>
    /// <para>
    /// The mechanism is the uncomfortable part, and it is worth stating rather than hiding behind the number. These retry loops are not waiting on a latch
    /// they can acquire; they are waiting for ANOTHER thread to finish a structural modification. A shorter backoff does not reduce the retries needed to
    /// converge — it spends the bounded budget in less wall-clock time, and on a box with more runnable threads than cores the thread being waited on may not
    /// be scheduled within it. 10,000 x 1.2 us is 12 ms and loses; 10,000 x 38 us is 380 ms and wins. The cap is therefore doing the job a yield would
    /// otherwise do, which is precisely what this type exists not to do. That tension is real and unresolved: the honest fix is forward progress in the
    /// remove path, not backoff tuning. Until then the number stays where the measurements put it.
    /// </para>
    /// <para>
    /// If you change it, re-run that harness pinned to 4 CPUs first. This constant has now defeated two correct-sounding arguments.
    /// </para>
    /// </remarks>
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
