using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// TickFence tests — no WAL needed (tests DirtyBitmap integration + skip behavior)
// ═══════════════════════════════════════════════════════════════════════

[NonParallelizable]
class StorageModeTickFenceTests : TestBase<StorageModeTickFenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<SvTestArchetype>.Touch();
        Archetype<TransientTestArchetype>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.RegisterComponentFromAccessor<CompSmVersioned>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void WriteTickFence_NoWal_ReturnsZero()
    {
        using var dbe = SetupEngine();

        // Default TestBase has no WAL configured
        Assert.That(dbe.WalManager, Is.Null);
        var lsn = dbe.WriteTickFence(1);
        Assert.That(lsn, Is.EqualTo(0));
    }

    [Test]
    public void WriteTickFence_NoDirty_ReturnsZero_NoWal()
    {
        using var dbe = SetupEngine();

        // Even without WAL, verify the method gracefully returns 0 when no dirty entities
        var lsn = dbe.WriteTickFence(1);
        Assert.That(lsn, Is.EqualTo(0));
    }

    [Test]
    public void WriteTickFence_ClearsDirtyBitmap()
    {
        using var dbe = SetupEngine();

        // Spawn and write to make SV dirty
        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(1);
        var entityId = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);
        entity.Write(SvTestArchetype.SvComp).Value = 42;

        var table = dbe.GetComponentTable<CompSmSingleVersion>();
        Assert.That(table.DirtyBitmap.HasDirty, Is.True, "Should be dirty after write");

        // WriteTickFence with no WAL still snapshots the bitmap (internal logic skips WAL claim)
        dbe.WriteTickFence(1);

        // Bitmap should be cleared by Snapshot() inside WriteTickFence
        Assert.That(table.DirtyBitmap.HasDirty, Is.False, "DirtyBitmap should be cleared after WriteTickFence");
    }

    [Test]
    public void WriteTickFence_VersionedAndTransient_Skipped()
    {
        using var dbe = SetupEngine();

        var vTable = dbe.GetComponentTable<CompSmVersioned>();
        var tTable = dbe.GetComponentTable<CompSmTransient>();

        Assert.That(vTable.StorageMode, Is.EqualTo(StorageMode.Versioned));
        Assert.That(tTable.StorageMode, Is.EqualTo(StorageMode.Transient));
        Assert.That(vTable.DirtyBitmap, Is.Null, "Versioned has no DirtyBitmap");
        Assert.That(tTable.DirtyBitmap, Is.Null, "Transient has no DirtyBitmap");

        // WriteTickFence should skip these tables without error
        var lsn = dbe.WriteTickFence(1);
        Assert.That(lsn, Is.EqualTo(0));
    }

    [Test]
    public void TickFenceHeader_SizeIs24Bytes()
    {
        Assert.That(TickFenceHeader.SizeInBytes, Is.EqualTo(24));
        unsafe
        {
            Assert.That(sizeof(TickFenceHeader), Is.EqualTo(24));
        }
    }

    [Test]
    public void WalChunkType_TickFenceIs3()
    {
        Assert.That((ushort)WalChunkType.TickFence, Is.EqualTo(3));
    }

    [Test]
    public void LastTickFenceLSN_InitiallyZero()
    {
        using var dbe = SetupEngine();
        Assert.That(dbe.LastTickFenceLSN, Is.EqualTo(0));
    }
}
