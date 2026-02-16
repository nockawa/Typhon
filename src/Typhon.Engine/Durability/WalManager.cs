using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Top-level orchestrator for the Write-Ahead Log subsystem. Owns the <see cref="WalCommitBuffer"/>, <see cref="WalWriter"/>, and
/// <see cref="WalSegmentManager"/> as a single cohesive unit.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <see cref="Initialize"/> creates the WAL directory and opens the first segment, then <see cref="Start"/> launches the writer thread.
/// <see cref="Dispose"/> stops the writer and releases all resources.
/// </para>
/// <para>
/// The manager delegates producer-facing APIs (<see cref="DurableLsn"/>, <see cref="WaitForDurable"/>) to the underlying <see cref="WalWriter"/>.
/// The <see cref="CommitBuffer"/> is exposed for transaction threads to claim and publish WAL records.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class WalManager : ResourceNode
{
    private readonly WalWriterOptions _options;
    private readonly IMemoryAllocator _allocator;
    private readonly IWalFileIO _fileIO;

    private WalSegmentManager _segmentManager;
    private WalWriter _writer;

    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Creates a new WAL manager. Call <see cref="Initialize"/> then <see cref="Start"/> to activate.
    /// </summary>
    /// <param name="options">Writer and segment configuration.</param>
    /// <param name="allocator">Memory allocator for buffer and staging allocations.</param>
    /// <param name="fileIO">Platform I/O abstraction.</param>
    /// <param name="parent">Parent resource node (typically <c>registry.Durability</c>).</param>
    /// <param name="commitBufferCapacity">Capacity of each commit buffer half in bytes. Default: 2 MB.</param>
    public WalManager(WalWriterOptions options, IMemoryAllocator allocator, IWalFileIO fileIO, IResource parent, int commitBufferCapacity = 2 * 1024 * 1024)
        : base("WalManager", ResourceType.WAL, parent)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(fileIO);

        _options = options;
        _allocator = allocator;
        _fileIO = fileIO;

        CommitBuffer = new WalCommitBuffer(allocator, this, commitBufferCapacity);
    }

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The commit buffer for producer threads to claim and publish WAL records.</summary>
    public WalCommitBuffer CommitBuffer { get; private set; }

    /// <summary>The highest LSN durably written to stable media.</summary>
    public long DurableLsn => _writer?.DurableLsn ?? 0;

    /// <summary>Whether the WAL writer thread is running.</summary>
    public bool IsRunning => _writer?.IsRunning ?? false;

    /// <summary>Whether a fatal I/O error has occurred.</summary>
    public bool HasFatalError => _writer?.HasFatalError ?? false;

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the WAL subsystem: creates directories, opens the first segment, and prepares the writer. Must be called before <see cref="Start"/>.
    /// </summary>
    /// <param name="lastSegmentId">Last known segment ID for continuity (0 for fresh start).</param>
    /// <param name="firstLSN">First LSN for the initial segment.</param>
    public void Initialize(long lastSegmentId = 0, long firstLSN = 1)
    {
        if (_initialized)
        {
            ThrowHelper.ThrowInvalidOp("WalManager is already initialized.");
        }

        _segmentManager = new WalSegmentManager(_fileIO, _options.WalDirectory, _options.SegmentSize, _options.PreAllocateSegments, _options.UseFUA);
        _segmentManager.Initialize(lastSegmentId, firstLSN);
        _writer = new WalWriter(CommitBuffer, _segmentManager, _fileIO, _options, _allocator, this);
        _initialized = true;
    }

    /// <summary>
    /// Starts the WAL writer thread. <see cref="Initialize"/> must be called first.
    /// </summary>
    public void Start()
    {
        if (!_initialized)
        {
            ThrowHelper.ThrowInvalidOp("WalManager must be initialized before starting.");
        }

        _writer.Start();
    }

    /// <summary>
    /// Blocks the caller until the specified LSN has been durably written.
    /// Delegates to <see cref="WalWriter.WaitForDurable"/>.
    /// </summary>
    public void WaitForDurable(long lsn, ref WaitContext ctx) => _writer.WaitForDurable(lsn, ref ctx);

    /// <summary>
    /// Requests an explicit flush of buffered WAL data.
    /// Used by <see cref="DurabilityMode.Deferred"/> callers.
    /// </summary>
    public void RequestFlush() => _writer?.RequestFlush();

    // ═══════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _writer?.Dispose();
            _writer = null;

            _segmentManager?.Dispose();
            _segmentManager = null;

            CommitBuffer?.Dispose();
            CommitBuffer = null;
        }

        base.Dispose(disposing);
        _disposed = true;
    }
}
