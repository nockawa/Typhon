using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Context passed to system delegates during tick execution.
/// Provides tick-level information needed by Callback, Simple, and Patate systems.
/// </summary>
/// <remarks>
/// This is a value type (struct) passed by value to system delegates via <c>Action&lt;TickContext&gt;</c>.
/// Future #196 will add a <c>Transaction</c> field for UoW-per-tick integration.
/// </remarks>
[PublicAPI]
public struct TickContext
{
    /// <summary>Monotonically increasing tick number (0-based).</summary>
    public long TickNumber { get; init; }
}
