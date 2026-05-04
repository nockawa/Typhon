using NUnit.Framework;
using System;
using Typhon.Profiler;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 29 Spatial event codecs added in Phase 3 (#281). Each test calls the
/// codec's Write/Encode method against a stack-allocated buffer, then calls Decode and asserts the decoded
/// payload matches the inputs. The Tier-2 gate is bypassed by going straight to the codec.
/// </summary>
[TestFixture]
public class SpatialEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:Query (kinds 117-122) — span events
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SpatialQueryAabb_RoundTrip()
    {
        var ev = new SpatialQueryAabbEvent
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
            NodesVisited = 12,
            LeavesEntered = 3,
            ResultCount = 17,
            RestartCount = 1,
            CategoryMask = 0x000000FFu,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialQueryEventCodec.DecodeAabb(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
            Assert.That(d.StartTimestamp, Is.EqualTo(StartTs));
            Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
            Assert.That(d.SpanId, Is.EqualTo(SpanId));
            Assert.That(d.ParentSpanId, Is.EqualTo(ParentSpanId));
            Assert.That(d.NodesVisited, Is.EqualTo(12));
            Assert.That(d.LeavesEntered, Is.EqualTo(3));
            Assert.That(d.ResultCount, Is.EqualTo(17));
            Assert.That(d.RestartCount, Is.EqualTo(1));
            Assert.That(d.CategoryMask, Is.EqualTo(0x000000FFu));
        });
    }

    [Test]
    public void SpatialQueryRadius_RoundTrip()
    {
        var ev = new SpatialQueryRadiusEvent
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
            NodesVisited = 5,
            ResultCount = 2,
            Radius = 12.5f,
            RestartCount = 0,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialQueryEventCodec.DecodeRadius(buf);
        Assert.That(d.NodesVisited, Is.EqualTo(5));
        Assert.That(d.ResultCount, Is.EqualTo(2));
        Assert.That(d.Radius, Is.EqualTo(12.5f));
        Assert.That(d.RestartCount, Is.EqualTo(0));
    }

    [Test]
    public void SpatialQueryRay_RoundTrip()
    {
        var ev = new SpatialQueryRayEvent
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
            NodesVisited = 8,
            ResultCount = 1,
            MaxDist = 100f,
            RestartCount = 2,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialQueryEventCodec.DecodeRay(buf);
        Assert.That(d.NodesVisited, Is.EqualTo(8));
        Assert.That(d.ResultCount, Is.EqualTo(1));
        Assert.That(d.MaxDist, Is.EqualTo(100f));
        Assert.That(d.RestartCount, Is.EqualTo(2));
    }

    [Test]
    public void SpatialQueryFrustum_RoundTrip()
    {
        var ev = new SpatialQueryFrustumEvent
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
            NodesVisited = 20,
            ResultCount = 7,
            PlaneCount = 6,
            RestartCount = 0,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialQueryEventCodec.DecodeFrustum(buf);
        Assert.That(d.NodesVisited, Is.EqualTo(20));
        Assert.That(d.ResultCount, Is.EqualTo(7));
        Assert.That(d.PlaneCount, Is.EqualTo(6));
        Assert.That(d.RestartCount, Is.EqualTo(0));
    }

    [Test]
    public void SpatialQueryKnn_RoundTrip()
    {
        var ev = new SpatialQueryKnnEvent
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
            K = 10,
            IterCount = 3,
            FinalRadius = 5.5f,
            ResultCount = 8,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialQueryEventCodec.DecodeKnn(buf);
        Assert.That(d.K, Is.EqualTo(10));
        Assert.That(d.IterCount, Is.EqualTo(3));
        Assert.That(d.FinalRadius, Is.EqualTo(5.5f));
        Assert.That(d.ResultCount, Is.EqualTo(8));
    }

    [TestCase((byte)0, (ushort)15, 42)]
    [TestCase((byte)1, (ushort)4, 1024)]
    public void SpatialQueryCount_RoundTrip(byte variant, ushort nodesVisited, int resultCount)
    {
        var ev = new SpatialQueryCountEvent
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
            Variant = variant,
            NodesVisited = nodesVisited,
            ResultCount = resultCount,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialQueryEventCodec.DecodeCount(buf);
        Assert.That(d.Variant, Is.EqualTo(variant));
        Assert.That(d.NodesVisited, Is.EqualTo(nodesVisited));
        Assert.That(d.ResultCount, Is.EqualTo(resultCount));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:RTree (kinds 123-126) — span events
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(0x1122334455667788L, (byte)5, (byte)1, (byte)0)]
    [TestCase(-1L, (byte)0, (byte)0, (byte)10)]
    public void SpatialRTreeInsert_RoundTrip(long entityId, byte depth, byte didSplit, byte restartCount)
    {
        var ev = new SpatialRTreeInsertEvent
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
            EntityId = entityId,
            Depth = depth,
            DidSplit = didSplit,
            RestartCount = restartCount,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialRTreeEventCodec.DecodeInsert(buf);
        Assert.That(d.EntityId, Is.EqualTo(entityId));
        Assert.That(d.Depth, Is.EqualTo(depth));
        Assert.That(d.DidSplit, Is.EqualTo(didSplit));
        Assert.That(d.RestartCount, Is.EqualTo(restartCount));
    }

    [Test]
    public void SpatialRTreeRemove_RoundTrip()
    {
        var ev = new SpatialRTreeRemoveEvent
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
            EntityId = 0xDEADBEEFL,
            LeafCollapse = 1,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialRTreeEventCodec.DecodeRemove(buf);
        Assert.That(d.EntityId, Is.EqualTo(0xDEADBEEFL));
        Assert.That(d.LeafCollapse, Is.EqualTo(1));
    }

    [Test]
    public void SpatialRTreeNodeSplit_RoundTrip()
    {
        var ev = new SpatialRTreeNodeSplitEvent
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
            Depth = 4,
            SplitAxis = 1,
            LeftCount = 30,
            RightCount = 35,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialRTreeEventCodec.DecodeNodeSplit(buf);
        Assert.That(d.Depth, Is.EqualTo(4));
        Assert.That(d.SplitAxis, Is.EqualTo(1));
        Assert.That(d.LeftCount, Is.EqualTo(30));
        Assert.That(d.RightCount, Is.EqualTo(35));
    }

    [Test]
    public void SpatialRTreeBulkLoad_RoundTrip()
    {
        var ev = new SpatialRTreeBulkLoadEvent
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
            EntityCount = 10000,
            LeafCount = 156,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialRTreeEventCodec.DecodeBulkLoad(buf);
        Assert.That(d.EntityCount, Is.EqualTo(10000));
        Assert.That(d.LeafCount, Is.EqualTo(156));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:Grid (kinds 127-129) — instant events
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SpatialGridCellTierChange_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialGridEventCodec.CellTierChangeSize];
        SpatialGridEventCodec.WriteCellTierChange(buf, ThreadSlot, StartTs, cellKey: 12345, oldTier: 0x01, newTier: 0x04);
        var d = SpatialGridEventCodec.DecodeCellTierChange(buf);
        Assert.That(d.CellKey, Is.EqualTo(12345));
        Assert.That(d.OldTier, Is.EqualTo(0x01));
        Assert.That(d.NewTier, Is.EqualTo(0x04));
    }

    [TestCase(1234, (sbyte)1, (ushort)5, (ushort)6)]
    [TestCase(-1, (sbyte)-1, (ushort)10, (ushort)9)]
    public void SpatialGridOccupancyChange_RoundTrip(int cellKey, sbyte delta, ushort occBefore, ushort occAfter)
    {
        Span<byte> buf = stackalloc byte[SpatialGridEventCodec.OccupancyChangeSize];
        SpatialGridEventCodec.WriteOccupancyChange(buf, ThreadSlot, StartTs, cellKey, delta, occBefore, occAfter);
        var d = SpatialGridEventCodec.DecodeOccupancyChange(buf);
        Assert.That(d.CellKey, Is.EqualTo(cellKey));
        Assert.That(d.Delta, Is.EqualTo(delta));
        Assert.That(d.OccBefore, Is.EqualTo(occBefore));
        Assert.That(d.OccAfter, Is.EqualTo(occAfter));
    }

    [Test]
    public void SpatialGridClusterCellAssign_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialGridEventCodec.ClusterCellAssignSize];
        SpatialGridEventCodec.WriteClusterCellAssign(buf, ThreadSlot, StartTs, clusterChunkId: 99, cellKey: 7, archetypeId: 42);
        var d = SpatialGridEventCodec.DecodeClusterCellAssign(buf);
        Assert.That(d.ClusterChunkId, Is.EqualTo(99));
        Assert.That(d.CellKey, Is.EqualTo(7));
        Assert.That(d.ArchetypeId, Is.EqualTo(42));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:Cell:Index (kinds 130-132) — instant events
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SpatialCellIndexAdd_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialCellIndexEventCodec.AddSize];
        SpatialCellIndexEventCodec.WriteAdd(buf, ThreadSlot, StartTs, cellKey: 5, slot: 3, clusterChunkId: 100, capacity: 16);
        var d = SpatialCellIndexEventCodec.DecodeAdd(buf);
        Assert.That(d.CellKey, Is.EqualTo(5));
        Assert.That(d.Slot, Is.EqualTo(3));
        Assert.That(d.ClusterChunkId, Is.EqualTo(100));
        Assert.That(d.Capacity, Is.EqualTo(16));
    }

    [Test]
    public void SpatialCellIndexUpdate_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialCellIndexEventCodec.UpdateSize];
        SpatialCellIndexEventCodec.WriteUpdate(buf, ThreadSlot, StartTs, cellKey: 5, slot: 3);
        var d = SpatialCellIndexEventCodec.DecodeUpdate(buf);
        Assert.That(d.CellKey, Is.EqualTo(5));
        Assert.That(d.Slot, Is.EqualTo(3));
    }

    [Test]
    public void SpatialCellIndexRemove_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialCellIndexEventCodec.RemoveSize];
        SpatialCellIndexEventCodec.WriteRemove(buf, ThreadSlot, StartTs, cellKey: 5, slot: 3, swappedClusterId: 99);
        var d = SpatialCellIndexEventCodec.DecodeRemove(buf);
        Assert.That(d.CellKey, Is.EqualTo(5));
        Assert.That(d.Slot, Is.EqualTo(3));
        Assert.That(d.SwappedClusterId, Is.EqualTo(99));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:ClusterMigration (kinds 133-135) — instant events
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SpatialClusterMigrationDetect_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialClusterMigrationEventCodec.DetectSize];
        SpatialClusterMigrationEventCodec.WriteDetect(buf, ThreadSlot, StartTs, archetypeId: 7, clusterChunkId: 99, oldCellKey: 1, newCellKey: 2);
        var d = SpatialClusterMigrationEventCodec.DecodeDetect(buf);
        Assert.That(d.ArchetypeId, Is.EqualTo(7));
        Assert.That(d.ClusterChunkId, Is.EqualTo(99));
        Assert.That(d.OldCellKey, Is.EqualTo(1));
        Assert.That(d.NewCellKey, Is.EqualTo(2));
    }

    [Test]
    public void SpatialClusterMigrationQueue_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialClusterMigrationEventCodec.QueueSize];
        SpatialClusterMigrationEventCodec.WriteQueue(buf, ThreadSlot, StartTs, archetypeId: 7, clusterChunkId: 99, queueLen: 5);
        var d = SpatialClusterMigrationEventCodec.DecodeQueue(buf);
        Assert.That(d.ArchetypeId, Is.EqualTo(7));
        Assert.That(d.ClusterChunkId, Is.EqualTo(99));
        Assert.That(d.QueueLen, Is.EqualTo(5));
    }

    [Test]
    public void SpatialClusterMigrationHysteresis_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialClusterMigrationEventCodec.HysteresisSize];
        SpatialClusterMigrationEventCodec.WriteHysteresis(buf, ThreadSlot, StartTs, archetypeId: 7, clusterChunkId: 99, escapeDistSq: 0.25f);
        var d = SpatialClusterMigrationEventCodec.DecodeHysteresis(buf);
        Assert.That(d.ArchetypeId, Is.EqualTo(7));
        Assert.That(d.ClusterChunkId, Is.EqualTo(99));
        Assert.That(d.EscapeDistSq, Is.EqualTo(0.25f));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:TierIndex (kinds 136-137)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SpatialTierIndexRebuild_RoundTrip()
    {
        var ev = new SpatialTierIndexRebuildEvent
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
            ArchetypeId = 12,
            ClusterCount = 200,
            OldVersion = 5,
            NewVersion = 7,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialTierIndexEventCodec.DecodeRebuild(buf);
        Assert.That(d.ArchetypeId, Is.EqualTo(12));
        Assert.That(d.ClusterCount, Is.EqualTo(200));
        Assert.That(d.OldVersion, Is.EqualTo(5));
        Assert.That(d.NewVersion, Is.EqualTo(7));
    }

    [Test]
    public void SpatialTierIndexVersionSkip_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialTierIndexEventCodec.VersionSkipSize];
        SpatialTierIndexEventCodec.WriteVersionSkip(buf, ThreadSlot, StartTs, archetypeId: 12, version: 7, reason: 1);
        var d = SpatialTierIndexEventCodec.DecodeVersionSkip(buf);
        Assert.That(d.ArchetypeId, Is.EqualTo(12));
        Assert.That(d.Version, Is.EqualTo(7));
        Assert.That(d.Reason, Is.EqualTo(1));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:Maintain (kinds 138-141)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SpatialMaintainInsert_RoundTrip()
    {
        var ev = new SpatialMaintainInsertEvent
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
            EntityPK = 0xCAFEL,
            ComponentTypeId = 5,
            DidDegenerate = 0,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialMaintainEventCodec.DecodeInsert(buf);
        Assert.That(d.EntityPK, Is.EqualTo(0xCAFEL));
        Assert.That(d.ComponentTypeId, Is.EqualTo(5));
        Assert.That(d.DidDegenerate, Is.EqualTo(0));
    }

    [Test]
    public void SpatialMaintainUpdateSlowPath_RoundTrip()
    {
        var ev = new SpatialMaintainUpdateSlowPathEvent
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
            EntityPK = 0xBEEFL,
            ComponentTypeId = 3,
            EscapeDistSq = 1.5f,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialMaintainEventCodec.DecodeUpdateSlowPath(buf);
        Assert.That(d.EntityPK, Is.EqualTo(0xBEEFL));
        Assert.That(d.ComponentTypeId, Is.EqualTo(3));
        Assert.That(d.EscapeDistSq, Is.EqualTo(1.5f));
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    [TestCase((byte)2)]
    public void SpatialMaintainAabbValidate_RoundTrip(byte opcode)
    {
        Span<byte> buf = stackalloc byte[SpatialMaintainEventCodec.AabbValidateSize];
        SpatialMaintainEventCodec.WriteAabbValidate(buf, ThreadSlot, StartTs, entityPK: 0x1234L, componentTypeId: 8, opcode);
        var d = SpatialMaintainEventCodec.DecodeAabbValidate(buf);
        Assert.That(d.EntityPK, Is.EqualTo(0x1234L));
        Assert.That(d.ComponentTypeId, Is.EqualTo(8));
        Assert.That(d.Opcode, Is.EqualTo(opcode));
    }

    [Test]
    public void SpatialMaintainBackPointerWrite_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialMaintainEventCodec.BackPointerWriteSize];
        SpatialMaintainEventCodec.WriteBackPointerWrite(buf, ThreadSlot, StartTs, componentChunkId: 17, leafChunkId: 22, slotIndex: 9);
        var d = SpatialMaintainEventCodec.DecodeBackPointerWrite(buf);
        Assert.That(d.ComponentChunkId, Is.EqualTo(17));
        Assert.That(d.LeafChunkId, Is.EqualTo(22));
        Assert.That(d.SlotIndex, Is.EqualTo(9));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spatial:Trigger (kinds 142-145)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void SpatialTriggerRegion_RoundTrip(byte op)
    {
        Span<byte> buf = stackalloc byte[SpatialTriggerEventCodec.RegionSize];
        SpatialTriggerEventCodec.WriteRegion(buf, ThreadSlot, StartTs, op, regionId: 5, categoryMask: 0xFF00FF00u);
        var d = SpatialTriggerEventCodec.DecodeRegion(buf);
        Assert.That(d.Op, Is.EqualTo(op));
        Assert.That(d.RegionId, Is.EqualTo(5));
        Assert.That(d.CategoryMask, Is.EqualTo(0xFF00FF00u));
    }

    [Test]
    public void SpatialTriggerEval_RoundTrip()
    {
        var ev = new SpatialTriggerEvalEvent
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
            RegionId = 5,
            OccupantCount = 50,
            EnterCount = 3,
            LeaveCount = 2,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SpatialTriggerEventCodec.DecodeEval(buf);
        Assert.That(d.RegionId, Is.EqualTo(5));
        Assert.That(d.OccupantCount, Is.EqualTo(50));
        Assert.That(d.EnterCount, Is.EqualTo(3));
        Assert.That(d.LeaveCount, Is.EqualTo(2));
    }

    [Test]
    public void SpatialTriggerOccupantDiff_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialTriggerEventCodec.OccupantDiffSize];
        SpatialTriggerEventCodec.WriteOccupantDiff(buf, ThreadSlot, StartTs, regionId: 5, prevCount: 10, currCount: 12, enterCount: 3, leaveCount: 1);
        var d = SpatialTriggerEventCodec.DecodeOccupantDiff(buf);
        Assert.That(d.RegionId, Is.EqualTo(5));
        Assert.That(d.PrevCount, Is.EqualTo(10));
        Assert.That(d.CurrCount, Is.EqualTo(12));
        Assert.That(d.EnterCount, Is.EqualTo(3));
        Assert.That(d.LeaveCount, Is.EqualTo(1));
    }

    [Test]
    public void SpatialTriggerCacheInvalidate_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SpatialTriggerEventCodec.CacheInvalidateSize];
        SpatialTriggerEventCodec.WriteCacheInvalidate(buf, ThreadSlot, StartTs, regionId: 5, oldVersion: 100, newVersion: 101);
        var d = SpatialTriggerEventCodec.DecodeCacheInvalidate(buf);
        Assert.That(d.RegionId, Is.EqualTo(5));
        Assert.That(d.OldVersion, Is.EqualTo(100));
        Assert.That(d.NewVersion, Is.EqualTo(101));
    }
}
