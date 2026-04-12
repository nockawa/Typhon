using Microsoft.Extensions.Logging;

namespace Typhon.Profiler.Server;

/// <summary>Source-generated log messages for <see cref="LiveSessionService"/>.</summary>
public sealed partial class LiveSessionService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "LiveSessionService starting — will connect to {Host}:{Port}")]
    private partial void LogStarting(string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to engine at {Host}:{Port}")]
    private partial void LogConnected(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection to engine lost, reconnecting...")]
    private partial void LogConnectionLost();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error in live session")]
    private partial void LogUnexpectedError(System.Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine sent SHUTDOWN frame")]
    private partial void LogShutdownReceived();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown frame type: 0x{Type:X2}")]
    private partial void LogUnknownFrame(byte type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed {FrameType} frame ({Reason}) — dropping")]
    private partial void LogMalformedFrame(string frameType, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "INIT received: {SystemCount} systems, {WorkerCount} workers, {Rate:F0} Hz")]
    private partial void LogInitReceived(int systemCount, int workerCount, float rate);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LZ4 decompression mismatch: expected {Expected}, got {Got}")]
    private partial void LogDecompressionMismatch(int expected, int got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received {Count} new span names")]
    private partial void LogSpanNamesReceived(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "SSE subscriber {Id} connected (total: {Count})")]
    private partial void LogSubscriberConnected(int id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "SSE subscriber {Id} disconnected (total: {Count})")]
    private partial void LogSubscriberDisconnected(int id, int count);
}
