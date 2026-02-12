using JetBrains.Annotations;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// RAII scope guard for epoch-based resource protection.
/// Enter via <see cref="Enter"/>, exit via <see cref="Dispose"/>.
/// Supports nesting — only the outermost scope advances the global epoch.
/// </summary>
/// <remarks>
/// <para>Copy safety: depth validation in <see cref="Dispose"/> detects misuse
/// (e.g., accidental struct copy disposing twice). A copied guard would have a stale
/// <c>_expectedDepth</c> that won't match the registry's current depth.</para>
/// <para>This is a ref struct to prevent heap allocation and boxing.
/// Always use in a <c>using</c> statement or explicit try/finally.</para>
/// </remarks>
[PublicAPI]
public ref struct EpochGuard
{
    private readonly EpochManager _manager;
    private readonly int _expectedDepth;
    private bool _disposed;

    private EpochGuard(EpochManager manager, int depth)
    {
        _manager = manager;
        _expectedDepth = depth;
        _disposed = false;
    }

    /// <summary>
    /// Enter an epoch scope. Returns a guard that must be disposed to exit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EpochGuard Enter(EpochManager manager)
    {
        var depth = manager.EnterScope();
        return new EpochGuard(manager, depth);
    }

    /// <summary>
    /// Exit the epoch scope. If this is the outermost scope, advances the global epoch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _manager.ExitScope(_expectedDepth);
        }
    }
}
