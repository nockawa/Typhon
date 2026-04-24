using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Per-thread SPSC (single-producer, single-consumer) ring buffer holding <b>variable-size</b> trace records. Producer is the owning thread;
/// consumer is the profiler's dedicated drain thread. The successor to <see cref="ThreadTraceBuffer"/>, which was fixed-stride.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why variable-size:</b> the old fixed 64-byte <see cref="Typhon.Profiler.TraceEvent"/> wasted 27 B per <c>BTreeInsert</c> event on scheduler
/// fields that were always zero, and forced scheduler events to pay for a trace-context slot they never used. The new layout from
/// <see cref="TraceRecordHeader"/> lets each record carry exactly the payload it needs — span events drop to ~37 B without trace context, scheduler
/// chunks drop to ~47 B, and rich transaction commits grow slightly (~63 B) while carrying 4 more fields than the old struct could.
/// </para>
/// <para>
/// <b>Framing:</b> records are laid out contiguously in a power-of-2 byte buffer. Each record begins with the 12-byte
/// <see cref="TraceRecordHeader.CommonHeaderSize"/> common header whose first 2 bytes are the total record size (u16 little-endian). The consumer
/// walks <c>_tail</c> forward by reading that size field and jumping. A special sentinel value <see cref="TraceRecordHeader.WrapSentinel"/>
/// (<c>0xFFFF</c>) at the start of a slot means "this slot is padding to the end of the buffer — jump the read pointer to the next wrap boundary"
/// (used when a record wouldn't fit contiguously at the current head position).
/// </para>
/// <para>
/// <b>Synchronization:</b> two monotonic 64-bit counters (<c>_head</c> producer-written, <c>_tail</c> consumer-written), each wrapped in a
/// <see cref="CacheLinePaddedLong"/> so they live on distinct cache lines — avoids the producer/consumer false-sharing ping-pong that is textbook
/// for SPSC rings (Tracy pads them for the same reason). On x64 TSO, plain field reads and writes of 64-bit primitives are naturally atomic and
/// ordered, so no <see cref="System.Threading.Volatile"/> fences are needed — same as <see cref="ThreadTraceBuffer"/> does for the same reason
/// (see CLAUDE.md).
/// </para>
/// <para>
/// <b>Even-size invariant:</b> all reservations are rounded UP to an even byte count. Since <see cref="Capacity"/> is a power of 2, this guarantees
/// that every <c>_head &amp; _mask</c> byte offset is even, and the u16 size field at the start of every record lives on an even offset. The
/// consequence: we never end up in the pathological "one byte left before the wrap, can't write a 2-byte wrap sentinel" corner case. Padding bytes
/// at the tail of each record are left at whatever value they already held — the consumer reads <c>size</c> from the header and skips the exact
/// number of bytes, so padding content is irrelevant.
/// </para>
/// <para>
/// <b>Drop-newest on full:</b> when the ring is full, <see cref="TryReserve"/> returns <c>false</c> and increments a per-ring drop counter. The
/// producer hot path <b>never blocks</b> — losing a sample is always preferable to stalling the engine.
/// </para>
/// </remarks>
internal sealed class TraceRecordRing
{
    /// <summary>Minimum reservation size — the common header alone. Anything smaller is a programmer error.</summary>
    private const int MinRecordSize = TraceRecordHeader.CommonHeaderSize;

    /// <summary>Maximum reservation size — u16 size field's usable range minus the wrap sentinel value.</summary>
    private const int MaxRecordSize = TraceRecordHeader.WrapSentinel - 1;  // 0xFFFE

    private readonly byte[] _buffer;
    private readonly int _mask;

    // Producer-written, consumer-read. Each on its own cache line to avoid false sharing.
    private CacheLinePaddedLong _head;
    private CacheLinePaddedLong _tail;

    // Producer scratch between TryReserve and Publish. Not visible to consumer.
    private long _pendingHead;

    // Producer-owned; consumer reads for diagnostics only (plain read on x64 is safe).
    private long _droppedEvents;

    /// <summary>Total bytes the buffer can hold (a power of 2, ≥ 64).</summary>
    private int Capacity { get; }

    /// <summary>Total records dropped on this buffer due to overflow (single-writer, plain read OK).</summary>
    public long DroppedEvents => _droppedEvents;

    /// <summary>
    /// Consumer-only non-destructive emptiness check. Returns <c>true</c> when no records are pending for drain. Used by the consumer thread to
    /// decide whether a retiring slot can be freed.
    /// </summary>
    public bool IsEmpty => _tail.Value == _head.Value;

    /// <summary>
    /// Number of bytes pending for drain. Single-writer for both counters, so the math is consistent; still may race across threads, use as an
    /// approximation only.
    /// </summary>
    public long BytesPending => _head.Value - _tail.Value;

    /// <summary>Diagnostic: dump the first N bytes at tail position as hex. For investigating drain-stuck states.</summary>
    public string DumpAtTail(int bytes = 32)
    {
        var tail = _tail.Value;
        var head = _head.Value;
        var tailOffset = (int)(tail & _mask);
        var n = Math.Min(bytes, _buffer.Length - tailOffset);
        var sb = new System.Text.StringBuilder();
        sb.Append($"tail={tail} head={head} tailOffset={tailOffset} size16@tail={BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(tailOffset))} bytes=[");
        for (int i = 0; i < n; i++)
        {
            sb.Append($"{_buffer[tailOffset + i]:X2} ");
        }

        sb.Append(']');
        return sb.ToString();
    }

    public TraceRecordRing(int capacity)
    {
        if (capacity < 64 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2 and at least 64", nameof(capacity));
        }
        _buffer = new byte[capacity];
        _mask = capacity - 1;
        Capacity = capacity;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Producer API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to reserve <paramref name="size"/> contiguous bytes for a new record. Returns <c>true</c> on success with a writable span in
    /// <paramref name="destination"/>; the caller must fill the span with a complete record (including the u16 size field at the start, matching
    /// <paramref name="size"/>) and then call <see cref="Publish"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rounding:</b> <paramref name="size"/> is rounded up to an even number internally. The returned <paramref name="destination"/> is sized
    /// at the even value; the caller should fill all of it, but bytes past the record's declared size are treated as padding by the reader.
    /// </para>
    /// <para>
    /// <b>Wrap handling:</b> if the record wouldn't fit before the end of the underlying byte buffer, this method writes a wrap sentinel
    /// (<c>u16 = 0xFFFF</c>) at the current head position, advances head to the next wrap boundary, then continues. The wrap sentinel's bytes are
    /// <i>not</i> counted in the caller's reservation — they are consumed from ring capacity and will be skipped by the consumer on drain.
    /// </para>
    /// <para>
    /// <b>Atomicity:</b> after <see cref="Publish"/> the producer's writes become visible to the consumer as an atomic unit (head is advanced in
    /// one 64-bit store). Before <see cref="Publish"/>, the consumer cannot observe the reserved bytes via the <c>_head</c>/<c>_tail</c>
    /// comparison — it will see the ring as still empty at this position.
    /// </para>
    /// </remarks>
    public bool TryReserve(int size, out Span<byte> destination)
    {
        if (size < MinRecordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"Record size must be at least {MinRecordSize}");
        }
        if (size > MaxRecordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"Record size must not exceed {MaxRecordSize}");
        }

        var evenSize = (size + 1) & ~1;  // round up to even
        if (evenSize > Capacity - 2)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"Record size {evenSize} cannot fit in a buffer of capacity {Capacity}");
        }

        var head = _head.Value;
        var tail = _tail.Value;
        var headOffset = (int)(head & _mask);
        var wrapBytes = 0;

        // Check for wrap: would this reservation cross the byte-array end?
        if (headOffset + evenSize > Capacity)
        {
            wrapBytes = Capacity - headOffset;  // always even because headOffset and Capacity are both even
        }

        // Full check: can we fit the wrap-sentinel bytes AND the record between head and tail?
        if ((head - tail) + wrapBytes + evenSize > Capacity)
        {
            _droppedEvents++;
            destination = default;
            return false;
        }

        if (wrapBytes > 0)
        {
            // Write the wrap sentinel's u16 size field. The remaining bytes in this slot are untouched — the consumer sees only the size field
            // and knows to jump the read pointer by wrapBytes.
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(headOffset), TraceRecordHeader.WrapSentinel);
            head += wrapBytes;
            headOffset = 0;
        }

        destination = _buffer.AsSpan(headOffset, evenSize);
        _pendingHead = head + evenSize;
        return true;
    }

    /// <summary>
    /// Commit the pending reservation: advance <c>_head</c> so the consumer can see the new record. Call this exactly once after filling the
    /// <see cref="TryReserve"/> destination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish() =>
        // On x64 TSO, this plain 64-bit store is naturally release-ordered relative to the preceding payload writes (all writes complete before
        // any later write is visible). The consumer's plain read of _head sees either the old value or the new value, never a torn 64-bit store.
        _head.Value = _pendingHead;

    // ═══════════════════════════════════════════════════════════════════════
    // Consumer API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drain pending records into <paramref name="destination"/>, record-by-record. Stops when the destination has no room for the next record
    /// or when the ring has no more records ready. Wrap sentinels are transparently skipped — the bytes the caller sees are a clean, contiguous
    /// record stream starting from the oldest pending record.
    /// </summary>
    /// <returns>Number of bytes written into <paramref name="destination"/>.</returns>
    /// <remarks>
    /// <b>Partial drain:</b> if the destination runs out of room mid-way, whichever records already fit are drained (and consumed from the ring),
    /// the rest stay for the next drain pass. This naturally paces large producers against a fixed merge-buffer size in the consumer thread.
    /// </remarks>
    public int Drain(Span<byte> destination)
    {
        var tail = _tail.Value;
        var head = _head.Value;
        var bytesWritten = 0;

        while (tail < head)
        {
            var tailOffset = (int)(tail & _mask);
            var recordSize = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(tailOffset));

            if (recordSize == TraceRecordHeader.WrapSentinel)
            {
                // Wrap sentinel: advance tail to the next wrap boundary, don't copy any bytes.
                var wrapBytes = Capacity - tailOffset;
                tail += wrapBytes;
                continue;
            }

            // A record size must never exceed the distance from tail to head — that would mean the producer wrote corrupt data.
            // Defensive: if we see such a value, treat it as a drain stop rather than propagating the corruption.
            if (recordSize < MinRecordSize || tail + recordSize > head)
            {
                break;
            }

            // Round up to even (producer did the same when reserving)
            var evenSize = (recordSize + 1) & ~1;

            if (bytesWritten + recordSize > destination.Length)
            {
                // Not enough room for this record in the destination — stop and leave it for the next drain pass.
                break;
            }

            _buffer.AsSpan(tailOffset, recordSize).CopyTo(destination[bytesWritten..]);
            bytesWritten += recordSize;
            tail += evenSize;
        }

        _tail.Value = tail;
        return bytesWritten;
    }

    /// <summary>
    /// Reset head/tail/drop counters to zero. Must not be called concurrently with <see cref="TryReserve"/>, <see cref="Publish"/>, or
    /// <see cref="Drain"/> — the slot-registry protocol guarantees this by transitioning a slot through <c>SlotState.Free</c> before the new owner
    /// claims it.
    /// </summary>
    public void Reset()
    {
        _head.Value = 0;
        _tail.Value = 0;
        _pendingHead = 0;
        _droppedEvents = 0;
    }
}
