using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        //
        // Capture `slot.Reader` into a local before the check-and-use pattern. Without this, a concurrent <see cref="Invalidate"/>
        // call could null out `slot.Reader` between the null-check and the return, handing the caller either null or a
        // just-disposed reader. Capturing once makes the fast path atomic from the perspective of a single caller.
        var fastReader = slot.Reader;
        if (fastReader != null && IsFreshFast(slot, sourcePath))
        {
            return fastReader;
        }

        lock (slot.Lock)
        {
            // Re-check under the lock — same capture pattern, now protected against Invalidate for the duration of this call.
            var slowReader = slot.Reader;
            if (slowReader != null && IsFreshFast(slot, sourcePath))
            {
                return slowReader;
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
                slot.CachedGcSuspensions = null;
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

    /// <summary>
    /// Return the full list of GC-suspension records for a trace, computed once per session and cached. Used by /api/trace/open to hand
    /// the client a complete, stable suspension list at open time — so the client's per-tick pause-time bar chart uses a yMax that
    /// doesn't fluctuate as individual chunks are loaded and LRU-evicted. Each record's start/duration is converted to microseconds
    /// relative to <paramref name="baselineQpc"/> using <paramref name="timestampFrequency"/> (ticks-per-second).
    /// </summary>
    public IReadOnlyList<GcSuspensionDto> GetOrComputeGcSuspensions(string sourcePath, long baselineQpc, long timestampFrequency)
    {
        if (!_sessions.TryGetValue(sourcePath, out var slot)) return Array.Empty<GcSuspensionDto>();
        // Fast path — double-checked: unlock read, lock only on cache miss. Same pattern as GetOrBuild.
        var fast = slot.CachedGcSuspensions;
        if (fast != null) return fast;
        lock (slot.Lock)
        {
            var slow = slot.CachedGcSuspensions;
            if (slow != null) return slow;
            var reader = slot.Reader;
            if (reader == null || timestampFrequency <= 0) return Array.Empty<GcSuspensionDto>();

            var result = new List<GcSuspensionDto>();
            // ArrayPool rental for decompression scratch — chunks are bounded at TraceFileCacheConstants.ByteCap = 1 MiB compressed,
            // and the uncompressed is a small multiple. Take the largest cache entry's uncompressed bytes as the scratch size.
            var maxCompressed = 0;
            var maxUncompressed = 0;
            foreach (var entry in reader.ChunkManifest)
            {
                if ((int)entry.CacheByteLength > maxCompressed) maxCompressed = (int)entry.CacheByteLength;
                if ((int)entry.UncompressedBytes > maxUncompressed) maxUncompressed = (int)entry.UncompressedBytes;
            }
            if (maxUncompressed == 0) return Array.Empty<GcSuspensionDto>();

            var compressedScratch = System.Buffers.ArrayPool<byte>.Shared.Rent(maxCompressed);
            var uncompressedScratch = System.Buffers.ArrayPool<byte>.Shared.Rent(maxUncompressed);
            try
            {
                foreach (var entry in reader.ChunkManifest)
                {
                    var compSpan = compressedScratch.AsSpan(0, (int)entry.CacheByteLength);
                    var uncompSpan = uncompressedScratch.AsSpan(0, (int)entry.UncompressedBytes);
                    reader.DecompressChunk(entry, uncompSpan, compSpan);
                    WalkRecordsForSuspensions(uncompSpan, baselineQpc, timestampFrequency, result);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(compressedScratch);
                System.Buffers.ArrayPool<byte>.Shared.Return(uncompressedScratch);
            }

            // Sort by startUs — client's per-tick bucketing walk assumes sorted input. In practice suspensions are already roughly
            // sorted because chunks are emitted in tick order, but an explicit sort is cheap and defensive.
            result.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));
            slot.CachedGcSuspensions = result;
            return result;
        }
    }

    /// <summary>
    /// Walk a decompressed chunk's record stream, decoding only <see cref="Typhon.Engine.Profiler.TraceEventKind.GcSuspension"/> records
    /// and appending each to <paramref name="sink"/>. All other record kinds are skipped via the u16 size prefix — no per-record full
    /// decode needed.
    /// </summary>
    private static void WalkRecordsForSuspensions(
        ReadOnlySpan<byte> records,
        long baselineQpc,
        long timestampFrequency,
        List<GcSuspensionDto> sink)
    {
        var pos = 0;
        while (pos + 3 <= records.Length)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size == 0 || size == 0xFFFF) break;  // empty slot / wrap sentinel — shouldn't happen in cache but defensive
            if (pos + size > records.Length) break;
            var kind = (Typhon.Engine.Profiler.TraceEventKind)records[pos + 2];
            if (kind == Typhon.Engine.Profiler.TraceEventKind.GcSuspension)
            {
                var data = Typhon.Engine.Profiler.GcSuspensionEventCodec.Decode(records.Slice(pos, size));
                // Convert Stopwatch ticks → µs. Multiply before divide to preserve precision against QPC frequencies that don't evenly
                // divide 1e6 (most desktop CPUs report 10_000_000 which divides cleanly, but guard anyway).
                var startUs = (data.StartTimestamp - baselineQpc) * 1_000_000L / timestampFrequency;
                var durationUs = data.DurationTicks * 1_000_000L / timestampFrequency;
                sink.Add(new GcSuspensionDto(startUs, durationUs, data.ThreadSlot));
            }
            pos += size;
        }
    }

    /// <summary>Serializable DTO for one GC-suspension event. Used only for /api/trace/open's gcSuspensions payload.</summary>
    public readonly record struct GcSuspensionDto(long StartUs, long DurationUs, byte ThreadSlot);

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
        /// <summary>
        /// Full suspension list for this trace, computed once per session and handed to clients via /api/trace/open. Null until first
        /// /open call that triggers the computation. Nulled out alongside Reader when <see cref="Invalidate"/> fires.
        /// </summary>
        public IReadOnlyList<GcSuspensionDto> CachedGcSuspensions;
    }
}
