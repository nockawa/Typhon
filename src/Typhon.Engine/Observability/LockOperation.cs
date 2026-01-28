namespace Typhon.Engine;

/// <summary>
/// Types of lock operations for deep mode telemetry.
/// </summary>
public enum LockOperation : byte
{
    /// <summary>No operation (empty entry marker).</summary>
    None = 0,

    // ═══════════════════════════════════════════════════════════════════════
    // AccessControl / AccessControlSmall operations (Shared/Exclusive)
    // ═══════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════
    // ResourceAccessControl operations (Accessing/Modify/Destroy)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>ACCESSING mode was acquired (ResourceAccessControl).</summary>
    AccessingAcquired,

    /// <summary>ACCESSING mode was released (ResourceAccessControl).</summary>
    AccessingReleased,

    /// <summary>Thread started waiting for ACCESSING mode (ResourceAccessControl).</summary>
    AccessingWaitStart,

    /// <summary>MODIFY mode was acquired (ResourceAccessControl).</summary>
    ModifyAcquired,

    /// <summary>MODIFY mode was released (ResourceAccessControl).</summary>
    ModifyReleased,

    /// <summary>Thread started waiting for MODIFY mode (ResourceAccessControl).</summary>
    ModifyWaitStart,

    /// <summary>Thread started promotion from ACCESSING to MODIFY (ResourceAccessControl).</summary>
    PromoteToModifyStart,

    /// <summary>Promotion from ACCESSING to MODIFY completed (ResourceAccessControl).</summary>
    PromoteToModifyAcquired,

    /// <summary>Demoted from MODIFY to ACCESSING (ResourceAccessControl).</summary>
    DemoteToAccessing,

    /// <summary>DESTROY mode was acquired - terminal state (ResourceAccessControl).</summary>
    DestroyAcquired,

    /// <summary>Thread started waiting for DESTROY mode (ResourceAccessControl).</summary>
    DestroyWaitStart,

    // ═══════════════════════════════════════════════════════════════════════
    // Common termination operations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Lock acquisition timed out.</summary>
    TimedOut,

    /// <summary>Lock acquisition was canceled.</summary>
    Canceled
}
