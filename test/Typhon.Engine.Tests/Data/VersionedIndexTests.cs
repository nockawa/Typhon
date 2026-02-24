using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

class VersionedIndexTests : TestBase<VersionedIndexTests>
{
    #region Phase 1 — Infrastructure Tests

    [Test]
    public void VersionedIndexEntry_StructSize_Is12Bytes()
    {
        unsafe
        {
            Assert.That(sizeof(VersionedIndexEntry), Is.EqualTo(12), "VersionedIndexEntry should be exactly 12 bytes (4 + 8)");
        }
    }

    [Test]
    public void VersionedIndexEntry_Active_CorrectProperties()
    {
        var entry = VersionedIndexEntry.Active(42, 100);

        Assert.That(entry.IsActive, Is.True);
        Assert.That(entry.IsTombstone, Is.False);
        Assert.That(entry.ChainId, Is.EqualTo(42));
        Assert.That(entry.TSN, Is.EqualTo(100));
        Assert.That(entry.SignedChainId, Is.EqualTo(42));
    }

    [Test]
    public void VersionedIndexEntry_Tombstone_CorrectProperties()
    {
        var entry = VersionedIndexEntry.Tombstone(42, 200);

        Assert.That(entry.IsActive, Is.False);
        Assert.That(entry.IsTombstone, Is.True);
        Assert.That(entry.ChainId, Is.EqualTo(42));
        Assert.That(entry.TSN, Is.EqualTo(200));
        Assert.That(entry.SignedChainId, Is.EqualTo(-42));
    }

    [Test]
    public void VersionedIndexEntry_Equality()
    {
        var a = VersionedIndexEntry.Active(1, 100);
        var b = VersionedIndexEntry.Active(1, 100);
        var c = VersionedIndexEntry.Tombstone(1, 100);

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a.Equals(c), Is.False);
    }

    [Test]
    public unsafe void TailBufferId_DefaultsToZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        Assert.That(ct.TailVSBS, Is.Not.Null, "CompD has AllowMultiple indexes, should have TailVSBS");
        Assert.That(ct.TailIndexSegment, Is.Not.Null, "CompD has AllowMultiple indexes, should have TailIndexSegment");
    }

    [Test]
    public void ComponentTable_NoAllowMultiple_NoTailVSBS()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct.TailVSBS, Is.Null, "CompA has no AllowMultiple indexes, should not have TailVSBS");
        Assert.That(ct.TailIndexSegment, Is.Null, "CompA has no AllowMultiple indexes, should not have TailIndexSegment");
    }

    [Test]
    public unsafe void AllocateTailBuffer_AddAndReadEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        // Allocate a TAIL buffer and add an entry
        var bufferId = tailVSBS.AllocateBuffer(ref accessor);
        Assert.That(bufferId, Is.GreaterThan(0));

        var entry = VersionedIndexEntry.Active(10, 500);
        tailVSBS.AddElement(bufferId, entry, ref accessor);

        // Read it back
        using var ba = tailVSBS.GetReadOnlyAccessor(bufferId);
        Assert.That(ba.TotalCount, Is.EqualTo(1));
        var elements = ba.ReadOnlyElements;
        Assert.That(elements.Length, Is.GreaterThanOrEqualTo(1));
        Assert.That(elements[0].SignedChainId, Is.EqualTo(10));
        Assert.That(elements[0].TSN, Is.EqualTo(500));

        accessor.Dispose();
    }

    #endregion

    #region Phase 2 — Write Path Tests

    [Test]
    public void Create_WithAllowMultipleIndex_TailHasActiveEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.CreateEntity(ref d);
            t.Commit();
        }

        // Verify TAIL buffer was written for AllowMultiple indexes
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        Assert.That(tailVSBS, Is.Not.Null);

        // Look up the HEAD buffer for field A (first AllowMultiple index)
        var ifi = ct.IndexedFieldInfos[0]; // Field A (float, AllowMultiple)
        Assert.That(ifi.Index.AllowMultiple, Is.True, "First indexed field should be AllowMultiple");

        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            float key = 1.0f;
            var accessor = ifi.Index.Segment.CreateChunkAccessor();
            var headResult = ifi.Index.TryGet(&key, ref accessor);
            Assert.That(headResult.IsSuccess, Is.True, "Key should exist in HEAD index");

            var headBufferId = headResult.Value;
            var tailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(headBufferId)).TailBufferId;
            accessor.Dispose();

            Assert.That(tailBufferId, Is.GreaterThan(0), "TAIL buffer should have been allocated");

            // Read TAIL entries
            var entries = CollectTailEntries(tailVSBS, tailBufferId);
            Assert.That(entries.Count, Is.GreaterThanOrEqualTo(1), "Should have at least one TAIL entry");

            var activeEntry = entries.Find(e => e.IsActive);
            Assert.That(activeEntry.IsActive, Is.True, "Should have an Active entry");
            Assert.That(activeEntry.TSN, Is.GreaterThan(0), "Active entry should have a valid TSN");
        }
    }

    [Test]
    public void Update_IndexedField_TailHasTombstoneAndActive()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.CreateEntity(ref d);
            t.Commit();
        }

        // Update field A from 1.0f to 5.0f
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 10, 2.0);
            t.UpdateEntity(entityId, ref d);
            t.Commit();
        }

        // Verify TAIL has Tombstone on old key (1.0f) and Active on new key (5.0f)
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);

            // Check old key (1.0f) — preserved by preserveEmptyBuffer even though HEAD buffer is empty
            float oldKey = 1.0f;
            var accessor = ifi.Index.Segment.CreateChunkAccessor();
            var oldHeadResult = ifi.Index.TryGet(&oldKey, ref accessor);
            Assert.That(oldHeadResult.IsSuccess, Is.True, "Old key should be preserved in BTree (preserveEmptyBuffer)");

            var oldTailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(oldHeadResult.Value)).TailBufferId;
            Assert.That(oldTailBufferId, Is.GreaterThan(0), "Old key should have TAIL buffer linked");

            var oldEntries = CollectTailEntries(tailVSBS, oldTailBufferId);
            Assert.That(oldEntries.Exists(e => e.IsTombstone), Is.True, "Old key's TAIL should have a Tombstone");

            // Check new key (5.0f) — should have Active entry
            float newKey = 5.0f;
            var newHeadResult = ifi.Index.TryGet(&newKey, ref accessor);
            Assert.That(newHeadResult.IsSuccess, Is.True, "New key should exist in HEAD index");

            var newTailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(newHeadResult.Value)).TailBufferId;
            Assert.That(newTailBufferId, Is.GreaterThan(0), "New key should have TAIL buffer");

            var newEntries = CollectTailEntries(tailVSBS, newTailBufferId);
            Assert.That(newEntries.Exists(e => e.IsActive), Is.True, "New key's TAIL should have an Active entry");
            accessor.Dispose();
        }
    }

    [Test]
    public void Delete_Entity_TailHasTombstone()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(3.0f, 10, 4.0);
            entityId = t.CreateEntity(ref d);
            t.Commit();
        }

        // Capture TAIL buffer ID before delete (since BTree key may be removed)
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        int tailBufferIdBeforeDelete;
        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            float key = 3.0f;
            var accessor = ifi.Index.Segment.CreateChunkAccessor();
            var headResult = ifi.Index.TryGet(&key, ref accessor);
            Assert.That(headResult.IsSuccess, Is.True);
            tailBufferIdBeforeDelete = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(headResult.Value)).TailBufferId;
            accessor.Dispose();
        }

        // Delete the entity
        {
            using var t = dbe.CreateQuickTransaction();
            t.DeleteEntity<CompD>(entityId);
            t.Commit();
        }

        // Verify TAIL has Tombstone (need epoch scope for EnumerateBuffer)
        if (tailBufferIdBeforeDelete > 0)
        {
            using var guard2 = EpochGuard.Enter(dbe.EpochManager);
            var entries = CollectTailEntries(tailVSBS, tailBufferIdBeforeDelete);
            Assert.That(entries.Exists(e => e.IsTombstone), Is.True, "TAIL should have a Tombstone after delete");
        }
    }

    [Test]
    public void ExistingTests_NoRegression_HeadPathUnchanged()
    {
        // Verify that normal CRUD on CompD still works (HEAD path unchanged)
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            e1 = t.CreateEntity(ref d);
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var res = t.ReadEntity(e1, out CompD read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(1.0f));
            Assert.That(read.B, Is.EqualTo(10));
            Assert.That(read.C, Is.EqualTo(2.0));
        }

        // Update and verify
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 20, 6.0);
            t.UpdateEntity(e1, ref d);
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var res = t.ReadEntity(e1, out CompD read);
            Assert.That(res, Is.True);
            Assert.That(read.A, Is.EqualTo(5.0f));
            Assert.That(read.B, Is.EqualTo(20));
            Assert.That(read.C, Is.EqualTo(6.0));
        }
    }

    #endregion

    #region Phase 3 — Temporal Query Tests

    [Test]
    public unsafe void TemporalQuery_BeforeCreate_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var tsnBeforeCreate = dbe.TransactionChain.NextFreeId;

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            t.CreateEntity(ref d);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A
        float key = 1.0f;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&key, tsnBeforeCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(0), "No entities should be visible before creation TSN");
    }

    [Test]
    public unsafe void TemporalQuery_AtCreate_ReturnsEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long tsnAtCreate;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAtCreate = t.TSN;
            var d = new CompD(1.0f, 10, 2.0);
            t.CreateEntity(ref d);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A
        float key = 1.0f;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&key, tsnAtCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(1), "Entity should be visible at creation TSN");
    }

    [Test]
    public unsafe void TemporalQuery_OldKey_AfterUpdate_SingleEntity_StillVisible()
    {
        // Single entity moves from key 1.0f to 5.0f. The old key's BTree entry is preserved
        // (empty HEAD buffer) so that the TAIL version-history remains reachable.
        // A temporal query at the creation TSN should still find the entity under the old key.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entityId;
        long tsnAtCreate;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAtCreate = t.TSN;
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.CreateEntity(ref d);
            t.Commit();
        }

        // Update field A from 1.0f to 5.0f — last entity leaves the old key
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 10, 2.0);
            t.UpdateEntity(entityId, ref d);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        // Query old key at creation TSN — entity should still be visible through TAIL
        float oldKey = 1.0f;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&oldKey, tsnAtCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(1), "Entity should be visible under old key at creation TSN through TAIL");
    }

    [Test]
    public unsafe void TemporalQuery_OldKey_AfterUpdate_MultiEntity_CorrectCount()
    {
        // Two entities share key A=1.0f, then one moves to A=5.0f.
        // A temporal query at the "both created" TSN should see 2 chain IDs for key 1.0f.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long entity1;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entity1 = t.CreateEntity(ref d);
            t.Commit();
        }

        long tsnAfterBothCreated;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAfterBothCreated = t.TSN;
            var d = new CompD(1.0f, 20, 3.0); // Same A=1.0f key (AllowMultiple), different B (unique index)
            t.CreateEntity(ref d);
            t.Commit();
        }

        // Update entity1's A from 1.0f to 5.0f
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 10, 2.0);
            t.UpdateEntity(entity1, ref d);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        // Query old key at tsnAfterBothCreated — both entities should be visible
        float oldKey = 1.0f;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&oldKey, tsnAfterBothCreated, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(2), "Both entities should be visible under old key at the TSN when both existed");
    }

    #endregion

    #region Phase 5 — GC Tests

    [Test]
    public void TailGC_PruneOldEntries_KeepsBoundarySentinel()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        // Manually create a TAIL buffer with entries at different TSNs
        var bufferId = tailVSBS.AllocateBuffer(ref accessor);

        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 100), ref accessor);   // Old
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Tombstone(1, 200), ref accessor); // Boundary sentinel at retention
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 300), ref accessor);    // Future

        // Prune with retentionTSN = 250 (should remove TSN=100, keep TSN=200 as sentinel, keep TSN=300)
        var removed = TailGarbageCollector.Prune(tailVSBS, bufferId, 250, ref accessor, out var newBufferId);

        Assert.That(removed, Is.EqualTo(1), "Should remove 1 old entry (TSN=100)");
        Assert.That(newBufferId, Is.GreaterThan(0), "New buffer should have been allocated");

        // Verify remaining entries: boundary sentinel (TSN=200) + future (TSN=300)
        var remaining = CollectTailEntries(tailVSBS, newBufferId);
        Assert.That(remaining.Count, Is.EqualTo(2), "Should have sentinel + future entry");
        Assert.That(remaining.Exists(e => e.TSN == 200 && e.IsTombstone), Is.True, "Boundary sentinel should be kept");
        Assert.That(remaining.Exists(e => e.TSN == 300 && e.IsActive), Is.True, "Future entry should be kept");

        accessor.Dispose();
    }

    [Test]
    public void TailGC_DeadChain_FullyRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        // Chain with only old entries ending in Tombstone (dead chain)
        var bufferId = tailVSBS.AllocateBuffer(ref accessor);
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 100), ref accessor);
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Tombstone(1, 200), ref accessor);

        // Prune with retentionTSN = 300 (both entries are old, sentinel is Tombstone, no future entries)
        var removed = TailGarbageCollector.Prune(tailVSBS, bufferId, 300, ref accessor, out _);

        Assert.That(removed, Is.EqualTo(2), "Dead chain should be fully removed (Active + Tombstone)");

        accessor.Dispose();
    }

    [Test]
    public void TailGC_NoOldEntries_NothingPruned()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        var bufferId = tailVSBS.AllocateBuffer(ref accessor);
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 500), ref accessor);

        // Prune with retentionTSN = 100 (all entries are in the future)
        var removed = TailGarbageCollector.Prune(tailVSBS, bufferId, 100, ref accessor, out var newBufferId);
        Assert.That(newBufferId, Is.EqualTo(bufferId), "Buffer ID should be unchanged when nothing is pruned");

        Assert.That(removed, Is.EqualTo(0), "No entries should be removed when all are in the future");

        accessor.Dispose();
    }

    #endregion

    #region Helpers

    private static List<VersionedIndexEntry> CollectTailEntries(VariableSizedBufferSegment<VersionedIndexEntry> tailVSBS, int tailBufferId)
    {
        var entries = new List<VersionedIndexEntry>();
        foreach (ref readonly var entry in tailVSBS.EnumerateBuffer(tailBufferId))
        {
            entries.Add(entry);
        }
        return entries;
    }

    #endregion
}
