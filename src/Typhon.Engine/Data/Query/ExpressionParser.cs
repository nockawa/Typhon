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
        CollectPredicates(expression.Body, expression.Parameters[0], predicates);
        return predicates.ToArray();
    }

    private static void CollectPredicates(Expression expr, ParameterExpression param, List<FieldPredicate> predicates)
    {
        if (expr is not BinaryExpression binary)
        {
            throw new NotSupportedException($"Unsupported expression node: {expr.NodeType}");
        }

        if (binary.NodeType == ExpressionType.AndAlso)
        {
            CollectPredicates(binary.Left, param, predicates);
            CollectPredicates(binary.Right, param, predicates);
            return;
        }

        var op = MapCompareOp(binary.NodeType);
        if (op == null)
        {
            throw new NotSupportedException($"Unsupported expression type: {binary.NodeType}");
        }

        var leftField = TryExtractFieldName(binary.Left, param);
        var rightField = TryExtractFieldName(binary.Right, param);

        if (leftField != null && rightField == null)
        {
            var value = EvaluateConstant(binary.Right);
            predicates.Add(new FieldPredicate(leftField, op.Value, value));
        }
        else if (rightField != null && leftField == null)
        {
            var value = EvaluateConstant(binary.Left);
            predicates.Add(new FieldPredicate(rightField, FlipOp(op.Value), value));
        }
        else
        {
            throw new NotSupportedException("Comparison must have exactly one field access and one constant.");
        }
    }

    private static string TryExtractFieldName(Expression expr, ParameterExpression param)
    {
        // Strip Convert wrappers (implicit numeric promotions)
        while (expr is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            expr = unary.Operand;
        }

        if (expr is MemberExpression member && member.Expression == param)
        {
            return member.Member.Name;
        }

        return null;
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
