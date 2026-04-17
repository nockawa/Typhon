using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Profiler.Server;

/// <summary>
/// Builds a <c>.typhon-trace-cache</c> sidecar by scanning a source <c>.typhon-trace</c> file in one linear pass. For Phase 1 the sidecar carries
/// the metadata needed to render the timeline overview without any chunk loads: per-tick summaries, global metrics, and a copy of the span-name
/// intern table.
/// </summary>
/// <remarks>
/// <para>
/// <b>What's NOT in Phase 1:</b> async-completion fold, FoldedChunkData section, tick index, chunk manifest. Those are Phase 2 — they enable
/// detail-chunk serving from the cache. Phase 1 keeps the existing <c>/api/trace/events</c> monolithic path working while adding instant overview
/// rendering; both paths coexist.
/// </para>
/// <para>
/// <b>Walk protocol:</b> open the source via <see cref="TraceFileReader"/>, read header + system/archetype/componentType tables (which seed the
/// cache's per-system aggregate slots), then drain blocks one at a time. Each block is a byte span of variable-size records; we walk by reading
/// the u16 size prefix at the head of each record. For <see cref="TraceEventKind.TickStart"/> we close the current tick's summary and open a new
/// one; for <see cref="TraceEventKind.SchedulerChunk"/> we decode the payload's <c>systemIdx</c> + <c>durationTicks</c> to drive the per-system
/// aggregate and per-tick max-system-duration metrics. Every record counts toward <c>eventCount</c> and updates <c>lastTimestamp</c>.
/// </para>
/// </remarks>
public static class TraceFileCacheBuilder
{
    /// <summary>Record common-header constants. Match the wire spec in claude/design/observability/typhon-profiler.md §4.</summary>
    private const int CommonHeaderSize = 12;
    private const int SpanHeaderExtSize = 25;
    private const int TraceContextSize = 16;

    /// <summary>Minimum wall-clock interval between progress callbacks — keeps SSE traffic manageable on fast-building traces.</summary>
    private const int ProgressIntervalMs = 200;

    /// <summary>
    /// Scan <paramref name="sourcePath"/> and write a fresh sidecar cache to <paramref name="cachePath"/>. Overwrites any existing cache at that
    /// path. Returns the high-level build result for logging / diagnostics.
    /// </summary>
    /// <param name="progress">
    /// Optional sink for progress updates. Called synchronously from the build loop after each tick close, throttled to at most one call per
    /// <see cref="ProgressIntervalMs"/>. Keep the handler fast (non-blocking) — slow handlers stall the build. Use this to drive SSE progress
    /// feeds or UI progress bars. Null = silent mode (same behavior as the legacy single-arg overload).
    /// </param>
    public static BuildResult Build(string sourcePath, string cachePath, IProgress<BuildProgress> progress = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source trace file not found.", sourcePath);
        }

        // Compute fingerprint before we open the source for read (edge-hash reads the source too — keep that read self-contained here so the
        // builder's open + walk is done in one sequential scan afterward).
        var fingerprint = new byte[32];
        TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);

        var started = DateTime.UtcNow;
        var lastProgressAt = started;

        using var sourceStream = File.OpenRead(sourcePath);
        var totalBytes = sourceStream.Length;
        using var reader = new TraceFileReader(sourceStream);
        // Scratch buffer for rewriting an async-completion's DurationTicks back into its kickoff record. Hoisted from inside the per-record
        // loop where it used to be a stackalloc — the CA2014 warning flags that as a stack-overflow hazard if the loop body grows, and
        // hoisting avoids repeated frame-pointer adjustments at no runtime cost.
        Span<byte> foldDurationBuf = stackalloc byte[8];
        // Emit an initial 0% tick so subscribers know the build has started even if the first ProgressIntervalMs hasn't elapsed yet.
        progress?.Report(new BuildProgress(0, totalBytes, 0, 0));

        var header = reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();

        var ticksPerUs = header.TimestampFrequency / 1_000_000.0;
        var tickSummaries = new List<TickSummary>(capacity: 4096);
        var systemAggregates = new Dictionary<int, (uint InvocationCount, double TotalDurationUs)>();
        var chunkManifest = new List<ChunkManifestEntry>(capacity: 256);

        // Per-tick accumulators. `tickActive` is false until the first TickStart; early instant events that precede any tick are skipped.
        var tickActive = false;
        uint currentTickNumber = 0;
        long currentTickFirstTs = 0;
        long currentTickLastTs = 0;
        uint currentEventCount = 0;
        long currentMaxSystemDurationTicks = 0;
        ulong currentActiveSystemsBitmask = 0;

        // Chunk accumulator — raw record bytes for the currently-building chunk. Flushed at tick boundaries when TICK_CAP or BYTE_CAP hit.
        // Using MemoryStream.GetBuffer() + tracked length avoids re-alloc when reused across chunks (its backing array grows to the worst-case
        // chunk size and is re-used across Write-then-Reset cycles). MemoryStream auto-grows to ByteCap + slack on demand.
        var chunkBuffer = new MemoryStream(capacity: TraceFileCacheConstants.ByteCap);
        uint chunkFromTick = 0;
        uint chunkEventCount = 0;

        // openKickoffs: maps an async span's SpanId → the offset in `chunkBuffer` where the kickoff record starts. When a matching *Completed
        // record arrives, we seek back to (kickoffOffset + CommonHeaderSize) and rewrite the kickoff's DurationTicks field with the completion's
        // full-async duration, then drop the completion record entirely. Cleared on every chunk flush — a kickoff in a previously-flushed chunk
        // cannot be folded (we'd have to decompress and rewrite the LZ4 block, not worth the complexity), so those cross-chunk pairs fall through
        // as two separate records and the client's existing fold logic still handles them.
        var openKickoffs = new Dictionary<ulong, long>();
        long foldedCount = 0;

        // Global accumulators.
        double globalStartUs = 0;
        double globalEndUs = 0;
        var globalStartSet = false;
        double globalMaxTickDurationUs = 0;
        double globalMaxSystemDurationUs = 0;
        long globalTotalEvents = 0;

        // Open the writer up front so we can stream chunks into the FoldedChunkData section as they're formed. Other sections (TickSummaries,
        // GlobalMetrics, ChunkManifest, SpanNameTable) are small and written at the end.
        using var cacheStream = File.Create(cachePath);
        using var writer = new TraceFileCacheWriter(cacheStream);
        writer.BeginSection(CacheSectionId.FoldedChunkData);

        while (reader.ReadNextBlock(out var recordBytes, out _))
        {
            var span = recordBytes.Span;
            var pos = 0;
            while (pos + CommonHeaderSize <= span.Length)
            {
                var size = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
                if (size < CommonHeaderSize || pos + size > span.Length)
                {
                    // Malformed — bail on this block. Don't throw; partial scans of truncated traces should still produce usable summaries.
                    break;
                }

                var kind = (TraceEventKind)span[pos + 2];
                var startTs = BinaryPrimitives.ReadInt64LittleEndian(span[(pos + 4)..]);

                if (kind == TraceEventKind.TickStart)
                {
                    if (tickActive)
                    {
                        FinalizeTick(
                            tickSummaries, currentTickNumber, currentTickFirstTs, currentTickLastTs,
                            currentEventCount, currentMaxSystemDurationTicks, currentActiveSystemsBitmask,
                            ticksPerUs, ref globalMaxTickDurationUs, ref globalMaxSystemDurationUs);

                        // Throttled progress report. Fires at tick boundaries (natural checkpoints) no more than once per ProgressIntervalMs so
                        // subscribers get regular updates without flooding the channel. `sourceStream.Position` = bytes consumed from the source,
                        // which is the best proxy for "work done" since record sizes vary — ticks and events give user-meaningful counters on top.
                        if (progress != null)
                        {
                            var now = DateTime.UtcNow;
                            if ((now - lastProgressAt).TotalMilliseconds >= ProgressIntervalMs)
                            {
                                progress.Report(new BuildProgress(sourceStream.Position, totalBytes, tickSummaries.Count, globalTotalEvents));
                                lastProgressAt = now;
                            }
                        }
                    }

                    // Chunk-close check happens BEFORE writing the new TickStart record into the chunk buffer: if the current chunk has reached
                    // its tick or byte cap, flush it and start a new chunk. The new TickStart record will become the first record of the new
                    // chunk. `chunkFromTick == 0` at the very start means "no chunk open yet" — initialize on first TickStart.
                    var nextTickNumber = currentTickNumber + 1;
                    if (chunkFromTick == 0)
                    {
                        chunkFromTick = nextTickNumber;
                    }
                    else
                    {
                        var ticksInChunk = nextTickNumber - chunkFromTick;
                        if (ticksInChunk >= TraceFileCacheConstants.TickCap || chunkBuffer.Length >= TraceFileCacheConstants.ByteCap)
                        {
                            FlushChunk(writer, chunkBuffer, chunkManifest, chunkFromTick, nextTickNumber, chunkEventCount);
                            // Clear open kickoffs: their byte offsets pointed into the chunk that's now LZ4-compressed on disk, so they can't be
                            // retroactively rewritten. Any completion records that arrive after this point will fall through to the normal write
                            // path and the client's existing fold logic will handle them as cross-chunk pairs.
                            openKickoffs.Clear();
                            chunkFromTick = nextTickNumber;
                            chunkEventCount = 0;
                        }
                    }

                    currentTickNumber = nextTickNumber;
                    currentTickFirstTs = startTs;
                    currentTickLastTs = startTs;
                    currentEventCount = 0;
                    currentMaxSystemDurationTicks = 0;
                    currentActiveSystemsBitmask = 0;
                    tickActive = true;

                    if (!globalStartSet)
                    {
                        globalStartUs = startTs / ticksPerUs;
                        globalStartSet = true;
                    }
                }

                if (tickActive)
                {
                    // TickEnd is the authoritative end marker: assign unconditionally so parallel-worker drains with occasional clock-skew'd
                    // timestamps (a late worker's last span arriving *after* TickEnd with a higher ts) don't leave the tick's last-ts pinned
                    // below the real end. For other kinds we advance only when the ts moves forward — prevents out-of-order early events from
                    // clobbering a later authoritative timestamp.
                    if (kind == TraceEventKind.TickEnd || startTs > currentTickLastTs)
                    {
                        currentTickLastTs = startTs;
                    }

                    // Fold path: if this is a *Completed record AND the matching kickoff is still in the current chunk buffer, rewrite the
                    // kickoff's DurationTicks with the completion's (which is the full async tail per the wire spec) and DROP the completion.
                    // Cross-chunk pairs fall through — their kickoff is in a previously-flushed (LZ4-compressed) chunk, so we can't modify it.
                    if (IsCompletionKind(kind) && size >= CommonHeaderSize + SpanHeaderExtSize)
                    {
                        var completionSpanId = BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 20)..]);
                        if (openKickoffs.Remove(completionSpanId, out var kickoffOffset))
                        {
                            var completionDurationTicks = BinaryPrimitives.ReadInt64LittleEndian(span[(pos + 12)..]);
                            var savedLength = chunkBuffer.Length;
                            chunkBuffer.Position = kickoffOffset + CommonHeaderSize;  // DurationTicks field
                            BinaryPrimitives.WriteInt64LittleEndian(foldDurationBuf, completionDurationTicks);
                            chunkBuffer.Write(foldDurationBuf);
                            // Restore position to EOF so subsequent record writes append at the end rather than overwriting.
                            chunkBuffer.Position = savedLength;

                            // DON'T extend currentTickLastTs by the folded (async-tail) end. A tick's duration in the summary is the wall-clock
                            // window between its TickStart and TickEnd records — NOT "when did async work triggered in this tick finally
                            // complete". Extending lastTs here bloats tick N's reported duration past its real endUs and into tick N+1's
                            // startUs, which then causes the viewer's selection math to treat adjacent ticks as overlapping. The kickoff's
                            // DurationTicks IS updated on the record itself (rewritten above), so the async tail is correctly preserved at the
                            // span level for GraphArea to render — just not pulled into the tick-summary rollup.

                            foldedCount++;
                            pos += size;
                            continue;  // skip normal record-write below
                        }
                        // Kickoff not in cache (cross-chunk or never seen) — fall through to the normal write path.
                    }

                    currentEventCount++;
                    globalTotalEvents++;
                    // Remember the offset where a kickoff record starts, so a later completion in the same chunk can rewrite its duration field.
                    // Must be captured BEFORE the Write (which advances the position).
                    if (IsKickoffKind(kind) && size >= CommonHeaderSize + SpanHeaderExtSize)
                    {
                        var kickoffSpanId = BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 20)..]);
                        openKickoffs[kickoffSpanId] = chunkBuffer.Length;
                    }
                    chunkBuffer.Write(span.Slice(pos, size));
                    chunkEventCount++;
                }

                // Decode SchedulerChunk for per-system metrics. These are the only records needed for the overview — other span kinds (Transaction,
                // B+Tree, PageCache, etc.) are scanned only for event counting + tick-last-timestamp, not decoded.
                if (kind == TraceEventKind.SchedulerChunk && size >= CommonHeaderSize + SpanHeaderExtSize + 2)
                {
                    var durationTicks = BinaryPrimitives.ReadInt64LittleEndian(span[(pos + 12)..]);
                    var spanFlags = span[pos + 36];
                    var hasTraceContext = (spanFlags & 0x01) != 0;
                    var payloadOffset = pos + CommonHeaderSize + SpanHeaderExtSize + (hasTraceContext ? TraceContextSize : 0);
                    if (payloadOffset + 2 <= pos + size)
                    {
                        var systemIdx = BinaryPrimitives.ReadUInt16LittleEndian(span[payloadOffset..]);

                        if (durationTicks > currentMaxSystemDurationTicks)
                        {
                            currentMaxSystemDurationTicks = durationTicks;
                        }
                        if (systemIdx < 64)
                        {
                            currentActiveSystemsBitmask |= 1UL << systemIdx;
                        }

                        var durationUs = durationTicks / ticksPerUs;
                        if (!systemAggregates.TryGetValue(systemIdx, out var agg))
                        {
                            agg = (0u, 0.0);
                        }
                        systemAggregates[systemIdx] = (agg.InvocationCount + 1, agg.TotalDurationUs + durationUs);
                    }
                }

                // Tick duration = wall-clock span from TickStart to TickEnd. We rely on the general record-loop above (every record's startTs
                // updates currentTickLastTs) to pick this up — TickEnd is always the last record emitted in a tick by the scheduler, and its
                // startTs is the true tick-end timestamp. Previously we ALSO extended currentTickLastTs by span.startTs + span.durationTicks
                // to catch edge cases where a span outlived TickEnd, but that interacts badly with server-side async fold: a PageCache kickoff
                // whose DurationTicks was rewritten to the full async tail would extend past the tick's natural boundary, bloating the summary
                // and making adjacent ticks' reported ranges overlap (tick N's endUs > tick N+1's startUs). That in turn made the viewer's
                // selection math pull neighbours into the visible set. Dropping the span-endTs extension restores correct tick boundaries and
                // means a tick's summary duration is exactly its TickStart → TickEnd wall time, with folded async tails correctly represented
                // on the kickoff record (span level, not summary level).

                pos += size;
            }
        }

        // Finalize the last tick + flush the final chunk (if any pending records).
        if (tickActive)
        {
            FinalizeTick(
                tickSummaries, currentTickNumber, currentTickFirstTs, currentTickLastTs,
                currentEventCount, currentMaxSystemDurationTicks, currentActiveSystemsBitmask,
                ticksPerUs, ref globalMaxTickDurationUs, ref globalMaxSystemDurationUs);
        }
        if (chunkBuffer.Length > 0)
        {
            FlushChunk(writer, chunkBuffer, chunkManifest, chunkFromTick, currentTickNumber + 1, chunkEventCount);
        }

        // tickSummaries[^1].TickNumber can never be 0 once populated (first tick is always 1, since TickStart bumps currentTickNumber to 1
        // before FinalizeTick records anything). The old ternary carried a dead branch — collapse to the straightforward form.
        globalEndUs = tickSummaries.Count > 0 ? currentTickLastTs / ticksPerUs : globalStartUs;

        // Compute p95 tick duration. Direct loop + Array.Sort; LINQ's Select().ToArray() would allocate a delegate + iterator on top of the
        // final array for no benefit at 500K entries.
        double p95 = 0;
        if (tickSummaries.Count > 0)
        {
            var durations = new double[tickSummaries.Count];
            for (var i = 0; i < tickSummaries.Count; i++)
            {
                durations[i] = tickSummaries[i].DurationUs;
            }
            Array.Sort(durations);
            var p95Idx = (int)(durations.Length * 0.95);
            p95 = durations[Math.Min(p95Idx, durations.Length - 1)];
        }

        // Write remaining sections: TickSummaries, GlobalMetrics, ChunkManifest, SpanNameTable. Then finalize the header.
        {
            writer.BeginSection(CacheSectionId.TickSummaries);
            writer.WriteArray<TickSummary>(tickSummaries.ToArray());

            writer.BeginSection(CacheSectionId.GlobalMetrics);
            var metrics = new GlobalMetricsFixed
            {
                GlobalStartUs = globalStartUs,
                GlobalEndUs = globalEndUs,
                MaxTickDurationUs = globalMaxTickDurationUs,
                MaxSystemDurationUs = globalMaxSystemDurationUs,
                P95TickDurationUs = p95,
                TotalEvents = globalTotalEvents,
                TotalTicks = (uint)tickSummaries.Count,
                SystemAggregateCount = (uint)systemAggregates.Count,
            };
            writer.WriteStruct(metrics);
            // System aggregates — extract to an array, sort in place by system index, then project to the on-disk struct. LINQ OrderBy+Select
            // chain allocated 2 delegates + 2 iterators for ~100 systems; the direct loop is clearer and avoids the allocations.
            var aggArray = new SystemAggregateDuration[systemAggregates.Count];
            var aggIdx = 0;
            foreach (var kv in systemAggregates)
            {
                aggArray[aggIdx++] = new SystemAggregateDuration
                {
                    SystemIndex = (ushort)kv.Key,
                    Padding = 0,
                    InvocationCount = kv.Value.InvocationCount,
                    TotalDurationUs = kv.Value.TotalDurationUs,
                };
            }
            Array.Sort(aggArray, static (a, b) => a.SystemIndex.CompareTo(b.SystemIndex));
            writer.WriteArray<SystemAggregateDuration>(aggArray);

            writer.BeginSection(CacheSectionId.ChunkManifest);
            writer.WriteArray<ChunkManifestEntry>(chunkManifest.ToArray());

            writer.BeginSection(CacheSectionId.SpanNameTable);
            writer.WriteSpanNameTable(reader.SpanNames);

            var cacheHeader = new CacheHeader
            {
                Flags = 0,
                SourceVersion = header.Version,
                ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            };
            // Copy fingerprint into the CacheHeader.SourceFingerprint fixed buffer without requiring /unsafe in the server csproj. The buffer
            // lives at offset 8 (after Magic u32 + Version u16 + Flags u16) per the struct layout in TraceFileCache.cs.
            const int FingerprintOffset = 8;
            var headerSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref cacheHeader, 1));
            fingerprint.AsSpan().CopyTo(headerSpan.Slice(FingerprintOffset, 32));
            writer.Finalize(cacheHeader);
        }

        // Final 100% tick so subscribers see completion state before the task signals done.
        progress?.Report(new BuildProgress(totalBytes, totalBytes, tickSummaries.Count, globalTotalEvents));

        return new BuildResult(
            TickCount: tickSummaries.Count,
            EventCount: globalTotalEvents,
            FoldedCount: foldedCount,
            SystemCount: systemAggregates.Count,
            Duration: DateTime.UtcNow - started,
            CacheFilePath: cachePath);
    }

    /// <summary>
    /// Flush <paramref name="chunkBuffer"/>'s accumulated raw record bytes to the writer's FoldedChunkData section (LZ4-compressed), emit a
    /// matching <see cref="ChunkManifestEntry"/>, and reset the buffer for the next chunk. <paramref name="toTick"/> is exclusive: the chunk
    /// covers ticks [fromTick, toTick).
    /// </summary>
    private static void FlushChunk(
        TraceFileCacheWriter writer,
        MemoryStream chunkBuffer,
        List<ChunkManifestEntry> manifest,
        uint fromTick,
        uint toTick,
        uint eventCount)
    {
        if (chunkBuffer.Length == 0)
        {
            return;
        }

        // GetBuffer returns the backing array without copying; slice to the actual written length.
        var payload = chunkBuffer.GetBuffer().AsSpan(0, (int)chunkBuffer.Length);
        var (cacheOffset, compressedLength, uncompressedLength) = writer.AppendLz4Chunk(payload);

        manifest.Add(new ChunkManifestEntry
        {
            FromTick = fromTick,
            ToTick = toTick,
            CacheByteOffset = cacheOffset,
            CacheByteLength = compressedLength,
            EventCount = eventCount,
            UncompressedBytes = uncompressedLength,
            Padding = 0,
        });

        // Reset the buffer for reuse — SetLength(0) keeps the backing array, only resets the position counter. No heap thrash between chunks.
        chunkBuffer.SetLength(0);
        chunkBuffer.Position = 0;
    }

    /// <summary>Closes out the current tick by appending a <see cref="TickSummary"/> and updating global maxima.</summary>
    private static void FinalizeTick(
        List<TickSummary> tickSummaries,
        uint tickNumber,
        long firstTs,
        long lastTs,
        uint eventCount,
        long maxSystemDurationTicks,
        ulong activeSystemsBitmask,
        double ticksPerUs,
        ref double globalMaxTickDurationUs,
        ref double globalMaxSystemDurationUs)
    {
        var durationUs = (lastTs - firstTs) / ticksPerUs;
        if (durationUs < 0) durationUs = 0;
        var maxSysUs = maxSystemDurationTicks / ticksPerUs;
        var startUs = firstTs / ticksPerUs;

        tickSummaries.Add(new TickSummary
        {
            TickNumber = tickNumber,
            DurationUs = (float)durationUs,
            EventCount = eventCount,
            MaxSystemDurationUs = (float)maxSysUs,
            ActiveSystemsBitmask = activeSystemsBitmask,
            StartUs = startUs,
        });

        if (durationUs > globalMaxTickDurationUs)
        {
            globalMaxTickDurationUs = durationUs;
        }
        if (maxSysUs > globalMaxSystemDurationUs)
        {
            globalMaxSystemDurationUs = maxSysUs;
        }
    }

    /// <summary>High-level summary of a cache-build pass. Useful for logging and telemetry.</summary>
    public record BuildResult(int TickCount, long EventCount, long FoldedCount, int SystemCount, TimeSpan Duration, string CacheFilePath);

    /// <summary>
    /// A single progress snapshot emitted during a cache build. <see cref="BytesRead"/> / <see cref="TotalBytes"/> is the most accurate
    /// completion metric (records vary in size); <see cref="TickCount"/> and <see cref="EventCount"/> are human-readable counters for UI.
    /// Emitted at tick boundaries, throttled to at most one per ~200 ms.
    /// </summary>
    public readonly record struct BuildProgress(long BytesRead, long TotalBytes, int TickCount, long EventCount);

    /// <summary>
    /// True for the three PageCache kickoff kinds whose matching *Completed record can later fold into them. Keep in sync with
    /// <see cref="IsCompletionKind"/>.
    /// </summary>
    private static bool IsKickoffKind(TraceEventKind kind) =>
        kind == TraceEventKind.PageCacheDiskRead ||
        kind == TraceEventKind.PageCacheDiskWrite ||
        kind == TraceEventKind.PageCacheFlush;

    /// <summary>
    /// True for the three PageCache *Completed kinds that carry the same SpanId as their kickoff and whose DurationTicks represents the
    /// full async tail (completionTimestamp - beginTimestamp per the wire spec).
    /// </summary>
    private static bool IsCompletionKind(TraceEventKind kind) =>
        kind == TraceEventKind.PageCacheDiskReadCompleted ||
        kind == TraceEventKind.PageCacheDiskWriteCompleted ||
        kind == TraceEventKind.PageCacheFlushCompleted;

    /// <summary>
    /// Standard sidecar-path convention: source <c>foo.typhon-trace</c> → cache <c>foo.typhon-trace-cache</c>. Allows the cache to live next to the
    /// source file without collision; users can delete either independently.
    /// </summary>
    public static string GetCachePathFor(string sourcePath)
    {
        return sourcePath + TraceFileCacheConstants.CacheFileExtension;
    }
}
