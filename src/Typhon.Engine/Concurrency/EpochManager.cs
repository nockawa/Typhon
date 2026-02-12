using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Manages epoch-based resource protection. One instance per <see cref="DatabaseEngine"/>.
/// Threads enter/exit epoch scopes via <see cref="EpochGuard"/>; pages tagged with an epoch
/// cannot be evicted until all scopes referencing that epoch have exited.
/// </summary>
[PublicAPI]
public sealed class EpochManager : IResource, IMetricSource
{
    private long _globalEpoch;
    private readonly EpochThreadRegistry _registry;

    // === IResource implementation ===
    private readonly string _id;
    private readonly IResource _parent;
    private readonly List<IResource> _children = [];
    private readonly DateTime _createdAt = DateTime.UtcNow;
    // Will be set in Phase 3 when DatabaseEngine integrates EpochManager
#pragma warning disable CS0649
    private IResourceRegistry _owner;
#pragma warning restore CS0649

    // === Metrics ===
    private long _epochAdvances;
    private long _scopeEnters;
    private long _registryExhaustionCount;

    public EpochManager(string id, IResource parent)
    {
        _id = id;
        _parent = parent;
        _globalEpoch = 1; // Start at 1 so 0 means "no epoch" / "not pinned"
        _registry = new EpochThreadRegistry();
    }

    /// <summary>Current global epoch value. Monotonically increasing.</summary>
    public long GlobalEpoch => _globalEpoch;

    /// <summary>
    /// The minimum epoch pinned by any active thread. Pages tagged with an epoch
    /// &gt;= this value cannot be evicted. Returns <see cref="GlobalEpoch"/> if no threads are active.
    /// </summary>
    public long MinActiveEpoch => _registry.ComputeMinActiveEpoch(_globalEpoch);

    /// <summary>Total number of epoch advances since creation.</summary>
    public long EpochAdvances => _epochAdvances;

    /// <summary>Total number of scope entries since creation.</summary>
    public long ScopeEnters => _scopeEnters;

    /// <summary>Number of active (pinned) slots in the thread registry.</summary>
    public int ActiveSlotCount => _registry.ActiveSlotCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Scope Management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter an epoch scope on the current thread. Pins the current global epoch,
    /// preventing eviction of pages tagged with this or later epochs.
    /// </summary>
    /// <returns>The depth before entering (0 for outermost scope).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int EnterScope()
    {
        _scopeEnters++;
        return _registry.PinCurrentThread(_globalEpoch);
    }

    /// <summary>
    /// Exit an epoch scope on the current thread. If this is the outermost scope,
    /// unpins the thread and advances the global epoch.
    /// </summary>
    /// <param name="expectedDepth">The depth returned by the matching <see cref="EnterScope"/> call.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitScope(int expectedDepth)
    {
        if (_registry.UnpinCurrentThread(expectedDepth))
        {
            // Outermost scope exited — advance the global epoch
            Interlocked.Increment(ref _globalEpoch);
            _epochAdvances++;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IResource
    // ═══════════════════════════════════════════════════════════════════════

    public string Id => _id;
    public ResourceType Type => ResourceType.Synchronization;
    public IResource Parent => _parent;
    public IEnumerable<IResource> Children => _children;
    public DateTime CreatedAt => _createdAt;
    public IResourceRegistry Owner => _owner;

    public bool RegisterChild(IResource child)
    {
        _children.Add(child);
        return true;
    }

    public bool RemoveChild(IResource resource) => _children.Remove(resource);

    // ═══════════════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════════════

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("EpochAdvances", _epochAdvances);
        writer.WriteThroughput("ScopeEnters", _scopeEnters);
        writer.WriteCapacity(_registry.ActiveSlotCount, EpochThreadRegistry.MaxSlots);
        writer.WriteThroughput("RegistryExhaustions", _registryExhaustionCount);
    }

    public void ResetPeaks()
    {
        // No high-water marks currently tracked
    }

    /// <summary>Increment exhaustion counter. Called by <see cref="EpochThreadRegistry"/> on slot exhaustion.</summary>
    internal void RecordRegistryExhaustion() => _registryExhaustionCount++;

    public void Dispose()
    {
        _registry.Dispose();
    }
}
