using System;
using System.Threading.Tasks;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Common interface for serving profiler chunk bytes regardless of whether the underlying source is a sealed
/// <c>.typhon-trace-cache</c> sidecar (replay) or an in-memory manifest backed by a temp file (live). Implemented by
/// <see cref="TraceSessionRuntime"/> and <see cref="AttachSessionRuntime"/> so <c>ProfilerController.GetChunk</c> can
/// branch-free.
/// </summary>
public interface IChunkProvider
{
    /// <summary>True when the runtime is ready to serve chunks (replay: build complete; live: at least one Init received).</summary>
    bool IsReady { get; }

    /// <summary>Source timestamp frequency (ticks/s) for µs conversion in chunk-response headers.</summary>
    long TimestampFrequency { get; }

    /// <summary>Returns the manifest entry for chunk <paramref name="chunkIdx"/>, awaiting readiness if necessary.</summary>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="chunkIdx"/> is outside the manifest range.</exception>
    /// <exception cref="InvalidOperationException">If the runtime never reached the ready state (build failed / never connected).</exception>
    ValueTask<ChunkManifestEntry> GetChunkManifestEntryAsync(int chunkIdx);

    /// <summary>
    /// Reads the raw LZ4-compressed bytes of chunk <paramref name="chunkIdx"/>. Returned array is rented from
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/> — caller MUST return it after writing the body.
    /// </summary>
    ValueTask<(byte[] Bytes, int Length)> ReadChunkCompressedAsync(int chunkIdx);
}
