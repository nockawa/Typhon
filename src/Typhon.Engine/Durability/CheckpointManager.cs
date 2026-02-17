using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Background thread that periodically writes dirty data pages to disk and advances <see cref="CheckpointLsn"/>, enabling WAL segment reclamation and
/// bounding crash-recovery replay time.
/// </summary>
/// <remarks>
/// <para>
/// The checkpoint pipeline per cycle:
/// <list type="number">
///   <item>Capture <c>DurableLsn</c> from <see cref="WalManager"/> atomically</item>
///   <item>Collect dirty memory page indices from the page cache</item>
///   <item>Write dirty pages to the data file (without decrementing DirtyCounter)</item>
///   <item>Fsync the data file</item>
///   <item>Decrement DirtyCounter for each written page (re-dirtied pages stay &gt; 0)</item>
///   <item>Transition UoW entries from WalDurable → Committed</item>
///   <item>Advance CheckpointLSN in the file header + fsync</item>
///   <item>Recycle WAL segments below CheckpointLSN</item>
/// </list>
/// </para>
/// <para>
/// Key invariant: <c>CheckpointLSN ≤ DurableLSN ≤ CurrentLSN</c>
/// </para>
/// <para>
/// Thread model follows <see cref="WalWriter"/>: dedicated <see cref="Thread"/>, <c>IsBackground=true</c>, <c>ThreadPriority.Normal</c>, named "Typhon-Checkpoint".
/// </para>
/// </remarks>
[PublicAPI]
internal sealed class CheckpointManager : ResourceNode, IMetricSource
{
    // ═══════════════════════════════════════════════════════════════
    // Dependencies
    // ═══════════════════════════════════════════════════════════════

    private readonly ManagedPagedMMF _mmf;
    private readonly UowRegistry _uowRegistry;
    private readonly WalManager _walManager;
    private readonly ResourceOptions _resourceOptions;
    private readonly EpochManager _epochManager;

    // ═══════════════════════════════════════════════════════════════
    // Thread lifecycle
    // ═══════════════════════════════════════════════════════════════

    private Thread _thread;
    private volatile bool _shutdown;
    private readonly Lock _lifecycleLock = new();
    private readonly ManualResetEventSlim _wakeEvent = new(false);

    // ═══════════════════════════════════════════════════════════════
    // State
    // ═══════════════════════════════════════════════════════════════

    private long _checkpointLsn;
    private volatile Exception _fatalError;
    private volatile bool _forceRequested;

    // ═══════════════════════════════════════════════════════════════
    // Metrics
    // ═══════════════════════════════════════════════════════════════

    private long _totalCheckpoints;
    private long _totalPagesWritten;
    private long _totalSegmentsRecycled;
    private long _totalUowTransitioned;
    private long _lastDurationUs;
    private long _maxDurationUs;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new Checkpoint Manager. Call <see cref="Start"/> to begin the background thread.
    /// </summary>
    /// <param name="mmf">The managed paged memory-mapped file (data file storage).</param>
    /// <param name="uowRegistry">Unit of Work registry for state transitions.</param>
    /// <param name="walManager">WAL manager for reading DurableLsn and segment reclamation.</param>
    /// <param name="resourceOptions">Configuration (CheckpointIntervalMs, CheckpointMaxDirtyPages).</param>
    /// <param name="epochManager">Epoch manager for page access.</param>
    /// <param name="parent">Parent resource node.</param>
    /// <param name="initialCheckpointLsn">Initial checkpoint LSN from file header (0 for fresh database).</param>
    internal CheckpointManager(ManagedPagedMMF mmf, UowRegistry uowRegistry, WalManager walManager, ResourceOptions resourceOptions, EpochManager epochManager, 
        IResource parent, long initialCheckpointLsn = 0) : base("CheckpointManager", ResourceType.WAL, parent)
    {
        ArgumentNullException.ThrowIfNull(mmf);
        ArgumentNullException.ThrowIfNull(uowRegistry);
        ArgumentNullException.ThrowIfNull(walManager);
        ArgumentNullException.ThrowIfNull(resourceOptions);
        ArgumentNullException.ThrowIfNull(epochManager);

        _mmf = mmf;
        _uowRegistry = uowRegistry;
        _walManager = walManager;
        _resourceOptions = resourceOptions;
        _epochManager = epochManager;
        _checkpointLsn = initialCheckpointLsn;
    }

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The highest LSN that has been fully checkpointed (data pages on stable media).</summary>
    public long CheckpointLsn => Interlocked.Read(ref _checkpointLsn);

    /// <summary>Whether the checkpoint thread is currently running.</summary>
    public bool IsRunning => _thread != null && _thread.IsAlive;

    /// <summary>Whether a fatal I/O error has occurred during checkpointing.</summary>
    public bool HasFatalError => _fatalError != null;

    /// <summary>Total number of checkpoint cycles completed.</summary>
    public long TotalCheckpoints => Interlocked.Read(ref _totalCheckpoints);

    /// <summary>Total number of dirty pages written across all checkpoints.</summary>
    public long TotalPagesWritten => Interlocked.Read(ref _totalPagesWritten);

    /// <summary>Total number of WAL segments reclaimed across all checkpoints.</summary>
    public long TotalSegmentsRecycled => Interlocked.Read(ref _totalSegmentsRecycled);

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the checkpoint background thread. Idempotent — does nothing if already running.
    /// </summary>
    public void Start()
    {
        if (_thread != null && _thread.IsAlive)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _shutdown = false;
            _thread = new Thread(CheckpointLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = "Typhon-Checkpoint"
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Requests an immediate checkpoint cycle. Wakes the background thread if sleeping.
    /// </summary>
    public void ForceCheckpoint()
    {
        _forceRequested = true;
        _wakeEvent.Set();
    }

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("Checkpoints", _totalCheckpoints);
        writer.WriteThroughput("PagesWritten", _totalPagesWritten);
        writer.WriteThroughput("SegmentsRecycled", _totalSegmentsRecycled);
        writer.WriteThroughput("UowTransitioned", _totalUowTransitioned);
        writer.WriteDuration("CheckpointDuration", _lastDurationUs, 0, _maxDurationUs);
    }

    /// <inheritdoc />
    public void ResetPeaks() => _maxDurationUs = _lastDurationUs;

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint loop (runs on dedicated thread)
    // ═══════════════════════════════════════════════════════════════

    private void CheckpointLoop()
    {
        try
        {
            while (!_shutdown)
            {
                // Sleep until woken by: timer expiry, ForceCheckpoint, or shutdown
                _wakeEvent.Wait(_resourceOptions.CheckpointIntervalMs);
                _wakeEvent.Reset();

                if (_shutdown)
                {
                    break;
                }

                // Skip if a previous cycle encountered a fatal error
                if (_fatalError != null)
                {
                    continue;
                }

                var force = _forceRequested;
                _forceRequested = false;

                // Check if we should run a checkpoint cycle
                var durableLsn = _walManager.DurableLsn;
                if (durableLsn <= Interlocked.Read(ref _checkpointLsn) && !force)
                {
                    // No new durable WAL records since last checkpoint and no force request
                    continue;
                }

                RunCheckpointCycle(durableLsn);
            }

            // Shutdown: run one final checkpoint cycle to flush all dirty pages
            if (_fatalError == null)
            {
                var finalLsn = _walManager.DurableLsn;
                if (finalLsn > Interlocked.Read(ref _checkpointLsn))
                {
                    RunCheckpointCycle(finalLsn);
                }
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint pipeline
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes one full checkpoint cycle. Visible internally for testability.
    /// </summary>
    internal void RunCheckpointCycle(long targetLsn)
    {
        var sw = Stopwatch.GetTimestamp();

        try
        {
            // Step 1: Capture target LSN (already passed as parameter)
            // This is the DurableLsn read atomically before entering the cycle.

            // Step 2: Collect dirty pages
            var dirtyPages = _mmf.CollectDirtyMemPageIndices();

            // Step 3: Write dirty pages (without decrementing DirtyCounter)
            if (dirtyPages.Length > 0)
            {
                _mmf.WritePagesForCheckpoint(dirtyPages).GetAwaiter().GetResult();
            }

            if (_shutdown)
            {
                return; // Check between expensive operations
            }

            // Step 4: Fsync data file
            _mmf.FlushToDisk();

            // Step 5: Decrement DirtyCounter for written pages
            // Pages re-dirtied during write will have DirtyCounter > 1, so after decrement they stay > 0
            foreach (var memPageIndex in dirtyPages)
            {
                _mmf.DecrementDirty(memPageIndex);
            }

            Interlocked.Add(ref _totalPagesWritten, dirtyPages.Length);

            // Step 6: Transition WalDurable → Committed
            var transitioned = _uowRegistry.TransitionWalDurableToCommitted();
            Interlocked.Add(ref _totalUowTransitioned, transitioned);

            // Step 7: Advance CheckpointLSN in file header + fsync
            _mmf.UpdateCheckpointLSN(targetLsn, _epochManager);
            Interlocked.Exchange(ref _checkpointLsn, targetLsn);

            // Step 8: Recycle WAL segments
            var segmentManager = _walManager.SegmentManager;
            if (segmentManager != null)
            {
                var recycled = segmentManager.MarkReclaimable(targetLsn);
                Interlocked.Add(ref _totalSegmentsRecycled, recycled);
            }

            Interlocked.Increment(ref _totalCheckpoints);
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            return;
        }

        // Record duration
        var elapsed = Stopwatch.GetTimestamp() - sw;
        var us = (long)((double)elapsed / Stopwatch.Frequency * 1_000_000.0);
        _lastDurationUs = us;
        if (us > _maxDurationUs)
        {
            _maxDurationUs = us;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════

    private bool _disposed;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _shutdown = true;
            _wakeEvent.Set(); // Wake the thread so it sees _shutdown
            _thread?.Join(TimeSpan.FromSeconds(10));

            _wakeEvent.Dispose();
        }

        base.Dispose(disposing);
        _disposed = true;
    }
}
