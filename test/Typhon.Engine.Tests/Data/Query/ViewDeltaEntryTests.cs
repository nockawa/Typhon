using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

[TestFixture]
public class ViewDeltaEntryTests
{
    #region KeyBytes8 Size

    [Test]
    public void KeyBytes8_Size_Is8Bytes() =>
        Assert.That(Unsafe.SizeOf<KeyBytes8>(), Is.EqualTo(8));

    #endregion

    #region ViewDeltaEntry Size

    [Test]
    public void ViewDeltaEntry_Size_Is24Bytes() =>
        Assert.That(Unsafe.SizeOf<ViewDeltaEntry>(), Is.EqualTo(24));

    #endregion

    #region ViewDeltaEntry Field Offsets

    [Test]
    public void FieldOffset_EntityPK_Is0() =>
        Assert.That(Marshal.OffsetOf<ViewDeltaEntry>(nameof(ViewDeltaEntry.EntityPK)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void FieldOffset_BeforeKey_Is8() =>
        Assert.That(Marshal.OffsetOf<ViewDeltaEntry>(nameof(ViewDeltaEntry.BeforeKey)).ToInt32(), Is.EqualTo(8));

    [Test]
    public void FieldOffset_AfterKey_Is16() =>
        Assert.That(Marshal.OffsetOf<ViewDeltaEntry>(nameof(ViewDeltaEntry.AfterKey)).ToInt32(), Is.EqualTo(16));

    #endregion

    #region ViewDeltaEntry Defaults

    [Test]
    public void Default_HasZeroEntityPK()
    {
        var entry = new ViewDeltaEntry();
        Assert.That(entry.EntityPK, Is.EqualTo(0));
    }

    [Test]
    public void Default_HasZeroKeys()
    {
        var entry = new ViewDeltaEntry();
        Assert.That(entry.BeforeKey.IsZero, Is.True);
        Assert.That(entry.AfterKey.IsZero, Is.True);
    }

    #endregion

    #region KeyBytes8 IsZero

    [Test]
    public void KeyBytes8_IsZero_TrueForDefault() =>
        Assert.That(new KeyBytes8().IsZero, Is.True);

    [Test]
    public void KeyBytes8_IsZero_FalseForNonZero() =>
        Assert.That(KeyBytes8.FromInt(42).IsZero, Is.False);

    [Test]
    public void KeyBytes8_IsZero_TrueForExplicitZero() =>
        Assert.That(KeyBytes8.FromInt(0).IsZero, Is.True);

    #endregion

    #region KeyBytes8 Roundtrip — Int

    [Test]
    public void KeyBytes8_Roundtrip_Int_Positive()
    {
        var kb = KeyBytes8.FromInt(12345);
        Assert.That(kb.AsInt(), Is.EqualTo(12345));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Int_Negative()
    {
        var kb = KeyBytes8.FromInt(-99);
        Assert.That(kb.AsInt(), Is.EqualTo(-99));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Int_Zero()
    {
        var kb = KeyBytes8.FromInt(0);
        Assert.That(kb.AsInt(), Is.EqualTo(0));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Int_MaxValue()
    {
        var kb = KeyBytes8.FromInt(int.MaxValue);
        Assert.That(kb.AsInt(), Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Int_MinValue()
    {
        var kb = KeyBytes8.FromInt(int.MinValue);
        Assert.That(kb.AsInt(), Is.EqualTo(int.MinValue));
    }

    #endregion

    #region KeyBytes8 Roundtrip — Long

    [Test]
    public void KeyBytes8_Roundtrip_Long_Positive()
    {
        var kb = KeyBytes8.FromLong(9876543210L);
        Assert.That(kb.AsLong(), Is.EqualTo(9876543210L));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Long_Negative()
    {
        var kb = KeyBytes8.FromLong(-9876543210L);
        Assert.That(kb.AsLong(), Is.EqualTo(-9876543210L));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Long_MaxValue()
    {
        var kb = KeyBytes8.FromLong(long.MaxValue);
        Assert.That(kb.AsLong(), Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Long_MinValue()
    {
        var kb = KeyBytes8.FromLong(long.MinValue);
        Assert.That(kb.AsLong(), Is.EqualTo(long.MinValue));
    }

    #endregion

    #region KeyBytes8 Roundtrip — Float

    [Test]
    public void KeyBytes8_Roundtrip_Float_Positive()
    {
        var kb = KeyBytes8.FromFloat(3.14f);
        Assert.That(kb.AsFloat(), Is.EqualTo(3.14f));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Float_Negative()
    {
        var kb = KeyBytes8.FromFloat(-2.5f);
        Assert.That(kb.AsFloat(), Is.EqualTo(-2.5f));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Float_Zero()
    {
        var kb = KeyBytes8.FromFloat(0f);
        Assert.That(kb.AsFloat(), Is.EqualTo(0f));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Float_MaxValue()
    {
        var kb = KeyBytes8.FromFloat(float.MaxValue);
        Assert.That(kb.AsFloat(), Is.EqualTo(float.MaxValue));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Float_MinValue()
    {
        var kb = KeyBytes8.FromFloat(float.MinValue);
        Assert.That(kb.AsFloat(), Is.EqualTo(float.MinValue));
    }

    #endregion

    #region KeyBytes8 Roundtrip — Double

    [Test]
    public void KeyBytes8_Roundtrip_Double_Positive()
    {
        var kb = KeyBytes8.FromDouble(3.141592653589793);
        Assert.That(kb.AsDouble(), Is.EqualTo(3.141592653589793));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Double_Negative()
    {
        var kb = KeyBytes8.FromDouble(-1.23e45);
        Assert.That(kb.AsDouble(), Is.EqualTo(-1.23e45));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Double_MaxValue()
    {
        var kb = KeyBytes8.FromDouble(double.MaxValue);
        Assert.That(kb.AsDouble(), Is.EqualTo(double.MaxValue));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Double_MinValue()
    {
        var kb = KeyBytes8.FromDouble(double.MinValue);
        Assert.That(kb.AsDouble(), Is.EqualTo(double.MinValue));
    }

    #endregion

    #region KeyBytes8 Roundtrip — Bool

    [Test]
    public void KeyBytes8_Roundtrip_Bool_True()
    {
        var kb = KeyBytes8.FromBool(true);
        Assert.That(kb.AsLong(), Is.EqualTo(1));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Bool_False()
    {
        var kb = KeyBytes8.FromBool(false);
        Assert.That(kb.AsLong(), Is.EqualTo(0));
        Assert.That(kb.IsZero, Is.True);
    }

    #endregion

    #region KeyBytes8 Roundtrip — Byte / SByte

    [Test]
    public void KeyBytes8_Roundtrip_Byte()
    {
        var kb = KeyBytes8.FromByte(255);
        Assert.That(kb.AsInt(), Is.EqualTo(255));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Byte_Zero()
    {
        var kb = KeyBytes8.FromByte(0);
        Assert.That(kb.AsInt(), Is.EqualTo(0));
        Assert.That(kb.IsZero, Is.True);
    }

    [Test]
    public void KeyBytes8_Roundtrip_SByte_Positive()
    {
        var kb = KeyBytes8.FromSByte(127);
        Assert.That(kb.AsInt(), Is.EqualTo(127));
    }

    [Test]
    public void KeyBytes8_Roundtrip_SByte_Negative()
    {
        var kb = KeyBytes8.FromSByte(-128);
        Assert.That(kb.AsInt(), Is.EqualTo(-128));
    }

    #endregion

    #region KeyBytes8 Roundtrip — Short / UShort

    [Test]
    public void KeyBytes8_Roundtrip_Short_Positive()
    {
        var kb = KeyBytes8.FromShort(32000);
        Assert.That(kb.AsInt(), Is.EqualTo(32000));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Short_Negative()
    {
        var kb = KeyBytes8.FromShort(-32000);
        Assert.That(kb.AsInt(), Is.EqualTo(-32000));
    }

    [Test]
    public void KeyBytes8_Roundtrip_UShort()
    {
        var kb = KeyBytes8.FromUShort(65535);
        Assert.That(kb.AsInt(), Is.EqualTo(65535));
    }

    #endregion

    #region KeyBytes8 Roundtrip — UInt / ULong

    [Test]
    public void KeyBytes8_Roundtrip_UInt()
    {
        var kb = KeyBytes8.FromUInt(uint.MaxValue);
        Assert.That(kb.AsLong(), Is.EqualTo((long)uint.MaxValue));
    }

    [Test]
    public void KeyBytes8_Roundtrip_ULong()
    {
        var kb = KeyBytes8.FromULong(12345678901234UL);
        Assert.That((ulong)kb.AsLong(), Is.EqualTo(12345678901234UL));
    }

    [Test]
    public void KeyBytes8_Roundtrip_ULong_MaxValue()
    {
        var kb = KeyBytes8.FromULong(ulong.MaxValue);
        Assert.That((ulong)kb.AsLong(), Is.EqualTo(ulong.MaxValue));
    }

    #endregion

    #region KeyBytes8 FromPointer

    [Test]
    public unsafe void KeyBytes8_FromPointer_ReadsCorrectBytes()
    {
        long source = 0x0102030405060708L;
        byte* ptr = (byte*)&source;

        var kb = KeyBytes8.FromPointer(ptr, 8);
        Assert.That(kb.AsLong(), Is.EqualTo(0x0102030405060708L));
    }

    [Test]
    public unsafe void KeyBytes8_FromPointer_PartialRead_4Bytes()
    {
        int source = 42;
        byte* ptr = (byte*)&source;

        var kb = KeyBytes8.FromPointer(ptr, 4);
        Assert.That(kb.AsInt(), Is.EqualTo(42));
    }

    [Test]
    public unsafe void KeyBytes8_FromPointer_PartialRead_1Byte()
    {
        byte source = 0xAB;
        byte* ptr = &source;

        var kb = KeyBytes8.FromPointer(ptr, 1);
        Assert.That((byte)kb.AsInt(), Is.EqualTo(0xAB));
    }

    #endregion

    #region KeyBytes8 RawValue

    [Test]
    public void KeyBytes8_RawValue_GetSet()
    {
        var kb = new KeyBytes8();
        kb.RawValue = 0x7FFFFFFFFFFFFFFF;
        Assert.That(kb.RawValue, Is.EqualTo(0x7FFFFFFFFFFFFFFF));
    }

    [Test]
    public void KeyBytes8_RawValue_MatchesAsLong()
    {
        var kb = KeyBytes8.FromLong(42);
        Assert.That(kb.RawValue, Is.EqualTo(kb.AsLong()));
    }

    #endregion
}