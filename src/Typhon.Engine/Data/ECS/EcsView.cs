using System;
using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Reactive ECS View — a persistent, refreshable entity set built from an <see cref="EcsQuery{TArchetype}"/>.
/// Tracks Added/Removed entities between refreshes via set difference (pull model).
/// </summary>
/// <remarks>
/// <para>Usage pattern (game loop):
/// <code>
/// var view = tx.Query&lt;Building&gt;().Enabled&lt;Placement&gt;().ToView();
/// // Each tick:
/// view.Refresh(currentTx);
/// foreach (var id in view.Added) { /* react to new entities */ }
/// foreach (var id in view.Removed) { /* react to removed entities */ }
/// </code>
/// </para>
/// <para>Enable/Disable changes are captured implicitly: Refresh re-evaluates T2 constraints, so disabling a component on an entity causes it to appear
/// in Removed on next Refresh.</para>
/// <para>For SV/Transient components, call <c>dbe.WriteTickFence()</c> before <c>view.Refresh()</c> to ensure data reflects the latest tick boundary.</para>
/// </remarks>
[PublicAPI]
public class EcsView<TArchetype> : IDisposable, IEnumerable<EntityId> where TArchetype : class
{
    private EcsQuery<TArchetype> _query;
    private HashSet<EntityId> _currentSet;
    private readonly List<EntityId> _added = new();
    private readonly List<EntityId> _removed = new();
    private long _lastRefreshTSN;
    private bool _isDisposed;

    internal EcsView(EcsQuery<TArchetype> query, HashSet<EntityId> initialSet, long initialTSN)
    {
        _query = query;
        _currentSet = initialSet;
        _lastRefreshTSN = initialTSN;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Number of entities currently matching the query.</summary>
    public int Count => _currentSet.Count;

    /// <summary>TSN of the last Refresh call.</summary>
    public long LastRefreshTSN => _lastRefreshTSN;

    /// <summary>True if this View has been disposed.</summary>
    public bool IsDisposed => _isDisposed;

    /// <summary>Test if an entity is currently in the View.</summary>
    public bool Contains(EntityId id) => _currentSet.Contains(id);

    // ═══════════════════════════════════════════════════════════════════════
    // Delta access (computed on each Refresh)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Entities that entered the View since the last Refresh.</summary>
    public IReadOnlyList<EntityId> Added => _added;

    /// <summary>Entities that left the View since the last Refresh.</summary>
    public IReadOnlyList<EntityId> Removed => _removed;

    /// <summary>True if any entities were added or removed since the last Refresh.</summary>
    public bool HasChanges => _added.Count > 0 || _removed.Count > 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh (pull model — full re-query + diff)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Re-execute the query against the given transaction and compute the delta (Added/Removed) since the last refresh. The View's entity set is updated to
    /// reflect the current state.
    /// </summary>
    public void Refresh(Transaction tx)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(EcsView<TArchetype>));
        }

        // Rebind query to new transaction (different TSN → different visibility)
        _query.UpdateTransaction(tx);
        var newSet = _query.Execute();

        _added.Clear();
        _removed.Clear();

        // Added: in newSet but not in currentSet
        foreach (var id in newSet)
        {
            if (!_currentSet.Contains(id))
            {
                _added.Add(id);
            }
        }

        // Removed: in currentSet but not in newSet
        foreach (var id in _currentSet)
        {
            if (!newSet.Contains(id))
            {
                _removed.Add(id);
            }
        }

        _currentSet = newSet;
        _lastRefreshTSN = tx.TSN;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Iteration
    // ═══════════════════════════════════════════════════════════════════════

    public HashSet<EntityId>.Enumerator GetEnumerator() => _currentSet.GetEnumerator();

    IEnumerator<EntityId> IEnumerable<EntityId>.GetEnumerator() => _currentSet.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<EntityId>)_currentSet).GetEnumerator();

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _currentSet.Clear();
        _added.Clear();
        _removed.Clear();
    }
}
