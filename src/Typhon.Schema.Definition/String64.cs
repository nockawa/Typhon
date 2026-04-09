// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Schema.Definition;

[PublicAPI]
public struct Point2F
{
    public float X;
    public float Y;
}

[PublicAPI]
public struct Point3F
{
    public float X;
    public float Y;
    public float Z;
}

[PublicAPI]
public struct Point4F
{
    public float X;
    public float Y; 
    public float Z;
    public float W;
}

[PublicAPI]
public struct Point2D
{
    public double X;
    public double Y;
}

[PublicAPI]
public struct Point3D
{
    public double X;
    public double Y;
    public double Z;
}

[PublicAPI]
public struct Point4D
{
    public double X;
    public double Y;
    public double Z;
    public double W;
}

[PublicAPI]
public struct QuaternionF
{
    public float X;
    public float Y;
    public float Z;
    public float W;
}

[PublicAPI]
public struct QuaternionD
{
    public double X;
    public double Y;
    public double Z;
    public double W;
}

public struct VarString
{

}

[PublicAPI]
public unsafe struct String1024
{
    private const int Size = 1024;
    private fixed byte _data[1024];

    public string AsString
    {
        get
        {
            fixed (byte* a = _data)
            {
                return Marshal.PtrToStringUTF8(new IntPtr(a));
            }
        }

        set
        {
            fixed (char* c = value)
            fixed (byte* a = _data)
            {
                var inLength = value.Length;
                var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
                if (sizeRequired < Size)
                {
                    var l = Encoding.UTF8.GetBytes(c, inLength, a, 63);
                    a[l] = 0;            // Null terminator
                }
                else
                {
                    Span<byte> buffer = stackalloc byte[sizeRequired];
                    Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                    Span<byte> d = new Span<byte>(a, Size);
                    buffer.Slice(0, Size).CopyTo(d);
                    a[Size - 1] = 0;
                }
            }
        }
    }
}

[PublicAPI]
[DebuggerDisplay("String: {AsString}")]
public unsafe struct String64 : IComparable<String64>, IEquatable<String64>
{
    private const int Size = 64;
    private fixed byte _data[Size];

    /// <summary>
    /// Construct a String64 instance from a memory area containing the string
    /// </summary>
    /// <param name="stringAddr">Address of the memory area containing the UTF8 string data</param>
    /// <param name="length">Length of the <paramref name="stringAddr"/> memory area</param>
    public String64(byte* stringAddr, int length=64)
    {
        fixed (byte* a = _data)
        {
            new Span<byte>(stringAddr, length).CopyTo(new Span<byte>(a, 64));
        }
    }

    public byte* GetStringContentAddr()
    {
        fixed (byte* a = _data)
        {
            return a;
        }
    }

    public readonly byte* GetStringContentAddrReaOnly()
    {
        fixed (byte* a = _data)
        {
            return a;
        }
    }

    public Span<byte> AsSpan()
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 64);
        }
    }

    public readonly ReadOnlySpan<byte> AsReadOnlySpan()
    {
        fixed (byte* a = _data)
        {
            return new ReadOnlySpan<byte>(a, 64);
        }
    }

    public static implicit operator String64(string str) => new() { AsString = str };

    public string AsString
    {
        get
        {
            fixed (byte* a = _data)
            {
                return Marshal.PtrToStringUTF8(new IntPtr(a));
            }
        }

        set
        {
            fixed (char* c = value)
            fixed (byte* a = _data)
            {
                var inLength = value.Length;
                var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
                if (sizeRequired < Size)
                {
                    var l = Encoding.UTF8.GetBytes(c, inLength, a, 63);
                    a[l] = 0;            // Null terminator
                }
                else
                {
                    Span<byte> buffer = (sizeRequired < 1024) ? stackalloc byte[sizeRequired] : new byte[sizeRequired];
                    Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                    Span<byte> d = new Span<byte>(a, Size);
                    buffer.Slice(0, Size).CopyTo(d);
                    a[Size-1] = 0;
                }
            }
        }
    }

    internal void SetVariant(string value, bool truncate)
    {
        fixed (char* c = value)
        fixed (byte* a = _data)
        {
            var inLength = value.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
            if (sizeRequired < (Size - 3))
            {
                var l = Encoding.UTF8.GetBytes(c, inLength, a+3, 60) + 3;
                a[0] = (byte)'s';
                a[1] = (byte)'t';
                a[2] = (byte)':';
                a[l] = 0;            // Null terminator
            }
            else
            {
                if (!truncate)
                {
                    throw new InvalidOperationException($"Can't set the given string into the variant, the string must not exceed {Size - 3} bytes as UTF8");
                }
                Span<byte> buffer = (sizeRequired < 1024) ? stackalloc byte[sizeRequired] : new byte[sizeRequired];
                Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                Span<byte> d = new(a, Size);
                buffer[..(Size-3)].CopyTo(d[3..]);

                a[0] = (byte)'s';
                a[1] = (byte)'t';
                a[2] = (byte)':';
                a[Size-1] = 0;
            }
        }
    }

    internal void SetVariant(bool value)
    {
        _data[0] = (byte)'b';
        _data[1] = (byte)'o';
        _data[2] = (byte)':';
        _data[3] = value ? (byte)'1' : (byte)'0';
        _data[4] = 0;
    }

    internal void SetVariant(sbyte value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'b';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    internal void SetVariant(short value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'s';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    internal void SetVariant(int value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'i';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    internal void SetVariant(long value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'l';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    public int CompareTo(String64 other) => AsSpan().SequenceCompareTo(other.AsSpan());

    public bool Equals(String64 other) => other.AsSpan().SequenceEqual(AsSpan());

    public override bool Equals(object obj) => obj is String64 other && Equals(other);

    public override int GetHashCode() => (int)MurmurHash2.Hash(AsSpan());

    public static bool operator ==(String64 left, String64 right) => left.Equals(right);

    public static bool operator !=(String64 left, String64 right) => !left.Equals(right);
}
