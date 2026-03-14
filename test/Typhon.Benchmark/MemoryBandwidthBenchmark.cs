using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Spectre.Console;

namespace Typhon.Benchmark;

/// <summary>
/// Measures effective memory bandwidth across chunk sizes (32B–512B), alignment modes,
/// and indirection levels (0=sequential, 1..4=pointer-chasing). Includes cold (cache trashed)
/// and warm (cache resident) passes. Tests ACLP effect via aligned vs misaligned 128B comparison.
/// </summary>
public static unsafe class MemoryBandwidthBenchmark
{
    const long DataBlockSize = 2L * 1024 * 1024 * 1024;        // 2 GB working set
    const long ReadSize = 512L * 1024 * 1024;                   // 512 MB read per test
    const int TrashBlockSize = 72 * 1024 * 1024;                // 72 MB > 7950X L3 per CCD (32 MB)
    const int Alignment = 128;                                   // ACLP natural alignment
    const int Runs = 21;                                         // measurement iterations
    const int TrimCount = 4;                                     // discard 4 best + 4 worst
    const double DramLatencyNs = 70.0;                           // assumed DRAM round-trip for derived metrics

    // Test configurations: Label, ChunkSize (bytes), AlignmentOffset (bytes)
    // "128M" = 128B chunks offset by 64B, straddling two 128B-aligned regions (disables ACLP pairing)
    static readonly (string Label, int Size, int Offset)[] Configs =
    [
        ("32", 32, 0),
        ("64", 64, 0),
        ("128", 128, 0),
        ("128M", 128, 64),
        ("256", 256, 0),
        ("512", 512, 0),
    ];

    static readonly int[] Indirections = [0, 1, 2, 3, 4];
    static readonly int[] StreamCounts = [1, 2, 4, 8];
    static readonly int[] SeqRunBytes = [128, 256, 512, 1024, 2048, 4096];
    static readonly int[] PrefetchDistances = [0, 1, 4, 8, 16];

    static byte* _dataBlock;
    static byte* _trashBlock;
    static double[,] _bwCold;                                   // [configIdx, indIdx] GB/s
    static double[,] _bwWarm;                                   // [configIdx, indIdx] GB/s
    static double[,,] _timingsCold;                             // [configIdx, indIdx, run] seconds
    static double[,,] _timingsWarm;                             // [configIdx, indIdx, run] seconds
    static double[] _bwStreams;                                  // [streamIdx] GB/s cold
    static double[] _bwSeqRuns;                                  // [runIdx] GB/s cold
    static double[] _bwPrefetch;                                  // [distIdx] GB/s cold

    // Anti-optimization sink — volatile write forces JIT to materialize the value
    static volatile int _sink;

    public static void Run()
    {
        int nc = Configs.Length;
        int ni = Indirections.Length;

        _bwCold = new double[nc, ni];
        _bwWarm = new double[nc, ni];
        _timingsCold = new double[nc, ni, Runs];
        _timingsWarm = new double[nc, ni, Runs];
        _bwStreams = new double[StreamCounts.Length];
        _bwSeqRuns = new double[SeqRunBytes.Length];
        _bwPrefetch = new double[PrefetchDistances.Length];

        AnsiConsole.Write(new Rule("[bold yellow]Memory Bandwidth — ACLP & Warm/Cold Analysis[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Data block:  [cyan]{DataBlockSize / (1024 * 1024 * 1024)}[/] GB ({Alignment}-byte aligned via NativeMemory)");
        AnsiConsole.MarkupLine($"  Read size:   [cyan]{ReadSize / (1024 * 1024)}[/] MB per test");
        AnsiConsole.MarkupLine($"  Trash block: [cyan]{TrashBlockSize / (1024 * 1024)}[/] MB (L3 eviction between runs)");
        AnsiConsole.MarkupLine($"  Runs: [cyan]{Runs}[/], trimmed mean (discard {TrimCount} best + {TrimCount} worst = {Runs - 2 * TrimCount} averaged)");
        AnsiConsole.MarkupLine($"  Timer freq:  [cyan]{Stopwatch.Frequency:N0}[/] Hz ({1_000_000_000.0 / Stopwatch.Frequency:F1} ns resolution)");
        AnsiConsole.MarkupLine($"  Processors:  [cyan]{Environment.ProcessorCount}[/]");
        AnsiConsole.MarkupLine($"  Configs:     [cyan]{nc}[/] (128M = misaligned 128B — straddles ACLP boundary)");
        AnsiConsole.MarkupLine($"  Total cells: [cyan]{nc * ni * 2}[/] ({nc} configs x {ni} indirections x 2 passes)");
        AnsiConsole.WriteLine();

        try
        {
            AnsiConsole.Markup("  Allocating 2 GB + 72 MB... ");
            Allocate();
            AnsiConsole.MarkupLine("[green]done[/]");

            AnsiConsole.Markup("  Filling with random data... ");
            FillRandom();
            AnsiConsole.MarkupLine("[green]done[/]");
            AnsiConsole.WriteLine();

            // Elevate priority to reduce scheduling noise
            using var proc = Process.GetCurrentProcess();
            var savedPriority = proc.PriorityClass;
            proc.PriorityClass = ProcessPriorityClass.High;

            try
            {
                for (int ci = 0; ci < nc; ci++)
                {
                    var (label, chunkSize, alignOffset) = Configs[ci];

                    for (int ii = 0; ii < ni; ii++)
                    {
                        int ind = Indirections[ii];
                        string indLabel = ind == 0 ? "sequential" : $"ind={ind}      ";
                        AnsiConsole.Markup($"  [cyan]{label,4}[/]B  {indLabel}: ");

                        // Cold pass (cache trashed between each timed run)
                        double bwCold = MeasureBandwidth(chunkSize, ind, alignOffset, warm: false, ci, ii);
                        _bwCold[ci, ii] = bwCold;

                        // Warm pass (no cache eviction between timed runs)
                        double bwWarm = MeasureBandwidth(chunkSize, ind, alignOffset, warm: true, ci, ii);
                        _bwWarm[ci, ii] = bwWarm;

                        AnsiConsole.MarkupLine($"cold=[bold green]{bwCold,7:F2}[/]  warm=[bold cyan]{bwWarm,7:F2}[/] GB/s");
                    }

                    if (ci < nc - 1)
                    {
                        AnsiConsole.WriteLine();
                    }
                }

                // ── Independent streams test: K random 128B reads interleaved ──
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[bold yellow]Independent Streams — 128B random, Cold[/]").LeftJustified());
                AnsiConsole.MarkupLine("  [grey]No pointer-chasing — addresses from offset array (tests OoO overlap of K independent DRAM requests)[/]");
                AnsiConsole.WriteLine();

                int streamChunkSize = 128;
                int streamLongsPerChunk = streamChunkSize / 8;
                int totalStreamChunks = (int)(ReadSize / streamChunkSize);

                int[] streamOffsets = GenerateUniqueOffsets(totalStreamChunks, streamChunkSize, 0);
                Shuffle(streamOffsets, seed: 77777);

                for (int si = 0; si < StreamCounts.Length; si++)
                {
                    int K = StreamCounts[si];
                    int chunksPerStream = totalStreamChunks / K;
                    int usedChunks = chunksPerStream * K;

                    // Interleave offsets: [s0_c0, s1_c0, ..., sK-1_c0, s0_c1, s1_c1, ...]
                    int[] interleaved = new int[usedChunks];
                    for (int i = 0; i < chunksPerStream; i++)
                    {
                        for (int s = 0; s < K; s++)
                        {
                            interleaved[i * K + s] = streamOffsets[s * chunksPerStream + i];
                        }
                    }

                    // Warmup
                    ReadKStreams(streamLongsPerChunk, streamChunkSize, K, interleaved, chunksPerStream);

                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();

                    // Timed runs (cold)
                    double[] timings = new double[Runs];
                    long blackhole = 0;

                    for (int run = 0; run < Runs; run++)
                    {
                        long trash = TrashCache();
                        blackhole ^= trash;

                        long t0 = Stopwatch.GetTimestamp();
                        long sum = ReadKStreams(streamLongsPerChunk, streamChunkSize, K, interleaved, chunksPerStream);
                        long t1 = Stopwatch.GetTimestamp();

                        timings[run] = (double)(t1 - t0) / Stopwatch.Frequency;
                        blackhole ^= sum;
                    }

                    _sink = (int)blackhole;

                    Array.Sort(timings);
                    double total = 0;
                    int n = 0;
                    for (int i = TrimCount; i < Runs - TrimCount; i++)
                    {
                        total += timings[i];
                        n++;
                    }

                    double avgSec = total / n;
                    double bw = (ReadSize / (1024.0 * 1024.0 * 1024.0)) / avgSec;
                    _bwStreams[si] = bw;

                    AnsiConsole.MarkupLine($"  [cyan]{K}[/] stream{(K > 1 ? "s" : " ")}: [bold green]{bw,7:F2}[/] GB/s");
                }

                // ── Sequential run length test: read S bytes linearly at each random location ──
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[bold yellow]Sequential Run Length — random jump then linear read, Cold[/]").LeftJustified());
                AnsiConsole.MarkupLine("  [grey]At each random offset, read S bytes sequentially before jumping to next random location[/]");
                AnsiConsole.WriteLine();

                for (int ri = 0; ri < SeqRunBytes.Length; ri++)
                {
                    int seqBytes = SeqRunBytes[ri];
                    int numJumps = (int)(ReadSize / seqBytes);

                    // Generate unique offsets with seqBytes spacing to avoid overlaps
                    int[] runOffsets = GenerateUniqueOffsets(numJumps, seqBytes, 0);
                    Shuffle(runOffsets, seed: 55555 + ri);

                    // Warmup
                    ReadSequentialRuns(seqBytes, runOffsets, numJumps);

                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();

                    // Timed runs (cold)
                    double[] timings = new double[Runs];
                    long blackhole = 0;

                    for (int run = 0; run < Runs; run++)
                    {
                        long trash = TrashCache();
                        blackhole ^= trash;

                        long t0 = Stopwatch.GetTimestamp();
                        long sum = ReadSequentialRuns(seqBytes, runOffsets, numJumps);
                        long t1 = Stopwatch.GetTimestamp();

                        timings[run] = (double)(t1 - t0) / Stopwatch.Frequency;
                        blackhole ^= sum;
                    }

                    _sink = (int)blackhole;

                    Array.Sort(timings);
                    double total = 0;
                    int n = 0;
                    for (int i = TrimCount; i < Runs - TrimCount; i++)
                    {
                        total += timings[i];
                        n++;
                    }

                    double avgSec = total / n;
                    double bw = (ReadSize / (1024.0 * 1024.0 * 1024.0)) / avgSec;
                    _bwSeqRuns[ri] = bw;

                    AnsiConsole.MarkupLine($"  [cyan]{seqBytes,5}[/]B run: [bold green]{bw,7:F2}[/] GB/s");
                }

                // ── Software Prefetch test: 128B random with Sse.Prefetch0 at various distances ──
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[bold yellow]Software Prefetch — 128B random, Cold[/]").LeftJustified());
                AnsiConsole.MarkupLine("  [grey]Sse.Prefetch0 issued D chunks ahead (2 prefetches per chunk = 2 cache lines). D=0 is no-prefetch baseline.[/]");
                AnsiConsole.WriteLine();

                int pfChunkSize = 128;
                int pfLongsPerChunk = pfChunkSize / 8;
                int pfNumChunks = (int)(ReadSize / pfChunkSize);

                int[] pfOffsets = GenerateUniqueOffsets(pfNumChunks, pfChunkSize, 0);
                Shuffle(pfOffsets, seed: 99999);

                for (int di = 0; di < PrefetchDistances.Length; di++)
                {
                    int dist = PrefetchDistances[di];

                    // Warmup
                    ReadWithPrefetch(pfLongsPerChunk, pfOffsets, pfNumChunks, dist);

                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();

                    // Timed runs (cold)
                    double[] timings = new double[Runs];
                    long blackhole = 0;

                    for (int run = 0; run < Runs; run++)
                    {
                        long trash = TrashCache();
                        blackhole ^= trash;

                        long t0 = Stopwatch.GetTimestamp();
                        long sum = ReadWithPrefetch(pfLongsPerChunk, pfOffsets, pfNumChunks, dist);
                        long t1 = Stopwatch.GetTimestamp();

                        timings[run] = (double)(t1 - t0) / Stopwatch.Frequency;
                        blackhole ^= sum;
                    }

                    _sink = (int)blackhole;

                    Array.Sort(timings);
                    double total = 0;
                    int n = 0;
                    for (int i = TrimCount; i < Runs - TrimCount; i++)
                    {
                        total += timings[i];
                        n++;
                    }

                    double avgSec = total / n;
                    double bw = (ReadSize / (1024.0 * 1024.0 * 1024.0)) / avgSec;
                    _bwPrefetch[di] = bw;

                    string label = dist == 0 ? "none" : $"D={dist,2}";
                    AnsiConsole.MarkupLine($"  Prefetch {label}: [bold green]{bw,7:F2}[/] GB/s");
                }
            }
            finally
            {
                proc.PriorityClass = savedPriority;
            }

            AnsiConsole.WriteLine();

            // Display result tables
            PrintBandwidthTable(_bwCold, "Bandwidth (GB/s) — Cold");
            PrintLatencyTable();
            PrintRatioVs64Table();
            PrintRatioVsSeqTable();
            PrintConcurrentRequestsTable();
            PrintBandwidthTable(_bwWarm, "Bandwidth (GB/s) — Warm");
            PrintWarmColdRatioTable();
            PrintStreamTable();
            PrintSeqRunTable();
            PrintPrefetchTable();

            string logPath = WriteLog();
            AnsiConsole.MarkupLine($"\n  Detailed log written to: [link]{logPath}[/]");
        }
        finally
        {
            Free();
        }
    }

    static void Allocate()
    {
        _dataBlock = (byte*)NativeMemory.AlignedAlloc((nuint)DataBlockSize, Alignment);
        _trashBlock = (byte*)NativeMemory.AlignedAlloc((nuint)TrashBlockSize, Alignment);
    }

    static void Free()
    {
        if (_dataBlock != null)
        {
            NativeMemory.AlignedFree(_dataBlock);
            _dataBlock = null;
        }

        if (_trashBlock != null)
        {
            NativeMemory.AlignedFree(_trashBlock);
            _trashBlock = null;
        }
    }

    static void FillRandom()
    {
        // xorshift64 — fast, good enough for filling data
        ulong state = 0xDEADBEEFCAFEBABE;

        ulong* p = (ulong*)_dataBlock;
        long count = DataBlockSize / 8;
        for (long i = 0; i < count; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            p[i] = state;
        }

        p = (ulong*)_trashBlock;
        count = TrashBlockSize / 8;
        for (long i = 0; i < count; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            p[i] = state;
        }
    }

    /// <summary>
    /// Reads the entire 72 MB trash block to evict working data from L1/L2/L3.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long TrashCache()
    {
        long sum = 0;
        long* p = (long*)_trashBlock;
        int count = TrashBlockSize / 8;
        for (int i = 0; i < count; i++)
        {
            sum += p[i];
        }

        return sum;
    }

    static double MeasureBandwidth(int chunkSize, int indirection, int alignOffset, bool warm, int ci, int ii)
    {
        int longsPerChunk = chunkSize / 8;

        // --- Setup: generate chain structure for indirection > 0 ---
        int[] groupStarts = null;
        int numGroups = 0;

        if (indirection > 0)
        {
            int totalChunks = (int)(ReadSize / chunkSize);
            numGroups = totalChunks / (indirection + 1);
            int usedChunks = numGroups * (indirection + 1);

            // Zone-based unique offsets (includes alignOffset shift for misaligned tests)
            int[] offsets = GenerateUniqueOffsets(usedChunks, chunkSize, alignOffset);

            // Shuffle to randomize the access pattern (deterministic seed per test)
            Shuffle(offsets, seed: ci * 1000 + ii * 100 + 42);

            // Build embedded pointer chains: first 4 bytes of chunk[j] = byte offset of chunk[j+1]
            groupStarts = new int[numGroups];
            for (int g = 0; g < numGroups; g++)
            {
                int bi = g * (indirection + 1);
                groupStarts[g] = offsets[bi];

                for (int j = 0; j < indirection; j++)
                {
                    *(int*)(_dataBlock + offsets[bi + j]) = offsets[bi + j + 1];
                }
            }
        }

        // Force GC before measurement — no collections during timed runs
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        // Warmup: JIT compilation + ensure pages are resident
        long warmupSum = indirection == 0
            ? ReadSequential(longsPerChunk, chunkSize, alignOffset)
            : ReadWithIndirection(longsPerChunk, chunkSize, indirection, groupStarts, numGroups);

        // Timed runs
        double[] timings = new double[Runs];
        long blackhole = warmupSum;

        for (int run = 0; run < Runs; run++)
        {
            // Cold mode: trash cache between runs to force DRAM accesses
            // Warm mode: skip trashing — data may remain in L1/L2/L3 from previous run
            if (!warm)
            {
                long trash = TrashCache();
                blackhole ^= trash;
            }

            long t0 = Stopwatch.GetTimestamp();

            long sum = indirection == 0
                ? ReadSequential(longsPerChunk, chunkSize, alignOffset)
                : ReadWithIndirection(longsPerChunk, chunkSize, indirection, groupStarts, numGroups);

            long t1 = Stopwatch.GetTimestamp();

            double elapsed = (double)(t1 - t0) / Stopwatch.Frequency;
            timings[run] = elapsed;
            blackhole ^= sum;

            if (warm)
            {
                _timingsWarm[ci, ii, run] = elapsed;
            }
            else
            {
                _timingsCold[ci, ii, run] = elapsed;
            }
        }

        // Force materialization of blackhole — prevents dead-code elimination
        _sink = (int)blackhole;

        // Trimmed mean: sort, discard extremes, average the middle
        Array.Sort(timings);
        double total = 0;
        int n = 0;
        for (int i = TrimCount; i < Runs - TrimCount; i++)
        {
            total += timings[i];
            n++;
        }

        double avgSec = total / n;
        return (ReadSize / (1024.0 * 1024.0 * 1024.0)) / avgSec;
    }

    /// <summary>
    /// Generates unique byte offsets using a zone-based approach.
    /// The usable space (2GB minus alignOffset) is divided into 'count' zones.
    /// Each zone yields one random aligned offset, shifted by alignOffset for misaligned tests.
    /// </summary>
    static int[] GenerateUniqueOffsets(int count, int chunkSize, int alignOffset)
    {
        int numSlots = (int)((DataBlockSize - alignOffset) / chunkSize);
        int slotsPerZone = numSlots / count;

        var rng = new Random(12345);
        var offsets = new int[count];

        for (int i = 0; i < count; i++)
        {
            int slot = i * slotsPerZone + rng.Next(slotsPerZone);
            offsets[i] = slot * chunkSize + alignOffset;
        }

        return offsets;
    }

    static void Shuffle(int[] array, int seed)
    {
        var rng = new Random(seed);
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }

    /// <summary>
    /// Sequential scan: reads 512 MB starting at dataBlock + alignOffset, chunk by chunk.
    /// Uses 4 independent accumulators to avoid compute bottleneck (keeps memory as the limiter).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long ReadSequential(int longsPerChunk, int chunkSize, int alignOffset)
    {
        long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        byte* start = _dataBlock + alignOffset;
        byte* end = start + ReadSize;

        for (byte* ptr = start; ptr < end; ptr += chunkSize)
        {
            long* lp = (long*)ptr;
            for (int k = 0; k < longsPerChunk; k += 4)
            {
                s0 += lp[k];
                s1 += lp[k + 1];
                s2 += lp[k + 2];
                s3 += lp[k + 3];
            }
        }

        return s0 + s1 + s2 + s3;
    }

    /// <summary>
    /// Random access with pointer-chasing chains.
    /// Each group of (indirection+1) chunks is linked via embedded offsets in the first 4 bytes.
    /// Within a group: serial dependency (data-dependent address) prevents OoO speculation.
    /// Between groups: independent (OoO can overlap multiple groups).
    /// AlignOffset is baked into groupStarts offsets at generation time.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long ReadWithIndirection(int longsPerChunk, int chunkSize, int indirection, int[] groupStarts, int numGroups)
    {
        long sum = 0;
        byte* basePtr = _dataBlock;

        fixed (int* starts = groupStarts)
        {
            for (int g = 0; g < numGroups; g++)
            {
                byte* ptr = basePtr + starts[g];

                for (int j = 0; j <= indirection; j++)
                {
                    // Read entire chunk — 4 accumulators to avoid ALU bottleneck
                    long* lp = (long*)ptr;
                    long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                    for (int k = 0; k < longsPerChunk; k += 4)
                    {
                        s0 += lp[k];
                        s1 += lp[k + 1];
                        s2 += lp[k + 2];
                        s3 += lp[k + 3];
                    }

                    sum += s0 + s1 + s2 + s3;

                    // Follow embedded pointer chain (data-dependent → serializes the next load)
                    if (j < indirection)
                    {
                        int nextOff = *(int*)ptr;
                        ptr = basePtr + nextOff;
                    }
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// Reads random 128B chunks using K interleaved independent streams.
    /// Each round issues K loads to K independent random addresses, testing
    /// whether the CPU's OoO engine can overlap multiple DRAM requests.
    /// No pointer-chasing — addresses come from the offset array (isolates concurrency from data dependency).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long ReadKStreams(int longsPerChunk, int chunkSize, int numStreams, int[] interleavedOffsets, int chunksPerStream)
    {
        long sum = 0;
        byte* basePtr = _dataBlock;

        fixed (int* offsets = interleavedOffsets)
        {
            int idx = 0;
            for (int i = 0; i < chunksPerStream; i++)
            {
                for (int s = 0; s < numStreams; s++)
                {
                    byte* ptr = basePtr + offsets[idx++];
                    long* lp = (long*)ptr;
                    long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                    for (int k = 0; k < longsPerChunk; k += 4)
                    {
                        s0 += lp[k];
                        s1 += lp[k + 1];
                        s2 += lp[k + 2];
                        s3 += lp[k + 3];
                    }

                    sum += s0 + s1 + s2 + s3;
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// At each random offset, reads seqBytes of data linearly (prefetcher-friendly),
    /// then jumps to the next random offset. Tests how sequential run length amortizes random-jump cost.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long ReadSequentialRuns(int seqBytes, int[] offsets, int numJumps)
    {
        long sum = 0;
        byte* basePtr = _dataBlock;
        int longsPerRun = seqBytes / 8;

        fixed (int* pOffsets = offsets)
        {
            for (int g = 0; g < numJumps; g++)
            {
                long* lp = (long*)(basePtr + pOffsets[g]);
                long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int k = 0; k < longsPerRun; k += 4)
                {
                    s0 += lp[k];
                    s1 += lp[k + 1];
                    s2 += lp[k + 2];
                    s3 += lp[k + 3];
                }

                sum += s0 + s1 + s2 + s3;
            }
        }

        return sum;
    }

    /// <summary>
    /// Reads random 128B chunks from an offset array, issuing Sse.Prefetch0 for the chunk
    /// 'prefetchDist' positions ahead. Two prefetches per chunk (128B = 2 cache lines).
    /// Distance 0 = no prefetch (baseline for comparison).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long ReadWithPrefetch(int longsPerChunk, int[] offsets, int numChunks, int prefetchDist)
    {
        long sum = 0;
        byte* basePtr = _dataBlock;

        fixed (int* pOff = offsets)
        {
            // Prime the prefetch pipeline: issue prefetches for the first 'prefetchDist' chunks
            for (int p = 0; p < prefetchDist && p < numChunks; p++)
            {
                byte* target = basePtr + pOff[p];
                Sse.Prefetch0(target);
                Sse.Prefetch0(target + 64);
            }

            for (int i = 0; i < numChunks; i++)
            {
                // Prefetch the chunk 'prefetchDist' positions ahead
                int ahead = i + prefetchDist;
                if (ahead < numChunks)
                {
                    byte* target = basePtr + pOff[ahead];
                    Sse.Prefetch0(target);
                    Sse.Prefetch0(target + 64);
                }

                // Read current chunk — same pattern as other read methods
                long* lp = (long*)(basePtr + pOff[i]);
                long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int k = 0; k < longsPerChunk; k += 4)
                {
                    s0 += lp[k];
                    s1 += lp[k + 1];
                    s2 += lp[k + 2];
                    s3 += lp[k + 3];
                }

                sum += s0 + s1 + s2 + s3;
            }
        }

        return sum;
    }

    // ─── Display Tables ─────────────────────────────────────────────────────────

    static void PrintBandwidthTable(double[,] bw, string title)
    {
        var table = new Table()
            .Title($"[bold yellow]{title}[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Chunk[/]").RightAligned());

        foreach (int ind in Indirections)
        {
            string header = ind == 0 ? "[bold]Seq[/]" : $"[bold]Ind={ind}[/]";
            table.AddColumn(new TableColumn(header).RightAligned());
        }

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            var cells = new Markup[Indirections.Length + 1];
            cells[0] = new Markup($"[cyan]{Configs[ci].Label,4}[/] B");

            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                cells[ii + 1] = new Markup($"[green]{bw[ci, ii],8:F2}[/]");
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Nanoseconds per chunk access. For sequential: throughput time. For random: effective latency.
    /// </summary>
    static void PrintLatencyTable()
    {
        var table = new Table()
            .Title("[bold yellow]Latency per access (ns) — Cold[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Chunk[/]").RightAligned());

        foreach (int ind in Indirections)
        {
            string header = ind == 0 ? "[bold]Seq[/]" : $"[bold]Ind={ind}[/]";
            table.AddColumn(new TableColumn(header).RightAligned());
        }

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            var cells = new Markup[Indirections.Length + 1];
            cells[0] = new Markup($"[cyan]{Configs[ci].Label,4}[/] B");

            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                double bwBytes = _bwCold[ci, ii] * 1024.0 * 1024.0 * 1024.0;
                double nsPerAccess = Configs[ci].Size / bwBytes * 1e9;
                cells[ii + 1] = new Markup($"[white]{nsPerAccess,8:F1}[/]");
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void PrintRatioVs64Table()
    {
        int idx64 = FindConfig("64");

        var table = new Table()
            .Title("[bold yellow]Ratio vs 64B (same indirection) — ACLP indicator[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Chunk[/]").RightAligned());

        foreach (int ind in Indirections)
        {
            string header = ind == 0 ? "[bold]Seq[/]" : $"[bold]Ind={ind}[/]";
            table.AddColumn(new TableColumn(header).RightAligned());
        }

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            var cells = new Markup[Indirections.Length + 1];
            cells[0] = new Markup($"[cyan]{Configs[ci].Label,4}[/] B");

            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                double ratio = _bwCold[ci, ii] / _bwCold[idx64, ii];

                // Highlight 128 and 128M rows for direct ACLP comparison
                string color = Configs[ci].Label is "128" or "128M" ? "bold yellow" : "white";
                cells[ii + 1] = new Markup($"[{color}]{ratio,7:F2}x[/]");
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void PrintRatioVsSeqTable()
    {
        var table = new Table()
            .Title("[bold yellow]Ratio vs Sequential (indirection cost)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Chunk[/]").RightAligned());

        foreach (int ind in Indirections)
        {
            string header = ind == 0 ? "[bold]Seq[/]" : $"[bold]Ind={ind}[/]";
            table.AddColumn(new TableColumn(header).RightAligned());
        }

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            var cells = new Markup[Indirections.Length + 1];
            cells[0] = new Markup($"[cyan]{Configs[ci].Label,4}[/] B");

            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                double ratio = _bwCold[ci, ii] / _bwCold[ci, 0];
                cells[ii + 1] = new Markup($"[white]{ratio,7:F2}x[/]");
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Estimates how many DRAM requests the CPU sustains concurrently.
    /// Derived: concurrent = bandwidth × assumed_dram_latency / chunk_size.
    /// For sequential reads, shows equivalent prefetcher efficiency.
    /// </summary>
    static void PrintConcurrentRequestsTable()
    {
        var table = new Table()
            .Title($"[bold yellow]Estimated concurrent DRAM requests (assumes {DramLatencyNs:F0}ns latency)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Chunk[/]").RightAligned());

        foreach (int ind in Indirections)
        {
            string header = ind == 0 ? "[bold]Seq[/]" : $"[bold]Ind={ind}[/]";
            table.AddColumn(new TableColumn(header).RightAligned());
        }

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            var cells = new Markup[Indirections.Length + 1];
            cells[0] = new Markup($"[cyan]{Configs[ci].Label,4}[/] B");

            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                double bwBytes = _bwCold[ci, ii] * 1024.0 * 1024.0 * 1024.0;
                double concurrent = bwBytes * DramLatencyNs * 1e-9 / Configs[ci].Size;
                cells[ii + 1] = new Markup($"[white]{concurrent,8:F1}[/]");
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void PrintWarmColdRatioTable()
    {
        var table = new Table()
            .Title("[bold yellow]Warm / Cold ratio (cache benefit)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Chunk[/]").RightAligned());

        foreach (int ind in Indirections)
        {
            string header = ind == 0 ? "[bold]Seq[/]" : $"[bold]Ind={ind}[/]";
            table.AddColumn(new TableColumn(header).RightAligned());
        }

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            var cells = new Markup[Indirections.Length + 1];
            cells[0] = new Markup($"[cyan]{Configs[ci].Label,4}[/] B");

            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                double ratio = _bwWarm[ci, ii] / _bwCold[ci, ii];

                string color;
                if (ratio >= 1.5)
                {
                    color = "bold green";
                }
                else if (ratio >= 1.1)
                {
                    color = "green";
                }
                else
                {
                    color = "white";
                }

                cells[ii + 1] = new Markup($"[{color}]{ratio,7:F2}x[/]");
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static int FindConfig(string label)
    {
        for (int i = 0; i < Configs.Length; i++)
        {
            if (Configs[i].Label == label)
            {
                return i;
            }
        }

        return -1;
    }

    static void PrintStreamTable()
    {
        int idx128 = FindConfig("128");
        double bwSeq = idx128 >= 0 ? _bwCold[idx128, 0] : 0;
        double bwInd1 = idx128 >= 0 ? _bwCold[idx128, 1] : 0;

        var table = new Table()
            .Title("[bold yellow]Independent Streams — 128B random chunks, Cold[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Streams[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]BW (GB/s)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]ns/access[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Conc. DRAM[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]vs 1-stream[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]vs Ind=1[/]").RightAligned());

        // Reference rows from main test
        if (idx128 >= 0)
        {
            double seqNs = 128.0 / (bwSeq * 1024 * 1024 * 1024) * 1e9;
            double seqConc = bwSeq * 1024 * 1024 * 1024 * DramLatencyNs * 1e-9 / 128;
            table.AddRow(
                new Markup("[grey]Seq (ref)[/]"),
                new Markup($"[grey]{bwSeq,8:F2}[/]"),
                new Markup($"[grey]{seqNs,8:F1}[/]"),
                new Markup($"[grey]{seqConc,8:F1}[/]"),
                new Markup("[grey]       —[/]"),
                new Markup($"[grey]{bwSeq / bwInd1,7:F1}x[/]"));

            double ind1Ns = 128.0 / (bwInd1 * 1024 * 1024 * 1024) * 1e9;
            double ind1Conc = bwInd1 * 1024 * 1024 * 1024 * DramLatencyNs * 1e-9 / 128;
            table.AddRow(
                new Markup("[grey]Ind=1 (ref)[/]"),
                new Markup($"[grey]{bwInd1,8:F2}[/]"),
                new Markup($"[grey]{ind1Ns,8:F1}[/]"),
                new Markup($"[grey]{ind1Conc,8:F1}[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]   1.0x[/]"));
        }

        for (int si = 0; si < StreamCounts.Length; si++)
        {
            double bw = _bwStreams[si];
            double nsPerAccess = 128.0 / (bw * 1024 * 1024 * 1024) * 1e9;
            double concurrent = bw * 1024 * 1024 * 1024 * DramLatencyNs * 1e-9 / 128;
            double vs1 = _bwStreams[0] > 0 ? bw / _bwStreams[0] : 0;
            double vsInd1 = bwInd1 > 0 ? bw / bwInd1 : 0;

            table.AddRow(
                new Markup($"[cyan]{StreamCounts[si],2}[/]"),
                new Markup($"[green]{bw,8:F2}[/]"),
                new Markup($"[white]{nsPerAccess,8:F1}[/]"),
                new Markup($"[white]{concurrent,8:F1}[/]"),
                new Markup($"[white]{vs1,7:F2}x[/]"),
                new Markup($"[yellow]{vsInd1,7:F1}x[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void PrintSeqRunTable()
    {
        int idx128 = FindConfig("128");
        double bwSeq = idx128 >= 0 ? _bwCold[idx128, 0] : 0;
        double bwInd1 = idx128 >= 0 ? _bwCold[idx128, 1] : 0;

        var table = new Table()
            .Title("[bold yellow]Sequential Run Length — random jump + linear read, Cold[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Run Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]BW (GB/s)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Jumps[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]ns/jump[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]vs 128B[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]% of Seq[/]").RightAligned());

        // Reference rows
        if (idx128 >= 0)
        {
            table.AddRow(
                new Markup("[grey]Seq (ref)[/]"),
                new Markup($"[grey]{bwSeq,8:F2}[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]    100%[/]"));

            table.AddRow(
                new Markup("[grey]Ind=1 (ref)[/]"),
                new Markup($"[grey]{bwInd1,8:F2}[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]       —[/]"),
                new Markup($"[grey]{bwInd1 / bwSeq * 100,6:F1}%[/]"));
        }

        for (int ri = 0; ri < SeqRunBytes.Length; ri++)
        {
            double bw = _bwSeqRuns[ri];
            int seqBytes = SeqRunBytes[ri];
            int numJumps = (int)(ReadSize / seqBytes);

            // Derive ns per jump: total_time - sequential_time = jump overhead
            // total_time = ReadSize / BW, seq_time = ReadSize / BW_seq
            double totalNs = ReadSize / (bw * 1024 * 1024 * 1024) * 1e9;
            double seqNs = ReadSize / (bwSeq * 1024 * 1024 * 1024) * 1e9;
            double jumpOverheadNs = (totalNs - seqNs) / numJumps;

            double vs128 = _bwSeqRuns[0] > 0 ? bw / _bwSeqRuns[0] : 0;
            double pctOfSeq = bwSeq > 0 ? bw / bwSeq * 100 : 0;

            string color = pctOfSeq >= 25 ? "green" : "white";
            table.AddRow(
                new Markup($"[cyan]{seqBytes,5}[/] B"),
                new Markup($"[{color}]{bw,8:F2}[/]"),
                new Markup($"[white]{numJumps,8:N0}[/]"),
                new Markup($"[white]{jumpOverheadNs,8:F1}[/]"),
                new Markup($"[white]{vs128,7:F2}x[/]"),
                new Markup($"[yellow]{pctOfSeq,6:F1}%[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void PrintPrefetchTable()
    {
        int idx128 = FindConfig("128");
        double bwSeq = idx128 >= 0 ? _bwCold[idx128, 0] : 0;
        double bwInd1 = idx128 >= 0 ? _bwCold[idx128, 1] : 0;

        var table = new Table()
            .Title("[bold yellow]Software Prefetch — 128B random chunks, Cold[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Prefetch[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]BW (GB/s)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]ns/access[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]vs D=0[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]vs Ind=1[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]% of Seq[/]").RightAligned());

        // Reference rows
        if (idx128 >= 0)
        {
            double seqNs = 128.0 / (bwSeq * 1024 * 1024 * 1024) * 1e9;
            table.AddRow(
                new Markup("[grey]Seq (ref)[/]"),
                new Markup($"[grey]{bwSeq,8:F2}[/]"),
                new Markup($"[grey]{seqNs,8:F1}[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]    100%[/]"));

            double ind1Ns = 128.0 / (bwInd1 * 1024 * 1024 * 1024) * 1e9;
            table.AddRow(
                new Markup("[grey]Ind=1 (ref)[/]"),
                new Markup($"[grey]{bwInd1,8:F2}[/]"),
                new Markup($"[grey]{ind1Ns,8:F1}[/]"),
                new Markup("[grey]       —[/]"),
                new Markup("[grey]   1.0x[/]"),
                new Markup($"[grey]{bwInd1 / bwSeq * 100,6:F1}%[/]"));
        }

        for (int di = 0; di < PrefetchDistances.Length; di++)
        {
            double bw = _bwPrefetch[di];
            int dist = PrefetchDistances[di];
            double nsPerAccess = 128.0 / (bw * 1024 * 1024 * 1024) * 1e9;
            double vsD0 = _bwPrefetch[0] > 0 ? bw / _bwPrefetch[0] : 0;
            double vsInd1 = bwInd1 > 0 ? bw / bwInd1 : 0;
            double pctOfSeq = bwSeq > 0 ? bw / bwSeq * 100 : 0;

            string label = dist == 0 ? "D=0 (none)" : $"D={dist}";
            string color = vsD0 >= 1.1 ? "green" : "white";
            table.AddRow(
                new Markup($"[cyan]{label,10}[/]"),
                new Markup($"[{color}]{bw,8:F2}[/]"),
                new Markup($"[white]{nsPerAccess,8:F1}[/]"),
                new Markup($"[white]{vsD0,7:F2}x[/]"),
                new Markup($"[yellow]{vsInd1,7:F2}x[/]"),
                new Markup($"[yellow]{pctOfSeq,6:F1}%[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    // ─── Log File ───────────────────────────────────────────────────────────────

    static string WriteLog()
    {
        string logPath = Path.Combine(Environment.CurrentDirectory, "bandwidth-log.txt");

        using var w = new StreamWriter(logPath);

        w.WriteLine("=== Memory Bandwidth — ACLP & Warm/Cold Analysis ===");
        w.WriteLine($"Date:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"Machine:     {Environment.MachineName}");
        w.WriteLine($"Processors:  {Environment.ProcessorCount}");
        w.WriteLine($"OS:          {Environment.OSVersion}");
        w.WriteLine($"Runtime:     {RuntimeInformation.FrameworkDescription}");
        w.WriteLine($"Data block:  {DataBlockSize / (1024 * 1024 * 1024)} GB ({Alignment}-byte aligned)");
        w.WriteLine($"Read size:   {ReadSize / (1024 * 1024)} MB per test");
        w.WriteLine($"Trash block: {TrashBlockSize / (1024 * 1024)} MB");
        w.WriteLine($"Runs:        {Runs} (trimmed mean: discard {TrimCount} best + {TrimCount} worst)");
        w.WriteLine($"Timer freq:  {Stopwatch.Frequency:N0} Hz");
        w.WriteLine($"DRAM latency:{DramLatencyNs:F0} ns (assumed, for derived metrics)");
        w.WriteLine();

        // Helper: write a 2D table [configIdx, indIdx]
        void WriteTable(string title, Func<int, int, string> cellFn)
        {
            w.WriteLine($"=== {title} ===");
            w.Write("Chunk\\Ind");
            foreach (int ind in Indirections)
            {
                w.Write($"\t{(ind == 0 ? "Seq" : $"Ind={ind}")}");
            }

            w.WriteLine();

            for (int ci = 0; ci < Configs.Length; ci++)
            {
                w.Write($"{Configs[ci].Label}B");
                for (int ii = 0; ii < Indirections.Length; ii++)
                {
                    w.Write($"\t{cellFn(ci, ii)}");
                }

                w.WriteLine();
            }

            w.WriteLine();
        }

        // Bandwidth tables
        WriteTable("Bandwidth (GB/s) — Cold", (ci, ii) => $"{_bwCold[ci, ii]:F2}");
        WriteTable("Bandwidth (GB/s) — Warm", (ci, ii) => $"{_bwWarm[ci, ii]:F2}");

        // Latency
        WriteTable("Latency per access (ns) — Cold", (ci, ii) =>
        {
            double bwBytes = _bwCold[ci, ii] * 1024.0 * 1024.0 * 1024.0;
            return $"{Configs[ci].Size / bwBytes * 1e9:F1}";
        });

        // Ratio vs 64B
        int idx64 = FindConfig("64");
        WriteTable("Ratio vs 64B — Cold (ACLP indicator)", (ci, ii) =>
            $"{_bwCold[ci, ii] / _bwCold[idx64, ii]:F2}x");

        // Ratio vs Sequential
        WriteTable("Ratio vs Sequential — Cold (indirection cost)", (ci, ii) =>
            $"{_bwCold[ci, ii] / _bwCold[ci, 0]:F2}x");

        // Concurrent DRAM requests
        WriteTable($"Concurrent DRAM requests (assumes {DramLatencyNs:F0}ns) — Cold", (ci, ii) =>
        {
            double bwBytes = _bwCold[ci, ii] * 1024.0 * 1024.0 * 1024.0;
            return $"{bwBytes * DramLatencyNs * 1e-9 / Configs[ci].Size:F1}";
        });

        // Warm/Cold ratio
        WriteTable("Warm / Cold ratio", (ci, ii) =>
            $"{_bwWarm[ci, ii] / _bwCold[ci, ii]:F2}x");

        // Effective bytes per DRAM access
        WriteTable("Effective bytes per DRAM access — Cold", (ci, ii) =>
        {
            double bwBytes = _bwCold[ci, ii] * 1024.0 * 1024.0 * 1024.0;
            return $"{bwBytes * DramLatencyNs * 1e-9:F0}B";
        });

        // Stream test results
        w.WriteLine("=== Independent Streams — 128B random, Cold ===");
        w.Write("Streams\tBW (GB/s)\tns/access\tConc. DRAM\tvs 1-stream\tvs Ind=1");
        w.WriteLine();
        int logIdx128 = FindConfig("128");
        double logBwInd1 = logIdx128 >= 0 ? _bwCold[logIdx128, 1] : 1;
        for (int si = 0; si < StreamCounts.Length; si++)
        {
            double bw = _bwStreams[si];
            double nsAcc = 128.0 / (bw * 1024 * 1024 * 1024) * 1e9;
            double conc = bw * 1024 * 1024 * 1024 * DramLatencyNs * 1e-9 / 128;
            double vs1 = _bwStreams[0] > 0 ? bw / _bwStreams[0] : 0;
            double vsI = logBwInd1 > 0 ? bw / logBwInd1 : 0;
            w.WriteLine($"{StreamCounts[si]}\t{bw:F2}\t{nsAcc:F1}\t{conc:F1}\t{vs1:F2}x\t{vsI:F1}x");
        }

        w.WriteLine();

        // Sequential run length results
        w.WriteLine("=== Sequential Run Length — random jump + linear read, Cold ===");
        w.Write("RunBytes\tBW (GB/s)\tJumps\tns/jump\tvs 128B\t% of Seq");
        w.WriteLine();
        double logBwSeq = logIdx128 >= 0 ? _bwCold[logIdx128, 0] : 1;
        for (int ri = 0; ri < SeqRunBytes.Length; ri++)
        {
            double bw = _bwSeqRuns[ri];
            int seqBytes = SeqRunBytes[ri];
            int numJumps = (int)(ReadSize / seqBytes);
            double totalNs = ReadSize / (bw * 1024 * 1024 * 1024) * 1e9;
            double seqNs = ReadSize / (logBwSeq * 1024 * 1024 * 1024) * 1e9;
            double jumpNs = (totalNs - seqNs) / numJumps;
            double vs128 = _bwSeqRuns[0] > 0 ? bw / _bwSeqRuns[0] : 0;
            double pct = logBwSeq > 0 ? bw / logBwSeq * 100 : 0;
            w.WriteLine($"{seqBytes}\t{bw:F2}\t{numJumps}\t{jumpNs:F1}\t{vs128:F2}x\t{pct:F1}%");
        }

        w.WriteLine();

        // Prefetch test results
        w.WriteLine("=== Software Prefetch — 128B random, Cold ===");
        w.Write("PrefetchDist\tBW (GB/s)\tns/access\tvs D=0\tvs Ind=1\t% of Seq");
        w.WriteLine();
        for (int di = 0; di < PrefetchDistances.Length; di++)
        {
            double bw = _bwPrefetch[di];
            int dist = PrefetchDistances[di];
            double nsAcc = 128.0 / (bw * 1024 * 1024 * 1024) * 1e9;
            double vsD0 = _bwPrefetch[0] > 0 ? bw / _bwPrefetch[0] : 0;
            double vsI = logBwInd1 > 0 ? bw / logBwInd1 : 0;
            double pct = logBwSeq > 0 ? bw / logBwSeq * 100 : 0;
            w.WriteLine($"{dist}\t{bw:F2}\t{nsAcc:F1}\t{vsD0:F2}x\t{vsI:F2}x\t{pct:F1}%");
        }

        w.WriteLine();

        // Per-run timings (cold)
        WriteTimings(w, "Per-run timings — Cold (ms, sorted ascending)", _timingsCold);

        // Per-run timings (warm)
        WriteTimings(w, "Per-run timings — Warm (ms, sorted ascending)", _timingsWarm);

        return logPath;
    }

    static void WriteTimings(StreamWriter w, string title, double[,,] timings)
    {
        w.WriteLine($"=== {title} ===");

        for (int ci = 0; ci < Configs.Length; ci++)
        {
            for (int ii = 0; ii < Indirections.Length; ii++)
            {
                string label = Indirections[ii] == 0 ? "Seq" : $"Ind={Indirections[ii]}";
                w.Write($"Chunk={Configs[ci].Label}B {label,-6}: ");

                double[] sorted = new double[Runs];
                for (int r = 0; r < Runs; r++)
                {
                    sorted[r] = timings[ci, ii, r];
                }

                Array.Sort(sorted);

                for (int r = 0; r < Runs; r++)
                {
                    bool trimmed = r < TrimCount || r >= Runs - TrimCount;
                    string mark = trimmed ? $"({sorted[r] * 1000:F2})" : $" {sorted[r] * 1000:F2} ";
                    w.Write(mark);
                }

                double mean = 0;
                for (int r = TrimCount; r < Runs - TrimCount; r++)
                {
                    mean += sorted[r];
                }

                mean /= (Runs - 2 * TrimCount);

                double variance = 0;
                for (int r = TrimCount; r < Runs - TrimCount; r++)
                {
                    double d = sorted[r] - mean;
                    variance += d * d;
                }

                double stddev = Math.Sqrt(variance / (Runs - 2 * TrimCount));
                double cv = mean > 0 ? stddev / mean * 100 : 0;

                w.Write($"  | mean={mean * 1000:F2}ms stddev={stddev * 1000:F3}ms cv={cv:F1}%");
                w.WriteLine();
            }
        }

        w.WriteLine();
    }
}
