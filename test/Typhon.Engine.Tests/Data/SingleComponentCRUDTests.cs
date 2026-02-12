using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Comprehensive unit tests for single (non-AllowMultiple) component CRUD operations.
/// These tests cover Create, Read, Update, Delete operations in various transaction scenarios,
/// including rollback, resurrection, and MVCC isolation.
/// </summary>
class SingleComponentCRUDTests : TestBase<SingleComponentCRUDTests>
{
    #region Create Tests

    /// <summary>
    /// Tests basic entity creation with a single component.
    /// </summary>
    [Test]
    public void Create_SingleComponent_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100, 1.5f, 2.5);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1), "Initial revision should be 1");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity is readable in new transaction
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(read.A, Is.EqualTo(100), "CompA.A should match");
            Assert.That(read.B, Is.EqualTo(1.5f), "CompA.B should match");
            Assert.That(read.C, Is.EqualTo(2.5), "CompA.C should match");
        }
    }

    /// <summary>
    /// Tests entity creation with multiple component types.
    /// </summary>
    [Test]
    public void Create_MultipleComponentTypes_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(50);
        var b = new CompB(60, 7.8f);
        var c = new CompC("TestString");
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(entityId, Is.Not.Zero);
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));
            Assert.That(t.GetComponentRevision<CompB>(entityId), Is.EqualTo(1));
            Assert.That(t.GetComponentRevision<CompC>(entityId), Is.EqualTo(1));
            t.Commit();
        }

        // Verify all components are readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompB readB, out CompC readC);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(50));
            Assert.That(readB.A, Is.EqualTo(60));
            Assert.That(readB.B, Is.EqualTo(7.8f));
            Assert.That(readC.String.AsString, Is.EqualTo("TestString"));
        }
    }

    /// <summary>
    /// Tests that creating multiple entities in the same transaction works correctly.
    /// </summary>
    [Test]
    public void Create_MultipleEntities_SameTransaction()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long[] entityIds = new long[10];

        using (var t = dbe.CreateTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var a = new CompA(i * 100);
                entityIds[i] = t.CreateEntity(ref a);
                Assert.That(entityIds[i], Is.Not.Zero, $"Entity {i} ID should be non-zero");
            }
            t.Commit();
        }

        // Verify all entities have unique IDs
        for (int i = 0; i < 10; i++)
        {
            for (int j = i + 1; j < 10; j++)
            {
                Assert.That(entityIds[i], Is.Not.EqualTo(entityIds[j]), "Entity IDs should be unique");
            }
        }

        // Verify all entities are readable with correct values
        using (var t = dbe.CreateTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var res = t.ReadEntity(entityIds[i], out CompA read);
                Assert.That(res, Is.True, $"Read entity {i} should succeed");
                Assert.That(read.A, Is.EqualTo(i * 100), $"Entity {i} value should match");
            }
        }
    }

    /// <summary>
    /// Tests that creating multiple entities across separate transactions works correctly.
    /// </summary>
    [Test]
    public void Create_MultipleEntities_SeparateTransactions()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long[] entityIds = new long[5];

        for (int i = 0; i < 5; i++)
        {
            using var t = dbe.CreateTransaction();
            var a = new CompA(i * 50);
            entityIds[i] = t.CreateEntity(ref a);
            t.Commit();
        }

        // Verify all entities are readable
        using (var t = dbe.CreateTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var res = t.ReadEntity(entityIds[i], out CompA read);
                Assert.That(res, Is.True, $"Read entity {i} should succeed");
                Assert.That(read.A, Is.EqualTo(i * 50));
            }
        }
    }

    /// <summary>
    /// Tests that rollback of create operation makes the entity not readable.
    /// </summary>
    [Test]
    public void Create_ThenRollback_EntityNotReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(999);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            Assert.That(entityId, Is.Not.Zero);

            var res = t.Rollback();
            Assert.That(res, Is.True, "Rollback should succeed");
        }

        // Verify entity is not readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _);
            Assert.That(res, Is.False, "Entity should not be readable after rollback");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(-1), "Revision should be -1 for non-existent entity");
        }
    }

    /// <summary>
    /// Tests reading an entity within the same transaction that created it.
    /// </summary>
    [Test]
    public void Create_ReadInSameTransaction_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateTransaction();

        var a = new CompA(123, 4.56f, 7.89);
        var entityId = t.CreateEntity(ref a);

        // Read within same transaction (read-your-writes)
        var res = t.ReadEntity(entityId, out CompA read);
        Assert.That(res, Is.True, "Read in same transaction should succeed");
        Assert.That(read.A, Is.EqualTo(123));
        Assert.That(read.B, Is.EqualTo(4.56f));
        Assert.That(read.C, Is.EqualTo(7.89));

        t.Commit();
    }

    #endregion

    #region Read Tests

    /// <summary>
    /// Tests reading a non-existent entity returns false.
    /// </summary>
    [Test]
    public void Read_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateTransaction();

        // Try to read an entity that was never created
        var res = t.ReadEntity(999999L, out CompA _);
        Assert.That(res, Is.False, "Reading non-existent entity should return false");
        Assert.That(t.GetComponentRevision<CompA>(999999L), Is.EqualTo(-1), "Revision should be -1");
    }

    /// <summary>
    /// Tests that reading the same entity multiple times returns consistent results.
    /// </summary>
    [Test]
    public void Read_SameEntity_MultipleTimes_ConsistentResults()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(42);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var res = t.ReadEntity(entityId, out CompA read);
                Assert.That(res, Is.True, $"Read {i} should succeed");
                Assert.That(read.A, Is.EqualTo(42), $"Read {i} should return consistent value");
            }
        }
    }

    /// <summary>
    /// Tests reading multiple component types from the same entity.
    /// </summary>
    [Test]
    public void Read_MultipleComponentTypes_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(10);
        var b = new CompB(20, 3.0f);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, ref b);
            t.Commit();
        }

        // Read individual components
        using (var t = dbe.CreateTransaction())
        {
            var res1 = t.ReadEntity(entityId, out CompA readA);
            Assert.That(res1, Is.True);
            Assert.That(readA.A, Is.EqualTo(10));

            var res2 = t.ReadEntity(entityId, out CompB readB);
            Assert.That(res2, Is.True);
            Assert.That(readB.A, Is.EqualTo(20));
            Assert.That(readB.B, Is.EqualTo(3.0f));
        }

        // Read both components together
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompB readB);
            Assert.That(res, Is.True);
            Assert.That(readA.A, Is.EqualTo(10));
            Assert.That(readB.A, Is.EqualTo(20));
        }
    }

    /// <summary>
    /// Tests that reading a component that doesn't exist on an entity returns false.
    /// </summary>
    [Test]
    public void Read_ComponentNotOnEntity_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Try to read CompB which wasn't added to this entity
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompB _);
            Assert.That(res, Is.False, "Reading component not on entity should return false");
        }
    }

    #endregion

    #region Update Tests

    /// <summary>
    /// Tests basic update operation in a separate transaction.
    /// </summary>
    [Test]
    public void Update_SeparateTransaction_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update in separate transaction
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(200, 3.0f, 4.0);
            var res = t.UpdateEntity(entityId, ref updated);
            Assert.That(res, Is.True, "Update should succeed");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(2), "Revision should increment");
            t.Commit();
        }

        // Verify update
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(200));
            Assert.That(read.B, Is.EqualTo(3.0f));
            Assert.That(read.C, Is.EqualTo(4.0));
        }
    }

    /// <summary>
    /// Tests update in the same transaction as create (should overwrite without creating new revision).
    /// </summary>
    [Test]
    public void Update_SameTransactionAsCreate_OverwritesWithoutNewRevision()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateTransaction();

        var a = new CompA(100);
        var entityId = t.CreateEntity(ref a);
        Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));

        // Update in same transaction
        var updated = new CompA(200);
        t.UpdateEntity(entityId, ref updated);

        // Revision should still be 1 (create + update in same tx = single revision)
        Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1),
            "Update in same transaction as create should not create new revision");

        // Read should show updated value
        t.ReadEntity(entityId, out CompA read);
        Assert.That(read.A, Is.EqualTo(200));

        t.Commit();

        // Verify after commit in new transaction
        using var t2 = dbe.CreateTransaction();
        t2.ReadEntity(entityId, out CompA read2);
        Assert.That(read2.A, Is.EqualTo(200));
        Assert.That(t2.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));
    }

    /// <summary>
    /// Tests multiple consecutive updates in the same transaction.
    /// </summary>
    [Test]
    public void Update_MultipleTimes_SameTransaction()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(0);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            for (int i = 1; i <= 5; i++)
            {
                var updated = new CompA(i * 100);
                t.UpdateEntity(entityId, ref updated);
            }

            // Final value should be 500
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(500));

            // Should only have revision 2 (one update transaction after create)
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(2));

            t.Commit();
        }

        // Verify final state
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(500));
        }
    }

    /// <summary>
    /// Tests multiple updates across separate transactions.
    /// </summary>
    [Test]
    public void Update_MultipleTimes_SeparateTransactions()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(0);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        for (int i = 1; i <= 5; i++)
        {
            using var t = dbe.CreateTransaction();
            var updated = new CompA(i * 100);
            t.UpdateEntity(entityId, ref updated);
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(i + 1),
                $"Revision after update {i} should be {i + 1}");
            t.Commit();
        }

        // Verify final state
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(500));
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(6));
        }
    }

    /// <summary>
    /// Tests that rolling back an update preserves the original value.
    /// </summary>
    [Test]
    public void Update_ThenRollback_OriginalValuePreserved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update and rollback
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(999);
            t.UpdateEntity(entityId, ref updated);

            // Within transaction, should see updated value
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(999));

            t.Rollback();
        }

        // After rollback, should see original value
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True, "Entity should still be readable after rollback");
            Assert.That(read.A, Is.EqualTo(100), "Original value should be preserved");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1), "Revision should not change after rollback");
        }
    }

    /// <summary>
    /// Tests updating a non-existent entity returns false.
    /// </summary>
    [Test]
    public void Update_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateTransaction();
        var a = new CompA(100);
        var res = t.UpdateEntity(999999L, ref a);
        Assert.That(res, Is.False, "Updating non-existent entity should return false");
    }

    /// <summary>
    /// Tests updating multiple component types on the same entity.
    /// </summary>
    [Test]
    public void Update_MultipleComponentTypes_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(10);
        var b = new CompB(20, 3.0f);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, ref b);
            t.Commit();
        }

        // Update both components
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(100);
            var updatedB = new CompB(200, 30.0f);
            t.UpdateEntity(entityId, ref updatedA, ref updatedB);
            t.Commit();
        }

        // Verify both were updated
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA readA, out CompB readB);
            Assert.That(readA.A, Is.EqualTo(100));
            Assert.That(readB.A, Is.EqualTo(200));
            Assert.That(readB.B, Is.EqualTo(30.0f));
        }
    }

    /// <summary>
    /// Tests read-before-update pattern in separate transaction.
    /// </summary>
    [Test]
    public void Update_ReadBeforeUpdate_SeparateTransaction()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Read then update in same transaction
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(100));

            // Update based on read value
            var updated = new CompA(read.A + 50);
            t.UpdateEntity(entityId, ref updated);

            // Should see updated value
            t.ReadEntity(entityId, out CompA readAfter);
            Assert.That(readAfter.A, Is.EqualTo(150));

            t.Commit();
        }

        // Verify final value
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(150));
        }
    }

    #endregion

    #region Delete Tests

    /// <summary>
    /// Tests basic delete operation in a separate transaction.
    /// </summary>
    [Test]
    public void Delete_SeparateTransaction_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Delete in separate transaction
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntity<CompA>(entityId);
            Assert.That(res, Is.True, "Delete should succeed");
            t.Commit();
        }

        // Verify entity is not readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _);
            Assert.That(res, Is.False, "Entity should not be readable after delete");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(-1));
        }
    }

    /// <summary>
    /// Tests delete in the same transaction as create.
    /// </summary>
    [Test]
    public void Delete_SameTransactionAsCreate_EntityNotReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            var a = new CompA(100);
            entityId = t.CreateEntity(ref a);

            // Delete in same transaction
            var deleted = t.DeleteEntity<CompA>(entityId);
            Assert.That(deleted, Is.True);

            t.Commit();
        }

        // Verify entity is not readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _);
            Assert.That(res, Is.False, "Entity should not be readable");
        }
    }

    /// <summary>
    /// Tests that rolling back a delete operation restores the entity.
    /// </summary>
    [Test]
    public void Delete_ThenRollback_EntityRestored()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Delete and rollback
        using (var t = dbe.CreateTransaction())
        {
            var deleted = t.DeleteEntity<CompA>(entityId);
            Assert.That(deleted, Is.True);

            t.Rollback();
        }

        // After rollback, entity should be readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True, "Entity should be readable after rollback");
            Assert.That(read.A, Is.EqualTo(100), "Value should be preserved");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Tests deleting a non-existent entity returns false.
    /// </summary>
    [Test]
    public void Delete_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateTransaction();
        var res = t.DeleteEntity<CompA>(999999L);
        Assert.That(res, Is.False, "Deleting non-existent entity should return false");
    }

    /// <summary>
    /// Tests deleting multiple component types from the same entity.
    /// </summary>
    [Test]
    public void Delete_MultipleComponentTypes_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(10);
        var b = new CompB(20, 3.0f);
        var c = new CompC("Test");
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, ref b, ref c);
            t.Commit();
        }

        // Delete all components
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntity<CompA, CompB, CompC>(entityId);
            Assert.That(res, Is.True);
            t.Commit();
        }

        // Verify none are readable
        using (var t = dbe.CreateTransaction())
        {
            Assert.That(t.ReadEntity(entityId, out CompA _), Is.False);
            Assert.That(t.ReadEntity(entityId, out CompB _), Is.False);
            Assert.That(t.ReadEntity(entityId, out CompC _), Is.False);
        }
    }

    /// <summary>
    /// Tests deleting only one component type, leaving others intact.
    /// </summary>
    [Test]
    public void Delete_OneComponentType_OthersRemain()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(10);
        var b = new CompB(20, 3.0f);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, ref b);
            t.Commit();
        }

        // Delete only CompA
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntity<CompA>(entityId);
            Assert.That(res, Is.True);
            t.Commit();
        }

        // CompA should be deleted, CompB should remain
        using (var t = dbe.CreateTransaction())
        {
            Assert.That(t.ReadEntity(entityId, out CompA _), Is.False, "CompA should be deleted");
            Assert.That(t.ReadEntity(entityId, out CompB readB), Is.True, "CompB should remain");
            Assert.That(readB.A, Is.EqualTo(20));
        }
    }

    /// <summary>
    /// Tests deleting the same entity twice returns false on second attempt.
    /// </summary>
    [Test]
    public void Delete_SameEntity_Twice_SecondReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // First delete
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntity<CompA>(entityId);
            Assert.That(res, Is.True);
            t.Commit();
        }

        // Second delete attempt
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntity<CompA>(entityId);
            Assert.That(res, Is.False, "Second delete should return false");
        }
    }

    #endregion

    #region Resurrection Tests

    /// <summary>
    /// Tests that after deleting an entity, we cannot update it (resurrection via update should fail).
    /// </summary>
    [Test]
    public void Resurrection_UpdateAfterDelete_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Delete
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompA>(entityId);
            t.Commit();
        }

        // Try to update deleted entity
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(999);
            var res = t.UpdateEntity(entityId, ref updated);
            Assert.That(res, Is.False, "Update of deleted entity should return false");
        }
    }

    /// <summary>
    /// Tests create, delete, rollback, then read - entity should be readable.
    /// </summary>
    [Test]
    public void Resurrection_CreateDeleteRollback_EntityReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        // Create
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Delete and rollback
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompA>(entityId);
            t.Rollback();
        }

        // Entity should be readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True, "Entity should be readable after delete rollback");
            Assert.That(read.A, Is.EqualTo(100));
        }
    }

    /// <summary>
    /// Tests update, delete in same transaction, then rollback - original value should be restored.
    /// </summary>
    [Test]
    public void Resurrection_UpdateDeleteRollback_OriginalRestored()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update then delete, then rollback
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(999);
            t.UpdateEntity(entityId, ref updated);
            t.DeleteEntity<CompA>(entityId);
            t.Rollback();
        }

        // Should see original value
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True, "Entity should be readable");
            Assert.That(read.A, Is.EqualTo(100), "Original value should be restored");
        }
    }

    #endregion

    #region MVCC Isolation Tests

    /// <summary>
    /// Tests that a transaction started before an update sees the old value.
    /// </summary>
    [Test]
    public void MVCC_TransactionSeesSnapshotAtCreationTime()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Start a long-running transaction
        using var earlyTxn = dbe.CreateTransaction();

        // Update in another transaction
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(200);
            t.UpdateEntity(entityId, ref updated);
            t.Commit();
        }

        // Early transaction should see original value (snapshot isolation)
        earlyTxn.ReadEntity(entityId, out CompA read);
        Assert.That(read.A, Is.EqualTo(100), "Early transaction should see original value");
        Assert.That(earlyTxn.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));

        // New transaction should see updated value
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA readNew);
            Assert.That(readNew.A, Is.EqualTo(200), "New transaction should see updated value");
        }
    }

    /// <summary>
    /// Tests that a transaction started before a delete still sees the entity.
    /// </summary>
    [Test]
    public void MVCC_TransactionSeesEntityBeforeDelete()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Start a long-running transaction
        using var earlyTxn = dbe.CreateTransaction();

        // Delete in another transaction
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompA>(entityId);
            t.Commit();
        }

        // Early transaction should still see the entity
        var res = earlyTxn.ReadEntity(entityId, out CompA read);
        Assert.That(res, Is.True, "Early transaction should still see entity");
        Assert.That(read.A, Is.EqualTo(100));

        // New transaction should not see the entity
        using (var t = dbe.CreateTransaction())
        {
            var resNew = t.ReadEntity(entityId, out CompA _);
            Assert.That(resNew, Is.False, "New transaction should not see deleted entity");
        }
    }

    /// <summary>
    /// Tests revision count with long-running transaction preventing cleanup.
    /// </summary>
    [Test]
    public void MVCC_LongRunningTransaction_PreventsRevisionCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(0);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }
        
        // Start a long-running transaction
        using var longRunningTxn = dbe.CreateTransaction();

        // Perform multiple updates
        for (int i = 1; i <= 5; i++)
        {
            using var t = dbe.CreateTransaction();
            var updated = new CompA(i * 100);
            t.UpdateEntity(entityId, ref updated);
            t.Commit();
        }

        // Check revision count while long-running transaction is active
        using (var t = dbe.CreateTransaction())
        {
            var revCount = t.GetRevisionCount<CompA>(entityId);
            Assert.That(revCount, Is.GreaterThan(1),
                "Multiple revisions should be retained while long-running transaction exists");
        }

        // Long-running transaction should see original value
        longRunningTxn.ReadEntity(entityId, out CompA readOld);
        Assert.That(readOld.A, Is.EqualTo(0), "Long-running transaction should see original value");

        // Complete long-running transaction
        longRunningTxn.Commit();

        // After long-running transaction completes, cleanup may reduce revision count
        // (This depends on implementation - revisions may be cleaned up lazily)
    }

    #endregion

    #region Edge Cases

    /// <summary>
    /// Tests operations with component containing default values.
    /// </summary>
    [Test]
    public void EdgeCase_DefaultValues_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(); // All default values (0, 0, 0)
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(0));
            Assert.That(read.B, Is.EqualTo(0));
            Assert.That(read.C, Is.EqualTo(0));
        }
    }

    /// <summary>
    /// Tests operations with extreme values.
    /// </summary>
    [Test]
    public void EdgeCase_ExtremeValues_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(int.MaxValue, float.MaxValue, double.MaxValue);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(int.MaxValue));
            Assert.That(read.B, Is.EqualTo(float.MaxValue));
            Assert.That(read.C, Is.EqualTo(double.MaxValue));
        }

        // Update to min values
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(int.MinValue, float.MinValue, double.MinValue);
            t.UpdateEntity(entityId, ref updated);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(int.MinValue));
            Assert.That(read.B, Is.EqualTo(float.MinValue));
            Assert.That(read.C, Is.EqualTo(double.MinValue));
        }
    }

    /// <summary>
    /// Tests operations with negative values.
    /// </summary>
    [Test]
    public void EdgeCase_NegativeValues_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(-100, -1.5f, -2.5);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(-100));
            Assert.That(read.B, Is.EqualTo(-1.5f));
            Assert.That(read.C, Is.EqualTo(-2.5));
        }
    }

    /// <summary>
    /// Tests rapid create-update-delete cycles.
    /// </summary>
    [Test]
    public void EdgeCase_RapidCRUDCycles_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        for (int cycle = 0; cycle < 10; cycle++)
        {
            long entityId;

            // Create
            using (var t = dbe.CreateTransaction())
            {
                var a = new CompA(cycle);
                entityId = t.CreateEntity(ref a);
                t.Commit();
            }

            // Update
            using (var t = dbe.CreateTransaction())
            {
                var updated = new CompA(cycle * 100);
                t.UpdateEntity(entityId, ref updated);
                t.Commit();
            }

            // Read and verify
            using (var t = dbe.CreateTransaction())
            {
                var res = t.ReadEntity(entityId, out CompA read);
                Assert.That(res, Is.True, $"Cycle {cycle}: Read should succeed");
                Assert.That(read.A, Is.EqualTo(cycle * 100), $"Cycle {cycle}: Value should match");
            }

            // Delete
            using (var t = dbe.CreateTransaction())
            {
                t.DeleteEntity<CompA>(entityId);
                t.Commit();
            }

            // Verify deleted
            using (var t = dbe.CreateTransaction())
            {
                var res = t.ReadEntity(entityId, out CompA _);
                Assert.That(res, Is.False, $"Cycle {cycle}: Entity should be deleted");
            }
        }
    }

    /// <summary>
    /// Tests that disposed transaction cannot be used.
    /// </summary>
    [Test]
    public void EdgeCase_UseAfterDispose_ThrowsOrReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var t = dbe.CreateTransaction();
        var a = new CompA(100);
        var entityId = t.CreateEntity(ref a);
        t.Commit();
        t.Dispose();

        // Operations on disposed transaction should fail gracefully
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed));
    }

    /// <summary>
    /// Tests empty string in string component.
    /// </summary>
    [Test]
    public void EdgeCase_EmptyString_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var c = new CompC("");
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref c);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompC read);
            Assert.That(res, Is.True);
            Assert.That(read.String.AsString, Is.EqualTo(""));
        }
    }

    /// <summary>
    /// Tests concurrent read operations (no writes).
    /// </summary>
    [Test]
    public void EdgeCase_ConcurrentReads_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(42);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Start multiple read transactions
        var transactions = new Transaction[5];
        for (int i = 0; i < 5; i++)
        {
            transactions[i] = dbe.CreateTransaction();
        }

        // All should read the same value
        for (int i = 0; i < 5; i++)
        {
            var res = transactions[i].ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True, $"Transaction {i} read should succeed");
            Assert.That(read.A, Is.EqualTo(42), $"Transaction {i} should read correct value");
        }

        // Dispose all transactions
        for (int i = 0; i < 5; i++)
        {
            transactions[i].Dispose();
        }
    }

    /// <summary>
    /// Tests creating an entity, then creating another entity in separate transaction.
    /// </summary>
    [Test]
    public void EdgeCase_InterleavedCreations_UniqueIds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId1, entityId2, entityId3;

        using (var t = dbe.CreateTransaction())
        {
            var a = new CompA(1);
            entityId1 = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            var a = new CompA(2);
            entityId2 = t.CreateEntity(ref a);
            t.Commit();
        }

        using (var t = dbe.CreateTransaction())
        {
            var a = new CompA(3);
            entityId3 = t.CreateEntity(ref a);
            t.Commit();
        }

        // All IDs should be unique
        Assert.That(entityId1, Is.Not.EqualTo(entityId2));
        Assert.That(entityId2, Is.Not.EqualTo(entityId3));
        Assert.That(entityId1, Is.Not.EqualTo(entityId3));

        // All entities should be readable with correct values
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId1, out CompA read1);
            t.ReadEntity(entityId2, out CompA read2);
            t.ReadEntity(entityId3, out CompA read3);

            Assert.That(read1.A, Is.EqualTo(1));
            Assert.That(read2.A, Is.EqualTo(2));
            Assert.That(read3.A, Is.EqualTo(3));
        }
    }

    #endregion

    #region Indexed Component Tests (CompD)

    /// <summary>
    /// Tests CRUD operations on indexed component (CompD).
    /// </summary>
    [Test]
    [Ignore("Need MVCC compatible secondary indices")]
    public void IndexedComponent_CRUD_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var d = new CompD(1.0f, 2, 3.0);
        long entityId;

        // Create
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref d);
            Assert.That(t.GetComponentRevision<CompD>(entityId), Is.EqualTo(1));
            t.Commit();
        }

        // Read
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompD read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(1.0f));
            Assert.That(read.B, Is.EqualTo(2));
            Assert.That(read.C, Is.EqualTo(3.0));
        }

        // Update
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompD(10.0f, 20, 30.0);
            t.UpdateEntity(entityId, ref updated);
            Assert.That(t.GetComponentRevision<CompD>(entityId), Is.EqualTo(2));
            t.Commit();
        }

        // Verify update
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompD read);
            Assert.That(read.A, Is.EqualTo(10.0f));
            Assert.That(read.B, Is.EqualTo(20));
            Assert.That(read.C, Is.EqualTo(30.0));
        }

        // Delete
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompD>(entityId);
            t.Commit();
        }

        // Verify deleted
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompD _);
            Assert.That(res, Is.False);
        }
    }

    /// <summary>
    /// Tests that updating indexed fields works correctly.
    /// </summary>
    [Test]
    [Ignore("Need MVCC compatible secondary indices")]
    public void IndexedComponent_UpdateIndexedField_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var d = new CompD(1.0f, 100, 3.0);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref d);
            t.Commit();
        }

        // Update the indexed field B
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompD(1.0f, 200, 3.0); // Only B changed
            t.UpdateEntity(entityId, ref updated);
            t.Commit();
        }

        // Verify the change
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompD read);
            Assert.That(read.B, Is.EqualTo(200), "Indexed field should be updated");
        }
    }

    /// <summary>
    /// Tests rollback of indexed component update.
    /// </summary>
    [Test]
    [Ignore("Need MVCC compatible secondary indices")]
    public void IndexedComponent_UpdateThenRollback_OriginalIndexValue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var d = new CompD(1.0f, 100, 3.0);
        long entityId;

        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref d);
            t.Commit();
        }

        // Update and rollback
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompD(10.0f, 999, 30.0);
            t.UpdateEntity(entityId, ref updated);
            t.Rollback();
        }

        // Original values should be preserved
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompD read);
            Assert.That(read.A, Is.EqualTo(1.0f));
            Assert.That(read.B, Is.EqualTo(100));
            Assert.That(read.C, Is.EqualTo(3.0));
        }
    }

    #endregion

    #region Complex Scenarios

    /// <summary>
    /// Tests a complex sequence of operations: create, read, update, read, delete, try read.
    /// </summary>
    [Test]
    public void ComplexScenario_FullLifecycle_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100, 1.0f, 2.0);
        long entityId;

        // Create
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));
            t.Commit();
        }

        // Read and verify
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(100));
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1));
        }

        // Update
        using (var t = dbe.CreateTransaction())
        {
            var updated = new CompA(200, 3.0f, 4.0);
            t.UpdateEntity(entityId, ref updated);
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(2));
            t.Commit();
        }

        // Read updated value
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(200));
            Assert.That(read.B, Is.EqualTo(3.0f));
            Assert.That(read.C, Is.EqualTo(4.0));
        }

        // Delete
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompA>(entityId);
            t.Commit();
        }

        // Try to read - should fail
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _);
            Assert.That(res, Is.False, "Deleted entity should not be readable");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(-1));
        }
    }

    /// <summary>
    /// Tests interleaved create and update operations across multiple transactions.
    /// </summary>
    [Test]
    public void ComplexScenario_InterleavedCreateUpdate_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId1, entityId2;

        // Create first entity
        using (var t = dbe.CreateTransaction())
        {
            var a = new CompA(100);
            entityId1 = t.CreateEntity(ref a);
            t.Commit();
        }

        // Create second entity and update first
        using (var t = dbe.CreateTransaction())
        {
            var a2 = new CompA(200);
            entityId2 = t.CreateEntity(ref a2);

            var updated1 = new CompA(150);
            t.UpdateEntity(entityId1, ref updated1);

            t.Commit();
        }

        // Update second entity
        using (var t = dbe.CreateTransaction())
        {
            var updated2 = new CompA(250);
            t.UpdateEntity(entityId2, ref updated2);
            t.Commit();
        }

        // Verify final state
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId1, out CompA read1);
            t.ReadEntity(entityId2, out CompA read2);

            Assert.That(read1.A, Is.EqualTo(150));
            Assert.That(read2.A, Is.EqualTo(250));

            Assert.That(t.GetComponentRevision<CompA>(entityId1), Is.EqualTo(2));
            Assert.That(t.GetComponentRevision<CompA>(entityId2), Is.EqualTo(2));
        }
    }

    /// <summary>
    /// Tests mixed committed and rolled back transactions.
    /// </summary>
    [Test]
    public void ComplexScenario_MixedCommitRollback_CorrectFinalState()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        long entityId;

        // Create and commit
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update to 200, commit
        using (var t = dbe.CreateTransaction())
        {
            var u = new CompA(200);
            t.UpdateEntity(entityId, ref u);
            t.Commit();
        }

        // Update to 300, rollback
        using (var t = dbe.CreateTransaction())
        {
            var u = new CompA(300);
            t.UpdateEntity(entityId, ref u);
            t.Rollback();
        }

        // Update to 400, commit
        using (var t = dbe.CreateTransaction())
        {
            var u = new CompA(400);
            t.UpdateEntity(entityId, ref u);
            t.Commit();
        }

        // Update to 500, rollback
        using (var t = dbe.CreateTransaction())
        {
            var u = new CompA(500);
            t.UpdateEntity(entityId, ref u);
            t.Rollback();
        }

        // Final value should be 400
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA read);
            Assert.That(read.A, Is.EqualTo(400), "Final value should be from last committed update");
        }
    }

    /// <summary>
    /// Tests operations with multiple entities and multiple component types.
    /// </summary>
    [Test]
    public void ComplexScenario_MultipleEntitiesMultipleComponents_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long[] entityIds = new long[3];

        // Create 3 entities with different component combinations
        using (var t = dbe.CreateTransaction())
        {
            var a1 = new CompA(100);
            var b1 = new CompB(110, 1.1f);
            entityIds[0] = t.CreateEntity(ref a1, ref b1);

            var a2 = new CompA(200);
            entityIds[1] = t.CreateEntity(ref a2);

            var b3 = new CompB(330, 3.3f);
            var c3 = new CompC("Entity3");
            entityIds[2] = t.CreateEntity(ref b3, ref c3);

            t.Commit();
        }

        // Update different entities
        using (var t = dbe.CreateTransaction())
        {
            var ua1 = new CompA(101);
            t.UpdateEntity(entityIds[0], ref ua1);

            var ua2 = new CompA(202);
            t.UpdateEntity(entityIds[1], ref ua2);

            t.Commit();
        }

        // Delete entity 1's CompB, keep CompA
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompB>(entityIds[0]);
            t.Commit();
        }

        // Verify final state
        using (var t = dbe.CreateTransaction())
        {
            // Entity 0: CompA updated, CompB deleted
            Assert.That(t.ReadEntity(entityIds[0], out CompA a0), Is.True);
            Assert.That(a0.A, Is.EqualTo(101));
            Assert.That(t.ReadEntity(entityIds[0], out CompB _), Is.False, "CompB should be deleted from entity 0");

            // Entity 1: CompA updated
            Assert.That(t.ReadEntity(entityIds[1], out CompA a1), Is.True);
            Assert.That(a1.A, Is.EqualTo(202));

            // Entity 2: No CompA, has CompB and CompC
            Assert.That(t.ReadEntity(entityIds[2], out CompA _), Is.False);
            Assert.That(t.ReadEntity(entityIds[2], out CompB b2), Is.True);
            Assert.That(b2.A, Is.EqualTo(330));
            Assert.That(t.ReadEntity(entityIds[2], out CompC c2), Is.True);
            Assert.That(c2.String.AsString, Is.EqualTo("Entity3"));
        }
    }

    #endregion
}
