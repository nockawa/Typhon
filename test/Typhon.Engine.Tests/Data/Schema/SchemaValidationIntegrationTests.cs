using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test component structs for schema validation integration testing ──
// Each V1/V2 pair shares the same [Component] name but has different layouts.

[Component("Typhon.Schema.UnitTest.SchemaWiden", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompWidenV1
{
    public int Score;
    public float Speed;

    public CompWidenV1(int score, float speed) { Score = score; Speed = speed; }
}

[Component("Typhon.Schema.UnitTest.SchemaWiden", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompWidenV2
{
    public long Score;
    public double Speed;

    public CompWidenV2(long score, double speed) { Score = score; Speed = speed; }
}

[Component("Typhon.Schema.UnitTest.SchemaBreak", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompBreakV1
{
    public int Score;
    public int Padding;

    public CompBreakV1(int score) { Score = score; Padding = 0; }
}

[Component("Typhon.Schema.UnitTest.SchemaBreak", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompBreakV2
{
    public String64 Score;
    public int Padding;
}

[Component("Typhon.Schema.UnitTest.SchemaFieldAdd", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompFieldAddV1
{
    public int A;
    public float B;

    public CompFieldAddV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.SchemaFieldAdd", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompFieldAddV2
{
    public int A;
    public int C;
    public float B;

    public CompFieldAddV2(int a, int c, float b) { A = a; C = c; B = b; }
}

/// <summary>
/// Integration tests verifying schema validation across database reopen cycles.
/// Each test creates a database with one component layout, closes it, then reopens with a different layout.
/// </summary>
class SchemaValidationIntegrationTests : TestBase<SchemaValidationIntegrationTests>
{
    [Test]
    public void ReopenIdentical_NoError()
    {
        long entityId;

        // Phase 1: Create database with V1
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompWidenV1>();

            var comp = new CompWidenV1(42, 3.14f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Reopen with SAME schema — no exception
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.DoesNotThrow(() => dbe.RegisterComponentFromAccessor<CompWidenV1>());

            // Verify data is intact
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity<CompWidenV1>(entityId, out var comp), Is.True);
            Assert.That(comp.Score, Is.EqualTo(42));
            Assert.That(comp.Speed, Is.EqualTo(3.14f));
        }
    }

    [Test]
    public void ReopenWithWidening_AllowsRegistration()
    {
        // Phase 1: Create database with V1 (int Score, float Speed)
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompWidenV1>();

            var comp = new CompWidenV1(100, 2.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Reopen with V2 (long Score, double Speed) — widening, should not throw
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.DoesNotThrow(() => dbe.RegisterComponentFromAccessor<CompWidenV2>());
        }
    }

    [Test]
    public void ReopenWithBreaking_ThrowsSchemaValidationException()
    {
        // Phase 1: Create database with V1 (int Score)
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompBreakV1>();

            var comp = new CompBreakV1(999);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Reopen with V2 (String64 Score) — breaking change, should throw
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var ex = Assert.Throws<SchemaValidationException>(() => dbe.RegisterComponentFromAccessor<CompBreakV2>());
            Assert.That(ex.Diff, Is.Not.Null);
            Assert.That(ex.Diff.HasBreakingChanges, Is.True);
        }
    }

    [Test]
    public void BreakingChange_ExceptionContainsFieldInfo()
    {
        // Phase 1
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompBreakV1>();

            var comp = new CompBreakV1(42);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var ex = Assert.Throws<SchemaValidationException>(() => dbe.RegisterComponentFromAccessor<CompBreakV2>());

            Assert.That(ex.Diff.FieldChanges, Has.Count.GreaterThan(0));
            var typeChange = ex.Diff.FieldChanges.Find(c => c.Kind == FieldChangeKind.TypeChanged);
            Assert.That(typeChange, Is.Not.Null);
            Assert.That(typeChange.FieldName, Is.EqualTo("Score"));
            Assert.That(typeChange.OldType, Is.EqualTo(FieldType.Int));
            Assert.That(typeChange.NewType, Is.EqualTo(FieldType.String64));
        }
    }

    [Test]
    public void SkipMode_BypassesBreakingValidation()
    {
        // Phase 1
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompBreakV1>();

            var comp = new CompBreakV1(42);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Skip mode — should NOT throw even with breaking change
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.DoesNotThrow(() => dbe.RegisterComponentFromAccessor<CompBreakV2>(schemaValidation: SchemaValidationMode.Skip));
        }
    }

    [Test]
    public void SchemaRevision_IncrementedOnChange()
    {
        // Phase 1: Create database with V1
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompFieldAddV1>();

            var comp = new CompFieldAddV1(10, 1.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Reopen with V2 (field added) — should increment SchemaRevision
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompFieldAddV2>();

            // Read back ComponentR1 to check SchemaRevision
            using var epochGuard = EpochGuard.Enter(dbe.EpochManager);
            var compTable = dbe.GetComponentTable<ComponentR1>();
            foreach (var kv in compTable.PrimaryKeyIndex.EnumerateLeaves())
            {
                if (!SystemCrud.Read(compTable, kv.Key, out ComponentR1 comp, dbe.EpochManager))
                {
                    continue;
                }

                if (comp.Name.AsString == "Typhon.Schema.UnitTest.SchemaFieldAdd")
                {
                    Assert.That(comp.SchemaRevision, Is.EqualTo(1), "SchemaRevision should be incremented from 0 to 1");
                    return;
                }
            }

            Assert.Fail("Did not find persisted ComponentR1 for SchemaFieldAdd");
        }
    }

    [Test]
    public void UserSchemaVersion_IncrementedOnChange()
    {
        // Phase 1: Create database with V1
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompFieldAddV1>();

            var comp = new CompFieldAddV1(10, 1.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.CreateEntity(ref comp);
            t.Commit();
        }

        // Phase 2: Reopen with V2 (field added) — should increment UserSchemaVersion
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompFieldAddV2>();

            // Read UserSchemaVersion from bootstrap dictionary
            var userSchemaVersion = dbe.MMF.Bootstrap.GetInt(DatabaseEngine.BK_UserSchemaVersion);
            Assert.That(userSchemaVersion, Is.GreaterThan(0), "UserSchemaVersion should be > 0 after schema change");
        }
    }
}
