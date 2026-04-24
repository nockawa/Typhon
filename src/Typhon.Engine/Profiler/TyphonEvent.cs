using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Static producer-side API for the Tracy-style typed-event profiler. Engine call sites that want to record a span construct a typed ref-struct
/// event via one of the <c>Begin*Event</c> factories, fill its fields, and let <c>Dispose</c> publish the record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot path:</b> every <c>Begin*Event</c> factory is a thin wrapper that loads the current slot, captures a start timestamp, and returns a
/// populated ref struct. The returned struct carries everything the <c>Dispose</c>/<c>Publish</c> path needs — no hidden state, no TLS reads on
/// the fast-path tail.
/// </para>
/// <para>
/// <b>JIT elimination when disabled:</b> the first instruction of every factory is <c>if (!TelemetryConfig.ProfilerActive) return default;</c>.
/// <c>ProfilerActive</c> is <c>static readonly</c>, initialized from config in the class's static constructor. When the profiler is disabled,
/// the JIT folds the factory body to <c>return default;</c> and every <c>Dispose</c> becomes a no-op. Zero CPU cost at the call site.
/// </para>
/// <para>
/// <b>Per-thread state:</b> <see cref="CurrentOpenSpanId"/> tracks the innermost open Typhon span on this thread for LIFO parent linking;
/// <see cref="CurrentTickNumber"/> holds the scheduler tick number for tick attribution. <see cref="SuppressActivityCapture"/> is the
/// per-thread opt-out for skipping <see cref="Activity.Current"/> reads.
/// </para>
/// </remarks>
public static class TyphonEvent
{
    /// <summary>Innermost open Typhon span on this thread. Captured in the <c>Begin*</c> factories as the new span's <c>ParentSpanId</c>.</summary>
    [ThreadStatic] 
    private static ulong CurrentOpenSpanId;

    /// <summary>Per-thread opt-out flag for <see cref="Activity.Current"/> capture.</summary>
    [ThreadStatic]
    private static bool SuppressActivityCapture;

    /// <summary>Current scheduler tick number for this thread. Set by <c>DagScheduler</c>; read implicitly via the session's tick tracking.</summary>
    [ThreadStatic]
    internal static int CurrentTickNumber;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-kind suppression deny-list
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Indexed by (int)TraceEventKind (0..255). When an entry is true, the matching Begin*Event factory short-circuits and returns default(T),
    // the same way it does when the profiler is globally off. The check lives inside BeginPrologue, guarded by the ProfilerActive check — so
    // when the profiler is off, the JIT still dead-code-eliminates the entire prologue including this array access. When the profiler is on,
    // the cost is one predictable cache-hot load + branch per factory call (~1 ns).
    //
    // Defaults: the 5 PageCache.* kinds are suppressed. PageCacheFetch is the dangerous one — called on every ChunkAccessor.GetPage in hot
    // loops, easily millions/sec on a read-heavy workload. We suppress all five page-cache kinds together for consistency; users running a
    // cache-miss investigation opt specific ones back in via UnsuppressKind.
    //
    // Replaces the pre-#243 TelemetryConfig.PagedMMFSpanCacheMiss / PagedMMFSpanIOOnly flags, which were compile-time gates for the old
    // TyphonActivitySource.StartActivity call path. The typed-event profiler has a single coarse gate (ProfilerActive) plus this fine-grained
    // per-kind deny-list — more flexible, same zero-cost-when-off guarantee.

    private static readonly bool[] SuppressedKinds = new bool[256];

    static TyphonEvent()
    {
        SuppressedKinds[(int)TraceEventKind.PageCacheFetch] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheDiskRead] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheDiskWrite] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheAllocatePage] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheFlush] = true;
        SuppressedKinds[(int)TraceEventKind.PageEvicted] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheDiskReadCompleted] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheDiskWriteCompleted] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheFlushCompleted] = true;
        SuppressedKinds[(int)TraceEventKind.PageCacheBackpressure] = true;
    }

    /// <summary>
    /// Mark a <see cref="TraceEventKind"/> as suppressed. Subsequent <c>Begin*</c> factory calls for that kind return <c>default</c> and emit
    /// no record. Existing in-flight scopes are unaffected — their Dispose still runs the PublishEvent path because they already hold a
    /// non-zero <c>SpanId</c>.
    /// </summary>
    /// <remarks>
    /// Thread-safe for concurrent readers (the hot path); not guaranteed ordered with concurrent writers. Typically called at profiler
    /// startup or from an admin/diagnostics endpoint. Plain byte-store, no <c>Interlocked</c> needed.
    /// </remarks>
    public static void SuppressKind(TraceEventKind kind) => SuppressedKinds[(int)kind] = true;

    /// <summary>Clear the suppression flag for a specific event kind. Inverse of <see cref="SuppressKind"/>.</summary>
    public static void UnsuppressKind(TraceEventKind kind) => SuppressedKinds[(int)kind] = false;

    /// <summary>Whether a specific event kind is currently in the deny-list.</summary>
    public static bool IsKindSuppressed(TraceEventKind kind) => SuppressedKinds[(int)kind];

    // ═══════════════════════════════════════════════════════════════════════
    // Prologue — shared by every Begin*Event factory
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Common prologue: check gate, check per-kind suppression, acquire slot, capture start timestamp, compute SpanId, set TLS, optionally
    /// read <see cref="Activity.Current"/>. Returns <c>false</c> if the span should be skipped (profiler off, kind suppressed, registry full)
    /// — callers return <c>default</c> in that case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BeginPrologue(TraceEventKind kind, out int slotIdx, out long startTs, out ulong spanId, out ulong parentSpanId,
        out ulong previousSpanId, out ulong traceIdHi, out ulong traceIdLo)
    {
        slotIdx = -1;
        startTs = 0;
        spanId = 0;
        parentSpanId = 0;
        previousSpanId = 0;
        traceIdHi = 0;
        traceIdLo = 0;

        if (!TelemetryConfig.ProfilerActive)
        {
            return false;
        }

        // Per-kind suppression deny-list. Ordered AFTER the ProfilerActive check so the JIT still eliminates the entire prologue body (including
        // this array load) when the profiler is globally off.
        if (SuppressedKinds[(int)kind])
        {
            return false;
        }

        var idx = ThreadSlotRegistry.GetOrAssignSlot();
        if (idx < 0)
        {
            return false;
        }

        var slot = ThreadSlotRegistry.GetSlot(idx);
        startTs = Stopwatch.GetTimestamp();
        spanId = SpanIdGenerator.NextId(idx, slot);
        previousSpanId = CurrentOpenSpanId;
        parentSpanId = previousSpanId;

        if (slot.CaptureActivityContext && !SuppressActivityCapture)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                Span<byte> traceBuf = stackalloc byte[16];
                activity.TraceId.CopyTo(traceBuf);
                traceIdHi = MemoryMarshal.Read<ulong>(traceBuf);
                traceIdLo = MemoryMarshal.Read<ulong>(traceBuf[8..]);

                if (parentSpanId == 0)
                {
                    Span<byte> spanBuf = stackalloc byte[8];
                    activity.SpanId.CopyTo(spanBuf);
                    parentSpanId = MemoryMarshal.Read<ulong>(spanBuf);
                }
            }
        }

        CurrentOpenSpanId = spanId;
        slotIdx = idx;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Publishing — shared by every ref-struct event's Dispose
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Publish a typed event to its owner slot's ring buffer and restore the parent scope's TLS open-span ID. Called from every typed event
    /// struct's <c>Dispose</c> method via a generic constraint that lets the JIT inline the full encode path for each concrete event type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>allows ref struct</c> constraint</b> (C# 13) permits <typeparamref name="T"/> to be a <c>ref struct</c> — which every Phase 1
    /// typed event is. Without it, the generic constraint would reject ref-struct instantiations. With it, the JIT specializes this method for
    /// each event type, inlining <see cref="ITraceEventEncoder.ComputeSize"/> and <see cref="ITraceEventEncoder.EncodeTo"/> at the call site.
    /// </para>
    /// <para>
    /// <b>Default-struct detection:</b> when <paramref name="spanId"/> is zero the event was returned from a short-circuit path
    /// (profiler disabled, registry full, suppressed). In that case the Dispose is a no-op — nothing was reserved, nothing to publish, no TLS to
    /// restore.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PublishEvent<T>(ref T evt, byte threadSlot, ulong previousSpanId, ulong spanId) where T : struct, ITraceEventEncoder, allows ref struct
    {
        // `ref T` rather than `in T` is deliberate: ITraceEventEncoder's ComputeSize/EncodeTo aren't (and can't be) marked `readonly` on
        // the interface, so calling them through an `in` parameter would force a defensive struct copy at every call site. Taking `ref`
        // gives the JIT a mutable alias and inlines the calls with zero copies.
        if (spanId == 0)
        {
            return;  // default struct — Dispose of a skipped span
        }

        var endTs = Stopwatch.GetTimestamp();
        var size = evt.ComputeSize();
        var slot = ThreadSlotRegistry.GetSlot(threadSlot);
        var ring = slot.Buffer;
        if (ring != null && ring.TryReserve(size, out var dst))
        {
            evt.EncodeTo(dst, endTs, out _);
            ring.Publish();
        }
        CurrentOpenSpanId = previousSpanId;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Typed event factories — one per TraceEventKind
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.EcsQueryExecute"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsQueryExecuteEvent BeginEcsQueryExecute(ushort archetypeTypeId)
    {
        if (!BeginPrologue(TraceEventKind.EcsQueryExecute, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new EcsQueryExecuteEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            ArchetypeTypeId = archetypeTypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.EcsQueryCount"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsQueryCountEvent BeginEcsQueryCount(ushort archetypeTypeId)
    {
        if (!BeginPrologue(TraceEventKind.EcsQueryCount, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new EcsQueryCountEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            ArchetypeTypeId = archetypeTypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.EcsQueryAny"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsQueryAnyEvent BeginEcsQueryAny(ushort archetypeTypeId)
    {
        if (!BeginPrologue(TraceEventKind.EcsQueryAny, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new EcsQueryAnyEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            ArchetypeTypeId = archetypeTypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.EcsViewRefresh"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsViewRefreshEvent BeginEcsViewRefresh(ushort archetypeTypeId)
    {
        if (!BeginPrologue(TraceEventKind.EcsViewRefresh, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new EcsViewRefreshEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            ArchetypeTypeId = archetypeTypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.EcsSpawn"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsSpawnEvent BeginEcsSpawn(ushort archetypeId)
    {
        if (!BeginPrologue(TraceEventKind.EcsSpawn, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new EcsSpawnEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            ArchetypeId = archetypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.EcsDestroy"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsDestroyEvent BeginEcsDestroy(ulong entityId)
    {
        if (!BeginPrologue(TraceEventKind.EcsDestroy, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new EcsDestroyEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            EntityId = entityId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.TransactionCommit"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TransactionCommitEvent BeginTransactionCommit(long tsn)
    {
        if (!BeginPrologue(TraceEventKind.TransactionCommit, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new TransactionCommitEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            Tsn = tsn,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.TransactionRollback"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TransactionRollbackEvent BeginTransactionRollback(long tsn)
    {
        if (!BeginPrologue(TraceEventKind.TransactionRollback, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new TransactionRollbackEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            Tsn = tsn,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.TransactionCommitComponent"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TransactionCommitComponentEvent BeginTransactionCommitComponent(long tsn, int componentTypeId)
    {
        if (!BeginPrologue(TraceEventKind.TransactionCommitComponent, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new TransactionCommitComponentEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            Tsn = tsn,
            ComponentTypeId = componentTypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.BTreeInsert"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BTreeInsertEvent BeginBTreeInsert()
    {
        if (!BeginPrologue(TraceEventKind.BTreeInsert, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new BTreeInsertEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.BTreeDelete"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BTreeDeleteEvent BeginBTreeDelete()
    {
        if (!BeginPrologue(TraceEventKind.BTreeDelete, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new BTreeDeleteEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.BTreeNodeSplit"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BTreeNodeSplitEvent BeginBTreeNodeSplit()
    {
        if (!BeginPrologue(TraceEventKind.BTreeNodeSplit, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new BTreeNodeSplitEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.BTreeNodeMerge"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BTreeNodeMergeEvent BeginBTreeNodeMerge()
    {
        if (!BeginPrologue(TraceEventKind.BTreeNodeMerge, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new BTreeNodeMergeEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.PageCacheFetch"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageCacheFetchEvent BeginPageCacheFetch(int filePageIndex)
    {
        if (!BeginPrologue(TraceEventKind.PageCacheFetch, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new PageCacheFetchEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            FilePageIndex = filePageIndex,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.PageCacheDiskRead"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageCacheDiskReadEvent BeginPageCacheDiskRead(int filePageIndex)
    {
        if (!BeginPrologue(TraceEventKind.PageCacheDiskRead, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new PageCacheDiskReadEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            FilePageIndex = filePageIndex,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.PageCacheDiskWrite"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageCacheDiskWriteEvent BeginPageCacheDiskWrite(int filePageIndex)
    {
        if (!BeginPrologue(TraceEventKind.PageCacheDiskWrite, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new PageCacheDiskWriteEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            FilePageIndex = filePageIndex,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.PageCacheAllocatePage"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageCacheAllocatePageEvent BeginPageCacheAllocatePage(int filePageIndex)
    {
        if (!BeginPrologue(TraceEventKind.PageCacheAllocatePage, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new PageCacheAllocatePageEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            FilePageIndex = filePageIndex,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.PageCacheFlush"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageCacheFlushEvent BeginPageCacheFlush(int pageCount)
    {
        if (!BeginPrologue(TraceEventKind.PageCacheFlush, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new PageCacheFlushEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            PageCount = pageCount,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.ClusterMigration"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ClusterMigrationEvent BeginClusterMigration(ushort archetypeId, int migrationCount)
    {
        if (!BeginPrologue(TraceEventKind.ClusterMigration, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new ClusterMigrationEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            ArchetypeId = archetypeId,
            MigrationCount = migrationCount,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transaction persist
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.TransactionPersist"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TransactionPersistEvent BeginTransactionPersist(long tsn)
    {
        if (!BeginPrologue(TraceEventKind.TransactionPersist, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new TransactionPersistEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Tsn = tsn,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Page cache backpressure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.PageCacheBackpressure"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageCacheBackpressureEvent BeginPageCacheBackpressure()
    {
        if (!BeginPrologue(TraceEventKind.PageCacheBackpressure, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new PageCacheBackpressureEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WAL events
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.WalFlush"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WalFlushEvent BeginWalFlush(int batchByteCount, int frameCount, long highLsn)
    {
        if (!BeginPrologue(TraceEventKind.WalFlush, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new WalFlushEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            BatchByteCount = batchByteCount, FrameCount = frameCount, HighLsn = highLsn,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.WalSegmentRotate"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WalSegmentRotateEvent BeginWalSegmentRotate(int newSegmentIndex)
    {
        if (!BeginPrologue(TraceEventKind.WalSegmentRotate, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new WalSegmentRotateEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            NewSegmentIndex = newSegmentIndex,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.WalWait"/> span. Only emitted when the fast path (already durable) misses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WalWaitEvent BeginWalWait(long targetLsn)
    {
        if (!BeginPrologue(TraceEventKind.WalWait, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new WalWaitEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            TargetLsn = targetLsn,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Checkpoint events
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.CheckpointCycle"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CheckpointCycleEvent BeginCheckpointCycle(long targetLsn, CheckpointReason reason)
    {
        if (!BeginPrologue(TraceEventKind.CheckpointCycle, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new CheckpointCycleEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            TargetLsn = targetLsn, Reason = (byte)reason,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.CheckpointCollect"/> span (no payload).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CheckpointCollectEvent BeginCheckpointCollect()
    {
        if (!BeginPrologue(TraceEventKind.CheckpointCollect, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new CheckpointCollectEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.CheckpointWrite"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CheckpointWriteEvent BeginCheckpointWrite()
    {
        if (!BeginPrologue(TraceEventKind.CheckpointWrite, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new CheckpointWriteEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.CheckpointFsync"/> span (no payload).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CheckpointFsyncEvent BeginCheckpointFsync()
    {
        if (!BeginPrologue(TraceEventKind.CheckpointFsync, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new CheckpointFsyncEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.CheckpointTransition"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CheckpointTransitionEvent BeginCheckpointTransition()
    {
        if (!BeginPrologue(TraceEventKind.CheckpointTransition, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new CheckpointTransitionEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.CheckpointRecycle"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CheckpointRecycleEvent BeginCheckpointRecycle()
    {
        if (!BeginPrologue(TraceEventKind.CheckpointRecycle, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new CheckpointRecycleEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Statistics events
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.StatisticsRebuild"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatisticsRebuildEvent BeginStatisticsRebuild(int entityCount, int mutationCount, int samplingInterval)
    {
        if (!BeginPrologue(TraceEventKind.StatisticsRebuild, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new StatisticsRebuildEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            EntityCount = entityCount, MutationCount = mutationCount, SamplingInterval = samplingInterval,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scheduler-internal fast paths — called from DagScheduler wrappers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Page-cache-internal: emit a zero-duration <see cref="TraceEventKind.PageEvicted"/> marker span recording that
    /// <paramref name="evictedFilePageIndex"/> was displaced from the cache. Parents under the currently-open span via
    /// <see cref="CurrentOpenSpanId"/> TLS (typically the enclosing <see cref="TraceEventKind.PageCacheAllocatePage"/> scope), so the viewer
    /// renders it nested inside the AllocatePage bar. Reuses <see cref="PageCacheEventCodec"/>'s wire shape — no new codec.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="BeginPrologue"/> so it honours both the global <see cref="TelemetryConfig.ProfilerActive"/> gate and the
    /// per-kind deny-list (<see cref="TraceEventKind.PageEvicted"/> is suppressed by default). When suppressed the whole body dead-code
    /// eliminates in Tier 1 JIT, just like the <c>Begin*</c> factories.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageEvicted(int evictedFilePageIndex)
    {
        if (!BeginPrologue(TraceEventKind.PageEvicted, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageEvicted, traceIdHi != 0 || traceIdLo != 0, optMask: 0);
        if (slot.Buffer.TryReserve(size, out var dst))
        {
            // Zero-duration marker: end == start. PageCacheEventCodec writes the duration as (end - start), so duration lands at 0.
            PageCacheEventCodec.Encode(dst, startTs, TraceEventKind.PageEvicted, (byte)slotIdx, startTs,
                spanId, parentSpanId, traceIdHi, traceIdLo, evictedFilePageIndex, pageCount: 0, optMask: 0, out _);
            slot.Buffer.Publish();
        }

        // Restore TLS immediately — this is a zero-duration marker, not a nestable scope.
        CurrentOpenSpanId = previousSpanId;
    }

    /// <summary>
    /// Page-cache-internal: emit a <see cref="TraceEventKind.PageCacheDiskReadCompleted"/> record from a thread-pool completion thread,
    /// carrying the full async-tail duration as <c>completionTimestamp - beginTimestamp</c>. The <paramref name="spanId"/> matches the
    /// originating <see cref="TraceEventKind.PageCacheDiskRead"/> span, giving the viewer a zero-cost correlator.
    /// </summary>
    /// <remarks>
    /// <b>Thread safety:</b> runs on whichever thread completes the <c>ReadAsync</c>, not the thread that began the span. Claims that thread's
    /// own slot via <see cref="ThreadSlotRegistry.GetOrAssignSlot"/> and publishes to its SPSC ring — no cross-thread writes. Does NOT touch
    /// <see cref="CurrentOpenSpanId"/> (different thread's TLS has nothing to do with this record).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageCacheDiskReadCompleted(ulong spanId, long beginTimestamp, int filePageIndex, long completionTimestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.PageCacheDiskReadCompleted])
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskReadCompleted, hasTraceContext: false, optMask: 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheDiskReadCompleted, (byte)slotIdx, beginTimestamp,
            spanId, parentSpanId: 0, traceIdHi: 0, traceIdLo: 0, filePageIndex, pageCount: 0, optMask: 0, out _);
        slot.Buffer.Publish();
    }

    /// <summary>
    /// Page-cache-internal: emit a <see cref="TraceEventKind.PageCacheDiskWriteCompleted"/> record from a thread-pool completion thread.
    /// Same correlation pattern as <see cref="EmitPageCacheDiskReadCompleted"/> — carries the originating DiskWrite span's <paramref name="spanId"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageCacheDiskWriteCompleted(ulong spanId, long beginTimestamp, int filePageIndex, long completionTimestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.PageCacheDiskWriteCompleted])
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskWriteCompleted, hasTraceContext: false, optMask: 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheDiskWriteCompleted, (byte)slotIdx, beginTimestamp,
            spanId, parentSpanId: 0, traceIdHi: 0, traceIdLo: 0, filePageIndex, pageCount: 0, optMask: 0, out _);
        slot.Buffer.Publish();
    }

    /// <summary>
    /// Page-cache-internal: emit a <see cref="TraceEventKind.PageCacheFlushCompleted"/> record from the <c>Task.WhenAll(...).ContinueWith</c>
    /// continuation in <c>SavePages</c>. The record's duration covers the full flush tail (all WriteAsync completions + fsync).
    /// </summary>
    /// <remarks>
    /// Following Flush convention, <paramref name="pageCount"/> is stored in the primary <c>filePageIndex</c> slot of the PageCache codec —
    /// matches how <see cref="BeginPageCacheFlush"/> encodes its own record, so the decoder path is identical.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageCacheFlushCompleted(ulong spanId, long beginTimestamp, int pageCount, long completionTimestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.PageCacheFlushCompleted])
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFlushCompleted, hasTraceContext: false, optMask: 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        // Flush convention: pageCount lives in the primary "filePageIndex" slot; the optional PageCount slot is unused (optMask=0).
        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheFlushCompleted, (byte)slotIdx, beginTimestamp,
            spanId, parentSpanId: 0, traceIdHi: 0, traceIdLo: 0, filePageIndex: pageCount, pageCount: 0, optMask: 0, out _);
        slot.Buffer.Publish();
    }

    /// <summary>
    /// Scheduler-internal: emit a <see cref="TraceEventKind.SchedulerChunk"/> span covering one chunk's execution. Writes a single record
    /// with both start and end timestamps pre-computed — no <c>using var</c> scope required on the caller side.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitSchedulerChunk(int systemIdx, int chunkIdx, int totalChunks, long startTimestamp, long endTimestamp, int entitiesProcessed)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var spanId = SpanIdGenerator.NextId(slotIdx, slot);

        var evt = new SchedulerChunkEvent
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTimestamp,
            SpanId = spanId,
            ParentSpanId = CurrentOpenSpanId,
            SystemIndex = (ushort)systemIdx,
            ChunkIndex = (ushort)chunkIdx,
            TotalChunks = (ushort)totalChunks,
            EntitiesProcessed = entitiesProcessed,
        };

        var size = evt.ComputeSize();
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        evt.EncodeTo(dst, endTimestamp, out _);
        slot.Buffer.Publish();
    }

    /// <summary>
    /// Diagnostic counters for <see cref="EmitTickStart"/> drop paths. Incremented on every path that would silently discard a
    /// TickStart record — slot unavailable (registry full), ring reserve failure (buffer full). Inspected post-mortem via
    /// <see cref="TickStartDroppedNoSlot"/> / <see cref="TickStartDroppedRingFull"/>. Non-zero values in either counter on a run
    /// that emitted N ticks but has N-1 TickStart records in the trace directly identify where the loss happened.
    /// </summary>
    private static long STickStartDroppedNoSlot;
    private static long STickStartDroppedRingFull;

    /// <summary>Count of TickStart emissions dropped because the thread could not claim a slot (registry full).</summary>
    public static long TickStartDroppedNoSlot => STickStartDroppedNoSlot;

    /// <summary>Count of TickStart emissions dropped because the producer ring was full at reserve time.</summary>
    public static long TickStartDroppedRingFull => STickStartDroppedRingFull;

    /// <summary>Scheduler-internal: emit an instant <see cref="TraceEventKind.TickStart"/> marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitTickStart(long timestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            Interlocked.Increment(ref STickStartDroppedNoSlot);
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (!ring.TryReserve(TraceRecordHeader.CommonHeaderSize, out var dst))
        {
            Interlocked.Increment(ref STickStartDroppedRingFull);
            return;
        }

        InstantEventCodec.WriteTickStart(dst, (byte)slotIdx, timestamp, out _);
        ring.Publish();
    }

    /// <summary>Scheduler-internal: emit an instant <see cref="TraceEventKind.TickEnd"/> marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitTickEnd(long timestamp, byte overloadLevel, byte tickMultiplier)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        const int size = TraceRecordHeader.CommonHeaderSize + 2;
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }

        InstantEventCodec.WriteTickEnd(dst, (byte)slotIdx, timestamp, overloadLevel, tickMultiplier, out _);
        ring.Publish();
    }

    /// <summary>Scheduler-internal: emit a <see cref="TraceEventKind.PhaseStart"/> instant marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPhaseStart(TickPhase phase, long timestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        const int size = TraceRecordHeader.CommonHeaderSize + 1;
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }

        InstantEventCodec.WritePhaseStart(dst, (byte)slotIdx, timestamp, phase, out _);
        ring.Publish();
    }

    /// <summary>Scheduler-internal: emit a <see cref="TraceEventKind.PhaseEnd"/> instant marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPhaseEnd(TickPhase phase, long timestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        const int size = TraceRecordHeader.CommonHeaderSize + 1;
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }

        InstantEventCodec.WritePhaseEnd(dst, (byte)slotIdx, timestamp, phase, out _);
        ring.Publish();
    }

    /// <summary>Scheduler-internal: emit a <see cref="TraceEventKind.SystemReady"/> marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitSystemReady(ushort systemIdx, ushort predecessorCount, long timestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        const int size = TraceRecordHeader.CommonHeaderSize + 4;
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }

        InstantEventCodec.WriteSystemReady(dst, (byte)slotIdx, timestamp, systemIdx, predecessorCount, out _);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GC-ingestion-internal emit helpers (called only by GcIngestionThread —
    // slot is owned by the caller, not looked up per call)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GC-ingestion-internal: emit a <see cref="TraceEventKind.GcStart"/> instant record. The caller's <paramref name="slot"/> must be the
    /// ingestion thread's own claimed slot — preserving the per-slot SPSC invariant without any locking on the ring itself.
    /// </summary>
    /// <remarks>
    /// Does not participate in the <c>CurrentOpenSpanId</c> parent-linking scheme — GC events are process-level and independent of any ambient
    /// Typhon span. Does not read <c>Activity.Current</c> either.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitGcStart(byte slot, long timestamp, byte generation, byte reason, byte type, uint count)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slot).Buffer;
        if (ring == null || !ring.TryReserve(GcInstantEventCodec.GcStartSize, out var dst))
        {
            return;
        }
        GcInstantEventCodec.WriteGcStart(dst, slot, timestamp, generation, (GcReason)reason, (GcType)type, count, out _);
        ring.Publish();
    }

    /// <summary>
    /// GC-ingestion-internal: emit a <see cref="TraceEventKind.GcEnd"/> instant record carrying the per-gen heap-size snapshot produced by
    /// <see cref="GC.GetGCMemoryInfo()"/> on the caller side.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitGcEnd(byte slot, long timestamp,
        byte generation, uint count, long pauseDurationTicks, ulong promotedBytes,
        ulong gen0SizeAfter, ulong gen1SizeAfter, ulong gen2SizeAfter, ulong lohSizeAfter, ulong pohSizeAfter,
        ulong totalCommittedBytes)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slot).Buffer;
        if (ring == null || !ring.TryReserve(GcInstantEventCodec.GcEndSize, out var dst))
        {
            return;
        }
        GcInstantEventCodec.WriteGcEnd(dst, slot, timestamp, generation, count, pauseDurationTicks, promotedBytes,
            gen0SizeAfter, gen1SizeAfter, gen2SizeAfter, lohSizeAfter, pohSizeAfter, totalCommittedBytes, out _);
        ring.Publish();
    }

    /// <summary>
    /// GC-ingestion-internal: emit a <see cref="TraceEventKind.GcSuspension"/> span covering the window from <c>GCSuspendEEBegin</c> to
    /// <c>GCRestartEEEnd</c>. SpanId is allocated from the ingestion thread's slot generator; <c>ParentSpanId = 0</c> (process-level).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitGcSuspension(byte slot, long startTimestamp, long endTimestamp, byte reason)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        var threadSlot = ThreadSlotRegistry.GetSlot(slot);
        var ring = threadSlot.Buffer;
        if (ring == null || !ring.TryReserve(GcSuspensionEventCodec.Size, out var dst))
        {
            return;
        }
        var spanId = SpanIdGenerator.NextId(slot, threadSlot);
        GcSuspensionEventCodec.Write(dst, slot, startTimestamp, endTimestamp, spanId, parentSpanId: 0, (GcSuspendReason)reason, out _);
        ring.Publish();
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.ThreadInfo"/> instant record carrying the slot's managed thread ID and UTF-8 name. Called once by
    /// <c>ThreadSlotRegistry.AssignClaim</c> from the claiming thread immediately after the claim completes, so the record lands in that
    /// thread's own slot (single-producer invariant preserved). The viewer uses these records to label lanes with real thread names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitThreadInfo(byte slot, int managedThreadId, string name)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slot).Buffer;
        if (ring == null)
        {
            return;
        }

        // Encode the name to a stack buffer sized for typical thread-name lengths (e.g., "TyphonProfilerConsumer" = 22 B). If the name
        // somehow exceeds 256 B, fall back to ArrayPool — still no GC pressure on the hot path that matters (this is slot claim, not span).
        ReadOnlySpan<char> nameSpan = name ?? string.Empty;
        Span<byte> nameBuf = stackalloc byte[256];
        int byteCount;
        if (System.Text.Encoding.UTF8.GetByteCount(nameSpan) <= nameBuf.Length)
        {
            byteCount = System.Text.Encoding.UTF8.GetBytes(nameSpan, nameBuf);
            EmitThreadInfoCore(ring, slot, managedThreadId, nameBuf[..byteCount]);
        }
        else
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(System.Text.Encoding.UTF8.GetMaxByteCount(nameSpan.Length));
            try
            {
                byteCount = System.Text.Encoding.UTF8.GetBytes(nameSpan, rented);
                EmitThreadInfoCore(ring, slot, managedThreadId, rented.AsSpan(0, byteCount));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitThreadInfoCore(TraceRecordRing ring, byte slot, int managedThreadId, ReadOnlySpan<byte> nameUtf8)
    {
        var size = ThreadInfoEventCodec.ComputeSize(nameUtf8.Length);
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }
        ThreadInfoEventCodec.WriteThreadInfo(dst, slot, Stopwatch.GetTimestamp(), managedThreadId, nameUtf8, out _);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Memory allocation / gauge snapshot — called from MemoryAllocator and DagScheduler
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Emit a <see cref="TraceEventKind.MemoryAllocEvent"/> instant record. Called from <c>MemoryAllocator.AllocatePinned</c> /
    /// <c>AllocateArray</c> (direction=<see cref="MemoryAllocDirection.Alloc"/>) and <c>MemoryAllocator.Remove</c>
    /// (direction=<see cref="MemoryAllocDirection.Free"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <see cref="TelemetryConfig.ProfilerMemoryAllocationsActive"/> (separate knob from the master profiler gate) so operators can run
    /// the profiler for span tracing without paying per-alloc event cost. The first line's <c>if (!active) return</c> dead-code-eliminates
    /// in Tier 1 JIT when the flag is off.
    /// </para>
    /// <para>
    /// Runs on whichever thread allocates/frees — claims that thread's own ring slot, preserving the per-slot SPSC invariant.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitMemoryAlloc(MemoryAllocDirection direction, ushort sourceTag, ulong sizeBytes, ulong totalAfterBytes)
    {
        if (!TelemetryConfig.ProfilerMemoryAllocationsActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(MemoryAllocEventCodec.EventSize, out var dst))
        {
            return;
        }

        MemoryAllocEventCodec.WriteMemoryAllocEvent(dst, (byte)slotIdx, Stopwatch.GetTimestamp(),
            direction, sourceTag, sizeBytes, totalAfterBytes, out _);
        ring.Publish();
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.PerTickSnapshot"/> record carrying the caller-collected gauge values. Intended single caller:
    /// <c>DagScheduler</c> at end-of-tick, running on the scheduler thread.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="TelemetryConfig.ProfilerGaugesActive"/>. The caller is expected to prepare <paramref name="values"/> as a
    /// <c>stackalloc</c> buffer — no allocation on the hot path. Snapshot size is variable; the codec computes total wire size from the
    /// value list before claiming ring space.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitPerTickSnapshot(uint tickNumber, long timestamp, uint flags, ReadOnlySpan<GaugeValue> values)
    {
        if (!TelemetryConfig.ProfilerGaugesActive)
        {
            Interlocked.Increment(ref SSnapshotSkippedGaugesInactive);
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            Interlocked.Increment(ref SSnapshotSkippedNoSlot);
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null)
        {
            Interlocked.Increment(ref SSnapshotSkippedNullRing);
            return;
        }

        var size = PerTickSnapshotEventCodec.ComputeSize(values);
        if (!ring.TryReserve(size, out var dst))
        {
            Interlocked.Increment(ref SSnapshotSkippedRingFull);
            return;
        }

        PerTickSnapshotEventCodec.WritePerTickSnapshot(dst, (byte)slotIdx, timestamp, tickNumber, flags, values, out _);
        ring.Publish();
        Interlocked.Increment(ref SSnapshotPublished);
    }

    private static long SSnapshotPublished;
    private static long SSnapshotSkippedGaugesInactive;
    private static long SSnapshotSkippedNoSlot;
    private static long SSnapshotSkippedNullRing;
    private static long SSnapshotSkippedRingFull;

    public static long SnapshotPublished => SSnapshotPublished;
    public static long SnapshotSkippedGaugesInactive => SSnapshotSkippedGaugesInactive;
    public static long SnapshotSkippedNoSlot => SSnapshotSkippedNoSlot;
    public static long SnapshotSkippedNullRing => SSnapshotSkippedNullRing;
    public static long SnapshotSkippedRingFull => SSnapshotSkippedRingFull;

    /// <summary>Scheduler-internal: emit a <see cref="TraceEventKind.SystemSkipped"/> marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitSystemSkipped(ushort systemIdx, byte skipReason, long timestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        const int size = TraceRecordHeader.CommonHeaderSize + 3;
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }

        InstantEventCodec.WriteSystemSkipped(dst, (byte)slotIdx, timestamp, systemIdx, skipReason, out _);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Thread-local control
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Opt the current thread out of <see cref="Activity.Current"/> capture.</summary>
    public static void SuppressActivityContextOnThisThread() => SuppressActivityCapture = true;

    /// <summary>Re-enable <see cref="Activity.Current"/> capture for the current thread.</summary>
    public static void RestoreActivityContextOnThisThread() => SuppressActivityCapture = false;

    /// <summary>Set this thread's current scheduler tick number. Called by <c>DagScheduler</c> at tick entry.</summary>
    public static void SetCurrentTickNumber(int tickNumber) => CurrentTickNumber = tickNumber;

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Total records dropped across all slots due to ring-buffer overflow.</summary>
    public static long TotalDroppedEvents
    {
        get
        {
            long total = 0;
            var hwm = ThreadSlotRegistry.HighWaterMark;
            for (var i = 0; i < hwm; i++)
            {
                var buffer = ThreadSlotRegistry.GetSlot(i).Buffer;
                if (buffer != null)
                {
                    total += buffer.DroppedEvents;
                }
            }
            return total;
        }
    }

    /// <summary>Number of slots currently claimed (Active or Retiring).</summary>
    public static int ActiveSlotCount => ThreadSlotRegistry.ActiveSlotCount;
}
