using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
[PublicAPI]
public readonly struct PackedDateTime48
{
    public const long BaseTicks = 621355968000000000; // Ticks at 1970-01-01T00:00:00Z
    public const int PackedShift = 10;
    public const double PackedTickResolution = 0.0000001 * (1 << PackedShift); // 102.4 microseconds
    public static readonly DateTime MinValue = new(BaseTicks);

    private readonly uint _high;
    private readonly ushort _low;
    
    public static PackedDateTime48 UtcNow => new(DateTime.UtcNow);
    public static PackedDateTime48 FromDateTimeTicks(long ticks) => new(ticks, false);
    public static PackedDateTime48 FromPackedDateTimeTicks(long ticks) => new(ticks, true);
    
    public static long ToPackedTicks(long dateTimeTicks) => (dateTimeTicks - BaseTicks) >> PackedShift;
    public static long ToDateTimeTicks(long packedTicks) => (packedTicks << PackedShift) + BaseTicks;
    public PackedDateTime48(DateTime dateTime) : this(dateTime.Ticks, false)
    {
    }

    public PackedDateTime48(long value, bool isPacked)
    {
        Debug.Assert(isPacked || value >= BaseTicks, $"Can't store the given value {value}, it is before {MinValue}.");
        var packedTicks = isPacked ? value : ToPackedTicks(value);
        
        _high = (uint)(packedTicks >> 16);
        _low = (ushort)(packedTicks & 0xFFFF);
    }
    
    public long Ticks => ((((long)_high << 16) | _low) << PackedShift) + BaseTicks;
    public long PackedTicks => (((long)_high << 16) | _low);

    public static explicit operator DateTime(PackedDateTime48 packed) => new(packed.Ticks);
    public static explicit operator PackedDateTime48(DateTime dateTime) => new(dateTime.Ticks, false);
    
    public override string ToString() => ((DateTime)this).ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture);
}

[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public readonly struct PackedTimeSpan32
{
    public static readonly TimeSpan MinValue = ((TimeSpan)new PackedTimeSpan32(int.MinValue, true));
    public static readonly TimeSpan MaxValue = ((TimeSpan)new PackedTimeSpan32(int.MaxValue, true));
    private readonly int _packedTicks;

    public long Ticks => (long)_packedTicks << PackedDateTime48.PackedShift;
    public static explicit operator TimeSpan(PackedTimeSpan32 packed) => new(packed.Ticks);

    public PackedTimeSpan32(TimeSpan timeSpan) : this(timeSpan.Ticks, false)
    {
        
    }
    
    public PackedTimeSpan32(long value, bool isPacked)
    {
        var ticks = isPacked ? value : (value >> PackedDateTime48.PackedShift);
        Debug.Assert(ticks is >= int.MinValue and <= int.MaxValue, $"Given value {value} too large to be packed");
        _packedTicks = (int)ticks;
    }

    public override string ToString() => ((TimeSpan)this).ToString();
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
[PublicAPI]
public readonly struct PackedTimeSpan48
{
    private readonly uint _high;
    private readonly ushort _low;

    public static readonly TimeSpan MinValue = ((TimeSpan)new PackedTimeSpan48(long.MinValue >> 16, true));
    public static readonly TimeSpan MaxValue = ((TimeSpan)new PackedTimeSpan48(long.MaxValue >> 16, true));

    public long Ticks => ((((long)_high << 16) | _low) << PackedDateTime48.PackedShift);

    public static explicit operator TimeSpan(PackedTimeSpan48 packed) => new(packed.Ticks);

    public PackedTimeSpan48(TimeSpan timeSpan) : this(timeSpan.Ticks, false)
    {
    }

    public PackedTimeSpan48(long value, bool isPacked)
    {
        var ticks = isPacked ? value : (value >> PackedDateTime48.PackedShift);
        Debug.Assert(ticks is >= long.MinValue >> 16 and <= long.MaxValue >> 16, $"Given value {value} too large to be packed");
        _high = (uint)(ticks >> 16);
        _low = (ushort)(ticks & 0xFFFF);
    }
    
    public override string ToString() => ((TimeSpan)this).ToString();
}
