using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// Represents a local subscription to a named View. Maintains an entity cache that is automatically updated by the receive loop as the server pushes deltas.
/// </summary>
/// <remarks>
/// <para>In v1, subscriptions are server-driven: the game server calls <c>SetSubscriptions</c> on the server-side <c>ClientConnection</c>.
/// This class registers local interest and callbacks so that when the server pushes a View with the matching name, deltas are applied and callbacks fire.</para>
/// <para>All callbacks fire on the receive thread. Do not block in callbacks.</para>
/// </remarks>
public sealed class ViewSubscription
{
    private readonly Dictionary<long, CachedEntity> _entities = new();

    /// <summary>View name as published by the server.</summary>
    public string ViewName { get; }

    /// <summary>
    /// Server-assigned View ID. Set when the server sends a <see cref="EventType.Subscribed"/> event.
    /// Zero until the server activates this subscription.
    /// </summary>
    public ushort ViewId { get; internal set; }

    /// <summary>
    /// True once the server has sent <see cref="EventType.SyncComplete"/> for this View, meaning all initial entities have been received and normal delta flow has begun.
    /// </summary>
    public bool IsSynced { get; internal set; }

    /// <summary>Read-only view of the local entity cache, keyed by entity ID.</summary>
    public IReadOnlyDictionary<long, CachedEntity> Entities => _entities;

    /// <summary>Fires when an entity is added to this View (new entity or incremental sync batch).</summary>
    public event Action<CachedEntity> OnEntityAdded;

    /// <summary>Fires when an entity's components are modified. The array contains only the changed components.</summary>
    public event Action<CachedEntity, ComponentFieldUpdate[]> OnEntityModified;

    /// <summary>Fires when an entity is removed from this View.</summary>
    public event Action<long> OnEntityRemoved;

    /// <summary>Fires when incremental sync is complete and normal delta flow begins.</summary>
    public event Action OnSyncComplete;

    /// <summary>Fires when a resync occurs (backpressure overflow). Cache has been cleared and will be repopulated.</summary>
    public event Action OnResync;

    internal ViewSubscription(string viewName)
    {
        ViewName = viewName;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal mutation (called by receive loop only)
    // ═══════════════════════════════════════════════════════════════

    internal void AddEntity(CachedEntity entity)
    {
        _entities[entity.Id] = entity;
        OnEntityAdded?.Invoke(entity);
    }

    internal void ModifyEntity(long entityId, ComponentFieldUpdate[] updates)
    {
        if (!_entities.TryGetValue(entityId, out var entity))
        {
            return; // Unknown entity — skip silently (can happen during sync race)
        }

        ApplyModifications(entity, updates);
        OnEntityModified?.Invoke(entity, updates);
    }

    internal void RemoveEntity(long entityId)
    {
        _entities.Remove(entityId);
        OnEntityRemoved?.Invoke(entityId);
    }

    internal void Clear() => _entities.Clear();

    internal void FireSyncComplete()
    {
        IsSynced = true;
        OnSyncComplete?.Invoke();
    }

    internal void FireResync()
    {
        IsSynced = false;
        _entities.Clear();
        OnResync?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════
    // Modification application
    // ═══════════════════════════════════════════════════════════════

    private static void ApplyModifications(CachedEntity entity, ComponentFieldUpdate[] updates)
    {
        for (var i = 0; i < updates.Length; i++)
        {
            ref var update = ref updates[i];
            for (var j = 0; j < entity.Components.Length; j++)
            {
                if (entity.Components[j].ComponentId == update.ComponentId)
                {
                    // v1: FieldDirtyBits == ~0UL — full component replacement.
                    // Forward-compatible: when FieldDirtyBits != ~0UL, per-field patching would apply.
                    entity.Components[j] = new ComponentSnapshot
                    {
                        ComponentId = update.ComponentId,
                        Data = update.FieldValues
                    };
                    break;
                }
            }
        }
    }
}
