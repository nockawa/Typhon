using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

public class QueryBuilder<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    private readonly DatabaseEngine _dbe;
    private Expression<Func<T1, T2, bool>> _predicate;

    internal QueryBuilder(DatabaseEngine dbe)
    {
        _dbe = dbe;
    }

    public QueryBuilder<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
    {
        _predicate = predicate;
        return this;
    }

    public View<T1, T2> ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (_predicate == null)
        {
            throw new InvalidOperationException("A Where predicate must be specified before calling ToView().");
        }

        var (predicatesForT1, predicatesForT2) = ExpressionParser.Parse<T1, T2>(_predicate);

        var ct1 = _dbe.GetComponentTable<T1>();
        if (ct1 == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T1).Name} is not registered.");
        }

        var ct2 = _dbe.GetComponentTable<T2>();
        if (ct2 == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T2).Name} is not registered.");
        }

        // Resolve evaluators for each component with their respective tags
        var evals1 = QueryResolverHelper.ResolveEvaluators(predicatesForT1, ct1, 0);
        var evals2 = QueryResolverHelper.ResolveEvaluators(predicatesForT2, ct2, 1);

        // Combine evaluators
        var combinedEvaluators = new FieldEvaluator[evals1.Length + evals2.Length];
        Array.Copy(evals1, combinedEvaluators, evals1.Length);
        Array.Copy(evals2, 0, combinedEvaluators, evals1.Length, evals2.Length);

        var view = new View<T1, T2>(combinedEvaluators, ct1.ViewRegistry, ct2.ViewRegistry, bufferCapacity);

        // Build field dependency arrays for each component table
        var fieldDeps1 = new int[evals1.Length];
        for (var i = 0; i < evals1.Length; i++)
        {
            fieldDeps1[i] = evals1[i].FieldIndex;
        }

        var fieldDeps2 = new int[evals2.Length];
        for (var i = 0; i < evals2.Length; i++)
        {
            fieldDeps2[i] = evals2[i].FieldIndex;
        }

        // Register in BOTH registries BEFORE scanning PK index
        ct1.ViewRegistry.RegisterView(view, fieldDeps1, 0);
        ct2.ViewRegistry.RegisterView(view, fieldDeps2, 1);

        // Populate initial entity set via PK index scan on T1
        using var tx = _dbe.CreateQuickTransaction();
        var pkIndex = ct1.PrimaryKeyIndex;

        foreach (var kv in pkIndex.EnumerateLeaves())
        {
            if (tx.ReadEntity<T1>(kv.Key, out var comp1) && tx.ReadEntity<T2>(kv.Key, out var comp2))
            {
                if (EvaluateAll(combinedEvaluators, ref comp1, ref comp2))
                {
                    view.AddEntityDirect(kv.Key);
                }
            }
        }

        // Drain any concurrent entries that arrived during population
        view.Refresh(tx);
        // Discard baseline artifacts
        view.ClearDelta();

        return view;
    }

    private static unsafe bool EvaluateAll(FieldEvaluator[] evaluators, ref T1 comp1, ref T2 comp2)
    {
        var comp1Ptr = (byte*)Unsafe.AsPointer(ref comp1);
        var comp2Ptr = (byte*)Unsafe.AsPointer(ref comp2);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            var ptr = eval.ComponentTag == 0 ? comp1Ptr : comp2Ptr;
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }
}
