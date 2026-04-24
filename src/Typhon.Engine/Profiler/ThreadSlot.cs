using System.Runtime.InteropServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-owned bookkeeping for one <see cref="ThreadSlotRegistry"/> slot: the variable-size SPSC ring the owning thread writes to, plus the
/// per-thread Activity-context opt-out flag and monotonic span counter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership split:</b> this class holds <i>producer-only</i> fields. The consumer-written lifecycle state (<see cref="SlotState"/>) lives on
/// the <see cref="PaddedSlot"/> struct in the registry's backing array, on a separate cache line. Keeping them on distinct cache lines means the
/// consumer's state CAS on every drain tick does not invalidate the producer's cached <see cref="Buffer"/> pointer.
/// </para>
/// <para>
/// <b>Lazy buffer allocation:</b> <see cref="Buffer"/> is <c>null</c> until <see cref="ThreadSlotRegistry.ClaimSlot"/> assigns a real
/// <see cref="TraceRecordRing"/>. Only the ~30 typical active slots consume event-buffer memory; the other 226 slots cost nothing at steady state.
/// </para>
/// </remarks>
internal sealed class ThreadSlot
{
    /// <summary>
    /// Variable-size SPSC ring buffer owned by this slot. <c>null</c> until the first claim assigns one; reused across re-claims of the same slot.
    /// </summary>
    public TraceRecordRing Buffer;

    /// <summary>
    /// Managed thread ID of the current owner, or 0 if the slot is free. Diagnostic only — never used as an index because the CLR recycles
    /// managed thread IDs.
    /// </summary>
    public int OwnerManagedThreadId;

    /// <summary>
    /// Hot-path flag: when <c>false</c>, <c>TyphonEvent</c> skips the <see cref="System.Diagnostics.Activity.Current"/> lookup entirely,
    /// saving ~5–9 ns per span. Cleared via <c>TyphonEvent.SuppressActivityContextOnThisThread</c> by scheduler workers and the profiler consumer
    /// thread at their startup entry.
    /// </summary>
    public bool CaptureActivityContext;

    /// <summary>
    /// Per-slot monotonic span counter. Producer-only-written via plain <c>++</c> (single writer ⇒ no <see cref="System.Threading.Interlocked"/>
    /// needed). NEVER reset on claim or reclaim — successive owners of the same slot keep the counter monotonic, so the <c>(slot, counter)</c>
    /// pair is unique for the lifetime of the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SpanId uniqueness:</b> <c>SpanId = ((ulong)slotIdx &lt;&lt; 56) | counter</c>. Slot index disambiguates concurrent owners of distinct
    /// slots; the never-reset counter disambiguates successive owners of the same slot. 56-bit counter space ≈ 2280 years at 1M spans/sec.
    /// </para>
    /// </remarks>
    public long SpanCounter;

    public ThreadSlot()
    {
        CaptureActivityContext = true;
    }
}

/// <summary>
/// Cache-line-padded entry in <c>ThreadSlotRegistry.s_slots</c>. Holds the consumer-written lifecycle <see cref="State"/> and the reference to
/// the <see cref="ThreadSlot"/> heap object. One struct per slot, exactly 64 bytes so adjacent slots do not false-share.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct PaddedSlot
{
    /// <summary>Lifecycle state — CAS target. Values correspond to <see cref="SlotState"/>.</summary>
    [FieldOffset(0)]
    public int State;

    /// <summary>Reference to the producer-owned bookkeeping. Allocated lazily by <c>ThreadSlotRegistry.ClaimSlot</c>.</summary>
    [FieldOffset(8)]
    public ThreadSlot Slot;
}
