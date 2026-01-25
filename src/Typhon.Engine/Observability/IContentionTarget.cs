namespace Typhon.Engine;

/// <summary>
/// Interface for resources that want to receive contention telemetry from locks.
/// Implement this interface on resource types (like BTree, ComponentTable) to collect
/// lock contention metrics and operation history.
/// </summary>
public interface IContentionTarget
{
    /// <summary>
    /// Current telemetry level for this resource.
    /// Use volatile field for thread-safe reads without locking.
    /// </summary>
    TelemetryLevel TelemetryLevel { get; }

    /// <summary>
    /// Optional link to owning IResource for graph integration.
    /// Return null if this target is not part of the resource graph.
    /// </summary>
    IResource OwningResource { get; }

    /// <summary>
    /// Light mode: Record that contention occurred.
    /// Called when a thread had to wait before acquiring a lock.
    /// Implement with Interlocked operations for thread-safety.
    /// </summary>
    /// <param name="waitUs">Microseconds spent waiting.</param>
    void RecordContention(long waitUs);

    /// <summary>
    /// Deep mode: Log a detailed lock operation.
    /// Called for every Enter/Exit when TelemetryLevel >= Deep.
    /// </summary>
    /// <param name="operation">The type of lock operation.</param>
    /// <param name="durationUs">Duration of the operation or wait time in microseconds.</param>
    void LogLockOperation(LockOperation operation, long durationUs);
}
