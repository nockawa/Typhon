using System.IO;
using Microsoft.Extensions.Logging;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-attach-session temp file that stores LZ4-compressed chunks for a live profiler session. The file is written
/// append-only by <see cref="AppendOnlyChunkSink"/> as the engine streams blocks; the owning <see cref="AttachSessionRuntime"/>
/// reads chunk bytes back via positional reads when serving <c>/api/sessions/{id}/profiler/chunks/{idx}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle is bound to the AttachSessionRuntime instance — created during <see cref="AttachSessionRuntime.StartAsync"/>
/// when the first Init frame arrives, disposed when the session ends. The file is deleted in <see cref="Dispose"/>.
/// </para>
/// <para>
/// Path: <c>%TEMP%/typhon-workbench/{sessionId}.cache</c> on Windows, equivalent on POSIX. Files are gitignored by the
/// surrounding directory; <see cref="LiveCacheTempFile.SweepOrphans"/> removes stragglers from prior crashes on workbench
/// startup.
/// </para>
/// </remarks>
public sealed class LiveCacheTempFile : IDisposable
{
    private static readonly object SweepLock = new();
    private static bool s_swept;

    private readonly FileStream _stream;
    private readonly AppendOnlyChunkSink _sink;
    private bool _disposed;

    public string Path { get; }
    public AppendOnlyChunkSink Sink => _sink;

    /// <summary>The temp directory all live caches share. Created if it doesn't exist.</summary>
    public static string TempDirectory => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typhon-workbench");

    private LiveCacheTempFile(string path, FileStream stream, AppendOnlyChunkSink sink)
    {
        Path = path;
        _stream = stream;
        _sink = sink;
    }

    public static LiveCacheTempFile Create(Guid sessionId)
    {
        var dir = TempDirectory;
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"{sessionId:N}.cache");

        // FileShare.Read so Read paths (chunk endpoint) can open the file concurrently for positional reads.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var sink = new AppendOnlyChunkSink(stream, ownsStream: true);
        return new LiveCacheTempFile(path, stream, sink);
    }

    /// <summary>
    /// Open a positional reader on the temp file. Caller is responsible for disposing it. Does NOT take exclusive ownership —
    /// multiple readers can be open simultaneously (each chunk request opens its own to avoid contention with the writer).
    /// </summary>
    public FileStream OpenReader()
    {
        return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    /// <summary>
    /// Sweep orphan temp files from prior workbench crashes. Idempotent and safe to call concurrently — first caller wins,
    /// subsequent calls are no-ops. Failures are swallowed (the sweep is best-effort).
    /// </summary>
    public static void SweepOrphans(ILogger logger)
    {
        lock (SweepLock)
        {
            if (s_swept)
            {
                return;
            }
            s_swept = true;
        }

        try
        {
            var dir = TempDirectory;
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (var path in Directory.EnumerateFiles(dir, "*.cache"))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Probably held by another running workbench instance — skip.
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to sweep orphan profiler temp files.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _sink.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch
        {
            // Temp file failed to delete — OS will clean it up eventually, or the next sweep will.
        }
    }
}
