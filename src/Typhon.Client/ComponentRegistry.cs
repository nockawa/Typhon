using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Client;

/// <summary>
/// Maps component IDs to their expected unmanaged type size for typed deserialization via <see cref="CachedEntity.Get{T}"/>.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<ushort, int> _sizes = new();

    /// <summary>
    /// Register a component type by its server-assigned component ID.
    /// The size of <typeparamref name="T"/> is recorded for validation during <see cref="CachedEntity.Get{T}"/>.
    /// </summary>
    public void Register<T>(ushort componentId) where T : unmanaged => _sizes[componentId] = Unsafe.SizeOf<T>();

    /// <summary>
    /// Returns the expected byte size for a registered component, or -1 if not registered.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetExpectedSize(ushort componentId) => _sizes.GetValueOrDefault(componentId, -1);
}
