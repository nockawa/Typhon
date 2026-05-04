using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format round-trip tests for the optional 2-byte <c>SourceLocationId</c> field
/// (<c>SpanFlagsHasSourceLocation</c>, bit 1 of <c>SpanFlags</c>).
///
/// Backward compatibility property: when <c>SourceLocationId == 0</c>, the encoder writes
/// the legacy bytes only (no flag bit, no extra 2 bytes). When non-zero, the encoder appends
/// 2 bytes after any optional trace context, and the decoder picks them up.
/// </summary>
[TestFixture]
public class SourceLocationWireFormatTests
{
    [Test]
    public void Encode_WithZeroSiteId_ProducesLegacyWireFormat()
    {
        var evt = new BTreeInsertEvent
        {
            ThreadSlot = 7,
            StartTimestamp = 1000,
            SpanId = 0xAAAA_BBBB_CCCC_DDDDul,
            ParentSpanId = 0,
            PreviousSpanId = 0,
            TraceIdHi = 0,
            TraceIdLo = 0,
            SourceLocationId = 0,
        };

        var size = evt.ComputeSize();
        Assert.That(size, Is.EqualTo(TraceRecordHeader.SpanHeaderSize(hasTraceContext: false)),
            "With SourceLocationId=0, ComputeSize must match the legacy span-header size.");

        var buffer = new byte[size];
        evt.EncodeTo(buffer, endTimestamp: 2000, out var bytesWritten);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var decoded = BTreeEventCodec.Decode(buffer);
        Assert.That(decoded.HasSourceLocation, Is.False);
        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0));
        Assert.That(decoded.SpanId, Is.EqualTo(evt.SpanId));
        Assert.That(decoded.ThreadSlot, Is.EqualTo(evt.ThreadSlot));
    }

    [Test]
    public void Encode_WithNonZeroSiteId_RoundTrips()
    {
        var evt = new BTreeInsertEvent
        {
            ThreadSlot = 3,
            StartTimestamp = 5000,
            SpanId = 0x1111_2222_3333_4444ul,
            ParentSpanId = 0x5555_6666_7777_8888ul,
            PreviousSpanId = 0,
            TraceIdHi = 0,
            TraceIdLo = 0,
            SourceLocationId = 0xABCD,
        };

        var size = evt.ComputeSize();
        Assert.That(size, Is.EqualTo(TraceRecordHeader.SpanHeaderSize(hasTraceContext: false) + TraceRecordHeader.SourceLocationIdSize),
            "With SourceLocationId != 0, ComputeSize must include the optional 2 bytes.");

        var buffer = new byte[size];
        evt.EncodeTo(buffer, endTimestamp: 7000, out var bytesWritten);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var decoded = BTreeEventCodec.Decode(buffer);
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0xABCD));
        Assert.That(decoded.SpanId, Is.EqualTo(evt.SpanId));
        Assert.That(decoded.ParentSpanId, Is.EqualTo(evt.ParentSpanId));
        Assert.That(decoded.DurationTicks, Is.EqualTo(2000));
    }

    [Test]
    public void Encode_WithSiteIdAndTraceContext_BothFieldsRoundTrip()
    {
        var evt = new BTreeInsertEvent
        {
            ThreadSlot = 9,
            StartTimestamp = 100,
            SpanId = 0xFFFF_0000_FFFF_0000ul,
            ParentSpanId = 0,
            PreviousSpanId = 0,
            TraceIdHi = 0xAAAA_BBBB_CCCC_DDDDul,
            TraceIdLo = 0x0011_2233_4455_6677ul,
            SourceLocationId = 0x4242,
        };

        var expectedSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext: true, hasSourceLocation: true);
        Assert.That(evt.ComputeSize(), Is.EqualTo(expectedSize));

        var buffer = new byte[expectedSize];
        evt.EncodeTo(buffer, endTimestamp: 200, out var bytesWritten);
        Assert.That(bytesWritten, Is.EqualTo(expectedSize));

        var decoded = BTreeEventCodec.Decode(buffer);
        Assert.That(decoded.HasTraceContext, Is.True);
        Assert.That(decoded.TraceIdHi, Is.EqualTo(evt.TraceIdHi));
        Assert.That(decoded.TraceIdLo, Is.EqualTo(evt.TraceIdLo));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x4242));
    }

    [Test]
    public void SpanHeaderSize_AccountsForOptionalSourceLocation()
    {
        Assert.That(TraceRecordHeader.SpanHeaderSize(hasTraceContext: false, hasSourceLocation: false),
            Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize));
        Assert.That(TraceRecordHeader.SpanHeaderSize(hasTraceContext: false, hasSourceLocation: true),
            Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 2));
        Assert.That(TraceRecordHeader.SpanHeaderSize(hasTraceContext: true, hasSourceLocation: false),
            Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize));
        Assert.That(TraceRecordHeader.SpanHeaderSize(hasTraceContext: true, hasSourceLocation: true),
            Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize + 2));
    }

    [Test]
    public void SpanFlags_HasSourceLocationBitIsDistinctFromTraceContext()
    {
        // Bit 0 = trace context, Bit 1 = source location. Must not collide.
        Assert.That(TraceRecordHeader.SpanFlagsHasTraceContext, Is.EqualTo((byte)0x01));
        Assert.That(TraceRecordHeader.SpanFlagsHasSourceLocation, Is.EqualTo((byte)0x02));
        Assert.That(TraceRecordHeader.SpanFlagsHasTraceContext & TraceRecordHeader.SpanFlagsHasSourceLocation,
            Is.EqualTo(0));
    }
}
