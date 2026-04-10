using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class MortonKeysTests
{
    [Test]
    public void Encode_Decode_RoundTrip_Exhaustive_Small_Range()
    {
        // Exhaustive for 256×256 grid — 65 536 iterations — cheap.
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int key = MortonKeys.Encode2D(x, y);
                var (dx, dy) = MortonKeys.Decode2D(key);
                Assert.That(dx, Is.EqualTo(x), $"x mismatch at ({x},{y}), key={key}");
                Assert.That(dy, Is.EqualTo(y), $"y mismatch at ({x},{y}), key={key}");
            }
        }
    }

    [Test]
    public void Encode_KnownValues()
    {
        // (0,0) -> 0
        Assert.That(MortonKeys.Encode2D(0, 0), Is.EqualTo(0));
        // (1,0) -> 0b01 = 1
        Assert.That(MortonKeys.Encode2D(1, 0), Is.EqualTo(1));
        // (0,1) -> 0b10 = 2
        Assert.That(MortonKeys.Encode2D(0, 1), Is.EqualTo(2));
        // (1,1) -> 0b11 = 3
        Assert.That(MortonKeys.Encode2D(1, 1), Is.EqualTo(3));
        // (2,0) -> 0b0100 = 4
        Assert.That(MortonKeys.Encode2D(2, 0), Is.EqualTo(4));
        // (3,3) -> 0b1111 = 15
        Assert.That(MortonKeys.Encode2D(3, 3), Is.EqualTo(15));
    }

    [Test]
    public void Encode_AllCellsInRegion_AreUnique()
    {
        // Verify Morton produces a bijection on [0, 128) × [0, 128).
        const int dim = 128;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int y = 0; y < dim; y++)
        {
            for (int x = 0; x < dim; x++)
            {
                int key = MortonKeys.Encode2D(x, y);
                Assert.That(seen.Add(key), Is.True, $"Duplicate Morton key {key} at ({x},{y})");
                // Key must fit in [0, dim*dim)
                Assert.That(key, Is.GreaterThanOrEqualTo(0));
                Assert.That(key, Is.LessThan(dim * dim));
            }
        }
        Assert.That(seen.Count, Is.EqualTo(dim * dim));
    }

    [Test]
    public void Scalar_And_Bmi2_Paths_Agree_Exhaustive()
    {
        // Exhaustive parity over 256×256 = 65 536 inputs. The public Encode2D/Decode2D dispatch on
        // Bmi2.IsSupported at runtime, so on BMI2 hardware (most modern CPUs) the scalar branch in
        // those public methods is dead. By directly invoking EncodeScalar2D / DecodeScalar2D we test
        // the fallback code path on every machine, regardless of CPU capabilities. Parity with the
        // BMI2 path is asserted here, and if a future hardware upgrade breaks PDEP/PEXT, the BMI2
        // path will diverge from scalar and the test will catch it.
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int fast = MortonKeys.Encode2D(x, y);
                int scalar = MortonKeys.EncodeScalar2D(x, y);
                Assert.That(scalar, Is.EqualTo(fast), $"encode mismatch at ({x},{y}): scalar={scalar}, bmi2={fast}");

                var (fx, fy) = MortonKeys.Decode2D(fast);
                var (sx, sy) = MortonKeys.DecodeScalar2D(scalar);
                Assert.That(sx, Is.EqualTo(fx), $"decode X mismatch at ({x},{y})");
                Assert.That(sy, Is.EqualTo(fy), $"decode Y mismatch at ({x},{y})");
                Assert.That(sx, Is.EqualTo(x));
                Assert.That(sy, Is.EqualTo(y));
            }
        }
    }

    [Test]
    public void Scalar_RoundTrip_Exhaustive_SmallRange()
    {
        // Dedicated scalar-only round trip — this covers the code path used on non-BMI2 CPUs
        // (theoretical) and the direct call sites inside MortonKeys itself.
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int key = MortonKeys.EncodeScalar2D(x, y);
                var (dx, dy) = MortonKeys.DecodeScalar2D(key);
                Assert.That(dx, Is.EqualTo(x));
                Assert.That(dy, Is.EqualTo(y));
            }
        }
    }

    [Test]
    public void Encode_SpatialLocality_AdjacentCells_KeysNearby()
    {
        // Morton ordering places spatially adjacent cells at keys within a small Hamming / numeric
        // distance. A weak property check — any two adjacent cells in a 32×32 grid differ by no more
        // than a handful of bits in their Morton keys.
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 31; x++)
            {
                int k1 = MortonKeys.Encode2D(x, y);
                int k2 = MortonKeys.Encode2D(x + 1, y);
                int diff = k1 ^ k2;
                Assert.That(System.Numerics.BitOperations.PopCount((uint)diff), Is.LessThanOrEqualTo(6));
            }
        }
    }
}
