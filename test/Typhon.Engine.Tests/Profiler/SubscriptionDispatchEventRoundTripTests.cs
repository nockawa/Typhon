using NUnit.Framework;
using System;
using Typhon.Profiler;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 6 Subscription dispatch event codecs added in Phase 9 (#287).
/// Existing kind 164 (RuntimeSubscriptionOutputExecute, Phase 4) covered by Phase 4's tests — not retested here.
/// </summary>
[TestFixture]
public class SubscriptionDispatchEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    [Test]
    public void RuntimeSubscriptionSubscriber_RoundTrip()
    {
        var ev = new RuntimeSubscriptionSubscriberEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SubscriberId = 12345u,
            ViewId = 7,
            DeltaCount = 50,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeSubscriber(buf);
        Assert.That(d.SubscriberId, Is.EqualTo(12345u));
        Assert.That(d.ViewId, Is.EqualTo(7));
        Assert.That(d.DeltaCount, Is.EqualTo(50));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
    }

    [Test]
    public void RuntimeSubscriptionDeltaBuild_RoundTrip()
    {
        var ev = new RuntimeSubscriptionDeltaBuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ViewId = 9,
            Added = 10,
            Removed = 3,
            Modified = 25,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeDeltaBuild(buf);
        Assert.That(d.ViewId, Is.EqualTo(9));
        Assert.That(d.Added, Is.EqualTo(10));
        Assert.That(d.Removed, Is.EqualTo(3));
        Assert.That(d.Modified, Is.EqualTo(25));
    }

    [TestCase((byte)0)]   // binary
    [TestCase((byte)1)]   // protobuf
    [TestCase((byte)2)]   // JSON
    public void RuntimeSubscriptionDeltaSerialize_RoundTrip(byte format)
    {
        var ev = new RuntimeSubscriptionDeltaSerializeEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ClientId = 99u,
            ViewId = 5,
            Bytes = 4096,
            Format = format,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeDeltaSerialize(buf);
        Assert.That(d.ClientId, Is.EqualTo(99u));
        Assert.That(d.ViewId, Is.EqualTo(5));
        Assert.That(d.Bytes, Is.EqualTo(4096));
        Assert.That(d.Format, Is.EqualTo(format));
    }

    [Test]
    public void RuntimeSubscriptionTransitionBeginSync_RoundTrip()
    {
        var ev = new RuntimeSubscriptionTransitionBeginSyncEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ClientId = 42u,
            ViewId = 11,
            EntitySnapshot = 1500,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeTransitionBeginSync(buf);
        Assert.That(d.ClientId, Is.EqualTo(42u));
        Assert.That(d.ViewId, Is.EqualTo(11));
        Assert.That(d.EntitySnapshot, Is.EqualTo(1500));
    }

    [Test]
    public void RuntimeSubscriptionOutputCleanup_RoundTrip()
    {
        var ev = new RuntimeSubscriptionOutputCleanupEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            DeadCount = 4,
            DeregCount = 12,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeOutputCleanup(buf);
        Assert.That(d.DeadCount, Is.EqualTo(4));
        Assert.That(d.DeregCount, Is.EqualTo(12));
    }

    [Test]
    public void RuntimeSubscriptionDeltaDirtyBitmapSupplement_RoundTrip()
    {
        var ev = new RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ModifiedFromRing = 100,
            SupplementCount = 250,
            UnionSize = 350,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeDirtyBitmapSupplement(buf);
        Assert.That(d.ModifiedFromRing, Is.EqualTo(100));
        Assert.That(d.SupplementCount, Is.EqualTo(250));
        Assert.That(d.UnionSize, Is.EqualTo(350));
    }

    [Test]
    public void Phase9_IsSpan_AllReturnTrue()
    {
        // All 6 Phase 9 kinds are spans — they should fall through the IsSpan default.
        Assert.That(TraceEventKind.RuntimeSubscriptionSubscriber.IsSpan(), Is.True);
        Assert.That(TraceEventKind.RuntimeSubscriptionDeltaBuild.IsSpan(), Is.True);
        Assert.That(TraceEventKind.RuntimeSubscriptionDeltaSerialize.IsSpan(), Is.True);
        Assert.That(TraceEventKind.RuntimeSubscriptionTransitionBeginSync.IsSpan(), Is.True);
        Assert.That(TraceEventKind.RuntimeSubscriptionOutputCleanup.IsSpan(), Is.True);
        Assert.That(TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement.IsSpan(), Is.True);
    }
}
