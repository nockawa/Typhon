using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Binary-format round-trip tests for <see cref="TraceFileCacheWriter"/> + <see cref="TraceFileCacheReader"/>. Exercises every section with
/// known content and asserts byte-for-byte recovery. Covers edge cases: empty span-name table, single-entry tables, fingerprint verify.
/// </summary>
[TestFixture]
public class TraceFileCacheRoundTripTests
{
    [Test]
    public void RoundTrip_AllSectionsRecoverIdentically()
    {
        var tickIndex = new[]
        {
            new TickIndexEntry { TickNumber = 1, ByteOffsetInSource = 1024, ByteLengthInSource = 500, EventCount = 42 },
            new TickIndexEntry { TickNumber = 2, ByteOffsetInSource = 1524, ByteLengthInSource = 800, EventCount = 55 },
            new TickIndexEntry { TickNumber = 3, ByteOffsetInSource = 2324, ByteLengthInSource = 300, EventCount = 17 },
        };
        var tickSummaries = new[]
        {
            new TickSummary { TickNumber = 1, DurationUs = 16.7f, EventCount = 42, MaxSystemDurationUs = 5.2f, ActiveSystemsBitmask = 0x07 },
            new TickSummary { TickNumber = 2, DurationUs = 22.1f, EventCount = 55, MaxSystemDurationUs = 8.9f, ActiveSystemsBitmask = 0x0F },
            new TickSummary { TickNumber = 3, DurationUs = 11.3f, EventCount = 17, MaxSystemDurationUs = 3.4f, ActiveSystemsBitmask = 0x03 },
        };
        var globalMetricsFixed = new GlobalMetricsFixed
        {
            GlobalStartUs = 0.0, GlobalEndUs = 50_000.0,
            MaxTickDurationUs = 22.1, MaxSystemDurationUs = 8.9, P95TickDurationUs = 20.0,
            TotalEvents = 114, TotalTicks = 3, SystemAggregateCount = 2,
        };
        var systemAggregates = new[]
        {
            new SystemAggregateDuration { SystemIndex = 0, InvocationCount = 3, TotalDurationUs = 15.0 },
            new SystemAggregateDuration { SystemIndex = 1, InvocationCount = 3, TotalDurationUs = 22.0 },
        };
        var spanNames = new Dictionary<int, string>
        {
            { 1, "BTreeInsert" },
            { 2, "TransactionCommit" },
            { 42, "MySpan" },
        };
        var chunkPayload1 = Encoding.UTF8.GetBytes("chunk-1-records-pretend-this-is-binary");
        var chunkPayload2 = Encoding.UTF8.GetBytes("chunk-2-records-also-binary-more-data-here-to-exercise-lz4");

        var path = Path.Combine(Path.GetTempPath(), $"trace-cache-rt-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            long chunk1Offset, chunk2Offset;
            uint chunk1CompLen, chunk2CompLen;
            uint chunk1UncompLen, chunk2UncompLen;

            // Write.
            using (var fs = File.Create(path))
            using (var writer = new TraceFileCacheWriter(fs))
            {
                writer.BeginSection(CacheSectionId.FoldedChunkData);
                (chunk1Offset, chunk1CompLen, chunk1UncompLen) = writer.AppendLz4Chunk(chunkPayload1);
                (chunk2Offset, chunk2CompLen, chunk2UncompLen) = writer.AppendLz4Chunk(chunkPayload2);

                writer.BeginSection(CacheSectionId.TickIndex);
                writer.WriteArray<TickIndexEntry>(tickIndex);

                writer.BeginSection(CacheSectionId.TickSummaries);
                writer.WriteArray<TickSummary>(tickSummaries);

                writer.BeginSection(CacheSectionId.GlobalMetrics);
                writer.WriteStruct(globalMetricsFixed);
                writer.WriteArray<SystemAggregateDuration>(systemAggregates);

                writer.BeginSection(CacheSectionId.ChunkManifest);
                var manifest = new[]
                {
                    new ChunkManifestEntry
                    {
                        FromTick = 1, ToTick = 2,
                        CacheByteOffset = chunk1Offset, CacheByteLength = chunk1CompLen,
                        EventCount = 42, UncompressedBytes = chunk1UncompLen,
                    },
                    new ChunkManifestEntry
                    {
                        FromTick = 2, ToTick = 4,
                        CacheByteOffset = chunk2Offset, CacheByteLength = chunk2CompLen,
                        EventCount = 72, UncompressedBytes = chunk2UncompLen,
                    },
                };
                writer.WriteArray<ChunkManifestEntry>(manifest);

                writer.BeginSection(CacheSectionId.SpanNameTable);
                writer.WriteSpanNameTable(spanNames);

                var header = new CacheHeader
                {
                    Flags = 0,
                    SourceVersion = 3,
                    ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
                    CreatedUtcTicks = 12345L,
                };
                // Fill fingerprint with a known pattern.
                unsafe
                {
                    for (var i = 0; i < 32; i++) header.SourceFingerprint[i] = (byte)i;
                }
                writer.Finalize(header);
            }

            // Read.
            using var rs = File.OpenRead(path);
            using var reader = new TraceFileCacheReader(rs);

            // Header basics.
            Assert.That(reader.Header.Magic, Is.EqualTo(CacheHeader.MagicValue));
            Assert.That(reader.Header.Version, Is.EqualTo(CacheHeader.CurrentVersion));
            Assert.That(reader.Header.SourceVersion, Is.EqualTo((ushort)3));
            Assert.That(reader.Header.ChunkerVersion, Is.EqualTo(TraceFileCacheConstants.CurrentChunkerVersion));
            Assert.That(reader.Header.CreatedUtcTicks, Is.EqualTo(12345L));

            // Fingerprint compare.
            var expectedFingerprint = new byte[32];
            for (var i = 0; i < 32; i++) expectedFingerprint[i] = (byte)i;
            Assert.That(reader.VerifyFingerprint(expectedFingerprint), Is.True, "Fingerprint should match what we wrote.");
            var wrongFp = new byte[32];
            Assert.That(reader.VerifyFingerprint(wrongFp), Is.False, "Zero fingerprint should not match.");

            // TickIndex.
            Assert.That(reader.TickIndex.Count, Is.EqualTo(tickIndex.Length));
            for (var i = 0; i < tickIndex.Length; i++)
            {
                Assert.That(reader.TickIndex[i].TickNumber, Is.EqualTo(tickIndex[i].TickNumber));
                Assert.That(reader.TickIndex[i].ByteOffsetInSource, Is.EqualTo(tickIndex[i].ByteOffsetInSource));
                Assert.That(reader.TickIndex[i].ByteLengthInSource, Is.EqualTo(tickIndex[i].ByteLengthInSource));
                Assert.That(reader.TickIndex[i].EventCount, Is.EqualTo(tickIndex[i].EventCount));
            }

            // TickSummaries.
            Assert.That(reader.TickSummaries.Count, Is.EqualTo(tickSummaries.Length));
            for (var i = 0; i < tickSummaries.Length; i++)
            {
                Assert.That(reader.TickSummaries[i].TickNumber, Is.EqualTo(tickSummaries[i].TickNumber));
                Assert.That(reader.TickSummaries[i].DurationUs, Is.EqualTo(tickSummaries[i].DurationUs));
                Assert.That(reader.TickSummaries[i].EventCount, Is.EqualTo(tickSummaries[i].EventCount));
                Assert.That(reader.TickSummaries[i].MaxSystemDurationUs, Is.EqualTo(tickSummaries[i].MaxSystemDurationUs));
                Assert.That(reader.TickSummaries[i].ActiveSystemsBitmask, Is.EqualTo(tickSummaries[i].ActiveSystemsBitmask));
            }

            // GlobalMetrics.
            Assert.That(reader.GlobalMetrics.GlobalStartUs, Is.EqualTo(globalMetricsFixed.GlobalStartUs));
            Assert.That(reader.GlobalMetrics.GlobalEndUs, Is.EqualTo(globalMetricsFixed.GlobalEndUs));
            Assert.That(reader.GlobalMetrics.P95TickDurationUs, Is.EqualTo(globalMetricsFixed.P95TickDurationUs));
            Assert.That(reader.GlobalMetrics.TotalEvents, Is.EqualTo(globalMetricsFixed.TotalEvents));
            Assert.That(reader.GlobalMetrics.TotalTicks, Is.EqualTo(globalMetricsFixed.TotalTicks));
            Assert.That(reader.GlobalMetrics.SystemAggregateCount, Is.EqualTo(globalMetricsFixed.SystemAggregateCount));
            Assert.That(reader.SystemAggregates.Count, Is.EqualTo(systemAggregates.Length));
            for (var i = 0; i < systemAggregates.Length; i++)
            {
                Assert.That(reader.SystemAggregates[i].SystemIndex, Is.EqualTo(systemAggregates[i].SystemIndex));
                Assert.That(reader.SystemAggregates[i].InvocationCount, Is.EqualTo(systemAggregates[i].InvocationCount));
                Assert.That(reader.SystemAggregates[i].TotalDurationUs, Is.EqualTo(systemAggregates[i].TotalDurationUs));
            }

            // ChunkManifest + decompression.
            Assert.That(reader.ChunkManifest.Count, Is.EqualTo(2));
            Assert.That(reader.ChunkManifest[0].FromTick, Is.EqualTo(1u));
            Assert.That(reader.ChunkManifest[0].ToTick, Is.EqualTo(2u));
            Assert.That(reader.ChunkManifest[1].FromTick, Is.EqualTo(2u));
            Assert.That(reader.ChunkManifest[1].ToTick, Is.EqualTo(4u));

            // Decompress both chunks and compare to original payloads.
            var scratch = new byte[Math.Max(chunk1CompLen, chunk2CompLen)];
            var out1 = new byte[chunk1UncompLen];
            reader.DecompressChunk(reader.ChunkManifest[0], out1, scratch);
            Assert.That(out1, Is.EqualTo(chunkPayload1));

            var out2 = new byte[chunk2UncompLen];
            reader.DecompressChunk(reader.ChunkManifest[1], out2, scratch);
            Assert.That(out2, Is.EqualTo(chunkPayload2));

            // SpanNames.
            Assert.That(reader.SpanNames.Count, Is.EqualTo(spanNames.Count));
            foreach (var (id, name) in spanNames)
            {
                Assert.That(reader.SpanNames.ContainsKey(id), Is.True);
                Assert.That(reader.SpanNames[id], Is.EqualTo(name));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void RoundTrip_EmptySpanNameTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trace-cache-empty-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            using (var fs = File.Create(path))
            using (var writer = new TraceFileCacheWriter(fs))
            {
                writer.BeginSection(CacheSectionId.SpanNameTable);
                writer.WriteSpanNameTable(new Dictionary<int, string>());

                var header = new CacheHeader
                {
                    SourceVersion = 3,
                    ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
                };
                writer.Finalize(header);
            }

            using var rs = File.OpenRead(path);
            using var reader = new TraceFileCacheReader(rs);
            Assert.That(reader.SpanNames.Count, Is.EqualTo(0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void Open_RejectsMismatchedChunkerVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trace-cache-badver-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            using (var fs = File.Create(path))
            using (var writer = new TraceFileCacheWriter(fs))
            {
                var header = new CacheHeader
                {
                    SourceVersion = 3,
                    // Deliberately write an unknown chunker version.
                    ChunkerVersion = 0xBEEF,
                };
                writer.Finalize(header);
            }

            using var rs = File.OpenRead(path);
            var ex = Assert.Throws<InvalidDataException>(() => new TraceFileCacheReader(rs));
            Assert.That(ex!.Message, Does.Contain("chunker version"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void Open_RejectsMismatchedMagic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trace-cache-badmagic-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            // Write a file with garbage bytes — definitely not a valid cache file.
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0xAB, 1024).ToArray());

            using var rs = File.OpenRead(path);
            var ex = Assert.Throws<InvalidDataException>(() => new TraceFileCacheReader(rs));
            Assert.That(ex!.Message, Does.Contain("magic"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void SourceFingerprint_IsStableForSameFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-stable-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, Enumerable.Range(0, 20_000).Select(i => (byte)(i % 256)).ToArray());

            var fp1 = new byte[32];
            var fp2 = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(path, fp1);
            TraceFileCacheReader.ComputeSourceFingerprint(path, fp2);
            Assert.That(fp1, Is.EqualTo(fp2), "Fingerprint must be deterministic for unchanged file.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void SourceFingerprint_ChangesWhenContentChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-change-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, Enumerable.Range(0, 20_000).Select(i => (byte)(i % 256)).ToArray());
            var fp1 = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(path, fp1);

            // Mutate the file — change a byte in the first 4 KB region so the edge-hash sees it.
            using (var fs = File.OpenWrite(path))
            {
                fs.Position = 100;
                fs.WriteByte(0xFF);
            }

            var fp2 = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(path, fp2);
            Assert.That(fp1, Is.Not.EqualTo(fp2), "Fingerprint must change when content changes.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
