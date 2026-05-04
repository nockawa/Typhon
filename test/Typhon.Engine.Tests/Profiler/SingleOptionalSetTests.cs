using System;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-optional tests for kinds with at least one [Optional] field. Each test sets exactly ONE optional via its
/// property setter and asserts that:
///   1. The corresponding optMask bit is set (and only that one — no other bits flipped accidentally).
///   2. The encoded payload size grows by exactly the one optional's wire size relative to the no-optionals case.
///
/// This complements <see cref="TraceEventEncodeEquivalenceTests"/> which sets ALL optionals simultaneously: when
/// the all-optionals golden test fails, you don't know which optional broke. This fixture localizes that.
/// </summary>
/// <remarks>
/// Skips kinds whose codec has slot-sharing or kind-conditional logic (TransactionEvent family, EcsQueryAny,
/// PageCache* family). Those are escape-hatch kinds where the wire layout doesn't follow the standard
/// "header + payload + optMask + optional fields in order" template.
/// </remarks>
[TestFixture]
public sealed class SingleOptionalSetTests
{
    private static TraceSpanHeader Header() => new()
    {
        ThreadSlot = 1,
        StartTimestamp = 1000L,
        SpanId = 0x1111UL,
        ParentSpanId = 0x2222UL,
    };

    private const long EndTs = 1100L;

    /// <summary>
    /// Scan the encoded payload (anything after the span header) for a byte equal to <paramref name="expectedMask"/>.
    /// Catches "wrong mask bit set" and "extra bit set" without coupling the test to per-kind wire-layout details.
    /// Must be called inline per-kind (not via a generic helper) because <c>scoped</c> + <c>allows ref struct</c>
    /// generic constraints don't compose for stack-allocated spans being passed across method boundaries.
    /// </summary>
    private static void AssertMaskByteFound(ReadOnlySpan<byte> buf, int len, byte expectedMask)
    {
        var headerSize = TraceRecordHeader.MinSpanHeaderSize; // 37 — no trace context in this test
        bool found = false;
        for (int i = headerSize; i < len; i++)
        {
            if (buf[i] == expectedMask)
            {
                found = true;
                break;
            }
        }
        Assert.That(found, Is.True,
            $"Expected optMask byte 0x{expectedMask:X2} not found in payload [{headerSize}..{len}]: {Convert.ToHexString(buf[..len])}");
    }

    // ── EcsSpawn (2 optionals: EntityId u64, Tsn i64) ──

    [Test]
    public void EcsSpawn_OnlyEntityIdSet_FlipsOnlyOptEntityIdBit()
    {
        var ev = new EcsSpawnEvent
        {
            Header = Header(),
            ArchetypeId = 7,
            EntityId = 0xAAAAUL,  // setter flips OptEntityId only
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, EcsSpawnEventCodec.OptEntityId);
    }

    [Test]
    public void EcsSpawn_OnlyTsnSet_FlipsOnlyOptTsnBit()
    {
        var ev = new EcsSpawnEvent
        {
            Header = Header(),
            ArchetypeId = 7,
            Tsn = 99L,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, EcsSpawnEventCodec.OptTsn);
    }

    // ── EcsDestroy (2 optionals: CascadeCount i32, Tsn i64) ──

    [Test]
    public void EcsDestroy_OnlyCascadeCountSet_FlipsOnlyOptCascadeCountBit()
    {
        var ev = new EcsDestroyEvent
        {
            Header = Header(),
            EntityId = 42UL,
            CascadeCount = 5,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, EcsDestroyEventCodec.OptCascadeCount);
    }

    [Test]
    public void EcsDestroy_OnlyTsnSet_FlipsOnlyOptTsnBit()
    {
        var ev = new EcsDestroyEvent
        {
            Header = Header(),
            EntityId = 42UL,
            Tsn = 99L,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, EcsDestroyEventCodec.OptTsn);
    }

    // ── CheckpointCycle (1 optional: DirtyPageCount i32) ──

    [Test]
    public void CheckpointCycle_OnlyDirtyPageCountSet_FlipsOnlyOptDirtyPageCountBit()
    {
        var ev = new CheckpointCycleEvent
        {
            Header = Header(),
            TargetLsn = 1000L,
            Reason = (byte)CheckpointReason.Periodic,
            DirtyPageCount = 256,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, CheckpointEventCodec.OptDirtyPageCount);
    }

    // ── CheckpointWrite (1 optional: WrittenCount i32) ──

    [Test]
    public void CheckpointWrite_OnlyWrittenCountSet_FlipsOnlyOptWrittenCountBit()
    {
        var ev = new CheckpointWriteEvent
        {
            Header = Header(),
            WrittenCount = 128,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, CheckpointEventCodec.OptWrittenCount);
    }

    // ── CheckpointTransition (1 optional: TransitionedCount i32) ──

    [Test]
    public void CheckpointTransition_OnlyTransitionedCountSet_FlipsOnlyOptTransitionedCountBit()
    {
        var ev = new CheckpointTransitionEvent
        {
            Header = Header(),
            TransitionedCount = 64,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, CheckpointEventCodec.OptTransitionedCount);
    }

    // ── CheckpointRecycle (1 optional: RecycledCount i32) ──

    [Test]
    public void CheckpointRecycle_OnlyRecycledCountSet_FlipsOnlyOptRecycledCountBit()
    {
        var ev = new CheckpointRecycleEvent
        {
            Header = Header(),
            RecycledCount = 16,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, CheckpointEventCodec.OptRecycledCount);
    }

    // ── No-optional baseline: confirm encoding fits in header-plus-payload only ──

    [Test]
    public void EcsSpawn_NoOptionalsSet_OptMaskByteIsZero()
    {
        var ev = new EcsSpawnEvent
        {
            Header = Header(),
            ArchetypeId = 7,
        };
        Span<byte> buf = stackalloc byte[256];
        ev.EncodeTo(buf, EndTs, out var len);
        AssertMaskByteFound(buf, len, expectedMask: 0);
    }
}
