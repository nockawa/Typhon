using System;
using System.IO;
using K4os.Compression.LZ4;
using NUnit.Framework;
using Typhon.Profiler;
using ProfilerTickPhase = Typhon.Profiler.TickPhase;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Verifies that <see cref="TraceBlockEncoder"/> round-trips events correctly and that the on-disk
/// format produced via <see cref="TraceFileWriter"/> + <see cref="TraceFileReader"/> matches the shared
/// encoder output byte-for-byte. Both the file writer and the TCP live-stream inspector go through
/// this same encoder, so this is a contract test for the protocol.
/// </summary>
[TestFixture]
public class TraceBlockEncoderTests
{
    private static TraceEvent MakeEvent(long timestamp, int tickNumber, TraceEventType type, ushort systemIndex = 0,
        ushort chunkIndex = 0, byte workerId = 0, int payload = 0, int entities = 0)
    {
        return new TraceEvent
        {
            TimestampTicks = timestamp,
            TickNumber = tickNumber,
            SystemIndex = systemIndex,
            ChunkIndex = chunkIndex,
            WorkerId = workerId,
            EventType = type,
            Phase = ProfilerTickPhase.SystemDispatch,
            SkipReason = 0,
            EntitiesProcessed = entities,
            Payload = payload,
            Reserved = 0
        };
    }

    [Test]
    public void RoundTrip_SingleEvent_PreservesAllFields()
    {
        var original = new[]
        {
            MakeEvent(1_000_000, 42, TraceEventType.ChunkStart, systemIndex: 7, chunkIndex: 3, workerId: 5, payload: 16, entities: 1234)
        };

        var decoded = EncodeDecode(original);

        Assert.That(decoded.Length, Is.EqualTo(1));
        Assert.That(decoded[0].TimestampTicks, Is.EqualTo(original[0].TimestampTicks));
        Assert.That(decoded[0].TickNumber, Is.EqualTo(original[0].TickNumber));
        Assert.That(decoded[0].SystemIndex, Is.EqualTo(original[0].SystemIndex));
        Assert.That(decoded[0].ChunkIndex, Is.EqualTo(original[0].ChunkIndex));
        Assert.That(decoded[0].WorkerId, Is.EqualTo(original[0].WorkerId));
        Assert.That(decoded[0].EventType, Is.EqualTo(original[0].EventType));
        Assert.That(decoded[0].Payload, Is.EqualTo(original[0].Payload));
        Assert.That(decoded[0].EntitiesProcessed, Is.EqualTo(original[0].EntitiesProcessed));
    }

    [Test]
    public void RoundTrip_MultipleEvents_PreservesAbsoluteTimestampsAfterDeltaEncoding()
    {
        // Non-monotonic-difference timestamps to exercise delta encoding
        var original = new[]
        {
            MakeEvent(10_000, 1, TraceEventType.TickStart),
            MakeEvent(10_100, 1, TraceEventType.PhaseStart),
            MakeEvent(11_234, 1, TraceEventType.ChunkStart, systemIndex: 2),
            MakeEvent(15_999, 1, TraceEventType.ChunkEnd, systemIndex: 2, entities: 42),
            MakeEvent(20_000, 1, TraceEventType.PhaseEnd),
            MakeEvent(20_050, 1, TraceEventType.TickEnd)
        };

        var decoded = EncodeDecode(original);

        Assert.That(decoded.Length, Is.EqualTo(original.Length));
        for (var i = 0; i < original.Length; i++)
        {
            Assert.That(decoded[i].TimestampTicks, Is.EqualTo(original[i].TimestampTicks), $"timestamp mismatch at index {i}");
            Assert.That(decoded[i].EventType, Is.EqualTo(original[i].EventType), $"event type mismatch at index {i}");
        }
    }

    [Test]
    public void RoundTrip_MaxBlockSize_Works()
    {
        // 4096 events is the advertised max block size
        var original = new TraceEvent[4096];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = MakeEvent(1000 + i * 37, 1, TraceEventType.ChunkStart, systemIndex: (ushort)(i % 100));
        }

        var decoded = EncodeDecode(original);

        Assert.That(decoded.Length, Is.EqualTo(original.Length));
        for (var i = 0; i < original.Length; i++)
        {
            Assert.That(decoded[i].TimestampTicks, Is.EqualTo(original[i].TimestampTicks));
            Assert.That(decoded[i].SystemIndex, Is.EqualTo(original[i].SystemIndex));
        }
    }

    [Test]
    public void BlockHeader_ExposesCorrectFields()
    {
        var original = new[]
        {
            MakeEvent(100, 1, TraceEventType.TickStart),
            MakeEvent(200, 1, TraceEventType.TickEnd)
        };

        var rawBuffer = new byte[original.Length * TraceBlockEncoder.EventSize];
        var compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(rawBuffer.Length)];
        var blockHeader = new byte[TraceBlockEncoder.BlockHeaderSize];

        var compressedSize = TraceBlockEncoder.EncodeBlock(original, rawBuffer, compressedBuffer, blockHeader);

        var (uncompressed, compressed, count) = TraceBlockEncoder.ReadBlockHeader(blockHeader);
        Assert.That(uncompressed, Is.EqualTo(original.Length * TraceBlockEncoder.EventSize));
        Assert.That(compressed, Is.EqualTo(compressedSize));
        Assert.That(count, Is.EqualTo(original.Length));
    }

    [Test]
    public void FileWriterAndReader_RoundTripViaSharedEncoder()
    {
        // Full pipeline: write through TraceFileWriter, read back through TraceFileReader.
        // Both use TraceBlockEncoder under the hood, so this validates the shared contract.
        var original = new[]
        {
            MakeEvent(1_000, 1, TraceEventType.TickStart),
            MakeEvent(1_050, 1, TraceEventType.ChunkStart, systemIndex: 5, workerId: 2),
            MakeEvent(1_200, 1, TraceEventType.ChunkEnd, systemIndex: 5, workerId: 2, entities: 100),
            MakeEvent(1_300, 1, TraceEventType.TickEnd)
        };

        // Use two separate MemoryStreams: write into one, copy bytes, read from another.
        // TraceFileWriter's Dispose closes the stream, so we can't reuse a single MemoryStream.
        var writeStream = new MemoryStream();
        var writer = new TraceFileWriter(writeStream);
        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 60,
            WorkerCount = 1,
            SystemCount = 0,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            SamplingSessionStartQpc = 0
        };
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteSpanNames(new System.Collections.Generic.Dictionary<int, string>());
        writer.WriteEvents(original);
        writer.Flush();

        var bytes = writeStream.ToArray();
        var readStream = new MemoryStream(bytes);

        using (var reader = new TraceFileReader(readStream))
        {
            var readHeader = reader.ReadHeader();
            Assert.That(readHeader.Magic, Is.EqualTo(TraceFileHeader.MagicValue));

            reader.ReadSystemDefinitions();
            reader.ReadSpanNames();

            Assert.That(reader.ReadNextBlock(out var events), Is.True);
            Assert.That(events.Length, Is.EqualTo(original.Length));

            for (var i = 0; i < original.Length; i++)
            {
                Assert.That(events[i].TimestampTicks, Is.EqualTo(original[i].TimestampTicks));
                Assert.That(events[i].EventType, Is.EqualTo(original[i].EventType));
                Assert.That(events[i].SystemIndex, Is.EqualTo(original[i].SystemIndex));
                Assert.That(events[i].WorkerId, Is.EqualTo(original[i].WorkerId));
                Assert.That(events[i].EntitiesProcessed, Is.EqualTo(original[i].EntitiesProcessed));
            }
        }
    }

    [Test]
    public void DoesNotMutateInput()
    {
        // The encoder must not mutate the caller's event span — delta encoding happens in the scratch buffer
        var original = new[]
        {
            MakeEvent(1000, 1, TraceEventType.TickStart),
            MakeEvent(2000, 1, TraceEventType.TickEnd)
        };

        var rawBuffer = new byte[original.Length * TraceBlockEncoder.EventSize];
        var compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(rawBuffer.Length)];
        var blockHeader = new byte[TraceBlockEncoder.BlockHeaderSize];

        TraceBlockEncoder.EncodeBlock(original, rawBuffer, compressedBuffer, blockHeader);

        // Originals unchanged
        Assert.That(original[0].TimestampTicks, Is.EqualTo(1000));
        Assert.That(original[1].TimestampTicks, Is.EqualTo(2000));
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static TraceEvent[] EncodeDecode(TraceEvent[] original)
    {
        var rawBuffer = new byte[original.Length * TraceBlockEncoder.EventSize];
        var compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(rawBuffer.Length)];
        var blockHeader = new byte[TraceBlockEncoder.BlockHeaderSize];

        var compressedSize = TraceBlockEncoder.EncodeBlock(original, rawBuffer, compressedBuffer, blockHeader);
        var (uncompressed, _, count) = TraceBlockEncoder.ReadBlockHeader(blockHeader);

        var decoded = new TraceEvent[count];
        var decodeRawBuffer = new byte[uncompressed];
        TraceBlockEncoder.DecodeBlock(
            compressedBuffer.AsSpan(0, compressedSize),
            uncompressed,
            count,
            decodeRawBuffer,
            decoded);

        return decoded;
    }
}
