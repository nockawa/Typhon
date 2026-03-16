using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Typhon.Engine;

public class QueryBuilder<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    private readonly DatabaseEngine _dbe;
    private readonly List<FieldPredicate> _predicatesT1 = new();
    private readonly List<FieldPredicate> _predicatesT2 = new();

    internal QueryBuilder(DatabaseEngine dbe)
    {
        _dbe = dbe;
    }

    public QueryBuilder<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
    {
        var (forT1, forT2) = ExpressionParser.Parse(predicate);
        _predicatesT1.AddRange(forT1);
        _predicatesT2.AddRange(forT2);
        return this;
    }

    public View<T1, T2> ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (_predicatesT1.Count == 0 && _predicatesT2.Count == 0)
        {
            throw new InvalidOperationException("A Where predicate must be specified before calling ToView().");
        }

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
        var evals1 = QueryResolverHelper.ResolveEvaluators(_predicatesT1.ToArray(), ct1, 0);
        var evals2 = QueryResolverHelper.ResolveEvaluators(_predicatesT2.ToArray(), ct2, 1);

        // Combine evaluators for filter evaluation
        var combinedEvaluators = new FieldEvaluator[evals1.Length + evals2.Length];
        Array.Copy(evals1, combinedEvaluators, evals1.Length);
        Array.Copy(evals2, 0, combinedEvaluators, evals1.Length, evals2.Length);

        // Select best plan: prefer secondary index, fallback to PK scan
        var (plan, planTable) = SelectBestPlan(evals1, ct1, evals2, ct2);

        var view = new View<T1, T2>(combinedEvaluators, ct1.ViewRegistry, ct2.ViewRegistry, ct1, plan, planTable, bufferCapacity);

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

        // Register in BOTH registries BEFORE population
        ct1.ViewRegistry.RegisterView(view, fieldDeps1, 0);
        ct2.ViewRegistry.RegisterView(view, fieldDeps2, 1);

        // Populate initial entity set via pipeline execution
        using var tx = _dbe.CreateQuickTransaction();
        PipelineExecutor.Instance.Execute<T1, T2>(plan, combinedEvaluators, planTable, tx, view.EntityIdsInternal);

        // Drain any concurrent entries that arrived during population
        view.Refresh(tx);
        view.ClearDelta();

        return view;
    }

    public ExecutionPlan GetExecutionPlan()
    {
        if (_predicatesT1.Count == 0 && _predicatesT2.Count == 0)
        {
            throw new InvalidOperationException("A Where predicate must be specified.");
        }

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

        var evals1 = QueryResolverHelper.ResolveEvaluators(_predicatesT1.ToArray(), ct1, 0);
        var evals2 = QueryResolverHelper.ResolveEvaluators(_predicatesT2.ToArray(), ct2, 1);

        var (plan, _) = SelectBestPlan(evals1, ct1, evals2, ct2);
        return plan;
    }

    private static (ExecutionPlan Plan, ComponentTable Table) SelectBestPlan(FieldEvaluator[] evals1, ComponentTable ct1, FieldEvaluator[] evals2, ComponentTable ct2)
    {
        var estimator = AdvancedSelectivityEstimator.Instance;

        ExecutionPlan? plan1 = evals1.Length > 0 ? PlanBuilder.Instance.BuildPlan(evals1, ct1, estimator) : null;
        ExecutionPlan? plan2 = evals2.Length > 0 ? PlanBuilder.Instance.BuildPlan(evals2, ct2, estimator) : null;

        if (plan1.HasValue && plan2.HasValue)
        {
            if (plan1.Value.UsesSecondaryIndex && !plan2.Value.UsesSecondaryIndex)
            {
                return (plan1.Value, ct1);
            }
            if (plan2.Value.UsesSecondaryIndex && !plan1.Value.UsesSecondaryIndex)
            {
                return (plan2.Value, ct2);
            }
            // Both use secondary index — pick the one with lower primary stream estimate.
            // Both use PK scan — default to T1.
            if (plan1.Value.UsesSecondaryIndex && plan2.Value.UsesSecondaryIndex)
            {
                var est1 = plan1.Value.EstimatedCounts.Length > 0 ? plan1.Value.EstimatedCounts[0] : long.MaxValue;
                var est2 = plan2.Value.EstimatedCounts.Length > 0 ? plan2.Value.EstimatedCounts[0] : long.MaxValue;
                return est1 <= est2 ? (plan1.Value, ct1) : (plan2.Value, ct2);
            }
            return (plan1.Value, ct1);
        }

        if (plan1.HasValue)
        {
            return (plan1.Value, ct1);
        }
        if (plan2.HasValue)
        {
            return (plan2.Value, ct2);
        }

        // No evaluators at all — PK scan on T1 (shouldn't happen, validation catches this)
        return (PlanBuilder.Instance.BuildPlan([], ct1, estimator), ct1);
    }
}
