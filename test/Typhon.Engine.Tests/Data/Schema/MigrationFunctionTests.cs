using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test component structs for migration functions ──
// Each version has the same [Component] Name but different Revision.
// Breaking changes require fields with the SAME FieldId but incompatible type (e.g., int → float).
// Note: all structs must be at least 8 bytes (minimum chunk stride).

#region Single-step: int Health → float Health (type change = breaking)

[Component("Typhon.Schema.UnitTest.MigPlayer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MigPlayerV1
{
    public int Health;   // FieldId 0
    public int Mana;     // FieldId 1

    public MigPlayerV1(int health, int mana) { Health = health; Mana = mana; }
}

[Component("Typhon.Schema.UnitTest.MigPlayer", 2)]
[StructLayout(LayoutKind.Sequential)]
struct MigPlayerV2
{
    public float Health;  // FieldId 0 — same name, int→float = BREAKING
    public int Mana;      // FieldId 1 — unchanged
    public int Shield;    // FieldId 2 — new field

    public MigPlayerV2(float health, int mana, int shield) { Health = health; Mana = mana; Shield = shield; }
}

#endregion

#region Chained: V1→V2→V3

[Component("Typhon.Schema.UnitTest.MigChain", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MigChainV1
{
    public int Value;    // FieldId 0
    public int Score;    // FieldId 1

    public MigChainV1(int value, int score) { Value = value; Score = score; }
}

[Component("Typhon.Schema.UnitTest.MigChain", 2)]
[StructLayout(LayoutKind.Sequential)]
struct MigChainV2
{
    public float Value;   // FieldId 0 — int→float = BREAKING
    public int Score;     // FieldId 1 — unchanged
    public int Bonus;     // FieldId 2 — new field

    public MigChainV2(float value, int score, int bonus) { Value = value; Score = score; Bonus = bonus; }
}

[Component("Typhon.Schema.UnitTest.MigChain", 3)]
[StructLayout(LayoutKind.Sequential)]
struct MigChainV3
{
    public double Value;      // FieldId 0 — float→double (would be widening, but the whole chain is driven by migration functions)
    public long TotalScore;   // FieldId 3 — new field (Score + Bonus merged)

    public MigChainV3(double value, long totalScore) { Value = value; TotalScore = totalScore; }
}

#endregion

#region Byte-level migration: int A → float A (type change = breaking)

[Component("Typhon.Schema.UnitTest.MigByte", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MigByteV1
{
    public int A;    // FieldId 0
    public int B;    // FieldId 1

    public MigByteV1(int a, int b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.MigByte", 2)]
[StructLayout(LayoutKind.Sequential)]
struct MigByteV2
{
    public float A;   // FieldId 0 — int→float = BREAKING
    public int B;     // FieldId 1 — unchanged

    public MigByteV2(float a, int b) { A = a; B = b; }
}

#endregion

#region Migration failure test (same name, type change = breaking)

[Component("Typhon.Schema.UnitTest.MigFail", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MigFailV1
{
    public int Value;     // FieldId 0
    public int Padding;   // FieldId 1 — ensures >= 8 bytes

    public MigFailV1(int value) { Value = value; Padding = 0; }
}

[Component("Typhon.Schema.UnitTest.MigFail", 2)]
[StructLayout(LayoutKind.Sequential)]
struct MigFailV2
{
    public float Value;   // FieldId 0 — int→float = BREAKING
    public int Padding;   // FieldId 1

    public MigFailV2(float value) { Value = value; Padding = 0; }
}

#endregion

#region Missing migration test (same name, type change = breaking, no migration registered)

[Component("Typhon.Schema.UnitTest.MigMissing", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MigMissingV1
{
    public int X;     // FieldId 0
    public int Y;     // FieldId 1 — ensures >= 8 bytes

    public MigMissingV1(int x) { X = x; Y = 0; }
}

[Component("Typhon.Schema.UnitTest.MigMissing", 2)]
[StructLayout(LayoutKind.Sequential)]
struct MigMissingV2
{
    public float X;   // FieldId 0 — int→float = BREAKING
    public int Y;     // FieldId 1

    public MigMissingV2(float x) { X = x; Y = 0; }
}

#endregion

/// <summary>
/// Tests for Phase 4: Migration Functions.
/// Verifies user-defined migration functions for breaking schema changes, chaining, error handling, and byte-level API.
/// </summary>
class MigrationFunctionTests : TestBase<MigrationFunctionTests>
{
    [Test]
    public void SingleStepMigration_SemanticConversion_AllEntitiesTransformed()
    {
        long pk1, pk2, pk3;

        // Phase 1: Create database with V1, populate multiple entities
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MigPlayerV1>();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            var c1 = new MigPlayerV1(100, 50);
            var c2 = new MigPlayerV1(75, 200);
            var c3 = new MigPlayerV1(0, 0);
            pk1 = t.CreateEntity(ref c1);
            pk2 = t.CreateEntity(ref c2);
            pk3 = t.CreateEntity(ref c3);
            t.Commit();
        }

        // Phase 2: Register migration, reopen with V2
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            // int Health → float Health (semantic conversion: raw int value → float)
            dbe.RegisterMigration<MigPlayerV1, MigPlayerV2>((ref MigPlayerV1 old, out MigPlayerV2 new_) =>
            {
                new_ = new MigPlayerV2
                {
                    Health = old.Health / 100f,  // int 100 → float 1.0
                    Mana = old.Mana,
                    Shield = 0,
                };
            });

            dbe.RegisterComponentFromAccessor<MigPlayerV2>();

            using var t = dbe.CreateQuickTransaction();

            Assert.That(t.ReadEntity<MigPlayerV2>(pk1, out var r1), Is.True);
            Assert.That(r1.Health, Is.EqualTo(1.0f));
            Assert.That(r1.Mana, Is.EqualTo(50));
            Assert.That(r1.Shield, Is.EqualTo(0));

            Assert.That(t.ReadEntity<MigPlayerV2>(pk2, out var r2), Is.True);
            Assert.That(r2.Health, Is.EqualTo(0.75f));
            Assert.That(r2.Mana, Is.EqualTo(200));

            Assert.That(t.ReadEntity<MigPlayerV2>(pk3, out var r3), Is.True);
            Assert.That(r3.Health, Is.EqualTo(0.0f));
            Assert.That(r3.Mana, Is.EqualTo(0));
        }
    }

    [Test]
    public void ChainedMigration_ThreeSteps_FinalValuesCorrect()
    {
        long pk;

        // Phase 1: Create with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MigChainV1>();

            var comp = new MigChainV1(42, 100);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            pk = t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Register chain V1→V2→V3, reopen with V3
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            dbe.RegisterMigration<MigChainV1, MigChainV2>((ref MigChainV1 old, out MigChainV2 new_) =>
            {
                new_ = new MigChainV2
                {
                    Value = old.Value,
                    Score = old.Score,
                    Bonus = 10,
                };
            });

            dbe.RegisterMigration<MigChainV2, MigChainV3>((ref MigChainV2 old, out MigChainV3 new_) =>
            {
                new_ = new MigChainV3
                {
                    Value = old.Value,
                    TotalScore = old.Score + old.Bonus,
                };
            });

            dbe.RegisterComponentFromAccessor<MigChainV3>();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity<MigChainV3>(pk, out var result), Is.True);
            Assert.That(result.Value, Is.EqualTo(42.0));
            Assert.That(result.TotalScore, Is.EqualTo(110L)); // 100 + 10
        }
    }

    [Test]
    public void MigrationFunctionThrows_EntityLogged_ThrowsSchemaMigrationException()
    {
        long pk1, pk2;

        // Phase 1: Create with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MigFailV1>();

            var c1 = new MigFailV1(42);
            var c2 = new MigFailV1(-1); // This one will trigger the migration failure
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            pk1 = t.CreateEntity(ref c1);
            pk2 = t.CreateEntity(ref c2);
            t.Commit();
        }

        // Phase 2: Register a migration that throws for negative values
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            dbe.RegisterMigration<MigFailV1, MigFailV2>((ref MigFailV1 old, out MigFailV2 new_) =>
            {
                if (old.Value < 0)
                {
                    throw new InvalidOperationException("Cannot convert negative value");
                }

                new_ = new MigFailV2 { Value = old.Value * 2.0f };
            });

            var ex = Assert.Throws<SchemaMigrationException>(() =>
                dbe.RegisterComponentFromAccessor<MigFailV2>());

            Assert.That(ex.ComponentName, Is.EqualTo("Typhon.Schema.UnitTest.MigFail"));
            Assert.That(ex.FailedEntityCount, Is.EqualTo(1));
            Assert.That(ex.Failures[0].Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(ex.Failures[0].OldDataHex, Is.Not.Empty);
        }
    }

    [Test]
    public void MissingMigration_ThrowsSchemaValidationException()
    {
        // Phase 1: Create with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MigMissingV1>();

            var comp = new MigMissingV1(42);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Reopen with V2 but NO migration registered
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            var ex = Assert.Throws<SchemaValidationException>(() =>
                dbe.RegisterComponentFromAccessor<MigMissingV2>());

            Assert.That(ex.Diff.HasBreakingChanges, Is.True);
        }
    }

    [Test]
    public void ByteLevelMigration_RawByteTransformation()
    {
        long pk;

        // Phase 1: Create with V1 (int A=100, int B=42)
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MigByteV1>();

            var comp = new MigByteV1(100, 42);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            pk = t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Register byte-level migration (int A → float A), reopen with V2
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            dbe.RegisterByteMigration("Typhon.Schema.UnitTest.MigByte", 1, 2,
                Unsafe.SizeOf<MigByteV1>(), Unsafe.SizeOf<MigByteV2>(),
                (ReadOnlySpan<byte> old, Span<byte> new_) =>
                {
                    // Convert first 4 bytes from int to float, copy remaining 4 bytes as-is
                    var intVal = BitConverter.ToInt32(old.Slice(0, 4));
                    BitConverter.TryWriteBytes(new_.Slice(0, 4), (float)intVal);
                    old.Slice(4, 4).CopyTo(new_.Slice(4, 4));
                });

            dbe.RegisterComponentFromAccessor<MigByteV2>();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity<MigByteV2>(pk, out var result), Is.True);
            Assert.That(result.A, Is.EqualTo(100.0f));
            Assert.That(result.B, Is.EqualTo(42));
        }
    }

    [Test]
    public void DuplicateRegistration_Throws()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        dbe.RegisterMigration<MigPlayerV1, MigPlayerV2>((ref MigPlayerV1 old, out MigPlayerV2 new_) =>
        {
            new_ = new MigPlayerV2 { Health = old.Health / 100f, Mana = old.Mana };
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            dbe.RegisterMigration<MigPlayerV1, MigPlayerV2>((ref MigPlayerV1 old, out MigPlayerV2 new_) =>
            {
                new_ = new MigPlayerV2 { Health = old.Health / 100f, Mana = old.Mana };
            });
        });
    }

    [Test]
    public void MigrationAfterMigration_CanCreateNewEntities()
    {
        long pk1;

        // Phase 1: Create with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MigPlayerV1>();

            var comp = new MigPlayerV1(80, 300);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            pk1 = t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Migrate to V2, then create new V2 entities
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            dbe.RegisterMigration<MigPlayerV1, MigPlayerV2>((ref MigPlayerV1 old, out MigPlayerV2 new_) =>
            {
                new_ = new MigPlayerV2
                {
                    Health = old.Health / 100f,
                    Mana = old.Mana,
                    Shield = 0,
                };
            });

            dbe.RegisterComponentFromAccessor<MigPlayerV2>();

            // Create new V2 entity
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            var newComp = new MigPlayerV2(0.5f, 999, 50);
            var pk2 = t.CreateEntity(ref newComp);
            t.Commit();

            // Verify both old (migrated) and new entities
            using var t2 = dbe.CreateQuickTransaction();
            Assert.That(t2.ReadEntity<MigPlayerV2>(pk1, out var r1), Is.True);
            Assert.That(r1.Health, Is.EqualTo(0.8f));
            Assert.That(r1.Mana, Is.EqualTo(300));

            Assert.That(t2.ReadEntity<MigPlayerV2>(pk2, out var r2), Is.True);
            Assert.That(r2.Health, Is.EqualTo(0.5f));
            Assert.That(r2.Mana, Is.EqualTo(999));
            Assert.That(r2.Shield, Is.EqualTo(50));
        }
    }
}
