using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Database_Engine;

/// <summary>
/// Comprehensive tests for the AllowMultiple component feature.
/// AllowMultiple components allow multiple instances of the same component type to be associated with a single entity.
/// The CompE type is defined with AllowMultiple=true: [Component(SchemaName, 1, true)]
/// </summary>
class AllowMultipleComponentTests : TestBase<AllowMultipleComponentTests>
{
    #region Create Tests - Same Transaction

    /// <summary>
    /// Tests creating an entity with a single CompA and multiple CompE instances in the same transaction.
    /// </summary>
    [Test]
    public void CreateEntity_WithMultipleComponents_SameTransaction_SuccessfulCommit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(42);
        Span<CompE> eList = stackalloc CompE[5];
        for (int i = 0; i < eList.Length; i++)
        {
            eList[i] = new CompE(1.0f + i, i * 10, 2.0 * i);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);

            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1), "CompA should have revision 1");
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(1), "CompE should have revision 1");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify data persisted correctly
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);

            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(42), "CompA.A should match");
            Assert.That(readE.Length, Is.EqualTo(5), "Should have 5 CompE instances");

            for (int i = 0; i < 5; i++)
            {
                Assert.That(readE[i].A, Is.EqualTo(1.0f + i), $"CompE[{i}].A should match");
                Assert.That(readE[i].B, Is.EqualTo(i * 10), $"CompE[{i}].B should match");
                Assert.That(readE[i].C, Is.EqualTo(2.0 * i), $"CompE[{i}].C should match");
            }
        }
    }

    /// <summary>
    /// Tests creating an entity with a single AllowMultiple component instance (edge case).
    /// </summary>
    [Test]
    public void CreateEntity_WithSingleMultipleComponent_SameTransaction_SuccessfulCommit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[1];
        eList[0] = new CompE(99.9f, 88, 77.7);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);

            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readE.Length, Is.EqualTo(1), "Should have exactly 1 CompE instance");
            Assert.That(readE[0].A, Is.EqualTo(99.9f), "CompE[0].A should match");
            Assert.That(readE[0].B, Is.EqualTo(88), "CompE[0].B should match");
            Assert.That(readE[0].C, Is.EqualTo(77.7), "CompE[0].C should match");
        }
    }

    /// <summary>
    /// Tests creating an entity with many AllowMultiple component instances (stress test).
    /// </summary>
    [Test]
    public void CreateEntity_WithManyMultipleComponents_SameTransaction_SuccessfulCommit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(1);
        const int count = 100;
        Span<CompE> eList = stackalloc CompE[count];
        for (int i = 0; i < count; i++)
        {
            eList[i] = new CompE(i * 0.1f, i, i * 1.5);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _, out CompE[] readE);

            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readE.Length, Is.EqualTo(count), $"Should have {count} CompE instances");

            for (int i = 0; i < count; i++)
            {
                Assert.That(readE[i].A, Is.EqualTo(i * 0.1f).Within(0.0001f), $"CompE[{i}].A should match");
                Assert.That(readE[i].B, Is.EqualTo(i), $"CompE[{i}].B should match");
                Assert.That(readE[i].C, Is.EqualTo(i * 1.5).Within(0.0001), $"CompE[{i}].C should match");
            }
        }
    }

    /// <summary>
    /// Tests that reading multiple component instances within the same transaction after creation works correctly.
    /// </summary>
    [Test]
    public void CreateAndRead_WithinSameTransaction_SuccessfulOperation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(42);
        Span<CompE> eList = stackalloc CompE[3];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);
        eList[2] = new CompE(3.0f, 30, 300.0);

        using var t = dbe.CreateTransaction();
        var entityId = t.CreateEntity(ref a, eList);
        Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

        // Read within the same transaction before commit
        var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
        Assert.That(res, Is.True, "Read should succeed within same transaction");
        Assert.That(readA.A, Is.EqualTo(42), "CompA.A should match");
        Assert.That(readE.Length, Is.EqualTo(3), "Should have 3 CompE instances");

        for (int i = 0; i < 3; i++)
        {
            Assert.That(readE[i].A, Is.EqualTo((i + 1) * 1.0f), $"CompE[{i}].A should match");
            Assert.That(readE[i].B, Is.EqualTo((i + 1) * 10), $"CompE[{i}].B should match");
            Assert.That(readE[i].C, Is.EqualTo((i + 1) * 100.0), $"CompE[{i}].C should match");
        }

        var commitRes = t.Commit();
        Assert.That(commitRes, Is.True, "Commit should succeed");
    }

    #endregion

    #region Create Tests - Separate Transactions

    /// <summary>
    /// Tests creating an entity in one transaction and reading it in another.
    /// </summary>
    [Test]
    public void CreateEntity_ReadInSeparateTransaction_SuccessfulOperation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(123);
        Span<CompE> eList = stackalloc CompE[4];
        for (int i = 0; i < 4; i++)
        {
            eList[i] = new CompE(i + 0.5f, i * 100, i * 10.0);
        }

        long entityId;

        // Create in first transaction
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Read in separate transaction
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);

            Assert.That(res, Is.True, "Read should succeed in separate transaction");
            Assert.That(readA.A, Is.EqualTo(123), "CompA.A should match");
            Assert.That(readE.Length, Is.EqualTo(4), "Should have 4 CompE instances");

            for (int i = 0; i < 4; i++)
            {
                Assert.That(readE[i].A, Is.EqualTo(i + 0.5f), $"CompE[{i}].A should match");
                Assert.That(readE[i].B, Is.EqualTo(i * 100), $"CompE[{i}].B should match");
                Assert.That(readE[i].C, Is.EqualTo(i * 10.0), $"CompE[{i}].C should match");
            }
        }
    }

    /// <summary>
    /// Tests creating multiple entities with AllowMultiple components in separate transactions.
    /// </summary>
    [Test]
    public void CreateMultipleEntities_SeparateTransactions_SuccessfulOperations()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var entityIds = new long[3];

        // Create 3 entities in separate transactions
        // Pre-allocate outside of loop to avoid CA2014 warning
        Span<CompE> eListBuffer = stackalloc CompE[4]; // Max size needed (2+2 for e=2)
        for (int e = 0; e < 3; e++)
        {
            var a = new CompA(e * 1000);
            var componentCount = e + 2; // 2, 3, 4 components
            var eList = eListBuffer.Slice(0, componentCount);
            for (int i = 0; i < componentCount; i++)
            {
                eList[i] = new CompE(e * 10.0f + i, e * 100 + i, e * 1000.0 + i);
            }

            using var t = dbe.CreateTransaction();
            entityIds[e] = t.CreateEntity(ref a, eList);
            var res = t.Commit();
            Assert.That(res, Is.True, $"Commit for entity {e} should succeed");
        }

        // Verify all entities in a single transaction
        using (var t = dbe.CreateTransaction())
        {
            for (int e = 0; e < 3; e++)
            {
                var res = t.ReadEntity(entityIds[e], out CompA readA, out CompE[] readE);

                Assert.That(res, Is.True, $"Read for entity {e} should succeed");
                Assert.That(readA.A, Is.EqualTo(e * 1000), $"Entity {e}: CompA.A should match");
                Assert.That(readE.Length, Is.EqualTo(e + 2), $"Entity {e}: Should have {e + 2} CompE instances");
            }
        }
    }

    #endregion

    #region Read Tests

    /// <summary>
    /// Tests reading a non-existent entity returns false.
    /// </summary>
    [Test]
    public void ReadEntity_NonExistent_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateTransaction();
        var res = t.ReadEntity(999999L, out CompA _, out CompE[] readE);

        Assert.That(res, Is.False, "Reading non-existent entity should return false");
    }

    /// <summary>
    /// Tests that multiple reads of the same entity return consistent data.
    /// </summary>
    [Test]
    public void ReadEntity_MultipleTimes_ConsistentData()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(55);
        Span<CompE> eList = stackalloc CompE[3];
        for (int i = 0; i < 3; i++)
        {
            eList[i] = new CompE(i * 1.1f, i * 11, i * 111.0);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Read multiple times
        using (var t = dbe.CreateTransaction())
        {
            for (int readAttempt = 0; readAttempt < 5; readAttempt++)
            {
                var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);

                Assert.That(res, Is.True, $"Read attempt {readAttempt} should succeed");
                Assert.That(readA.A, Is.EqualTo(55), $"Read attempt {readAttempt}: CompA.A should match");
                Assert.That(readE.Length, Is.EqualTo(3), $"Read attempt {readAttempt}: Should have 3 CompE instances");
            }
        }
    }

    #endregion

    #region Delete Tests - Same Transaction

    /// <summary>
    /// Tests deleting CompA in a separate transaction from creation.
    /// Note: Deleting in the same transaction as creation may have different behavior.
    /// </summary>
    [Test]
    public void CreateThenDeleteCompA_SeparateTransactions_CompANotReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(77);
        Span<CompE> eList = stackalloc CompE[3];
        for (int i = 0; i < 3; i++)
        {
            eList[i] = new CompE(i, i, i);
        }

        long entityId;
        // Create in first transaction
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Delete CompA in second transaction
        using (var t = dbe.CreateTransaction())
        {
            var deletedA = t.DeleteEntity<CompA>(entityId);
            Assert.That(deletedA, Is.True, "Delete CompA should succeed");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify CompA is deleted
        using (var t = dbe.CreateTransaction())
        {
            var resA = t.ReadEntity(entityId, out CompA _);
            Assert.That(resA, Is.False, "CompA should not be readable after deletion");
        }
    }

    /// <summary>
    /// Tests deleting both single and multiple components using DeleteEntity with two type parameters.
    /// </summary>
    [Test]
    public void DeleteEntity_BothComponentTypes_SameTransaction_BothDeleted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(88);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 1, 1.0);
        eList[1] = new CompE(2.0f, 2, 2.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Delete both component types
        using (var t = dbe.CreateTransaction())
        {
            var deleted = t.DeleteEntity<CompA, CompE>(entityId);
            Assert.That(deleted, Is.True, "Delete should succeed");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify both are deleted
        using (var t = dbe.CreateTransaction())
        {
            var resA = t.ReadEntity(entityId, out CompA _);
            Assert.That(resA, Is.False, "CompA should not be readable after deletion");

            var resE = t.ReadEntity(entityId, out CompA _, out CompE[] readE);
            Assert.That(resE, Is.False, "CompE should not be readable after deletion");
        }
    }

    #endregion

    #region Delete Tests - Separate Transactions

    /// <summary>
    /// Tests creating in one transaction and deleting in another.
    /// </summary>
    [Test]
    public void CreateInOneTxn_DeleteInAnother_EntityDeleted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(99);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 1, 1.0);
        eList[1] = new CompE(2.0f, 2, 2.0);

        long entityId;

        // Create
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Verify exists
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _, out CompE[] readE);
            Assert.That(res, Is.True, "Entity should exist after creation");
            Assert.That(readE.Length, Is.EqualTo(2), "Should have 2 CompE instances");
        }

        // Delete in separate transaction
        using (var t = dbe.CreateTransaction())
        {
            var deleted = t.DeleteEntity<CompA, CompE>(entityId);
            Assert.That(deleted, Is.True, "Delete should succeed");
            t.Commit();
        }

        // Verify deleted
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(res, Is.False, "Entity should not be readable after deletion");
        }
    }

    #endregion

    #region Rollback Tests

    /// <summary>
    /// Tests that rolling back a create operation makes the entity unreadable.
    /// </summary>
    [Test]
    public void CreateAndRollback_EntityNotReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(111);
        Span<CompE> eList = stackalloc CompE[3];
        for (int i = 0; i < 3; i++)
        {
            eList[i] = new CompE(i, i, i);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Rollback();
            Assert.That(res, Is.True, "Rollback should succeed");
        }

        // Verify entity is not readable
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(res, Is.False, "Entity should not be readable after rollback");
        }
    }

    /// <summary>
    /// Tests that rolling back a create operation with AllowMultiple components makes the entity unreadable.
    /// </summary>
    [Test]
    public void CreateWithMultiple_ThenRollback_EntityNotReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(222);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(10.0f, 10, 10.0);
        eList[1] = new CompE(20.0f, 20, 20.0);

        long entityId;

        // Create entity with AllowMultiple components and rollback
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Rollback();
            Assert.That(res, Is.True, "Rollback should succeed");
        }

        // Verify entity is not readable after rollback
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _);
            Assert.That(res, Is.False, "Entity should not be readable after rollback");
        }
    }

    #endregion

    #region Edge Cases

    /// <summary>
    /// Tests creating multiple entities with varying numbers of AllowMultiple components.
    /// </summary>
    [Test]
    public void CreateEntities_VaryingComponentCounts_AllSuccessful()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var entityIds = new long[5];
        var componentCounts = new int[] { 1, 2, 5, 10, 20 };

        // Create entities with varying component counts
        for (int e = 0; e < 5; e++)
        {
            var a = new CompA(e);
            var eList = new CompE[componentCounts[e]];
            for (int i = 0; i < componentCounts[e]; i++)
            {
                eList[i] = new CompE(i * 0.1f, i, i * 0.01);
            }

            using var t = dbe.CreateTransaction();
            entityIds[e] = t.CreateEntity(ref a, eList.AsSpan());
            t.Commit();
        }

        // Verify all entities
        using (var t = dbe.CreateTransaction())
        {
            for (int e = 0; e < 5; e++)
            {
                var res = t.ReadEntity(entityIds[e], out CompA _, out CompE[] readE);
                Assert.That(res, Is.True, $"Read for entity {e} should succeed");
                Assert.That(readE.Length, Is.EqualTo(componentCounts[e]),
                    $"Entity {e} should have {componentCounts[e]} CompE instances");
            }
        }
    }

    /// <summary>
    /// Tests that the exact data values are preserved for AllowMultiple components.
    /// </summary>
    [Test]
    public void CreateEntity_SpecificValues_DataIntegrityPreserved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(int.MaxValue);
        Span<CompE> eList = stackalloc CompE[4];
        eList[0] = new CompE(float.MinValue, int.MinValue, double.MinValue);
        eList[1] = new CompE(float.MaxValue, int.MaxValue, double.MaxValue);
        eList[2] = new CompE(0.0f, 0, 0.0);
        eList[3] = new CompE(-1.5f, -100, -999.999);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Verify exact values
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);

            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(int.MaxValue), "CompA.A should match");
            Assert.That(readE.Length, Is.EqualTo(4), "Should have 4 CompE instances");

            Assert.That(readE[0].A, Is.EqualTo(float.MinValue), "CompE[0].A should match float.MinValue");
            Assert.That(readE[0].B, Is.EqualTo(int.MinValue), "CompE[0].B should match int.MinValue");
            Assert.That(readE[0].C, Is.EqualTo(double.MinValue), "CompE[0].C should match double.MinValue");

            Assert.That(readE[1].A, Is.EqualTo(float.MaxValue), "CompE[1].A should match float.MaxValue");
            Assert.That(readE[1].B, Is.EqualTo(int.MaxValue), "CompE[1].B should match int.MaxValue");
            Assert.That(readE[1].C, Is.EqualTo(double.MaxValue), "CompE[1].C should match double.MaxValue");

            Assert.That(readE[2].A, Is.EqualTo(0.0f), "CompE[2].A should match 0");
            Assert.That(readE[2].B, Is.EqualTo(0), "CompE[2].B should match 0");
            Assert.That(readE[2].C, Is.EqualTo(0.0), "CompE[2].C should match 0");

            Assert.That(readE[3].A, Is.EqualTo(-1.5f), "CompE[3].A should match -1.5");
            Assert.That(readE[3].B, Is.EqualTo(-100), "CompE[3].B should match -100");
            Assert.That(readE[3].C, Is.EqualTo(-999.999), "CompE[3].C should match -999.999");
        }
    }

    /// <summary>
    /// Tests concurrent reads of the same entity with AllowMultiple components.
    /// </summary>
    [Test]
    public void ConcurrentReads_SameEntity_AllSuccessful()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(333);
        Span<CompE> eList = stackalloc CompE[5];
        for (int i = 0; i < 5; i++)
        {
            eList[i] = new CompE(i * 1.0f, i, i * 1.0);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Create multiple read transactions simultaneously
        var t1 = dbe.CreateTransaction();
        var t2 = dbe.CreateTransaction();
        var t3 = dbe.CreateTransaction();

        try
        {
            var res1 = t1.ReadEntity(entityId, out CompA _, out CompE[] readE1);
            var res2 = t2.ReadEntity(entityId, out CompA _, out CompE[] readE2);
            var res3 = t3.ReadEntity(entityId, out CompA _, out CompE[] readE3);

            Assert.That(res1, Is.True, "Read 1 should succeed");
            Assert.That(res2, Is.True, "Read 2 should succeed");
            Assert.That(res3, Is.True, "Read 3 should succeed");

            Assert.That(readE1.Length, Is.EqualTo(5), "Read 1 should have 5 components");
            Assert.That(readE2.Length, Is.EqualTo(5), "Read 2 should have 5 components");
            Assert.That(readE3.Length, Is.EqualTo(5), "Read 3 should have 5 components");
        }
        finally
        {
            t1.Dispose();
            t2.Dispose();
            t3.Dispose();
        }
    }

    /// <summary>
    /// Tests that reading only CompA from an entity with both CompA and CompE works.
    /// </summary>
    [Test]
    public void ReadOnlyCompA_FromEntityWithMultiple_SuccessfulOperation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(444);
        Span<CompE> eList = stackalloc CompE[3];
        for (int i = 0; i < 3; i++)
        {
            eList[i] = new CompE(i, i, i);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Read only CompA
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA);
            Assert.That(res, Is.True, "Reading only CompA should succeed");
            Assert.That(readA.A, Is.EqualTo(444), "CompA.A should match");
        }
    }

    /// <summary>
    /// Tests revision tracking for both single and AllowMultiple components.
    /// </summary>
    [Test]
    public void CreateEntity_RevisionTracking_BothComponentTypesCorrect()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(555);
        Span<CompE> eList = stackalloc CompE[3];
        for (int i = 0; i < 3; i++)
        {
            eList[i] = new CompE(i, i, i);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);

            // Check revisions before commit
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1),
                "CompA revision should be 1 after create");
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(1),
                "CompE revision should be 1 after create");

            t.Commit();

            // Check revisions after commit
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1),
                "CompA revision should still be 1 after commit");
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(1),
                "CompE revision should still be 1 after commit");
        }

        // Check revisions in new transaction
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA _, out CompE[] _);

            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(1),
                "CompA revision should be 1 in new transaction");
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(1),
                "CompE revision should be 1 in new transaction");
        }
    }

    /// <summary>
    /// Tests creating entities with AllowMultiple components interleaved with noise operations.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), new object[] { 2 })]
    public void CreateWithMultiple_WithNoise_SuccessfulOperation(int noiseMode, bool noiseOwnTransaction)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long[] noiseIds = null;
        if (noiseMode >= 1)
        {
            noiseIds = CreateNoiseCompA(dbe);
        }

        var a = new CompA(666);
        Span<CompE> eList = stackalloc CompE[4];
        for (int i = 0; i < 4; i++)
        {
            eList[i] = new CompE(i * 1.5f, i * 15, i * 150.0);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            if (noiseMode >= 2)
            {
                UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
            }

            entityId = t.CreateEntity(ref a, eList);
            Assert.That(entityId, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(666), "CompA.A should match");
            Assert.That(readE.Length, Is.EqualTo(4), "Should have 4 CompE instances");
        }
    }

    /// <summary>
    /// Tests that DeleteEntity with two type parameters deletes both components.
    /// Note: DeleteEntity<T> for a single AllowMultiple type is not supported.
    /// Use DeleteEntity<TC1, TC2> to delete both component types together.
    /// </summary>
    [Test]
    public void DeleteBothTypes_BothDeleted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(777);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 1, 1.0);
        eList[1] = new CompE(2.0f, 2, 2.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Delete both types using DeleteEntity<TC1, TC2>
        using (var t = dbe.CreateTransaction())
        {
            var deleted = t.DeleteEntity<CompA, CompE>(entityId);
            Assert.That(deleted, Is.True, "Delete should succeed");
            t.Commit();
        }

        // Verify both are deleted
        using (var t = dbe.CreateTransaction())
        {
            var resA = t.ReadEntity(entityId, out CompA _);
            Assert.That(resA, Is.False, "CompA should not be readable after deletion");

            var resE = t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(resE, Is.False, "Entity should not be readable after deletion");
        }
    }

    /// <summary>
    /// Tests that updating CompA doesn't affect CompE components.
    /// </summary>
    [Test]
    public void UpdateCompA_CompEUnchanged()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(888);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(11.0f, 111, 1111.0);
        eList[1] = new CompE(22.0f, 222, 2222.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Update only CompA
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(999);
            var updated = t.UpdateEntity(entityId, ref updatedA);
            Assert.That(updated, Is.True, "Update should succeed");
            t.Commit();
        }

        // Verify CompA is updated but CompE is unchanged
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(999), "CompA.A should be updated");
            Assert.That(readE.Length, Is.EqualTo(2), "Should still have 2 CompE instances");
            Assert.That(readE[0].A, Is.EqualTo(11.0f), "CompE[0] should be unchanged");
            Assert.That(readE[1].A, Is.EqualTo(22.0f), "CompE[1] should be unchanged");
        }
    }

    #endregion

    #region Update Tests - AllowMultiple with Span

    /// <summary>
    /// Tests updating AllowMultiple components with the same count as before.
    /// </summary>
    [Test]
    public void UpdateEntity_SameCount_AllComponentsUpdated()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[3];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);
        eList[2] = new CompE(3.0f, 30, 300.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Update with same count (3 items)
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(200);
            Span<CompE> updatedE = stackalloc CompE[3];
            updatedE[0] = new CompE(10.0f, 100, 1000.0);
            updatedE[1] = new CompE(20.0f, 200, 2000.0);
            updatedE[2] = new CompE(30.0f, 300, 3000.0);

            var res = t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            Assert.That(res, Is.True, "Update should succeed");

            t.Commit();
        }

        // Verify all components are updated
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(200), "CompA.A should be updated");
            Assert.That(readE.Length, Is.EqualTo(3), "Should still have 3 CompE instances");
            Assert.That(readE[0].A, Is.EqualTo(10.0f), "CompE[0].A should be updated");
            Assert.That(readE[0].B, Is.EqualTo(100), "CompE[0].B should be updated");
            Assert.That(readE[1].A, Is.EqualTo(20.0f), "CompE[1].A should be updated");
            Assert.That(readE[2].A, Is.EqualTo(30.0f), "CompE[2].A should be updated");
        }
    }

    /// <summary>
    /// Tests updating AllowMultiple components with more items than before.
    /// </summary>
    [Test]
    public void UpdateEntity_MoreItems_ComponentsAdded()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Update with more items (2 -> 5)
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(200);
            Span<CompE> updatedE = stackalloc CompE[5];
            for (int i = 0; i < 5; i++)
            {
                updatedE[i] = new CompE(i * 10.0f, i * 100, i * 1000.0);
            }

            var res = t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            Assert.That(res, Is.True, "Update should succeed");

            t.Commit();
        }

        // Verify all components including new ones
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(200), "CompA.A should be updated");
            Assert.That(readE.Length, Is.EqualTo(5), "Should now have 5 CompE instances");

            for (int i = 0; i < 5; i++)
            {
                Assert.That(readE[i].A, Is.EqualTo(i * 10.0f), $"CompE[{i}].A should match");
                Assert.That(readE[i].B, Is.EqualTo(i * 100), $"CompE[{i}].B should match");
                Assert.That(readE[i].C, Is.EqualTo(i * 1000.0), $"CompE[{i}].C should match");
            }
        }
    }

    /// <summary>
    /// Tests updating AllowMultiple components with fewer items than before.
    /// </summary>
    [Test]
    public void UpdateEntity_FewerItems_ExcessComponentsRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[5];
        for (int i = 0; i < 5; i++)
        {
            eList[i] = new CompE(i * 1.0f, i * 10, i * 100.0);
        }

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Update with fewer items (5 -> 2)
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(200);
            Span<CompE> updatedE = stackalloc CompE[2];
            updatedE[0] = new CompE(99.0f, 990, 9900.0);
            updatedE[1] = new CompE(88.0f, 880, 8800.0);

            var res = t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            Assert.That(res, Is.True, "Update should succeed");

            t.Commit();
        }

        // Verify only 2 components remain
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(200), "CompA.A should be updated");
            Assert.That(readE.Length, Is.EqualTo(2), "Should now have only 2 CompE instances");
            Assert.That(readE[0].A, Is.EqualTo(99.0f), "CompE[0].A should match new value");
            Assert.That(readE[1].A, Is.EqualTo(88.0f), "CompE[1].A should match new value");
        }
    }

    /// <summary>
    /// Tests updating AllowMultiple components in the same transaction as creation.
    /// </summary>
    [Test]
    public void UpdateEntity_SameTransactionAsCreate_UpdateApplied()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(1), "CompE revision should be 1 after create");

            // Update in same transaction
            var updatedA = new CompA(999);
            Span<CompE> updatedE = stackalloc CompE[3];
            updatedE[0] = new CompE(10.0f, 100, 1000.0);
            updatedE[1] = new CompE(20.0f, 200, 2000.0);
            updatedE[2] = new CompE(30.0f, 300, 3000.0);

            var res = t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            Assert.That(res, Is.True, "Update in same transaction should succeed");

            // Read within same transaction
            res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read in same transaction should succeed");
            Assert.That(readA.A, Is.EqualTo(999), "CompA.A should be updated within transaction");
            Assert.That(readE.Length, Is.EqualTo(3), "Should have 3 CompE instances");

            t.Commit();
        }

        // Verify after commit
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(999), "CompA.A should be updated");
            Assert.That(readE.Length, Is.EqualTo(3), "Should have 3 CompE instances");
        }
    }

    /// <summary>
    /// Tests multiple updates to AllowMultiple components in separate transactions.
    /// </summary>
    [Test]
    public void UpdateEntity_MultipleUpdates_SeparateTransactions_AllApplied()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 1, 1.0);
        eList[1] = new CompE(2.0f, 2, 2.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // First update: 2 -> 3 items
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(200);
            Span<CompE> updatedE = stackalloc CompE[3];
            updatedE[0] = new CompE(10.0f, 10, 10.0);
            updatedE[1] = new CompE(20.0f, 20, 20.0);
            updatedE[2] = new CompE(30.0f, 30, 30.0);

            t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            t.Commit();
        }

        // Second update: 3 -> 1 item
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(300);
            Span<CompE> updatedE = stackalloc CompE[1];
            updatedE[0] = new CompE(999.0f, 999, 999.0);

            t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            t.Commit();
        }

        // Third update: 1 -> 4 items
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(400);
            Span<CompE> updatedE = stackalloc CompE[4];
            for (int i = 0; i < 4; i++)
            {
                updatedE[i] = new CompE(i * 100.0f, i * 1000, i * 10000.0);
            }

            t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            t.Commit();
        }

        // Verify final state
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(400), "CompA.A should reflect last update");
            Assert.That(readE.Length, Is.EqualTo(4), "Should have 4 CompE instances after final update");

            for (int i = 0; i < 4; i++)
            {
                Assert.That(readE[i].A, Is.EqualTo(i * 100.0f), $"CompE[{i}].A should match");
            }
        }
    }

    /// <summary>
    /// Tests that updating AllowMultiple components increments the revision.
    /// </summary>
    [Test]
    public void UpdateEntity_AllowMultiple_RevisionIncremented()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(1), "Initial CompE revision should be 1");
            t.Commit();
        }

        // Update in separate transaction
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(200);
            Span<CompE> updatedE = stackalloc CompE[2];
            updatedE[0] = new CompE(10.0f, 100, 1000.0);
            updatedE[1] = new CompE(20.0f, 200, 2000.0);

            t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);

            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(2), "CompA revision should be 2 after update");
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(2), "CompE revision should be 2 after update");

            t.Commit();
        }

        // Verify revision in new transaction
        using (var t = dbe.CreateTransaction())
        {
            t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(t.GetComponentRevision<CompA>(entityId), Is.EqualTo(2), "CompA revision should be 2");
            Assert.That(t.GetComponentRevision<CompE>(entityId), Is.EqualTo(2), "CompE revision should be 2");
        }
    }

    /// <summary>
    /// Tests updating AllowMultiple components and then rolling back.
    /// </summary>
    [Test]
    public void UpdateEntity_ThenRollback_OriginalValuesPreserved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Update and rollback
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(999);
            Span<CompE> updatedE = stackalloc CompE[5];
            for (int i = 0; i < 5; i++)
            {
                updatedE[i] = new CompE(i * 100.0f, i * 1000, i * 10000.0);
            }

            var res = t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)updatedE);
            Assert.That(res, Is.True, "Update should succeed");

            t.Rollback();
        }

        // Verify original values preserved
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(100), "CompA.A should be original value after rollback");
            Assert.That(readE.Length, Is.EqualTo(2), "Should still have 2 CompE instances after rollback");
            Assert.That(readE[0].A, Is.EqualTo(1.0f), "CompE[0].A should be original value");
            Assert.That(readE[1].A, Is.EqualTo(2.0f), "CompE[1].A should be original value");
        }
    }

    #endregion

    #region DeleteEntities Tests - AllowMultiple Only

    /// <summary>
    /// Tests deleting only AllowMultiple components using DeleteEntities<T>, leaving single components intact.
    /// </summary>
    [Test]
    public void DeleteEntities_OnlyCompE_CompARemains()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[3];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);
        eList[2] = new CompE(3.0f, 30, 300.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Delete only CompE using DeleteEntities<T>
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntities<CompE>(entityId);
            Assert.That(res, Is.True, "DeleteEntities<CompE> should succeed");
            t.Commit();
        }

        // Verify CompA still exists but CompE is gone
        using (var t = dbe.CreateTransaction())
        {
            var resA = t.ReadEntity(entityId, out CompA readA);
            Assert.That(resA, Is.True, "CompA should still be readable");
            Assert.That(readA.A, Is.EqualTo(100), "CompA.A should match original value");

            // Reading with CompE should fail since CompE is deleted
            var resBoth = t.ReadEntity(entityId, out CompA _, out CompE[] readE);
            Assert.That(resBoth, Is.False, "Reading entity with deleted CompE should fail");
        }
    }

    /// <summary>
    /// Tests deleting AllowMultiple components in the same transaction as creation.
    /// </summary>
    [Test]
    public void DeleteEntities_SameTransactionAsCreate_ComponentsDeleted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);

            // Delete CompE in same transaction
            var res = t.DeleteEntities<CompE>(entityId);
            Assert.That(res, Is.True, "DeleteEntities in same transaction should succeed");

            t.Commit();
        }

        // Verify CompA exists but CompE is gone
        using (var t = dbe.CreateTransaction())
        {
            var resA = t.ReadEntity(entityId, out CompA readA);
            Assert.That(resA, Is.True, "CompA should be readable");
            Assert.That(readA.A, Is.EqualTo(100), "CompA.A should match");

            var resBoth = t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(resBoth, Is.False, "CompE should be deleted");
        }
    }

    /// <summary>
    /// Tests deleting AllowMultiple components and then rolling back.
    /// </summary>
    [Test]
    public void DeleteEntities_ThenRollback_ComponentsRestored()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Delete and rollback
        using (var t = dbe.CreateTransaction())
        {
            var res = t.DeleteEntities<CompE>(entityId);
            Assert.That(res, Is.True, "DeleteEntities should succeed");

            t.Rollback();
        }

        // Verify components still exist
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Entity should still be readable after rollback");
            Assert.That(readA.A, Is.EqualTo(100), "CompA.A should match");
            Assert.That(readE.Length, Is.EqualTo(2), "Should still have 2 CompE instances");
            Assert.That(readE[0].A, Is.EqualTo(1.0f), "CompE[0].A should match original");
            Assert.That(readE[1].A, Is.EqualTo(2.0f), "CompE[1].A should match original");
        }
    }

    /// <summary>
    /// Tests creating new AllowMultiple components after deleting them.
    /// </summary>
    [Test]
    public void DeleteEntities_ThenRecreate_NewComponentsCreated()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(100);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 10, 100.0);
        eList[1] = new CompE(2.0f, 20, 200.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Delete CompE
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntities<CompE>(entityId);
            t.Commit();
        }

        // Verify deleted
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(res, Is.False, "CompE should be deleted");
        }

        // Update to recreate CompE with new values
        using (var t = dbe.CreateTransaction())
        {
            var updatedA = new CompA(200);
            Span<CompE> newE = stackalloc CompE[4];
            for (int i = 0; i < 4; i++)
            {
                newE[i] = new CompE(i * 50.0f, i * 500, i * 5000.0);
            }

            var res = t.UpdateEntity(entityId, ref updatedA, (ReadOnlySpan<CompE>)newE);
            Assert.That(res, Is.True, "Update to recreate CompE should succeed");
            t.Commit();
        }

        // Verify new components
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Read should succeed");
            Assert.That(readA.A, Is.EqualTo(200), "CompA.A should be updated");
            Assert.That(readE.Length, Is.EqualTo(4), "Should have 4 new CompE instances");
        }
    }

    #endregion

    #region MVCC Isolation Tests

    /// <summary>
    /// Tests that a transaction started before creation can still read newly created entities.
    /// Note: This documents the current behavior where new entities are visible to existing transactions.
    /// </summary>
    [Test]
    public void TransactionStartedBeforeCreate_CanSeeNewEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Start a transaction before creation
        var earlyTransaction = dbe.CreateTransaction();

        var a = new CompA(1000);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(1.0f, 1, 1.0);
        eList[1] = new CompE(2.0f, 2, 2.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        try
        {
            // Early transaction can read newly created entities (current behavior)
            var res = earlyTransaction.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Transaction can see newly created entity");
            Assert.That(readA.A, Is.EqualTo(1000), "CompA.A should match");
            Assert.That(readE.Length, Is.EqualTo(2), "Should have 2 CompE instances");
        }
        finally
        {
            earlyTransaction.Dispose();
        }
    }

    /// <summary>
    /// Tests that a transaction started before deletion still sees the entity.
    /// </summary>
    [Test]
    public void MVCCIsolation_TransactionStartedBeforeDelete_StillSeesEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var a = new CompA(1111);
        Span<CompE> eList = stackalloc CompE[2];
        eList[0] = new CompE(10.0f, 10, 10.0);
        eList[1] = new CompE(20.0f, 20, 20.0);

        long entityId;
        using (var t = dbe.CreateTransaction())
        {
            entityId = t.CreateEntity(ref a, eList);
            t.Commit();
        }

        // Start a transaction before deletion
        var earlyTransaction = dbe.CreateTransaction();

        // Read once to establish snapshot
        earlyTransaction.ReadEntity(entityId, out CompA _, out CompE[] _);

        // Delete in a separate transaction
        using (var t = dbe.CreateTransaction())
        {
            t.DeleteEntity<CompA, CompE>(entityId);
            t.Commit();
        }

        try
        {
            // Early transaction should still see the entity (snapshot isolation)
            var res = earlyTransaction.ReadEntity(entityId, out CompA readA, out CompE[] readE);
            Assert.That(res, Is.True, "Transaction started before deletion should still see the entity");
            Assert.That(readA.A, Is.EqualTo(1111), "CompA.A should match original value");
            Assert.That(readE.Length, Is.EqualTo(2), "Should still have 2 CompE instances");
        }
        finally
        {
            earlyTransaction.Dispose();
        }

        // New transaction should not see the entity
        using (var t = dbe.CreateTransaction())
        {
            var res = t.ReadEntity(entityId, out CompA _, out CompE[] _);
            Assert.That(res, Is.False, "New transaction should not see deleted entity");
        }
    }

    #endregion
}
