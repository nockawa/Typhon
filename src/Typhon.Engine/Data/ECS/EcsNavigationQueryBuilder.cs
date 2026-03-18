using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// ECS-aware navigation query builder. Wraps <see cref="NavigationQueryBuilder{TSource,TTarget}"/> with archetype-aware results.
/// Created via <see cref="EcsQuery{TArchetype}.NavigateField{TSource,TTarget}"/>.
/// </summary>
[PublicAPI]
#pragma warning disable TYPHON005 // Builder borrows Transaction, doesn't own it
public class EcsNavigationQueryBuilder<TSourceArch, TSource, TTarget> where TSourceArch : class where TSource : unmanaged where TTarget : unmanaged
{
    private readonly NavigationQueryBuilder<TSource, TTarget> _inner;
    private readonly EcsQuery<TSourceArch> _query;
    private readonly Transaction _tx;

    internal EcsNavigationQueryBuilder(EcsQuery<TSourceArch> query, Transaction tx, string fkFieldName)
    {
        _query = query;
        _tx = tx;
        _inner = new NavigationQueryBuilder<TSource, TTarget>(tx.DBE, fkFieldName);
    }

    /// <summary>
    /// Filter by source and target predicates. Source parameters come first, target second.
    /// Only indexed fields are supported (same constraint as navigation views).
    /// </summary>
    public EcsNavigationQueryBuilder<TSourceArch, TSource, TTarget> Where(Expression<Func<TSource, TTarget, bool>> predicate)
    {
        _inner.Where(predicate);
        return this;
    }

    /// <summary>Create an incremental navigation view. Registers with both source and target ViewRegistries.</summary>
    public ViewBase ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity) => _inner.ToView(bufferCapacity);

    /// <summary>Execute the navigation query and return matching source entity IDs.</summary>
    public HashSet<EntityId> Execute()
    {
        var pkResult = _inner.Execute(_tx);
        var result = new HashSet<EntityId>();
        foreach (var pk in pkResult)
        {
            var entityId = EntityId.FromRaw(pk);
            if (_query.MaskTestPublic(entityId.ArchetypeId))
            {
                result.Add(entityId);
            }
        }
        return result;
    }

    /// <summary>Count matching source entities.</summary>
    public int Count() => Execute().Count;

    /// <summary>Test if any source entity matches.</summary>
    public bool Any() => Execute().Count > 0;
}
