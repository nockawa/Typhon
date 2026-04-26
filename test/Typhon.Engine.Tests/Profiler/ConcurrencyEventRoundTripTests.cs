using NUnit.Framework;
using System;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for all 27 Concurrency event codecs added in Phase 2 (#280).
/// Each test calls the codec's Write* method against a stack-allocated buffer, then calls Decode*
/// and asserts the decoded payload matches the inputs. The Tier-2 gate is bypassed by going
/// straight to the codec — Q4 Option C from the design debate.
/// </summary>
[TestFixture]
public class ConcurrencyEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long Timestamp = 1_234_567_890L;

    // ─────────────────────────────────────────────────────────────────────
    // AccessControl (kinds 90–95)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(TraceEventKind.ConcurrencyAccessControlSharedAcquire, false, (ushort)0)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlSharedAcquire, true, (ushort)1234)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlExclusiveAcquire, false, (ushort)0)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlExclusiveAcquire, true, (ushort)65535)]
    public void AccessControlAcquire_RoundTrip(TraceEventKind kind, bool hadToWait, ushort elapsedUs)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAccessControlEventCodec.AcquireSize];
        ConcurrencyAccessControlEventCodec.WriteAcquire(buf, kind, ThreadSlot, Timestamp,
            threadId: 42, hadToWait, elapsedUs);

        var d = ConcurrencyAccessControlEventCodec.DecodeAcquire(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Kind, Is.EqualTo(kind));
            Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
            Assert.That(d.Timestamp, Is.EqualTo(Timestamp));
            Assert.That(d.ThreadId, Is.EqualTo(42));
            Assert.That(d.HadToWait, Is.EqualTo(hadToWait));
            Assert.That(d.ElapsedUs, Is.EqualTo(elapsedUs));
        });
    }

    [TestCase(TraceEventKind.ConcurrencyAccessControlSharedRelease)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlExclusiveRelease)]
    public void AccessControlRelease_RoundTrip(TraceEventKind kind)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAccessControlEventCodec.ReleaseSize];
        ConcurrencyAccessControlEventCodec.WriteRelease(buf, kind, ThreadSlot, Timestamp, threadId: 99);

        var d = ConcurrencyAccessControlEventCodec.DecodeRelease(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Kind, Is.EqualTo(kind));
            Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
            Assert.That(d.Timestamp, Is.EqualTo(Timestamp));
            Assert.That(d.ThreadId, Is.EqualTo(99));
        });
    }

    [TestCase((ushort)0, (byte)0)]
    [TestCase((ushort)1500, (byte)1)]
    public void AccessControlPromotion_RoundTrip(ushort elapsedUs, byte variant)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAccessControlEventCodec.PromotionSize];
        ConcurrencyAccessControlEventCodec.WritePromotion(buf, ThreadSlot, Timestamp, elapsedUs, variant);

        var d = ConcurrencyAccessControlEventCodec.DecodePromotion(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
            Assert.That(d.Timestamp, Is.EqualTo(Timestamp));
            Assert.That(d.ElapsedUs, Is.EqualTo(elapsedUs));
            Assert.That(d.Variant, Is.EqualTo(variant));
        });
    }

    [Test]
    public void AccessControlContention_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAccessControlEventCodec.ContentionSize];
        ConcurrencyAccessControlEventCodec.WriteContention(buf, ThreadSlot, Timestamp);

        var d = ConcurrencyAccessControlEventCodec.DecodeContention(buf);
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.Timestamp, Is.EqualTo(Timestamp));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AccessControlSmall (kinds 96–100)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(TraceEventKind.ConcurrencyAccessControlSmallSharedAcquire)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlSmallSharedRelease)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlSmallExclusiveAcquire)]
    [TestCase(TraceEventKind.ConcurrencyAccessControlSmallExclusiveRelease)]
    public void AccessControlSmallEvent_RoundTrip(TraceEventKind kind)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAccessControlSmallEventCodec.EventSize];
        ConcurrencyAccessControlSmallEventCodec.WriteEvent(buf, kind, ThreadSlot, Timestamp, threadId: 1234);

        var d = ConcurrencyAccessControlSmallEventCodec.DecodeEvent(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Kind, Is.EqualTo(kind));
            Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
            Assert.That(d.Timestamp, Is.EqualTo(Timestamp));
            Assert.That(d.ThreadId, Is.EqualTo(1234));
        });
    }

    [Test]
    public void AccessControlSmallContention_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAccessControlSmallEventCodec.ContentionSize];
        ConcurrencyAccessControlSmallEventCodec.WriteContention(buf, ThreadSlot, Timestamp);

        var d = ConcurrencyAccessControlSmallEventCodec.DecodeContention(buf);
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.Timestamp, Is.EqualTo(Timestamp));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ResourceAccessControl (kinds 101–105)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(true, (byte)5, (ushort)0)]
    [TestCase(false, (byte)255, (ushort)9999)]
    public void ResourceAccessing_RoundTrip(bool success, byte accessingCount, ushort elapsedUs)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyResourceAccessControlEventCodec.AccessingSize];
        ConcurrencyResourceAccessControlEventCodec.WriteAccessing(buf, ThreadSlot, Timestamp,
            success, accessingCount, elapsedUs);

        var d = ConcurrencyResourceAccessControlEventCodec.DecodeAccessing(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Success, Is.EqualTo(success));
            Assert.That(d.AccessingCount, Is.EqualTo(accessingCount));
            Assert.That(d.ElapsedUs, Is.EqualTo(elapsedUs));
        });
    }

    [TestCase(true, (ushort)42, (ushort)0)]
    [TestCase(false, (ushort)0, (ushort)1500)]
    public void ResourceModify_RoundTrip(bool success, ushort threadId, ushort elapsedUs)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyResourceAccessControlEventCodec.ModifySize];
        ConcurrencyResourceAccessControlEventCodec.WriteModify(buf, ThreadSlot, Timestamp,
            success, threadId, elapsedUs);

        var d = ConcurrencyResourceAccessControlEventCodec.DecodeModify(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Success, Is.EqualTo(success));
            Assert.That(d.ThreadId, Is.EqualTo(threadId));
            Assert.That(d.ElapsedUs, Is.EqualTo(elapsedUs));
        });
    }

    [TestCase(true, (ushort)0)]
    [TestCase(false, (ushort)2500)]
    public void ResourceDestroy_RoundTrip(bool success, ushort elapsedUs)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyResourceAccessControlEventCodec.DestroySize];
        ConcurrencyResourceAccessControlEventCodec.WriteDestroy(buf, ThreadSlot, Timestamp, success, elapsedUs);

        var d = ConcurrencyResourceAccessControlEventCodec.DecodeDestroy(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Success, Is.EqualTo(success));
            Assert.That(d.ElapsedUs, Is.EqualTo(elapsedUs));
        });
    }

    [TestCase((ushort)0)]
    [TestCase((ushort)45000)]
    public void ResourceModifyPromotion_RoundTrip(ushort elapsedUs)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyResourceAccessControlEventCodec.ModifyPromotionSize];
        ConcurrencyResourceAccessControlEventCodec.WriteModifyPromotion(buf, ThreadSlot, Timestamp, elapsedUs);

        var d = ConcurrencyResourceAccessControlEventCodec.DecodeModifyPromotion(buf);
        Assert.That(d.ElapsedUs, Is.EqualTo(elapsedUs));
    }

    [Test]
    public void ResourceContention_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyResourceAccessControlEventCodec.ContentionSize];
        ConcurrencyResourceAccessControlEventCodec.WriteContention(buf, ThreadSlot, Timestamp);

        var d = ConcurrencyResourceAccessControlEventCodec.DecodeContention(buf);
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Epoch (kinds 106–111)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(0u, (byte)0, true)]
    [TestCase(uint.MaxValue, (byte)255, false)]
    public void EpochScopeEnter_RoundTrip(uint epoch, byte depthBefore, bool isDormantToActive)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyEpochEventCodec.ScopeEnterSize];
        ConcurrencyEpochEventCodec.WriteScopeEnter(buf, ThreadSlot, Timestamp, epoch, depthBefore, isDormantToActive);

        var d = ConcurrencyEpochEventCodec.DecodeScopeEnter(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Epoch, Is.EqualTo(epoch));
            Assert.That(d.DepthBefore, Is.EqualTo(depthBefore));
            Assert.That(d.IsDormantToActive, Is.EqualTo(isDormantToActive));
        });
    }

    [TestCase(123u, true)]
    [TestCase(0u, false)]
    public void EpochScopeExit_RoundTrip(uint epoch, bool isOutermost)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyEpochEventCodec.ScopeExitSize];
        ConcurrencyEpochEventCodec.WriteScopeExit(buf, ThreadSlot, Timestamp, epoch, isOutermost);

        var d = ConcurrencyEpochEventCodec.DecodeScopeExit(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.Epoch, Is.EqualTo(epoch));
            Assert.That(d.IsOutermost, Is.EqualTo(isOutermost));
        });
    }

    [TestCase(1u)]
    [TestCase(uint.MaxValue)]
    public void EpochAdvance_RoundTrip(uint newEpoch)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyEpochEventCodec.AdvanceSize];
        ConcurrencyEpochEventCodec.WriteAdvance(buf, ThreadSlot, Timestamp, newEpoch);

        var d = ConcurrencyEpochEventCodec.DecodeAdvance(buf);
        Assert.That(d.NewEpoch, Is.EqualTo(newEpoch));
    }

    [Test]
    public void EpochRefresh_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyEpochEventCodec.RefreshSize];
        ConcurrencyEpochEventCodec.WriteRefresh(buf, ThreadSlot, Timestamp, oldEpoch: 100, newEpoch: 101);

        var d = ConcurrencyEpochEventCodec.DecodeRefresh(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.OldEpoch, Is.EqualTo(100u));
            Assert.That(d.NewEpoch, Is.EqualTo(101u));
        });
    }

    [Test]
    public void EpochSlotClaim_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyEpochEventCodec.SlotClaimSize];
        ConcurrencyEpochEventCodec.WriteSlotClaim(buf, ThreadSlot, Timestamp,
            slotIndex: 5, threadId: 1234, activeCount: 17);

        var d = ConcurrencyEpochEventCodec.DecodeSlotClaim(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.SlotIndex, Is.EqualTo(5));
            Assert.That(d.ThreadId, Is.EqualTo(1234));
            Assert.That(d.ActiveCount, Is.EqualTo(17));
        });
    }

    [Test]
    public void EpochSlotReclaim_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyEpochEventCodec.SlotReclaimSize];
        ConcurrencyEpochEventCodec.WriteSlotReclaim(buf, ThreadSlot, Timestamp,
            slotIndex: 8, oldOwner: 100, newOwner: 200);

        var d = ConcurrencyEpochEventCodec.DecodeSlotReclaim(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.SlotIndex, Is.EqualTo(8));
            Assert.That(d.OldOwner, Is.EqualTo(100));
            Assert.That(d.NewOwner, Is.EqualTo(200));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AdaptiveWaiter (kind 112)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((ushort)10, AdaptiveWaiterTransitionKind.Yield)]
    [TestCase((ushort)50, AdaptiveWaiterTransitionKind.Sleep)]
    public void AdaptiveWaiterYieldOrSleep_RoundTrip(ushort spinCountBefore, AdaptiveWaiterTransitionKind kind)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyAdaptiveWaiterEventCodec.EventSize];
        ConcurrencyAdaptiveWaiterEventCodec.WriteYieldOrSleep(buf, ThreadSlot, Timestamp, spinCountBefore, kind);

        var d = ConcurrencyAdaptiveWaiterEventCodec.DecodeYieldOrSleep(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.SpinCountBefore, Is.EqualTo(spinCountBefore));
            Assert.That(d.Kind, Is.EqualTo(kind));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // OlcLatch (kinds 113–116)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(0u, false)]
    [TestCase(uint.MaxValue, true)]
    public void OlcLatchWriteLockAttempt_RoundTrip(uint versionBefore, bool success)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyOlcLatchEventCodec.WriteLockAttemptSize];
        ConcurrencyOlcLatchEventCodec.WriteWriteLockAttempt(buf, ThreadSlot, Timestamp, versionBefore, success);

        var d = ConcurrencyOlcLatchEventCodec.DecodeWriteLockAttempt(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.VersionBefore, Is.EqualTo(versionBefore));
            Assert.That(d.Success, Is.EqualTo(success));
        });
    }

    [Test]
    public void OlcLatchWriteUnlock_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyOlcLatchEventCodec.WriteUnlockSize];
        ConcurrencyOlcLatchEventCodec.WriteWriteUnlock(buf, ThreadSlot, Timestamp,
            oldVersion: 5, newVersion: 9);

        var d = ConcurrencyOlcLatchEventCodec.DecodeWriteUnlock(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.OldVersion, Is.EqualTo(5u));
            Assert.That(d.NewVersion, Is.EqualTo(9u));
        });
    }

    [TestCase(0u)]
    [TestCase(uint.MaxValue)]
    public void OlcLatchMarkObsolete_RoundTrip(uint version)
    {
        Span<byte> buf = stackalloc byte[ConcurrencyOlcLatchEventCodec.MarkObsoleteSize];
        ConcurrencyOlcLatchEventCodec.WriteMarkObsolete(buf, ThreadSlot, Timestamp, version);

        var d = ConcurrencyOlcLatchEventCodec.DecodeMarkObsolete(buf);
        Assert.That(d.Version, Is.EqualTo(version));
    }

    [Test]
    public void OlcLatchValidationFail_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[ConcurrencyOlcLatchEventCodec.ValidationFailSize];
        ConcurrencyOlcLatchEventCodec.WriteValidationFail(buf, ThreadSlot, Timestamp,
            expectedVersion: 100, actualVersion: 101);

        var d = ConcurrencyOlcLatchEventCodec.DecodeValidationFail(buf);
        Assert.Multiple(() =>
        {
            Assert.That(d.ExpectedVersion, Is.EqualTo(100u));
            Assert.That(d.ActualVersion, Is.EqualTo(101u));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Wire-format invariants
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void All_Concurrency_Kinds_Are_Classified_As_Instant()
    {
        for (var v = 90; v <= 116; v++)
        {
            var kind = (TraceEventKind)v;
            Assert.That(kind.IsSpan(), Is.False,
                $"Kind {kind} (numeric {v}) should be classified as instant — Phase 2 Concurrency events have no span header extension.");
        }
    }
}
