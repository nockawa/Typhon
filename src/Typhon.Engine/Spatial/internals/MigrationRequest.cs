using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A queued migration request: move the entity currently living at
/// <c>(SourceClusterChunkId, SourceSlotIndex)</c> into a cluster attached to <see cref="DestCellKey"/> — optionally
/// into one specific cluster, named by <see cref="DestClusterChunkId"/>.
/// </summary>
/// <remarks>
/// <para>Populated during cell-crossing detection inside <c>DatabaseEngine.DetectClusterMigrations</c> and, since #872
/// step 10, by intra-cell drift detection; drained by <c>ArchetypeClusterState.ExecuteMigrations</c> at the tick fence.</para>
/// <para>20 bytes, five <see cref="int"/>s — a 1K-entry queue is 20 KB. It was 12 bytes at <c>Pack = 4</c> before step 10
/// added the destination cluster and step 12 the destination slot; five ints need no packing to sit end to end, and the
/// natural alignment is what the sort and the slice scan want. Both slot indices would fit a <see cref="byte"/>, which
/// would buy back the four bytes — not done, because narrowing <see cref="SourceSlotIndex"/> touches every existing
/// reader for 4 KB per thousand queued requests on a queue whose steady-state depth is the drifter count of one tick.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MigrationRequest
{
    /// <summary>"Any cluster in the destination cell will do" — the cell-crossing case, and the value every pre-step-10 caller means.</summary>
    public const int AnyCluster = -1;

    /// <summary>"Any free slot in the destination cluster will do" — what a step-10 relocation means, and the value every caller but repair passes.</summary>
    public const int AnySlot = -1;

    /// <summary>Cluster chunk id of the entity's current (pre-migration) slot.</summary>
    public readonly int SourceClusterChunkId;

    /// <summary>Slot index within <see cref="SourceClusterChunkId"/>.</summary>
    public readonly int SourceSlotIndex;

    /// <summary>Target cell key the entity should land in after migration.</summary>
    public readonly int DestCellKey;

    /// <summary>
    /// The cluster to land in, or <see cref="AnyCluster"/> to let <c>ClaimSlotInCell</c> pick by first fit.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a cell key is not enough.</b> <c>ClaimSlotInCell</c> scans the destination cell's cluster list from a cursor and takes the first
    /// cluster with a free slot — it has no AABB awareness, and for an INTRA-cell relocation the source cluster is in that same list and usually has a
    /// free slot. So the claim would frequently hand the entity straight back to the cluster it was drifting out of. It cannot return the same SLOT (the
    /// source bit is still set), so nothing corrupts; the relocation is simply a no-op, and the least-enlargement choice that detection just computed is
    /// silently discarded. First-fit placement is the very thing #872 exists to repair, so leaving the destination to it would make step 10 a measurement
    /// of nothing.</para>
    /// <para><b>A pin is a preference, not a guarantee.</b> Between detection and the drain the pinned cluster can fill up, or be drained and freed.
    /// Execution falls back to <c>ClaimSlotInCell</c> in that case: a worse slot is a worse AABB, which costs selectivity, while failing the migration
    /// would strand the entity in a cluster it no longer belongs to.</para>
    /// </remarks>
    public readonly int DestClusterChunkId;

    /// <summary>
    /// The exact slot to land in within <see cref="DestClusterChunkId"/>, or <see cref="AnySlot"/> to take the cluster's first free slot.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the repair path (#872 step 12) sets this.</b> A full cell re-sort computes the ENTIRE destination layout up front — sort order
    /// determines which cluster and which slot every entity lands in — so the placement is an output of the planner, not of the claim.</para>
    /// <para><b>What would break it is the SORT, not the slicing.</b> Before the Migrate phase dispatches,
    /// <c>ArchetypeClusterState.SortPendingMigrationsByDestCellKey</c> runs an <c>Array.Sort</c> — introsort, <b>unstable</b> — over a comparer that reads
    /// <c>DestCellKey</c> alone. Every request a repair emits for one cell compares equal, so the planner's emission order within that cell is permuted
    /// arbitrarily, and first fit would then assign slots in the permuted order. That sort runs only on the parallel path
    /// (<c>FenceExecSystem</c>), so the serial and parallel fences would produce different packings from identical input — which is exactly what
    /// <c>AC-12.4</c> forbids. Pinning the slot makes the packing independent of it.</para>
    /// <para><i>Slicing is NOT the reason, and an earlier version of this comment said it was.</i>
    /// <c>FenceWorkPlan.EmitMigrationApplyItems</c> advances each slice boundary until <c>DestCellKey</c> changes, so one cell's run is never split across
    /// workers and two workers can never claim into the same fresh cluster. Stated because the wrong reason is worse than none: whoever makes that sort
    /// stable would, from it, correctly conclude this field is dead and delete it.</para>
    /// <para><b>Still a preference, like the cluster pin.</b> The planner allocates its fresh clusters during Prep and publishes them into
    /// <c>CellClusterPool</c> immediately, so a same-tick cell-crossing migration whose own pinned cluster is full can fall through to
    /// <c>ClaimSlotInCell</c>'s first fit and take a slot this plan reserved. A previously-queued request can also have emptied a source slot, in which case
    /// the repair request is skipped and leaves a hole. The fallback chain is exact slot → the pinned cluster's first free slot → <c>ClaimSlotInCell</c>,
    /// each step a worse layout and none of them wrong.</para>
    /// </remarks>
    public readonly int DestSlotIndex;

    public MigrationRequest(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey, int destClusterChunkId = AnyCluster, int destSlotIndex = AnySlot)
    {
        SourceClusterChunkId = sourceClusterChunkId;
        SourceSlotIndex = sourceSlotIndex;
        DestCellKey = destCellKey;
        DestClusterChunkId = destClusterChunkId;
        DestSlotIndex = destSlotIndex;
    }
}
