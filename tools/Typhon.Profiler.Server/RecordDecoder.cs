using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Engine.Profiler;

namespace Typhon.Profiler.Server;

/// <summary>
/// Walks a raw record block (as produced by <c>TraceRecordRing.Drain</c> and decompressed from a file or TCP block frame) and converts each
/// size-prefixed record into a <see cref="LiveTraceEvent"/> DTO ready for JSON serialization.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateful tick-number derivation:</b> the decoder holds a single <c>_currentTick</c> counter that advances on every
/// <see cref="TraceEventKind.TickStart"/> record. Every subsequent record is tagged with the latest tick value. The first <c>TickStart</c> becomes
/// tick <c>1</c> — the server has no way to recover the real scheduler tick index because the wire format doesn't carry it on the record itself.
/// Mid-session reconnects therefore display tick numbers that differ from what the engine sees; for a fresh trace-from-start this is accurate.
/// </para>
/// <para>
/// <b>Supported kinds:</b> all instant and span kinds defined in <see cref="TraceEventKind"/>. Each span kind is decoded via its matching codec
/// (SchedulerChunkEventCodec, BTreeEventCodec, TransactionEventCodec, EcsSpawn/Destroy/Query/ViewRefreshEventCodec, PageCacheEventCodec,
/// ClusterMigrationEventCodec). <see cref="TraceEventKind.NamedSpan"/> is currently surfaced as a generic span with no name decoding.
/// </para>
/// </remarks>
public sealed class RecordDecoder
{
    private readonly double _ticksPerUs;
    private int _currentTick;

    public RecordDecoder(long timestampFrequency)
    {
        if (timestampFrequency <= 0)
        {
            throw new ArgumentException("Timestamp frequency must be positive", nameof(timestampFrequency));
        }
        _ticksPerUs = timestampFrequency / 1_000_000.0;
    }

    /// <summary>Current tick number (last TickStart seen). Exposed for diagnostics.</summary>
    public int CurrentTick => _currentTick;

    /// <summary>Reset the tick counter. Call when a new TCP session starts or when loading a fresh file.</summary>
    public void Reset() => _currentTick = 0;

    /// <summary>
    /// Seed the tick counter before decoding a chunk that doesn't start from tick 1. Set to <c>(fromTick - 1)</c> so that the first
    /// <see cref="TraceEventKind.TickStart"/> record in the chunk increments the counter to <c>fromTick</c> and subsequent events get the
    /// correct tick numbers. Used by the chunk-serving endpoint when decoding individual chunks from the sidecar cache.
    /// </summary>
    public void SetCurrentTick(int value) => _currentTick = value;

    /// <summary>
    /// Walks <paramref name="recordBytes"/> as a sequence of size-prefixed records and appends one DTO per record to <paramref name="output"/>.
    /// Malformed records (implausible size, unknown kind) stop the walk early — partial results are still useful to the client.
    /// </summary>
    public void DecodeBlock(ReadOnlySpan<byte> recordBytes, List<LiveTraceEvent> output)
    {
        // Snapshot the tick counter + output length at entry. If a malformed record breaks the walk mid-block, we roll both back so the caller
        // sees "no records decoded from this block" rather than partial records tagged with a half-advanced tick number. The live-session path
        // reuses the same decoder across blocks — without this rollback, a single corrupt block would mis-number every subsequent event.
        var savedTick = _currentTick;
        var savedOutputCount = output.Count;
        var pos = 0;

        while (pos + TraceRecordHeader.CommonHeaderSize <= recordBytes.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(recordBytes[pos..]);
            if (size < TraceRecordHeader.CommonHeaderSize || pos + size > recordBytes.Length)
            {
                // Malformed — roll back tick counter + drop any DTOs produced from this block so corruption doesn't propagate to later blocks.
                _currentTick = savedTick;
                if (output.Count > savedOutputCount)
                {
                    output.RemoveRange(savedOutputCount, output.Count - savedOutputCount);
                }
                return;
            }

            var record = recordBytes.Slice(pos, size);
            var kind = (TraceEventKind)record[2];

            if (kind == TraceEventKind.TickStart)
            {
                _currentTick++;
            }

            var dto = DecodeRecord(kind, record);
            if (dto != null)
            {
                output.Add(dto);
            }

            pos += size;
        }
    }

    private LiveTraceEvent DecodeRecord(TraceEventKind kind, ReadOnlySpan<byte> record)
    {
        if (!kind.IsSpan())
        {
            return DecodeInstant(kind, record);
        }

        return kind switch
        {
            TraceEventKind.SchedulerChunk => DecodeSchedulerChunk(record),

            TraceEventKind.BTreeInsert or TraceEventKind.BTreeDelete
                or TraceEventKind.BTreeNodeSplit or TraceEventKind.BTreeNodeMerge => DecodeBTree(record),

            TraceEventKind.TransactionCommit or TraceEventKind.TransactionRollback
                or TraceEventKind.TransactionCommitComponent => DecodeTransaction(record),

            TraceEventKind.TransactionPersist => DecodeTransactionPersist(record),

            TraceEventKind.EcsSpawn => DecodeEcsSpawn(record),
            TraceEventKind.EcsDestroy => DecodeEcsDestroy(record),

            TraceEventKind.EcsQueryExecute or TraceEventKind.EcsQueryCount
                or TraceEventKind.EcsQueryAny => DecodeEcsQuery(record),

            TraceEventKind.EcsViewRefresh => DecodeEcsViewRefresh(record),

            TraceEventKind.PageCacheFetch or TraceEventKind.PageCacheDiskRead
                or TraceEventKind.PageCacheDiskWrite or TraceEventKind.PageCacheAllocatePage
                or TraceEventKind.PageCacheFlush or TraceEventKind.PageEvicted
                or TraceEventKind.PageCacheDiskReadCompleted or TraceEventKind.PageCacheDiskWriteCompleted
                or TraceEventKind.PageCacheFlushCompleted => DecodePageCache(record),

            TraceEventKind.PageCacheBackpressure => DecodePageCacheBackpressure(record),

            TraceEventKind.ClusterMigration => DecodeClusterMigration(record),

            TraceEventKind.WalFlush or TraceEventKind.WalSegmentRotate
                or TraceEventKind.WalWait => DecodeWal(record),

            TraceEventKind.CheckpointCycle or TraceEventKind.CheckpointCollect
                or TraceEventKind.CheckpointWrite or TraceEventKind.CheckpointFsync
                or TraceEventKind.CheckpointTransition or TraceEventKind.CheckpointRecycle => DecodeCheckpoint(record),

            TraceEventKind.StatisticsRebuild => DecodeStatisticsRebuild(record),

            // NamedSpan and any future kinds fall through to generic span-header decoding (no typed payload).
            _ => DecodeGenericSpan(kind, record),
        };
    }

    private LiveTraceEvent DecodeInstant(TraceEventKind kind, ReadOnlySpan<byte> record)
    {
        var data = InstantEventCodec.Decode(record);
        var timestampUs = data.Timestamp / _ticksPerUs;

        return kind switch
        {
            TraceEventKind.TickStart => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
            },
            TraceEventKind.TickEnd => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                OverloadLevel = data.P1,
                TickMultiplier = data.P2,
            },
            TraceEventKind.PhaseStart or TraceEventKind.PhaseEnd => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                Phase = data.P1,
            },
            TraceEventKind.SystemReady => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                SystemIndex = data.P1,
            },
            TraceEventKind.SystemSkipped => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                SystemIndex = data.P1,
                SkipReason = data.P2,
            },
            _ => null,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Span decoders — one per kind, each delegating to its matching codec
    // ═══════════════════════════════════════════════════════════════════════

    private LiveTraceEvent DecodeSchedulerChunk(ReadOnlySpan<byte> record)
    {
        var data = SchedulerChunkEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerChunk,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            SystemIndex = data.SystemIndex,
            ChunkIndex = data.ChunkIndex,
            TotalChunks = data.TotalChunks,
            EntitiesProcessed = data.EntitiesProcessed,
        };
    }

    private LiveTraceEvent DecodeBTree(ReadOnlySpan<byte> record)
    {
        var data = BTreeEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
        };
    }

    private LiveTraceEvent DecodeTransaction(ReadOnlySpan<byte> record)
    {
        var data = TransactionEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            ComponentTypeId = data.Kind == TraceEventKind.TransactionCommitComponent ? data.ComponentTypeId : null,
            ComponentCount = data.HasComponentCount ? data.ComponentCount : null,
            ConflictDetected = data.HasConflictDetected ? data.ConflictDetected : null,
        };
    }

    private LiveTraceEvent DecodeEcsSpawn(ReadOnlySpan<byte> record)
    {
        var data = EcsSpawnEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.EcsSpawn,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeId,
            EntityId = data.HasEntityId ? Id(data.EntityId) : null,
            Tsn = data.HasTsn ? SignedId(data.Tsn) : null,
        };
    }

    private LiveTraceEvent DecodeEcsDestroy(ReadOnlySpan<byte> record)
    {
        var data = EcsDestroyEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.EcsDestroy,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            EntityId = Id(data.EntityId),
            CascadeCount = data.HasCascadeCount ? data.CascadeCount : null,
            Tsn = data.HasTsn ? SignedId(data.Tsn) : null,
        };
    }

    private LiveTraceEvent DecodeEcsQuery(ReadOnlySpan<byte> record)
    {
        var data = EcsQueryEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeTypeId,
            ResultCount = data.HasResultCount ? data.ResultCount : null,
            ScanMode = data.HasScanMode ? (int)data.ScanMode : null,
            Found = data.HasFound ? data.Found : null,
        };
    }

    private LiveTraceEvent DecodeEcsViewRefresh(ReadOnlySpan<byte> record)
    {
        var data = EcsViewRefreshEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.EcsViewRefresh,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeTypeId,
            Mode = data.HasMode ? (int)data.Mode : null,
            ResultCount = data.HasResultCount ? data.ResultCount : null,
            DeltaCount = data.HasDeltaCount ? data.DeltaCount : null,
        };
    }

    private LiveTraceEvent DecodePageCache(ReadOnlySpan<byte> record)
    {
        var data = PageCacheEventCodec.Decode(record);

        // Flush (kickoff AND async-completion variants) writes its PageCount in the primary "filePageIndex" slot; the other page-cache kinds
        // keep FilePageIndex there.
        var isFlush = data.Kind == TraceEventKind.PageCacheFlush || data.Kind == TraceEventKind.PageCacheFlushCompleted;
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            FilePageIndex = isFlush ? null : data.FilePageIndex,
            PageCount = isFlush ? data.FilePageIndex : (data.HasPageCount ? data.PageCount : null),
        };
    }

    private LiveTraceEvent DecodeClusterMigration(ReadOnlySpan<byte> record)
    {
        var data = ClusterMigrationEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.ClusterMigration,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeId,
            MigrationCount = data.MigrationCount,
        };
    }

    private LiveTraceEvent DecodeGenericSpan(TraceEventKind kind, ReadOnlySpan<byte> record)
    {
        TraceRecordHeader.ReadCommonHeader(record, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(record[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        ulong traceIdHi = 0, traceIdLo = 0;
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(record[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        return new LiveTraceEvent
        {
            Kind = (int)kind,
            ThreadSlot = threadSlot,
            TickNumber = _currentTick,
            TimestampUs = startTimestamp / _ticksPerUs,
            DurationUs = durationTicks / _ticksPerUs,
            SpanId = Id(spanId),
            ParentSpanId = Id(parentSpanId),
            TraceIdHi = hasTraceContext ? Id(traceIdHi) : null,
            TraceIdLo = hasTraceContext ? Id(traceIdLo) : null,
        };
    }

    private LiveTraceEvent DecodeTransactionPersist(ReadOnlySpan<byte> record)
    {
        var data = TransactionEventCodec.DecodePersist(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.TransactionPersist,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            WalLsn = data.HasWalLsn ? SignedId(data.WalLsn) : null,
        };
    }

    private LiveTraceEvent DecodePageCacheBackpressure(ReadOnlySpan<byte> record)
    {
        var data = PageCacheBackpressureCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.PageCacheBackpressure,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            RetryCount = data.RetryCount,
            DirtyCount = data.DirtyCount,
            EpochCount = data.EpochCount,
        };
    }

    private LiveTraceEvent DecodeWal(ReadOnlySpan<byte> record)
    {
        var data = WalEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            BatchByteCount = data.Kind == TraceEventKind.WalFlush ? data.BatchByteCount : null,
            FrameCount = data.Kind == TraceEventKind.WalFlush ? data.FrameCount : null,
            HighLsn = data.Kind == TraceEventKind.WalFlush ? SignedId(data.HighLsn) : null,
            NewSegmentIndex = data.Kind == TraceEventKind.WalSegmentRotate ? data.NewSegmentIndex : null,
            TargetLsn = data.Kind == TraceEventKind.WalWait ? SignedId(data.TargetLsn) : null,
        };
    }

    private LiveTraceEvent DecodeCheckpoint(ReadOnlySpan<byte> record)
    {
        var data = CheckpointEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            TargetLsn = data.Kind == TraceEventKind.CheckpointCycle ? SignedId(data.TargetLsn) : null,
            Reason = data.Kind == TraceEventKind.CheckpointCycle ? (int)data.Reason : null,
            DirtyPageCount = data.HasDirtyPageCount ? data.DirtyPageCount : null,
            WrittenCount = data.HasWrittenCount ? data.WrittenCount : null,
            TransitionedCount = data.HasTransitionedCount ? data.TransitionedCount : null,
            RecycledCount = data.HasRecycledCount ? data.RecycledCount : null,
        };
    }

    private LiveTraceEvent DecodeStatisticsRebuild(ReadOnlySpan<byte> record)
    {
        var data = StatisticsRebuildEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.StatisticsRebuild,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            EntityCount = data.EntityCount,
            MutationCount = data.MutationCount,
            SamplingInterval = data.SamplingInterval,
        };
    }

    // ID fields (SpanId, ParentSpanId, TraceIdHi/Lo, EntityId) are emitted as **decimal** strings rather than 16-char zero-padded hex.
    // Rationale: hex-encoded 64-bit IDs are 18-22 chars each (counting quotes), decimal is typically 1-20 chars but averages ~10-12 chars on
    // realistic data — roughly a 30% size reduction on the dominant ID-string fields. Strings (rather than JSON numbers) preserve full 64-bit
    // precision for the client since JS numbers top out at 2^53. Zero IDs become the single char "0" — the client's depth walk already treats
    // both "0" and the legacy "0000000000000000" as "no parent", so this is a one-sided change with no client update required. Kept as strings
    // (not JSON numbers) to avoid rewriting the client type surface from `string` to `string | number` everywhere.
    private static string Id(ulong value) => value.ToString();
    private static string SignedId(long value) => value.ToString();
}
