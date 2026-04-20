using JetBrains.Annotations;
using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Static description of the profiling session, passed to each exporter once via <see cref="IProfilerExporter.Initialize"/>. Holds everything
/// the exporter needs to write the header + metadata tables.
/// </summary>
/// <remarks>
/// All fields are immutable for the lifetime of the session — set once at <c>TyphonProfiler.Start</c> and never mutated. Multiple exporters can
/// safely read them concurrently without synchronization.
/// </remarks>
[PublicAPI]
public sealed class ProfilerSessionMetadata
{
    /// <summary>System DAG metadata captured at session start. Empty array if the profiler is started outside a runtime context.</summary>
    public SystemDefinitionRecord[] Systems { get; }

    /// <summary>Archetype table — maps <c>ArchetypeId</c> numbers in typed events back to human-readable names for the viewer.</summary>
    public ArchetypeRecord[] Archetypes { get; }

    /// <summary>Component type table — maps <c>ComponentTypeId</c> numbers in typed events back to C# type names for the viewer.</summary>
    public ComponentTypeRecord[] ComponentTypes { get; }

    /// <summary>Number of scheduler worker threads at session start. Zero if the profiler is running standalone (no scheduler).</summary>
    public int WorkerCount { get; }

    /// <summary>Target tick rate in Hz (e.g., 60.0). Zero for non-runtime profiling.</summary>
    public float BaseTickRate { get; }

    /// <summary><c>Stopwatch.GetTimestamp()</c> value captured at <c>TyphonProfiler.Start</c>. Anchors all subsequent event timestamps.</summary>
    public long StartTimestamp { get; }

    /// <summary><c>Stopwatch.Frequency</c> at session start. Lets exporters convert ticks to wall-clock time without re-querying.</summary>
    public long StopwatchFrequency { get; }

    /// <summary>UTC wall-clock time when the session started, for human-readable headers.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>
    /// <c>Stopwatch.GetTimestamp()</c> captured when an EventPipe CPU-sampling session started. Zero when no sampling companion is running.
    /// Hosts that attach an EventPipe session (e.g., AntHill profile runner) populate this so the viewer can overlay .nettrace flame graphs on
    /// the same time base as the record stream.
    /// </summary>
    public long SamplingSessionStartQpc { get; }

    /// <summary>
    /// Delegate returning the engine's current scheduler tick number. Called by <c>TcpExporter</c> at TCP-connect time to stamp the Init frame
    /// with "engine was on tick N when this client connected." The viewer uses this to display absolute engine tick numbers rather than a
    /// 1-based counter that restarts on every reconnect. <c>null</c> when the host doesn't run a scheduler (e.g., IO-only profile runs) — in
    /// that case the Init frame carries zero and the server's decoder falls back to its legacy restart-from-1 behavior.
    /// </summary>
    public Func<long> CurrentEngineTickProvider { get; }

    public ProfilerSessionMetadata(SystemDefinitionRecord[] systems, ArchetypeRecord[] archetypes, ComponentTypeRecord[] componentTypes, int workerCount,
        float baseTickRate, long startTimestamp, long stopwatchFrequency, DateTime startedUtc, long samplingSessionStartQpc = 0,
        Func<long> currentEngineTickProvider = null)
    {
        Systems = systems ?? [];
        Archetypes = archetypes ?? [];
        ComponentTypes = componentTypes ?? [];
        WorkerCount = workerCount;
        BaseTickRate = baseTickRate;
        StartTimestamp = startTimestamp;
        StopwatchFrequency = stopwatchFrequency;
        StartedUtc = startedUtc;
        SamplingSessionStartQpc = samplingSessionStartQpc;
        CurrentEngineTickProvider = currentEngineTickProvider;
    }
}
