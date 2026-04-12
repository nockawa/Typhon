using Microsoft.Extensions.Logging;

namespace Typhon.Engine;

/// <summary>
/// Source-generated log messages for <see cref="TcpStreamInspector"/>. The <c>[LoggerMessage]</c> generator emits code that checks <c>IsEnabled</c>
/// before formatting, avoiding any allocation or boxing when the level is filtered out.
/// </summary>
public sealed partial class TcpStreamInspector
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "TcpStreamInspector listening on port {Port} ({WorkerCount} workers, {BaseTickRate:F0} Hz)")]
    private partial void LogListenerStarted(int port, int workerCount, float baseTickRate);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TcpStreamInspector: profiler client connected from {RemoteEndPoint}")]
    private partial void LogClientConnected(string remoteEndPoint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TcpStreamInspector: profiler client disconnected ({DroppedFrames} frames dropped during session)")]
    private partial void LogClientDisconnected(long droppedFrames);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "TcpStreamInspector: INIT frame delivery failed, closing connection ({Reason})")]
    private partial void LogInitFailed(string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "TcpStreamInspector: rejected duplicate connection from {RemoteEndPoint} (single-client only)")]
    private partial void LogDuplicateRejected(string remoteEndPoint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TcpStreamInspector shutting down ({DroppedFrames} frames dropped total)")]
    private partial void LogShuttingDown(long droppedFrames);
}
