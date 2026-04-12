using System;
using System.Buffers.Binary;

namespace Typhon.Profiler;

/// <summary>
/// Frame types for the live streaming TCP protocol between the engine and profiler server.
/// </summary>
public enum LiveFrameType : byte
{
    /// <summary>Session initialization: TraceFileHeader + SystemDefinitionTable + SpanNameTable.</summary>
    Init = 0x01,

    /// <summary>Compressed event block (identical to .typhon-trace file block format).</summary>
    TickEvents = 0x02,

    /// <summary>Incremental span name entries (only newly interned names since last send).</summary>
    SpanNames = 0x03,

    /// <summary>Graceful shutdown signal (empty payload).</summary>
    Shutdown = 0xFF
}

/// <summary>
/// Wire protocol helpers for the live streaming framed binary protocol.
/// Frame envelope: <c>[1B FrameType] [4B PayloadLength] [PayloadLength bytes Payload]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Endianness:</b> all multi-byte integers are little-endian. This applies to the 4-byte payload length
/// in the frame header, the 12-byte block header inside TICK_EVENTS frames, and the <see cref="TraceFileHeader"/>
/// struct inside the INIT payload. Supported architectures (x86/x64/ARM64) are all little-endian natively, so
/// <see cref="System.Runtime.InteropServices.MemoryMarshal.Read{T}"/> on blittable structs reinterprets the bytes
/// correctly. Running on big-endian hardware (s390x, some MIPS) is unsupported and will produce corrupted output.
/// </para>
/// <para>
/// <b>Framing model:</b> unidirectional (engine → server). The engine never reads from the socket. A single client
/// is accepted at a time; additional connections are rejected. On disconnect, the engine drops all buffered events
/// until a new client connects and receives a fresh INIT frame.
/// </para>
/// </remarks>
public static class LiveStreamProtocol
{
    /// <summary>Size of the frame envelope header in bytes (1 type + 4 length).</summary>
    public const int FrameHeaderSize = 5;

    /// <summary>Default TCP port for the live stream listener.</summary>
    public const int DefaultPort = 9001;

    /// <summary>Writes a frame header into the destination span.</summary>
    public static void WriteFrameHeader(Span<byte> dest, LiveFrameType type, int payloadLength)
    {
        dest[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(dest[1..], payloadLength);
    }

    /// <summary>Reads a frame header from the source span.</summary>
    public static (LiveFrameType Type, int PayloadLength) ReadFrameHeader(ReadOnlySpan<byte> source) 
        => ((LiveFrameType)source[0], BinaryPrimitives.ReadInt32LittleEndian(source[1..]));
}
