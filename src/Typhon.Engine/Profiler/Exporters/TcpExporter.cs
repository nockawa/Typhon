using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using K4os.Compression.LZ4;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler.Exporters;

/// <summary>
/// <see cref="IProfilerExporter"/> that streams typed-event record batches over TCP to a single connected client (typically the browser-based
/// profiler viewer).
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing:</b> <see cref="LiveStreamProtocol"/> envelopes. On connect, the exporter sends an <see cref="LiveFrameType.Init"/> frame whose
/// payload is the v3 file header + system / archetype / component-type tables (identical binary format to the file writer). Subsequent batches
/// are sent as <see cref="LiveFrameType.Block"/> frames: <see cref="TraceBlockEncoder.BlockHeaderSize"/> + LZ4-compressed record bytes.
/// </para>
/// <para>
/// <b>Single-client model:</b> listens on one port, accepts one client at a time, rejects additional connections.
/// </para>
/// <para>
/// <b>Non-blocking sends:</b> after the initial Init frame is delivered in blocking mode, the socket is switched to non-blocking. Subsequent
/// sends treat <see cref="SocketError.WouldBlock"/> as a drop — the exporter thread never blocks on a slow client.
/// </para>
/// </remarks>
public sealed class TcpExporter : ResourceNode, IProfilerExporter
{
    private readonly int _port;
    private TcpListener _listener;
    private Thread _acceptThread;
    private bool _shutdown;
    private Socket _client;
    private long _droppedFrames;

    private byte[] _compressedBuffer;
    private byte[] _frameBuffer;
    private ProfilerSessionMetadata _metadata;

    private bool _disposed;

    public TcpExporter(int port, IResource parent) : base("TcpExporter", ResourceType.Service, parent ?? throw new ArgumentNullException(nameof(parent)))
    {
        _port = port;
        // Match <see cref="FileExporter"/>'s capacity — 64 gives the socket-send path ~16 MB of slack, enough to absorb a single// gcChurn-class burst
        // without drop-newest firing. Previously 4, which was too tight for any workload with multi-tick-spanning// I/O pressure. See FileExporter ctor for
        // why not 256.
        Queue = new ExporterQueue(boundedCapacity: 64);
    }

    /// <inheritdoc />
    public string Name => "TcpExporter";

    /// <inheritdoc />
    public ExporterQueue Queue { get; }

    /// <summary>Number of frames dropped because the socket was not write-ready or partially sent.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <inheritdoc />
    public void Initialize(ProfilerSessionMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

        _compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(TraceRecordBatchPool.MaxPayloadBytes)];
        _frameBuffer = new byte[LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize + _compressedBuffer.Length];

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(1);

        _acceptThread = new Thread(AcceptLoop)
        {
            Name = "TyphonProfilerTcpExporterAccept",
            IsBackground = true
        };
        _acceptThread.Start();
    }

    /// <inheritdoc />
    public void ProcessBatch(TraceRecordBatch batch)
    {
        var client = _client;
        if (client == null || batch.PayloadBytes == 0)
        {
            return;
        }

        // Frame layout: [5B envelope][12B block header][LZ4-compressed records]
        var fb = _frameBuffer.AsSpan();
        var blockHeader = fb.Slice(LiveStreamProtocol.FrameHeaderSize, TraceBlockEncoder.BlockHeaderSize);
        var compressedSlot = fb[(LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize)..];

        var compressedSize = TraceBlockEncoder.EncodeBlock(
            batch.Payload.AsSpan(0, batch.PayloadBytes),
            batch.Count,
            compressedSlot,
            blockHeader);

        var payloadSize = TraceBlockEncoder.BlockHeaderSize + compressedSize;
        LiveStreamProtocol.WriteFrameHeader(fb, LiveFrameType.Block, payloadSize);

        TrySendAll(client, _frameBuffer, 0, LiveStreamProtocol.FrameHeaderSize + payloadSize);
    }

    /// <inheritdoc />
    public void Flush()
    {
        var client = _client;
        if (client != null)
        {
            var shutdownFrame = new byte[LiveStreamProtocol.FrameHeaderSize];
            LiveStreamProtocol.WriteFrameHeader(shutdownFrame, LiveFrameType.Shutdown, 0);
            TrySendAll(client, shutdownFrame, 0, shutdownFrame.Length);
        }

        _shutdown = true;
        try { _listener?.Stop(); }
        catch
        {
            // ignored
        }

        _acceptThread?.Join(500);
    }

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
            _shutdown = true;
            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                try { client.Shutdown(SocketShutdown.Both); }
                catch
                {
                    // ignored
                }

                try { client.Close(); }
                catch
                {
                    // ignored
                }
            }
            try { _listener?.Stop(); }
            catch
            {
                // ignored
            }

            _acceptThread?.Join(2000);
            try { Queue?.Dispose(); }
            catch
            {
                // ignored
            }
        }
        base.Dispose(disposing);
    }

    private void TrySendAll(Socket client, byte[] buffer, int offset, int length)
    {
        try
        {
            var sent = client.Send(buffer, offset, length, SocketFlags.None, out var error);
            if (error == SocketError.Success && sent == length)
            {
                return;
            }

            if (error == SocketError.WouldBlock)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }
            Interlocked.Increment(ref _droppedFrames);
            DisposeClient(client);
        }
        catch (SocketException)
        {
            DisposeClient(client);
        }
        catch (ObjectDisposedException)
        {
            _client = null;
        }
    }

    private void DisposeClient(Socket expected)
    {
        if (Interlocked.CompareExchange(ref _client, null, expected) == expected)
        {
            try { expected.Close(); }
            catch
            {
                // ignored
            }
        }
    }

    private void AcceptLoop()
    {
        while (!_shutdown)
        {
            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch (SocketException) when (_shutdown) { break; }
            catch (ObjectDisposedException) { break; }

            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            if (_client != null)
            {
                try { socket.Close(); }
                catch
                {
                    // ignored
                }

                continue;
            }

            try
            {
                var initPayload = BuildInitPayload();
                var frameBuffer = new byte[LiveStreamProtocol.FrameHeaderSize + initPayload.Length];
                LiveStreamProtocol.WriteFrameHeader(frameBuffer.AsSpan(), LiveFrameType.Init, initPayload.Length);
                initPayload.CopyTo(frameBuffer.AsSpan(LiveStreamProtocol.FrameHeaderSize));

                socket.SendTimeout = 5000;
                socket.Send(frameBuffer);
                socket.Blocking = false;
                _client = socket;
            }
            catch (SocketException)
            {
                try { socket.Close(); }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>
    /// Build the INIT frame payload — identical layout to the first four sections of a <c>.typhon-trace</c> file: header + system defs
    /// + archetype table + component type table.
    /// </summary>
    private byte[] BuildInitPayload()
    {
        using var ms = new MemoryStream();

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = _metadata.StopwatchFrequency,
            BaseTickRate = _metadata.BaseTickRate,
            WorkerCount = (byte)_metadata.WorkerCount,
            SystemCount = (ushort)_metadata.Systems.Length,
            ArchetypeCount = (ushort)_metadata.Archetypes.Length,
            ComponentTypeCount = (ushort)_metadata.ComponentTypes.Length,
            CreatedUtcTicks = _metadata.StartedUtc.Ticks,
            SamplingSessionStartQpc = _metadata.SamplingSessionStartQpc,
        };
        ms.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1)));

        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // System definitions
        bw.Write((ushort)_metadata.Systems.Length);
        foreach (var sys in _metadata.Systems)
        {
            bw.Write(sys.Index);
            WriteShortString(bw, sys.Name);
            bw.Write(sys.Type);
            bw.Write(sys.Priority);
            bw.Write(sys.IsParallel);
            bw.Write(sys.TierFilter);

            bw.Write((byte)sys.Predecessors.Length);
            foreach (var pred in sys.Predecessors)
            {
                bw.Write(pred);
            }

            bw.Write((byte)sys.Successors.Length);
            foreach (var succ in sys.Successors)
            {
                bw.Write(succ);
            }
        }

        // Archetype table
        bw.Write((ushort)_metadata.Archetypes.Length);
        foreach (var a in _metadata.Archetypes)
        {
            bw.Write(a.ArchetypeId);
            WriteShortString(bw, a.Name);
        }

        // Component type table
        bw.Write((ushort)_metadata.ComponentTypes.Length);
        foreach (var c in _metadata.ComponentTypes)
        {
            bw.Write(c.ComponentTypeId);
            WriteShortString(bw, c.Name);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteShortString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var len = (byte)Math.Min(bytes.Length, 255);
        bw.Write(len);
        bw.Write(bytes, 0, len);
    }
}
