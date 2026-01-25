namespace Typhon.Engine;

/// <summary>
/// Telemetry level for resource contention tracking.
/// </summary>
public enum TelemetryLevel
{
    /// <summary>No telemetry recording. Zero overhead.</summary>
    None = 0,

    /// <summary>
    /// Aggregate counters only: contention counts, wait times.
    /// Minimal overhead (~5-10ns per operation).
    /// </summary>
    Light = 1,

    /// <summary>
    /// Full operation history with timestamps and thread IDs.
    /// For targeted troubleshooting of specific resources.
    /// Higher overhead (~50-100ns per operation).
    /// </summary>
    Deep = 2
}
