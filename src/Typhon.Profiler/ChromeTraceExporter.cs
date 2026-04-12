using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Typhon.Profiler;

/// <summary>
/// Exports a <c>.typhon-trace</c> file to Chrome Trace JSON format, viewable in <c>chrome://tracing</c> or <a href="https://ui.perfetto.dev">Perfetto UI</a>.
/// </summary>
/// <remarks>
/// Mapping:
/// <list type="bullet">
///   <item>Each worker thread → a separate <c>tid</c></item>
///   <item>Each tick → process (<c>pid</c>) = 1, with tick boundaries as instant events</item>
///   <item>Each system chunk → a complete event (<c>ph: "X"</c>) with duration</item>
///   <item>Tick phases → complete events on a dedicated "Phases" thread</item>
///   <item>Typhon-specific data (entities, skip reason, tier) → <c>args</c> dict</item>
/// </list>
/// </remarks>
public static class ChromeTraceExporter
{
    /// <summary>
    /// Exports a trace file to Chrome Trace JSON format.
    /// </summary>
    /// <param name="traceFilePath">Path to the <c>.typhon-trace</c> file.</param>
    /// <param name="outputStream">Stream to write JSON output to.</param>
    public static void Export(string traceFilePath, Stream outputStream)
    {
        using var reader = new TraceFileReader(File.OpenRead(traceFilePath));
        var header = reader.ReadHeader();
        var systems = reader.ReadSystemDefinitions();
        var events = reader.ReadAllEvents();

        Export(header, systems, events, outputStream);
    }

    /// <summary>
    /// Exports pre-loaded trace data to Chrome Trace JSON format.
    /// </summary>
    public static void Export(TraceFileHeader header, IReadOnlyList<SystemDefinitionRecord> systems, IReadOnlyList<TraceEvent> events, Stream outputStream)
    {
        var ticksPerUs = header.TimestampFrequency / 1_000_000.0;

        using var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteStartArray("traceEvents");

        // Write process and thread metadata
        WriteMetadata(writer, header, systems);

        // Track phase starts for computing durations
        var phaseStarts = new Dictionary<TickPhase, long>();

        // Track chunk starts for computing durations: key = (systemIndex, chunkIndex, workerId)
        var chunkStarts = new Dictionary<(ushort, ushort, byte), long>();

        foreach (var evt in events)
        {
            var timestampUs = evt.TimestampTicks / ticksPerUs;

            switch (evt.EventType)
            {
                case TraceEventType.TickStart:
                    // Instant event marking tick boundary
                    WriteInstant(writer, "TickStart", timestampUs, 0, 0,
                        w => w.WriteNumber("tickNumber", evt.TickNumber));
                    phaseStarts.Clear();
                    chunkStarts.Clear();
                    break;

                case TraceEventType.TickEnd:
                    var overloadLevel = evt.Payload & 0xFF;
                    var tickMultiplier = (evt.Payload >> 8) & 0xFF;
                    WriteInstant(writer, "TickEnd", timestampUs, 0, 0, w =>
                    {
                        w.WriteNumber("tickNumber", evt.TickNumber);
                        w.WriteNumber("overloadLevel", overloadLevel);
                        w.WriteNumber("tickMultiplier", tickMultiplier);
                    });
                    break;

                case TraceEventType.PhaseStart:
                    phaseStarts[evt.Phase] = evt.TimestampTicks;
                    break;

                case TraceEventType.PhaseEnd:
                    if (phaseStarts.TryGetValue(evt.Phase, out var phaseStart))
                    {
                        var durationUs = (evt.TimestampTicks - phaseStart) / ticksPerUs;
                        // Phases rendered on thread ID = workerCount + 1 (dedicated "Phases" lane)
                        WriteComplete(writer, evt.Phase.ToString(), timestampUs - durationUs, durationUs,
                            1, header.WorkerCount + 1, null);
                    }

                    break;

                case TraceEventType.SystemReady:
                    WriteInstant(writer, GetSystemName(systems, evt.SystemIndex) + " Ready",
                        timestampUs, 1, header.WorkerCount + 2, null);
                    break;

                case TraceEventType.ChunkStart:
                    chunkStarts[(evt.SystemIndex, evt.ChunkIndex, evt.WorkerId)] = evt.TimestampTicks;
                    break;

                case TraceEventType.ChunkEnd:
                    var key = (evt.SystemIndex, evt.ChunkIndex, evt.WorkerId);
                    if (chunkStarts.TryGetValue(key, out var chunkStart))
                    {
                        var durationUs2 = (evt.TimestampTicks - chunkStart) / ticksPerUs;
                        var sysName = GetSystemName(systems, evt.SystemIndex);
                        var name = evt.ChunkIndex > 0 || IsParallel(systems, evt.SystemIndex)
                            ? $"{sysName}[{evt.ChunkIndex}]"
                            : sysName;

                        WriteComplete(writer, name, timestampUs - durationUs2, durationUs2,
                            1, evt.WorkerId, w =>
                            {
                                w.WriteNumber("systemIndex", evt.SystemIndex);
                                w.WriteNumber("chunkIndex", evt.ChunkIndex);
                                w.WriteNumber("entities", evt.EntitiesProcessed);
                                if (IsParallel(systems, evt.SystemIndex))
                                {
                                    w.WriteNumber("totalChunks", evt.Payload);
                                }
                            });
                    }

                    break;

                case TraceEventType.SystemSkipped:
                    WriteInstant(writer, GetSystemName(systems, evt.SystemIndex) + " [SKIPPED]",
                        timestampUs, 1, header.WorkerCount + 2,
                        w => w.WriteString("reason", ((SkipReasonCode)evt.SkipReason).ToString()));
                    break;
            }
        }

        writer.WriteEndArray(); // traceEvents

        // Write metadata
        writer.WriteString("displayTimeUnit", "us");
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, TraceFileHeader header, IReadOnlyList<SystemDefinitionRecord> systems)
    {
        // Process name
        WriteMetadataEvent(writer, "process_name", 1, 0, w => w.WriteString("name", "Typhon Runtime"));

        // Thread names for each worker
        for (var i = 0; i < header.WorkerCount; i++)
        {
            WriteMetadataEvent(writer, "thread_name", 1, i,
                w => w.WriteString("name", $"Worker {i}"));
        }

        // Phases thread
        WriteMetadataEvent(writer, "thread_name", 1, header.WorkerCount + 1,
            w => w.WriteString("name", "Tick Phases"));

        // DAG events thread
        WriteMetadataEvent(writer, "thread_name", 1, header.WorkerCount + 2,
            w => w.WriteString("name", "DAG Events"));
    }

    private static void WriteComplete(Utf8JsonWriter writer, string name, double timestampUs, double durationUs, int pid, int tid, Action<Utf8JsonWriter> writeArgs)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "X");
        writer.WriteString("name", name);
        writer.WriteNumber("ts", Math.Round(timestampUs, 3));
        writer.WriteNumber("dur", Math.Round(durationUs, 3));
        writer.WriteNumber("pid", pid);
        writer.WriteNumber("tid", tid);

        if (writeArgs != null)
        {
            writer.WriteStartObject("args");
            writeArgs(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteInstant(Utf8JsonWriter writer, string name, double timestampUs, int pid, int tid, Action<Utf8JsonWriter> writeArgs)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "i");
        writer.WriteString("name", name);
        writer.WriteNumber("ts", Math.Round(timestampUs, 3));
        writer.WriteNumber("pid", pid);
        writer.WriteNumber("tid", tid);
        writer.WriteString("s", "t"); // thread scope

        if (writeArgs != null)
        {
            writer.WriteStartObject("args");
            writeArgs(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteMetadataEvent(Utf8JsonWriter writer, string name, int pid, int tid, Action<Utf8JsonWriter> writeArgs)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "M");
        writer.WriteString("name", name);
        writer.WriteNumber("pid", pid);
        writer.WriteNumber("tid", tid);

        writer.WriteStartObject("args");
        writeArgs(writer);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static string GetSystemName(IReadOnlyList<SystemDefinitionRecord> systems, ushort index) =>
        index < systems.Count ? systems[index].Name : $"System[{index}]";

    private static bool IsParallel(IReadOnlyList<SystemDefinitionRecord> systems, ushort index) => index < systems.Count && systems[index].IsParallel;

    /// <summary>
    /// Mirror of Typhon.Engine's SkipReason enum for display purposes.
    /// </summary>
    private enum SkipReasonCode : byte
    {
        NotSkipped = 0,
        RunIfFalse = 1,
        EmptyInput = 2,
        EmptyEvents = 3,
        Throttled = 4,
        Shed = 5,
        Exception = 6,
        DependencyFailed = 7
    }
}
