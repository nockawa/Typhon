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
/// Currently only <see cref="DurabilityMode.Deferred"/> is functional (no WAL yet). The full durability
/// model activates when Tier 5 (WAL) lands.
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

    /// <summary>UoW identifier for revision stamping and crash recovery. 0 until UoW Registry (#51) lands.</summary>
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
    /// Force WAL flush. No-op until WAL is implemented in Tier 5.
    /// For <see cref="DurabilityMode.Deferred"/> mode, transitions state to <see cref="UnitOfWorkState.WalDurable"/>.
    /// </summary>
    public void Flush()
    {
        // Future: signal WAL writer, wait for FUA
        // For now: transition state directly
        if (_state == UnitOfWorkState.Pending)
        {
            _state = UnitOfWorkState.WalDurable;
        }
    }

    /// <summary>
    /// Async version of <see cref="Flush"/>. No-op until WAL is implemented in Tier 5.
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

        // Cancel any outstanding operations
        _cts.Cancel();
        _cts.Dispose();

        // Future: return UoW ID to registry (#51)

        _state = UnitOfWorkState.Free;
    }
}
