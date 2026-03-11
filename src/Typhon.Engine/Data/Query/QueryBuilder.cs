using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

public class QueryBuilder<T> where T : unmanaged
{
    private readonly DatabaseEngine _dbe;
    private readonly List<Expression<Func<T, bool>>> _whereExpressions = new();
    private OrderByField? _orderBy;
    private int _skip;
    private int _take = int.MaxValue;

    internal QueryBuilder(DatabaseEngine dbe)
    {
        _dbe = dbe;
    }

    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        _whereExpressions.Add(predicate);
        return this;
    }

    public NavigationQueryBuilder<T, TTarget> Navigate<TTarget>(Expression<Func<T, long>> fkSelector) where TTarget : unmanaged
    {
        if (_whereExpressions.Count > 0)
        {
            throw new InvalidOperationException("Navigate() must be called before Where(). Place all predicates in Where() after Navigate().");
        }
        var fkFieldName = ExpressionParser.ExtractFieldName(fkSelector);
        return new NavigationQueryBuilder<T, TTarget>(_dbe, fkFieldName);
    }

    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderBy = new OrderByField(ResolveOrderByFieldIndex(keySelector));
        return this;
    }

    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderBy = new OrderByField(ResolveOrderByFieldIndex(keySelector), descending: true);
        return this;
    }

    public QueryBuilder<T> OrderByPK(bool descending = false)
    {
        _orderBy = new OrderByField(-1, descending: descending);
        return this;
    }

    public QueryBuilder<T> Skip(int count)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("Skip requires OrderBy.");
        }
        _skip = count;
        return this;
    }

    public QueryBuilder<T> Take(int count)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("Take requires OrderBy.");
        }
        _take = count;
        return this;
    }

    public ViewBase ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (_orderBy.HasValue)
        {
            throw new InvalidOperationException("OrderBy is not supported with ToView(). Use Execute(tx) for ordered results.");
        }
        ValidatePredicates();

        var ct = GetComponentTable();
        var branches = ResolveBranches();

        if (branches.Length > 1)
        {
            // OR path: create per-branch evaluators and plans
            var branchEvaluators = new FieldEvaluator[branches.Length][];
            var plans = new ExecutionPlan[branches.Length];
            for (var b = 0; b < branches.Length; b++)
            {
                branchEvaluators[b] = QueryResolverHelper.ResolveEvaluators(branches[b], ct, 0, (byte)b);
                plans[b] = PlanBuilder.Instance.BuildPlan(branchEvaluators[b], ct, AdvancedSelectivityEstimator.Instance);
            }

            var orView = new OrView<T>(branchEvaluators, plans, ct.ViewRegistry, ct, bufferCapacity);
            ct.ViewRegistry.RegisterView(orView);

            using var tx = _dbe.CreateQuickTransaction();
            orView.PopulateInitial(tx);
            orView.Refresh(tx);
            orView.ClearDelta();

            return orView;
        }

        // AND path: single branch
        var evaluators = ResolveEvaluators(branches[0], ct);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);

        var view = new View<T>(evaluators, ct.ViewRegistry, ct, plan, bufferCapacity);
        ct.ViewRegistry.RegisterView(view);

        using var andTx = _dbe.CreateQuickTransaction();
        PipelineExecutor.Instance.Execute<T>(plan, plan.OrderedEvaluators, ct, andTx, view.EntityIdsInternal);

        view.Refresh(andTx);
        view.ClearDelta();

        return view;
    }

    public HashSet<long> Execute(Transaction tx)
    {
        ValidatePredicates();
        var ct = GetComponentTable();
        var branches = ResolveBranches();

        if (branches.Length > 1)
        {
            return ExecuteOrBranches(branches, ct, tx);
        }

        var evaluators = ResolveEvaluators(branches[0], ct);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
        var result = new HashSet<long>();
        PipelineExecutor.Instance.Execute<T>(plan, plan.OrderedEvaluators, ct, tx, result);
        return result;
    }

    public List<long> ExecuteOrdered(Transaction tx)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("ExecuteOrdered requires OrderBy.");
        }
        ValidatePredicates();
        var ct = GetComponentTable();
        var branches = ResolveBranches();

        if (branches.Length > 1)
        {
            throw new InvalidOperationException("ExecuteOrdered with OR predicates is not supported. Use Execute(tx) for unordered results.");
        }

        var evaluators = ResolveEvaluators(branches[0], ct);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance, _orderBy.Value);
        var result = new List<long>();
        PipelineExecutor.Instance.ExecuteOrdered<T>(plan, plan.OrderedEvaluators, ct, tx, result, _skip, _take);
        return result;
    }

    public int Count(Transaction tx)
    {
        ValidatePredicates();
        var ct = GetComponentTable();
        var branches = ResolveBranches();

        if (branches.Length > 1)
        {
            return ExecuteOrBranches(branches, ct, tx).Count;
        }

        var evaluators = ResolveEvaluators(branches[0], ct);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
        return PipelineExecutor.Instance.Count<T>(plan, plan.OrderedEvaluators, ct, tx);
    }

    public bool Any(Transaction tx)
    {
        ValidatePredicates();
        var ct = GetComponentTable();
        var branches = ResolveBranches();

        if (branches.Length > 1)
        {
            return ExecuteOrBranches(branches, ct, tx).Count > 0;
        }

        var evaluators = ResolveEvaluators(branches[0], ct);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
        var result = new List<long>();
        PipelineExecutor.Instance.ExecuteOrdered<T>(plan, plan.OrderedEvaluators, ct, tx, result, 0, 1);
        return result.Count > 0;
    }

    public ExecutionPlan GetExecutionPlan()
    {
        ValidatePredicates();
        var ct = GetComponentTable();
        var branches = ResolveBranches();

        if (branches.Length > 1)
        {
            throw new InvalidOperationException("GetExecutionPlan is not supported for OR predicates (multiple plans, one per branch).");
        }

        var evaluators = ResolveEvaluators(branches[0], ct);
        return _orderBy.HasValue ? PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance, _orderBy.Value) :
            PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
    }

    /// <summary>
    /// Executes an OR query by running each branch independently and unioning results.
    /// Each branch is planned and executed separately — the union deduplicates naturally via HashSet.
    /// </summary>
    private HashSet<long> ExecuteOrBranches(FieldPredicate[][] branches, ComponentTable ct, Transaction tx)
    {
        var result = new HashSet<long>();
        for (var b = 0; b < branches.Length; b++)
        {
            var evaluators = QueryResolverHelper.ResolveEvaluators(branches[b], ct, 0, (byte)b);
            var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
            PipelineExecutor.Instance.Execute<T>(plan, plan.OrderedEvaluators, ct, tx, result);
        }
        return result;
    }

    private void ValidatePredicates()
    {
        if (_whereExpressions.Count == 0)
        {
            throw new InvalidOperationException("A Where predicate must be specified.");
        }
    }

    /// <summary>
    /// Resolves all Where expressions into DNF branches.
    /// Single expression: direct ParseDnf.
    /// Multiple expressions: ParseDnf each, then cross-product (AND of ORs).
    /// </summary>
    private FieldPredicate[][] ResolveBranches()
    {
        if (_whereExpressions.Count == 1)
        {
            return ExpressionParser.ParseDnf(_whereExpressions[0]);
        }

        // Multiple Where() calls: cross-product all branch sets (AND combination)
        FieldPredicate[][] current = ExpressionParser.ParseDnf(_whereExpressions[0]);
        for (var i = 1; i < _whereExpressions.Count; i++)
        {
            var next = ExpressionParser.ParseDnf(_whereExpressions[i]);
            current = CrossProductBranches(current, next);
            if (current.Length > ExpressionParser.MaxDnfBranches)
            {
                throw new InvalidOperationException(
                    $"Chained Where() expressions produce {current.Length} DNF clauses (max {ExpressionParser.MaxDnfBranches}). " +
                    "Each chained Where() with OR multiplies the clause count. " +
                    "Combine predicates into a single Where() expression or reduce OR pairs.");
            }
        }
        return current;
    }

    /// <summary>
    /// Cross-products two branch sets: AND(OR(a1,a2), OR(b1,b2)) → OR(a1∧b1, a1∧b2, a2∧b1, a2∧b2).
    /// For pure AND chains (each has 1 branch), this reduces to simple predicate concatenation.
    /// </summary>
    private static FieldPredicate[][] CrossProductBranches(FieldPredicate[][] left, FieldPredicate[][] right)
    {
        var result = new FieldPredicate[left.Length * right.Length][];
        var idx = 0;
        for (var l = 0; l < left.Length; l++)
        {
            for (var r = 0; r < right.Length; r++)
            {
                var combined = new FieldPredicate[left[l].Length + right[r].Length];
                Array.Copy(left[l], combined, left[l].Length);
                Array.Copy(right[r], 0, combined, left[l].Length, right[r].Length);
                result[idx++] = combined;
            }
        }
        return result;
    }

    private ComponentTable GetComponentTable()
    {
        var ct = _dbe.GetComponentTable<T>();
        if (ct == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T).Name} is not registered.");
        }
        return ct;
    }

    private int ResolveOrderByFieldIndex<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var fieldName = ExpressionParser.ExtractFieldName(keySelector);
        var ct = GetComponentTable();
        if (!ct.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on component '{ct.Definition.Name}'.");
        }
        if (!field.HasIndex)
        {
            throw new InvalidOperationException($"Field '{fieldName}' must be indexed to use as OrderBy.");
        }
        return QueryResolverHelper.FindFieldIndex(ct.Definition, field);
    }

    private static FieldEvaluator[] ResolveEvaluators(FieldPredicate[] predicates, ComponentTable ct) =>
        QueryResolverHelper.ResolveEvaluators(predicates, ct, 0);
}

internal static class QueryResolverHelper
{
    public static FieldEvaluator[] ResolveEvaluators(FieldPredicate[] predicates, ComponentTable ct, byte componentTag, byte branchIndex = 0)
    {
        var definition = ct.Definition;
        var evaluators = new FieldEvaluator[predicates.Length];

        for (var i = 0; i < predicates.Length; i++)
        {
            var pred = predicates[i];

            if (!definition.FieldsByName.TryGetValue(pred.FieldName, out var field))
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' not found on component '{definition.Name}'.");
            }

            if (!field.HasIndex)
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' is not indexed. View predicates require indexed fields.");
            }

            var fieldIndex = FindFieldIndex(definition, field);
            var keyType = MapFieldTypeToKeyType(field.Type);
            var threshold = EncodeThreshold(pred.Value, keyType);

            evaluators[i] = new FieldEvaluator
            {
                FieldIndex = (byte)fieldIndex,
                FieldOffset = (ushort)field.OffsetInComponentStorage,
                FieldSize = (byte)field.FieldSize,
                KeyType = keyType,
                CompareOp = pred.Operator,
                Threshold = threshold,
                ComponentTag = componentTag,
                BranchIndex = branchIndex
            };
        }

        return evaluators;
    }

    /// <summary>
    /// Finds the index into IndexedFieldInfos[] by replicating the iteration order from BuildIndexedFieldInfo.
    /// </summary>
    public static int FindFieldIndex(DBComponentDefinition definition, DBComponentDefinition.Field targetField)
    {
        var index = 0;
        for (var i = 0; i < definition.MaxFieldId; i++)
        {
            var f = definition[i];
            if (f == null || !f.HasIndex)
            {
                continue;
            }

            if (f == targetField)
            {
                return index;
            }

            index++;
        }

        throw new InvalidOperationException($"Field '{targetField.Name}' not found in indexed fields.");
    }

    public static KeyType MapFieldTypeToKeyType(FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Boolean => KeyType.Bool,
            FieldType.Byte => KeyType.SByte,      // FieldType.Byte = signed byte (sbyte)
            FieldType.UByte => KeyType.Byte,       // FieldType.UByte = unsigned byte (byte)
            FieldType.Short => KeyType.Short,
            FieldType.UShort => KeyType.UShort,
            FieldType.Int => KeyType.Int,
            FieldType.UInt => KeyType.UInt,
            FieldType.Long => KeyType.Long,
            FieldType.ULong => KeyType.ULong,
            FieldType.Float => KeyType.Float,
            FieldType.Double => KeyType.Double,
            _ => throw new NotSupportedException($"Field type {fieldType} is not supported for view predicates.")
        };

    public static long EncodeThreshold(object value, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool:
                return Convert.ToBoolean(value) ? 1L : 0L;
            case KeyType.SByte:
                return Convert.ToSByte(value);
            case KeyType.Byte:
                return Convert.ToByte(value);
            case KeyType.Short:
                return Convert.ToInt16(value);
            case KeyType.UShort:
                return Convert.ToUInt16(value);
            case KeyType.Int:
                return Convert.ToInt32(value);
            case KeyType.UInt:
                return Convert.ToUInt32(value);
            case KeyType.Long:
                return Convert.ToInt64(value);
            case KeyType.ULong:
                return (long)Convert.ToUInt64(value);
            case KeyType.Float:
            {
                var f = Convert.ToSingle(value);
                return Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = Convert.ToDouble(value);
                return Unsafe.As<double, long>(ref d);
            }
            default:
                throw new NotSupportedException($"Key type {keyType} is not supported.");
        }
    }
}
