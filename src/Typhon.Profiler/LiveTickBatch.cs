namespace Typhon.Profiler;

/// <summary>
/// One tick's worth of decoded records, grouped and ready for SSE serialization. Produced by
/// <see cref="AttachSessionRuntime"/> from incoming <see cref="LiveFrameType.Block"/> frames; consumed by
/// subscribers (SSE handlers) via bounded channels.
/// </summary>
public sealed class LiveTickBatch
{
    public int TickNumber { get; init; }
    public LiveTraceEvent[] Events { get; init; }
}
