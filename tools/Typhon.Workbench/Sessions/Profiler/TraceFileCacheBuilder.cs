using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions.Profiler;

/// <summary>
/// Builds a <c>.typhon-trace-cache</c> sidecar by scanning a source <c>.typhon-trace</c> file in one linear pass. Ported verbatim
/// from <c>Typhon.Profiler.Server.TraceFileCacheBuilder</c> for the Workbench profiler module — kept as a parallel copy during the
/// cross-server migration (see <c>claude/design/typhon-workbench/modules/02-profiler.md</c> §11 Phase 4).
/// </summary>
public static class TraceFileCacheBuilder
{
    private const int CommonHeaderSize = 12;
    private const int SpanHeaderExtSize = 25;
    private const int TraceContextSize = 16;

    /// <summary>Minimum wall-clock interval between progress callbacks — keeps SSE traffic manageable on fast-building traces.</summary>
    private const int ProgressIntervalMs = 200;

    /// <summary>
    /// Scan <paramref name="sourcePath"/> and write a fresh sidecar cache to <paramref name="cachePath"/>. Overwrites any existing cache at that
    /// path. Returns the high-level build result for logging / diagnostics.
    /// </summary>
    public static BuildResult Build(string sourcePath, string cachePath, IProgress<BuildProgress> progress = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source trace file not found.", sourcePath);
        }

        var fingerprint = new byte[32];
        TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);

        var started = DateTime.UtcNow;
        var lastProgressAt = started;

        using var sourceStream = File.OpenRead(sourcePath);
        var totalBytes = sourceStream.Length;
        using var reader = new TraceFileReader(sourceStream);
        Span<byte> foldDurationBuf = stackalloc byte[8];
        progress?.Report(new BuildProgress(0, totalBytes, 0, 0));

        var header = reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();

        var ticksPerUs = header.TimestampFrequency / 1_000_000.0;
        var tickSummaries = new List<TickSummary>(capacity: 4096);
        var systemAggregates = new Dictionary<int, (uint InvocationCount, double TotalDurationUs)>();
        var chunkManifest = new List<ChunkManifestEntry>(capacity: 256);

        var tickActive = false;
        var preTickBuffer = new MemoryStream(capacity: 4096);
        uint preTickEventCount = 0;
        uint currentTickNumber = 0;
        long currentTickFirstTs = 0;
        long currentTickLastTs = 0;
        uint currentEventCount = 0;
        long currentMaxSystemDurationTicks = 0;
        ulong currentActiveSystemsBitmask = 0;

        var chunkBuffer = new MemoryStream(capacity: TraceFileCacheConstants.ByteCap);
        uint chunkFromTick = 0;
        uint chunkEventCount = 0;
        uint chunkFlags = 0;
        long tickBytesInChunk = 0;
        uint tickEventsInChunk = 0;

        var openKickoffs = new Dictionary<ulong, long>();
        long foldedCount = 0;

        double globalStartUs = 0;
        double globalEndUs = 0;
        var globalStartSet = false;
        double globalMaxTickDurationUs = 0;
        double globalMaxSystemDurationUs = 0;
        long globalTotalEvents = 0;

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

                    var nextTickNumber = currentTickNumber + 1;
                    if (chunkFromTick == 0)
                    {
                        chunkFromTick = nextTickNumber;
                        if (preTickBuffer.Length > 0)
                        {
                            chunkBuffer.Write(preTickBuffer.GetBuffer(), 0, (int)preTickBuffer.Length);
                            chunkEventCount += preTickEventCount;
                            globalTotalEvents += preTickEventCount;
                            preTickBuffer.SetLength(0);
                            preTickBuffer.Position = 0;
                            preTickEventCount = 0;
                        }
                    }
                    else
                    {
                        var ticksInChunk = nextTickNumber - chunkFromTick;
                        if (ticksInChunk >= TraceFileCacheConstants.TickCap
                            || chunkBuffer.Length >= TraceFileCacheConstants.ByteCap
                            || chunkEventCount >= TraceFileCacheConstants.EventCap)
                        {
                            FlushChunk(writer, chunkBuffer, chunkManifest, chunkFromTick, nextTickNumber, chunkEventCount, chunkFlags);
                            openKickoffs.Clear();
                            chunkFromTick = nextTickNumber;
                            chunkEventCount = 0;
                            chunkFlags = 0;
                        }
                    }
                    tickBytesInChunk = 0;
                    tickEventsInChunk = 0;

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
                    if (kind == TraceEventKind.TickEnd || startTs > currentTickLastTs)
                    {
                        currentTickLastTs = startTs;
                    }

                    if (IsCompletionKind(kind) && size >= CommonHeaderSize + SpanHeaderExtSize)
                    {
                        var completionSpanId = BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 20)..]);
                        if (openKickoffs.Remove(completionSpanId, out var kickoffOffset))
                        {
                            var completionDurationTicks = BinaryPrimitives.ReadInt64LittleEndian(span[(pos + 12)..]);
                            var savedLength = chunkBuffer.Length;
                            chunkBuffer.Position = kickoffOffset + CommonHeaderSize;
                            BinaryPrimitives.WriteInt64LittleEndian(foldDurationBuf, completionDurationTicks);
                            chunkBuffer.Write(foldDurationBuf);
                            chunkBuffer.Position = savedLength;

                            foldedCount++;
                            pos += size;
                            continue;
                        }
                    }

                    if (tickBytesInChunk + size > TraceFileCacheConstants.IntraTickByteCap
                        || tickEventsInChunk >= TraceFileCacheConstants.IntraTickEventCap)
                    {
                        FlushChunk(writer, chunkBuffer, chunkManifest, chunkFromTick, currentTickNumber + 1, chunkEventCount, chunkFlags);
                        openKickoffs.Clear();
                        chunkFromTick = currentTickNumber;
                        chunkEventCount = 0;
                        chunkFlags = TraceFileCacheConstants.FlagIsContinuation;
                        tickBytesInChunk = 0;
                        tickEventsInChunk = 0;
                    }

                    currentEventCount++;
                    globalTotalEvents++;
                    if (IsKickoffKind(kind) && size >= CommonHeaderSize + SpanHeaderExtSize)
                    {
                        var kickoffSpanId = BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 20)..]);
                        openKickoffs[kickoffSpanId] = chunkBuffer.Length;
                    }
                    chunkBuffer.Write(span.Slice(pos, size));
                    chunkEventCount++;
                    tickBytesInChunk += size;
                    tickEventsInChunk++;
                }
                else if (kind == TraceEventKind.MemoryAllocEvent || kind == TraceEventKind.GcStart || kind == TraceEventKind.GcEnd
                    || kind == TraceEventKind.GcSuspension || kind == TraceEventKind.ThreadInfo)
                {
                    preTickBuffer.Write(span.Slice(pos, size));
                    preTickEventCount++;
                }

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

                pos += size;
            }
        }

        if (tickActive)
        {
            FinalizeTick(
                tickSummaries, currentTickNumber, currentTickFirstTs, currentTickLastTs,
                currentEventCount, currentMaxSystemDurationTicks, currentActiveSystemsBitmask,
                ticksPerUs, ref globalMaxTickDurationUs, ref globalMaxSystemDurationUs);
        }
        if (chunkBuffer.Length > 0)
        {
            FlushChunk(writer, chunkBuffer, chunkManifest, chunkFromTick, currentTickNumber + 1, chunkEventCount, chunkFlags);
        }

        globalEndUs = tickSummaries.Count > 0 ? currentTickLastTs / ticksPerUs : globalStartUs;

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
            const int FingerprintOffset = 8;
            var headerSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref cacheHeader, 1));
            fingerprint.AsSpan().CopyTo(headerSpan.Slice(FingerprintOffset, 32));
            writer.Finalize(cacheHeader);
        }

        progress?.Report(new BuildProgress(totalBytes, totalBytes, tickSummaries.Count, globalTotalEvents));

        return new BuildResult(
            TickCount: tickSummaries.Count,
            EventCount: globalTotalEvents,
            FoldedCount: foldedCount,
            SystemCount: systemAggregates.Count,
            Duration: DateTime.UtcNow - started,
            CacheFilePath: cachePath);
    }

    private static void FlushChunk(
        TraceFileCacheWriter writer,
        MemoryStream chunkBuffer,
        List<ChunkManifestEntry> manifest,
        uint fromTick,
        uint toTick,
        uint eventCount,
        uint flags)
    {
        if (chunkBuffer.Length == 0 || eventCount == 0)
        {
            return;
        }

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
            Flags = flags,
        });

        chunkBuffer.SetLength(0);
        chunkBuffer.Position = 0;
    }

    private static bool FinalizeTick(
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
        if (firstTs <= 0)
        {
            return false;
        }
        if (tickSummaries.Count > 0)
        {
            var prevFirstTs = (long)(tickSummaries[^1].StartUs * ticksPerUs);
            if (firstTs < prevFirstTs)
            {
                return false;
            }
        }

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
        return true;
    }

    /// <summary>High-level summary of a cache-build pass. Useful for logging and telemetry.</summary>
    public record BuildResult(int TickCount, long EventCount, long FoldedCount, int SystemCount, TimeSpan Duration, string CacheFilePath);

    /// <summary>Single progress snapshot emitted during a cache build. Emitted at tick boundaries, throttled to at most one per ~200 ms.</summary>
    public readonly record struct BuildProgress(long BytesRead, long TotalBytes, int TickCount, long EventCount);

    private static bool IsKickoffKind(TraceEventKind kind) =>
        kind == TraceEventKind.PageCacheDiskRead ||
        kind == TraceEventKind.PageCacheDiskWrite ||
        kind == TraceEventKind.PageCacheFlush;

    private static bool IsCompletionKind(TraceEventKind kind) =>
        kind == TraceEventKind.PageCacheDiskReadCompleted ||
        kind == TraceEventKind.PageCacheDiskWriteCompleted ||
        kind == TraceEventKind.PageCacheFlushCompleted;

    /// <summary>Standard sidecar-path convention: source <c>foo.typhon-trace</c> → cache <c>foo.typhon-trace-cache</c>.</summary>
    public static string GetCachePathFor(string sourcePath)
    {
        return sourcePath + TraceFileCacheConstants.CacheFileExtension;
    }
}
