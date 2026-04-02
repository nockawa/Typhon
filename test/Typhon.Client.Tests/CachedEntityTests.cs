using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

[TestFixture]
public class CachedEntityTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TestPosition
    {
        public float X;
        public float Y;
        public float Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestHealth
    {
        public int Current;
        public int Max;
    }

    [Test]
    public void Get_RegisteredComponent_ReturnsCorrectValue()
    {
        var pos = new TestPosition { X = 1.5f, Y = 2.5f, Z = 3.5f };
        var bytes = new byte[Marshal.SizeOf<TestPosition>()];
        MemoryMarshal.Write(bytes, in pos);

        var entity = new CachedEntity(42, [new ComponentSnapshot { ComponentId = 1, Data = bytes }]);

        var result = entity.Get<TestPosition>(1);

        Assert.That(result.X, Is.EqualTo(1.5f));
        Assert.That(result.Y, Is.EqualTo(2.5f));
        Assert.That(result.Z, Is.EqualTo(3.5f));
    }

    [Test]
    public void Get_MissingComponent_ThrowsKeyNotFound()
    {
        var entity = new CachedEntity(42, [new ComponentSnapshot { ComponentId = 1, Data = new byte[12] }]);

        Assert.Throws<KeyNotFoundException>(() => entity.Get<TestPosition>(99));
    }

    [Test]
    public void Get_SizeMismatch_ThrowsInvalidOperation()
    {
        // Data is only 4 bytes but TestPosition needs 12
        var entity = new CachedEntity(42, [new ComponentSnapshot { ComponentId = 1, Data = new byte[4] }]);

        Assert.Throws<InvalidOperationException>(() => entity.Get<TestPosition>(1));
    }

    [Test]
    public void TryGet_Present_ReturnsTrueAndValue()
    {
        var health = new TestHealth { Current = 80, Max = 100 };
        var bytes = new byte[Marshal.SizeOf<TestHealth>()];
        MemoryMarshal.Write(bytes, in health);

        var entity = new CachedEntity(42, [new ComponentSnapshot { ComponentId = 2, Data = bytes }]);

        var found = entity.TryGet<TestHealth>(2, out var result);

        Assert.That(found, Is.True);
        Assert.That(result.Current, Is.EqualTo(80));
        Assert.That(result.Max, Is.EqualTo(100));
    }

    [Test]
    public void TryGet_Missing_ReturnsFalse()
    {
        var entity = new CachedEntity(42, [new ComponentSnapshot { ComponentId = 1, Data = new byte[12] }]);

        var found = entity.TryGet<TestPosition>(99, out _);

        Assert.That(found, Is.False);
    }

    [Test]
    public void HasComponent_Present_ReturnsTrue()
    {
        var entity = new CachedEntity(42,
        [
            new ComponentSnapshot { ComponentId = 1, Data = new byte[12] },
            new ComponentSnapshot { ComponentId = 2, Data = new byte[8] }
        ]);

        Assert.That(entity.HasComponent(1), Is.True);
        Assert.That(entity.HasComponent(2), Is.True);
    }

    [Test]
    public void HasComponent_Missing_ReturnsFalse()
    {
        var entity = new CachedEntity(42, [new ComponentSnapshot { ComponentId = 1, Data = new byte[12] }]);

        Assert.That(entity.HasComponent(99), Is.False);
    }

    [Test]
    public void MultipleComponents_GetCorrectOne()
    {
        var pos = new TestPosition { X = 5, Y = 6, Z = 7 };
        var health = new TestHealth { Current = 50, Max = 100 };
        var posBytes = new byte[Marshal.SizeOf<TestPosition>()];
        var healthBytes = new byte[Marshal.SizeOf<TestHealth>()];
        MemoryMarshal.Write(posBytes, in pos);
        MemoryMarshal.Write(healthBytes, in health);

        var entity = new CachedEntity(42,
        [
            new ComponentSnapshot { ComponentId = 1, Data = posBytes },
            new ComponentSnapshot { ComponentId = 2, Data = healthBytes }
        ]);

        Assert.That(entity.Get<TestPosition>(1).X, Is.EqualTo(5f));
        Assert.That(entity.Get<TestHealth>(2).Current, Is.EqualTo(50));
    }
}
