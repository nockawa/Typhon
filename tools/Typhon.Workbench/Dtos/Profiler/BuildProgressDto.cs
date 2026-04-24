namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Wire shape emitted on the build-progress SSE endpoint. Phase describes the current build state; bytes / counts are
/// non-null only during <c>building</c>, and <see cref="Message"/> is non-null only during <c>error</c>.
/// </summary>
/// <param name="Phase"><c>"building"</c>, <c>"done"</c>, or <c>"error"</c>.</param>
/// <param name="BytesRead">Source bytes consumed so far — null unless <c>Phase == "building"</c>.</param>
/// <param name="TotalBytes">Source file size — null unless <c>Phase == "building"</c>.</param>
/// <param name="TickCount">Ticks encountered so far — null unless <c>Phase == "building"</c>.</param>
/// <param name="EventCount">Events encountered so far — null unless <c>Phase == "building"</c>.</param>
/// <param name="Message">Error message text — non-null only when <c>Phase == "error"</c>.</param>
public record BuildProgressDto(
    string Phase,
    long? BytesRead = null,
    long? TotalBytes = null,
    int? TickCount = null,
    long? EventCount = null,
    string Message = null);
