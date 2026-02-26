using NUnit.Framework;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Pure unit tests for <see cref="FieldIdResolver"/> — no database needed.
/// Tests the resolution algorithm: name matching, PreviousName renames, explicit IDs, and edge cases.
/// </summary>
class FieldIdResolverTests
{
    private static FieldR1 MakeField(string name, int fieldId) =>
        new() { Name = (String64)name, FieldId = fieldId };

    [Test]
    public void NameMatch_ReusesPersistedId()
    {
        var persisted = new[]
        {
            MakeField("Health", 0),
            MakeField("Speed", 1),
            MakeField("Armor", 2)
        };

        var resolver = new FieldIdResolver(persisted);

        Assert.That(resolver.ResolveFieldId("Health", null, null), Is.EqualTo(0));
        Assert.That(resolver.ResolveFieldId("Speed", null, null), Is.EqualTo(1));
        Assert.That(resolver.ResolveFieldId("Armor", null, null), Is.EqualTo(2));

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.False, "No changes when all fields match by name");
        Assert.That(resolver.RemovedFieldNames, Is.Empty);
    }

    [Test]
    public void FieldAdded_AssignsNextId()
    {
        var persisted = new[]
        {
            MakeField("Health", 0),
            MakeField("Speed", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        Assert.That(resolver.ResolveFieldId("Health", null, null), Is.EqualTo(0));
        Assert.That(resolver.ResolveFieldId("Speed", null, null), Is.EqualTo(1));

        // New field gets max(persisted) + 1 = 2
        var newId = resolver.ResolveFieldId("Armor", null, null);
        Assert.That(newId, Is.EqualTo(2));

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.True, "Adding a field is a schema change");
        Assert.That(resolver.RemovedFieldNames, Is.Empty);
    }

    [Test]
    public void FieldRemoved_DetectedByComplete()
    {
        var persisted = new[]
        {
            MakeField("Health", 0),
            MakeField("Speed", 1),
            MakeField("Armor", 2)
        };

        var resolver = new FieldIdResolver(persisted);

        // Only resolve Health and Armor — Speed is removed
        resolver.ResolveFieldId("Health", null, null);
        resolver.ResolveFieldId("Armor", null, null);

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.True);
        Assert.That(resolver.RemovedFieldNames, Has.Count.EqualTo(1));
        Assert.That(resolver.RemovedFieldNames, Contains.Item("Speed"));
    }

    [Test]
    public void Rename_PreviousName_PreservesId()
    {
        var persisted = new[]
        {
            MakeField("Hitpoints", 0),
            MakeField("Speed", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        // "Health" is the new name, "Hitpoints" was the previous name
        var id = resolver.ResolveFieldId("Health", "Hitpoints", null);
        Assert.That(id, Is.EqualTo(0), "Should reuse Hitpoints' FieldId");

        Assert.That(resolver.ResolveFieldId("Speed", null, null), Is.EqualTo(1));

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.True, "Rename is a schema change");
        Assert.That(resolver.Renames, Has.Count.EqualTo(1));
        Assert.That(resolver.Renames[0], Is.EqualTo(("Hitpoints", "Health", 0)));
        Assert.That(resolver.RemovedFieldNames, Is.Empty, "Renamed field should not appear as removed");
    }

    [Test]
    public void CircularRename_BothResolve()
    {
        // Swap: A was FieldId 0, B was FieldId 1
        // Now A has PreviousName="B" and B has PreviousName="A"
        var persisted = new[]
        {
            MakeField("A", 0),
            MakeField("B", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        // "NewA" was previously "B" (FieldId 1)
        var idA = resolver.ResolveFieldId("NewA", "B", null);
        Assert.That(idA, Is.EqualTo(1), "NewA should get B's old FieldId");

        // "NewB" was previously "A" (FieldId 0)
        var idB = resolver.ResolveFieldId("NewB", "A", null);
        Assert.That(idB, Is.EqualTo(0), "NewB should get A's old FieldId");

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.True);
        Assert.That(resolver.Renames, Has.Count.EqualTo(2));
        Assert.That(resolver.RemovedFieldNames, Is.Empty);
    }

    [Test]
    public void ExplicitFieldId_Honored()
    {
        var persisted = new[]
        {
            MakeField("Health", 0),
            MakeField("Speed", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        // Explicit FieldId matches persisted "Speed" (FieldId 1) — that's fine
        var id = resolver.ResolveFieldId("Speed", null, 1);
        Assert.That(id, Is.EqualTo(1));

        // New field with explicit FieldId = 5 (not persisted, should work)
        var id2 = resolver.ResolveFieldId("NewField", null, 5);
        Assert.That(id2, Is.EqualTo(5));

        resolver.ResolveFieldId("Health", null, null);
        resolver.Complete();
    }

    [Test]
    public void ExplicitFieldId_ConflictThrows()
    {
        var persisted = new[]
        {
            MakeField("Health", 0),
            MakeField("Speed", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        // Explicit FieldId 1 is taken by "Speed", but we're calling it "Armor" — conflict
        Assert.Throws<InvalidOperationException>(() =>
            resolver.ResolveFieldId("Armor", null, 1));
    }

    [Test]
    public void Overflow_Throws()
    {
        // Create a persisted field near the max FieldId
        var persisted = new[]
        {
            MakeField("NearMax", short.MaxValue)
        };

        var resolver = new FieldIdResolver(persisted);

        resolver.ResolveFieldId("NearMax", null, null);

        // Next new field would need short.MaxValue + 1 = 32768, which exceeds the limit
        Assert.Throws<InvalidOperationException>(() =>
            resolver.ResolveFieldId("NewField", null, null));
    }

    [Test]
    public void DuplicatePreviousName_ReturnsSameId()
    {
        // The FieldIdResolver itself does NOT enforce PreviousName uniqueness — that validation
        // is handled by CreateFromAccessor's pre-pass. If two fields claim the same PreviousName,
        // the resolver returns the same FieldId for both, which would be caught by Build() as
        // a duplicate FieldId error.
        var persisted = new[]
        {
            MakeField("OldName", 0),
            MakeField("Other", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        // First rename: "OldName" matched, returns its FieldId 0
        var id1 = resolver.ResolveFieldId("NewName1", "OldName", null);
        Assert.That(id1, Is.EqualTo(0));

        // Second rename: "OldName" is still in _persistedByName (resolver doesn't remove it),
        // so it returns the same FieldId 0 — Build() would reject this as a duplicate.
        var id2 = resolver.ResolveFieldId("NewName2", "OldName", null);
        Assert.That(id2, Is.EqualTo(0), "Resolver returns same FieldId (Build would reject duplicate)");
    }

    [Test]
    public void MixedExplicitAndAuto()
    {
        var persisted = new[]
        {
            MakeField("A", 0),
            MakeField("B", 1),
            MakeField("C", 2)
        };

        var resolver = new FieldIdResolver(persisted);

        // A: name match → 0
        Assert.That(resolver.ResolveFieldId("A", null, null), Is.EqualTo(0));

        // B: explicit FieldId 1 (matches persisted) → 1
        Assert.That(resolver.ResolveFieldId("B", null, 1), Is.EqualTo(1));

        // D: new field → max(persisted) + 1 = 3
        Assert.That(resolver.ResolveFieldId("D", null, null), Is.EqualTo(3));

        // E: explicit FieldId 10 (new, no conflict) → 10
        Assert.That(resolver.ResolveFieldId("E", null, 10), Is.EqualTo(10));

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.True, "Has new fields and removed field C");
        Assert.That(resolver.RemovedFieldNames, Contains.Item("C"));
    }

    [Test]
    public void NewFieldsSkipUsedIds()
    {
        var persisted = new[]
        {
            MakeField("A", 0),
            MakeField("B", 2)  // gap at ID 1
        };

        var resolver = new FieldIdResolver(persisted);

        resolver.ResolveFieldId("A", null, null);
        resolver.ResolveFieldId("B", null, null);

        // New field gets max + 1 = 3 (not 1, because nextNewId starts at max+1)
        var newId = resolver.ResolveFieldId("C", null, null);
        Assert.That(newId, Is.EqualTo(3));

        resolver.Complete();
    }

    [Test]
    public void EmptyPersisted_AllNewFields()
    {
        var persisted = Array.Empty<FieldR1>();
        var resolver = new FieldIdResolver(persisted);

        Assert.That(resolver.ResolveFieldId("A", null, null), Is.EqualTo(0));
        Assert.That(resolver.ResolveFieldId("B", null, null), Is.EqualTo(1));
        Assert.That(resolver.ResolveFieldId("C", null, null), Is.EqualTo(2));

        resolver.Complete();
        Assert.That(resolver.HasChanges, Is.True, "All fields are new");
        Assert.That(resolver.RemovedFieldNames, Is.Empty);
    }

    [Test]
    public void ExplicitFieldId_WithPreviousName_MatchesByPreviousName()
    {
        var persisted = new[]
        {
            MakeField("OldName", 5),
            MakeField("Other", 1)
        };

        var resolver = new FieldIdResolver(persisted);

        // Explicit FieldId matches the persisted field's ID, accessed via PreviousName
        var id = resolver.ResolveFieldId("NewName", "OldName", 5);
        Assert.That(id, Is.EqualTo(5));

        resolver.ResolveFieldId("Other", null, null);
        resolver.Complete();
        Assert.That(resolver.RemovedFieldNames, Is.Empty);
    }
}
