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
    public unsafe void SizeOf_FieldEvaluator_Is16Bytes() =>
        Assert.That(sizeof(FieldEvaluator), Is.EqualTo(16));

    // ──────────────────────────────────────────────
    //  Int (signed 32-bit) — representative signed integer
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
    //  UInt (unsigned 32-bit) — representative unsigned integer
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
    //  Double (64-bit IEEE 754) — representative floating point
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
