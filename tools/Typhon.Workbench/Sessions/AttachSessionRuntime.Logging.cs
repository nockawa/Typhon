using Microsoft.Extensions.Logging;

namespace Typhon.Workbench.Sessions;

/// <summary>Source-generated log messages for <see cref="AttachSessionRuntime"/>.</summary>
public sealed partial class AttachSessionRuntime
{
    [LoggerMessage(Level = LogLevel.Information, Message = "AttachSessionRuntime starting — will connect to {Host}:{Port}")]
    private partial void LogStarting(string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: connected to engine at {Host}:{Port}")]
    private partial void LogConnected(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attach: connection to engine lost, reconnecting...")]
    private partial void LogConnectionLost();

    [LoggerMessage(Level = LogLevel.Error, Message = "Attach: unexpected error in read loop")]
    private partial void LogUnexpectedError(System.Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: engine sent SHUTDOWN frame")]
    private partial void LogShutdownReceived();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attach: unknown frame type 0x{Type:X2}")]
    private partial void LogUnknownFrame(byte type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attach: malformed {FrameType} frame ({Reason}) — dropping")]
    private partial void LogMalformedFrame(string frameType, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Attach: INIT received — {SystemCount} systems, {WorkerCount} workers, {Rate:F0} Hz")]
    private partial void LogInitReceived(int systemCount, int workerCount, float rate);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Attach: LZ4 decompression mismatch — expected {Expected}, got {Got}")]
    private partial void LogDecompressionMismatch(int expected, int got);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: SSE subscriber {Id} connected (total: {Count})")]
    private partial void LogSubscriberConnected(System.Guid id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: SSE subscriber {Id} disconnected (total: {Count})")]
    private partial void LogSubscriberDisconnected(System.Guid id, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Attach: read-loop background task faulted")]
    private partial void LogReadLoopFaulted(System.Exception exception);
}
