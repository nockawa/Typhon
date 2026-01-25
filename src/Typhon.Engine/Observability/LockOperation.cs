namespace Typhon.Engine;

/// <summary>
/// Types of lock operations for deep mode telemetry.
/// </summary>
public enum LockOperation : byte
{
    /// <summary>No operation (empty entry marker).</summary>
    None = 0,

    /// <summary>Shared access was acquired.</summary>
    SharedAcquired,

    /// <summary>Shared access was released.</summary>
    SharedReleased,

    /// <summary>Thread started waiting for shared access.</summary>
    SharedWaitStart,

    /// <summary>Exclusive access was acquired.</summary>
    ExclusiveAcquired,

    /// <summary>Exclusive access was released.</summary>
    ExclusiveReleased,

    /// <summary>Thread started waiting for exclusive access.</summary>
    ExclusiveWaitStart,

    /// <summary>Thread started promotion from shared to exclusive.</summary>
    PromoteToExclusiveStart,

    /// <summary>Promotion from shared to exclusive completed.</summary>
    PromoteToExclusiveAcquired,

    /// <summary>Demoted from exclusive to shared.</summary>
    DemoteToShared,

    /// <summary>Lock acquisition timed out.</summary>
    TimedOut,

    /// <summary>Lock acquisition was canceled.</summary>
    Canceled
}
