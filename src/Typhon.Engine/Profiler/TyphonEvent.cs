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

        // Phase 6 — Extreme-frequency Data plane leaves stay deny-listed even when their
        // leaf gate flips on; operators must call UnsuppressKind to opt in for forensic runs.
        SuppressedKinds[(int)TraceEventKind.DataMvccChainWalk] = true;
        SuppressedKinds[(int)TraceEventKind.DataIndexBTreeSearch] = true;
        SuppressedKinds[(int)TraceEventKind.DataIndexBTreeNodeCow] = true;

        // Phase 7 — Extreme/high-frequency Query / ECS:View leaves stay deny-listed.
        SuppressedKinds[(int)TraceEventKind.QueryExecuteIterate] = true;
        SuppressedKinds[(int)TraceEventKind.QueryExecuteFilter] = true;
        SuppressedKinds[(int)TraceEventKind.QueryExecutePagination] = true;
        SuppressedKinds[(int)TraceEventKind.EcsQueryMaskAnd] = true;
        SuppressedKinds[(int)TraceEventKind.EcsViewProcessEntry] = true;
        SuppressedKinds[(int)TraceEventKind.EcsViewProcessEntryOr] = true;

        // Phase 8 — Extreme-frequency Durability leaves stay deny-listed (belt-and-suspenders Q3).
        SuppressedKinds[(int)TraceEventKind.DurabilityWalFrame] = true;
        SuppressedKinds[(int)TraceEventKind.DurabilityRecoveryRecord] = true;
        SuppressedKinds[(int)TraceEventKind.DurabilityUowState] = true;
        SuppressedKinds[(int)TraceEventKind.DurabilityUowDeadline] = true;

        // Phase 9 — High-frequency Subscription leaves deny-listed (Q5).
        SuppressedKinds[(int)TraceEventKind.RuntimeSubscriptionSubscriber] = true;
        SuppressedKinds[(int)TraceEventKind.RuntimeSubscriptionDeltaSerialize] = true;
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
    internal static void EmitPageEvicted(int evictedFilePageIndex, byte dirtyBit = 0)
    {
        if (!BeginPrologue(TraceEventKind.PageEvicted, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        // Phase 5 wire-additive: always set OptDirtyBit so the trailing 1-byte dirty flag is encoded.
        const byte optMask = PageCacheEventCodec.OptDirtyBit;
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageEvicted, traceIdHi != 0 || traceIdLo != 0, optMask);
        if (slot.Buffer.TryReserve(size, out var dst))
        {
            // Zero-duration marker: end == start. PageCacheEventCodec writes the duration as (end - start), so duration lands at 0.
            PageCacheEventCodec.Encode(dst, startTs, TraceEventKind.PageEvicted, (byte)slotIdx, startTs,
                spanId, parentSpanId, traceIdHi, traceIdLo, evictedFilePageIndex, 0, optMask, out _, dirtyBit);
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
        // Phase 5: producer-side duration threshold gate (default 1 ms).
        if (IsBelowCompletionThreshold(beginTimestamp, completionTimestamp))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskReadCompleted, false, 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheDiskReadCompleted, (byte)slotIdx, beginTimestamp,
            spanId, 0, 0, 0, filePageIndex, 0, 0, out _);
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
        // Phase 5: producer-side duration threshold gate (default 1 ms).
        if (IsBelowCompletionThreshold(beginTimestamp, completionTimestamp))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskWriteCompleted, false, 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheDiskWriteCompleted, (byte)slotIdx, beginTimestamp,
            spanId, 0, 0, 0, filePageIndex, 0, 0, out _);
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
        // Phase 5: producer-side duration threshold gate (default 1 ms).
        if (IsBelowCompletionThreshold(beginTimestamp, completionTimestamp))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFlushCompleted, false, 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        // Flush convention: pageCount lives in the primary "filePageIndex" slot; the optional PageCount slot is unused (optMask=0).
        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheFlushCompleted, (byte)slotIdx, beginTimestamp,
            spanId, 0, 0, 0, pageCount, 0, 0, out _);
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
        GcSuspensionEventCodec.Write(dst, slot, startTimestamp, endTimestamp, spanId, 0, (GcSuspendReason)reason, out _);
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
    /// <remarks>
    /// Phase 4 (#282) extended the payload (wire-additive): <paramref name="wouldBeChunkCount"/> and <paramref name="successorsUnblocked"/> are new fields.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitSystemSkipped(ushort systemIdx, byte skipReason, long timestamp, ushort wouldBeChunkCount = 0, ushort successorsUnblocked = 0)
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
        if (!ring.TryReserve(InstantEventCodec.SystemSkippedSize, out var dst))
        {
            return;
        }

        InstantEventCodec.WriteSystemSkipped(dst, (byte)slotIdx, timestamp, systemIdx, skipReason, wouldBeChunkCount, successorsUnblocked, out _);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrency tracing (Phase 2, #280) — instant Emit methods.
    //
    // All gate on a Tier-2 leaf flag from the TelemetryConfig.Concurrency*
    // tree. Every method follows the EmitMemoryAlloc shape:
    //   1. gate check (JIT-eliminated when off)
    //   2. acquire ring slot
    //   3. reserve ring space
    //   4. encode via per-subtree codec
    //   5. publish.
    //
    // Cost when Tier-2 disabled (proven by Phase 1 microbench): 0 ns.
    // Cost when enabled, ring available: ~5 ns per emission.
    // ═══════════════════════════════════════════════════════════════════════

    // ── AccessControl ──────────────────────────────────────────────────────

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSharedAcquire"/> instant.
    /// Gated on <see cref="TelemetryConfig.ConcurrencyAccessControlSharedAcquireActive"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSharedAcquire(ushort threadId, bool hadToWait, ushort elapsedUs)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSharedAcquireActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlEventCodec.AcquireSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlEventCodec.WriteAcquire(dst, TraceEventKind.ConcurrencyAccessControlSharedAcquire, (byte)slotIdx, Stopwatch.GetTimestamp(), 
            threadId, hadToWait, elapsedUs);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSharedRelease"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSharedRelease(ushort threadId)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSharedReleaseActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlEventCodec.ReleaseSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlEventCodec.WriteRelease(dst, TraceEventKind.ConcurrencyAccessControlSharedRelease, (byte)slotIdx, Stopwatch.GetTimestamp(), 
            threadId);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlExclusiveAcquire"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlExclusiveAcquire(ushort threadId, bool hadToWait, ushort elapsedUs)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlExclusiveAcquireActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlEventCodec.AcquireSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlEventCodec.WriteAcquire(dst, TraceEventKind.ConcurrencyAccessControlExclusiveAcquire, (byte)slotIdx, Stopwatch.GetTimestamp(), 
            threadId, hadToWait, elapsedUs);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlExclusiveRelease"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlExclusiveRelease(ushort threadId)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlExclusiveReleaseActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlEventCodec.ReleaseSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlEventCodec.WriteRelease(dst, TraceEventKind.ConcurrencyAccessControlExclusiveRelease, (byte)slotIdx, Stopwatch.GetTimestamp(), 
            threadId);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlPromotion"/> instant. Variant: 0 = promote (shared→exclusive), 1 = demote.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlPromotion(ushort elapsedUs, byte variant)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlPromotionActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlEventCodec.PromotionSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlEventCodec.WritePromotion(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), elapsedUs, variant);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlContention"/> instant — fires when the contention flag is set, before the wait completes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlContention()
    {
        if (!TelemetryConfig.ConcurrencyAccessControlContentionActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlEventCodec.ContentionSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlEventCodec.WriteContention(dst, (byte)slotIdx, Stopwatch.GetTimestamp());
        ring.Publish();
    }

    // ── AccessControlSmall ─────────────────────────────────────────────────

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSmallSharedAcquire"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSmallSharedAcquire(ushort threadId)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSmallSharedAcquireActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlSmallEventCodec.EventSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlSmallEventCodec.WriteEvent(dst, TraceEventKind.ConcurrencyAccessControlSmallSharedAcquire, (byte)slotIdx, Stopwatch.GetTimestamp(),
            threadId);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSmallSharedRelease"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSmallSharedRelease(ushort threadId)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSmallSharedReleaseActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlSmallEventCodec.EventSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlSmallEventCodec.WriteEvent(dst, TraceEventKind.ConcurrencyAccessControlSmallSharedRelease, (byte)slotIdx, Stopwatch.GetTimestamp(),
            threadId);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSmallExclusiveAcquire"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSmallExclusiveAcquire(ushort threadId)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSmallExclusiveAcquireActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlSmallEventCodec.EventSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlSmallEventCodec.WriteEvent(dst, TraceEventKind.ConcurrencyAccessControlSmallExclusiveAcquire, (byte)slotIdx, 
            Stopwatch.GetTimestamp(), threadId);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSmallExclusiveRelease"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSmallExclusiveRelease(ushort threadId)
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSmallExclusiveReleaseActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlSmallEventCodec.EventSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlSmallEventCodec.WriteEvent(dst, TraceEventKind.ConcurrencyAccessControlSmallExclusiveRelease, (byte)slotIdx, 
            Stopwatch.GetTimestamp(), threadId);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAccessControlSmallContention"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAccessControlSmallContention()
    {
        if (!TelemetryConfig.ConcurrencyAccessControlSmallContentionActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAccessControlSmallEventCodec.ContentionSize, out var dst))
        {
            return;
        }
        ConcurrencyAccessControlSmallEventCodec.WriteContention(dst, (byte)slotIdx, Stopwatch.GetTimestamp());
        ring.Publish();
    }

    // ── ResourceAccessControl ──────────────────────────────────────────────

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyResourceAccessing"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyResourceAccessing(bool success, byte accessingCount, ushort elapsedUs)
    {
        if (!TelemetryConfig.ConcurrencyResourceAccessControlAccessingActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyResourceAccessControlEventCodec.AccessingSize, out var dst))
        {
            return;
        }
        ConcurrencyResourceAccessControlEventCodec.WriteAccessing(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), success, accessingCount, elapsedUs);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyResourceModify"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyResourceModify(bool success, ushort threadId, ushort elapsedUs)
    {
        if (!TelemetryConfig.ConcurrencyResourceAccessControlModifyActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyResourceAccessControlEventCodec.ModifySize, out var dst))
        {
            return;
        }
        ConcurrencyResourceAccessControlEventCodec.WriteModify(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), success, threadId, elapsedUs);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyResourceDestroy"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyResourceDestroy(bool success, ushort elapsedUs)
    {
        if (!TelemetryConfig.ConcurrencyResourceAccessControlDestroyActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyResourceAccessControlEventCodec.DestroySize, out var dst))
        {
            return;
        }
        ConcurrencyResourceAccessControlEventCodec.WriteDestroy(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), success, elapsedUs);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyResourceModifyPromotion"/> instant — fires from the slow path of TryPromoteToModify when waiting for accessors to drain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyResourceModifyPromotion(ushort elapsedUs)
    {
        if (!TelemetryConfig.ConcurrencyResourceAccessControlModifyPromotionActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyResourceAccessControlEventCodec.ModifyPromotionSize, out var dst))
        {
            return;
        }
        ConcurrencyResourceAccessControlEventCodec.WriteModifyPromotion(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), elapsedUs);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyResourceContention"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyResourceContention()
    {
        if (!TelemetryConfig.ConcurrencyResourceAccessControlContentionActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyResourceAccessControlEventCodec.ContentionSize, out var dst))
        {
            return;
        }
        ConcurrencyResourceAccessControlEventCodec.WriteContention(dst, (byte)slotIdx, Stopwatch.GetTimestamp());
        ring.Publish();
    }

    // ── Epoch ──────────────────────────────────────────────────────────────

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyEpochScopeEnter"/> instant — EpochGuard.Enter.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyEpochScopeEnter(uint epoch, byte depthBefore, bool isDormantToActive)
    {
        if (!TelemetryConfig.ConcurrencyEpochScopeEnterActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyEpochEventCodec.ScopeEnterSize, out var dst))
        {
            return;
        }
        ConcurrencyEpochEventCodec.WriteScopeEnter(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), epoch, depthBefore, isDormantToActive);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyEpochScopeExit"/> instant — EpochGuard.Dispose.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyEpochScopeExit(uint epoch, bool isOutermost)
    {
        if (!TelemetryConfig.ConcurrencyEpochScopeExitActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyEpochEventCodec.ScopeExitSize, out var dst))
        {
            return;
        }
        ConcurrencyEpochEventCodec.WriteScopeExit(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), epoch, isOutermost);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyEpochAdvance"/> instant — GlobalEpoch increment.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyEpochAdvance(uint newEpoch)
    {
        if (!TelemetryConfig.ConcurrencyEpochAdvanceActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyEpochEventCodec.AdvanceSize, out var dst))
        {
            return;
        }
        ConcurrencyEpochEventCodec.WriteAdvance(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), newEpoch);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyEpochRefresh"/> instant — RefreshScope mid-scope epoch bump.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyEpochRefresh(uint oldEpoch, uint newEpoch)
    {
        if (!TelemetryConfig.ConcurrencyEpochRefreshActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyEpochEventCodec.RefreshSize, out var dst))
        {
            return;
        }
        ConcurrencyEpochEventCodec.WriteRefresh(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), oldEpoch, newEpoch);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyEpochSlotClaim"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyEpochSlotClaim(ushort slotIndex, ushort threadId, ushort activeCount)
    {
        if (!TelemetryConfig.ConcurrencyEpochSlotClaimActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyEpochEventCodec.SlotClaimSize, out var dst))
        {
            return;
        }
        ConcurrencyEpochEventCodec.WriteSlotClaim(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), slotIndex, threadId, activeCount);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyEpochSlotReclaim"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyEpochSlotReclaim(ushort slotIndex, ushort oldOwner, ushort newOwner)
    {
        if (!TelemetryConfig.ConcurrencyEpochSlotReclaimActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyEpochEventCodec.SlotReclaimSize, out var dst))
        {
            return;
        }
        ConcurrencyEpochEventCodec.WriteSlotReclaim(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), slotIndex, oldOwner, newOwner);
        ring.Publish();
    }

    // ── AdaptiveWaiter ─────────────────────────────────────────────────────

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyAdaptiveWaiterYieldOrSleep"/> instant — fires only when the current SpinOnce yielded or slept (NOT per-spin).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyAdaptiveWaiterYieldOrSleep(ushort spinCountBefore, AdaptiveWaiterTransitionKind kind)
    {
        if (!TelemetryConfig.ConcurrencyAdaptiveWaiterYieldOrSleepActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyAdaptiveWaiterEventCodec.EventSize, out var dst))
        {
            return;
        }
        ConcurrencyAdaptiveWaiterEventCodec.WriteYieldOrSleep(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), spinCountBefore, kind);
        ring.Publish();
    }

    // ── OlcLatch ───────────────────────────────────────────────────────────

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyOlcLatchWriteLockAttempt"/> instant — fires on TryWriteLock failure path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyOlcLatchWriteLockAttempt(uint versionBefore, bool success)
    {
        if (!TelemetryConfig.ConcurrencyOlcLatchWriteLockAttemptActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyOlcLatchEventCodec.WriteLockAttemptSize, out var dst))
        {
            return;
        }
        ConcurrencyOlcLatchEventCodec.WriteWriteLockAttempt(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), versionBefore, success);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyOlcLatchWriteUnlock"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyOlcLatchWriteUnlock(uint oldVersion, uint newVersion)
    {
        if (!TelemetryConfig.ConcurrencyOlcLatchWriteUnlockActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyOlcLatchEventCodec.WriteUnlockSize, out var dst))
        {
            return;
        }
        ConcurrencyOlcLatchEventCodec.WriteWriteUnlock(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), oldVersion, newVersion);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyOlcLatchMarkObsolete"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyOlcLatchMarkObsolete(uint version)
    {
        if (!TelemetryConfig.ConcurrencyOlcLatchMarkObsoleteActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyOlcLatchEventCodec.MarkObsoleteSize, out var dst))
        {
            return;
        }
        ConcurrencyOlcLatchEventCodec.WriteMarkObsolete(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), version);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.ConcurrencyOlcLatchValidationFail"/> instant — fires on optimistic re-read mismatch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConcurrencyOlcLatchValidationFail(uint expectedVersion, uint actualVersion)
    {
        if (!TelemetryConfig.ConcurrencyOlcLatchValidationFailActive)
        {
            return;
        }
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(ConcurrencyOlcLatchEventCodec.ValidationFailSize, out var dst))
        {
            return;
        }
        ConcurrencyOlcLatchEventCodec.WriteValidationFail(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), expectedVersion, actualVersion);
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

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Spatial tracing (Phase 3, #281) — kinds 117-145.
    // BeginX = span factories for queries/RTree/Maintain/TierIndex.Rebuild/Trigger.Eval.
    // EmitX = instant emitters for Grid/Cell:Index/ClusterMigration:Detect|Queue|Hysteresis/
    //         TierIndex.VersionSkip/Maintain.AabbValidate|BackPointerWrite/Trigger.Region|Occupant|Cache.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Spatial:Query spans (kinds 117-122) ─────────────────────────────────

    /// <summary>Begin a <see cref="TraceEventKind.SpatialQueryAabb"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialQueryAabbEvent BeginSpatialQueryAabb(uint categoryMask)
    {
        if (!TelemetryConfig.SpatialQueryAabbActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialQueryAabb, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialQueryAabbEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, CategoryMask = categoryMask,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialQueryRadius"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialQueryRadiusEvent BeginSpatialQueryRadius(float radius)
    {
        if (!TelemetryConfig.SpatialQueryRadiusActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialQueryRadius, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialQueryRadiusEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Radius = radius,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialQueryRay"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialQueryRayEvent BeginSpatialQueryRay(float maxDist)
    {
        if (!TelemetryConfig.SpatialQueryRayActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialQueryRay, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialQueryRayEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, MaxDist = maxDist,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialQueryFrustum"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialQueryFrustumEvent BeginSpatialQueryFrustum(byte planeCount)
    {
        if (!TelemetryConfig.SpatialQueryFrustumActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialQueryFrustum, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialQueryFrustumEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, PlaneCount = planeCount,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialQueryKnn"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialQueryKnnEvent BeginSpatialQueryKnn(ushort k)
    {
        if (!TelemetryConfig.SpatialQueryKnnActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialQueryKnn, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialQueryKnnEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, K = k,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialQueryCount"/> span. <paramref name="variant"/>: 0=AABB, 1=Radius.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialQueryCountEvent BeginSpatialQueryCount(byte variant)
    {
        if (!TelemetryConfig.SpatialQueryCountActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialQueryCount, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialQueryCountEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Variant = variant,
        };
    }

    // ── Spatial:RTree spans (kinds 123-126) ─────────────────────────────────

    /// <summary>Begin a <see cref="TraceEventKind.SpatialRTreeInsert"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialRTreeInsertEvent BeginSpatialRTreeInsert(long entityId)
    {
        if (!TelemetryConfig.SpatialRTreeInsertActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialRTreeInsert, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialRTreeInsertEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, EntityId = entityId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialRTreeRemove"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialRTreeRemoveEvent BeginSpatialRTreeRemove(long entityId)
    {
        if (!TelemetryConfig.SpatialRTreeRemoveActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialRTreeRemove, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialRTreeRemoveEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, EntityId = entityId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialRTreeNodeSplit"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialRTreeNodeSplitEvent BeginSpatialRTreeNodeSplit(byte depth)
    {
        if (!TelemetryConfig.SpatialRTreeNodeSplitActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialRTreeNodeSplit, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialRTreeNodeSplitEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Depth = depth,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialRTreeBulkLoad"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialRTreeBulkLoadEvent BeginSpatialRTreeBulkLoad(int entityCount)
    {
        if (!TelemetryConfig.SpatialRTreeBulkLoadActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialRTreeBulkLoad, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialRTreeBulkLoadEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, EntityCount = entityCount,
        };
    }

    // ── Spatial:Grid instants (kinds 127-129) ───────────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SpatialGridCellTierChange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialGridCellTierChange(int cellKey, byte oldTier, byte newTier)
    {
        if (!TelemetryConfig.SpatialGridCellTierChangeActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialGridEventCodec.CellTierChangeSize, out var dst)) return;
        SpatialGridEventCodec.WriteCellTierChange(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), cellKey, oldTier, newTier);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialGridOccupancyChange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialGridOccupancyChange(int cellKey, sbyte delta, ushort occBefore, ushort occAfter)
    {
        if (!TelemetryConfig.SpatialGridOccupancyChangeActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialGridEventCodec.OccupancyChangeSize, out var dst)) return;
        SpatialGridEventCodec.WriteOccupancyChange(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), cellKey, delta, occBefore, occAfter);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialGridClusterCellAssign"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialGridClusterCellAssign(int clusterChunkId, int cellKey, ushort archetypeId)
    {
        if (!TelemetryConfig.SpatialGridClusterCellAssignActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialGridEventCodec.ClusterCellAssignSize, out var dst)) return;
        SpatialGridEventCodec.WriteClusterCellAssign(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), clusterChunkId, cellKey, archetypeId);
        ring.Publish();
    }

    // ── Spatial:Cell:Index instants (kinds 130-132) ─────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SpatialCellIndexAdd"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialCellIndexAdd(int cellKey, int slot, int clusterChunkId, int capacity)
    {
        if (!TelemetryConfig.SpatialCellIndexAddActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialCellIndexEventCodec.AddSize, out var dst)) return;
        SpatialCellIndexEventCodec.WriteAdd(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), cellKey, slot, clusterChunkId, capacity);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialCellIndexUpdate"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialCellIndexUpdate(int cellKey, int slot)
    {
        if (!TelemetryConfig.SpatialCellIndexUpdateActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialCellIndexEventCodec.UpdateSize, out var dst)) return;
        SpatialCellIndexEventCodec.WriteUpdate(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), cellKey, slot);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialCellIndexRemove"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialCellIndexRemove(int cellKey, int slot, int swappedClusterId)
    {
        if (!TelemetryConfig.SpatialCellIndexRemoveActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialCellIndexEventCodec.RemoveSize, out var dst)) return;
        SpatialCellIndexEventCodec.WriteRemove(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), cellKey, slot, swappedClusterId);
        ring.Publish();
    }

    // ── Spatial:ClusterMigration instants (kinds 133-135) ───────────────────

    /// <summary>Emit <see cref="TraceEventKind.SpatialClusterMigrationDetect"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialClusterMigrationDetect(ushort archetypeId, int clusterChunkId, int oldCellKey, int newCellKey)
    {
        if (!TelemetryConfig.SpatialClusterMigrationDetectActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialClusterMigrationEventCodec.DetectSize, out var dst)) return;
        SpatialClusterMigrationEventCodec.WriteDetect(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), archetypeId, clusterChunkId, oldCellKey, newCellKey);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialClusterMigrationQueue"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialClusterMigrationQueue(ushort archetypeId, int clusterChunkId, ushort queueLen)
    {
        if (!TelemetryConfig.SpatialClusterMigrationQueueActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialClusterMigrationEventCodec.QueueSize, out var dst)) return;
        SpatialClusterMigrationEventCodec.WriteQueue(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), archetypeId, clusterChunkId, queueLen);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialClusterMigrationHysteresis"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialClusterMigrationHysteresis(ushort archetypeId, int clusterChunkId, float escapeDistSq)
    {
        if (!TelemetryConfig.SpatialClusterMigrationHysteresisActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialClusterMigrationEventCodec.HysteresisSize, out var dst)) return;
        SpatialClusterMigrationEventCodec.WriteHysteresis(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), archetypeId, clusterChunkId, escapeDistSq);
        ring.Publish();
    }

    // ── Spatial:TierIndex (kind 136 span + 137 instant) ─────────────────────

    /// <summary>Begin a <see cref="TraceEventKind.SpatialTierIndexRebuild"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialTierIndexRebuildEvent BeginSpatialTierIndexRebuild(ushort archetypeId)
    {
        if (!TelemetryConfig.SpatialTierIndexRebuildActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialTierIndexRebuild, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialTierIndexRebuildEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, ArchetypeId = archetypeId,
        };
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialTierIndexVersionSkip"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialTierIndexVersionSkip(ushort archetypeId, int version, byte reason)
    {
        if (!TelemetryConfig.SpatialTierIndexVersionSkipActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialTierIndexEventCodec.VersionSkipSize, out var dst)) return;
        SpatialTierIndexEventCodec.WriteVersionSkip(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), archetypeId, version, reason);
        ring.Publish();
    }

    // ── Spatial:Maintain (kinds 138-141) ────────────────────────────────────

    /// <summary>Begin a <see cref="TraceEventKind.SpatialMaintainInsert"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialMaintainInsertEvent BeginSpatialMaintainInsert(long entityPK, ushort componentTypeId)
    {
        if (!TelemetryConfig.SpatialMaintainInsertActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialMaintainInsert, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialMaintainInsertEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            EntityPK = entityPK, ComponentTypeId = componentTypeId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialMaintainUpdateSlowPath"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialMaintainUpdateSlowPathEvent BeginSpatialMaintainUpdateSlowPath(long entityPK, ushort componentTypeId, float escapeDistSq)
    {
        if (!TelemetryConfig.SpatialMaintainUpdateSlowPathActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialMaintainUpdateSlowPath, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialMaintainUpdateSlowPathEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            EntityPK = entityPK, ComponentTypeId = componentTypeId, EscapeDistSq = escapeDistSq,
        };
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialMaintainAabbValidate"/>. <paramref name="opcode"/>: 0=insert, 1=update, 2=remove.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialMaintainAabbValidate(long entityPK, ushort componentTypeId, byte opcode)
    {
        if (!TelemetryConfig.SpatialMaintainAabbValidateActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialMaintainEventCodec.AabbValidateSize, out var dst)) return;
        SpatialMaintainEventCodec.WriteAabbValidate(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), entityPK, componentTypeId, opcode);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialMaintainBackPointerWrite"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialMaintainBackPointerWrite(int componentChunkId, int leafChunkId, ushort slotIndex)
    {
        if (!TelemetryConfig.SpatialMaintainBackPointerWriteActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialMaintainEventCodec.BackPointerWriteSize, out var dst)) return;
        SpatialMaintainEventCodec.WriteBackPointerWrite(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), componentChunkId, leafChunkId, slotIndex);
        ring.Publish();
    }

    // ── Spatial:Trigger (kinds 142-145) ─────────────────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SpatialTriggerRegion"/>. <paramref name="op"/>: 0=create, 1=destroy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialTriggerRegion(byte op, ushort regionId, uint categoryMask)
    {
        if (!TelemetryConfig.SpatialTriggerRegionActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialTriggerEventCodec.RegionSize, out var dst)) return;
        SpatialTriggerEventCodec.WriteRegion(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), op, regionId, categoryMask);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.SpatialTriggerEval"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpatialTriggerEvalEvent BeginSpatialTriggerEval(ushort regionId)
    {
        if (!TelemetryConfig.SpatialTriggerEvalActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SpatialTriggerEval, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SpatialTriggerEvalEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, RegionId = regionId,
        };
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialTriggerOccupantDiff"/> (stats only — no bitmap).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialTriggerOccupantDiff(ushort regionId, ushort prevCount, ushort currCount, ushort enterCount, ushort leaveCount)
    {
        if (!TelemetryConfig.SpatialTriggerOccupantDiffActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialTriggerEventCodec.OccupantDiffSize, out var dst)) return;
        SpatialTriggerEventCodec.WriteOccupantDiff(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), regionId, prevCount, currCount, enterCount, leaveCount);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SpatialTriggerCacheInvalidate"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSpatialTriggerCacheInvalidate(ushort regionId, int oldVersion, int newVersion)
    {
        if (!TelemetryConfig.SpatialTriggerCacheInvalidateActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SpatialTriggerEventCodec.CacheInvalidateSize, out var dst)) return;
        SpatialTriggerEventCodec.WriteCacheInvalidate(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), regionId, oldVersion, newVersion);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Scheduler & Runtime tracing (Phase 4, #282) — kinds 146-164.
    // BeginX = span factories (SystemSingleThreaded, WorkerIdle, WorkerBetweenTick,
    //          DependencyFanOut, GraphBuild/Rebuild, TransactionLifecycle, SubscriptionOutputExecute).
    // EmitX = instant emitters (System StartExecution/Completion/QueueWait, Worker Wake,
    //         Dispense, DependencyReady, Overload trio, UoWCreate/Flush).
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Scheduler:System (kinds 146-149) ────────────────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SchedulerSystemStartExecution"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerSystemStartExecution(ushort sysIdx)
    {
        if (!TelemetryConfig.SchedulerSystemStartExecutionActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerSystemEventCodec.StartExecutionSize, out var dst)) return;
        SchedulerSystemEventCodec.WriteStartExecution(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), sysIdx);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SchedulerSystemCompletion"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerSystemCompletion(ushort sysIdx, byte reason, uint durationUs)
    {
        if (!TelemetryConfig.SchedulerSystemCompletionActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerSystemEventCodec.CompletionSize, out var dst)) return;
        SchedulerSystemEventCodec.WriteCompletion(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), sysIdx, reason, durationUs);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SchedulerSystemQueueWait"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerSystemQueueWait(ushort sysIdx, uint queueWaitUs)
    {
        if (!TelemetryConfig.SchedulerSystemQueueWaitActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerSystemEventCodec.QueueWaitSize, out var dst)) return;
        SchedulerSystemEventCodec.WriteQueueWait(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), sysIdx, queueWaitUs);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.SchedulerSystemSingleThreaded"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchedulerSystemSingleThreadedEvent BeginSchedulerSystemSingleThreaded(ushort sysIdx, byte isParallelQuery, ushort chunkCount)
    {
        if (!TelemetryConfig.SchedulerSystemSingleThreadedActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SchedulerSystemSingleThreaded, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SchedulerSystemSingleThreadedEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            SysIdx = sysIdx, IsParallelQuery = isParallelQuery, ChunkCount = chunkCount,
        };
    }

    // ── Scheduler:Worker (kinds 150-152) ────────────────────────────────────

    /// <summary>Begin a <see cref="TraceEventKind.SchedulerWorkerIdle"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchedulerWorkerIdleEvent BeginSchedulerWorkerIdle(byte workerId)
    {
        if (!TelemetryConfig.SchedulerWorkerIdleActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SchedulerWorkerIdle, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SchedulerWorkerIdleEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, WorkerId = workerId,
        };
    }

    /// <summary>Emit <see cref="TraceEventKind.SchedulerWorkerWake"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerWorkerWake(byte workerId, uint delayUs)
    {
        if (!TelemetryConfig.SchedulerWorkerWakeActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerWorkerEventCodec.WakeSize, out var dst)) return;
        SchedulerWorkerEventCodec.WriteWake(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), workerId, delayUs);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.SchedulerWorkerBetweenTick"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchedulerWorkerBetweenTickEvent BeginSchedulerWorkerBetweenTick(byte workerId)
    {
        if (!TelemetryConfig.SchedulerWorkerBetweenTickActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SchedulerWorkerBetweenTick, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SchedulerWorkerBetweenTickEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, WorkerId = workerId,
        };
    }

    // ── Scheduler:Dispense (kind 153) ───────────────────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SchedulerDispense"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerDispense(ushort sysIdx, int chunkIdx, byte workerId)
    {
        if (!TelemetryConfig.SchedulerDispenseActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerDispenseEventCodec.Size, out var dst)) return;
        SchedulerDispenseEventCodec.Write(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), sysIdx, chunkIdx, workerId);
        ring.Publish();
    }

    // ── Scheduler:Dependency (kinds 154-155) ────────────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SchedulerDependencyReady"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerDependencyReady(ushort fromSysIdx, ushort toSysIdx, ushort fanOut, ushort predRemain)
    {
        if (!TelemetryConfig.SchedulerDependencyReadyActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerDependencyEventCodec.ReadySize, out var dst)) return;
        SchedulerDependencyEventCodec.WriteReady(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), fromSysIdx, toSysIdx, fanOut, predRemain);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.SchedulerDependencyFanOut"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchedulerDependencyFanOutEvent BeginSchedulerDependencyFanOut(ushort completingSysIdx)
    {
        if (!TelemetryConfig.SchedulerDependencyFanOutActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SchedulerDependencyFanOut, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SchedulerDependencyFanOutEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, CompletingSysIdx = completingSysIdx,
        };
    }

    // ── Scheduler:Overload (kinds 156-158) ──────────────────────────────────

    /// <summary>Emit <see cref="TraceEventKind.SchedulerOverloadLevelChange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerOverloadLevelChange(byte prevLvl, byte newLvl, float ratio, int queueDepth, byte oldMul, byte newMul)
    {
        if (!TelemetryConfig.SchedulerOverloadLevelChangeActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerOverloadEventCodec.LevelChangeSize, out var dst)) return;
        SchedulerOverloadEventCodec.WriteLevelChange(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), prevLvl, newLvl, ratio, queueDepth, oldMul, newMul);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SchedulerOverloadSystemShed"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerOverloadSystemShed(ushort sysIdx, byte level, ushort divisor, byte decision)
    {
        if (!TelemetryConfig.SchedulerOverloadSystemShedActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerOverloadEventCodec.SystemShedSize, out var dst)) return;
        SchedulerOverloadEventCodec.WriteSystemShed(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), sysIdx, level, divisor, decision);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.SchedulerOverloadTickMultiplier"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerOverloadTickMultiplier(long tick, byte multiplier, byte intervalTicks)
    {
        if (!TelemetryConfig.SchedulerOverloadTickMultiplierActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(SchedulerOverloadEventCodec.TickMultiplierSize, out var dst)) return;
        SchedulerOverloadEventCodec.WriteTickMultiplier(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), tick, multiplier, intervalTicks);
        ring.Publish();
    }

    // ── Scheduler:Graph (kinds 159-160) ─────────────────────────────────────

    /// <summary>Begin a <see cref="TraceEventKind.SchedulerGraphBuild"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchedulerGraphBuildEvent BeginSchedulerGraphBuild()
    {
        if (!TelemetryConfig.SchedulerGraphBuildActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SchedulerGraphBuild, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SchedulerGraphBuildEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.SchedulerGraphRebuild"/> span. Design stub — no Phase 4 producer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchedulerGraphRebuildEvent BeginSchedulerGraphRebuild(ushort oldSysCount, ushort newSysCount, byte reason)
    {
        if (!TelemetryConfig.SchedulerGraphRebuildActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.SchedulerGraphRebuild, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new SchedulerGraphRebuildEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            OldSysCount = oldSysCount, NewSysCount = newSysCount, Reason = reason,
        };
    }

    // ── Runtime:Phase + Transaction + Subscription (kinds 161-164) ──────────

    /// <summary>Emit <see cref="TraceEventKind.RuntimePhaseUoWCreate"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitRuntimePhaseUoWCreate(long tick)
    {
        if (!TelemetryConfig.RuntimePhaseUoWCreateActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(RuntimeEventCodec.UoWCreateSize, out var dst)) return;
        RuntimeEventCodec.WriteUoWCreate(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), tick);
        ring.Publish();
    }

    /// <summary>Emit <see cref="TraceEventKind.RuntimePhaseUoWFlush"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitRuntimePhaseUoWFlush(long tick, int changeCount)
    {
        if (!TelemetryConfig.RuntimePhaseUoWFlushActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(RuntimeEventCodec.UoWFlushSize, out var dst)) return;
        RuntimeEventCodec.WriteUoWFlush(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), tick, changeCount);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.RuntimeTransactionLifecycle"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeTransactionLifecycleEvent BeginRuntimeTransactionLifecycle(ushort sysIdx)
    {
        if (!TelemetryConfig.RuntimeTransactionLifecycleActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.RuntimeTransactionLifecycle, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new RuntimeTransactionLifecycleEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, SysIdx = sysIdx,
        };
    }

    // ── TLS-based cross-method Tx Lifecycle bracketing ────────────────────────────────────────
    // Used by TyphonRuntime.OnSystemStart/EndInternal: those run on the same worker thread sequentially
    // for a given system, but in different methods — so the ref-struct factory doesn't fit. Instead we
    // stash start state in TLS at OnSystemStart and consume it at OnSystemEnd.

    [ThreadStatic] private static long _txLifecycleStartTs;
    [ThreadStatic] private static ulong _txLifecycleSpanId;
    [ThreadStatic] private static ulong _txLifecyclePreviousSpanId;
    [ThreadStatic] private static ulong _txLifecycleParentSpanId;
    [ThreadStatic] private static ulong _txLifecycleTraceIdHi;
    [ThreadStatic] private static ulong _txLifecycleTraceIdLo;
    [ThreadStatic] private static int _txLifecycleSlotIdx;
    [ThreadStatic] private static ushort _txLifecycleSysIdx;

    /// <summary>Cross-method begin for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>. Pair with <see cref="EmitRuntimeTransactionLifecycleEnd"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginRuntimeTransactionLifecycleTls(ushort sysIdx)
    {
        if (!TelemetryConfig.RuntimeTransactionLifecycleActive)
        {
            _txLifecycleSpanId = 0;
            return;
        }
        if (!BeginPrologue(TraceEventKind.RuntimeTransactionLifecycle, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            _txLifecycleSpanId = 0;
            return;
        }
        _txLifecycleStartTs = startTs;
        _txLifecycleSpanId = spanId;
        _txLifecyclePreviousSpanId = previousSpanId;
        _txLifecycleParentSpanId = parentSpanId;
        _txLifecycleTraceIdHi = traceIdHi;
        _txLifecycleTraceIdLo = traceIdLo;
        _txLifecycleSlotIdx = slotIdx;
        _txLifecycleSysIdx = sysIdx;
    }

    /// <summary>Cross-method end for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>. Reads TLS state set by <see cref="BeginRuntimeTransactionLifecycleTls"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitRuntimeTransactionLifecycleEnd(bool success)
    {
        var spanId = _txLifecycleSpanId;
        if (spanId == 0)
        {
            return;
        }
        var endTs = Stopwatch.GetTimestamp();
        var startTs = _txLifecycleStartTs;
        var slotIdx = _txLifecycleSlotIdx;
        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var ring = slot.Buffer;
        var hasTC = _txLifecycleTraceIdHi != 0 || _txLifecycleTraceIdLo != 0;
        var size = RuntimeEventCodec.ComputeSizeLifecycle(hasTC);
        if (ring != null && ring.TryReserve(size, out var dst))
        {
            var durationTicks = endTs - startTs;
            var txDurUs = (uint)Math.Min((durationTicks * 1_000_000L) / Stopwatch.Frequency, uint.MaxValue);
            RuntimeEventCodec.EncodeLifecycle(dst, endTs, (byte)slotIdx, startTs,
                spanId, _txLifecycleParentSpanId, _txLifecycleTraceIdHi, _txLifecycleTraceIdLo,
                _txLifecycleSysIdx, txDurUs, success ? (byte)1 : (byte)0, out _);
            ring.Publish();
        }
        CurrentOpenSpanId = _txLifecyclePreviousSpanId;
        _txLifecycleSpanId = 0;
    }

    /// <summary>Begin a <see cref="TraceEventKind.RuntimeSubscriptionOutputExecute"/> span.</summary>
    /// <remarks>Stats fields (clientCount, viewsRefreshed, deltasPushed, overflowCount) default to 0; Phase 9 wires them through.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionOutputExecuteEvent BeginRuntimeSubscriptionOutputExecute(long tick, byte level)
    {
        if (!TelemetryConfig.RuntimeSubscriptionOutputExecuteActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionOutputExecute, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new RuntimeSubscriptionOutputExecuteEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Tick = tick, Level = level,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 5 — Storage & Memory factories
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <c>true</c> when the configured Phase 5 completion threshold (kinds 56/57/58) is positive AND the elapsed
    /// duration falls below it. Producer-side gate — keeps short-IO traffic out of the ring without changing wire format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBelowCompletionThreshold(long beginTimestamp, long completionTimestamp)
    {
        var thresholdMs = TelemetryConfig.StoragePageCacheCompletionThresholdMs;
        if (thresholdMs <= 0)
        {
            return false;
        }
        var durationMs = (completionTimestamp - beginTimestamp) * 1000L / Stopwatch.Frequency;
        return durationMs < thresholdMs;
    }

    /// <summary>Begin a <see cref="TraceEventKind.StoragePageCacheDirtyWalk"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StoragePageCacheDirtyWalkEvent BeginStoragePageCacheDirtyWalk(int rangeStart, int rangeLen)
    {
        if (!TelemetryConfig.StoragePageCacheDirtyWalkActive)
        {
            return default;
        }
        if (!BeginPrologue(TraceEventKind.StoragePageCacheDirtyWalk, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new StoragePageCacheDirtyWalkEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            RangeStart = rangeStart, RangeLen = rangeLen, DirtyMs = 0,
        };
    }

    /// <summary>Emit a <see cref="TraceEventKind.StorageSegmentCreate"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitStorageSegmentCreate(int segmentId, int pageCount)
    {
        if (!TelemetryConfig.StorageSegmentCreateActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(StorageSegmentEventCodec.CreateLoadSize, out var dst)) return;
        StorageSegmentEventCodec.WriteCreate(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), segmentId, pageCount);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.StorageSegmentGrow"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitStorageSegmentGrow(int segmentId, int oldLen, int newLen)
    {
        if (!TelemetryConfig.StorageSegmentGrowActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(StorageSegmentEventCodec.GrowSize, out var dst)) return;
        StorageSegmentEventCodec.WriteGrow(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), segmentId, oldLen, newLen);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.StorageSegmentLoad"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitStorageSegmentLoad(int segmentId, int pageCount)
    {
        if (!TelemetryConfig.StorageSegmentLoadActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(StorageSegmentEventCodec.CreateLoadSize, out var dst)) return;
        StorageSegmentEventCodec.WriteLoad(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), segmentId, pageCount);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.StorageChunkSegmentGrow"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitStorageChunkSegmentGrow(int stride, int oldCap, int newCap)
    {
        if (!TelemetryConfig.StorageChunkSegmentGrowActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(StorageMiscEventCodec.ChunkSegmentGrowSize, out var dst)) return;
        StorageMiscEventCodec.WriteChunkSegmentGrow(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), stride, oldCap, newCap);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.StorageFileHandle"/> instant. <paramref name="op"/> = 0 (open) or 1 (close).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitStorageFileHandle(byte op, int filePathId, byte modeOrReason)
    {
        if (!TelemetryConfig.StorageFileHandleEnabledActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(StorageMiscEventCodec.FileHandleSize, out var dst)) return;
        StorageMiscEventCodec.WriteFileHandle(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), op, filePathId, modeOrReason);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.StorageOccupancyMapGrow"/> instant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitStorageOccupancyMapGrow(int oldCap, int newCap)
    {
        if (!TelemetryConfig.StorageOccupancyMapGrowActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(StorageMiscEventCodec.OccupancyMapGrowSize, out var dst)) return;
        StorageMiscEventCodec.WriteOccupancyMapGrow(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), oldCap, newCap);
        ring.Publish();
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.MemoryAlignmentWaste"/> instant. The producer-side gate is the
    /// <see cref="TelemetryConfig.MemoryAlignmentWasteActive"/> flag; the call site additionally suppresses zero-waste cases.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitMemoryAlignmentWaste(int size, int alignment, ushort wastePctHundredths)
    {
        if (!TelemetryConfig.MemoryAlignmentWasteActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(MemoryAlignmentWasteEventCodec.Size, out var dst)) return;
        MemoryAlignmentWasteEventCodec.Write(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), size, alignment, wastePctHundredths);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 6 — Data plane factories
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a <see cref="TraceEventKind.DataTransactionInit"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataTransactionInitEvent BeginDataTransactionInit(long tsn, ushort uowId)
    {
        if (!TelemetryConfig.DataTransactionInitActive) return default;
        if (!BeginPrologue(TraceEventKind.DataTransactionInit, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataTransactionInitEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            Tsn = tsn, UowId = uowId,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.DataTransactionPrepare"/> span (high-freq, default-suppressed at the leaf gate).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataTransactionPrepareEvent BeginDataTransactionPrepare(long tsn)
    {
        if (!TelemetryConfig.DataTransactionPrepareActive) return default;
        if (!BeginPrologue(TraceEventKind.DataTransactionPrepare, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataTransactionPrepareEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Tsn = tsn,
        };
    }

    /// <summary>Begin a <see cref="TraceEventKind.DataTransactionValidate"/> span (wraps the commit-loop validation pass).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataTransactionValidateEvent BeginDataTransactionValidate(long tsn, int entryCount)
    {
        if (!TelemetryConfig.DataTransactionValidateActive) return default;
        if (!BeginPrologue(TraceEventKind.DataTransactionValidate, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataTransactionValidateEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Tsn = tsn, EntryCount = entryCount,
        };
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataTransactionConflict"/> instant — fires only when a real conflict is detected.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataTransactionConflict(long tsn, long pk, int componentTypeId, byte conflictType)
    {
        if (!TelemetryConfig.DataTransactionConflictActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataTransactionEventCodec.ConflictSize, out var dst)) return;
        DataTransactionEventCodec.WriteConflict(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), tsn, pk, componentTypeId, conflictType);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.DataTransactionCleanup"/> span (wraps deferred-cleanup batch enqueue).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataTransactionCleanupEvent BeginDataTransactionCleanup(long tsn, int entityCount)
    {
        if (!TelemetryConfig.DataTransactionCleanupActive) return default;
        if (!BeginPrologue(TraceEventKind.DataTransactionCleanup, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataTransactionCleanupEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Tsn = tsn, EntityCount = entityCount,
        };
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataMvccChainWalk"/> instant. Slow path — only emitted when the full revision-chain walk runs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataMvccChainWalk(long tsn, byte chainLen, byte visibility)
    {
        if (!TelemetryConfig.DataMvccChainWalkActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DataMvccChainWalk]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataMvccEventCodec.ChainWalkSize, out var dst)) return;
        DataMvccEventCodec.WriteChainWalk(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), tsn, chainLen, visibility);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.DataMvccVersionCleanup"/> span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataMvccVersionCleanupEvent BeginDataMvccVersionCleanup(long pk)
    {
        if (!TelemetryConfig.DataMvccVersionCleanupActive) return default;
        if (!BeginPrologue(TraceEventKind.DataMvccVersionCleanup, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataMvccVersionCleanupEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, Pk = pk, EntriesFreed = 0,
        };
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataIndexBTreeSearch"/> instant. Extreme-frequency — also default-suppressed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataIndexBTreeSearch(byte retryReason, byte restartCount)
    {
        if (!TelemetryConfig.DataIndexBTreeSearchActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DataIndexBTreeSearch]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataIndexBTreeEventCodec.SearchSize, out var dst)) return;
        DataIndexBTreeEventCodec.WriteSearch(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), retryReason, restartCount);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.DataIndexBTreeRangeScan"/> span (covers the whole enumeration).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataIndexBTreeRangeScanEvent BeginDataIndexBTreeRangeScan()
    {
        if (!TelemetryConfig.DataIndexBTreeRangeScanActive) return default;
        if (!BeginPrologue(TraceEventKind.DataIndexBTreeRangeScan, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataIndexBTreeRangeScanEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo,
            ResultCount = 0, RestartCount = 0,
        };
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataIndexBTreeRangeScanRevalidate"/> instant on each OLC restart.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataIndexBTreeRangeScanRevalidate(byte restartCount)
    {
        if (!TelemetryConfig.DataIndexBTreeRangeScanRevalidateActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataIndexBTreeEventCodec.RevalidateSize, out var dst)) return;
        DataIndexBTreeEventCodec.WriteRevalidate(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), restartCount);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataIndexBTreeRebalanceFallback"/> instant when OLC retry fails and pessimistic path takes over.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataIndexBTreeRebalanceFallback(byte reason)
    {
        if (!TelemetryConfig.DataIndexBTreeRebalanceFallbackActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataIndexBTreeEventCodec.RebalanceFallbackSize, out var dst)) return;
        DataIndexBTreeEventCodec.WriteRebalanceFallback(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), reason);
        ring.Publish();
    }

    /// <summary>Begin a <see cref="TraceEventKind.DataIndexBTreeBulkInsert"/> span (multi-value index insert).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DataIndexBTreeBulkInsertEvent BeginDataIndexBTreeBulkInsert(int bufferId, int entryCount)
    {
        if (!TelemetryConfig.DataIndexBTreeBulkInsertActive) return default;
        if (!BeginPrologue(TraceEventKind.DataIndexBTreeBulkInsert, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return default;
        }
        return new DataIndexBTreeBulkInsertEvent
        {
            ThreadSlot = (byte)slotIdx, StartTimestamp = startTs, SpanId = spanId, ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId, TraceIdHi = traceIdHi, TraceIdLo = traceIdLo, BufferId = bufferId, EntryCount = entryCount,
        };
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataIndexBTreeRoot"/> instant. <paramref name="op"/> = 0 (Init) or 1 (Split).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataIndexBTreeRoot(byte op, int rootChunkId, byte height)
    {
        if (!TelemetryConfig.DataIndexBTreeRootActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataIndexBTreeEventCodec.RootSize, out var dst)) return;
        DataIndexBTreeEventCodec.WriteRoot(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), op, rootChunkId, height);
        ring.Publish();
    }

    /// <summary>Emit a <see cref="TraceEventKind.DataIndexBTreeNodeCow"/> instant on PreDirtyForWrite. Extreme-frequency — also default-suppressed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDataIndexBTreeNodeCow(int srcChunkId, int dstChunkId)
    {
        if (!TelemetryConfig.DataIndexBTreeNodeCowActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DataIndexBTreeNodeCow]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DataIndexBTreeEventCodec.NodeCowSize, out var dst)) return;
        DataIndexBTreeEventCodec.WriteNodeCow(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), srcChunkId, dstChunkId);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 7 — Query / ECS:Query / ECS:View factories
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryParseEvent BeginQueryParse(ushort predicateCount, byte branchCount)
    {
        if (!TelemetryConfig.QueryParseActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryParse, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryParseEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, PredicateCount = predicateCount, BranchCount = branchCount };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryParseDnfEvent BeginQueryParseDnf(ushort inBranches, ushort outBranches)
    {
        if (!TelemetryConfig.QueryParseDnfActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryParseDnf, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryParseDnfEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, InBranches = inBranches, OutBranches = outBranches };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryPlanEvent BeginQueryPlan(byte evaluatorCount, ushort indexFieldIdx, long rangeMin, long rangeMax)
    {
        if (!TelemetryConfig.QueryPlanEnabledActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryPlan, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryPlanEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, EvaluatorCount = evaluatorCount, IndexFieldIdx = indexFieldIdx, RangeMin = rangeMin, RangeMax = rangeMax };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryEstimateEvent BeginQueryEstimate(ushort fieldIdx, long cardinality)
    {
        if (!TelemetryConfig.QueryEstimateActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryEstimate, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryEstimateEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, FieldIdx = fieldIdx, Cardinality = cardinality };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitQueryPlanPrimarySelect(byte candidates, byte winnerIdx, byte reason)
    {
        if (!TelemetryConfig.QueryPlanPrimarySelectActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(QueryEventCodec.PrimarySelectSize, out var dst)) return;
        QueryEventCodec.WritePrimarySelect(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), candidates, winnerIdx, reason);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryPlanSortEvent BeginQueryPlanSort(byte evaluatorCount, uint sortNs)
    {
        if (!TelemetryConfig.QueryPlanSortActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryPlanSort, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryPlanSortEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, EvaluatorCount = evaluatorCount, SortNs = sortNs };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryExecuteIndexScanEvent BeginQueryExecuteIndexScan(ushort primaryFieldIdx, byte mode)
    {
        if (!TelemetryConfig.QueryExecuteIndexScanActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryExecuteIndexScan, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryExecuteIndexScanEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, PrimaryFieldIdx = primaryFieldIdx, Mode = mode };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryExecuteIterateEvent BeginQueryExecuteIterate()
    {
        if (!TelemetryConfig.QueryExecuteIterateActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryExecuteIterate, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryExecuteIterateEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryExecuteFilterEvent BeginQueryExecuteFilter(byte filterCount)
    {
        if (!TelemetryConfig.QueryExecuteFilterActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryExecuteFilter, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryExecuteFilterEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, FilterCount = filterCount };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryExecutePaginationEvent BeginQueryExecutePagination(int skip, int take)
    {
        if (!TelemetryConfig.QueryExecutePaginationActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryExecutePagination, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryExecutePaginationEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, Skip = skip, Take = take };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitQueryExecuteStorageMode(byte mode)
    {
        if (!TelemetryConfig.QueryExecuteStorageModeActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(QueryEventCodec.StorageModeSize, out var dst)) return;
        QueryEventCodec.WriteStorageMode(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), mode);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryCountEvent BeginQueryCount()
    {
        if (!TelemetryConfig.QueryCountActive) return default;
        if (!BeginPrologue(TraceEventKind.QueryCount, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new QueryCountEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }

    // ── ECS:Query depth ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsQueryConstructEvent BeginEcsQueryConstruct(ushort targetArchId, byte polymorphic, byte maskSize)
    {
        if (!TelemetryConfig.EcsQueryConstructActive) return default;
        if (!BeginPrologue(TraceEventKind.EcsQueryConstruct, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new EcsQueryConstructEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, TargetArchId = targetArchId, Polymorphic = polymorphic, MaskSize = maskSize };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsQueryMaskAnd(ushort bitsBefore, ushort bitsAfter, byte opType)
    {
        if (!TelemetryConfig.EcsQueryMaskAndActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsQueryDepthEventCodec.MaskAndSize, out var dst)) return;
        EcsQueryDepthEventCodec.WriteMaskAnd(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), bitsBefore, bitsAfter, opType);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsQuerySubtreeExpandEvent BeginEcsQuerySubtreeExpand(ushort subtreeCount, ushort rootId)
    {
        if (!TelemetryConfig.EcsQuerySubtreeExpandActive) return default;
        if (!BeginPrologue(TraceEventKind.EcsQuerySubtreeExpand, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new EcsQuerySubtreeExpandEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, SubtreeCount = subtreeCount, RootId = rootId };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsQueryConstraintEnabled(ushort typeId, byte enableBit)
    {
        if (!TelemetryConfig.EcsQueryConstraintEnabledActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsQueryDepthEventCodec.ConstraintEnabledSize, out var dst)) return;
        EcsQueryDepthEventCodec.WriteConstraintEnabled(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), typeId, enableBit);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsQuerySpatialAttach(byte spatialType, float qbX1, float qbY1, float qbX2, float qbY2)
    {
        if (!TelemetryConfig.EcsQuerySpatialAttachActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsQueryDepthEventCodec.SpatialAttachSize, out var dst)) return;
        EcsQueryDepthEventCodec.WriteSpatialAttach(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), spatialType, qbX1, qbY1, qbX2, qbY2);
        ring.Publish();
    }

    // ── ECS:View depth ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsViewRefreshPullEvent BeginEcsViewRefreshPull(uint queryNs, ushort archetypeMaskBits)
    {
        if (!TelemetryConfig.EcsViewRefreshPullActive) return default;
        if (!BeginPrologue(TraceEventKind.EcsViewRefreshPull, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new EcsViewRefreshPullEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, QueryNs = queryNs, ArchetypeMaskBits = archetypeMaskBits };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsViewIncrementalDrainEvent BeginEcsViewIncrementalDrain()
    {
        if (!TelemetryConfig.EcsViewIncrementalDrainActive) return default;
        if (!BeginPrologue(TraceEventKind.EcsViewIncrementalDrain, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new EcsViewIncrementalDrainEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.EcsViewDeltaBufferOverflow"/> instant — operationally critical, never default-suppressed.
    /// Still gated by master <see cref="TelemetryConfig.ProfilerActive"/> and parent <see cref="TelemetryConfig.EcsViewDeltaBufferOverflowActive"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsViewDeltaBufferOverflow(long currentTsn, long tailTsn, ushort marginPagesLost)
    {
        if (!TelemetryConfig.EcsViewDeltaBufferOverflowActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsViewEventCodec.DeltaBufferOverflowSize, out var dst)) return;
        EcsViewEventCodec.WriteDeltaBufferOverflow(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), currentTsn, tailTsn, marginPagesLost);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsViewProcessEntry(long pk, ushort fieldIdx, byte pass)
    {
        if (!TelemetryConfig.EcsViewProcessEntryActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsViewEventCodec.ProcessEntrySize, out var dst)) return;
        EcsViewEventCodec.WriteProcessEntry(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), pk, fieldIdx, pass);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsViewProcessEntryOr(long pk, byte branchCount, uint bitmapDelta)
    {
        if (!TelemetryConfig.EcsViewProcessEntryOrActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsViewEventCodec.ProcessEntryOrSize, out var dst)) return;
        EcsViewEventCodec.WriteProcessEntryOr(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), pk, branchCount, bitmapDelta);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsViewRefreshFullEvent BeginEcsViewRefreshFull(int oldCount, int newCount, uint requeryNs)
    {
        if (!TelemetryConfig.EcsViewRefreshFullActive) return default;
        if (!BeginPrologue(TraceEventKind.EcsViewRefreshFull, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new EcsViewRefreshFullEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, OldCount = oldCount, NewCount = newCount, RequeryNs = requeryNs };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EcsViewRefreshFullOrEvent BeginEcsViewRefreshFullOr(int oldCount, int newCount, byte branchCount)
    {
        if (!TelemetryConfig.EcsViewRefreshFullOrActive) return default;
        if (!BeginPrologue(TraceEventKind.EcsViewRefreshFullOr, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new EcsViewRefreshFullOrEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, OldCount = oldCount, NewCount = newCount, BranchCount = branchCount };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsViewRegistryRegister(ushort viewId, ushort fieldIdx, ushort regCount)
    {
        if (!TelemetryConfig.EcsViewRegistryRegisterActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsViewEventCodec.RegistrySize, out var dst)) return;
        EcsViewEventCodec.WriteRegistryRegister(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), viewId, fieldIdx, regCount);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsViewRegistryDeregister(ushort viewId, ushort fieldIdx, ushort regCount)
    {
        if (!TelemetryConfig.EcsViewRegistryDeregisterActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsViewEventCodec.RegistrySize, out var dst)) return;
        EcsViewEventCodec.WriteRegistryDeregister(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), viewId, fieldIdx, regCount);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitEcsViewDeltaCacheMiss(long pk, byte reason)
    {
        if (!TelemetryConfig.EcsViewDeltaCacheMissActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(EcsViewEventCodec.DeltaCacheMissSize, out var dst)) return;
        EcsViewEventCodec.WriteDeltaCacheMiss(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), pk, reason);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 8 — Durability factories (WAL / Checkpoint / Recovery / UoW)
    // ═══════════════════════════════════════════════════════════════════════

    // ── WAL ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityWalQueueDrainEvent BeginDurabilityWalQueueDrain(int bytesAligned, int frameCount)
    {
        if (!TelemetryConfig.DurabilityWalQueueDrainActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityWalQueueDrain, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityWalQueueDrainEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, BytesAligned = bytesAligned, FrameCount = frameCount };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityWalOsWriteEvent BeginDurabilityWalOsWrite(int bytesAligned, int frameCount, long highLsn)
    {
        if (!TelemetryConfig.DurabilityWalOsWriteActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityWalOsWrite, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityWalOsWriteEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, BytesAligned = bytesAligned, FrameCount = frameCount, HighLsn = highLsn };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityWalSignalEvent BeginDurabilityWalSignal(long highLsn)
    {
        if (!TelemetryConfig.DurabilityWalSignalActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityWalSignal, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityWalSignalEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, HighLsn = highLsn };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityWalGroupCommit(ushort triggerMs, int producerThread)
    {
        if (!TelemetryConfig.DurabilityWalGroupCommitActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityWalEventCodec.GroupCommitSize, out var dst)) return;
        DurabilityWalEventCodec.WriteGroupCommit(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), triggerMs, producerThread);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityWalQueue(byte drainAttempt, int dataLen, byte waitReason)
    {
        if (!TelemetryConfig.DurabilityWalQueueActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityWalEventCodec.QueueSize, out var dst)) return;
        DurabilityWalEventCodec.WriteQueue(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), drainAttempt, dataLen, waitReason);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityWalBufferEvent BeginDurabilityWalBuffer(int bytesAligned, int pad)
    {
        if (!TelemetryConfig.DurabilityWalBufferActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityWalBuffer, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityWalBufferEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, BytesAligned = bytesAligned, Pad = pad };
    }

    /// <summary>Emit per-frame WAL CRC instant — extreme-freq, deny-listed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityWalFrame(ushort frameCount, uint crcStart)
    {
        if (!TelemetryConfig.DurabilityWalFrameActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DurabilityWalFrame]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityWalEventCodec.FrameSize, out var dst)) return;
        DurabilityWalEventCodec.WriteFrame(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), frameCount, crcStart);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityWalBackpressureEvent BeginDurabilityWalBackpressure(uint waitUs, int producerThread)
    {
        if (!TelemetryConfig.DurabilityWalBackpressureActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityWalBackpressure, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityWalBackpressureEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, WaitUs = waitUs, ProducerThread = producerThread };
    }

    // ── Checkpoint depth ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityCheckpointWriteBatchEvent BeginDurabilityCheckpointWriteBatch(int writeBatchSize, int stagingAllocated)
    {
        if (!TelemetryConfig.DurabilityCheckpointWriteBatchActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityCheckpointWriteBatch, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityCheckpointWriteBatchEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, WriteBatchSize = writeBatchSize, StagingAllocated = stagingAllocated };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityCheckpointBackpressureEvent BeginDurabilityCheckpointBackpressure(uint waitMs, byte exhausted)
    {
        if (!TelemetryConfig.DurabilityCheckpointBackpressureActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityCheckpointBackpressure, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityCheckpointBackpressureEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, WaitMs = waitMs, Exhausted = exhausted };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityCheckpointSleepEvent BeginDurabilityCheckpointSleep(uint sleepMs, byte wakeReason)
    {
        if (!TelemetryConfig.DurabilityCheckpointSleepActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityCheckpointSleep, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityCheckpointSleepEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, SleepMs = sleepMs, WakeReason = wakeReason };
    }

    // ── Recovery ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityRecoveryStart(long checkpointLsn, byte reason)
    {
        if (!TelemetryConfig.DurabilityRecoveryStartActive) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityRecoveryEventCodec.StartSize, out var dst)) return;
        DurabilityRecoveryEventCodec.WriteStart(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), checkpointLsn, reason);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityRecoveryDiscoverEvent BeginDurabilityRecoveryDiscover(int segCount, long totalBytes, int firstSegId)
    {
        if (!TelemetryConfig.DurabilityRecoveryDiscoverActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityRecoveryDiscover, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityRecoveryDiscoverEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, SegCount = segCount, TotalBytes = totalBytes, FirstSegId = firstSegId };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityRecoverySegmentEvent BeginDurabilityRecoverySegment(int segId)
    {
        if (!TelemetryConfig.DurabilityRecoverySegmentActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityRecoverySegment, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityRecoverySegmentEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, SegId = segId, RecCount = 0, Bytes = 0, Truncated = 0 };
    }

    /// <summary>Emit recovery per-record instant — extreme-freq, deny-listed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityRecoveryRecord(byte chunkType, long lsn, int size)
    {
        if (!TelemetryConfig.DurabilityRecoveryRecordActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DurabilityRecoveryRecord]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityRecoveryEventCodec.RecordSize, out var dst)) return;
        DurabilityRecoveryEventCodec.WriteRecord(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), chunkType, lsn, size);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityRecoveryFpiEvent BeginDurabilityRecoveryFpi(int fpiCount)
    {
        if (!TelemetryConfig.DurabilityRecoveryFpiActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityRecoveryFpi, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityRecoveryFpiEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, FpiCount = fpiCount, RepairedCount = 0, Mismatches = 0 };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityRecoveryRedoEvent BeginDurabilityRecoveryRedo()
    {
        if (!TelemetryConfig.DurabilityRecoveryRedoActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityRecoveryRedo, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityRecoveryRedoEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityRecoveryUndoEvent BeginDurabilityRecoveryUndo(int voidedUowCount)
    {
        if (!TelemetryConfig.DurabilityRecoveryUndoActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityRecoveryUndo, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityRecoveryUndoEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, VoidedUowCount = voidedUowCount };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DurabilityRecoveryTickFenceEvent BeginDurabilityRecoveryTickFence()
    {
        if (!TelemetryConfig.DurabilityRecoveryTickFenceActive) return default;
        if (!BeginPrologue(TraceEventKind.DurabilityRecoveryTickFence, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new DurabilityRecoveryTickFenceEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }

    // ── UoW ──

    /// <summary>Emit UoW state transition instant — extreme-freq, deny-listed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityUowState(byte from, byte to, ushort uowId, byte reason)
    {
        if (!TelemetryConfig.DurabilityUowStateActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DurabilityUowState]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityUowEventCodec.StateSize, out var dst)) return;
        DurabilityUowEventCodec.WriteState(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), from, to, uowId, reason);
        ring.Publish();
    }

    /// <summary>Emit UoW deadline-check instant — extreme-freq, deny-listed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitDurabilityUowDeadline(long deadline, long remaining, byte expired)
    {
        if (!TelemetryConfig.DurabilityUowDeadlineActive) return;
        if (SuppressedKinds[(int)TraceEventKind.DurabilityUowDeadline]) return;
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0) return;
        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(DurabilityUowEventCodec.DeadlineSize, out var dst)) return;
        DurabilityUowEventCodec.WriteDeadline(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), deadline, remaining, expired);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 9 — Subscription dispatch factories (per-subscriber depth)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Begin a per-subscriber invocation span. High-freq, deny-listed by default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionSubscriberEvent BeginRuntimeSubscriptionSubscriber(uint subscriberId, ushort viewId, int deltaCount)
    {
        if (!TelemetryConfig.RuntimeSubscriptionSubscriberActive) return default;
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionSubscriber, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new RuntimeSubscriptionSubscriberEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, SubscriberId = subscriberId, ViewId = viewId, DeltaCount = deltaCount };
    }

    /// <summary>Begin a delta-builder span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionDeltaBuildEvent BeginRuntimeSubscriptionDeltaBuild(ushort viewId)
    {
        if (!TelemetryConfig.RuntimeSubscriptionDeltaBuildActive) return default;
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionDeltaBuild, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new RuntimeSubscriptionDeltaBuildEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, ViewId = viewId };
    }

    /// <summary>Begin a per-client delta-serialize span. High-freq, deny-listed by default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionDeltaSerializeEvent BeginRuntimeSubscriptionDeltaSerialize(uint clientId, ushort viewId, byte format)
    {
        if (!TelemetryConfig.RuntimeSubscriptionDeltaSerializeActive) return default;
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionDeltaSerialize, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new RuntimeSubscriptionDeltaSerializeEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, ClientId = clientId, ViewId = viewId, Format = format };
    }

    /// <summary>Begin a Subscription:Transition:BeginSync span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionTransitionBeginSyncEvent BeginRuntimeSubscriptionTransitionBeginSync(uint clientId, ushort viewId, int entitySnapshot)
    {
        if (!TelemetryConfig.RuntimeSubscriptionTransitionBeginSyncActive) return default;
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionTransitionBeginSync, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new RuntimeSubscriptionTransitionBeginSyncEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo, ClientId = clientId, ViewId = viewId, EntitySnapshot = entitySnapshot };
    }

    /// <summary>Begin a dead-client cleanup span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionOutputCleanupEvent BeginRuntimeSubscriptionOutputCleanup()
    {
        if (!TelemetryConfig.RuntimeSubscriptionOutputCleanupActive) return default;
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionOutputCleanup, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new RuntimeSubscriptionOutputCleanupEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }

    /// <summary>Begin a dirty-bitmap-supplement span (when ring overflows).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent BeginRuntimeSubscriptionDeltaDirtyBitmapSupplement()
    {
        if (!TelemetryConfig.RuntimeSubscriptionDeltaDirtyBitmapSupplementActive) return default;
        if (!BeginPrologue(TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement, out var s, out var t, out var sid, out var psid, out var prev, out var thi, out var tlo)) return default;
        return new RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent { ThreadSlot = (byte)s, StartTimestamp = t, SpanId = sid, ParentSpanId = psid, PreviousSpanId = prev, TraceIdHi = thi, TraceIdLo = tlo };
    }
}
