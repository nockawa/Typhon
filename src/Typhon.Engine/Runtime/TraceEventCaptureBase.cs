using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Typhon.Profiler;
using ProfilerTraceEventType = Typhon.Profiler.TraceEventType;
using ProfilerTickPhase = Typhon.Profiler.TickPhase;

namespace Typhon.Engine;

/// <summary>
/// Base class for <see cref="IRuntimeInspector"/> implementations that capture trace events via per-thread SPSC ring buffers.
/// </summary>
/// <remarks>
/// <para>
/// Architecture (inspired by Tracy Profiler and UE5 Insights):
/// <list type="bullet">
///   <item>Each worker thread writes to its own <see cref="ThreadTraceBuffer"/> via [ThreadStatic] — zero contention</item>
///   <item>The timer thread (single-writer for tick/phase events) uses buffer index 0</item>
///   <item>At tick end, the consumer drains all per-thread buffers, sorts them by timestamp, and hands them to <see cref="FlushBlock"/></item>
///   <item>Subclasses override <see cref="InitializeOutput"/>, <see cref="FlushBlock"/>, and <see cref="CloseOutput"/> to send events to a file, TCP socket, etc.</item>
/// </list>
/// </para>
/// <para>
/// Thread safety: <see cref="OnChunkStart"/>/<see cref="OnChunkEnd"/> are called from worker threads. All other methods are called from the timer thread.
/// The per-thread buffers ensure no contention on the hot path.
/// </para>
/// </remarks>
public abstract class TraceEventCaptureBase : IRuntimeInspector, IDisposable
{
    /// <summary>Per-thread trace buffer capacity (events). Power of 2 for fast masking.</summary>
    protected const int BufferCapacity = 2048;

    /// <summary>Max events to merge per tick before writing blocks.</summary>
    protected const int MergeBufferCapacity = 8192;

    /// <summary>Max events per output block. Matches <c>TraceFileWriter.MaxEventsPerBlock</c>. A tick with more events is split across multiple blocks.</summary>
    protected const int MaxEventsPerBlock = 4096;

    // Per-thread buffers
    protected ThreadTraceBuffer[] _buffers;
    protected readonly TraceEvent[] _mergeBuffer;
    protected int _workerCount;
    protected int _currentTickNumber;

    // Span name interning — accessed from any worker thread via the lock
    protected readonly Dictionary<string, int> _spanNameToId = new();
    protected readonly Dictionary<int, string> _spanIdToName = new();
    protected int _nextSpanNameId;

    // OTel capture
    private ActivityListener _activityListener;

    protected bool _disposed;

    [ThreadStatic]
    private static int t_bufferIndex;

    [ThreadStatic]
    private static bool t_bufferIndexInitialized;

    /// <summary>Cached comparer so <c>Array.Sort</c> doesn't allocate a fresh comparer per flush.</summary>
    protected static readonly IComparer<TraceEvent> TimestampComparer =
        Comparer<TraceEvent>.Create(static (a, b) => a.TimestampTicks.CompareTo(b.TimestampTicks));

    protected TraceEventCaptureBase()
    {
        _mergeBuffer = new TraceEvent[MergeBufferCapacity];
    }

    // ═══════════════════════════════════════════════════════════════
    // Subclass hooks
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize the subclass-specific output (open file, start TCP listener, etc.). Called after buffers are allocated but before the ActivityListener is attached.
    /// </summary>
    protected abstract void InitializeOutput(SystemDefinition[] systems, int workerCount, float baseTickRate);

    /// <summary>
    /// Write a single block of up to <see cref="MaxEventsPerBlock"/> events. Events are already sorted by timestamp in ascending order.
    /// Return <c>false</c> to abort the current flush (e.g., the socket died). No further blocks will be attempted this tick.
    /// </summary>
    protected abstract bool FlushBlock(ReadOnlySpan<TraceEvent> events);

    /// <summary>
    /// Optional pre-flush hook called once per tick before any <see cref="FlushBlock"/> invocations. Useful for sending ancillary data like
    /// incremental span names that should precede the event frames. Return <c>false</c> to abort the flush entirely.
    /// </summary>
    protected virtual bool OnBeforeFlush() => true;

    /// <summary>
    /// Close the subclass-specific output. Called from <see cref="OnSchedulerStopping"/> after the final flush and before the ActivityListener is disposed.
    /// </summary>
    protected abstract void CloseOutput();

    // ═══════════════════════════════════════════════════════════════
    // IRuntimeInspector — lifecycle
    // ═══════════════════════════════════════════════════════════════

    public virtual void OnSchedulerStarted(SystemDefinition[] systems, int workerCount, float baseTickRate)
    {
        _workerCount = workerCount;

        // Buffer 0 = timer thread, buffers 1..workerCount = worker threads
        _buffers = new ThreadTraceBuffer[workerCount + 1];
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i] = new ThreadTraceBuffer(BufferCapacity);
        }

        // Timer thread claims buffer 0
        t_bufferIndex = 0;
        t_bufferIndexInitialized = true;

        // Let the subclass prepare its output before we start capturing
        InitializeOutput(systems, workerCount, baseTickRate);

        // Set up ActivityListener to capture OTel spans from Typhon.Engine
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Typhon.Engine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = OnActivityStarted,
            ActivityStopped = OnActivityStopped
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public virtual void OnSchedulerStopping()
    {
        FlushBuffers();

        _activityListener?.Dispose();
        _activityListener = null;

        CloseOutput();
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activityListener?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // IRuntimeInspector — event callbacks (identical across subclasses)
    // ═══════════════════════════════════════════════════════════════

    public void OnTickStart(long tickNumber, long timestampTicks)
    {
        _currentTickNumber = (int)tickNumber;
        Emit(new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = (int)tickNumber,
            EventType = ProfilerTraceEventType.TickStart
        });
    }

    public void OnPhaseStart(TickPhase phase, long timestampTicks)
    {
        Emit(new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = _currentTickNumber,
            EventType = ProfilerTraceEventType.PhaseStart,
            Phase = (ProfilerTickPhase)(byte)phase
        });
    }

    public void OnPhaseEnd(TickPhase phase, long timestampTicks)
    {
        Emit(new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = _currentTickNumber,
            EventType = ProfilerTraceEventType.PhaseEnd,
            Phase = (ProfilerTickPhase)(byte)phase
        });
    }

    public void OnSystemReady(int systemIndex, long timestampTicks)
    {
        Emit(new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = _currentTickNumber,
            SystemIndex = (ushort)systemIndex,
            EventType = ProfilerTraceEventType.SystemReady
        });
    }

    public void OnChunkStart(int systemIndex, int chunkIndex, int workerId, long timestampTicks, int totalChunks)
    {
        EnsureWorkerBuffer(workerId);
        EmitToBuffer(workerId + 1, new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = _currentTickNumber,
            SystemIndex = (ushort)systemIndex,
            ChunkIndex = (ushort)chunkIndex,
            WorkerId = (byte)workerId,
            EventType = ProfilerTraceEventType.ChunkStart,
            Payload = totalChunks
        });
    }

    public void OnChunkEnd(int systemIndex, int chunkIndex, int workerId, long timestampTicks, int entitiesProcessed)
    {
        EmitToBuffer(workerId + 1, new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = _currentTickNumber,
            SystemIndex = (ushort)systemIndex,
            ChunkIndex = (ushort)chunkIndex,
            WorkerId = (byte)workerId,
            EventType = ProfilerTraceEventType.ChunkEnd,
            EntitiesProcessed = entitiesProcessed
        });
    }

    public void OnSystemSkipped(int systemIndex, SkipReason reason, long timestampTicks)
    {
        Emit(new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = _currentTickNumber,
            SystemIndex = (ushort)systemIndex,
            EventType = ProfilerTraceEventType.SystemSkipped,
            SkipReason = (byte)reason
        });
    }

    public void OnTickEnd(long tickNumber, long timestampTicks)
    {
        Emit(new TraceEvent
        {
            TimestampTicks = timestampTicks,
            TickNumber = (int)tickNumber,
            EventType = ProfilerTraceEventType.TickEnd
        });

        FlushBuffers();
    }

    // ═══════════════════════════════════════════════════════════════
    // Emit helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Emit an event to the current thread's buffer (timer thread uses buffer 0).</summary>
    protected void Emit(in TraceEvent evt)
    {
        if (!t_bufferIndexInitialized)
        {
            t_bufferIndex = 0;
            t_bufferIndexInitialized = true;
        }

        _buffers[t_bufferIndex].Write(in evt);
    }

    /// <summary>Emit an event to a specific buffer index (for worker threads).</summary>
    protected void EmitToBuffer(int bufferIndex, in TraceEvent evt) => _buffers[bufferIndex].Write(in evt);

    /// <summary>Set up a worker thread's buffer index on first emit.</summary>
    protected void EnsureWorkerBuffer(int workerId)
    {
        if (!t_bufferIndexInitialized)
        {
            t_bufferIndex = workerId + 1;
            t_bufferIndexInitialized = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Flush orchestration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Drains all per-thread buffers into the merge buffer, sorts them by timestamp, and invokes <see cref="FlushBlock"/> in chunks of <see cref="MaxEventsPerBlock"/>.
    /// Called from the timer thread at tick end — single-threaded, no contention with workers (workers are idle between ticks).
    /// </summary>
    protected void FlushBuffers()
    {
        var totalEvents = 0;

        for (var i = 0; i < _buffers.Length; i++)
        {
            var buffer = _buffers[i];
            while (buffer.TryRead(out var evt) && totalEvents < MergeBufferCapacity)
            {
                _mergeBuffer[totalEvents++] = evt;
            }
        }

        if (totalEvents == 0)
        {
            return;
        }

        // Sort by timestamp for correct delta encoding (uses cached comparer — no allocation)
        Array.Sort(_mergeBuffer, 0, totalEvents, TimestampComparer);

        if (!OnBeforeFlush())
        {
            return;
        }

        // Emit events in chunks of MaxEventsPerBlock so large ticks don't get silently truncated
        var sent = 0;
        while (sent < totalEvents)
        {
            var count = Math.Min(totalEvents - sent, MaxEventsPerBlock);
            if (!FlushBlock(new ReadOnlySpan<TraceEvent>(_mergeBuffer, sent, count)))
            {
                return;
            }
            sent += count;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Span name interning (shared between OTel capture and subclass access)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get or create an interned span name ID. Thread-safe — called from any worker thread via the ActivityListener.</summary>
    protected int InternSpanName(string name)
    {
        lock (_spanNameToId)
        {
            if (_spanNameToId.TryGetValue(name, out var id))
            {
                return id;
            }

            id = _nextSpanNameId++;
            _spanNameToId[name] = id;
            _spanIdToName[id] = name;
            return id;
        }
    }

    private void OnActivityStarted(Activity activity)
    {
        var nameId = InternSpanName(activity.OperationName);
        Emit(new TraceEvent
        {
            TimestampTicks = Stopwatch.GetTimestamp(),
            TickNumber = _currentTickNumber,
            EventType = ProfilerTraceEventType.SpanStart,
            Payload = nameId,
            WorkerId = (byte)(t_bufferIndexInitialized ? t_bufferIndex : 0)
        });
    }

    private void OnActivityStopped(Activity activity)
    {
        var nameId = InternSpanName(activity.OperationName);
        Emit(new TraceEvent
        {
            TimestampTicks = Stopwatch.GetTimestamp(),
            TickNumber = _currentTickNumber,
            EventType = ProfilerTraceEventType.SpanEnd,
            Payload = nameId,
            WorkerId = (byte)(t_bufferIndexInitialized ? t_bufferIndex : 0)
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Per-thread SPSC ring buffer
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-thread SPSC (single-producer, single-consumer) ring buffer for trace events.
    /// The producer is the owning thread; the consumer is the timer thread at tick end.
    /// </summary>
    /// <remarks>
    /// Volatile on <c>_head</c>/<c>_tail</c> provides release/acquire ordering, not atomicity — without it, the JIT or CPU could reorder the
    /// event-slot write past the head publish, letting the consumer see an advanced head with garbage in the slot. This is why we keep
    /// <c>Volatile.Read</c>/<c>Volatile.Write</c> here despite the project rule against them on <c>≤64</c>-bit fields (that rule is about atomicity, not ordering).
    /// </remarks>
    protected sealed class ThreadTraceBuffer
    {
        private readonly TraceEvent[] _events;
        private readonly int _mask;
        private int _head; // written by producer (owning thread)
        private int _tail; // written by consumer (timer thread)

        public ThreadTraceBuffer(int capacity)
        {
            if ((capacity & (capacity - 1)) != 0)
            {
                throw new ArgumentException("Capacity must be a power of 2", nameof(capacity));
            }

            _events = new TraceEvent[capacity];
            _mask = capacity - 1;
        }

        /// <summary>Write an event. Called from the owning thread only.</summary>
        public void Write(in TraceEvent evt)
        {
            var head = _head;
            var next = (head + 1) & _mask;

            // If full, drop the event (better than blocking the hot path)
            if (next == Volatile.Read(ref _tail))
            {
                return;
            }

            _events[head] = evt;
            Volatile.Write(ref _head, next);
        }

        /// <summary>Read an event. Called from the consumer thread only.</summary>
        public bool TryRead(out TraceEvent evt)
        {
            var tail = _tail;

            if (tail == Volatile.Read(ref _head))
            {
                evt = default;
                return false;
            }

            evt = _events[tail];
            Volatile.Write(ref _tail, (tail + 1) & _mask);
            return true;
        }
    }
}
