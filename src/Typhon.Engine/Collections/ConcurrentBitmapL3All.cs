// unset

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// A lock-free, thread-safe hierarchical bitmap with 3 levels of acceleration structures.
///
/// Structure:
/// - L0All: Ground truth bitmap (each bit = one allocation slot)
/// - L1All: "All full" summary (bit set when ALL 64 corresponding L0 bits are set)
/// - L1Any: "Any set" summary (bit set when ANY corresponding L0 bit is set)
/// - L2All: Top-level summary (bit set when ALL 64 corresponding L1All bits are set)
///
/// Concurrency model:
/// - All state is encapsulated in an immutable-ish BitmapState object
/// - L0 operations use Interlocked CAS - the result determines success/failure
/// - L1/L2 are acceleration hints with best-effort updates (temporal incoherence is acceptable)
/// - Resize creates a new state with recounted TotalBitSet, then atomically swaps
/// - TotalBitSet is always exact: each state owns its counter, orphaned states don't matter
/// </summary>
public class ConcurrentBitmapL3All
{
    private const int L0All = 0;
    private const int L1All = 1;
    private const int L1Any = 2;
    private const int L2All = 3;

    /// <summary>
    /// Encapsulates all mutable state. Each state instance owns its maps and counter.
    /// When resize occurs, a new state is created with recounted bits.
    /// Operations on orphaned states don't affect the current state's accuracy.
    /// </summary>
    private sealed class BitmapState
    {
        public readonly Memory<long>[] Maps;
        public readonly int Capacity;
        public int TotalBitSet;

        public BitmapState(int capacity)
        {
            Capacity = capacity;
            TotalBitSet = 0;

            Maps = new Memory<long>[4];
            var length = Math.Max(1, (capacity + 63) / 64);
            Maps[L0All] = new long[length];

            length = Math.Max(1, (length + 63) / 64);
            Maps[L1All] = new long[length];
            Maps[L1Any] = new long[length];

            length = Math.Max(1, (length + 63) / 64);
            Maps[L2All] = new long[length];
        }

        public BitmapState(int newCapacity, BitmapState oldState)
        {
            Capacity = newCapacity;

            Maps = new Memory<long>[4];
            var l0Length = Math.Max(1, (newCapacity + 63) / 64);
            var copySize = Math.Min(l0Length, oldState.Maps[L0All].Length);

            // Copy L0 data
            Maps[L0All] = new long[l0Length];
            var newL0Span = Maps[L0All].Span;
            var oldL0Span = oldState.Maps[L0All].Span;

            // First pass: copy the data
            oldL0Span.Slice(0, copySize).CopyTo(newL0Span);

            // Memory barrier to ensure we see all concurrent writes
            Thread.MemoryBarrier();

            // Second pass: OR in any bits that were set during the first copy
            // This catches writes from concurrent SetL0 operations that raced with us
            for (int i = 0; i < copySize; i++)
            {
                var currentOldValue = Volatile.Read(ref oldL0Span[i]);
                var newBits = currentOldValue & ~newL0Span[i];
                if (newBits != 0)
                {
                    newL0Span[i] |= newBits;
                }
            }

            // When shrinking, clear any stale bits beyond the new capacity in the last word
            var remainingBits = newCapacity & 63;
            if (remainingBits > 0)
            {
                var mask = (1L << remainingBits) - 1;
                newL0Span[l0Length - 1] &= mask;
            }

            // Allocate L1 and L2 arrays
            var l1Length = Math.Max(1, (l0Length + 63) / 64);
            Maps[L1All] = new long[l1Length];
            Maps[L1Any] = new long[l1Length];

            var l2Length = Math.Max(1, (l1Length + 63) / 64);
            Maps[L2All] = new long[l2Length];

            // Rebuild L1/L2 hierarchy from L0 ground truth
            // This ensures correctness for both grow and shrink operations
            RebuildHierarchy();

            // Always recount bits from our own maps - guarantees accuracy
            TotalBitSet = CountBits();
        }

        /// <summary>
        /// Rebuilds L1 and L2 acceleration structures from L0 ground truth.
        /// Called during resize to ensure hierarchy is consistent with actual data.
        /// </summary>
        private void RebuildHierarchy()
        {
            var l0Span = Maps[L0All].Span;
            var l1AllSpan = Maps[L1All].Span;
            var l1AnySpan = Maps[L1Any].Span;
            var l2AllSpan = Maps[L2All].Span;

            // Build L1 from L0
            for (int i = 0; i < l0Span.Length; i++)
            {
                var l0Val = l0Span[i];
                var l1Idx = i >> 6;
                var l1Bit = 1L << (i & 0x3F);

                if (l0Val == -1)
                    l1AllSpan[l1Idx] |= l1Bit;
                if (l0Val != 0)
                    l1AnySpan[l1Idx] |= l1Bit;
            }

            // Build L2 from L1All
            for (int i = 0; i < l1AllSpan.Length; i++)
            {
                if (l1AllSpan[i] == -1)
                {
                    var l2Idx = i >> 6;
                    var l2Bit = 1L << (i & 0x3F);
                    l2AllSpan[l2Idx] |= l2Bit;
                }
            }
        }

        public int CountBits()
        {
            var span = Maps[L0All].Span;
            var count = 0;
            for (int i = 0; i < span.Length; i++)
            {
                count += BitOperations.PopCount((ulong)span[i]);
            }
            return count;
        }
    }

    private volatile BitmapState _state;

    public int Capacity => _state.Capacity;
    public int TotalBitSet => _state.TotalBitSet;
    public bool IsFull
    {
        get
        {
            var state = _state;
            return state.Capacity == state.TotalBitSet;
        }
    }

    public ConcurrentBitmapL3All(int bitCount)
    {
        _state = new BitmapState(bitCount);
    }

    /// <summary>
    /// Resizes the bitmap by creating a new state with recounted bits, then atomically swapping.
    /// Both growing and shrinking are supported.
    /// Operations racing with resize will operate on the old state and return false.
    /// When shrinking, bits beyond the new capacity are lost; callers attempting to access
    /// out-of-bounds indices after shrink will receive IndexOutOfRangeException on retry.
    /// </summary>
    public void Resize(int newBitCount)
    {
        var oldState = _state;

        // No-op if capacity unchanged
        if (newBitCount == oldState.Capacity)
            return;

        var newState = new BitmapState(newBitCount, oldState);

        // Atomic swap - this is the linearization point
        Interlocked.Exchange(ref _state, newState);
    }

    /// <summary>
    /// Sets a single bit at the given index.
    /// Returns true if the bit was successfully set by this call, false if it was already set.
    /// When state changes during the operation (due to resize), the method retries on the new state
    /// to ensure lock-free correctness without duplicates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL0(int bitIndex)
    {
        while (true)
        {
            var state = _state;

            // Check bounds before accessing arrays
            if (bitIndex < 0 || bitIndex >= state.Capacity)
            {
                return false;
            }

            var maps = state.Maps;
            var l0Offset = bitIndex >> 6;
            var l0Mask = 1L << (bitIndex & 0x3F);

            // CAS operation on L0 - this IS the ground truth
            var prevL0 = Interlocked.Or(ref maps[L0All].Span[l0Offset], l0Mask);
            if ((prevL0 & l0Mask) != 0)
            {
                // Bit was already set - optimistic failure, caller retries elsewhere
                return false;
            }

            // Successfully set the bit in this state - now update hints (best-effort)

            // Update L1All if L0 word became fully set
            if ((prevL0 | l0Mask) == -1)
            {
                var l1Offset = l0Offset >> 6;
                var l1Mask = 1L << (l0Offset & 0x3F);

                // Best-effort: Interlocked.Or is safe even if concurrent
                var prevL1 = Interlocked.Or(ref maps[L1All].Span[l1Offset], l1Mask);

                // Update L2All if L1 word became fully set
                if ((prevL1 | l1Mask) == -1)
                {
                    var l2Offset = l1Offset >> 6;
                    var l2Mask = 1L << (l1Offset & 0x3F);
                    Interlocked.Or(ref maps[L2All].Span[l2Offset], l2Mask);
                }
            }

            // Update L1Any if L0 word transitioned from empty to non-empty
            if (prevL0 == 0)
            {
                var l1Offset = l0Offset >> 6;
                var l1Mask = 1L << (l0Offset & 0x3F);
                Interlocked.Or(ref maps[L1Any].Span[l1Offset], l1Mask);
            }

            // Check state BEFORE incrementing counter to avoid incrementing an orphaned state
            var currentState = _state;
            if (currentState != state)
            {
                // State changed - our CAS went to an orphaned state
                // Check if bit is already set in current state (by copy or another thread)
                var currentL0Span = currentState.Maps[L0All].Span;
                if (l0Offset >= currentL0Span.Length || (currentL0Span[l0Offset] & l0Mask) != 0)
                {
                    // Bit is out of bounds or already set - someone else has it
                    return false;
                }
                // Bit is NOT set in current state - loop and retry
                continue;
            }

            // State unchanged - increment counter
            Interlocked.Increment(ref state.TotalBitSet);

            // Double-check state didn't change during increment
            if (_state == state)
            {
                // State still unchanged - we definitely own this bit
                return true;
            }

            // State changed after increment - we incremented orphaned state's counter
            // The bit might or might not be in the new state, but we can't safely claim it
            // because another thread might have set it. We need to retry.
            // (The orphaned counter increment is harmless - that state is garbage collected)
        }
    }

    /// <summary>
    /// Sets all 64 bits of an L0 word atomically (bulk allocation).
    /// Uses CompareExchange to ensure the word was completely empty.
    /// Returns true if successful, false if any bit was already set.
    /// When state changes during the operation (due to resize), the method retries on the new state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL1(int index)
    {
        while (true)
        {
            var state = _state;

            // Check bounds: index is an L0 word index, so check if word exists
            var l0Length = state.Maps[L0All].Length;
            if (index < 0 || index >= l0Length)
            {
                return false;
            }

            var maps = state.Maps;
            var l0Offset = index;

            // CompareExchange: Only set all bits if word was completely empty
            var prevL0 = Interlocked.CompareExchange(ref maps[L0All].Span[l0Offset], -1L, 0L);
            if (prevL0 != 0)
            {
                // Word wasn't empty - we didn't modify anything
                return false;
            }

            // Successfully claimed all 64 bits - update hints (best-effort)
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = Interlocked.Or(ref maps[L1All].Span[l1Offset], l1Mask);

            // Update L2All if L1 word became fully set
            if ((prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                Interlocked.Or(ref maps[L2All].Span[l2Offset], l2Mask);
            }

            // L1Any: word definitely has bits now
            Interlocked.Or(ref maps[L1Any].Span[l1Offset], l1Mask);

            // Check state BEFORE incrementing counter to avoid incrementing an orphaned state
            var currentState = _state;
            if (currentState != state)
            {
                // State changed - our CAS went to an orphaned state
                // Check if word is already set in current state (by copy or another thread)
                var currentL0Span = currentState.Maps[L0All].Span;
                if (l0Offset >= currentL0Span.Length || currentL0Span[l0Offset] != 0)
                {
                    // Word is out of bounds or has bits set - someone else has it
                    return false;
                }
                // Word is empty in current state - loop and retry
                continue;
            }

            // State unchanged - increment counter
            Interlocked.Add(ref state.TotalBitSet, 64);

            // Double-check state didn't change during increment
            if (_state == state)
            {
                // State still unchanged - we definitely own these bits
                return true;
            }

            // State changed after increment - we incremented orphaned state's counter
            // We need to retry to claim in the current state
        }
    }

    /// <summary>
    /// Clears a single bit at the given index.
    /// This operation is idempotent - clearing an already-clear bit is a no-op.
    /// Returns true if successful (bit cleared or was already clear), false if state changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool ClearL0(int index)
    {
        var state = _state;
        var maps = state.Maps;

        var l0Offset = index >> 6;
        var l0Mask = ~(1L << (index & 0x3F));

        // CAS: Clear the bit
        var prevL0 = Interlocked.And(ref maps[L0All].Span[l0Offset], l0Mask);

        // If bit wasn't set, nothing to do (idempotent)
        if ((prevL0 & ~l0Mask) == 0)
        {
            // Bit was already clear - just check for state change
            return _state == state;
        }

        // Bit was set, now cleared - update hints (best-effort)

        // Update L1All if L0 word was fully set (no longer is)
        if (prevL0 == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = Interlocked.And(ref maps[L1All].Span[l1Offset], ~l1Mask);

            // Update L2All if L1 word was fully set
            if (prevL1 == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                Interlocked.And(ref maps[L2All].Span[l2Offset], ~l2Mask);
            }
        }

        // Update L1Any if L0 word became empty
        if ((prevL0 & l0Mask) == 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);
            Interlocked.And(ref maps[L1Any].Span[l1Offset], ~l1Mask);
        }

        // Decrement THIS state's counter
        Interlocked.Decrement(ref state.TotalBitSet);

        // Check if state was swapped
        return _state == state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool IsSet(int index)
    {
        var state = _state;
        var offset = index >> 6;
        var mask = 1L << (index & 0x3F);

        return (state.Maps[L0All].Span[offset] & mask) != 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL0(ref int index, ref long mask)
    {
        var state = _state;
        var capacity = state.Capacity;
        var maps = state.Maps;

        var c0 = ++index;
        long v0 = mask;
        long t0;

        var ll0 = (capacity + 63) / 64;
        var ll1 = maps[L1All].Length;
        var ll2 = maps[L2All].Length;

        while (c0 < capacity)
        {
            // Do we have to fetch a new L0?
            if (((c0 & 0x3F) == 0) || (v0 == -1))
            {
                // Check if we can skip the rest of the level 0
                for (int i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                {
                    t0 = 1L << (c0 & 0x3F);
                    v0 = maps[L0All].Span[i0] | (t0 - 1);

                    if (v0 != -1)
                    {
                        break;
                    }
                    c0 = ++i0 << 6;

                    // Check if we can skip the rest of the level 1
                    for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                    {
                        var v1 = maps[L1All].Span[i1] >> (i0 & 0x3F);
                        if (v1 != -1)
                        {
                            break;
                        }

                        i0 = 0;
                        c0 = ++i1 << 12;

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                        {
                            var v2 = maps[L2All].Span[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }
                            i1 = 0;
                            c0 = ++i2 << 18;
                        }
                    }
                }
            }

            // After hierarchical skip, verify we're still within bounds
            if (c0 >= capacity)
            {
                return false;
            }

            var bitPos = BitOperations.TrailingZeroCount(~v0);
            v0 |= (1L << bitPos);
            index = (c0 & ~0x3F) + bitPos;
            mask = v0;
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL1(ref int index, ref long mask)
    {
        var state = _state;
        var maps = state.Maps;
        var c1 = ++index;
        long v1 = mask;
        var ll1 = maps[L1All].Length;
        var ll2 = maps[L2All].Length;

        while (c1 < (ll1 << 6))
        {
            if (((c1 & 0x3F) == 0) || (v1 == -1))
            {
                // Check if we can skip the rest of the level 1
                for (int i1 = c1 >> 6; i1 < ll1; i1 = c1 >> 6)
                {
                    var t1 = 1L << (c1 & 0x3F);
                    v1 = maps[L1All].Span[i1] | (t1 - 1);
                    if (v1 != -1)
                    {
                        break;
                    }

                    c1 = ++i1 << 6;

                    // Check if we can skip the rest of the level 2
                    for (int i2 = c1 >> 12; i2 < ll2; i2 = c1 >> 12)
                    {
                        var v2 = maps[L2All].Span[i2] >> (i1 & 0x3F);
                        if (v2 != -1)
                        {
                            break;
                        }

                        i1 = 0;
                        c1 = ++i2 << 12;
                    }
                }

                // After hierarchical skip, verify we're still within bounds
                if (c1 >= (ll1 << 6))
                {
                    return false;
                }
            }

            var t = 1L << (c1 & 0x3F);
            v1 = maps[L1Any].Span[c1 >> 6] | (t - 1);
            var bitPos = BitOperations.TrailingZeroCount(~v1);
            v1 |= (1L << bitPos);
            index = (c1 & ~0x3F) + bitPos;
            mask = v1;
            return true;
        }

        return false;
    }
}
