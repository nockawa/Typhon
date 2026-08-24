using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-worker-slot hot state for <see cref="Typhon.Engine.EventQueue{T}"/> (#861) — the three scalars a producer touches on every
/// <c>Push</c>, clustered onto one cache line so concurrent workers never share one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why AoS and why non-generic.</b> Rule MD-03 requires each independently-mutated element of a concurrently-mutated structure to sit on its own
/// ≥64-byte line, and prefers clustering an element's fields into one padded struct over parallel padded arrays — same memory, fewer line fetches.
/// The natural expression would carry the segment's <c>T[]</c> buffer here too, but the CLR rejects that outright: a generic type cannot have explicit
/// layout (<c>TypeLoadException: generic types cannot have explicit layout</c>, verified). So the buffers live in a separate <c>T[][]</c> whose slots
/// are written once per bind or grow, mirroring <c>PointInTimeAccessor._workerAccessors</c> — an unpadded reference array is fine there because the
/// slot is not the hot field. The three fields that ARE hot are here, together, on one line.
/// </para>
/// <para>
/// <b>No atomics.</b> Every field is written only by the single worker that owns the slot, and read by the consumer only after the producing system's
/// DAG completion barrier — a full fence. That is the same ordering guarantee the single-producer implementation relied on (rules ED-03 / ED-05c); the
/// segmentation narrows it from "one producer per queue" to "one producer per slot", which the scheduler enforces structurally.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct EventQueueSegmentState
{
    /// <summary>Live entries in this slot's buffer. Zeroed by <c>Drain</c> and by <c>Reset</c>.</summary>
    [FieldOffset(0)]
    public int Count;

    /// <summary>Pushes dropped in this slot because the segment was at its growth ceiling. Cleared by <c>Reset</c>.</summary>
    [FieldOffset(4)]
    public uint Overflow;

    /// <summary>High-water <see cref="Count"/> observed in this slot before a <c>Drain</c> reset it. Cleared by <c>Reset</c>.</summary>
    [FieldOffset(8)]
    public uint PeakBeforeDrain;

    // No Produced field: EventQueue<T>.Produced is derived as Count + Consumed, which is exact and saves a store per push.
}
