using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class FieldEvaluatorTests
{
    // ──────────────────────────────────────────────
    //  Layout
    // ──────────────────────────────────────────────

    [Test]
    public unsafe void SizeOf_FieldEvaluator_Is24Bytes() =>
        Assert.That(sizeof(FieldEvaluator), Is.EqualTo(24));

    // ──────────────────────────────────────────────
    //  Bool
    // ──────────────────────────────────────────────

    [TestCase(true, true, CompareOp.Equal, true)]
    [TestCase(false, false, CompareOp.Equal, true)]
    [TestCase(true, false, CompareOp.Equal, false)]
    [TestCase(true, false, CompareOp.NotEqual, true)]
    [TestCase(false, true, CompareOp.NotEqual, true)]
    [TestCase(true, true, CompareOp.NotEqual, false)]
    [TestCase(true, false, CompareOp.GreaterThan, false)]
    [TestCase(true, false, CompareOp.LessThan, false)]
    [TestCase(true, false, CompareOp.GreaterThanOrEqual, false)]
    [TestCase(true, false, CompareOp.LessThanOrEqual, false)]
    public unsafe void Bool_AllOps(bool value, bool threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Bool,
            CompareOp = op,
            Threshold = threshold ? 1L : 0L
        };

        byte buf = value ? (byte)1 : (byte)0;
        byte* ptr = &buf;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  Byte (unsigned 8-bit)
    // ──────────────────────────────────────────────

    [TestCase((byte)100, (byte)100, CompareOp.Equal, true)]
    [TestCase((byte)100, (byte)101, CompareOp.Equal, false)]
    [TestCase((byte)100, (byte)101, CompareOp.NotEqual, true)]
    [TestCase((byte)100, (byte)100, CompareOp.NotEqual, false)]
    [TestCase((byte)101, (byte)100, CompareOp.GreaterThan, true)]
    [TestCase((byte)100, (byte)100, CompareOp.GreaterThan, false)]
    [TestCase((byte)99, (byte)100, CompareOp.GreaterThan, false)]
    [TestCase((byte)99, (byte)100, CompareOp.LessThan, true)]
    [TestCase((byte)100, (byte)100, CompareOp.LessThan, false)]
    [TestCase((byte)101, (byte)100, CompareOp.LessThan, false)]
    [TestCase((byte)100, (byte)100, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((byte)101, (byte)100, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((byte)99, (byte)100, CompareOp.GreaterThanOrEqual, false)]
    [TestCase((byte)100, (byte)100, CompareOp.LessThanOrEqual, true)]
    [TestCase((byte)99, (byte)100, CompareOp.LessThanOrEqual, true)]
    [TestCase((byte)101, (byte)100, CompareOp.LessThanOrEqual, false)]
    [TestCase((byte)255, (byte)0, CompareOp.GreaterThan, true)]
    [TestCase((byte)0, (byte)255, CompareOp.LessThan, true)]
    [TestCase((byte)255, (byte)255, CompareOp.Equal, true)]
    public unsafe void Byte_AllOps(byte value, byte threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Byte,
            CompareOp = op,
            Threshold = threshold
        };

        byte* ptr = stackalloc byte[1];
        *ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  SByte (signed 8-bit)
    // ──────────────────────────────────────────────

    [TestCase((sbyte)50, (sbyte)50, CompareOp.Equal, true)]
    [TestCase((sbyte)50, (sbyte)51, CompareOp.Equal, false)]
    [TestCase((sbyte)50, (sbyte)51, CompareOp.NotEqual, true)]
    [TestCase((sbyte)51, (sbyte)50, CompareOp.GreaterThan, true)]
    [TestCase((sbyte)50, (sbyte)50, CompareOp.GreaterThan, false)]
    [TestCase((sbyte)49, (sbyte)50, CompareOp.LessThan, true)]
    [TestCase((sbyte)50, (sbyte)50, CompareOp.LessThan, false)]
    [TestCase((sbyte)50, (sbyte)50, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((sbyte)51, (sbyte)50, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((sbyte)49, (sbyte)50, CompareOp.GreaterThanOrEqual, false)]
    [TestCase((sbyte)50, (sbyte)50, CompareOp.LessThanOrEqual, true)]
    [TestCase((sbyte)49, (sbyte)50, CompareOp.LessThanOrEqual, true)]
    [TestCase((sbyte)51, (sbyte)50, CompareOp.LessThanOrEqual, false)]
    [TestCase((sbyte)-1, (sbyte)0, CompareOp.LessThan, true)]
    [TestCase((sbyte)-128, (sbyte)127, CompareOp.LessThan, true)]
    [TestCase((sbyte)127, (sbyte)-128, CompareOp.GreaterThan, true)]
    [TestCase((sbyte)-50, (sbyte)-49, CompareOp.LessThan, true)]
    [TestCase((sbyte)-50, (sbyte)-50, CompareOp.Equal, true)]
    public unsafe void SByte_AllOps(sbyte value, sbyte threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.SByte,
            CompareOp = op,
            Threshold = threshold
        };

        byte* ptr = stackalloc byte[1];
        *(sbyte*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  Short (signed 16-bit)
    // ──────────────────────────────────────────────

    [TestCase((short)1000, (short)1000, CompareOp.Equal, true)]
    [TestCase((short)1000, (short)1001, CompareOp.Equal, false)]
    [TestCase((short)1000, (short)1001, CompareOp.NotEqual, true)]
    [TestCase((short)1001, (short)1000, CompareOp.GreaterThan, true)]
    [TestCase((short)1000, (short)1000, CompareOp.GreaterThan, false)]
    [TestCase((short)999, (short)1000, CompareOp.LessThan, true)]
    [TestCase((short)1000, (short)1000, CompareOp.LessThan, false)]
    [TestCase((short)1000, (short)1000, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((short)1001, (short)1000, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((short)999, (short)1000, CompareOp.GreaterThanOrEqual, false)]
    [TestCase((short)1000, (short)1000, CompareOp.LessThanOrEqual, true)]
    [TestCase((short)999, (short)1000, CompareOp.LessThanOrEqual, true)]
    [TestCase((short)1001, (short)1000, CompareOp.LessThanOrEqual, false)]
    [TestCase((short)-1, (short)0, CompareOp.LessThan, true)]
    [TestCase(short.MinValue, short.MaxValue, CompareOp.LessThan, true)]
    [TestCase(short.MaxValue, short.MinValue, CompareOp.GreaterThan, true)]
    [TestCase((short)-500, (short)-499, CompareOp.LessThan, true)]
    public unsafe void Short_AllOps(short value, short threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Short,
            CompareOp = op,
            Threshold = threshold
        };

        byte* ptr = stackalloc byte[2];
        *(short*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  UShort (unsigned 16-bit)
    // ──────────────────────────────────────────────

    [TestCase((ushort)1000, (ushort)1000, CompareOp.Equal, true)]
    [TestCase((ushort)1000, (ushort)1001, CompareOp.Equal, false)]
    [TestCase((ushort)1000, (ushort)1001, CompareOp.NotEqual, true)]
    [TestCase((ushort)1001, (ushort)1000, CompareOp.GreaterThan, true)]
    [TestCase((ushort)1000, (ushort)1000, CompareOp.GreaterThan, false)]
    [TestCase((ushort)999, (ushort)1000, CompareOp.LessThan, true)]
    [TestCase((ushort)1000, (ushort)1000, CompareOp.LessThan, false)]
    [TestCase((ushort)1000, (ushort)1000, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((ushort)1001, (ushort)1000, CompareOp.GreaterThanOrEqual, true)]
    [TestCase((ushort)999, (ushort)1000, CompareOp.GreaterThanOrEqual, false)]
    [TestCase((ushort)1000, (ushort)1000, CompareOp.LessThanOrEqual, true)]
    [TestCase((ushort)999, (ushort)1000, CompareOp.LessThanOrEqual, true)]
    [TestCase((ushort)1001, (ushort)1000, CompareOp.LessThanOrEqual, false)]
    [TestCase((ushort)65535, (ushort)0, CompareOp.GreaterThan, true)]
    [TestCase((ushort)0, (ushort)65535, CompareOp.LessThan, true)]
    [TestCase((ushort)65535, (ushort)65535, CompareOp.Equal, true)]
    public unsafe void UShort_AllOps(ushort value, ushort threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.UShort,
            CompareOp = op,
            Threshold = threshold
        };

        byte* ptr = stackalloc byte[2];
        *(ushort*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  Int (signed 32-bit)
    // ──────────────────────────────────────────────

    [TestCase(42, 42, CompareOp.Equal, true)]
    [TestCase(42, 43, CompareOp.Equal, false)]
    [TestCase(42, 43, CompareOp.NotEqual, true)]
    [TestCase(42, 42, CompareOp.NotEqual, false)]
    [TestCase(43, 42, CompareOp.GreaterThan, true)]
    [TestCase(42, 42, CompareOp.GreaterThan, false)]
    [TestCase(41, 42, CompareOp.GreaterThan, false)]
    [TestCase(41, 42, CompareOp.LessThan, true)]
    [TestCase(42, 42, CompareOp.LessThan, false)]
    [TestCase(43, 42, CompareOp.LessThan, false)]
    [TestCase(42, 42, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(43, 42, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(41, 42, CompareOp.GreaterThanOrEqual, false)]
    [TestCase(42, 42, CompareOp.LessThanOrEqual, true)]
    [TestCase(41, 42, CompareOp.LessThanOrEqual, true)]
    [TestCase(43, 42, CompareOp.LessThanOrEqual, false)]
    [TestCase(-1, 0, CompareOp.LessThan, true)]
    [TestCase(int.MinValue, int.MaxValue, CompareOp.LessThan, true)]
    [TestCase(int.MaxValue, int.MinValue, CompareOp.GreaterThan, true)]
    [TestCase(-100, -99, CompareOp.LessThan, true)]
    [TestCase(-100, -100, CompareOp.Equal, true)]
    public unsafe void Int_AllOps(int value, int threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Int,
            CompareOp = op,
            Threshold = threshold
        };

        byte* ptr = stackalloc byte[4];
        *(int*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  UInt (unsigned 32-bit)
    // ──────────────────────────────────────────────

    [TestCase(42u, 42u, CompareOp.Equal, true)]
    [TestCase(42u, 43u, CompareOp.Equal, false)]
    [TestCase(42u, 43u, CompareOp.NotEqual, true)]
    [TestCase(43u, 42u, CompareOp.GreaterThan, true)]
    [TestCase(42u, 42u, CompareOp.GreaterThan, false)]
    [TestCase(41u, 42u, CompareOp.LessThan, true)]
    [TestCase(42u, 42u, CompareOp.LessThan, false)]
    [TestCase(42u, 42u, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(43u, 42u, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(41u, 42u, CompareOp.GreaterThanOrEqual, false)]
    [TestCase(42u, 42u, CompareOp.LessThanOrEqual, true)]
    [TestCase(41u, 42u, CompareOp.LessThanOrEqual, true)]
    [TestCase(43u, 42u, CompareOp.LessThanOrEqual, false)]
    [TestCase(uint.MaxValue, 0u, CompareOp.GreaterThan, true)]
    [TestCase(0u, uint.MaxValue, CompareOp.LessThan, true)]
    [TestCase(uint.MaxValue, uint.MaxValue, CompareOp.Equal, true)]
    public unsafe void UInt_AllOps(uint value, uint threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.UInt,
            CompareOp = op,
            Threshold = (long)threshold
        };

        byte* ptr = stackalloc byte[4];
        *(uint*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  Long (signed 64-bit)
    // ──────────────────────────────────────────────

    [TestCase(1000L, 1000L, CompareOp.Equal, true)]
    [TestCase(1000L, 1001L, CompareOp.Equal, false)]
    [TestCase(1000L, 1001L, CompareOp.NotEqual, true)]
    [TestCase(1001L, 1000L, CompareOp.GreaterThan, true)]
    [TestCase(1000L, 1000L, CompareOp.GreaterThan, false)]
    [TestCase(999L, 1000L, CompareOp.LessThan, true)]
    [TestCase(1000L, 1000L, CompareOp.LessThan, false)]
    [TestCase(1000L, 1000L, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(1001L, 1000L, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(999L, 1000L, CompareOp.GreaterThanOrEqual, false)]
    [TestCase(1000L, 1000L, CompareOp.LessThanOrEqual, true)]
    [TestCase(999L, 1000L, CompareOp.LessThanOrEqual, true)]
    [TestCase(1001L, 1000L, CompareOp.LessThanOrEqual, false)]
    [TestCase(-1L, 0L, CompareOp.LessThan, true)]
    [TestCase(long.MinValue, long.MaxValue, CompareOp.LessThan, true)]
    [TestCase(long.MaxValue, long.MinValue, CompareOp.GreaterThan, true)]
    [TestCase(-100L, -99L, CompareOp.LessThan, true)]
    [TestCase(-100L, -100L, CompareOp.Equal, true)]
    public unsafe void Long_AllOps(long value, long threshold, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Long,
            CompareOp = op,
            Threshold = threshold
        };

        byte* ptr = stackalloc byte[8];
        *(long*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    // ──────────────────────────────────────────────
    //  ULong (unsigned 64-bit)
    // ──────────────────────────────────────────────

    [TestCase(1000UL, 1000L, CompareOp.Equal, true)]
    [TestCase(1000UL, 1001L, CompareOp.Equal, false)]
    [TestCase(1000UL, 1001L, CompareOp.NotEqual, true)]
    [TestCase(1001UL, 1000L, CompareOp.GreaterThan, true)]
    [TestCase(1000UL, 1000L, CompareOp.GreaterThan, false)]
    [TestCase(999UL, 1000L, CompareOp.LessThan, true)]
    [TestCase(1000UL, 1000L, CompareOp.LessThan, false)]
    [TestCase(1000UL, 1000L, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(1001UL, 1000L, CompareOp.GreaterThanOrEqual, true)]
    [TestCase(999UL, 1000L, CompareOp.GreaterThanOrEqual, false)]
    [TestCase(1000UL, 1000L, CompareOp.LessThanOrEqual, true)]
    [TestCase(999UL, 1000L, CompareOp.LessThanOrEqual, true)]
    [TestCase(1001UL, 1000L, CompareOp.LessThanOrEqual, false)]
    [TestCase(0UL, 0L, CompareOp.Equal, true)]
    public unsafe void ULong_AllOps(ulong value, long thresholdAsLong, CompareOp op, bool expected)
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.ULong,
            CompareOp = op,
            Threshold = thresholdAsLong
        };

        byte* ptr = stackalloc byte[8];
        *(ulong*)ptr = value;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.EqualTo(expected));
    }

    [Test]
    public unsafe void ULong_LargeValues()
    {
        // ulong.MaxValue stored as long bit pattern
        var maxVal = ulong.MaxValue;
        var thresholdBits = Unsafe.As<ulong, long>(ref maxVal);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.ULong,
            CompareOp = CompareOp.Equal,
            Threshold = thresholdBits
        };

        byte* ptr = stackalloc byte[8];
        *(ulong*)ptr = ulong.MaxValue;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);

        // MaxValue > 0
        eval.CompareOp = CompareOp.GreaterThan;
        eval.Threshold = 0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);

        // 0 < MaxValue
        *(ulong*)ptr = 0UL;
        eval.CompareOp = CompareOp.LessThan;
        eval.Threshold = thresholdBits;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);
    }

    // ──────────────────────────────────────────────
    //  Float (32-bit IEEE 754)
    // ──────────────────────────────────────────────

    [Test]
    public unsafe void Float_Equal_AtThreshold()
    {
        var thr = 3.14f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.Equal,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];
        *(float*)ptr = 3.14f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);

        *(float*)ptr = 3.15f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void Float_NotEqual()
    {
        var thr = 1.0f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.NotEqual,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];
        *(float*)ptr = 2.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);

        *(float*)ptr = 1.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void Float_GreaterThan_BoundaryValues()
    {
        var thr = 10.0f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.GreaterThan,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];

        *(float*)ptr = 10.001f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Above threshold");

        *(float*)ptr = 10.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "At threshold");

        *(float*)ptr = 9.999f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Below threshold");
    }

    [Test]
    public unsafe void Float_LessThan_BoundaryValues()
    {
        var thr = 10.0f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.LessThan,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];

        *(float*)ptr = 9.999f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Below threshold");

        *(float*)ptr = 10.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "At threshold");

        *(float*)ptr = 10.001f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Above threshold");
    }

    [Test]
    public unsafe void Float_GreaterThanOrEqual_BoundaryValues()
    {
        var thr = 5.0f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.GreaterThanOrEqual,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];

        *(float*)ptr = 5.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "At threshold");

        *(float*)ptr = 5.001f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Above threshold");

        *(float*)ptr = 4.999f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Below threshold");
    }

    [Test]
    public unsafe void Float_LessThanOrEqual_BoundaryValues()
    {
        var thr = 5.0f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.LessThanOrEqual,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];

        *(float*)ptr = 5.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "At threshold");

        *(float*)ptr = 4.999f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Below threshold");

        *(float*)ptr = 5.001f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Above threshold");
    }

    [Test]
    public unsafe void Float_NegativeValues()
    {
        var thr = -5.0f;
        var thrBits = (long)Unsafe.As<float, int>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Float,
            CompareOp = CompareOp.LessThan,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[4];

        *(float*)ptr = -6.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "-6 < -5");

        *(float*)ptr = -5.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "-5 not < -5");

        *(float*)ptr = -4.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "-4 not < -5");

        eval.CompareOp = CompareOp.GreaterThan;
        *(float*)ptr = -4.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "-4 > -5");

        *(float*)ptr = -6.0f;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "-6 not > -5");
    }

    // ──────────────────────────────────────────────
    //  Double (64-bit IEEE 754)
    // ──────────────────────────────────────────────

    [Test]
    public unsafe void Double_Equal_AtThreshold()
    {
        var thr = 3.141592653589793;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.Equal,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];
        *(double*)ptr = 3.141592653589793;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);

        *(double*)ptr = 3.14;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void Double_NotEqual()
    {
        var thr = 1.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.NotEqual,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];
        *(double*)ptr = 2.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True);

        *(double*)ptr = 1.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void Double_GreaterThan_BoundaryValues()
    {
        var thr = 100.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.GreaterThan,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];

        *(double*)ptr = 100.001;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Above threshold");

        *(double*)ptr = 100.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "At threshold");

        *(double*)ptr = 99.999;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Below threshold");
    }

    [Test]
    public unsafe void Double_LessThan_BoundaryValues()
    {
        var thr = 100.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.LessThan,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];

        *(double*)ptr = 99.999;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Below threshold");

        *(double*)ptr = 100.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "At threshold");

        *(double*)ptr = 100.001;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Above threshold");
    }

    [Test]
    public unsafe void Double_GreaterThanOrEqual_BoundaryValues()
    {
        var thr = 50.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.GreaterThanOrEqual,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];

        *(double*)ptr = 50.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "At threshold");

        *(double*)ptr = 50.001;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Above threshold");

        *(double*)ptr = 49.999;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Below threshold");
    }

    [Test]
    public unsafe void Double_LessThanOrEqual_BoundaryValues()
    {
        var thr = 50.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.LessThanOrEqual,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];

        *(double*)ptr = 50.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "At threshold");

        *(double*)ptr = 49.999;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "Below threshold");

        *(double*)ptr = 50.001;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "Above threshold");
    }

    [Test]
    public unsafe void Double_NegativeValues()
    {
        var thr = -10.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = CompareOp.LessThan,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];

        *(double*)ptr = -11.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "-11 < -10");

        *(double*)ptr = -10.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "-10 not < -10");

        *(double*)ptr = -9.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "-9 not < -10");

        eval.CompareOp = CompareOp.GreaterThan;
        *(double*)ptr = -9.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.True, "-9 > -10");

        *(double*)ptr = -11.0;
        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False, "-11 not > -10");
    }

    // ──────────────────────────────────────────────
    //  Edge cases
    // ──────────────────────────────────────────────

    [Test]
    public unsafe void InvalidKeyType_ReturnsFalse()
    {
        var eval = new FieldEvaluator
        {
            KeyType = (KeyType)255,
            CompareOp = CompareOp.Equal,
            Threshold = 0
        };

        byte* ptr = stackalloc byte[8];
        *(long*)ptr = 0;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void InvalidCompareOp_SignedPath_ReturnsFalse()
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Int,
            CompareOp = (CompareOp)255,
            Threshold = 42
        };

        byte* ptr = stackalloc byte[4];
        *(int*)ptr = 42;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void InvalidCompareOp_UnsignedPath_ReturnsFalse()
    {
        var eval = new FieldEvaluator
        {
            KeyType = KeyType.UInt,
            CompareOp = (CompareOp)255,
            Threshold = 42
        };

        byte* ptr = stackalloc byte[4];
        *(uint*)ptr = 42;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }

    [Test]
    public unsafe void InvalidCompareOp_FloatPath_ReturnsFalse()
    {
        var thr = 1.0;
        var thrBits = Unsafe.As<double, long>(ref thr);

        var eval = new FieldEvaluator
        {
            KeyType = KeyType.Double,
            CompareOp = (CompareOp)255,
            Threshold = thrBits
        };

        byte* ptr = stackalloc byte[8];
        *(double*)ptr = 1.0;

        Assert.That(FieldEvaluator.Evaluate(ref eval, ptr), Is.False);
    }
}
