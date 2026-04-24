using Microsoft.Extensions.Logging;

namespace Typhon.Workbench.Sessions;

/// <summary>Source-generated log messages for <see cref="TraceSessionRuntime"/>.</summary>
public sealed partial class TraceSessionRuntime
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Trace cache build failed for {Path}")]
    private partial void LogBuildFailed(System.Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Trace cache build background task faulted for {Path}")]
    private partial void LogBuildTaskFaulted(System.Exception exception, string path);
}
