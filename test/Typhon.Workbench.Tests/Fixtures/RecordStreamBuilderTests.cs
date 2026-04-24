using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions.Profiler;

namespace Typhon.Workbench.Tests.Fixtures;

/// <summary>
/// Smoke tests for <see cref="RecordStreamBuilder"/> — verifies its byte output is actually
/// decodable by the production <see cref="RecordDecoder"/>. If these fail, downstream decoder
/// tests built on the helper are also compromised.
/// </summary>
[TestFixture]
public sealed class RecordStreamBuilderTests
{
    private RecordDecoder _decoder;
    private List<LiveTraceEvent> _output;

    [SetUp]
    public void SetUp()
    {
        _decoder = new RecordDecoder(timestampFrequency: 10_000_000);
        _decoder.SetCurrentTick(0);
        _output = [];
    }

    [Test]
    public void TickStart_IncrementsTickCounter()
    {
        var bytes = new RecordStreamBuilder().TickStart(timestamp: 100).Build();
        _decoder.DecodeBlock(bytes, _output);
        Assert.That(_decoder.CurrentTick, Is.EqualTo(1), "first TickStart → tick 1");
        Assert.That(_output, Has.Count.EqualTo(1));
        Assert.That(_output[0].TickNumber, Is.EqualTo(1));
    }

    [Test]
    public void MultipleTickStarts_IncrementSequentially()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100)
            .TickStart(timestamp: 200)
            .TickStart(timestamp: 300)
            .Build();
        _decoder.DecodeBlock(bytes, _output);
        Assert.That(_decoder.CurrentTick, Is.EqualTo(3));
        Assert.That(_output, Has.Count.EqualTo(3));
    }

    [Test]
    public void Reset_ZeroesCounterForNewSession()
    {
        var bytes = new RecordStreamBuilder().TickStart().TickStart().Build();
        _decoder.DecodeBlock(bytes, _output);
        Assert.That(_decoder.CurrentTick, Is.EqualTo(2));

        _decoder.Reset();
        Assert.That(_decoder.CurrentTick, Is.EqualTo(0));
    }

    [Test]
    public void MalformedUndersized_StopsWalkAndRollsBack()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100) // valid
            .MalformedUndersized()     // triggers bail-out
            .TickStart(timestamp: 200) // should NOT be decoded
            .Build();

        _decoder.DecodeBlock(bytes, _output);
        // Decoder rolls back partial output on malformed record.
        Assert.That(_output, Has.Count.EqualTo(0),
            "malformed record rolls back the entire block — partial output cleared");
        Assert.That(_decoder.CurrentTick, Is.EqualTo(0),
            "tick counter is also rolled back to pre-block state");
    }

    [Test]
    public void UnknownKind_BuildsByteStream()
    {
        // Smoke-test just that the builder emits the right shape. The decoder's handling of
        // unknown kinds is a separate contract and has its own test set — keeping this narrow
        // keeps failure signals pointed at the right layer.
        var bytes = new RecordStreamBuilder()
            .UnknownKind(kindByte: 250, timestamp: 100)
            .Build();
        Assert.That(bytes.Length, Is.EqualTo(12), "common header only = 12 bytes");
        Assert.That(bytes[2], Is.EqualTo((byte)250), "kind byte preserved in output");
    }
}
