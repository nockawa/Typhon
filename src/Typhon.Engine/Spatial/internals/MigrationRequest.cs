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
/// <para>16 bytes, four <see cref="int"/>s — a 1K-entry queue is 16 KB. It was 12 bytes at <c>Pack = 4</c> before step 10
/// added the destination cluster; four ints need no packing to sit end to end, and the natural alignment is what the
/// sort and the slice scan want.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MigrationRequest
{
    /// <summary>"Any cluster in the destination cell will do" — the cell-crossing case, and the value every pre-step-10 caller means.</summary>
    public const int AnyCluster = -1;

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

    public MigrationRequest(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey, int destClusterChunkId = AnyCluster)
    {
        SourceClusterChunkId = sourceClusterChunkId;
        SourceSlotIndex = sourceSlotIndex;
        DestCellKey = destCellKey;
        DestClusterChunkId = destClusterChunkId;
    }
}
