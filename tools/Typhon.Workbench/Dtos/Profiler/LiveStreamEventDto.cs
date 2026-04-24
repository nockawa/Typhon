using Typhon.Profiler;

namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Wire shape for the profiler live SSE stream. Discriminated by <see cref="Kind"/>:
/// <list type="bullet">
///   <item><c>"metadata"</c> — <see cref="Metadata"/> non-null. Emitted on connect (if cached) and on every Init frame.</item>
///   <item><c>"tick"</c> — <see cref="Tick"/> non-null. One per tick batch decoded from a Block frame.</item>
///   <item><c>"heartbeat"</c> — <see cref="Status"/> non-null. Emitted on connection-state change and on idle timeout.</item>
/// </list>
/// Every frame ships as a default SSE <c>message</c> event (no <c>event:</c> prefix) because the client's
/// <c>useEventSource</c> hook only listens to <c>onmessage</c>. Clients switch on <see cref="Kind"/>.
/// </summary>
public record LiveStreamEventDto(
    string Kind,
    ProfilerMetadataDto Metadata = null,
    LiveTickBatch Tick = null,
    string Status = null);
