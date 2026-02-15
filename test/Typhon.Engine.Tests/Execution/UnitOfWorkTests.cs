using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

class UnitOfWorkTests : TestBase<UnitOfWorkTests>
{
    [Test]
    public void UoW_Create_Dispose_Lifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Pending));
        Assert.That(uow.IsDisposed, Is.False);

        uow.Dispose();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Free));
        Assert.That(uow.IsDisposed, Is.True);
    }

    [Test]
    public void UoW_CreateTransaction_ReturnsValidTx()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var uow = dbe.CreateUnitOfWork();
        using var tx = uow.CreateTransaction();

        Assert.That(tx, Is.Not.Null);
        Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Created));
        Assert.That(tx.TSN, Is.GreaterThan(0));

        // Verify the transaction actually works: create, commit, read
        var comp = new CompA(42);
        var pk = tx.CreateEntity(ref comp);
        Assert.That(pk, Is.GreaterThan(0));

        var committed = tx.Commit();
        Assert.That(committed, Is.True);
    }

    [Test]
    public void UoW_MultipleTransactions_ShareIdentity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var uow = dbe.CreateUnitOfWork();

        using var tx1 = uow.CreateTransaction();
        using var tx2 = uow.CreateTransaction();

        // Both transactions should reference the same UoW
        Assert.That(tx1.OwningUnitOfWork, Is.SameAs(uow));
        Assert.That(tx2.OwningUnitOfWork, Is.SameAs(uow));
        Assert.That(uow.TransactionCount, Is.EqualTo(2));

        tx1.Commit();
        tx2.Commit();
    }

    [Test]
    public void UoW_DisposedUoW_ThrowsOnCreateTx()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        uow.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
#pragma warning disable TYPHON004 // CreateTransaction will throw before returning
            uow.CreateTransaction();
#pragma warning restore TYPHON004
        });
    }

    [Test]
    public void UoW_DurabilityMode_PreservedFromCreation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var deferred = dbe.CreateUnitOfWork(DurabilityMode.Deferred);
        Assert.That(deferred.DurabilityMode, Is.EqualTo(DurabilityMode.Deferred));

        using var groupCommit = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
        Assert.That(groupCommit.DurabilityMode, Is.EqualTo(DurabilityMode.GroupCommit));

        using var immediate = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
        Assert.That(immediate.DurabilityMode, Is.EqualTo(DurabilityMode.Immediate));
    }

    [Test]
    public void UoW_StateTransitions()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Pending));

        // Flush transitions Pending → WalDurable (no-op semantics until WAL)
        uow.Flush();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.WalDurable));

        // Second flush is a no-op (already WalDurable)
        uow.Flush();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.WalDurable));

        // Dispose transitions → Free
        uow.Dispose();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Free));
    }

    [Test]
    public void UoW_Flush_NoOp_WithoutWAL()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow = dbe.CreateUnitOfWork();

        // Flush and FlushAsync should succeed without WAL infrastructure
        Assert.DoesNotThrow(() => uow.Flush());

        // FlushAsync should return completed task
        var task = uow.FlushAsync();
        Assert.That(task.IsCompleted, Is.True);
    }

    [Test]
    public void UoW_DoubleDispose_NoThrow()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        uow.Dispose();

        // Second dispose should not throw
        Assert.DoesNotThrow(() => uow.Dispose());
        Assert.That(uow.IsDisposed, Is.True);
    }

    [Test]
    public void UoW_TransactionCount_Increments()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.TransactionCount, Is.EqualTo(0));

        using var tx1 = uow.CreateTransaction();
        Assert.That(uow.TransactionCount, Is.EqualTo(1));

        using var tx2 = uow.CreateTransaction();
        Assert.That(uow.TransactionCount, Is.EqualTo(2));

        tx1.Commit();
        tx2.Commit();
    }

    [Test]
    public void UoW_DefaultDurabilityMode_IsDeferred()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.DurabilityMode, Is.EqualTo(DurabilityMode.Deferred));
    }

    [Test]
    public void UoW_UowId_IsAllocated_FromRegistry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.UowId, Is.GreaterThan((ushort)0), "UoW ID should be allocated from registry (1+)");
    }

    // ═══════════════════════════════════════════════════════════════
    // Transaction Binding Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Tx_OwningUnitOfWork_SetViaInit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var uow = dbe.CreateUnitOfWork();
        using var tx = uow.CreateTransaction();

        Assert.That(tx.OwningUnitOfWork, Is.SameAs(uow));
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateQuickTransaction Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void QuickTx_CommitThenDispose_CleanLifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        UnitOfWork capturedUow;
        long pk;

        // Create, use, commit, dispose — all in one block
        {
            var tx = dbe.CreateQuickTransaction();
            capturedUow = tx.OwningUnitOfWork;
            Assert.That(capturedUow, Is.Not.Null);
            Assert.That(capturedUow.State, Is.EqualTo(UnitOfWorkState.Pending));
            Assert.That(tx.OwnsUnitOfWork, Is.True);

            var comp = new CompA(99);
            pk = tx.CreateEntity(ref comp);
            tx.Commit();
            tx.Dispose();
        }

        // After dispose, the UoW should also be disposed
        Assert.That(capturedUow.IsDisposed, Is.True);
        Assert.That(capturedUow.State, Is.EqualTo(UnitOfWorkState.Free));

        // Verify the data is actually committed and readable
        using var uow2 = dbe.CreateUnitOfWork();
        using var readTx = uow2.CreateTransaction();
        var found = readTx.ReadEntity(pk, out CompA readComp);
        Assert.That(found, Is.True);
        Assert.That(readComp.A, Is.EqualTo(99));
    }

    [Test]
    public void QuickTx_DisposesUoW()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var tx = dbe.CreateQuickTransaction();
        var uow = tx.OwningUnitOfWork;

        Assert.That(uow.IsDisposed, Is.False);
        tx.Dispose();
        Assert.That(uow.IsDisposed, Is.True, "QuickTransaction dispose should also dispose the backing UoW");
    }

    [Test]
    public void QuickTx_DurabilityMode_Passthrough()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        Assert.That(tx.OwningUnitOfWork.DurabilityMode, Is.EqualTo(DurabilityMode.Immediate));
    }

    // ═══════════════════════════════════════════════════════════════
    // Integration: Full CRUD Through UoW
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CreateEntity_ThroughUoW_Succeeds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var uow = dbe.CreateUnitOfWork();
            using var tx = uow.CreateTransaction();

            var comp = new CompA(123, 4.56f, 7.89);
            pk = tx.CreateEntity(ref comp);
            Assert.That(pk, Is.GreaterThan(0));
            tx.Commit();
        }

        // Read back through a new UoW
        {
            using var uow = dbe.CreateUnitOfWork();
            using var tx = uow.CreateTransaction();

            var found = tx.ReadEntity(pk, out CompA read);
            Assert.That(found, Is.True);
            Assert.That(read.A, Is.EqualTo(123));
            Assert.That(read.B, Is.EqualTo(4.56f));
            Assert.That(read.C, Is.EqualTo(7.89));
        }
    }

    [Test]
    public void MultipleUoWs_ConcurrentAccess()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create data in UoW 1
        long pk;
        {
            using var uow1 = dbe.CreateUnitOfWork();
            using var tx1 = uow1.CreateTransaction();
            var comp = new CompA(100);
            pk = tx1.CreateEntity(ref comp);
            tx1.Commit();
        }

        // Read from UoW 2 and UoW 3 simultaneously
        using var uow2 = dbe.CreateUnitOfWork();
        using var uow3 = dbe.CreateUnitOfWork();

        using var tx2 = uow2.CreateTransaction();
        using var tx3 = uow3.CreateTransaction();

        var found2 = tx2.ReadEntity(pk, out CompA read2);
        var found3 = tx3.ReadEntity(pk, out CompA read3);

        Assert.That(found2, Is.True);
        Assert.That(found3, Is.True);
        Assert.That(read2.A, Is.EqualTo(100));
        Assert.That(read3.A, Is.EqualTo(100));

        tx2.Commit();
        tx3.Commit();
    }
}
