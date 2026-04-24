using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Profiler;
using Typhon.Workbench.Tests.Fixtures;

namespace Typhon.Workbench.Tests.Sessions.Profiler;

/// <summary>
/// Regression guard for <see cref="RecordDecoder"/>'s instant-event dispatch. Feeds one valid record
/// per <see cref="TraceEventKind"/> that the decoder claims to understand and asserts it round-trips
/// to a <see cref="LiveTraceEvent"/> with the matching <c>Kind</c> — catches the class of bug where
/// a new case is added to the codec but forgotten in <see cref="RecordDecoder.DecodeInstant"/>.
///
/// Span-kind coverage lives in a follow-up (would need a SpanBuilder mirror of
/// <see cref="RecordStreamBuilder"/> for the 25 B span header extension). The value here is narrow
/// and high-ROI: every instant kind that ships decodes without surprise.
/// </summary>
[TestFixture]
public sealed class RecordDecoderEventKindTests
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
    public void TickStart_DecodesAndIncrementsTick()
    {
        var bytes = new RecordStreamBuilder().TickStart(timestamp: 100).Build();
        _decoder.DecodeBlock(bytes, _output);

        Assert.That(_output, Has.Count.EqualTo(1));
        Assert.That(_output[0].Kind, Is.EqualTo((int)TraceEventKind.TickStart));
        Assert.That(_output[0].TickNumber, Is.EqualTo(1), "TickStart must bump the decoder's tick counter");
    }

    [Test]
    public void TickEnd_PreservesOverloadAndMultiplier()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100)
            .TickEnd(timestamp: 200, overloadLevel: 3, tickMultiplier: 2)
            .Build();
        _decoder.DecodeBlock(bytes, _output);

        Assert.That(_output, Has.Count.EqualTo(2));
        Assert.That(_output[1].Kind, Is.EqualTo((int)TraceEventKind.TickEnd));
        Assert.That(_output[1].OverloadLevel, Is.EqualTo(3));
        Assert.That(_output[1].TickMultiplier, Is.EqualTo(2));
    }

    [Test]
    public void PhaseStart_And_PhaseEnd_PreservePhaseByte()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100)
            .PhaseStart(timestamp: 110, phase: 2)
            .PhaseEnd(timestamp: 180, phase: 2)
            .Build();
        _decoder.DecodeBlock(bytes, _output);

        Assert.That(_output, Has.Count.EqualTo(3));
        Assert.That(_output[1].Kind, Is.EqualTo((int)TraceEventKind.PhaseStart));
        Assert.That(_output[1].Phase, Is.EqualTo(2));
        Assert.That(_output[2].Kind, Is.EqualTo((int)TraceEventKind.PhaseEnd));
        Assert.That(_output[2].Phase, Is.EqualTo(2));
    }

    [Test]
    public void SystemReady_PreservesSystemIndex()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100)
            .SystemReady(timestamp: 120, systemIdx: 42, predecessorCount: 7)
            .Build();
        _decoder.DecodeBlock(bytes, _output);

        Assert.That(_output, Has.Count.EqualTo(2));
        Assert.That(_output[1].Kind, Is.EqualTo((int)TraceEventKind.SystemReady));
        Assert.That(_output[1].SystemIndex, Is.EqualTo(42));
    }

    [Test]
    public void SystemSkipped_PreservesIndexAndReason()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100)
            .SystemSkipped(timestamp: 130, systemIdx: 11, skipReason: 3)
            .Build();
        _decoder.DecodeBlock(bytes, _output);

        Assert.That(_output, Has.Count.EqualTo(2));
        Assert.That(_output[1].Kind, Is.EqualTo((int)TraceEventKind.SystemSkipped));
        Assert.That(_output[1].SystemIndex, Is.EqualTo(11));
        Assert.That(_output[1].SkipReason, Is.EqualTo(3));
    }

    [Test]
    public void Instant_GenericMarker_DecodesWithNoCrash()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100)
            .Instant(timestamp: 150, nameId: 7, payload: 42)
            .Build();
        _decoder.DecodeBlock(bytes, _output);

        // Generic Instant has no match-arm in DecodeInstant's final switch, so it decodes to null
        // and the decoder silently drops it. That's the documented contract — this test pins it
        // down as a regression guard. Only the TickStart survives.
        Assert.That(_output, Has.Count.EqualTo(1), "generic Instant is intentionally dropped; only TickStart survives");
        Assert.That(_output[0].Kind, Is.EqualTo((int)TraceEventKind.TickStart));
    }

    [Test]
    public void MalformedUndersizedRecord_RollsBackPartialOutput()
    {
        var bytes = new RecordStreamBuilder()
            .TickStart(timestamp: 100) // valid, produces one event
            .MalformedUndersized()     // size < 12 — triggers early-exit + rollback
            .TickStart(timestamp: 200) // would produce another event but walk already aborted
            .Build();
        _decoder.DecodeBlock(bytes, _output);

        Assert.That(_output, Has.Count.EqualTo(0),
            "malformed record rolls back the entire block — partial output is cleared");
        Assert.That(_decoder.CurrentTick, Is.EqualTo(0),
            "tick counter is also rolled back to pre-block state");
    }

    [Test]
    public void DecodeBlock_OverMultipleBlocks_AccumulatesTickCounter()
    {
        // Simulates the real streaming case: each Block frame is decoded independently and the
        // tick counter persists across calls.
        var block1 = new RecordStreamBuilder().TickStart(100).TickEnd(200).Build();
        var block2 = new RecordStreamBuilder().TickStart(300).TickEnd(400).Build();

        _decoder.DecodeBlock(block1, _output);
        Assert.That(_decoder.CurrentTick, Is.EqualTo(1));

        _decoder.DecodeBlock(block2, _output);
        Assert.That(_decoder.CurrentTick, Is.EqualTo(2));

        Assert.That(_output, Has.Count.EqualTo(4));
        Assert.That(_output[0].TickNumber, Is.EqualTo(1));
        Assert.That(_output[1].TickNumber, Is.EqualTo(1));
        Assert.That(_output[2].TickNumber, Is.EqualTo(2));
        Assert.That(_output[3].TickNumber, Is.EqualTo(2));
    }

    [Test]
    public void Reset_ZeroesCounter_ForNewSession()
    {
        var bytes = new RecordStreamBuilder().TickStart().TickStart().Build();
        _decoder.DecodeBlock(bytes, _output);
        Assert.That(_decoder.CurrentTick, Is.EqualTo(2));

        _decoder.Reset();
        Assert.That(_decoder.CurrentTick, Is.EqualTo(0));
        Assert.That(_output, Has.Count.EqualTo(2), "Reset does not touch the output buffer — caller owns that");
    }
}
