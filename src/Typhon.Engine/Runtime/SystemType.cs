using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Defines the execution model of a system in the meta-DAG.
/// </summary>
[PublicAPI]
public enum SystemType
{
    /// <summary>
    /// Bulk data-parallel system. Work is divided into chunks distributed across workers via atomic counter (D4). Multiple workers process chunks concurrently.
    /// </summary>
    Patate,

    /// <summary>
    /// Lightweight single-invocation system. Executes inline on the dispatching worker (D3).
    /// Used for input processing, cleanup, timers, and small cross-entity logic.
    /// </summary>
    Callback
}
