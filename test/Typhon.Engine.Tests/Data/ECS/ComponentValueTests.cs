using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

unsafe class ComponentValueTests
{
    [StructLayout(LayoutKind.Sequential)]
    struct SimpleComp
    {
        public int X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LargeComp
    {
        public long A, B, C, D, E, F, G, H;     // 64 bytes
        public long I, J, K, L, M, N;            // 48 bytes = 112 total (max payload)
    }

    [Test]
    public void SizeOf_128Bytes()
    {
        Assert.That(sizeof(ComponentValue), Is.EqualTo(128));
    }

    [Test]
    public void Create_RoundTrip_SimpleStruct()
    {
        var value = new SimpleComp { X = 42, Y = 3.14f };
        var cv = ComponentValue.Create(7, in value);

        Assert.That(cv.ComponentTypeId, Is.EqualTo(7));
        Assert.That(cv.DataSize, Is.EqualTo(sizeof(SimpleComp)));

        var read = cv.Read<SimpleComp>();
        Assert.That(read.X, Is.EqualTo(42));
        Assert.That(read.Y, Is.EqualTo(3.14f).Within(0.001f));
    }

    [Test]
    public void Create_RoundTrip_Int()
    {
        int value = 999;
        var cv = ComponentValue.Create(1, in value);

        Assert.That(cv.DataSize, Is.EqualTo(4));
        Assert.That(cv.Read<int>(), Is.EqualTo(999));
    }

    [Test]
    public void Create_RoundTrip_MaxPayload()
    {
        var value = new LargeComp
        {
            A = 1, B = 2, C = 3, D = 4, E = 5, F = 6, G = 7, H = 8,
            I = 9, J = 10, K = 11, L = 12, M = 13, N = 14,
        };

        Assert.That(sizeof(LargeComp), Is.EqualTo(112)); // exactly max payload
        var cv = ComponentValue.Create(0, in value);

        var read = cv.Read<LargeComp>();
        Assert.That(read.A, Is.EqualTo(1));
        Assert.That(read.N, Is.EqualTo(14));
    }

    [Test]
    public void CompHandle_Set_CreatesComponentValue()
    {
        var comp = new Comp<SimpleComp>(5);
        var value = new SimpleComp { X = 100, Y = 200.0f };
        var cv = comp.Set(in value);

        Assert.That(cv.ComponentTypeId, Is.EqualTo(5));
        Assert.That(cv.DataSize, Is.EqualTo(sizeof(SimpleComp)));

        var read = cv.Read<SimpleComp>();
        Assert.That(read.X, Is.EqualTo(100));
    }

    [Test]
    public void CompHandle_Default_ZeroInitialized()
    {
        var comp = new Comp<SimpleComp>(2);
        var cv = comp.Default();

        var read = cv.Read<SimpleComp>();
        Assert.That(read.X, Is.EqualTo(0));
        Assert.That(read.Y, Is.EqualTo(0.0f));
    }
}
