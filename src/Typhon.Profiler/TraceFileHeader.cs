using System.Runtime.InteropServices;

namespace Typhon.Profiler;

/// <summary>
/// Fixed 64-byte header at the start of a <c>.typhon-trace</c> file.
/// Contains metadata needed to interpret the trace events that follow.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TraceFileHeader
{
    /// <summary>File magic: ASCII "TYTR" (0x52_54_59_54 little-endian).</summary>
    public uint Magic;

    /// <summary>Format version. Current: 1.</summary>
    public ushort Version;

    /// <summary>Flags (reserved for future use).</summary>
    public ushort Flags;

    /// <summary><c>Stopwatch.Frequency</c> — needed to convert timestamp ticks to real time.</summary>
    public long TimestampFrequency;

    /// <summary>Target tick rate in Hz (e.g., 60.0 for 60 fps).</summary>
    public float BaseTickRate;

    /// <summary>Number of worker threads in the DagScheduler.</summary>
    public byte WorkerCount;

    /// <summary>Number of systems in the DAG.</summary>
    public ushort SystemCount;

    /// <summary>UTC timestamp when the trace was started (DateTime.UtcNow.Ticks).</summary>
    public long CreatedUtcTicks;

    /// <summary>
    /// QPC timestamp (<c>Stopwatch.GetTimestamp()</c>) when the EventPipe sampling session started.
    /// Used to correlate <c>.nettrace</c> relative timestamps with <c>.typhon-trace</c> absolute timestamps.
    /// Zero if CPU sampling was not enabled.
    /// </summary>
    public long SamplingSessionStartQpc;

    /// <summary>Reserved for future use — pads header to 64 bytes.</summary>
    public unsafe fixed byte Reserved[21];

    /// <summary>File magic constant: ASCII "TYTR".</summary>
    public const uint MagicValue = 0x52_54_59_54; // 'T','Y','T','R' little-endian

    /// <summary>Current format version.</summary>
    public const ushort CurrentVersion = 1;
}
