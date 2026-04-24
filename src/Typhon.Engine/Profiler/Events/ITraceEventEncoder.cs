using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Common contract implemented by every typed-event <c>ref struct</c>: the two methods needed to serialize a completed event into a ring-buffer
/// slot. Used only via C# 13 generic constraints with <c>allows ref struct</c> to let <c>TyphonEvent.PublishEvent&lt;T&gt;</c> share its
/// Dispose-time publish logic across all event types without boxing or per-event code duplication.
/// </summary>
public interface ITraceEventEncoder
{
    /// <summary>Total bytes this event will write when serialized.</summary>
    int ComputeSize();

    /// <summary>Serialize this event into <paramref name="destination"/>. <paramref name="endTimestamp"/> is the span-close time.</summary>
    void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten);
}
