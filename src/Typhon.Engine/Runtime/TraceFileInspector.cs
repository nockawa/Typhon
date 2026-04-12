using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// <see cref="IRuntimeInspector"/> implementation that records trace events to a <c>.typhon-trace</c> file.
/// Inherits the SPSC buffer + flush pipeline from <see cref="TraceEventCaptureBase"/>; only the output (file writer + optional CPU sampler) is specialized here.
/// </summary>
public sealed class TraceFileInspector : TraceEventCaptureBase
{
    private readonly string _filePath;
    private readonly bool _enableCpuSampling;
    private TraceFileWriter _writer;
    private EventPipeSampler _sampler;

    /// <summary>
    /// Creates a new trace file inspector.
    /// </summary>
    /// <param name="filePath">Path for the <c>.typhon-trace</c> file.</param>
    /// <param name="enableCpuSampling">
    /// When true, also starts an EventPipe CPU sampling session (~1 KHz managed stack capture).
    /// A companion <c>.nettrace</c> file is created alongside the trace file.
    /// Adds ~0.3-0.5% overhead from periodic EE suspensions.
    /// </param>
    public TraceFileInspector(string filePath, bool enableCpuSampling = false)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _enableCpuSampling = enableCpuSampling;
    }

    protected override void InitializeOutput(SystemDefinition[] systems, int workerCount, float baseTickRate)
    {
        // Start CPU sampling FIRST (so we have the session start QPC for the header)
        long samplingStartQpc = 0;
        if (_enableCpuSampling)
        {
            _sampler = new EventPipeSampler();
            _sampler.Start(_filePath);
            samplingStartQpc = _sampler.SessionStartQpc;
        }

        // Open file and write header + system definitions
        var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        _writer = new TraceFileWriter(stream);

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = Stopwatch.Frequency,
            BaseTickRate = baseTickRate,
            WorkerCount = (byte)workerCount,
            SystemCount = (ushort)systems.Length,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            SamplingSessionStartQpc = samplingStartQpc
        };
        _writer.WriteHeader(in header);

        // Convert SystemDefinition[] to SystemDefinitionRecord[]
        var records = new SystemDefinitionRecord[systems.Length];
        for (var i = 0; i < systems.Length; i++)
        {
            var sys = systems[i];

            // Build predecessors list from successors of other systems
            var predecessors = systems
                .Where(s => s.Successors.Contains(sys.Index))
                .Select(s => (ushort)s.Index)
                .ToArray();

            records[i] = new SystemDefinitionRecord
            {
                Index = (ushort)sys.Index,
                Name = sys.Name,
                Type = (byte)sys.Type,
                Priority = (byte)sys.Priority,
                IsParallel = sys.IsParallelQuery,
                TierFilter = (byte)sys.TierFilter,
                Predecessors = predecessors,
                Successors = sys.Successors.Select(s => (ushort)s).ToArray()
            };
        }

        _writer.WriteSystemDefinitions(records);

        // Write empty span name table (will be updated at shutdown with all interned names)
        _writer.WriteSpanNames(_spanIdToName);
    }

    protected override bool FlushBlock(ReadOnlySpan<TraceEvent> events)
    {
        _writer.WriteEvents(events);
        return true;
    }

    protected override void CloseOutput()
    {
        // Write the span name table at the end of the file (reader scans for it after event blocks)
        _writer?.WriteSpanNames(_spanIdToName);
        _writer?.Flush();
        _sampler?.Stop();
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        base.Dispose();
        _sampler?.Dispose();
        _writer?.Dispose();
    }
}
