// unset

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Per-index statistics for selectivity estimation. Basic counts (<see cref="EntryCount"/>, <see cref="MinValue"/>, <see cref="MaxValue"/>) are read
/// live from the B+Tree metadata at zero maintenance cost. The optional <see cref="Histogram"/> provides finer-grained distribution data when rebuilt
/// explicitly — including an accurate total entity count for AllowMultiple indexes (see <see cref="Histogram.TotalCount"/>).
/// </summary>
internal class IndexStatistics
{
    private readonly BTreeBase _index;

    public IndexStatistics(BTreeBase index)
    {
        _index = index;
    }

    /// <summary>
    /// Number of leaf entries in the B+Tree, read live. For unique indexes this equals the entity count.
    /// For AllowMultiple indexes this is the number of <b>distinct keys</b>, not entities — use
    /// <see cref="Histogram"/>.<see cref="Histogram.TotalCount"/> for an accurate entity count after rebuild.
    /// </summary>
    public int EntryCount => _index.EntryCount;

    /// <summary>Minimum key encoded as long (same encoding as <see cref="QueryResolverHelper.EncodeThreshold"/>).</summary>
    public long MinValue => _index.GetMinKeyAsLong();

    /// <summary>Maximum key encoded as long.</summary>
    public long MaxValue => _index.GetMaxKeyAsLong();

    /// <summary>Estimated distinct values. Returns -1 (unknown) until HyperLogLog is implemented in Phase 3.</summary>
    public long DistinctValues => -1;

    /// <summary>Optional equi-width histogram. Null until <see cref="RebuildHistogram"/> is called.</summary>
    public Histogram Histogram { get; internal set; }

    /// <summary>The underlying B+Tree index.</summary>
    internal BTreeBase Index => _index;

    /// <summary>
    /// Rebuilds the histogram by scanning all leaf entries in the B+Tree.
    /// Cold path — acceptable to use type-switch dispatch.
    /// </summary>
    /// <remarks>
    /// <b>Scalability warning:</b> This performs a full O(N) leaf scan. Acceptable for Phase 2 (explicit, infrequent calls)
    /// but must be replaced with a scalable approach (sampling, incremental, or background streaming) before 1.0.
    /// See Phase 4 note in <c>claude/design/ViewSystem/phase-2.md</c>.
    /// </remarks>
    public void RebuildHistogram()
    {
        if (_index.EntryCount == 0)
        {
            Histogram = null;
            return;
        }

        long min = _index.GetMinKeyAsLong();
        long max = _index.GetMaxKeyAsLong();

        var bucketCounts = new int[Histogram.BucketCount];
        int totalCount = 0;

        // Type-switch to dispatch into the generic BTree<TKey>.EnumerateLeaves()
        switch (_index)
        {
            case BTree<sbyte> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<byte> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<short> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<ushort> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<char> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<int> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<uint> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<long> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<float> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            case BTree<double> tree:
                ScanAndBucket(tree, min, max, bucketCounts, ref totalCount);
                break;
            default:
                throw new NotSupportedException($"Histogram rebuild not supported for index type {_index.GetType().Name}.");
        }

        Histogram = new Histogram(min, max, bucketCounts, totalCount);
    }

    private static unsafe void ScanAndBucket<TKey>(BTree<TKey> tree, long min, long max, int[] bucketCounts, ref int totalCount)
        where TKey : unmanaged
    {
        long bucketWidth = (max == min) ? 0 : Math.Max(1, (max - min) / Histogram.BucketCount);
        bool allowMultiple = tree.AllowMultiple;

        using var guard = EpochGuard.Enter(tree.Segment.Manager.EpochManager);
        var accessor = allowMultiple ? tree.Segment.CreateChunkAccessor() : default;
        try
        {
            foreach (var kv in tree.EnumerateLeaves())
            {
                long encoded = BTree<TKey>.KeyToLong(kv.Key);
                int bucket;
                if (bucketWidth == 0)
                {
                    bucket = 0;
                }
                else
                {
                    long offset = encoded - min;
                    long b = offset / bucketWidth;
                    bucket = (int)Math.Clamp(b, 0, Histogram.BucketCount - 1);
                }

                // For AllowMultiple indexes, the leaf Value is a VSBS root chunk ID.
                // Read the buffer's TotalCount to weight this key by its actual entity count.
                int weight = 1;
                if (allowMultiple)
                {
                    int bufferRootChunkId = kv.Value;
                    byte* rootAddr = accessor.GetChunkAddress(bufferRootChunkId);
                    weight = Unsafe.AsRef<VariableSizedBufferRootHeader>(rootAddr).TotalCount;
                }

                bucketCounts[bucket] += weight;
                totalCount += weight;
            }
        }
        finally
        {
            if (allowMultiple)
            {
                accessor.Dispose();
            }
        }
    }
}
