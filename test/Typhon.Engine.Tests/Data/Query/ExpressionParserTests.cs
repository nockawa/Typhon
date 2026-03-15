#pragma warning disable CS1718 

using NUnit.Framework;
using System;
using System.Linq.Expressions;

namespace Typhon.Engine.Tests;

class ExpressionParserTests
{
    #region FlipOp — all 6 operators via reversed operands

    [Test]
    public void FlipOp_ReversedGreaterThan_BecomesLessThan()
    {
        // 50 > p.B → p.B < 50
        var predicates = ExpressionParser.Parse<CompD>(p => 50 > p.B);
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.LessThan));
        Assert.That(predicates[0].Value, Is.EqualTo(50));
    }

    [Test]
    public void FlipOp_ReversedLessThan_BecomesGreaterThan()
    {
        // 50 < p.B → p.B > 50
        var predicates = ExpressionParser.Parse<CompD>(p => 50 < p.B);
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
    }

    [Test]
    public void FlipOp_ReversedGreaterThanOrEqual_BecomesLessThanOrEqual()
    {
        // 50 >= p.B → p.B <= 50
        var predicates = ExpressionParser.Parse<CompD>(p => 50 >= p.B);
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
    }

    [Test]
    public void FlipOp_ReversedLessThanOrEqual_BecomesGreaterThanOrEqual()
    {
        // 50 <= p.B → p.B >= 50
        var predicates = ExpressionParser.Parse<CompD>(p => 50 <= p.B);
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThanOrEqual));
    }

    [Test]
    public void FlipOp_ReversedEqual_StaysEqual()
    {
        // 42 == p.B → p.B == 42
        var predicates = ExpressionParser.Parse<CompD>(p => 42 == p.B);
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.Equal));
    }

    [Test]
    public void FlipOp_ReversedNotEqual_StaysNotEqual()
    {
        // 42 != p.B → p.B != 42
        var predicates = ExpressionParser.Parse<CompD>(p => 42 != p.B);
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.NotEqual));
    }

    #endregion

    #region InvertOp — all 6 operators via NOT

    [Test]
    public void InvertOp_NotGreaterThan_BecomesLessThanOrEqual()
    {
        var branches = ExpressionParser.ParseDnf<CompD>(p => !(p.B > 50));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
    }

    [Test]
    public void InvertOp_NotLessThan_BecomesGreaterThanOrEqual()
    {
        var branches = ExpressionParser.ParseDnf<CompD>(p => !(p.B < 50));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.GreaterThanOrEqual));
    }

    [Test]
    public void InvertOp_NotGreaterThanOrEqual_BecomesLessThan()
    {
        var branches = ExpressionParser.ParseDnf<CompD>(p => !(p.B >= 50));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThan));
    }

    [Test]
    public void InvertOp_NotLessThanOrEqual_BecomesGreaterThan()
    {
        var branches = ExpressionParser.ParseDnf<CompD>(p => !(p.B <= 50));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.GreaterThan));
    }

    [Test]
    public void InvertOp_NotEqual_BecomesNotEqual()
    {
        var branches = ExpressionParser.ParseDnf<CompD>(p => !(p.B == 50));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.NotEqual));
    }

    [Test]
    public void InvertOp_NotNotEqual_BecomesEqual()
    {
        var branches = ExpressionParser.ParseDnf<CompD>(p => !(p.B != 50));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.Equal));
    }

    #endregion

    #region Double negation

    [Test]
    public void DoubleNegation_CancelsOut()
    {
        // !!(p.B > 50) → p.B > 50
        Expression<Func<CompD, bool>> expr = p => p.B > 50;
        // Build manually with two NOTs since C# won't let us write !!(comparison)
        var notOnce = Expression.Not(expr.Body);
        var notTwice = Expression.Not(notOnce);
        var doubleNeg = Expression.Lambda<Func<CompD, bool>>(notTwice, expr.Parameters);

        var branches = ExpressionParser.ParseDnf(doubleNeg);
        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.GreaterThan));
    }

    [Test]
    public void TripleNegation_EqualsNegation()
    {
        // !!!(p.B > 50) → !(p.B > 50) → p.B <= 50
        Expression<Func<CompD, bool>> expr = p => p.B > 50;
        var not1 = Expression.Not(expr.Body);
        var not2 = Expression.Not(not1);
        var not3 = Expression.Not(not2);
        var tripleNeg = Expression.Lambda<Func<CompD, bool>>(not3, expr.Parameters);

        var branches = ExpressionParser.ParseDnf(tripleNeg);
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
    }

    #endregion

    #region De Morgan — full symmetry

    [Test]
    public void DeMorgan_NotOr_BecomesAndNot()
    {
        // !(A > 3.0f || B > 40) → A <= 3.0f && B <= 40 — single branch
        Expression<Func<CompD, bool>> expr = p => !(p.A > 3.0f || p.B > 40);
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(2));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
        Assert.That(branches[0][1].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
    }

    [Test]
    public void DeMorgan_NotOr_NestedWithAnd()
    {
        // !(A > 3.0f || B > 40) && C > 1.0 → single branch: A <= 3.0f AND B <= 40 AND C > 1.0
        Expression<Func<CompD, bool>> expr = p => !(p.A > 3.0f || p.B > 40) && p.C > 1.0;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(3));
    }

    #endregion

    #region DNF branch limit

    [Test]
    public void ParseDnf_Exactly16Branches_Succeeds()
    {
        // (A||B) && (C||D) && (E||F) && (G||H) = 2^4 = 16 branches (at the limit)
        // Using same fields with different thresholds
        Expression<Func<CompD, bool>> expr = p =>
            (p.B > 100 || p.B < 5) &&
            (p.A > 10.0f || p.A < 1.0f) &&
            (p.C > 50.0 || p.C < 1.0) &&
            (p.B > 200 || p.B < 3);

        var branches = ExpressionParser.ParseDnf(expr);
        Assert.That(branches.Length, Is.EqualTo(16));
    }

    [Test]
    public void ParseDnf_Over16Branches_Throws()
    {
        // (A||B) && (C||D) && (E||F) && (G||H) && (I||J) = 2^5 = 32 branches
        Expression<Func<CompD, bool>> expr = p =>
            (p.B > 100 || p.B < 5) &&
            (p.A > 10.0f || p.A < 1.0f) &&
            (p.C > 50.0 || p.C < 1.0) &&
            (p.B > 200 || p.B < 3) &&
            (p.A > 99.0f || p.A < 0.1f);

        var ex = Assert.Throws<InvalidOperationException>(() => ExpressionParser.ParseDnf(expr));
        Assert.That(ex.Message, Does.Contain("32 DNF clauses"));
        Assert.That(ex.Message, Does.Contain("max 16"));
    }

    #endregion

    #region Reversed operand + negation combo

    [Test]
    public void ReversedOperand_WithNot_InvertsFlipped()
    {
        // !(50 < p.B) → !(p.B > 50) → p.B <= 50
        Expression<Func<CompD, bool>> innerExpr = p => 50 < p.B;
        var notExpr = Expression.Not(innerExpr.Body);
        var lambda = Expression.Lambda<Func<CompD, bool>>(notExpr, innerExpr.Parameters);

        var branches = ExpressionParser.ParseDnf(lambda);
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
        Assert.That(branches[0][0].Value, Is.EqualTo(50));
    }

    [Test]
    public void ReversedOperand_GreaterThanOrEqual_WithNot()
    {
        // !(50 >= p.B) → !(p.B <= 50) → p.B > 50
        Expression<Func<CompD, bool>> innerExpr = p => 50 >= p.B;
        var notExpr = Expression.Not(innerExpr.Body);
        var lambda = Expression.Lambda<Func<CompD, bool>>(notExpr, innerExpr.Parameters);

        var branches = ExpressionParser.ParseDnf(lambda);
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.GreaterThan));
    }

    #endregion

    #region Complex constant expressions

    [Test]
    public void Parse_ArithmeticInConstant_Evaluates()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.B > 20 + 30);
        Assert.That(predicates[0].Value, Is.EqualTo(50));
    }

    [Test]
    public void Parse_MethodCallInConstant_Evaluates()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.B > Math.Max(10, 42));
        Assert.That(predicates[0].Value, Is.EqualTo(42));
    }

    [Test]
    public void Parse_NestedClosure_Evaluates()
    {
        var outer = new { Value = 99 };
        var predicates = ExpressionParser.Parse<CompD>(p => p.B > outer.Value);
        Assert.That(predicates[0].Value, Is.EqualTo(99));
    }

    [Test]
    public void Parse_CastInConstant_Evaluates()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.A > (float)(int)42);
        Assert.That(predicates[0].Value, Is.EqualTo(42.0f));
    }

    #endregion

    #region Error paths

    [Test]
    public void Parse_FieldToField_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            // ReSharper disable once EqualExpressionComparison
            ExpressionParser.Parse<CompD>(p => p.B > p.B));
    }

    [Test]
    public void Parse_UnsupportedBinaryOperator_Throws()
    {
        // ExclusiveOr is a valid bool-returning BinaryExpression but not a comparison operator
        var param = Expression.Parameter(typeof(CompD), "p");
        var xor = Expression.ExclusiveOr(Expression.Constant(true), Expression.Constant(false));
        var lambda = Expression.Lambda<Func<CompD, bool>>(xor, param);
        Assert.Throws<NotSupportedException>(() => ExpressionParser.Parse<CompD>(lambda));
    }

    [Test]
    public void Parse_NonBinaryExpression_Throws()
    {
        // Method call expression as root — not a BinaryExpression
        var param = Expression.Parameter(typeof(CompD), "p");
        var methodCall = Expression.Call(typeof(Console).GetMethod("WriteLine", [typeof(string)]),
            Expression.Constant("hello"));
        // Can't use this directly as Func<CompD, bool> body since it returns void.
        // Use a unary (not a binary) that isn't a Not:
        var constant = Expression.Constant(true);
        var lambda = Expression.Lambda<Func<CompD, bool>>(constant, param);
        Assert.Throws<NotSupportedException>(() => ExpressionParser.Parse<CompD>(lambda));
    }

    [Test]
    public void ExtractFieldName_NonFieldAccess_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            ExpressionParser.ExtractFieldName<CompD, int>(p => p.B + 1));
    }

    #endregion

    #region Deeply nested expressions

    [Test]
    public void ParseDnf_ThreeWayOr_ThreeBranches()
    {
        Expression<Func<CompD, bool>> expr = p => p.B > 50 || p.B < 10 || p.B == 25;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(3));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(branches[1][0].Operator, Is.EqualTo(CompareOp.LessThan));
        Assert.That(branches[2][0].Operator, Is.EqualTo(CompareOp.Equal));
    }

    [Test]
    public void ParseDnf_ThreeWayAnd_SingleBranch()
    {
        Expression<Func<CompD, bool>> expr = p => p.A > 1.0f && p.B > 10 && p.C > 2.0;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(3));
    }

    [Test]
    public void ParseDnf_SinglePredicate_OneBranchOnePredicate()
    {
        Expression<Func<CompD, bool>> expr = p => p.B == 42;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(1));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.Equal));
    }

    [Test]
    public void ParseDnf_OrInsideOr_Flattened()
    {
        // (A || B) || C should flatten to 3 branches, not nested
        Expression<Func<CompD, bool>> expr = p => (p.B > 50 || p.B < 10) || p.B == 25;
        var branches = ExpressionParser.ParseDnf(expr);
        Assert.That(branches.Length, Is.EqualTo(3));
    }

    [Test]
    public void ParseDnf_AndInsideAnd_Flattened()
    {
        // (A && B) && C should flatten to 1 branch with 3 predicates
        Expression<Func<CompD, bool>> expr = p => (p.A > 1.0f && p.B > 10) && p.C > 2.0;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(3));
    }

    #endregion
}
