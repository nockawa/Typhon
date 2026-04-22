using System;
using System.IO;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler.Exporters;

/// <summary>
/// <see cref="IProfilerExporter"/> that writes the typed-event profiler's record stream to a <c>.typhon-trace</c> v3 binary file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format:</b> header + system/archetype/component-type tables + repeated LZ4-compressed record blocks. <see cref="TraceFileWriter"/> handles
/// block framing; this exporter owns the file stream and forwards <see cref="TraceRecordBatch.Payload"/> byte slices to it.
/// </para>
/// <para>
/// <b>Resource tree:</b> derives from <see cref="ResourceNode"/> so the exporter shows up under <c>Profiler/FileExporter</c>. Dispose is idempotent.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="ProcessBatch"/> is called from the dedicated exporter thread. Single writer.
/// </para>
/// </remarks>
public sealed class FileExporter : ResourceNode, IProfilerExporter
{
    private readonly string _filePath;
    private FileStream _stream;
    private TraceFileWriter _writer;
    private bool _disposed;
    private long _batchesProcessed;
    private long _recordsProcessed;

    /// <summary>Diagnostic: how many batches this exporter has written so far.</summary>
    public long BatchesProcessed => _batchesProcessed;

    /// <summary>Diagnostic: total records written (sum of each batch's Count).</summary>
    public long RecordsProcessed => _recordsProcessed;

    public FileExporter(string filePath, IResource parent) : base("FileExporter", ResourceType.Service, parent ?? throw new ArgumentNullException(nameof(parent)))
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Queue = new ExporterQueue(boundedCapacity: 64);
    }

    /// <inheritdoc />
    public ExporterQueue Queue { get; }

    /// <inheritdoc />
    public void Initialize(ProfilerSessionMetadata metadata)
    {
        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        _stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        _writer = new TraceFileWriter(_stream);

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = metadata.StopwatchFrequency,
            BaseTickRate = metadata.BaseTickRate,
            WorkerCount = (byte)metadata.WorkerCount,
            SystemCount = (ushort)metadata.Systems.Length,
            ArchetypeCount = (ushort)metadata.Archetypes.Length,
            ComponentTypeCount = (ushort)metadata.ComponentTypes.Length,
            CreatedUtcTicks = metadata.StartedUtc.Ticks,
            SamplingSessionStartQpc = metadata.SamplingSessionStartQpc,
        };
        _writer.WriteHeader(in header);
        _writer.WriteSystemDefinitions(metadata.Systems);
        _writer.WriteArchetypes(metadata.Archetypes);
        _writer.WriteComponentTypes(metadata.ComponentTypes);
    }

    /// <inheritdoc />
    public void ProcessBatch(TraceRecordBatch batch)
    {
        if (_writer == null || batch.PayloadBytes == 0)
        {
            return;
        }

        _writer.WriteRecords(batch.Payload.AsSpan(0, batch.PayloadBytes), batch.Count);
        Interlocked.Increment(ref _batchesProcessed);
        Interlocked.Add(ref _recordsProcessed, batch.Count);
    }

    /// <inheritdoc />
    public void Flush() => _writer?.Flush();

    /// <inheritdoc />
    void IDisposable.Dispose() => Dispose(true);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            try { _writer?.Dispose(); }
            catch
            {
                // ignored
            }

            _writer = null;
            _stream = null;
            try { Queue?.Dispose(); }
            catch
            {
                // ignored
            }
        }
        base.Dispose(disposing);
    }
}
