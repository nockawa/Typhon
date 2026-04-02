using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// A locally cached entity with its component data. Updated by the receive loop as deltas arrive.
/// </summary>
public sealed class CachedEntity
{
    /// <summary>Entity identifier (raw 64-bit value matching server-side EntityId).</summary>
    public long Id { get; }

    /// <summary>All component snapshots for this entity. Updated in-place on Modified deltas.</summary>
    public ComponentSnapshot[] Components { get; internal set; }

    internal CachedEntity(long id, ComponentSnapshot[] components)
    {
        Id = id;
        Components = components;
    }

    /// <summary>
    /// Read a component value by its type ID. Requires prior <see cref="TyphonConnection.RegisterComponent{T}"/>.
    /// </summary>
    /// <typeparam name="T">Unmanaged struct matching the server-side component layout.</typeparam>
    /// <param name="componentId">Server-assigned component type ID.</param>
    /// <returns>The component value, read directly from cached bytes via <see cref="MemoryMarshal.Read{T}"/>.</returns>
    /// <exception cref="KeyNotFoundException">Component ID not found on this entity.</exception>
    /// <exception cref="InvalidOperationException">Byte size mismatch between <typeparamref name="T"/> and cached data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Get<T>(ushort componentId) where T : unmanaged
    {
        for (var i = 0; i < Components.Length; i++)
        {
            if (Components[i].ComponentId == componentId)
            {
                var data = Components[i].Data;
                if (data.Length < Unsafe.SizeOf<T>())
                {
                    throw new InvalidOperationException(
                        $"Component {componentId}: cached data is {data.Length} bytes but {typeof(T).Name} requires {Unsafe.SizeOf<T>()} bytes");
                }

                return MemoryMarshal.Read<T>(data);
            }
        }

        throw new KeyNotFoundException($"Component {componentId} not found on entity {Id}");
    }

    /// <summary>
    /// Try to read a component value by its type ID. Returns false if the component is not present.
    /// </summary>
    public bool TryGet<T>(ushort componentId, out T value) where T : unmanaged
    {
        for (var i = 0; i < Components.Length; i++)
        {
            if (Components[i].ComponentId == componentId)
            {
                var data = Components[i].Data;
                if (data.Length >= Unsafe.SizeOf<T>())
                {
                    value = MemoryMarshal.Read<T>(data);
                    return true;
                }

                break;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Check whether this entity has a component with the given ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasComponent(ushort componentId)
    {
        for (var i = 0; i < Components.Length; i++)
        {
            if (Components[i].ComponentId == componentId)
            {
                return true;
            }
        }

        return false;
    }
}
