// unset

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Engine;

public struct Point2F
{
    public float X;
    public float Y;
}

public struct Point3F
{
    public float X;
    public float Y;
    public float Z;
}

public struct Point4F
{
    public float X;
    public float Y;
    public float Z;
    public float W;
}

public struct Point2D
{
    public double X;
    public double Y;
}

public struct Point3D
{
    public double X;
    public double Y;
    public double Z;
}

public struct Point4D
{
    public double X;
    public double Y;
    public double Z;
    public double W;
}

public struct QuaternionF
{
    public float X;
    public float Y;
    public float Z;
    public float W;
}

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

[DebuggerDisplay("String: {AsString}")]
public unsafe struct String64 : IComparable<String64>, IEquatable<String64>
{
    private const int Size = 64;
    private fixed byte _data[Size];

    /// <summary>
    /// Construct a String64 instance from a memory area containing the string
    /// </summary>
    /// <param name="stringAddr">Address of the memory area containing the UTF8 string data</param>
    /// <param name="length">Length of the <see cref="stringAddr"/> memory area</param>
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

    public Span<byte> AsSpan()
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 64);
        }
    }

    public static implicit operator String64(string str) => new() {AsString = str};

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
                    a[Size-1] = 0;
                }
            }
        }
    }

    public int CompareTo(String64 other) => AsSpan().SequenceCompareTo(other.AsSpan());

    public bool Equals(String64 other) => other.AsSpan().SequenceEqual(AsSpan());

    public override bool Equals(object obj) => obj is String64 other && Equals(other);

    public override int GetHashCode() => (int)MurmurHash2.Hash(AsSpan());

    public static bool operator ==(String64 left, String64 right) => left.Equals(right);

    public static bool operator !=(String64 left, String64 right) => !left.Equals(right);
}

public static class StringExtensions
{
    internal unsafe static bool StoreString(string str, byte* dest, int destMaxSize)
    {
        var l = Encoding.UTF8.GetByteCount(str);
        if (l + 1 > destMaxSize)
        {
            return false;
        }

        fixed (char* c = str)
        {
            Encoding.UTF8.GetBytes(c, str.Length, dest, destMaxSize);
            dest[l] = 0;            // Null terminator
        }

        return true;
    }

    internal unsafe static string LoadString(byte* addr) => Marshal.PtrToStringUTF8((IntPtr)addr);

}

public static class MathHelpers{
    public static bool IsPow2(int x) => (x & (x - 1)) == 0;
    public static bool IsPow2(long x) => (x & (x - 1)) == 0;
}