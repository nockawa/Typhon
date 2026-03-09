using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Typhon.Engine;

internal readonly struct FieldPredicate
{
    public readonly string FieldName;
    public readonly CompareOp Operator;
    public readonly object Value;

    public FieldPredicate(string fieldName, CompareOp op, object value)
    {
        FieldName = fieldName;
        Operator = op;
        Value = value;
    }
}

internal static class ExpressionParser
{
    public static FieldPredicate[] Parse<T>(Expression<Func<T, bool>> expression)
    {
        var predicates = new List<FieldPredicate>();
        CollectPredicates(expression.Body, [expression.Parameters[0]], predicates, null);
        return predicates.ToArray();
    }

    public static string ExtractFieldName<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var body = keySelector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }
        if (body is MemberExpression member && member.Expression == keySelector.Parameters[0])
        {
            return member.Member.Name;
        }
        throw new NotSupportedException("OrderBy expression must be a simple field access (e.g., p => p.Level).");
    }

    public static (FieldPredicate[] ForT1, FieldPredicate[] ForT2) Parse<T1, T2>(Expression<Func<T1, T2, bool>> expression)
    {
        var predicates = new List<FieldPredicate>();
        var paramIndices = new List<int>();
        CollectPredicates(expression.Body, [expression.Parameters[0], expression.Parameters[1]], predicates, paramIndices);

        var forT1 = new List<FieldPredicate>();
        var forT2 = new List<FieldPredicate>();
        for (var i = 0; i < predicates.Count; i++)
        {
            if (paramIndices[i] == 0)
            {
                forT1.Add(predicates[i]);
            }
            else
            {
                forT2.Add(predicates[i]);
            }
        }

        return (forT1.ToArray(), forT2.ToArray());
    }

    private static void CollectPredicates(Expression expr, ParameterExpression[] parameters, List<FieldPredicate> predicates,
        List<int> paramIndices)
    {
        if (expr is not BinaryExpression binary)
        {
            throw new NotSupportedException($"Unsupported expression node: {expr.NodeType}");
        }

        if (binary.NodeType == ExpressionType.AndAlso)
        {
            CollectPredicates(binary.Left, parameters, predicates, paramIndices);
            CollectPredicates(binary.Right, parameters, predicates, paramIndices);
            return;
        }

        var op = MapCompareOp(binary.NodeType);
        if (op == null)
        {
            throw new NotSupportedException($"Unsupported expression type: {binary.NodeType}");
        }

        var (leftField, leftParamIndex) = TryExtractFieldWithParam(binary.Left, parameters);
        var (rightField, rightParamIndex) = TryExtractFieldWithParam(binary.Right, parameters);

        if (leftField != null && rightField != null)
        {
            throw new NotSupportedException("Field-to-field comparisons across parameters are not supported.");
        }

        if (leftField != null && rightField == null)
        {
            var value = EvaluateConstant(binary.Right);
            predicates.Add(new FieldPredicate(leftField, op.Value, value));
            paramIndices?.Add(leftParamIndex);
        }
        else if (rightField != null && leftField == null)
        {
            var value = EvaluateConstant(binary.Left);
            predicates.Add(new FieldPredicate(rightField, FlipOp(op.Value), value));
            paramIndices?.Add(rightParamIndex);
        }
        else
        {
            throw new NotSupportedException("Comparison must have exactly one field access and one constant.");
        }
    }

    private static (string fieldName, int paramIndex) TryExtractFieldWithParam(Expression expr, ParameterExpression[] parameters)
    {
        // Strip Convert wrappers (implicit numeric promotions)
        while (expr is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            expr = unary.Operand;
        }

        if (expr is MemberExpression member)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (member.Expression == parameters[i])
                {
                    return (member.Member.Name, i);
                }
            }
        }

        return (null, -1);
    }

    private static object EvaluateConstant(Expression expr)
    {
        if (expr is ConstantExpression constant)
        {
            return constant.Value;
        }

        // Handle closures, type conversions, nested expressions
        return Expression.Lambda(expr).Compile().DynamicInvoke();
    }

    private static CompareOp? MapCompareOp(ExpressionType type) =>
        type switch
        {
            ExpressionType.Equal => CompareOp.Equal,
            ExpressionType.NotEqual => CompareOp.NotEqual,
            ExpressionType.GreaterThan => CompareOp.GreaterThan,
            ExpressionType.LessThan => CompareOp.LessThan,
            ExpressionType.GreaterThanOrEqual => CompareOp.GreaterThanOrEqual,
            ExpressionType.LessThanOrEqual => CompareOp.LessThanOrEqual,
            _ => null
        };

    private static CompareOp FlipOp(CompareOp op) =>
        op switch
        {
            CompareOp.GreaterThan => CompareOp.LessThan,
            CompareOp.LessThan => CompareOp.GreaterThan,
            CompareOp.GreaterThanOrEqual => CompareOp.LessThanOrEqual,
            CompareOp.LessThanOrEqual => CompareOp.GreaterThanOrEqual,
            _ => op // Equal and NotEqual are symmetric
        };
}
