using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Typhon.Engine;

public class NavigationQueryBuilder<TSource, TTarget> where TSource : unmanaged where TTarget : unmanaged
{
    private readonly DatabaseEngine _dbe;
    private readonly string _fkFieldName;
    private readonly List<FieldPredicate> _sourcePredicates = new();
    private readonly List<FieldPredicate> _targetPredicates = new();

    internal NavigationQueryBuilder(DatabaseEngine dbe, string fkFieldName)
    {
        _dbe = dbe;
        _fkFieldName = fkFieldName;
    }

    public NavigationQueryBuilder<TSource, TTarget> Where(Expression<Func<TSource, TTarget, bool>> predicate)
    {
        (FieldPredicate[] forSource, FieldPredicate[] forTarget) = ExpressionParser.Parse(predicate);
        _sourcePredicates.AddRange(forSource);
        _targetPredicates.AddRange(forTarget);
        return this;
    }

    public ViewBase ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (_sourcePredicates.Count == 0 && _targetPredicates.Count == 0)
        {
            throw new InvalidOperationException("A Where predicate must be specified before calling ToView().");
        }

        var (sourceCT, targetCT, fkField, fkFieldIndex) = ValidateAndResolve();

        // Resolve evaluators for source (componentTag=0) and target (componentTag=1)
        var sourceEvals = _sourcePredicates.Count > 0 ? QueryResolverHelper.ResolveEvaluators(_sourcePredicates.ToArray(), sourceCT, 0) : [];
        var targetEvals = _targetPredicates.Count > 0 ? QueryResolverHelper.ResolveEvaluators(_targetPredicates.ToArray(), targetCT, 1) : [];

        // Build the NavigationView
        var view = new NavigationView<TSource, TTarget>(sourceEvals, targetEvals, sourceCT, targetCT, fkFieldIndex, fkField.OffsetInComponentStorage, bufferCapacity);

        // Build field dependency arrays for each component table's ViewRegistry
        // Source needs: FK field + any source predicate fields
        var sourceFkFieldIdx = QueryResolverHelper.FindFieldIndex(sourceCT.Definition, fkField);
        var sourceFieldDeps = new HashSet<int> { sourceFkFieldIdx };
        for (var i = 0; i < sourceEvals.Length; i++)
        {
            sourceFieldDeps.Add(sourceEvals[i].FieldIndex);
        }
        var sourceFieldDepsArray = new int[sourceFieldDeps.Count];
        sourceFieldDeps.CopyTo(sourceFieldDepsArray);
        Array.Sort(sourceFieldDepsArray);

        var targetFieldDeps = new int[targetEvals.Length];
        for (var i = 0; i < targetEvals.Length; i++)
        {
            targetFieldDeps[i] = targetEvals[i].FieldIndex;
        }

        // Register in BOTH registries BEFORE population
        sourceCT.ViewRegistry.RegisterView(view, sourceFieldDepsArray, 0);
        if (targetFieldDeps.Length > 0)
        {
            targetCT.ViewRegistry.RegisterView(view, targetFieldDeps, 1);
        }

        // Populate initial entity set
        using var tx = _dbe.CreateQuickTransaction();
        ExecuteNavigation(sourceEvals, targetEvals, sourceCT, targetCT, fkField, tx, view.EntityIdsInternal);

        // Drain any concurrent entries that arrived during population
        view.Refresh(tx);
        view.ClearDelta();

        return view;
    }

    public HashSet<long> Execute(Transaction tx)
    {
        if (_sourcePredicates.Count == 0 && _targetPredicates.Count == 0)
        {
            throw new InvalidOperationException("A Where predicate must be specified.");
        }

        var (sourceCT, targetCT, fkField, _) = ValidateAndResolve();

        var sourceEvals = _sourcePredicates.Count > 0 ? QueryResolverHelper.ResolveEvaluators(_sourcePredicates.ToArray(), sourceCT, 0) : [];
        var targetEvals = _targetPredicates.Count > 0 ? QueryResolverHelper.ResolveEvaluators(_targetPredicates.ToArray(), targetCT, 1) : [];

        var result = new HashSet<long>();
        ExecuteNavigation(sourceEvals, targetEvals, sourceCT, targetCT, fkField, tx, result);
        return result;
    }

    public int Count(Transaction tx) => Execute(tx).Count;

    public bool Any(Transaction tx) => Execute(tx).Count > 0;

    public List<long> ExecuteOrdered(Transaction tx) =>
        throw new InvalidOperationException("ExecuteOrdered is not supported for navigation queries. Use Execute(tx) for unordered results.");

    private void ExecuteNavigation(FieldEvaluator[] sourceEvals, FieldEvaluator[] targetEvals, ComponentTable sourceCT, ComponentTable targetCT,
        DBComponentDefinition.Field fkField, Transaction tx, HashSet<long> result)
    {
        var hasSourcePreds = sourceEvals.Length > 0;
        var hasTargetPreds = targetEvals.Length > 0;

        if (hasTargetPreds)
        {
            // Target-first: scan target, reverse-lookup source entities via FK index
            PipelineExecutor.Instance.ExecuteNavigationTargetFirst<TSource, TTarget>(sourceEvals, targetEvals, sourceCT, targetCT, 
                fkField.OffsetInComponentStorage, tx, result);
        }
        else
        {
            // Source-first: scan source, forward-lookup targets
            PipelineExecutor.Instance.ExecuteNavigationSourceFirst<TSource, TTarget>(sourceEvals, targetEvals, sourceCT, targetCT, 
                fkField.OffsetInComponentStorage, tx, result);
        }
    }

    private (ComponentTable sourceCT, ComponentTable targetCT, DBComponentDefinition.Field fkField, int fkFieldIndex) ValidateAndResolve()
    {
        var sourceCT = _dbe.GetComponentTable<TSource>();
        if (sourceCT == null)
        {
            throw new InvalidOperationException($"Component type {typeof(TSource).Name} is not registered.");
        }

        var targetCT = _dbe.GetComponentTable<TTarget>();
        if (targetCT == null)
        {
            throw new InvalidOperationException($"Component type {typeof(TTarget).Name} is not registered.");
        }

        if (!sourceCT.Definition.FieldsByName.TryGetValue(_fkFieldName, out var fkField))
        {
            throw new InvalidOperationException($"Field '{_fkFieldName}' not found on component '{sourceCT.Definition.Name}'.");
        }

        if (!fkField.IsForeignKey)
        {
            throw new InvalidOperationException($"Field '{_fkFieldName}' is not marked with [ForeignKey]. Navigate() requires a foreign key field.");
        }

        if (fkField.ForeignKeyTargetType != typeof(TTarget))
        {
            throw new InvalidOperationException(
                $"Field '{_fkFieldName}' targets {fkField.ForeignKeyTargetType.Name}, but Navigate<{typeof(TTarget).Name}>() was called.");
        }

        if (!fkField.HasIndex || !fkField.IndexAllowMultiple)
        {
            throw new InvalidOperationException(
                $"Field '{_fkFieldName}' must have [Index(AllowMultiple = true)] for navigation queries (reverse lookup requires AllowMultiple index).");
        }

        var fkFieldIndex = QueryResolverHelper.FindFieldIndex(sourceCT.Definition, fkField);
        return (sourceCT, targetCT, fkField, fkFieldIndex);
    }
}
