using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test structs for registry validation tests ──

#region Valid pair (same name, increasing revision)

[Component("Typhon.Schema.UnitTest.RegPair", 1)]
[StructLayout(LayoutKind.Sequential)]
struct RegPairV1
{
    public int A;
}

[Component("Typhon.Schema.UnitTest.RegPair", 2)]
[StructLayout(LayoutKind.Sequential)]
struct RegPairV2
{
    public float A;
}

[Component("Typhon.Schema.UnitTest.RegPair", 3)]
[StructLayout(LayoutKind.Sequential)]
struct RegPairV3
{
    public double A;
}

#endregion

#region Name mismatch

[Component("Typhon.Schema.UnitTest.RegNameA", 1)]
[StructLayout(LayoutKind.Sequential)]
struct RegNameA
{
    public int X;
}

[Component("Typhon.Schema.UnitTest.RegNameB", 2)]
[StructLayout(LayoutKind.Sequential)]
struct RegNameB
{
    public float X;
}

#endregion

#region Revision not increasing

[Component("Typhon.Schema.UnitTest.RegRevBad", 2)]
[StructLayout(LayoutKind.Sequential)]
struct RegRevBadV2
{
    public int X;
}

[Component("Typhon.Schema.UnitTest.RegRevBad", 1)]
[StructLayout(LayoutKind.Sequential)]
struct RegRevBadV1
{
    public float X;
}

#endregion

/// <summary>
/// Unit tests for <see cref="MigrationRegistry"/> — validation, chain resolution, and error cases.
/// These tests exercise the registry in isolation without a database.
/// </summary>
class MigrationRegistryTests
{
    [Test]
    public void Register_ValidPair_Succeeds()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });

        Assert.That(registry.Count, Is.EqualTo(1));
    }

    [Test]
    public void Register_NameMismatch_Throws()
    {
        var registry = new MigrationRegistry();
        Assert.Throws<ArgumentException>(() =>
        {
            registry.Register<RegNameA, RegNameB>((ref RegNameA old, out RegNameB new_) =>
            {
                new_ = new RegNameB { X = old.X };
            });
        });
    }

    [Test]
    public void Register_RevisionNotIncreasing_Throws()
    {
        var registry = new MigrationRegistry();
        Assert.Throws<ArgumentException>(() =>
        {
            registry.Register<RegRevBadV2, RegRevBadV1>((ref RegRevBadV2 old, out RegRevBadV1 new_) =>
            {
                new_ = new RegRevBadV1 { X = old.X };
            });
        });
    }

    [Test]
    public void Register_Duplicate_Throws()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
            {
                new_ = new RegPairV2 { A = old.A * 2 };
            });
        });
    }

    [Test]
    public void GetChain_DirectMatch_ReturnsSingleStep()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });

        var chain = registry.GetChain("Typhon.Schema.UnitTest.RegPair", 1, 2);
        Assert.That(chain, Is.Not.Null);
        Assert.That(chain.Value.StepCount, Is.EqualTo(1));
        Assert.That(chain.Value.Steps[0].FromRevision, Is.EqualTo(1));
        Assert.That(chain.Value.Steps[0].ToRevision, Is.EqualTo(2));
    }

    [Test]
    public void GetChain_MultiStep_ReturnsOrderedChain()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });
        registry.Register<RegPairV2, RegPairV3>((ref RegPairV2 old, out RegPairV3 new_) =>
        {
            new_ = new RegPairV3 { A = old.A };
        });

        var chain = registry.GetChain("Typhon.Schema.UnitTest.RegPair", 1, 3);
        Assert.That(chain, Is.Not.Null);
        Assert.That(chain.Value.StepCount, Is.EqualTo(2));
        Assert.That(chain.Value.Steps[0].FromRevision, Is.EqualTo(1));
        Assert.That(chain.Value.Steps[0].ToRevision, Is.EqualTo(2));
        Assert.That(chain.Value.Steps[1].FromRevision, Is.EqualTo(2));
        Assert.That(chain.Value.Steps[1].ToRevision, Is.EqualTo(3));
    }

    [Test]
    public void GetChain_NoPath_ReturnsNull()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });

        // No path from 2 to 3
        var chain = registry.GetChain("Typhon.Schema.UnitTest.RegPair", 2, 3);
        Assert.That(chain, Is.Null);
    }

    [Test]
    public void GetChain_UnknownComponent_ReturnsNull()
    {
        var registry = new MigrationRegistry();
        var chain = registry.GetChain("Nonexistent.Component", 1, 2);
        Assert.That(chain, Is.Null);
    }

    [Test]
    public void RegisterByte_ValidArgs_Succeeds()
    {
        var registry = new MigrationRegistry();
        registry.RegisterByte("Test.Component", 1, 2, 8, 16,
            (ReadOnlySpan<byte> old, Span<byte> new_) =>
            {
                old.CopyTo(new_);
            });

        Assert.That(registry.Count, Is.EqualTo(1));

        var direct = registry.GetDirect("Test.Component", 1, 2);
        Assert.That(direct, Is.Not.Null);
        Assert.That(direct.OldSize, Is.EqualTo(8));
        Assert.That(direct.NewSize, Is.EqualTo(16));
    }

    [Test]
    public void RegisterByte_RevisionNotIncreasing_Throws()
    {
        var registry = new MigrationRegistry();
        Assert.Throws<ArgumentException>(() =>
        {
            registry.RegisterByte("Test.Component", 2, 1, 8, 16,
                (ReadOnlySpan<byte> old, Span<byte> new_) => { old.CopyTo(new_); });
        });
    }

    [Test]
    public void MaxIntermediateSize_ComputedCorrectly()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });
        registry.Register<RegPairV2, RegPairV3>((ref RegPairV2 old, out RegPairV3 new_) =>
        {
            new_ = new RegPairV3 { A = old.A };
        });

        var chain = registry.GetChain("Typhon.Schema.UnitTest.RegPair", 1, 3);
        Assert.That(chain, Is.Not.Null);

        // Max size should be the largest of all old/new sizes in the chain
        var expectedMax = Math.Max(
            Math.Max(Unsafe.SizeOf<RegPairV1>(), Unsafe.SizeOf<RegPairV2>()),
            Math.Max(Unsafe.SizeOf<RegPairV2>(), Unsafe.SizeOf<RegPairV3>()));
        Assert.That(chain.Value.MaxIntermediateSize, Is.EqualTo(expectedMax));
    }

    [Test]
    public void MigrationFunctionThrowsOnZeroInit_RegistrationFails()
    {
        var registry = new MigrationRegistry();
        Assert.Throws<InvalidOperationException>(() =>
        {
            registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
            {
                // This always throws — should fail sanity check during registration
                throw new DivideByZeroException("bad migration");
            });
        });
    }

    [Test]
    public void HasMigrationsFor_RegisteredComponent_ReturnsTrue()
    {
        var registry = new MigrationRegistry();
        registry.Register<RegPairV1, RegPairV2>((ref RegPairV1 old, out RegPairV2 new_) =>
        {
            new_ = new RegPairV2 { A = old.A };
        });

        Assert.That(registry.HasMigrationsFor("Typhon.Schema.UnitTest.RegPair"), Is.True);
        Assert.That(registry.HasMigrationsFor("Nonexistent.Component"), Is.False);
    }
}
