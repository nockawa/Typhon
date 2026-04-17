using System;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using NewTickPhase = Typhon.Engine.Profiler.TickPhase;

namespace Typhon.Engine.Tests.Profiler.Events;

/// <summary>
/// Round-trip tests for the new typed profiler events — each test builds an event struct, serializes it via <c>EncodeTo</c>, decodes it back via
/// the matching codec, and asserts every field survives the round trip. These tests are the authoritative fixture for the wire format.
/// </summary>
/// <remarks>
/// <b>Scope:</b> Phase 1 of the typed-event migration — pure serialization, no ring buffer, no TyphonEvent plumbing, no exporter. Each test
/// uses a stack-allocated <see cref="Span{Byte}"/> buffer. Tests cover: no-trace-context / trace-context variants, no-optional-fields /
/// all-optional-fields variants, and the edge case where a single optional bit flips.
/// </remarks>
[TestFixture]
public class EventRoundTripTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // BTree events — no payload, span header only
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BTreeInsert_NoTraceContext_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeInsertEvent
        {
            ThreadSlot = 12,
            StartTimestamp = 1_000_000,
            SpanId = 0xAABBCCDD_00000001UL,
            ParentSpanId = 0,
            TraceIdHi = 0,
            TraceIdLo = 0,
        };
        var endTs = 1_000_500L;

        var expectedSize = evt.ComputeSize();
        evt.EncodeTo(buffer, endTs, out var written);

        Assert.That(written, Is.EqualTo(expectedSize));
        Assert.That(written, Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize),
            "No trace context → span header only, no payload, 37 bytes");

        var decoded = BTreeEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.BTreeInsert));
        Assert.That(decoded.ThreadSlot, Is.EqualTo(12));
        Assert.That(decoded.StartTimestamp, Is.EqualTo(1_000_000L));
        Assert.That(decoded.DurationTicks, Is.EqualTo(500L));
        Assert.That(decoded.SpanId, Is.EqualTo(0xAABBCCDD_00000001UL));
        Assert.That(decoded.ParentSpanId, Is.EqualTo(0UL));
        Assert.That(decoded.HasTraceContext, Is.False);
    }

    [Test]
    public void BTreeInsert_WithTraceContext_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeInsertEvent
        {
            ThreadSlot = 3,
            StartTimestamp = 42,
            SpanId = 1,
            ParentSpanId = 2,
            TraceIdHi = 0x1122334455667788UL,
            TraceIdLo = 0x99AABBCCDDEEFF00UL,
        };

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);

        Assert.That(written, Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize),
            "With trace context → span header + 16 bytes, 53 bytes total");

        var decoded = BTreeEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.BTreeInsert));
        Assert.That(decoded.DurationTicks, Is.EqualTo(58L));
        Assert.That(decoded.TraceIdHi, Is.EqualTo(0x1122334455667788UL));
        Assert.That(decoded.TraceIdLo, Is.EqualTo(0x99AABBCCDDEEFF00UL));
        Assert.That(decoded.HasTraceContext, Is.True);
    }

    [Test]
    public void BTreeDelete_KindIsCorrect()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeDeleteEvent { ThreadSlot = 1, StartTimestamp = 10, SpanId = 5 };
        evt.EncodeTo(buffer, endTimestamp: 20, out var written);
        var decoded = BTreeEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.BTreeDelete));
    }

    [Test]
    public void BTreeNodeSplit_KindIsCorrect()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeNodeSplitEvent { ThreadSlot = 1, StartTimestamp = 10, SpanId = 5 };
        evt.EncodeTo(buffer, endTimestamp: 20, out var written);
        var decoded = BTreeEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.BTreeNodeSplit));
    }

    [Test]
    public void BTreeNodeMerge_KindIsCorrect()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeNodeMergeEvent { ThreadSlot = 1, StartTimestamp = 10, SpanId = 5 };
        evt.EncodeTo(buffer, endTimestamp: 20, out var written);
        var decoded = BTreeEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.BTreeNodeMerge));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ECS Query events — required + optional + enum
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void EcsQueryExecute_AllOptionalsSet_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryExecuteEvent
        {
            ThreadSlot = 8,
            StartTimestamp = 500_000,
            SpanId = 0x0100000000000007UL,
            ParentSpanId = 0x0100000000000003UL,
            ArchetypeTypeId = 42,
        };
        evt.ResultCount = 1234;
        evt.ScanMode = EcsQueryScanMode.Spatial;

        evt.EncodeTo(buffer, endTimestamp: 500_750, out var written);

        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.EcsQueryExecute));
        Assert.That(decoded.ThreadSlot, Is.EqualTo(8));
        Assert.That(decoded.StartTimestamp, Is.EqualTo(500_000L));
        Assert.That(decoded.DurationTicks, Is.EqualTo(750L));
        Assert.That(decoded.SpanId, Is.EqualTo(0x0100000000000007UL));
        Assert.That(decoded.ParentSpanId, Is.EqualTo(0x0100000000000003UL));
        Assert.That(decoded.ArchetypeTypeId, Is.EqualTo(42));
        Assert.That(decoded.HasResultCount, Is.True);
        Assert.That(decoded.ResultCount, Is.EqualTo(1234));
        Assert.That(decoded.HasScanMode, Is.True);
        Assert.That(decoded.ScanMode, Is.EqualTo(EcsQueryScanMode.Spatial));
        Assert.That(decoded.HasFound, Is.False);
    }

    [Test]
    public void EcsQueryExecute_NoOptionalsSet_SkipsPayloadSlots()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryExecuteEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 7,
        };

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);

        Assert.That(written, Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 3),
            "span header (37) + archetype u16 (2) + optMask u8 (1) = 40 bytes — no optional slots");

        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.HasResultCount, Is.False);
        Assert.That(decoded.HasScanMode, Is.False);
        Assert.That(decoded.ArchetypeTypeId, Is.EqualTo(7));
    }

    [Test]
    public void EcsQueryExecute_OnlyResultCountSet_SkipsScanMode()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryExecuteEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 9,
        };
        evt.ResultCount = 99;

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);

        Assert.That(written, Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 3 + 4),
            "span header (37) + required payload (3) + ResultCount (4) = 44 bytes, no ScanMode slot");

        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.HasResultCount, Is.True);
        Assert.That(decoded.ResultCount, Is.EqualTo(99));
        Assert.That(decoded.HasScanMode, Is.False);
    }

    [Test]
    public void EcsQueryCount_KindIsCorrect()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryCountEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 3,
        };
        evt.ResultCount = 500;
        evt.ScanMode = EcsQueryScanMode.Targeted;

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.EcsQueryCount));
        Assert.That(decoded.ResultCount, Is.EqualTo(500));
        Assert.That(decoded.ScanMode, Is.EqualTo(EcsQueryScanMode.Targeted));
    }

    [Test]
    public void EcsQueryAny_FoundOptionalSharesSlotWithResultCount()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryAnyEvent
        {
            ThreadSlot = 2,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 11,
        };
        evt.Found = true;
        evt.ScanMode = EcsQueryScanMode.Broad;

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);

        Assert.That(written, Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 3 + 4 + 1),
            "span header + required + Found (4B shared with ResultCount slot) + ScanMode (1B)");

        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.EcsQueryAny));
        Assert.That(decoded.HasFound, Is.True);
        Assert.That(decoded.Found, Is.True);
        Assert.That(decoded.HasResultCount, Is.False);
        Assert.That(decoded.ScanMode, Is.EqualTo(EcsQueryScanMode.Broad));
    }

    [Test]
    public void EcsQueryAny_FoundFalse_RoundTripsCorrectly()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryAnyEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 5,
        };
        evt.Found = false;

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.HasFound, Is.True, "Found was explicitly set — mask bit should be on even if value is false");
        Assert.That(decoded.Found, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transaction events
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TransactionCommit_FullPayload_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new TransactionCommitEvent
        {
            ThreadSlot = 4,
            StartTimestamp = 10_000,
            SpanId = 0x0100000000000002UL,
            ParentSpanId = 0,
            Tsn = 12345,
        };
        evt.ComponentCount = 7;
        evt.ConflictDetected = true;

        evt.EncodeTo(buffer, endTimestamp: 12_000, out var written);
        var decoded = TransactionEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.TransactionCommit));
        Assert.That(decoded.Tsn, Is.EqualTo(12345L));
        Assert.That(decoded.DurationTicks, Is.EqualTo(2_000L));
        Assert.That(decoded.HasComponentCount, Is.True);
        Assert.That(decoded.ComponentCount, Is.EqualTo(7));
        Assert.That(decoded.HasConflictDetected, Is.True);
        Assert.That(decoded.ConflictDetected, Is.True);
    }

    [Test]
    public void TransactionRollback_OnlyTsn_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new TransactionRollbackEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 9,
            Tsn = 99,
        };

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = TransactionEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.TransactionRollback));
        Assert.That(decoded.Tsn, Is.EqualTo(99L));
        Assert.That(decoded.HasComponentCount, Is.False);
        Assert.That(decoded.HasConflictDetected, Is.False);
    }

    [Test]
    public void TransactionCommitComponent_TwoRequiredFields_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new TransactionCommitComponentEvent
        {
            ThreadSlot = 2,
            StartTimestamp = 0,
            SpanId = 5,
            ParentSpanId = 2,  // child of an outer commit
            Tsn = 500,
            ComponentTypeId = 42,
        };

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = TransactionEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.TransactionCommitComponent));
        Assert.That(decoded.Tsn, Is.EqualTo(500L));
        Assert.That(decoded.ComponentTypeId, Is.EqualTo(42));
        Assert.That(decoded.ParentSpanId, Is.EqualTo(2UL));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ECS lifecycle events (Spawn, Destroy, ViewRefresh)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void EcsSpawn_AllOptionals_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsSpawnEvent
        {
            ThreadSlot = 3,
            StartTimestamp = 100,
            SpanId = 1,
            ArchetypeId = 7,
        };
        evt.EntityId = 0xDEADBEEFUL;
        evt.Tsn = 42;

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = EcsSpawnEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.ArchetypeId, Is.EqualTo(7));
        Assert.That(decoded.HasEntityId, Is.True);
        Assert.That(decoded.EntityId, Is.EqualTo(0xDEADBEEFUL));
        Assert.That(decoded.HasTsn, Is.True);
        Assert.That(decoded.Tsn, Is.EqualTo(42L));
    }

    [Test]
    public void EcsDestroy_CascadeCountOptional_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsDestroyEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 5,
            EntityId = 99,
        };
        evt.CascadeCount = 4;

        evt.EncodeTo(buffer, endTimestamp: 50, out var written);
        var decoded = EcsDestroyEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.EntityId, Is.EqualTo(99UL));
        Assert.That(decoded.HasCascadeCount, Is.True);
        Assert.That(decoded.CascadeCount, Is.EqualTo(4));
        Assert.That(decoded.HasTsn, Is.False);
    }

    [Test]
    public void EcsViewRefresh_IncrementalMode_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsViewRefreshEvent
        {
            ThreadSlot = 5,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 11,
        };
        evt.Mode = EcsViewRefreshMode.Incremental;
        evt.ResultCount = 250;
        evt.DeltaCount = 13;

        evt.EncodeTo(buffer, endTimestamp: 500, out var written);
        var decoded = EcsViewRefreshEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.ArchetypeTypeId, Is.EqualTo(11));
        Assert.That(decoded.Mode, Is.EqualTo(EcsViewRefreshMode.Incremental));
        Assert.That(decoded.ResultCount, Is.EqualTo(250));
        Assert.That(decoded.DeltaCount, Is.EqualTo(13));
    }

    [Test]
    public void EcsViewRefresh_OverflowMode_NoDeltaCount()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsViewRefreshEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            ArchetypeTypeId = 3,
        };
        evt.Mode = EcsViewRefreshMode.Overflow;
        evt.ResultCount = 1000;

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = EcsViewRefreshEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Mode, Is.EqualTo(EcsViewRefreshMode.Overflow));
        Assert.That(decoded.ResultCount, Is.EqualTo(1000));
        Assert.That(decoded.HasDeltaCount, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Page cache events
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void PageCacheFetch_FilePageIndexOnly_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheFetchEvent
        {
            ThreadSlot = 2,
            StartTimestamp = 0,
            SpanId = 1,
            FilePageIndex = 777,
        };

        evt.EncodeTo(buffer, endTimestamp: 50, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PageCacheFetch));
        Assert.That(decoded.FilePageIndex, Is.EqualTo(777));
        Assert.That(decoded.HasPageCount, Is.False);
    }

    [Test]
    public void PageCacheDiskWrite_WithPageCount_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheDiskWriteEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            FilePageIndex = 100,
        };
        evt.PageCount = 8;

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PageCacheDiskWrite));
        Assert.That(decoded.FilePageIndex, Is.EqualTo(100));
        Assert.That(decoded.HasPageCount, Is.True);
        Assert.That(decoded.PageCount, Is.EqualTo(8));
    }

    [Test]
    public void PageCacheFlush_UsesFilePageIndexSlotForCount()
    {
        // Flush reuses the FilePageIndex slot to store its PageCount (they share the same 4-byte primary payload slot)
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheFlushEvent
        {
            ThreadSlot = 1,
            StartTimestamp = 0,
            SpanId = 1,
            PageCount = 16,
        };

        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PageCacheFlush));
        Assert.That(decoded.FilePageIndex, Is.EqualTo(16),
            "Flush stores its PageCount in the FilePageIndex wire slot for codec uniformity");
    }

    [Test]
    public void PageCacheDiskRead_KindIsCorrect()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheDiskReadEvent { ThreadSlot = 1, StartTimestamp = 0, SpanId = 1, FilePageIndex = 5 };
        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PageCacheDiskRead));
    }

    [Test]
    public void PageCacheAllocatePage_KindIsCorrect()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheAllocatePageEvent { ThreadSlot = 1, StartTimestamp = 0, SpanId = 1, FilePageIndex = 5 };
        evt.EncodeTo(buffer, endTimestamp: 100, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PageCacheAllocatePage));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cluster migration + Scheduler chunk
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterMigration_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new ClusterMigrationEvent
        {
            ThreadSlot = 0,
            StartTimestamp = 1000,
            SpanId = 1,
            ArchetypeId = 42,
            MigrationCount = 128,
        };

        evt.EncodeTo(buffer, endTimestamp: 1500, out var written);
        var decoded = ClusterMigrationEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.ArchetypeId, Is.EqualTo(42));
        Assert.That(decoded.MigrationCount, Is.EqualTo(128));
        Assert.That(decoded.DurationTicks, Is.EqualTo(500L));
    }

    [Test]
    public void SchedulerChunk_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new SchedulerChunkEvent
        {
            ThreadSlot = 6,
            StartTimestamp = 0,
            SpanId = 1,
            ParentSpanId = 0,
            SystemIndex = 9,
            ChunkIndex = 2,
            TotalChunks = 8,
            EntitiesProcessed = 1024,
        };

        evt.EncodeTo(buffer, endTimestamp: 400, out var written);
        var decoded = SchedulerChunkEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SystemIndex, Is.EqualTo(9));
        Assert.That(decoded.ChunkIndex, Is.EqualTo(2));
        Assert.That(decoded.TotalChunks, Is.EqualTo(8));
        Assert.That(decoded.EntitiesProcessed, Is.EqualTo(1024));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // NamedSpan fallback
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NamedSpan_InlineUtf8Name_RoundTrips()
    {
        var buffer = new byte[256];
        NamedSpanEventCodec.Encode(buffer, endTimestamp: 100, threadSlot: 1, startTimestamp: 0,
            spanId: 1, parentSpanId: 0, traceIdHi: 0, traceIdLo: 0,
            name: "MyCustomSpan.Nested", out var written);

        var decoded = NamedSpanEventCodec.Decode(new ReadOnlyMemory<byte>(buffer, 0, written));
        Assert.That(decoded.GetName(), Is.EqualTo("MyCustomSpan.Nested"));
        Assert.That(decoded.DurationTicks, Is.EqualTo(100L));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Instant events
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void InstantEvent_TickStart_HeaderOnly()
    {
        Span<byte> buffer = stackalloc byte[32];
        InstantEventCodec.WriteTickStart(buffer, threadSlot: 1, timestamp: 12345, out var written);

        Assert.That(written, Is.EqualTo(TraceRecordHeader.CommonHeaderSize));

        var decoded = InstantEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.TickStart));
        Assert.That(decoded.Timestamp, Is.EqualTo(12345L));
    }

    [Test]
    public void InstantEvent_TickEnd_CarriesOverloadAndMultiplier()
    {
        Span<byte> buffer = stackalloc byte[32];
        InstantEventCodec.WriteTickEnd(buffer, threadSlot: 1, timestamp: 500, overloadLevel: 2, tickMultiplier: 4, out var written);

        var decoded = InstantEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.TickEnd));
        Assert.That(decoded.P1, Is.EqualTo(2));
        Assert.That(decoded.P2, Is.EqualTo(4));
    }

    [Test]
    public void InstantEvent_SystemReady_PacksSystemIdxAndPredecessorCount()
    {
        Span<byte> buffer = stackalloc byte[32];
        InstantEventCodec.WriteSystemReady(buffer, threadSlot: 0, timestamp: 0, systemIndex: 42, predecessorCount: 5, out var written);

        var decoded = InstantEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.SystemReady));
        Assert.That(decoded.P1, Is.EqualTo(42));
        Assert.That(decoded.P2, Is.EqualTo(5));
    }

    [Test]
    public void InstantEvent_PhaseStart_CarriesPhase()
    {
        Span<byte> buffer = stackalloc byte[32];
        InstantEventCodec.WritePhaseStart(buffer, threadSlot: 0, timestamp: 0, phase: NewTickPhase.SystemDispatch, out var written);

        var decoded = InstantEventCodec.Decode(buffer[..written]);
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PhaseStart));
        Assert.That(decoded.P1, Is.EqualTo((int)NewTickPhase.SystemDispatch));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Size field invariant
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void EveryEventKind_ReportsCorrectSizeInHeader()
    {
        // The u16 size field in the common header must always equal the bytes actually written — the consumer relies on this
        // to advance its read cursor through a variable-size record stream.
        Span<byte> buffer = stackalloc byte[256];

        var btInsert = new BTreeInsertEvent { ThreadSlot = 1, StartTimestamp = 0, SpanId = 1 };
        btInsert.EncodeTo(buffer, 100, out var written);
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer), Is.EqualTo((ushort)written),
            "BTreeInsert: header size must match bytes written");

        var txCommit = new TransactionCommitEvent { ThreadSlot = 1, StartTimestamp = 0, SpanId = 1, Tsn = 5 };
        txCommit.ComponentCount = 3;
        txCommit.EncodeTo(buffer, 100, out written);
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer), Is.EqualTo((ushort)written),
            "TransactionCommit: header size must match bytes written");

        var pcDiskWrite = new PageCacheDiskWriteEvent { ThreadSlot = 1, StartTimestamp = 0, SpanId = 1, FilePageIndex = 9 };
        pcDiskWrite.PageCount = 4;
        pcDiskWrite.EncodeTo(buffer, 100, out written);
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer), Is.EqualTo((ushort)written),
            "PageCacheDiskWrite: header size must match bytes written");
    }
}
