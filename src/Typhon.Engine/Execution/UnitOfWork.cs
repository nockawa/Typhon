using JetBrains.Annotations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

/// <summary>
/// The durability boundary for user operations. Batches multiple transactions for efficient persistence
/// while maintaining atomicity guarantees on crash recovery. Each UoW is assigned a UoW ID that stamps
/// all revisions created within its scope.
/// </summary>
/// <remarks>
/// <para>
/// This is the middle tier of the three-tier API hierarchy: <c>DatabaseEngine → UnitOfWork → Transaction</c>.
/// Create via <see cref="DatabaseEngine.CreateUnitOfWork"/>.
/// </para>
/// <para>
/// In WAL mode, durability is achieved via WAL FUA writes. In WAL-less mode (no <see cref="WalManager"/>),
/// durability uses ChangeSet-based page tracking: <see cref="ChangeSet.SaveChanges"/> writes dirty pages
/// to OS cache, and <see cref="PagedMMF.FlushToDisk"/> issues fsync. See §2.3 of 02-execution.md.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class UnitOfWork : IDisposable
{
    private readonly DatabaseEngine _dbe;
    private readonly DurabilityMode _durabilityMode;
    private readonly ushort _uowId;
    private UnitOfWorkState _state;
    private int _transactionCount;
    private int _committedTransactionCount;
    private bool _disposed;

    private readonly CancellationTokenSource _cts;
    private readonly Deadline _deadline;

    /// <summary>Controls when WAL records become crash-safe for this UoW.</summary>
    public DurabilityMode DurabilityMode => _durabilityMode;

    /// <summary>Current lifecycle state of this UoW.</summary>
    public UnitOfWorkState State => _state;

    /// <summary>UoW identifier for revision stamping and crash recovery. Allocated from UoW Registry.</summary>
    public ushort UowId => _uowId;

    /// <summary>Number of transactions created within this UoW.</summary>
    public int TransactionCount => _transactionCount;

    /// <summary>Number of transactions that have been committed within this UoW.</summary>
    public int CommittedTransactionCount => _committedTransactionCount;

    /// <summary>Whether this UoW has been disposed.</summary>
    public bool IsDisposed => _disposed;

    internal UnitOfWork(DatabaseEngine dbe, DurabilityMode durabilityMode, ushort uowId, TimeSpan timeout)
    {
        _dbe = dbe;
        _durabilityMode = durabilityMode;
        _uowId = uowId;
        _state = UnitOfWorkState.Pending;

        _cts = new CancellationTokenSource();
        _deadline = timeout == TimeSpan.Zero
            ? Deadline.Infinite
            : Deadline.FromTimeout(timeout);
    }

    /// <summary>
    /// Creates a new transaction within this UoW. The transaction inherits the UoW's identity
    /// and deadline for revision stamping and cancellation propagation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The UoW has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The UoW is not in <see cref="UnitOfWorkState.Pending"/> state.</exception>
    [return: TransfersOwnership]
    public Transaction CreateTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != UnitOfWorkState.Pending)
        {
            throw new InvalidOperationException($"Cannot create transaction: UoW is {_state}");
        }

        Interlocked.Increment(ref _transactionCount);
        _dbe.RecordTransactionCreated();
        return _dbe.TransactionChain.CreateTransaction(_dbe, this);
    }

    /// <summary>
    /// Creates a <see cref="UnitOfWorkContext"/> for use with <see cref="Transaction.Commit(ref UnitOfWorkContext, Transaction.ConcurrencyConflictHandler)"/>.
    /// Composes the UoW's deadline with the provided timeout (tighter deadline wins).
    /// </summary>
    public UnitOfWorkContext CreateContext(TimeSpan timeout = default)
    {
        var effectiveDeadline = timeout == default
            ? _deadline
            : Deadline.Min(_deadline, Deadline.FromTimeout(timeout));

        return new UnitOfWorkContext(effectiveDeadline, _cts.Token, _uowId);
    }

    /// <summary>
    /// Records that a transaction within this UoW has committed.
    /// </summary>
    internal void RecordTransactionCommitted() => Interlocked.Increment(ref _committedTransactionCount);

    /// <summary>
    /// Force WAL flush. When a <see cref="WalManager"/> is available, signals the WAL writer to flush
    /// buffered data and waits for durability. Otherwise, transitions state directly.
    /// </summary>
    public void Flush()
    {
        if (_state != UnitOfWorkState.Pending)
        {
            return;
        }

        var walManager = _dbe.WalManager;
        if (walManager != null)
        {
            // Signal the WAL writer to flush and wait for durable LSN
            walManager.RequestFlush();
            var currentLsn = walManager.CommitBuffer.NextLsn - 1;
            if (currentLsn > 0)
            {
                var ctx = _deadline == Deadline.Infinite ? WaitContext.Null : WaitContext.FromDeadline(_deadline);
                walManager.WaitForDurable(currentLsn, ref ctx);
            }
        }
        else
        {
            // WAL-less mode: fsync the data file to ensure all SaveChanges writes are on stable storage.
            _dbe.MMF.FlushToDisk();
        }

        _state = UnitOfWorkState.WalDurable;
    }

    /// <summary>
    /// Async version of <see cref="Flush"/>. Currently synchronous — true async will be added
    /// when the checkpoint pipeline is implemented.
    /// </summary>
    public Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Ensure durability: flush WAL (WAL mode) or fsync data file (WAL-less mode)
        Flush();

        // Cancel any outstanding operations
        _cts.Cancel();
        _cts.Dispose();

        if (_uowId != 0)
        {
            _dbe.UowRegistry.Release(_uowId);
        }

        _state = UnitOfWorkState.Free;
    }
}
