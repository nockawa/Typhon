using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine
{
    /// <summary>
    /// Store a map of bits that can be set/clear concurrently with an efficient evaluation of which bits are set in the whole map.
    /// </summary>
    /// <remarks>
    /// We have internally three levels of bit map, the first that stores the actual data packed in long (64-bits).
    /// The second will contain one bit per each long of the first map, set to 1 if there's at least one bit set in the main map or 0 is they are all cleared.
    /// The third which does the same than the second but for 4096 bits. This allows us to enumerate all the bits that are set in the bitmap in a very efficient fashion,
    /// using the second and third level to "jump" faster to the next bit set.
    /// </remarks>
    public class ConcurrentBitmapL3 : IEnumerable<int>
    {
        private readonly Memory<long>[] _data;
        internal int LastEnumLoopCount;

        public ConcurrentBitmapL3(int bitCount)
        {
            _data = new Memory<long>[3];
            Capacity = bitCount;

            var length = Math.Max(1, (bitCount + 63) / 64);
            _data[0] = new long[length];
            
            length = Math.Max(1, (length + 63) / 64);
            _data[1] = new long[length];
            
            length = Math.Max(1, (length + 63) / 64);
            _data[2] = new long[length];
        }

        public void Reset()
        {
            _data[0].Span.Clear();
            _data[1].Span.Clear();
            _data[2].Span.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index)
        {
            var offset = index >> 6;
            var mask = 1 << (index & 0x3F);
            var prevValue = Interlocked.Or(ref _data[0].Span[offset], mask);
            if (prevValue != 0)
            {
                return;
            }

            index = offset;
            offset = index >> 6;
            mask = 1 << (index & 0x3F);
            prevValue = Interlocked.Or(ref _data[1].Span[offset], mask);
            if (prevValue != 0)
            {
                return;
            }

            index = offset;
            offset = index >> 6;
            mask = 1 << (index & 0x3F);
            Interlocked.Or(ref _data[2].Span[offset], mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear(int index)
        {
            var offset = index >> 6;
            var mask = ~(1 << (index & 0x3F));
            var prevVal = Interlocked.And(ref _data[0].Span[offset], mask);
            if (prevVal == 0 || (prevVal&mask) != 0)
            {
                return;
            }

            index = offset;
            offset = index >> 6;
            mask = ~(1 << (index & 0x3F));
            prevVal = Interlocked.And(ref _data[1].Span[offset], mask);
            if (prevVal == 0 || (prevVal&mask) != 0)
            {
                return;
            }

            index = offset;
            offset = index >> 6;
            mask = ~(1 << (index & 0x3F));
            Interlocked.And(ref _data[1].Span[offset], mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSet(int index)
        {
            var offset = index >> 6;
            var mask = 1L << (index & 0x3F);
            return (_data[0].Span[offset] & mask) != 0L;
        }

        public int Capacity { get; }

        public IEnumerator<int> GetEnumerator() => new Enumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<int>
        {
            private readonly ConcurrentBitmapL3 _owner;

            private int _c0;
            private long _v0;
            private int _loopCount;

            public Enumerator(ConcurrentBitmapL3 owner)
            {
                _owner = owner;
                _c0 = -1;
                _v0 = -1;
                _loopCount = 0;
            }

            public bool MoveNext()
            {
                var o = _owner;

                var c0 = ++_c0;
                var v0 = _v0;
                var lc = _loopCount;

                var ll0 = o._data[0].Length;
                var ll1 = o._data[1].Length;
                var ll2 = o._data[2].Length;

                while (c0 < o.Capacity)
                {
                    // Do we have to fetch a new L0?
                    if (((c0 & 0x3F) == 0) || (v0 == 0))
                    {
                        // Check if we can skip the rest of the level 0
                        for (int i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6, lc++)
                        {
                            v0 = o._data[0].Span[i0] >> c0;
                            if (v0 != 0)
                            {
                                break;
                            }
                            c0 = ++i0 << 6;

                            // Check if we can skip the rest of the level 1
                            for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12, lc++)
                            {
                                var v1 = o._data[1].Span[i1] >> (i0 & 0x3F);
                                if (v1 != 0)
                                {
                                    break;
                                }

                                i0 = 0;
                                c0 = ++i1 << 12;

                                // Check if we can skip the rest of the level 2
                                for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18, lc++)
                                {
                                    var v2 = o._data[2].Span[i2] >> (i1 & 0x3F);
                                    if (v2 != 0)
                                    {
                                        break;
                                    }
                                    i1 = 0;
                                    c0 = ++i2 << 18;
                                }
                            }
                        }
                    }

                    if ((v0 & 1) != 0)
                    {
                        _c0 = c0;
                        _v0 = v0 >> 1;
                        _loopCount = lc;
                        return true;
                    }

                    v0 >>= 1;
                    c0++;
                    lc++;
                }

                o.LastEnumLoopCount = lc;

                return false;
            }

            public void Reset()
            {
                _c0 = -1;
                _v0 = -1;
                _loopCount = 0;
            }

            public int Current => _c0;

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }
        }
    }
}