using System.Runtime.InteropServices;

namespace Typhon.Profiler;

/// <summary>
/// Fixed 64-byte header at the start of a <c>.typhon-trace</c> file. Contains session-wide metadata that lets the viewer decode the record stream
/// that follows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version 3</b> (Tracy-style typed-event rewrite): file format uses variable-size self-describing records instead of a fixed 64 B struct.
/// Block layout is size-prefixed records, LZ4-compressed per block. Older v1/v2 files are unreadable — the viewer and all tooling are updated
/// in lockstep.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TraceFileHeader
{
    /// <summary>File magic: ASCII "TYTR" (0x52_54_59_54 little-endian).</summary>
    public uint Magic;

    /// <summary>Format version. Current: 3 (variable-size typed records replace the fixed-stride 64 B struct of v2).</summary>
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

    /// <summary>Number of archetypes in the archetype table.</summary>
    public ushort ArchetypeCount;

    /// <summary>Number of component types in the component type table.</summary>
    public ushort ComponentTypeCount;

    /// <summary>UTC timestamp when the trace was started (DateTime.UtcNow.Ticks).</summary>
    public long CreatedUtcTicks;

    /// <summary>
    /// <c>Stopwatch.GetTimestamp()</c> captured when the host's EventPipe CPU-sampling session started, or <c>0</c> if no sampling companion is
    /// attached to this trace. The viewer correlates <c>.nettrace</c> CPU samples into the flame graph by mapping their relative milliseconds
    /// against this anchor in the same <see cref="TimestampFrequency"/> time base the record stream uses.
    /// </summary>
    public long SamplingSessionStartQpc;

    /// <summary>
    /// Engine's scheduler tick number at the moment this Init payload was built. For file-based traces this is always <c>0</c> (files start before
    /// the scheduler runs). For TCP live streams it's the tick the engine was on when the client connected — the server's decoder seeds its
    /// running tick counter with <c>EngineTickAtInit - 1</c> so subsequent TickStart records carry the absolute engine tick number instead of a
    /// 1-based counter that restarts on every reconnect. Old trace files without this field have a zeroed slot here, which falls back to the
    /// legacy "decoder starts counting from 1" behavior — no compatibility break.
    /// </summary>
    public long EngineTickAtInit;

    /// <summary>Reserved for future use.</summary>
    public byte Reserved0;

    /// <summary>File magic constant: ASCII "TYTR".</summary>
    public const uint MagicValue = 0x52_54_59_54; // 'T','Y','T','R' little-endian

    /// <summary>Current format version.</summary>
    public const ushort CurrentVersion = 3;
}
