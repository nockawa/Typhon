using NUnit.Framework;
using System;
using Typhon.Profiler;

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
        var size = RuntimeSubscriptionEventCodec.ComputeSizeSubscriber(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        RuntimeSubscriptionEventCodec.EncodeSubscriber(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            subscriberId: 12345u, viewId: 7, deltaCount: 50, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeSubscriber(buf);
        Assert.That(d.SubscriberId, Is.EqualTo(12345u));
        Assert.That(d.ViewId, Is.EqualTo(7));
        Assert.That(d.DeltaCount, Is.EqualTo(50));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
    }

    [Test]
    public void RuntimeSubscriptionDeltaBuild_RoundTrip()
    {
        var size = RuntimeSubscriptionEventCodec.ComputeSizeDeltaBuild(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        RuntimeSubscriptionEventCodec.EncodeDeltaBuild(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            viewId: 9, added: 10, removed: 3, modified: 25, out _);
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
        var size = RuntimeSubscriptionEventCodec.ComputeSizeDeltaSerialize(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        RuntimeSubscriptionEventCodec.EncodeDeltaSerialize(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            clientId: 99u, viewId: 5, bytes: 4096, format, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeDeltaSerialize(buf);
        Assert.That(d.ClientId, Is.EqualTo(99u));
        Assert.That(d.ViewId, Is.EqualTo(5));
        Assert.That(d.Bytes, Is.EqualTo(4096));
        Assert.That(d.Format, Is.EqualTo(format));
    }

    [Test]
    public void RuntimeSubscriptionTransitionBeginSync_RoundTrip()
    {
        var size = RuntimeSubscriptionEventCodec.ComputeSizeTransitionBeginSync(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        RuntimeSubscriptionEventCodec.EncodeTransitionBeginSync(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            clientId: 42u, viewId: 11, entitySnapshot: 1500, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeTransitionBeginSync(buf);
        Assert.That(d.ClientId, Is.EqualTo(42u));
        Assert.That(d.ViewId, Is.EqualTo(11));
        Assert.That(d.EntitySnapshot, Is.EqualTo(1500));
    }

    [Test]
    public void RuntimeSubscriptionOutputCleanup_RoundTrip()
    {
        var size = RuntimeSubscriptionEventCodec.ComputeSizeOutputCleanup(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        RuntimeSubscriptionEventCodec.EncodeOutputCleanup(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            deadCount: 4, deregCount: 12, out _);
        var d = RuntimeSubscriptionEventCodec.DecodeOutputCleanup(buf);
        Assert.That(d.DeadCount, Is.EqualTo(4));
        Assert.That(d.DeregCount, Is.EqualTo(12));
    }

    [Test]
    public void RuntimeSubscriptionDeltaDirtyBitmapSupplement_RoundTrip()
    {
        var size = RuntimeSubscriptionEventCodec.ComputeSizeDirtyBitmapSupplement(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        RuntimeSubscriptionEventCodec.EncodeDirtyBitmapSupplement(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            modifiedFromRing: 100, supplementCount: 250, unionSize: 350, out _);
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
