using JetBrains.Annotations;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

[PublicAPI]
public static class MathExtensions
{
    #region Constants

    private static readonly CultureInfo DefaultCulture = new("en-us");

    #endregion

    #region Public APIs

    #region Methods

    public static string Bandwidth(int size, double elapsed) => string.Create(DefaultCulture, $"{(size / elapsed).FriendlySize()}/sec");

    public static string Bandwidth(long size, double elapsed) => string.Create(DefaultCulture, $"{(size / elapsed).FriendlySize()}/sec");

    public static string FriendlySize(this long val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    public static string FriendlySize(this int val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    public static string FriendlySize(this double val)
    {
        var scalesF = new[] { "b", "Kb", "Mb", "Gb" };
        var f = val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    public static string FriendlyAmount(this int val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1000)
            {
                break;
            }

            f /= 1000;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    extension(double val)
    {
        public string FriendlyAmount()
        {
            var scalesF = new[] { "", "K", "M", "G" };
            var f = val;
            var iF = 0;
            for (; iF < 3; iF++)
            {
                if (f < 1000)
                {
                    break;
                }

                f /= 1000;
            }
            return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
        }

        public string FriendlyTime(bool displayRate = true)
        {
            var scalesE = new[] { "sec", "ms", "µs", "ns" };
            var e = val;
            var iE = 0;
            for (; iE < 3; iE++)
            {
                if (Math.Abs(e) > 1)
                {
                    break;
                }

                e *= 1000;
            }

            if (displayRate)
            {
                var scalesF = new[] { "", "K", "M", "B" };
                var f = 1 / val;
                var iF = 0;
                for (; iF < 3; iF++)
                {
                    if (f < 1000)
                    {
                        break;
                    }

                    f /= 1000;
                }
                return string.Create(DefaultCulture, $"{e:0.###}{scalesE[iE]} ({f:0.###}{scalesF[iF]}/sec)");
            }
            else
            {
                return string.Create(DefaultCulture, $"{e:0.###}{scalesE[iE]}");
            }
        }
    }

    public static bool IsPowerOf2(this int x) => (x & (x - 1)) == 0;
    public static bool IsPowerOf2(this long x) => (x & (x - 1)) == 0;

    /// <summary>
    /// Return the next power of 2 of the given value
    /// </summary>
    /// <param name="v">The value</param>
    /// <returns>The next power of 2</returns>
    /// <remarks>
    /// If the given value is already a power of 2, this method will return the next one.
    /// </remarks>
    public static int NextPowerOf2(this int v)
    {
        v |= v >> 1;         v |= v >> 2;
        v |= v >> 4;         v |= v >> 8;
        v |= v >> 16;
        v++;
        return v;
    }

    public static double TicksToSeconds(this long ticks) => ((double)ticks / TimeSpan.TicksPerSecond);

    public static double TotalSeconds(this int ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;
    public static double TotalSeconds(this long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;

    #endregion

    #endregion
}

[PublicAPI]
public static class PackExtensions
{
    #region Public APIs

    #region Methods

    // Byte level packing
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte ByteLevelSize(this uint n, bool writeZero=false)
    {
        if ((n & 0xFF000000) != 0) return 4;
        if ((n & 0x00FF0000) != 0) return 3;
        if ((n & 0x0000FF00) != 0) return 2;
        if ((n & 0x000000FF) != 0) return 1;
        return (byte)(writeZero ? 1 : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte High(this ushort n) => (byte)(n >> 8);

    extension(ref ushort n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void High(byte val) => n = (ushort)(val << 8 | (n & 0xFF));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void High(sbyte val) => n = (ushort)(val << 8 | (n & 0xFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static ushort High(this uint n) => (ushort)(n >> 16);

    extension(ref uint n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void High(ushort val) => n = (uint)val << 16 | (n & 0xFFFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void High(short val) => n = (uint)val << 16 | (n & 0xFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static uint High(this ulong n) => (uint)(n >> 32);

    extension(ref ulong n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void High(uint val) => n = (ulong)val << 32 | (n & 0xFFFFFFFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void High(int val) => n = (ulong)val << 32 | (n & 0xFFFFFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static sbyte HighS(this ushort n) => (sbyte)(n >> 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static short HighS(this uint n) => (short)(n >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int HighS(this ulong n) => (int)(n >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte Low(this ushort n) => (byte)n;

    extension(ref ushort n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Low(byte val) => n = (ushort)((n & 0xFF00) | val);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Low(sbyte val) => n = (ushort)((n & 0xFF00) | (byte)val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static ushort Low(this uint n) => (ushort)n;

    extension(ref uint n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Low(ushort val) => n = (n & 0xFFFF0000) | val;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Low(short val) => n = (n & 0xFFFF0000) | (ushort)val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static uint Low(this ulong n) => (uint)n;

    extension(ref ulong n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Low(uint val) => n = (n & 0xFFFFFFFF00000000) | val;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Low(int val) => n = (n & 0xFFFFFFFF00000000) | (uint)val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static sbyte LowS(this ushort n) => (sbyte)n;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static short LowS(this uint n) => (short)n;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int LowS(this ulong n) => (int)n;

    // 16-bits with unsigned
    extension(ref ushort n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Pack(byte high, byte low) => n = (ushort)(high << 8 | low);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Pack(sbyte high, sbyte low) => n = (ushort)(high << 8 | (byte)low);
    }

    // 16-bits with signed

    // 32-bits with unsigned
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref uint n, ushort high, ushort low) => n = (uint)high << 16 | low;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref int n, ushort high, ushort low) => n = high << 16 | low;

    // 32-bits with signed
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref uint n, short high, short low) => n = (uint)high << 16 | (ushort)low;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref int n, short high, short low) => n = high << 16 | (ushort)low;

    // 64-bits with unsigned
    extension(ref ulong n)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Pack(uint high, uint low) => n = (ulong)high << 32 | low;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Pack(int high, int low) => n = (ulong)high << 32 | (uint)low;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Pack(ushort highUHigh, ushort highULow, uint low) => n = (ulong)((uint)highUHigh<<16 | highULow) << 32 | low;
    }

    // 64-bits with signed

    public static unsafe void ReadByteLevel(this ref uint n, void* addr, int byteSize)
    {
        var cur = (byte*)addr;
        switch (byteSize)
        {
            case 1:
                n = *cur;
                break;
            case 2:
                n = *(ushort*)cur;
                break;
            case 3:
                n = *(ushort*)cur;
                n |= (uint)(*(cur+2) << 16);
                break;
            case 4:
                n = *(uint*)cur;
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (byte, byte) Unpack(this ushort n) => ((byte)(n >> 8), (byte)(n & 0xFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (ushort, ushort) Unpack(this uint n) => ((ushort)(n >> 16), (ushort)(n & 0xFFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (uint, uint) Unpack(this ulong n) => ((uint)(n >> 32), (uint)(n & 0xFFFFFFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (sbyte, sbyte) UnpackS(this ushort n) => ((sbyte)(n >> 8), (sbyte)(n & 0xFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (short, short) UnpackS(this uint n) => ((short)(n >> 16), (short)(n & 0xFFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) UnpackS(this ulong n) => ((int)(n >> 32), (int)(n & 0xFFFFFFFF));

    public static unsafe int WriteByteLevel(this uint n, ref byte* dest, bool writeZero=false)
    {
        var s = n.ByteLevelSize(writeZero);
        switch (s)
        {
            case 0:
                return 0;
            case 1:
                *dest = (byte)n;
                ++dest;
                return 1;
            case 2:
                *((ushort*)dest) = (ushort)n;
                dest += 2;
                return 2;
            case 3:
                *((ushort*)dest) = (ushort)n;
                dest[2] = (byte)(n >> 16);
                dest += 3;
                return 3;
            default:
                *((uint*)dest) = n;
                dest += 4;
                return 4;
        }
    }

    #endregion

    #endregion
}
