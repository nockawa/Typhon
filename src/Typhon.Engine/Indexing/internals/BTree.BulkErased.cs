using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The key-erased half of the bulk value update: what the tick fence's IndexMassUpdate phase calls, and the single place the raw staging bytes become typed
/// entries (#872 step 6).
/// </summary>
internal abstract partial class BTree<TKey, TStore>
{
    public override int BulkEntryStride(bool multi) => multi ? Unsafe.SizeOf<BTreeMultiValueUpdate<TKey>>() : Unsafe.SizeOf<BTreeValueUpdate<TKey>>();

    public override unsafe void WriteBulkEntry(Span<byte> dest, void* keyAddr, int newValue)
    {
        AssertWholeEntries(dest.Length, false, nameof(dest));
        MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(dest)[0] = new BTreeValueUpdate<TKey>(Unsafe.Read<TKey>(keyAddr), newValue);
    }

    public override unsafe void WriteBulkMultiEntry(Span<byte> dest, void* keyAddr, int elementId, int oldValue, int newValue)
    {
        AssertWholeEntries(dest.Length, true, nameof(dest));
        MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(dest)[0]
            = new BTreeMultiValueUpdate<TKey>(Unsafe.Read<TKey>(keyAddr), elementId, oldValue, newValue);
    }

    /// <summary>
    /// Asserts that a staging span holds a whole number of entries at this tree's stride.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/> truncates, silently.</b> A span one byte short of a whole entry casts to a span with one
    /// FEWER entry and no error anywhere, so a stride the caller got wrong turns into entries this surface never sees: the sort leaves a trailing entry
    /// unsorted in a buffer it then declares sorted, the partitioning descent asserts on that order and applies to the wrong leaf, and the merge writes fewer
    /// entries than the caller sized its destination for. <see cref="BulkEntryStride"/> exists precisely because producers get the layout wrong — a 4-byte
    /// key packs to 8 with its value while an 8-byte key pads to 16 — and until now the surface consuming that stride never checked the arithmetic came back.
    /// </para>
    /// <para>
    /// <b>Debug-only, unlike <c>ExclusiveWindow</c>, and the difference is the threat model.</b> That guard is always compiled because what it catches is a
    /// foreign thread the engine cannot see coming. Every span reaching here is assembled by engine code in <c>IndexUpdateStaging</c> and
    /// <c>FenceExecSystem</c> from a stride this same tree object handed out, with no user-input path — so a mismatch is a programming error a Debug suite
    /// run catches, not a runtime condition. Checked at all six entry points rather than the two that write, because a half-checked erased surface reads as
    /// if the unchecked half had been considered safe.
    /// </para>
    /// </remarks>
    [Conditional("DEBUG")]
    private void AssertWholeEntries(int byteLength, bool multi, string paramName)
    {
        var stride = BulkEntryStride(multi);
        Debug.Assert(byteLength % stride == 0,
            $"{paramName}: {byteLength} bytes is not a whole number of {(multi ? "multi" : "unique")} entries at stride {stride}. MemoryMarshal.Cast would "
            + $"silently drop the partial tail — see AssertWholeEntries.");
    }

    /// <remarks>
    /// <b>The sort need not be stable, and the reason is worth stating because the opposite is the usual assumption.</b> Two entries sharing a key name
    /// different elements of that key's buffer and carry their own <c>OldValue</c>, so they update disjoint slots and the order between them cannot change
    /// the result. On a unique index a key appears at most once per batch to begin with. That also means <c>AC-6.4</c>'s determinism survives a change in W
    /// reordering equal keys: the applied set is the same whichever order they arrive in.
    /// </remarks>
    public override void SortBulkEntries(Span<byte> entries, bool multi)
    {
        AssertWholeEntries(entries.Length, multi, nameof(entries));
        if (multi)
        {
            MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(entries).Sort(new MultiEntryComparer(Comparer));
        }
        else
        {
            MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(entries).Sort(new UniqueEntryComparer(Comparer));
        }
    }

    public override void MergeBulkRuns(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest, bool multi)
    {
        AssertWholeEntries(a.Length, multi, nameof(a));
        AssertWholeEntries(b.Length, multi, nameof(b));

        // The destination must hold exactly the two runs. Sized by the caller from lengths it tracks itself, so a truncating cast here would not merely drop
        // a tail — it would leave the caller believing it had gathered entries that are no longer anywhere.
        Debug.Assert(dest.Length == a.Length + b.Length,
            $"MergeBulkRuns destination is {dest.Length} bytes for runs of {a.Length} + {b.Length}.");

        if (multi)
        {
            MergeRuns<BTreeMultiValueUpdate<TKey>, MultiApplier>(a, b, dest);
        }
        else
        {
            MergeRuns<BTreeValueUpdate<TKey>, UniqueApplier>(a, b, dest);
        }
    }

    /// <remarks>
    /// <b>Stable, and deliberately so even though the apply does not need it.</b> Taking from the left run on a tie keeps entries that share a key in the
    /// order the producer emitted them, which makes the merged batch a pure function of the per-chunk runs and their order — so the same migration set
    /// produces the same bytes whatever W was, which is what <c>AC-6.4</c> asks for. An unstable tie-break would still be correct (entries sharing a key
    /// name different elements) but would make the batch, and therefore the leaf-snapped partition computed from it, depend on the chunk count.
    /// </remarks>
    private void MergeRuns<TEntry, TApplier>(ReadOnlySpan<byte> aBytes, ReadOnlySpan<byte> bBytes, Span<byte> destBytes)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        var a = MemoryMarshal.Cast<byte, TEntry>(aBytes);
        var b = MemoryMarshal.Cast<byte, TEntry>(bBytes);
        var dest = MemoryMarshal.Cast<byte, TEntry>(destBytes);
        TApplier applier = default;

        var i = 0;
        var j = 0;
        var k = 0;
        while (i < a.Length && j < b.Length)
        {
            if (CompareKeys(applier.KeyOf(a[i]), applier.KeyOf(b[j]), Comparer) <= 0)
            {
                dest[k++] = a[i++];
            }
            else
            {
                dest[k++] = b[j++];
            }
        }

        while (i < a.Length)
        {
            dest[k++] = a[i++];
        }

        while (j < b.Length)
        {
            dest[k++] = b[j++];
        }
    }

    public override int PartitionBulkEntries(ReadOnlySpan<byte> entries, bool multi, int desiredParts, Span<int> boundaries,
        ref ChunkAccessor<TStore> accessor)
    {
        AssertWholeEntries(entries.Length, multi, nameof(entries));
        return multi
            ? PartitionByLeafBoundaries(MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(entries), desiredParts, boundaries, ref accessor)
            : PartitionByLeafBoundaries(MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(entries), desiredParts, boundaries, ref accessor);
    }

    public override int ApplyBulkEntries(ReadOnlySpan<byte> entries, bool multi, ref ChunkAccessor<TStore> accessor, out BulkUpdateStats stats)
    {
        AssertWholeEntries(entries.Length, multi, nameof(entries));
        return multi
            ? UpdateValues(MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(entries), ref accessor, out stats)
            : UpdateValues(MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(entries), ref accessor, out stats);
    }

    /// <remarks>
    /// A struct comparer rather than a <see cref="Comparison{T}"/>: the JIT monomorphises <c>Span.Sort</c> on it, so the key comparison inlines and nothing
    /// allocates. A lambda would allocate a delegate per call and dispatch it once per comparison, on a path that runs once per indexed field per tick.
    /// </remarks>
    private readonly struct UniqueEntryComparer : IComparer<BTreeValueUpdate<TKey>>
    {
        private readonly IComparer<TKey> _comparer;

        public UniqueEntryComparer(IComparer<TKey> comparer) => _comparer = comparer;

        public int Compare(BTreeValueUpdate<TKey> x, BTreeValueUpdate<TKey> y) => CompareKeys(x.Key, y.Key, _comparer);
    }

    /// <inheritdoc cref="UniqueEntryComparer"/>
    private readonly struct MultiEntryComparer : IComparer<BTreeMultiValueUpdate<TKey>>
    {
        private readonly IComparer<TKey> _comparer;

        public MultiEntryComparer(IComparer<TKey> comparer) => _comparer = comparer;

        public int Compare(BTreeMultiValueUpdate<TKey> x, BTreeMultiValueUpdate<TKey> y) => CompareKeys(x.Key, y.Key, _comparer);
    }
}
