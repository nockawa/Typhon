using NUnit.Framework;
using System;
using Typhon.Profiler;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 27 Query / ECS:Query / ECS:View event codecs added in Phase 7 (#285)
/// + the wire-additive +variant byte on existing kind 32 (D2).
/// </summary>
[TestFixture]
public class QueryEcsViewEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    // ─────────────────────────────────────────────────────────────────────
    // EcsQueryExecute (kind 32) — wire-additive +variant u8 (D2)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(EcsQueryVariant.Execute)]
    [TestCase(EcsQueryVariant.Count)]
    [TestCase(EcsQueryVariant.Any)]
    public void EcsQueryExecute_VariantExtended_RoundTrip(EcsQueryVariant variant)
    {
        var optMask = (byte)(EcsQueryEventCodec.OptResultCount | EcsQueryEventCodec.OptScanMode | EcsQueryEventCodec.OptVariant);
        var size = EcsQueryEventCodec.ComputeSize(hasTraceContext: false, optMask);
        Span<byte> buf = stackalloc byte[size];
        EcsQueryEventCodec.Encode(buf, EndTs, TraceEventKind.EcsQueryExecute, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, archetypeTypeId: 42, optMask,
            resultCount: 1234, scanMode: EcsQueryScanMode.Targeted, found: false, out var bytesWritten, variant);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = EcsQueryEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.EcsQueryExecute));
        Assert.That(d.ArchetypeTypeId, Is.EqualTo(42));
        Assert.That(d.HasResultCount, Is.True);
        Assert.That(d.ResultCount, Is.EqualTo(1234));
        Assert.That(d.HasVariant, Is.True);
        Assert.That(d.Variant, Is.EqualTo(variant));
    }

    [Test]
    public void EcsQueryExecute_LegacyShape_RoundTrip()
    {
        // Legacy producer (no variant byte) — decoder still works.
        var optMask = EcsQueryEventCodec.OptResultCount;
        var size = EcsQueryEventCodec.ComputeSize(hasTraceContext: false, optMask);
        Span<byte> buf = stackalloc byte[size];
        EcsQueryEventCodec.Encode(buf, EndTs, TraceEventKind.EcsQueryExecute, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, archetypeTypeId: 42, optMask,
            resultCount: 999, scanMode: EcsQueryScanMode.Empty, found: false, out _);

        var d = EcsQueryEventCodec.Decode(buf);
        Assert.That(d.HasVariant, Is.False);
        Assert.That(d.Variant, Is.EqualTo(EcsQueryVariant.Execute));  // default
    }

    // ─────────────────────────────────────────────────────────────────────
    // Query (kinds 187-198)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void QueryParse_RoundTrip()
    {
        var ev = new QueryParseEvent
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
            PredicateCount = 7,
            BranchCount = 3,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeParse(buf);
        Assert.That(d.PredicateCount, Is.EqualTo(7));
        Assert.That(d.BranchCount, Is.EqualTo(3));
    }

    [Test]
    public void QueryParseDnf_RoundTrip()
    {
        var ev = new QueryParseDnfEvent
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
            InBranches = 5,
            OutBranches = 12,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeParseDnf(buf);
        Assert.That(d.InBranches, Is.EqualTo(5));
        Assert.That(d.OutBranches, Is.EqualTo(12));
    }

    [Test]
    public void QueryPlan_RoundTrip()
    {
        var ev = new QueryPlanEvent
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
            EvaluatorCount = 4,
            IndexFieldIdx = 9,
            RangeMin = -100,
            RangeMax = 1_000_000,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodePlan(buf);
        Assert.That(d.EvaluatorCount, Is.EqualTo(4));
        Assert.That(d.IndexFieldIdx, Is.EqualTo(9));
        Assert.That(d.RangeMin, Is.EqualTo(-100));
        Assert.That(d.RangeMax, Is.EqualTo(1_000_000));
    }

    [Test]
    public void QueryEstimate_RoundTrip()
    {
        var ev = new QueryEstimateEvent
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
            FieldIdx = 3,
            Cardinality = 50_000_000L,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeEstimate(buf);
        Assert.That(d.FieldIdx, Is.EqualTo(3));
        Assert.That(d.Cardinality, Is.EqualTo(50_000_000L));
    }

    [Test]
    public void QueryPlanPrimarySelect_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[QueryEventCodec.PrimarySelectSize];
        QueryEventCodec.WritePrimarySelect(buf, ThreadSlot, StartTs, candidates: 5, winnerIdx: 2, reason: 1);
        var d = QueryEventCodec.DecodePrimarySelect(buf);
        Assert.That(d.Candidates, Is.EqualTo(5));
        Assert.That(d.WinnerIdx, Is.EqualTo(2));
        Assert.That(d.Reason, Is.EqualTo(1));
    }

    [Test]
    public void QueryPlanSort_RoundTrip()
    {
        var ev = new QueryPlanSortEvent
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
            EvaluatorCount = 8,
            SortNs = 12345u,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodePlanSort(buf);
        Assert.That(d.EvaluatorCount, Is.EqualTo(8));
        Assert.That(d.SortNs, Is.EqualTo(12345u));
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void QueryExecuteIndexScan_RoundTrip(byte mode)
    {
        var ev = new QueryExecuteIndexScanEvent
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
            PrimaryFieldIdx = 11,
            Mode = mode,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeIndexScan(buf);
        Assert.That(d.PrimaryFieldIdx, Is.EqualTo(11));
        Assert.That(d.Mode, Is.EqualTo(mode));
    }

    [Test]
    public void QueryExecuteIterate_RoundTrip()
    {
        var ev = new QueryExecuteIterateEvent
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
            ChunkCount = 50,
            EntryCount = 50000,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeIterate(buf);
        Assert.That(d.ChunkCount, Is.EqualTo(50));
        Assert.That(d.EntryCount, Is.EqualTo(50000));
    }

    [Test]
    public void QueryExecuteFilter_RoundTrip()
    {
        var ev = new QueryExecuteFilterEvent
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
            FilterCount = 3,
            RejectedCount = 12345,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeFilter(buf);
        Assert.That(d.FilterCount, Is.EqualTo(3));
        Assert.That(d.RejectedCount, Is.EqualTo(12345));
    }

    [Test]
    public void QueryExecutePagination_RoundTrip()
    {
        var ev = new QueryExecutePaginationEvent
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
            Skip = 100,
            Take = 50,
            EarlyTerm = 1,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodePagination(buf);
        Assert.That(d.Skip, Is.EqualTo(100));
        Assert.That(d.Take, Is.EqualTo(50));
        Assert.That(d.EarlyTerm, Is.EqualTo(1));
    }

    [TestCase((byte)0)]   // SV
    [TestCase((byte)1)]   // Versioned
    [TestCase((byte)2)]   // Transient
    public void QueryExecuteStorageMode_RoundTrip(byte mode)
    {
        Span<byte> buf = stackalloc byte[QueryEventCodec.StorageModeSize];
        QueryEventCodec.WriteStorageMode(buf, ThreadSlot, StartTs, mode);
        var d = QueryEventCodec.DecodeStorageMode(buf);
        Assert.That(d.Mode, Is.EqualTo(mode));
    }

    [Test]
    public void QueryCount_RoundTrip()
    {
        var ev = new QueryCountEvent
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
            ResultCount = 9876,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = QueryEventCodec.DecodeCount(buf);
        Assert.That(d.ResultCount, Is.EqualTo(9876));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ECS:Query depth (kinds 199-203)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EcsQueryConstruct_RoundTrip()
    {
        var ev = new EcsQueryConstructEvent
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
            TargetArchId = 25,
            Polymorphic = 1,
            MaskSize = 4,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = EcsQueryDepthEventCodec.DecodeConstruct(buf);
        Assert.That(d.TargetArchId, Is.EqualTo(25));
        Assert.That(d.Polymorphic, Is.EqualTo(1));
        Assert.That(d.MaskSize, Is.EqualTo(4));
    }

    [Test]
    public void EcsQueryMaskAnd_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsQueryDepthEventCodec.MaskAndSize];
        EcsQueryDepthEventCodec.WriteMaskAnd(buf, ThreadSlot, StartTs, bitsBefore: 100, bitsAfter: 75, opType: 1);
        var d = EcsQueryDepthEventCodec.DecodeMaskAnd(buf);
        Assert.That(d.BitsBefore, Is.EqualTo(100));
        Assert.That(d.BitsAfter, Is.EqualTo(75));
        Assert.That(d.OpType, Is.EqualTo(1));
    }

    [Test]
    public void EcsQuerySubtreeExpand_RoundTrip()
    {
        var ev = new EcsQuerySubtreeExpandEvent
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
            SubtreeCount = 8,
            RootId = 12,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = EcsQueryDepthEventCodec.DecodeSubtreeExpand(buf);
        Assert.That(d.SubtreeCount, Is.EqualTo(8));
        Assert.That(d.RootId, Is.EqualTo(12));
    }

    [Test]
    public void EcsQueryConstraintEnabled_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsQueryDepthEventCodec.ConstraintEnabledSize];
        EcsQueryDepthEventCodec.WriteConstraintEnabled(buf, ThreadSlot, StartTs, typeId: 42, enableBit: 1);
        var d = EcsQueryDepthEventCodec.DecodeConstraintEnabled(buf);
        Assert.That(d.TypeId, Is.EqualTo(42));
        Assert.That(d.EnableBit, Is.EqualTo(1));
    }

    [Test]
    public void EcsQuerySpatialAttach_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsQueryDepthEventCodec.SpatialAttachSize];
        EcsQueryDepthEventCodec.WriteSpatialAttach(buf, ThreadSlot, StartTs, spatialType: 2,
            qbX1: 1.5f, qbY1: -2.5f, qbX2: 100.25f, qbY2: 200.75f);
        var d = EcsQueryDepthEventCodec.DecodeSpatialAttach(buf);
        Assert.That(d.SpatialType, Is.EqualTo(2));
        Assert.That(d.QueryBoxX1, Is.EqualTo(1.5f));
        Assert.That(d.QueryBoxY1, Is.EqualTo(-2.5f));
        Assert.That(d.QueryBoxX2, Is.EqualTo(100.25f));
        Assert.That(d.QueryBoxY2, Is.EqualTo(200.75f));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ECS:View depth (kinds 204-213)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EcsViewRefreshPull_RoundTrip()
    {
        var ev = new EcsViewRefreshPullEvent
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
            QueryNs = 5_000u,
            ArchetypeMaskBits = 7,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = EcsViewEventCodec.DecodeRefreshPull(buf);
        Assert.That(d.QueryNs, Is.EqualTo(5_000u));
        Assert.That(d.ArchetypeMaskBits, Is.EqualTo(7));
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void EcsViewIncrementalDrain_RoundTrip(byte overflow)
    {
        var ev = new EcsViewIncrementalDrainEvent
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
            DeltaCount = 250,
            Overflow = overflow,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = EcsViewEventCodec.DecodeIncrementalDrain(buf);
        Assert.That(d.DeltaCount, Is.EqualTo(250));
        Assert.That(d.Overflow, Is.EqualTo(overflow));
    }

    [Test]
    public void EcsViewDeltaBufferOverflow_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsViewEventCodec.DeltaBufferOverflowSize];
        EcsViewEventCodec.WriteDeltaBufferOverflow(buf, ThreadSlot, StartTs,
            currentTsn: 1000, tailTsn: 500, marginPagesLost: 7);
        var d = EcsViewEventCodec.DecodeDeltaBufferOverflow(buf);
        Assert.That(d.CurrentTsn, Is.EqualTo(1000));
        Assert.That(d.TailTsn, Is.EqualTo(500));
        Assert.That(d.MarginPagesLost, Is.EqualTo(7));
    }

    [Test]
    public void EcsViewProcessEntry_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsViewEventCodec.ProcessEntrySize];
        EcsViewEventCodec.WriteProcessEntry(buf, ThreadSlot, StartTs, pk: 0x1234, fieldIdx: 9, pass: 1);
        var d = EcsViewEventCodec.DecodeProcessEntry(buf);
        Assert.That(d.Pk, Is.EqualTo(0x1234));
        Assert.That(d.FieldIdx, Is.EqualTo(9));
        Assert.That(d.Pass, Is.EqualTo(1));
    }

    [Test]
    public void EcsViewProcessEntryOr_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsViewEventCodec.ProcessEntryOrSize];
        EcsViewEventCodec.WriteProcessEntryOr(buf, ThreadSlot, StartTs, pk: 0x5678, branchCount: 4, bitmapDelta: 0xDEADBEEFu);
        var d = EcsViewEventCodec.DecodeProcessEntryOr(buf);
        Assert.That(d.Pk, Is.EqualTo(0x5678));
        Assert.That(d.BranchCount, Is.EqualTo(4));
        Assert.That(d.BitmapDelta, Is.EqualTo(0xDEADBEEFu));
    }

    [Test]
    public void EcsViewRefreshFull_RoundTrip()
    {
        var ev = new EcsViewRefreshFullEvent
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
            OldCount = 100,
            NewCount = 200,
            RequeryNs = 500_000u,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = EcsViewEventCodec.DecodeRefreshFull(buf);
        Assert.That(d.OldCount, Is.EqualTo(100));
        Assert.That(d.NewCount, Is.EqualTo(200));
        Assert.That(d.RequeryNs, Is.EqualTo(500_000u));
    }

    [Test]
    public void EcsViewRefreshFullOr_RoundTrip()
    {
        var ev = new EcsViewRefreshFullOrEvent
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
            OldCount = 50,
            NewCount = 75,
            BranchCount = 3,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = EcsViewEventCodec.DecodeRefreshFullOr(buf);
        Assert.That(d.OldCount, Is.EqualTo(50));
        Assert.That(d.NewCount, Is.EqualTo(75));
        Assert.That(d.BranchCount, Is.EqualTo(3));
    }

    [Test]
    public void EcsViewRegistryRegister_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsViewEventCodec.RegistrySize];
        EcsViewEventCodec.WriteRegistryRegister(buf, ThreadSlot, StartTs, viewId: 5, fieldIdx: 12, regCount: 3);
        var d = EcsViewEventCodec.DecodeRegistry(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.EcsViewRegistryRegister));
        Assert.That(d.ViewId, Is.EqualTo(5));
        Assert.That(d.FieldIdx, Is.EqualTo(12));
        Assert.That(d.RegCount, Is.EqualTo(3));
    }

    [Test]
    public void EcsViewRegistryDeregister_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsViewEventCodec.RegistrySize];
        EcsViewEventCodec.WriteRegistryDeregister(buf, ThreadSlot, StartTs, viewId: 5, fieldIdx: 12, regCount: 2);
        var d = EcsViewEventCodec.DecodeRegistry(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.EcsViewRegistryDeregister));
        Assert.That(d.RegCount, Is.EqualTo(2));
    }

    [Test]
    public void EcsViewDeltaCacheMiss_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[EcsViewEventCodec.DeltaCacheMissSize];
        EcsViewEventCodec.WriteDeltaCacheMiss(buf, ThreadSlot, StartTs, pk: 0xABCD, reason: 2);
        var d = EcsViewEventCodec.DecodeDeltaCacheMiss(buf);
        Assert.That(d.Pk, Is.EqualTo(0xABCD));
        Assert.That(d.Reason, Is.EqualTo(2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // IsSpan classification
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void Phase7_IsSpan_ClassifiesCorrectly()
    {
        // Spans
        Assert.That(TraceEventKind.QueryParse.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryParseDnf.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryPlan.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryEstimate.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryPlanSort.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryExecuteIndexScan.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryExecuteIterate.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryExecuteFilter.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryExecutePagination.IsSpan(), Is.True);
        Assert.That(TraceEventKind.QueryCount.IsSpan(), Is.True);
        Assert.That(TraceEventKind.EcsQueryConstruct.IsSpan(), Is.True);
        Assert.That(TraceEventKind.EcsQuerySubtreeExpand.IsSpan(), Is.True);
        Assert.That(TraceEventKind.EcsViewRefreshPull.IsSpan(), Is.True);
        Assert.That(TraceEventKind.EcsViewIncrementalDrain.IsSpan(), Is.True);
        Assert.That(TraceEventKind.EcsViewRefreshFull.IsSpan(), Is.True);
        Assert.That(TraceEventKind.EcsViewRefreshFullOr.IsSpan(), Is.True);
        // Instants
        Assert.That(TraceEventKind.QueryPlanPrimarySelect.IsSpan(), Is.False);
        Assert.That(TraceEventKind.QueryExecuteStorageMode.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsQueryMaskAnd.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsQueryConstraintEnabled.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsQuerySpatialAttach.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsViewDeltaBufferOverflow.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsViewProcessEntry.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsViewProcessEntryOr.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsViewRegistryRegister.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsViewRegistryDeregister.IsSpan(), Is.False);
        Assert.That(TraceEventKind.EcsViewDeltaCacheMiss.IsSpan(), Is.False);
    }
}
