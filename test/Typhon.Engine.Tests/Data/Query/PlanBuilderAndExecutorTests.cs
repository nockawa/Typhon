using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests;

class PlanBuilderAndExecutorTests : TestBase<PlanBuilderAndExecutorTests>
{
    #region Helpers

    private static long CreateEntity(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var pk = t.CreateEntity(ref d);
        t.Commit();
        return pk;
    }

    /// <summary>
    /// Resolves evaluators and builds an execution plan using the basic selectivity estimator.
    /// Returns the plan and the ordered evaluators (from the plan).
    /// </summary>
    private static (ExecutionPlan Plan, FieldEvaluator[] Evaluators) BuildPlanFromExpression(DatabaseEngine dbe, 
        System.Linq.Expressions.Expression<System.Func<CompD, bool>> predicate, OrderByField? orderBy = null)
    {
        var fieldPredicates = ExpressionParser.Parse<CompD>(predicate);
        var ct = dbe.GetComponentTable<CompD>();
        var allEvaluators = QueryResolverHelper.ResolveEvaluators(fieldPredicates, ct, 0);
        var estimator = BasicSelectivityEstimator.Instance;

        var plan = orderBy.HasValue ? PlanBuilder.Instance.BuildPlan(allEvaluators, ct, estimator, orderBy.Value) : PlanBuilder.Instance.BuildPlan(allEvaluators, ct, estimator);

        return (plan, plan.OrderedEvaluators);
    }

    private static HashSet<long> ExecutePlan(DatabaseEngine dbe, ExecutionPlan plan)
    {
        var ct = dbe.GetComponentTable<CompD>();
        using var tx = dbe.CreateQuickTransaction();
        return PipelineExecutor.Instance.Execute<CompD>(plan, plan.OrderedEvaluators, ct, tx);
    }

    private static List<long> ExecutePlanOrdered(DatabaseEngine dbe, ExecutionPlan plan,
        int skip = 0, int take = int.MaxValue)
    {
        var ct = dbe.GetComponentTable<CompD>();
        using var tx = dbe.CreateQuickTransaction();
        return PipelineExecutor.Instance.ExecuteOrdered<CompD>(plan, plan.OrderedEvaluators, ct, tx, skip, take);
    }

    #endregion

    #region PlanBuilder Tests

    [Test]
    public void PlanBuilder_SinglePredicate_SingleEvaluator()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 50, 2.0);
        var (plan, evaluators) = BuildPlanFromExpression(dbe, p => p.B > 40);

        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(1));
        Assert.That(evaluators, Has.Length.EqualTo(1));
    }

    [Test]
    public void PlanBuilder_MultiPredicate_SortedByAscendingEstimate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create 100 entities with varying field values
        for (var i = 0; i < 100; i++)
        {
            CreateEntity(dbe, i / 10.0f, i, i * 1.0);
        }

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 90 && p.A > 5.0f);

        // Verify evaluators are ordered by ascending estimated cardinality
        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(2));
        Assert.That(plan.EstimatedCounts, Has.Length.EqualTo(2));
        Assert.That(plan.EstimatedCounts[0], Is.LessThanOrEqualTo(plan.EstimatedCounts[1]));
    }

    [Test]
    public void PlanBuilder_OrderBy_SetsDescendingFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        for (var i = 0; i < 50; i++)
        {
            CreateEntity(dbe, i * 1.0f, i, i * 1.0);
        }

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        // OrderBy sets the descending flag; evaluators still ordered by selectivity
        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 10 && p.A > 5.0f, orderBy);

        Assert.That(plan.Descending, Is.False);
        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(2));
    }

    [Test]
    public void PlanBuilder_OrderByPK_AllEvaluatorsAreFilters()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 50, 2.0);

        var orderBy = new OrderByField(-1);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40, orderBy);

        // PK is always the primary scan; all predicates become filter evaluators
        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(1));
        Assert.That(plan.Descending, Is.False);
    }

    [Test]
    public void PlanBuilder_OrderByDescending_SetsFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 50, 2.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex, descending: true);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 10, orderBy);

        Assert.That(plan.Descending, Is.True);
    }

    [Test]
    public void PlanBuilder_TieBreaking_LowerFieldIndexFirst()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Single entity: both fields have the same estimated cardinality
        CreateEntity(dbe, 1.0f, 1, 1.0);

        var ct = dbe.GetComponentTable<CompD>();
        var aFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["A"]);
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A == 1.0f && p.B == 1);

        // With equal selectivity, the lower FieldIndex should come first
        var expectedFirst = System.Math.Min(aFieldIndex, bFieldIndex);
        Assert.That(plan.OrderedEvaluators[0].FieldIndex, Is.EqualTo(expectedFirst));
    }

    #endregion

    #region PipelineExecutor Tests

    [Test]
    public void Execute_SinglePredicate_FiltersCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateEntity(dbe, 1.0f, 30, 2.0);
        var pk3 = CreateEntity(dbe, 1.0f, 60, 2.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40);
        var result = ExecutePlan(dbe, plan);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(pk1));
        Assert.That(result, Does.Not.Contain(pk2));
        Assert.That(result, Does.Contain(pk3));
    }

    [Test]
    public void Execute_MultiPredicate_MatchesBruteForce()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create 20 entities with varying B and A values
        var pks = new long[20];
        for (var i = 0; i < 20; i++)
        {
            pks[i] = CreateEntity(dbe, i * 0.5f, i * 5, i * 1.0);
        }

        // B > 50 && A > 3.0f => Intersection: i=11..19 => 9 entities
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50 && p.A > 3.0f);
        var result = ExecutePlan(dbe, plan);

        // Brute-force verification
        var expected = new HashSet<long>();
        for (var i = 0; i < 20; i++)
        {
            if (i * 5 > 50 && i * 0.5f > 3.0f)
            {
                expected.Add(pks[i]);
            }
        }

        Assert.That(result, Is.EquivalentTo(expected));
    }

    [Test]
    public void Execute_EmptyResult_NoMatches()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 10, 2.0);
        CreateEntity(dbe, 2.0f, 20, 3.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 100);
        var result = ExecutePlan(dbe, plan);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Execute_AllMatch_EveryEntityPasses()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateEntity(dbe, 2.0f, 60, 3.0);
        var pk3 = CreateEntity(dbe, 3.0f, 70, 4.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0);
        var result = ExecutePlan(dbe, plan);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Does.Contain(pk1));
        Assert.That(result, Does.Contain(pk2));
        Assert.That(result, Does.Contain(pk3));
    }

    [Test]
    public void ExecuteOrdered_Descending_ReverseOrder()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 10, 2.0);
        var pk2 = CreateEntity(dbe, 2.0f, 20, 3.0);
        var pk3 = CreateEntity(dbe, 3.0f, 30, 4.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex, descending: true);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(pk3)); // PK order descending
        Assert.That(result[1], Is.EqualTo(pk2));
        Assert.That(result[2], Is.EqualTo(pk1));
    }

    [Test]
    public void ExecuteOrdered_Ascending_NaturalOrder()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 10, 2.0);
        var pk2 = CreateEntity(dbe, 2.0f, 20, 3.0);
        var pk3 = CreateEntity(dbe, 3.0f, 30, 4.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(pk1)); // PK order ascending
        Assert.That(result[1], Is.EqualTo(pk2));
        Assert.That(result[2], Is.EqualTo(pk3));
    }

    [Test]
    public void ExecuteOrdered_SkipTake_CorrectWindow()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pks = new long[10];
        for (var i = 0; i < 10; i++)
        {
            pks[i] = CreateEntity(dbe, 1.0f, (i + 1) * 10, 2.0);
        }

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);

        // Skip 3, Take 4
        var result = ExecutePlanOrdered(dbe, plan, skip: 3, take: 4);

        Assert.That(result, Has.Count.EqualTo(4));
        Assert.That(result[0], Is.EqualTo(pks[3]));
        Assert.That(result[1], Is.EqualTo(pks[4]));
        Assert.That(result[2], Is.EqualTo(pks[5]));
        Assert.That(result[3], Is.EqualTo(pks[6]));
    }

    [Test]
    public void ExecuteOrdered_SkipExceedsCount_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 50, 2.0);
        CreateEntity(dbe, 2.0f, 60, 3.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan, skip: 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ExecuteOrdered_TakeZero_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 50, 2.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan, take: 0);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Execute_OrderByPK_FiltersCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateEntity(dbe, 2.0f, 30, 3.0);
        var pk3 = CreateEntity(dbe, 3.0f, 60, 4.0);

        var orderBy = new OrderByField(-1); // PK ordering
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40, orderBy);

        var result = ExecutePlanOrdered(dbe, plan);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(pk1));
        Assert.That(result, Does.Contain(pk3));
    }

    [Test]
    public void Execute_EqualityPredicate_MatchesExactValue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 42, 2.0);
        var pk2 = CreateEntity(dbe, 2.0f, 99, 3.0);
        var pk3 = CreateEntity(dbe, 3.0f, 77, 4.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B == 42);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(pk1));
    }

    [Test]
    public void ExecutionPlan_ToString_DiagnosticOutput()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        CreateEntity(dbe, 1.0f, 50, 2.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40);
        var str = plan.ToString();

        // Plan may use PK scan or secondary Index scan depending on available indexes
        Assert.That(str, Does.Contain("scan"));
        Assert.That(str, Does.Contain("Field["));
    }

    #endregion

    #region Secondary Index Scan Tests

    [Test]
    public void PlanBuilder_UniqueIndex_SelectsSecondaryStream()
    {
        // B has [Index] (unique) — should be selected as primary stream
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        for (var i = 0; i < 10; i++)
        {
            CreateEntity(dbe, i * 1.0f, i * 10, i * 2.0);
        }

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "Plan should use secondary index for unique field B");
        Assert.That(plan.PrimaryFieldIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(plan.ToString(), Does.Contain("Index scan"));
    }

    [Test]
    public void PlanBuilder_AllowMultipleIndex_FallsBackToPKScan()
    {
        // A has [Index(AllowMultiple = true)] — should NOT be selected as primary stream
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        for (var i = 0; i < 10; i++)
        {
            CreateEntity(dbe, i * 1.0f, i * 10, i * 2.0);
        }

        // Only predicate is on A (AllowMultiple) — must fall back to PK scan
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A > 5.0f);

        Assert.That(plan.UsesSecondaryIndex, Is.False, "AllowMultiple index should not be used as primary stream");
        Assert.That(plan.PrimaryFieldIndex, Is.EqualTo(-1));
    }

    [Test]
    public void IndexScan_EqualityPointQuery_SingleMatch()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pk1 = CreateEntity(dbe, 1.0f, 42, 1.0);
        CreateEntity(dbe, 2.0f, 99, 2.0);
        CreateEntity(dbe, 3.0f, 77, 3.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B == 42);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "EQ on unique index should use index scan");

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(pk1));
    }

    [Test]
    public void IndexScan_EqualityPointQuery_NoMatch()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        CreateEntity(dbe, 1.0f, 10, 1.0);
        CreateEntity(dbe, 2.0f, 20, 2.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B == 999);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void IndexScan_GreaterThan_ExcludesBoundary()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entities with B = 10, 20, 30, 40, 50
        var pks = new long[5];
        for (var i = 0; i < 5; i++)
        {
            pks[i] = CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0);
        }

        // B > 30: should match B=40 and B=50 only (NOT B=30)
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 30);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(pks[3])); // B=40
        Assert.That(result, Does.Contain(pks[4])); // B=50
        Assert.That(result, Does.Not.Contain(pks[2])); // B=30 excluded
    }

    [Test]
    public void IndexScan_GreaterThanOrEqual_IncludesBoundary()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pks = new long[5];
        for (var i = 0; i < 5; i++)
        {
            pks[i] = CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0);
        }

        // B >= 30: should match B=30, B=40, B=50
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B >= 30);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Does.Contain(pks[2])); // B=30 included
        Assert.That(result, Does.Contain(pks[3])); // B=40
        Assert.That(result, Does.Contain(pks[4])); // B=50
    }

    [Test]
    public void IndexScan_LessThan_ExcludesBoundary()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pks = new long[5];
        for (var i = 0; i < 5; i++)
        {
            pks[i] = CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0);
        }

        // B < 30: should match B=10 and B=20 only (NOT B=30)
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B < 30);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(pks[0])); // B=10
        Assert.That(result, Does.Contain(pks[1])); // B=20
        Assert.That(result, Does.Not.Contain(pks[2])); // B=30 excluded
    }

    [Test]
    public void IndexScan_LessThanOrEqual_IncludesBoundary()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pks = new long[5];
        for (var i = 0; i < 5; i++)
        {
            pks[i] = CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0);
        }

        // B <= 30: should match B=10, B=20, B=30
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B <= 30);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Does.Contain(pks[0])); // B=10
        Assert.That(result, Does.Contain(pks[1])); // B=20
        Assert.That(result, Does.Contain(pks[2])); // B=30 included
    }

    [Test]
    public void IndexScan_MultiPredicate_IndexOnPrimaryAndFilterOnSecondary()
    {
        // B (unique index) used as primary stream, A (AllowMultiple) evaluated as filter
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pks = new long[20];
        for (var i = 0; i < 20; i++)
        {
            pks[i] = CreateEntity(dbe, i * 0.5f, i * 5, i * 1.0);
        }

        // B > 50 && A > 3.0f → B narrows to i=11..19 (B=55..95), A > 3.0 further filters to i >= 7
        // Intersection: i=11..19 → 9 entities
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50 && p.A > 3.0f);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "B (unique) should be primary stream");

        var result = ExecutePlan(dbe, plan);

        // Brute-force verification
        var expected = new HashSet<long>();
        for (var i = 0; i < 20; i++)
        {
            if (i * 5 > 50 && i * 0.5f > 3.0f)
            {
                expected.Add(pks[i]);
            }
        }

        Assert.That(result, Is.EquivalentTo(expected));
    }

    [Test]
    public void IndexScan_LargeDataset_CorrectResults()
    {
        // Verify correctness at scale — wrong scan range would produce wrong results
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pks = new long[200];
        for (var i = 0; i < 200; i++)
        {
            pks[i] = CreateEntity(dbe, i * 0.1f, i, i * 0.5);
        }

        // B >= 150: should match exactly 50 entities (i=150..199)
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B >= 150);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
        Assert.That(result, Has.Count.EqualTo(50));

        for (var i = 150; i < 200; i++)
        {
            Assert.That(result, Does.Contain(pks[i]));
        }

        for (var i = 0; i < 150; i++)
        {
            Assert.That(result, Does.Not.Contain(pks[i]));
        }
    }

    [Test]
    public void IndexScan_OrderByPK_FallsBackToPKScan()
    {
        // When OrderBy is by PK, secondary index should NOT be used
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        for (var i = 0; i < 10; i++)
        {
            CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0);
        }

        var orderBy = new OrderByField(-1); // PK ordering
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50, orderBy);

        Assert.That(plan.UsesSecondaryIndex, Is.False, "OrderBy PK should force PK scan");
    }

    #endregion
}
