using System;
using System.Collections.Concurrent;
using System.IO;
using Typhon.Profiler;

namespace Typhon.Profiler.Server;

/// <summary>
/// Manages per-trace-file cache lifecycles. Opens and caches <see cref="TraceFileCacheReader"/> instances keyed by source path, builds the sidecar
/// on miss, verifies the fingerprint on each open and rebuilds if stale.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety: <see cref="GetOrBuild"/> is safe to call from multiple request threads — a per-path <see cref="object"/> lock serializes
/// concurrent opens of the same path. Different paths don't contend. Readers, once cached, are not locked on access — callers must not dispose
/// them, and the service keeps them alive for the process lifetime (or until invalidated).
/// </para>
/// <para>
/// Phase 1 scope: synchronous build on miss (request thread blocks until the cache is ready). Progressive-build (HTTP 202 while in progress,
/// SSE-style progress feed) is Phase 2 — deliberately not implemented here to keep the surface simple.
/// </para>
/// </remarks>
public sealed class TraceSessionService
{
    private readonly ConcurrentDictionary<string, SessionSlot> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Return a cache reader for the given source path. Builds the sidecar on the first call for a path (or when the existing sidecar's
    /// fingerprint doesn't match the source). Subsequent calls return the cached reader instantly.
    /// </summary>
    public TraceFileCacheReader GetOrBuild(string sourcePath) => GetOrBuildWithProgress(sourcePath, progress: null);

    /// <summary>
    /// Same as <see cref="GetOrBuild(string)"/>, but routes builder progress events to <paramref name="progress"/> while the build is running.
    /// Callers that want a progress feed (e.g., the SSE endpoint) pass a sink; callers that just want the reader pass null. Concurrent calls to
    /// the same path serialize on the session slot lock — the second caller blocks until the first finishes, so only ONE build runs per path
    /// at a time. That also means: only the first caller's <paramref name="progress"/> sees per-tick updates; later callers get the final
    /// reader once it's ready (no replay). For the current single-dev-tool use case that's acceptable; a broadcaster could be added later.
    /// </summary>
    public TraceFileCacheReader GetOrBuildWithProgress(string sourcePath, IProgress<TraceFileCacheBuilder.BuildProgress> progress)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentException("Path is required.", nameof(sourcePath));
        }
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source trace file not found.", sourcePath);
        }

        var slot = _sessions.GetOrAdd(sourcePath, _ => new SessionSlot());

        // Double-checked open — fast path reads the already-built reader; slow path locks + builds.
        if (slot.Reader != null && IsFreshFast(slot, sourcePath))
        {
            return slot.Reader;
        }

        lock (slot.Lock)
        {
            if (slot.Reader != null && IsFreshFast(slot, sourcePath))
            {
                return slot.Reader;
            }

            // Dispose any stale reader before rebuilding.
            slot.Reader?.Dispose();
            slot.Reader = null;

            var cachePath = TraceFileCacheBuilder.GetCachePathFor(sourcePath);

            // Open existing cache if present and fresh. If stale or missing, build.
            if (File.Exists(cachePath))
            {
                try
                {
                    var reader = new TraceFileCacheReader(File.OpenRead(cachePath));
                    if (VerifyFresh(reader, sourcePath))
                    {
                        slot.Reader = reader;
                        StampSlotFromSource(slot, sourcePath, reader);
                        return reader;
                    }
                    reader.Dispose();
                }
                catch (InvalidDataException)
                {
                    // Stale format / corrupt — rebuild from scratch.
                }
            }

            var result = TraceFileCacheBuilder.Build(sourcePath, cachePath, progress);
            Console.WriteLine(
                $"[cache-build] {Path.GetFileName(sourcePath)}: {result.TickCount} ticks, {result.EventCount:N0} events " +
                $"({result.FoldedCount:N0} folded async completions dropped), {result.SystemCount} systems, " +
                $"{result.Duration.TotalMilliseconds:F1} ms");
            slot.Reader = new TraceFileCacheReader(File.OpenRead(cachePath));
            StampSlotFromSource(slot, sourcePath, slot.Reader);
            return slot.Reader;
        }
    }

    /// <summary>
    /// Look up the cached timestamp frequency for a source path if the cache reader is already loaded. Returns 0 if not loaded. Callers that
    /// need the frequency during request handling can use this to avoid re-opening the source file per request (see
    /// <see cref="GetSourceTimestampFrequencyCached"/> in Program.cs for the integration point).
    /// </summary>
    public long GetCachedTimestampFrequency(string sourcePath)
    {
        if (_sessions.TryGetValue(sourcePath, out var slot))
        {
            // CachedTimestampFrequency is written under the slot lock but read unlocked. On x64, aligned 64-bit reads are naturally atomic,
            // so the read either sees 0 (not stamped yet) or the real value — no torn reads.
            return slot.CachedTimestampFrequency;
        }
        return 0;
    }

    /// <summary>
    /// Forget the cached reader for a given source path. Use when the client signals end-of-session (not critical — readers are cheap to keep
    /// around — but useful for tests and for explicit invalidation after external file modification).
    /// </summary>
    public void Invalidate(string sourcePath)
    {
        if (_sessions.TryRemove(sourcePath, out var slot))
        {
            lock (slot.Lock)
            {
                slot.Reader?.Dispose();
                slot.Reader = null;
            }
        }
    }

    private static bool VerifyFresh(TraceFileCacheReader reader, string sourcePath)
    {
        Span<byte> fp = stackalloc byte[32];
        TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fp);
        return reader.VerifyFingerprint(fp);
    }

    /// <summary>
    /// Cheap freshness check using the source file's (mtime, length) tuple cached on the slot. Returns true if the tuple still matches what
    /// we stamped when the reader was loaded — in that case the SHA-256 fingerprint hasn't changed either (fingerprint includes both), so
    /// we can skip the ~1 ms hash recomputation on every GetOrBuild call. Returns false if the source file was touched or never stamped.
    /// </summary>
    private static bool IsFreshFast(SessionSlot slot, string sourcePath)
    {
        if (slot.CachedSourceLength == 0)
        {
            return false;
        }
        try
        {
            var fi = new FileInfo(sourcePath);
            if (!fi.Exists) return false;
            return fi.Length == slot.CachedSourceLength && fi.LastWriteTimeUtc.Ticks == slot.CachedSourceMTimeTicks;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Record source file's (mtime, length) tuple + cached timestamp frequency for the fast-freshness path. Called under the slot lock.</summary>
    private static void StampSlotFromSource(SessionSlot slot, string sourcePath, TraceFileCacheReader reader)
    {
        try
        {
            var fi = new FileInfo(sourcePath);
            slot.CachedSourceLength = fi.Length;
            slot.CachedSourceMTimeTicks = fi.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            // If the stamp fails we just fall back to the slow fingerprint path next time — no harm done.
            slot.CachedSourceLength = 0;
        }
        // Cache the timestamp frequency by re-opening the source once. The per-request /api/trace/chunk-binary endpoint used to do this on
        // every request; caching on the slot turns that into a one-time cost per trace open.
        try
        {
            using var srcStream = File.OpenRead(sourcePath);
            using var srcReader = new TraceFileReader(srcStream);
            slot.CachedTimestampFrequency = srcReader.ReadHeader().TimestampFrequency;
        }
        catch
        {
            slot.CachedTimestampFrequency = 0;
        }
    }

    private sealed class SessionSlot
    {
        public readonly object Lock = new();
        public TraceFileCacheReader Reader;
        /// <summary>Source file length at last stamp; 0 = unstamped.</summary>
        public long CachedSourceLength;
        /// <summary>Source file LastWriteTimeUtc.Ticks at last stamp.</summary>
        public long CachedSourceMTimeTicks;
        /// <summary>Source file's TimestampFrequency — cached so /api/trace/chunk-binary doesn't re-open the source per request.</summary>
        public long CachedTimestampFrequency;
    }
}
