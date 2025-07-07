// unset

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

public class ConcurrentBitmapL3All
{
    private const int L0All = 0;
    private const int L1All = 1;
    private const int L1Any = 2;
    private const int L2All = 3;

    private volatile int _control;
    private Memory<long>[] _maps;

    public int Capacity { get; private set; }
    public int TotalBitSet { get; private set; }
    public bool IsFull => Capacity == TotalBitSet;

    public ConcurrentBitmapL3All(int bitCount)
    {
        Capacity = bitCount;
        TotalBitSet = 0;

        _maps = new Memory<long>[4];
        var length = Math.Max(1, (bitCount + 63) / 64);
        _maps[L0All] = new long[length];

        length = Math.Max(1, (length + 63) / 64);
        _maps[L1All] = new long[length];
        _maps[L1Any] = new long[length];

        length = Math.Max(1, (length + 63) / 64);
        _maps[L2All] = new long[length];
    }

    public void Resize(int newBitCount)
    {
        TakeControl();

        var shrink = newBitCount < Capacity;
        Capacity = newBitCount;

        var maps = new Memory<long>[4];
        var length = Math.Max(1, (newBitCount + 63) / 64);
        var copySize = Math.Min(length, _maps[L0All].Length);

        maps[L0All] = new long[length];
        _maps[L0All].Span.Slice(0, copySize).CopyTo(maps[L0All].Span);

        length = Math.Max(1, (length + 63) / 64);
        maps[L1All] = new long[length];
        maps[L1Any] = new long[length];

        copySize = Math.Max(1, (copySize + 63) / 64);
        _maps[L1All].Span.Slice(0, copySize).CopyTo(maps[L1All].Span);
        _maps[L1Any].Span.Slice(0, copySize).CopyTo(maps[L1Any].Span);

        length = Math.Max(1, (length + 63) / 64);
        maps[L2All] = new long[length];

        copySize = Math.Max(1, (copySize + 63) / 64);
        _maps[L2All].Span.Slice(0, copySize).CopyTo(maps[L2All].Span);

        _maps = maps;

        if (shrink)
        {
            var span = maps[L0All].Span.Cast<long, ulong>();
            var spanLength = span.Length;
            var newCount = 0;

            for (int i = 0; i < spanLength; i++)
            {
                newCount += BitOperations.PopCount(span[i]);
            }

            TotalBitSet = newCount;
        }

        _control = 0;
    }


    private void TakeControl()
    {
        if (Interlocked.CompareExchange(ref _control, 1, 0) != 0)
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _control, 1, 0) != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL0(int bitIndex)
    {
        var l0Offset = bitIndex >> 6;
        var l0Mask = 1L << (bitIndex & 0x3F);

        TakeControl();

        var prevL0 = Interlocked.Or(ref _maps[L0All].Span[l0Offset], l0Mask);
        if ((prevL0 & l0Mask) != 0)
        {
            _control = 0;
            // The bit was concurrently set by someone else
            return false;
        }

        if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] |= l1Mask;

            if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] |= l2Mask;
            }
        }

        if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);
            _maps[L1Any].Span[l1Offset] |= l1Mask;
        }

        ++TotalBitSet;
        _control = 0;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL1(int index)
    {
        var l0Offset = index;
        var l0Mask = -1L;

        TakeControl();
        var prevL0 = Interlocked.Or(ref _maps[L0All].Span[l0Offset], l0Mask);
        if (prevL0 != 0)
        {
            _control = 0;
            // Can't allocate the whole L1, some bits are set at L0
            return false;
        }

        if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] |= l1Mask;

            if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] |= l2Mask;
            }
        }

        if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            _maps[L1Any].Span[l1Offset] |= l1Mask;
        }

        TotalBitSet += 64;
        _control = 0;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ClearL0(int index)
    {
        var l0Offset = index >> 6;
        var l0Mask = ~(1L << (index & 0x3F));

        TakeControl();
        var prevL0 = Interlocked.And(ref _maps[L0All].Span[l0Offset], l0Mask);
        if ((prevL0 == -1) && ((prevL0 & l0Mask) != -1))
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] &= l1Mask;

            if (prevL1 == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] &= l2Mask;
            }
        }

        if ((prevL0 != 0) && ((prevL0 & l0Mask) == 0))
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            _maps[L1Any].Span[l1Offset] &= l1Mask;
        }

        _control = 0;
        --TotalBitSet;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool IsSet(int index)
    {
        var offset = index >> 6;
        var mask = 1L << (index & 0x3F);

        return (_maps[L0All].Span[offset] & mask) != 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL0(ref int index, ref long mask)
    {
        var capacity = Capacity;
        var maps = _maps;

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
        var maps = _maps;
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