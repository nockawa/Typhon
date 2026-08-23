using JetBrains.Annotations;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// A producer's handle on one worker slot of an <see cref="EventQueue{T}"/> (#861). Resolve it once per system body, then push through it.
/// </summary>
/// <remarks>
/// <para>
/// The point of the type is that the slot lookup happens ONCE. After that, <see cref="Push"/> is a bounds check, an array store and two increments
/// against fields this thread exclusively owns — no <c>Interlocked</c>, no shared cache line, no indirection back through the queue. That is what keeps
/// a multi-producer queue at single-producer cost.
/// </para>
/// <para>
/// <b>Stack-only and single-threaded by construction.</b> A <c>ref struct</c> cannot be captured into a lambda, boxed, or stored on the heap, so a
/// writer cannot outlive its system body or be smuggled onto another thread — the compiler enforces the invariant the design depends on.
/// </para>
/// <para>
/// Obtain one with <c>ctx.Writer(queue)</c>, which supplies the caller's <see cref="TickContext.WorkerId"/>.
/// </para>
/// </remarks>
/// <typeparam name="T">The event type.</typeparam>
[PublicAPI]
public ref struct EventWriter<T>
{
    private readonly EventQueue<T> _queue;
    private readonly int _slot;

    // The slot's state, resolved once — its address is stable for the queue's lifetime.
    private readonly ref EventQueueSegmentState _state;

    // The BUFFER, by contrast, must be re-read every push. Caching it by value was a silent-corruption bug: two live writers on one slot (a second
    // ctx.Writer, a copy passed to a helper) leave one holding an orphaned array after the other grows the segment. The bounds check self-heals that
    // while Count stays above the stale length, but a Drain resets Count to 0 and the stale writer's fast path then succeeds into the orphan — the
    // event is lost and an already-consumed one is delivered in its place, with Count and Produced both agreeing. The outer array's identity is fixed
    // at bind, so caching THAT and indexing per push is both correct and nearly free.
    private readonly T[][] _buffers;

    internal EventWriter(EventQueue<T> queue, int slot)
    {
        _queue = queue;
        _slot = slot;
        _state = ref queue.SlotState(slot);
        _buffers = queue.SlotBuffers();
    }

    /// <summary>False for a <c>default</c> writer — one built from a null queue. Pushing through it is a no-op.</summary>
    public readonly bool IsValid => _queue != null;

    /// <summary>Events accepted into this slot so far this tick. Zero for an invalid writer.</summary>
    public readonly int Count => _queue == null ? 0 : _state.Count;

    /// <summary>
    /// Appends an event to this worker's segment.
    /// </summary>
    /// <param name="item">The event to enqueue.</param>
    /// <returns>
    /// <c>true</c> if the event was stored; <c>false</c> if it was dropped because the segment is at its growth ceiling. A dropped event is counted in
    /// <see cref="EventQueueBase.OverflowCount"/>. <c>Push</c> deliberately never throws: it runs inside parallel chunks, where an exception is
    /// converted into a system failure and, under a strict tick-abort policy (#567), can cancel the rest of the tick.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Push(T item)
    {
        // Buffers first: a `default` writer has a null ref field, so _state must not be touched before we know the writer is live.
        var buffers = _buffers;
        if (buffers != null)
        {
            var buffer = buffers[_slot];
            var n = _state.Count;
            if (buffer != null && (uint)n < (uint)buffer.Length)
            {
                buffer[n] = item;
                _state.Count = n + 1;
                if (n == 0)
                {
                    // Raise the queue's O(1) emptiness gate only on this segment's 0 -> 1 transition. Doing it every push cost a volatile read per
                    // push for no added information; a mid-tick Drain resets Count to 0, so a later push re-raises it.
                    _queue.MarkProduced();
                }

                return true;
            }
        }

        return PushGrow(item);
    }

    /// <summary>Lazy first allocation, doubling, or drop-at-ceiling. Split out so the fast path above stays inlineable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool PushGrow(T item)
    {
        if (_queue == null)
        {
            // A writer opened on a null queue. Discards, exactly as the `queue?.Push(...)` idiom it replaces did.
            return false;
        }

        // No re-caching needed: the fast path re-reads _buffers[_slot] every push, so a grow performed here is visible immediately.
        return _queue.PushSlow(_slot, item);
    }
}
