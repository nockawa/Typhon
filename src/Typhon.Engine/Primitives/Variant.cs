using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Store data of a type determined at construction and formatted as a string
/// </summary>
/// <remarks>
/// <para>
/// This type allows to store in a field of a component a data that can be of a user set type at construction.
/// </para>
/// <para>
/// The variant has a fixed size 64 bytes as its only field is a <see cref="String64"/> storing the data type and value in the form <c>"tt:data"</c>.
/// </para>
/// <para>
/// There are methods to get or explicitly cast the variant to the literal type of the data it stores.
/// </para>
/// <para>
/// This struct is read-only.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct Variant : IComparable<Variant>, IEquatable<Variant>
{
    public Variant(bool value)      => _text.SetVariant(value);
    public Variant(sbyte value)     => _text.SetVariant(value);
    public Variant(short value)     => _text.SetVariant(value);
    public Variant(int value)       => _text.SetVariant(value);
    
    public Variant(long value)      => _text.SetVariant(value);

    public Variant(string value, bool truncate)
    {
        _text.SetVariant(value, truncate);
    }
    
    public static explicit operator string(Variant v) => v.AsString();
    public static explicit operator bool(Variant v) => v.AsBool();
    public static explicit operator sbyte(Variant v) => v.AsByte();
    public static explicit operator short(Variant v) => v.AsShort();
    public static explicit operator int(Variant v) => v.AsInt();
    public static explicit operator long(Variant v) => v.AsLong();

    unsafe public string AsString()
    {
        CheckAssertType(FieldType.String);
        fixed (byte* a = _text.AsReadOnlySpan()[3..])
        {
            return Marshal.PtrToStringUTF8(new IntPtr(a));
        }
    }

    unsafe public bool AsBool()
    {
        CheckAssertType(FieldType.Boolean);
        var a = _text.GetStringContentAddrReaOnly();
        return a[3] == (byte)'1';
    }

    public sbyte AsByte()
    {
        CheckAssertType(FieldType.Byte);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return sbyte.Parse(spanUtf8);
    }

    public short AsShort()
    {
        CheckAssertType(FieldType.Short);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return short.Parse(spanUtf8);
    }

    public int AsInt()
    {
        CheckAssertType(FieldType.Int);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return int.Parse(spanUtf8);
    }

    public long AsLong()
    {
        CheckAssertType(FieldType.Long);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return long.Parse(spanUtf8);
    }

    private void CheckAssertType(FieldType fieldType)
    {
        if (FieldType != fieldType)
        {
            throw new InvalidOperationException($"Can't cast {this} to {fieldType} because it's of {FieldType} type");
        }
    }

    public override string ToString()
    {
        switch (FieldType)
        {
            case FieldType.Boolean:
                var b = AsBool();
                var val = b ? "true" : "false";
                return $"{val} (bool)";
            case FieldType.String:
                return $"{AsString()} (string)";
            case FieldType.Byte:
                return $"{AsByte().ToString()} (byte)";
            case FieldType.Short:
                return $"{AsShort().ToString()} (short)";
            case FieldType.Int:
                return $"{AsInt().ToString()} (int)";
            case FieldType.Long:
                return $"{AsLong().ToString()} (long)";
        }

        return "";
    }

    private readonly String64 _text;

    private Variant(String64 text)
    {
        _text = text;
    }

    public FieldType FieldType
    {
        get
        {
            var header = _text.AsReadOnlySpan();
            if (header.Length < 3 || header[2] != ':')
            {
                return FieldType.None;
            }
            
            return (ushort)((header[0] << 8) | header[1]) switch
            {
                (byte)'b' << 8 | (byte)'o' => FieldType.Boolean,
                (byte)'s' << 8 | (byte)'b' => FieldType.Byte,
                (byte)'s' << 8 | (byte)'s' => FieldType.Short,
                (byte)'s' << 8 | (byte)'i' => FieldType.Int,
                (byte)'s' << 8 | (byte)'l' => FieldType.Long,
                (byte)'u' << 8 | (byte)'b' => FieldType.UByte,
                (byte)'u' << 8 | (byte)'s' => FieldType.UShort,
                (byte)'u' << 8 | (byte)'i' => FieldType.UInt,
                (byte)'u' << 8 | (byte)'l' => FieldType.ULong,
                (byte)'f' << 8 | (byte)'l' => FieldType.Float,
                (byte)'d' << 8 | (byte)'f' => FieldType.Double,
                (byte)'c' << 8 | (byte)'h' => FieldType.Char,
                (byte)'s' << 8 | (byte)'t' => FieldType.String,
                _ => FieldType.None
            };
        }
    }

    public int CompareTo(Variant other) => _text.AsReadOnlySpan().SequenceCompareTo(other._text.AsReadOnlySpan());

    public bool Equals(Variant other) => other._text.AsReadOnlySpan().SequenceEqual(_text.AsReadOnlySpan());

    public override bool Equals(object obj) => obj is Variant other && Equals(other);

    public override int GetHashCode() => (int)MurmurHash2.Hash(_text.AsReadOnlySpan());

    public static bool operator ==(Variant left, Variant right) => left.Equals(right);

    public static bool operator !=(Variant left, Variant right) => !left.Equals(right);
}