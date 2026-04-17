using System;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Tunable parameters for <c>TyphonProfiler.Start</c>. All defaults are tuned for typical Typhon workloads (~30 producer threads, ~3 exporters).
/// </summary>
public sealed class ProfilerOptions
{
    /// <summary>
    /// Cadence at which the consumer thread wakes to drain all slot rings and fan out to exporters. Default: 1 ms.
    /// Lower = lower end-to-end latency for live viewers, higher CPU. Higher = batched I/O efficiency, more records buffered.
    /// </summary>
    public TimeSpan ConsumerCadence { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Bounded queue depth between the consumer thread and each exporter. Default: 4.
    /// When an exporter is slow, this many cadence ticks of batches can buffer before drop-newest kicks in.
    /// </summary>
    public int PerExporterChannelDepth { get; set; } = 4;

    /// <summary>
    /// Capacity of the consumer's per-pass merge scratch buffer in <b>bytes</b>. Drains from all slots accumulate here before sorting and slicing
    /// into <see cref="TraceRecordBatch"/>es. Default: 512 KB — large enough that one drain pass can produce 2+ full batches from a burst of ~15k
    /// records while keeping the whole scratch in L2 cache on a typical CPU.
    /// </summary>
    public int MergeBufferBytes { get; set; } = 512 * 1024;

    /// <summary>Validates options and throws if any field is invalid.</summary>
    public void Validate()
    {
        if (ConsumerCadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConsumerCadence), "must be > 0");
        }
        if (PerExporterChannelDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PerExporterChannelDepth), "must be ≥ 1");
        }
        if (MergeBufferBytes < TraceRecordBatchPool.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MergeBufferBytes), $"must be ≥ TraceRecordBatchPool.MaxPayloadBytes ({TraceRecordBatchPool.MaxPayloadBytes})");
        }
    }
}
