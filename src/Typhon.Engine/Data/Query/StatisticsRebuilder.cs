// unset

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Rebuilds HLL, MCV, and Histogram statistics for all indexed fields of a ComponentTable in a single chunk-based scan using page-granularity sampling.
/// </summary>
/// <remarks>
/// <para>
/// The scan iterates pages of the ComponentSegment directly, reading the L0 bitmap to find occupied chunks and extracting field values via pointer arithmetic.
/// This avoids B+Tree traversal overhead and processes all indexed fields per entity in one pass.
/// </para>
/// <para>
/// After building new statistics structures, references are atomic-swapped on the IndexStatistics array, ensuring concurrent query threads never see torn data.
/// </para>
/// </remarks>
internal static class StatisticsRebuilder
{
    /// <summary>
    /// Rebuilds HLL, MCV, and Histogram for ALL indexed fields of a ComponentTable in a single chunk-based scan with page-granularity sampling.
    /// </summary>
    /// <param name="table">The ComponentTable to scan.</param>
    /// <param name="epochManager">Epoch manager for page access protection.</param>
    /// <param name="pageInterval">Page sampling interval: 1 = full scan, N = every Nth page.</param>
    internal static unsafe void RebuildAll(ComponentTable table, EpochManager epochManager, int pageInterval = 1)
    {
        var indexedFieldInfos = table.IndexedFieldInfos;
        var indexStats = table.IndexStats;
        int fieldCount = indexedFieldInfos.Length;
        if (fieldCount == 0)
        {
            return;
        }

        // Allocate per-field accumulators
        var hlls = new HyperLogLog[fieldCount];
        var freqs = new Dictionary<long, int>[fieldCount];
        var bucketCounts = new int[fieldCount][];
        var mins = new long[fieldCount];
        var maxes = new long[fieldCount];

        for (int i = 0; i < fieldCount; i++)
        {
            hlls[i] = new HyperLogLog();
            freqs[i] = new Dictionary<long, int>();
            bucketCounts[i] = new int[Histogram.BucketCount];
            // Use live min/max from B+Tree for histogram bucketing (always accurate, even with sampling)
            mins[i] = indexStats[i].MinValue;
            maxes[i] = indexStats[i].MaxValue;
        }

        // Pre-compute bucket widths from B+Tree min/max (not sampled data)
        var bucketWidths = new long[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            bucketWidths[i] = (maxes[i] == mins[i]) ? 0 : Math.Max(1, (maxes[i] - mins[i]) / Histogram.BucketCount);
        }

        var segment = table.ComponentSegment;
        int totalPages = segment.Length;
        int stride = segment.Stride;
        int rootChunkCount = segment.ChunkCountRootPage;
        int otherChunkCount = segment.ChunkCountPerPage;
        int bitmapLongsRoot = (rootChunkCount + 63) >> 6;
        int bitmapLongsOther = (otherChunkCount + 63) >> 6;
        int rootDataOffset = segment.RootChunkDataOffset;
        int otherDataOffset = segment.OtherChunkDataOffset;

        int sampledEntities = 0;

        // Single epoch guard for the entire scan
        using var guard = EpochGuard.Enter(epochManager);
        var epoch = guard.Epoch;

        for (int pageIndex = 0; pageIndex < totalPages; pageIndex += pageInterval)
        {
            bool isRoot = (pageIndex == 0);
            int maxChunks = isRoot ? rootChunkCount : otherChunkCount;
            int bitmapLongs = isRoot ? bitmapLongsRoot : bitmapLongsOther;
            int dataOffset = isRoot ? rootDataOffset : otherDataOffset;

            var page = segment.GetPage(pageIndex, epoch, out _);
            var bitmap = page.MetadataReadOnly<long>();

            for (int w = 0; w < bitmapLongs; w++)
            {
                long word = bitmap[w];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    int chunkInPage = w * 64 + bit;
                    word &= word - 1; // Clear lowest set bit

                    if (chunkInPage >= maxChunks)
                    {
                        break;
                    }

                    // Skip chunk 0 on root page (null sentinel)
                    if (isRoot && chunkInPage == 0)
                    {
                        continue;
                    }

                    // Get pointer to chunk raw data
                    var chunkData = page.RawData<byte>(dataOffset + chunkInPage * stride, stride);
                    sampledEntities++;

                    fixed (byte* ptr = chunkData)
                    {
                        for (int f = 0; f < fieldCount; f++)
                        {
                            long key = ExtractKeyAsLong(ptr, indexedFieldInfos[f].OffsetToField, indexStats[f].KeyType);

                            // HLL
                            hlls[f].Add(key);

                            // Frequency counting for MCV
                            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(freqs[f], key, out _);
                            count++;

                            // Histogram bucketing using B+Tree min/max
                            int bucket;
                            if (bucketWidths[f] == 0)
                            {
                                bucket = 0;
                            }
                            else
                            {
                                long offset = key - mins[f];
                                long b = offset / bucketWidths[f];
                                bucket = (int)Math.Clamp(b, 0, Histogram.BucketCount - 1);
                            }
                            bucketCounts[f][bucket]++;
                        }
                    }
                }
            }
        }

        if (sampledEntities == 0)
        {
            return;
        }

        // Compute scale factor for sampling
        int estimatedTotalEntities = table.PrimaryKeyIndex.EntryCount;
        double scaleFactor = (pageInterval > 1 && sampledEntities > 0) ? (double)estimatedTotalEntities / sampledEntities : 1.0;
        long scaledTotal = (long)(sampledEntities * scaleFactor);

        // Build final structures and atomic-swap per field
        for (int f = 0; f < fieldCount; f++)
        {
            // MCV: scale individual counts via scaleFactor
            var mcv = MostCommonValues.Build(freqs[f], scaledTotal, scaleFactor);

            // Histogram: scale bucket counts if sampling
            int[] scaledBuckets;
            int histogramTotal;
            if (scaleFactor > 1.0)
            {
                scaledBuckets = new int[Histogram.BucketCount];
                histogramTotal = 0;
                for (int b = 0; b < Histogram.BucketCount; b++)
                {
                    scaledBuckets[b] = Math.Max(0, (int)(bucketCounts[f][b] * scaleFactor));
                    histogramTotal += scaledBuckets[b];
                }
            }
            else
            {
                scaledBuckets = bucketCounts[f];
                histogramTotal = 0;
                for (int b = 0; b < Histogram.BucketCount; b++)
                {
                    histogramTotal += scaledBuckets[b];
                }
            }

            var histogram = new Histogram(mins[f], maxes[f], scaledBuckets, histogramTotal);

            // Atomic swap: volatile writes ensure visibility to concurrent readers
            indexStats[f].HyperLogLog = hlls[f];
            indexStats[f].MostCommonValues = mcv;
            indexStats[f].Histogram = histogram;
        }
    }

    /// <summary>
    /// Convenience API: full scan (no sampling). Suitable for tests and explicit rebuilds.
    /// </summary>
    internal static void RebuildStatistics(ComponentTable table, EpochManager epochManager) =>
        RebuildAll(table, epochManager, pageInterval: 1);

    /// <summary>
    /// Extracts the key value from raw chunk bytes at the given offset, encoded as a long
    /// using the same convention as B+Tree key encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ExtractKeyAsLong(byte* chunkAddr, int offset, KeyType keyType)
    {
        byte* ptr = chunkAddr + offset;
        return keyType switch
        {
            KeyType.Bool => *(bool*)ptr ? 1L : 0L,
            KeyType.Byte => *ptr,
            KeyType.SByte => *(sbyte*)ptr,
            KeyType.Short => *(short*)ptr,
            KeyType.UShort => *(ushort*)ptr,
            KeyType.Int => *(int*)ptr,
            KeyType.UInt => *(uint*)ptr,
            KeyType.Long => *(long*)ptr,
            KeyType.ULong => (long)*(ulong*)ptr,
            KeyType.Float => *(int*)ptr,       // IEEE 754 bit pattern
            KeyType.Double => *(long*)ptr,      // IEEE 754 bit pattern
            _ => *(long*)ptr
        };
    }
}
