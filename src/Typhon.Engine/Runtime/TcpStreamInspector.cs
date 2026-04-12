using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// <see cref="IRuntimeInspector"/> implementation that streams trace events over TCP to the profiler server in real time.
/// Inherits the SPSC buffer + flush pipeline from <see cref="TraceEventCaptureBase"/>; this class owns the TCP listener, non-blocking send path, and INIT framing.
/// </summary>
/// <remarks>
/// <para>
/// The inspector listens on a TCP port and accepts exactly one client (the profiler server). On connection, it sends an INIT frame with the trace header,
/// system definitions, and current span names. On each tick end, the base class flushes per-thread buffers and calls <see cref="FlushBlock"/>, which
/// delta-encodes, LZ4-compresses, and sends a TICK_EVENTS frame.
/// </para>
/// <para>
/// The socket is switched to non-blocking mode after the INIT frame. <see cref="FlushBlock"/> uses <c>Send(..., out SocketError)</c> and treats
/// <c>WouldBlock</c> as a drop — the timer thread never blocks on I/O. A partial send closes the socket because the receiver would be mid-frame.
/// </para>
/// </remarks>
public sealed partial class TcpStreamInspector : TraceEventCaptureBase
{
    private readonly int _port;
    private readonly ILogger _logger;

    // TCP state
    private TcpListener _listener;
    private volatile Socket _client;
    private Thread _acceptThread;
    private volatile bool _shutdown;
    private byte[] _initPayload;
    private int _initPayloadSpanTableOffset;  // offset of SpanNameTableMagic in _initPayload
    private long _droppedFrames;

    // Compression + frame buffers (reused per tick)
    private byte[] _rawBuffer;
    private byte[] _frameBuffer;

    /// <summary>First span ID that has NOT yet been sent to the client. Advanced after a successful SPAN_NAMES send.</summary>
    private int _lastSentSpanId;

    /// <summary>Number of frames dropped because the socket was not write-ready or partial send.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public TcpStreamInspector(int port = LiveStreamProtocol.DefaultPort, ILogger logger = null)
    {
        _port = port;
        _logger = logger ?? NullLogger.Instance;
    }

    // ═══════════════════════════════════════════════════════════════
    // Subclass hooks
    // ═══════════════════════════════════════════════════════════════

    protected override void InitializeOutput(SystemDefinition[] systems, int workerCount, float baseTickRate)
    {
        // Allocate raw buffer for event serialization + delta encoding
        var rawBufferSize = MaxEventsPerBlock * TraceBlockEncoder.EventSize;
        _rawBuffer = new byte[rawBufferSize];

        // Frame buffer: frame envelope + block header + worst-case LZ4 output (compresses in-place into the frame buffer)
        var maxCompressedSize = LZ4Codec.MaximumOutputSize(rawBufferSize);
        _frameBuffer = new byte[LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize + maxCompressedSize];

        // Pre-serialize INIT payload: header + system defs + empty span names
        _initPayload = BuildInitPayload(systems, workerCount, baseTickRate);

        // Start TCP listener + accept thread
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(1);

        _acceptThread = new Thread(AcceptLoop)
        {
            Name = "TcpStreamInspector.Accept",
            IsBackground = true
        };
        _acceptThread.Start();

        LogListenerStarted(_port, workerCount, baseTickRate);
    }

    protected override bool OnBeforeFlush()
    {
        var client = _client;
        if (client == null)
        {
            return false; // No client — base class will skip all FlushBlock calls
        }

        return SendIncrementalSpanNames(client);
    }

    protected override bool FlushBlock(ReadOnlySpan<TraceEvent> events)
    {
        var client = _client;
        if (client == null)
        {
            return false;
        }

        // Build the frame in _frameBuffer: [5B frame header][12B block header][LZ4 data]
        // TraceBlockEncoder compresses directly into the frame buffer — no intermediate copy.
        var fb = _frameBuffer.AsSpan();
        var blockHeader = fb.Slice(LiveStreamProtocol.FrameHeaderSize, TraceBlockEncoder.BlockHeaderSize);
        var compressedSlot = fb[(LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize)..];

        var compressedSize = TraceBlockEncoder.EncodeBlock(events, _rawBuffer, compressedSlot, blockHeader);

        var payloadSize = TraceBlockEncoder.BlockHeaderSize + compressedSize;
        var frameSize = LiveStreamProtocol.FrameHeaderSize + payloadSize;
        LiveStreamProtocol.WriteFrameHeader(fb, LiveFrameType.TickEvents, payloadSize);

        return TrySendAll(client, _frameBuffer, 0, frameSize);
    }

    protected override void CloseOutput()
    {
        LogShuttingDown(Interlocked.Read(ref _droppedFrames));

        // Send shutdown frame using the non-blocking send path
        var client = _client;
        if (client != null)
        {
            var shutdownFrame = new byte[LiveStreamProtocol.FrameHeaderSize];
            LiveStreamProtocol.WriteFrameHeader(shutdownFrame, LiveFrameType.Shutdown, 0);
            TrySendAll(client, shutdownFrame, 0, shutdownFrame.Length);
        }

        // Shut down the accept loop and join it so no new connection can race dispose
        _shutdown = true;
        try { _listener?.Stop(); } catch { }
        _acceptThread?.Join(500);
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        base.Dispose();
        _shutdown = true;

        // Swap _client to null before closing to avoid concurrent use by stragglers
        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            try { client.Shutdown(SocketShutdown.Both); } catch { }
            try { client.Close(); } catch { }
        }

        try { _listener?.Stop(); } catch { }
        _acceptThread?.Join(2000);
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-blocking TCP send
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to send all <paramref name="length"/> bytes on a non-blocking socket.
    /// Returns true if the full frame was sent, false otherwise (caller should give up on this client).
    /// On WouldBlock, increments the drop counter and returns false.
    /// On partial send or any other error, closes the socket (the peer is now mid-frame and cannot recover).
    /// </summary>
    private bool TrySendAll(Socket client, byte[] buffer, int offset, int length)
    {
        try
        {
            var sent = client.Send(buffer, offset, length, SocketFlags.None, out var error);

            if (error == SocketError.Success && sent == length)
            {
                return true;
            }

            if (error == SocketError.WouldBlock)
            {
                Interlocked.Increment(ref _droppedFrames);
                return false;
            }

            // Any other error, or partial send — tear down
            Interlocked.Increment(ref _droppedFrames);
            DisposeClient(client);
            return false;
        }
        catch (SocketException)
        {
            DisposeClient(client);
            return false;
        }
        catch (ObjectDisposedException)
        {
            _client = null;
            return false;
        }
    }

    /// <summary>Closes the given socket if it is still the current client. Uses CompareExchange to avoid leaking a replacement on concurrent failure.</summary>
    private void DisposeClient(Socket expected)
    {
        if (Interlocked.CompareExchange(ref _client, null, expected) == expected)
        {
            try { expected.Close(); } catch { }
            LogClientDisconnected(Interlocked.Read(ref _droppedFrames));
        }
    }

    /// <summary>
    /// Sends any newly-interned span names as a SPAN_NAMES frame. Returns true if nothing to send, or if the send succeeded. Returns false if the socket died.
    /// </summary>
    private bool SendIncrementalSpanNames(Socket client)
    {
        int nextId;
        int sendFromId;
        int countToSend;

        lock (_spanNameToId)
        {
            nextId = _nextSpanNameId;
            sendFromId = _lastSentSpanId;
            countToSend = nextId - sendFromId;
            if (countToSend <= 0)
            {
                return true;
            }
        }

        // Serialize: [uint16 count] [foreach: uint16 id, uint8 nameLen, UTF8 bytes]
        // Max per-name size is 2 + 1 + 255 = 258 bytes. If the batch fits in the reusable frame buffer, use it; otherwise fall back to a one-off allocation.
        var maxPayloadSize = 2 + countToSend * 258;
        var usesFrameBuffer = maxPayloadSize <= _frameBuffer.Length - LiveStreamProtocol.FrameHeaderSize;
        var scratch = usesFrameBuffer
            ? _frameBuffer.AsSpan(LiveStreamProtocol.FrameHeaderSize)
            : new byte[maxPayloadSize].AsSpan();

        var pos = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(scratch[pos..], (ushort)countToSend);
        pos += 2;

        lock (_spanNameToId)
        {
            for (var id = sendFromId; id < nextId; id++)
            {
                if (!_spanIdToName.TryGetValue(id, out var name))
                {
                    continue;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(scratch[pos..], (ushort)id);
                pos += 2;

                var nameByteCount = Encoding.UTF8.GetByteCount(name);
                var clamped = Math.Min(nameByteCount, 255);
                scratch[pos++] = (byte)clamped;

                var written = Encoding.UTF8.GetBytes(name.AsSpan(), scratch.Slice(pos, clamped));
                pos += written;
            }
        }

        var payloadSize = pos;

        if (usesFrameBuffer)
        {
            LiveStreamProtocol.WriteFrameHeader(_frameBuffer.AsSpan(), LiveFrameType.SpanNames, payloadSize);
            if (!TrySendAll(client, _frameBuffer, 0, LiveStreamProtocol.FrameHeaderSize + payloadSize))
            {
                return false;
            }
        }
        else
        {
            var tmp = new byte[LiveStreamProtocol.FrameHeaderSize + payloadSize];
            LiveStreamProtocol.WriteFrameHeader(tmp.AsSpan(), LiveFrameType.SpanNames, payloadSize);
            scratch[..payloadSize].CopyTo(tmp.AsSpan(LiveStreamProtocol.FrameHeaderSize));
            if (!TrySendAll(client, tmp, 0, tmp.Length))
            {
                return false;
            }
        }

        lock (_spanNameToId)
        {
            _lastSentSpanId = nextId;
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // TCP accept loop
    // ═══════════════════════════════════════════════════════════════

    private void AcceptLoop()
    {
        while (!_shutdown)
        {
            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch (SocketException) when (_shutdown)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            socket.NoDelay = true;
            // Enable TCP keepalive so a half-closed peer eventually frees the slot instead of lingering
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var remoteEp = socket.RemoteEndPoint?.ToString() ?? "<unknown>";

            // Single client only — reject additional connections
            if (_client != null)
            {
                LogDuplicateRejected(remoteEp);
                try { socket.Close(); } catch { }
                continue;
            }

            // Send INIT frame with current state. INIT is sent in BLOCKING mode on the accept thread — we need to guarantee delivery of session metadata
            // before flipping the socket to non-blocking for the timer thread's hot-path sends.
            try
            {
                byte[] currentInit;
                int currentSpanCount;
                lock (_spanNameToId)
                {
                    currentSpanCount = _nextSpanNameId;
                    currentInit = currentSpanCount > 0
                        ? RebuildInitPayloadWithSpanNames()
                        : _initPayload;
                }

                var frameBuffer = new byte[LiveStreamProtocol.FrameHeaderSize + currentInit.Length];
                LiveStreamProtocol.WriteFrameHeader(frameBuffer.AsSpan(), LiveFrameType.Init, currentInit.Length);
                currentInit.CopyTo(frameBuffer.AsSpan(LiveStreamProtocol.FrameHeaderSize));

                socket.SendTimeout = 5000; // generous timeout for the one-shot INIT write
                socket.Send(frameBuffer);

                // Flip to non-blocking for the timer-thread send loop
                socket.Blocking = false;

                lock (_spanNameToId)
                {
                    _lastSentSpanId = currentSpanCount;
                }

                _client = socket;
                LogClientConnected(remoteEp);
            }
            catch (SocketException ex)
            {
                LogInitFailed(ex.SocketErrorCode.ToString());
                try { socket.Close(); } catch { }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // INIT payload serialization
    // ═══════════════════════════════════════════════════════════════

    private byte[] BuildInitPayload(SystemDefinition[] systems, int workerCount, float baseTickRate)
    {
        using var ms = new MemoryStream();

        // Write header (64 bytes, same as file format)
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
            SamplingSessionStartQpc = 0
        };
        ms.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1)));

        // Write system definitions (same binary format as TraceFileWriter)
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        var records = BuildSystemRecords(systems);

        bw.Write((ushort)records.Length);
        foreach (var sys in records)
        {
            bw.Write(sys.Index);

            var nameBytes = Encoding.UTF8.GetBytes(sys.Name);
            bw.Write((byte)Math.Min(nameBytes.Length, 255));
            bw.Write(nameBytes, 0, Math.Min(nameBytes.Length, 255));

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

        // Record the exact offset of the span name table so we can replace it deterministically on reconnect without scanning for the magic
        // (which could collide with bytes inside a system name like "SpawnManager").
        bw.Flush();
        _initPayloadSpanTableOffset = (int)ms.Position;

        // Write empty span name table (magic + count=0)
        bw.Write(TraceFileWriter.SpanNameTableMagic);
        bw.Write((ushort)0);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Re-serialize the INIT payload with the current span name table. Caller must hold the <c>_spanNameToId</c> lock.</summary>
    private byte[] RebuildInitPayloadWithSpanNames()
    {
        using var ms = new MemoryStream();

        // Copy header + system defs verbatim (everything before the span table)
        ms.Write(_initPayload.AsSpan(0, _initPayloadSpanTableOffset));

        // Write the current span name table
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(TraceFileWriter.SpanNameTableMagic);
        bw.Write((ushort)_spanIdToName.Count);
        foreach (var kv in _spanIdToName)
        {
            bw.Write((ushort)kv.Key);
            var nameBytes = Encoding.UTF8.GetBytes(kv.Value);
            bw.Write((byte)Math.Min(nameBytes.Length, 255));
            bw.Write(nameBytes, 0, Math.Min(nameBytes.Length, 255));
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static SystemDefinitionRecord[] BuildSystemRecords(SystemDefinition[] systems)
    {
        var records = new SystemDefinitionRecord[systems.Length];
        for (var i = 0; i < systems.Length; i++)
        {
            var sys = systems[i];
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

        return records;
    }
}
