using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.internals;

/// <summary>
/// Defers the free of a disposed view's pinned delta buffer until no thread can still be holding a reference to it (#864).
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this replaces.</b> A commit publisher reads <c>reg.View.IsDisposed</c> and then writes 24 bytes through
/// <c>reg.DeltaBuffer</c>'s raw pointers. Two steps with nothing sequencing them, so a disposal landing in between frees the pinned block —
/// <c>NativeMemory.AlignedFree</c>, immediately re-issuable to any other allocation in the process — under an in-flight write. Silent heap
/// corruption, not a catchable exception.
/// </para>
/// <para>
/// <b>Why deferral rather than exclusion.</b> Excluding the disposer (a shared/exclusive latch around the publish pass) costs the publisher a
/// synchronised acquire on a path measured at ~15.6 ns per append, forces every publish site to hoist that acquire out of loops it is buried
/// three deep in, and still has to decide what to do when the exclusive acquire times out — where the only options are hanging <c>Dispose</c>
/// or doing the unsafe thing anyway. Deferral costs the publisher NOTHING: a late write lands in a ring nobody will drain, in memory that is
/// still mapped and still owned. Harmless by construction rather than by exclusion.
/// </para>
/// <para>
/// <b>Why the epoch is the right watermark.</b> Every publisher already runs inside an <c>EpochManager</c> scope — the commit path enters one
/// for the whole transaction, the fence path for the whole chunk — and none refreshes it mid-publish. So publishers already announce
/// themselves; this only reads the announcement. That is the same shape ADR-035 uses to defer MVCC content-chunk reclamation behind
/// <c>MinTSN</c>, against a watermark the fence path (which publishes with no TSN at all) actually has.
/// </para>
/// <para>
/// <b>The ordering this relies on</b> is documented on <c>EpochThreadRegistry.PinCurrentThread</c>: the pin store is sequentially consistent
/// and <see cref="Retire"/>'s stamp is an <c>Interlocked.Increment</c>, forming a Dekker pair. Either the reclaimer observes the publisher's
/// pin and defers, or the publisher observes the deregistration that preceded the stamp and never reaches the buffer.
/// </para>
/// </remarks>
internal sealed class ViewBufferReclaimer
{
    private readonly EpochManager _epochManager;
    private readonly Lock _gate = new();
    private readonly List<Entry> _pending = [];

    private long _freedTotal;
    private long _retiredTotal;

    internal ViewBufferReclaimer(EpochManager epochManager) => _epochManager = epochManager;

    private readonly struct Entry(ViewDeltaRingBuffer owner, PinnedMemoryBlock block, long retireEpoch)
    {
        public ViewDeltaRingBuffer Owner { get; } = owner;
        public PinnedMemoryBlock Block { get; } = block;
        public long RetireEpoch { get; } = retireEpoch;
    }

    /// <summary>Blocks retired but not yet freed. Surfaced because a cost nothing reports is a cost nobody can diagnose.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Total bytes held by retired-but-unfreed blocks.</summary>
    internal long PendingBytes
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                for (var i = 0; i < _pending.Count; i++)
                {
                    total += _pending[i].Block.MemoryBlockSize;
                }
                return total;
            }
        }
    }

    /// <summary>Blocks freed over this reclaimer's lifetime.</summary>
    internal long FreedTotal => Volatile.Read(ref _freedTotal);

    /// <summary>Blocks retired over this reclaimer's lifetime.</summary>
    internal long RetiredTotal => Volatile.Read(ref _retiredTotal);

    /// <summary>
    /// Takes ownership of a disposed view's block. The block stays MAPPED and its pointers stay valid, so a publisher that is already past its
    /// <c>IsDisposed</c> check writes into live memory.
    /// </summary>
    /// <remarks>
    /// The stamp must be taken AFTER the view has been removed from every registry. That ordering is what the safety argument turns on: a
    /// publisher whose registry read preceded the deregistration is guaranteed to have pinned an epoch below this stamp, and a publisher that
    /// pinned above it cannot have seen the registration at all.
    /// </remarks>
    internal void Retire(ViewDeltaRingBuffer owner, PinnedMemoryBlock block)
    {
        if (block == null || block.IsDisposed)
        {
            return;
        }

        // Interlocked, not a plain read of GlobalEpoch: this is the reclaimer half of the Dekker pair, and it must be a full fence so the
        // deregistration that preceded it cannot sink below the slot scan that Drain performs.
        var stamp = _epochManager.BumpEpochForRetire();

        lock (_gate)
        {
            _pending.Add(new Entry(owner, block, stamp));
            _retiredTotal++;
        }

        Drain();
    }

    /// <summary>
    /// Frees every retired block whose stamp is at or below the oldest epoch any thread is still pinned to.
    /// </summary>
    /// <remarks>
    /// Cheap and safe to call often — it early-outs on an empty list without computing the watermark, which is the common case on the tick path.
    /// </remarks>
    internal void Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var min = _epochManager.MinActiveEpoch;
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].RetireEpoch > min)
                {
                    continue;
                }

                // Null the owner's pointers BEFORE the free, so nothing can observe them pointing at memory the allocator is about to re-issue.
                _pending[i].Owner?.OnBlockReclaimed();
                _pending[i].Block.Dispose();
                _pending.RemoveAt(i);
                _freedTotal++;
            }
        }
    }

    /// <summary>
    /// Drops references to anything still pending, at engine teardown.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT free: each block is a registered child of the resource that allocated it, so the resource tree's own cascade
    /// disposes it. Freeing here as well would be the double-free that <c>PinnedMemoryBlock.Dispose</c> only survives by nulling its pointer.
    /// </remarks>
    internal void AbandonAll()
    {
        lock (_gate)
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                _pending[i].Owner?.OnBlockReclaimed();
            }
            _pending.Clear();
        }
    }
}
