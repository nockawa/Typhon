using System;

namespace Typhon.Profiler;

/// <summary>
/// Builds a <c>.typhon-trace-cache</c> sidecar by scanning a source <c>.typhon-trace</c> file in one linear pass.
/// Implementation has been refactored to <see cref="IncrementalCacheBuilder"/>; this type now exists as a thin façade so existing
/// callers (workbench replay path, Typhon.Engine tests, future tooling) continue to work without changes.
/// </summary>
public static class TraceFileCacheBuilder
{
    /// <summary>
    /// Scan <paramref name="sourcePath"/> and write a fresh sidecar cache to <paramref name="cachePath"/>. Overwrites any existing cache at that
    /// path. Returns the high-level build result for logging / diagnostics.
    /// </summary>
    public static BuildResult Build(string sourcePath, string cachePath, IProgress<BuildProgress> progress = null)
        => IncrementalCacheBuilder.Build(sourcePath, cachePath, progress);

    /// <summary>High-level summary of a cache-build pass. Useful for logging and telemetry.</summary>
    public record BuildResult(int TickCount, long EventCount, long FoldedCount, int SystemCount, TimeSpan Duration, string CacheFilePath);

    /// <summary>Single progress snapshot emitted during a cache build. Emitted at tick boundaries, throttled to at most one per ~200 ms.</summary>
    public readonly record struct BuildProgress(long BytesRead, long TotalBytes, int TickCount, long EventCount);

    /// <summary>Standard sidecar-path convention: source <c>foo.typhon-trace</c> → cache <c>foo.typhon-trace-cache</c>.</summary>
    public static string GetCachePathFor(string sourcePath)
    {
        return sourcePath + TraceFileCacheConstants.CacheFileExtension;
    }
}
