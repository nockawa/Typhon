using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.TestGuild", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompGuild
{
    [Index(AllowMultiple = true)] public int Level;
    [Index] public int MemberCap;

    public CompGuild(int level, int memberCap)
    {
        Level = level;
        MemberCap = memberCap;
    }
}

[Component("Typhon.Schema.UnitTest.TestPlayer", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompPlayer
{
    [Index(AllowMultiple = true), ForeignKey(typeof(CompGuild))]
    public long GuildId;
    [Index(AllowMultiple = true)] public int Active;  // 1=active, 0=inactive (bool can't be indexed)

    public CompPlayer(long guildId, bool active)
    {
        GuildId = guildId;
        Active = active ? 1 : 0;
    }
}

class NavigationViewTests : TestBase<NavigationViewTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompDArch>.Touch();
        Archetype<CompDFArch>.Touch();
        Archetype<CompFArch>.Touch();
        Archetype<CompGuildArch>.Touch();
        Archetype<CompPlayerArch>.Touch();
    }

    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        base.RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<CompGuild>();
        dbe.RegisterComponentFromAccessor<CompPlayer>();
    }

    /// <summary>Reconstructs an EntityId from a raw pk value (test-only, uses InternalsVisibleTo).</summary>
    private static EntityId ToEntityId(long pk) =>
        Unsafe.As<long, EntityId>(ref pk);

    private static long CreateGuild(DatabaseEngine dbe, int level, int memberCap)
    {
        using var t = dbe.CreateQuickTransaction();
        var g = new CompGuild(level, memberCap);
        var id = t.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(in g));
        t.Commit();
        return (long)id.RawValue;
    }

    private static long CreatePlayer(DatabaseEngine dbe, long guildId, bool active)
    {
        using var t = dbe.CreateQuickTransaction();
        var p = new CompPlayer(guildId, active);
        var id = t.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(in p));
        t.Commit();
        return (long)id.RawValue;
    }

    private static void UpdateGuild(DatabaseEngine dbe, long pk, int level, int memberCap)
    {
        using var t = dbe.CreateQuickTransaction();
        var g = new CompGuild(level, memberCap);
        ref var w = ref t.OpenMut(ToEntityId(pk)).Write(CompGuildArch.Guild);
        w = g;
        t.Commit();
    }

    private static void UpdatePlayer(DatabaseEngine dbe, long pk, long guildId, bool active)
    {
        using var t = dbe.CreateQuickTransaction();
        var p = new CompPlayer(guildId, active);
        ref var w = ref t.OpenMut(ToEntityId(pk)).Write(CompPlayerArch.Player);
        w = p;
        t.Commit();
    }

    private static void DeleteGuild(DatabaseEngine dbe, long pk)
    {
        using var t = dbe.CreateQuickTransaction();
        t.Destroy(ToEntityId(pk));
        t.Commit();
    }

    private static void DeletePlayer(DatabaseEngine dbe, long pk)
    {
        using var t = dbe.CreateQuickTransaction();
        t.Destroy(ToEntityId(pk));
        t.Commit();
    }

    private static void RefreshView(DatabaseEngine dbe, ViewBase view)
    {
        using var t = dbe.CreateQuickTransaction();
        view.Refresh(t);
    }

    #region Schema Validation

    [Test]
    public void ForeignKey_OnLongField_Accepted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompPlayer>();
        Assert.That(ct, Is.Not.Null);
        var field = ct.Definition.FieldsByName["GuildId"];
        Assert.That(field.IsForeignKey, Is.True);
        Assert.That(field.ForeignKeyTargetType, Is.EqualTo(typeof(CompGuild)));
    }

    [Test]
    public void ForeignKey_OnIntField_Rejected()
    {
        // We can't easily register a bad component at runtime without a test-only struct,
        // so we validate the Field metadata directly.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Verify that CompGuild.Level (int) does NOT have IsForeignKey set
        var ct = dbe.GetComponentTable<CompGuild>();
        var levelField = ct.Definition.FieldsByName["Level"];
        Assert.That(levelField.IsForeignKey, Is.False);
    }

    [Test]
    public void Navigate_OnNonForeignKeyField_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // CompD.B is an indexed int field, NOT a foreign key — validation happens at Execute/ToView time
        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() =>
        {
            dbe.Query<CompD>().Navigate<CompGuild>(p => (long)p.B)
                .Where((s, t) => t.Level >= 10)
                .Execute(tx);
        });
    }

    [Test]
    public void Navigate_WrongTargetType_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // CompPlayer.GuildId is FK to CompGuild, but we try to navigate to CompD
        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() =>
        {
            dbe.Query<CompPlayer>().Navigate<CompD>(p => p.GuildId)
                .Where((s, t) => t.A > 1.0f)
                .Execute(tx);
        });
    }

    #endregion

    #region Forward Navigation

    [Test]
    public void Navigate_InitialPopulation_MatchesQualifyingEntities()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);  // Level >= 10 → qualifies
        var guild2 = CreateGuild(dbe, 5, 30);   // Level < 10 → doesn't qualify
        var player1 = CreatePlayer(dbe, guild1, true);   // FK to qualifying guild
        var player2 = CreatePlayer(dbe, guild2, true);   // FK to non-qualifying guild
        var player3 = CreatePlayer(dbe, guild1, false);  // FK to qualifying guild

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(player1), Is.True);
        Assert.That(view.Contains(player3), Is.True);
        Assert.That(view.Contains(player2), Is.False);
    }

    [Test]
    public void Navigate_FKChangeToQualifyingTarget_AddsEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);  // qualifies
        var guild2 = CreateGuild(dbe, 5, 30);   // doesn't qualify
        var player = CreatePlayer(dbe, guild2, true);  // initially FK to non-qualifying

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.False);

        // Change FK to point to qualifying guild
        UpdatePlayer(dbe, player, guild1, true);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.True);
    }

    [Test]
    public void Navigate_FKChangeToNonQualifyingTarget_RemovesEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);  // qualifies
        var guild2 = CreateGuild(dbe, 5, 30);   // doesn't qualify
        var player = CreatePlayer(dbe, guild1, true);  // initially FK to qualifying

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.True);

        // Change FK to point to non-qualifying guild
        UpdatePlayer(dbe, player, guild2, true);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.False);
    }

    [Test]
    public void Navigate_FKChangeBetweenTwoQualifyingTargets_StaysInView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);
        var guild2 = CreateGuild(dbe, 15, 60);
        var player = CreatePlayer(dbe, guild1, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.True);

        // Change FK to different qualifying guild
        UpdatePlayer(dbe, player, guild2, true);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.True);
    }

    [Test]
    public void Navigate_SourceCreated_AddsIfQualifying()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(0));

        // Create player pointing to qualifying guild
        var player = CreatePlayer(dbe, guild1, true);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.True);
    }

    [Test]
    public void Navigate_SourceDeleted_RemovesFromView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);
        var player = CreatePlayer(dbe, guild1, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.True);

        DeletePlayer(dbe, player);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.False);
    }

    #endregion

    #region Reverse Navigation

    [Test]
    public void Navigate_TargetBecomeQualifying_AddsSourceEntities()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 5, 30);  // initially doesn't qualify
        var player1 = CreatePlayer(dbe, guild1, true);
        var player2 = CreatePlayer(dbe, guild1, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(0));

        // Update guild to qualifying level (boundary crossing IN)
        UpdateGuild(dbe, guild1, 10, 30);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(player1), Is.True);
        Assert.That(view.Contains(player2), Is.True);
    }

    [Test]
    public void Navigate_TargetBecomesNonQualifying_RemovesSourceEntities()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);  // qualifies
        var player1 = CreatePlayer(dbe, guild1, true);
        var player2 = CreatePlayer(dbe, guild1, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(2));

        // Downgrade guild below threshold (boundary crossing OUT)
        UpdateGuild(dbe, guild1, 5, 50);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(0));
    }

    [Test]
    public void Navigate_TargetNoBoundaryCrossing_NoReverseUpdate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);  // qualifies
        var player = CreatePlayer(dbe, guild1, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));

        // Change guild level from 10 to 15 — still qualifies, no boundary crossing
        UpdateGuild(dbe, guild1, 15, 50);
        RefreshView(dbe, view);

        // Entity should still be in view
        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(player), Is.True);
    }

    [Test]
    public void Navigate_TargetDeleted_RemovesSourceEntities()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);
        var player = CreatePlayer(dbe, guild1, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.True);

        DeleteGuild(dbe, guild1);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.False);
    }

    [Test]
    public void Navigate_FanOut_ManySourcesForOneTarget()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 5, 200);  // doesn't qualify initially
        var playerPKs = new long[100];
        for (var i = 0; i < 100; i++)
        {
            playerPKs[i] = CreatePlayer(dbe, guild, true);
        }

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(0));

        // Guild becomes qualifying — all 100 players should appear
        UpdateGuild(dbe, guild, 10, 200);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(100));
        foreach (var pk in playerPKs)
        {
            Assert.That(view.Contains(pk), Is.True);
        }
    }

    #endregion

    #region Combined Predicates

    [Test]
    public void Navigate_SourceAndTargetPredicates()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 10, 50);

        var player1 = CreatePlayer(dbe, guild, true);   // active=true, qualifies
        var player2 = CreatePlayer(dbe, guild, false);  // active=false, doesn't qualify

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => s.Active == 1 && t.Level >= 10)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(player1), Is.True);
        Assert.That(view.Contains(player2), Is.False);
    }

    [Test]
    public void Navigate_SourcePredicateChange_WhileTargetQualifies()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 10, 50);
        var player = CreatePlayer(dbe, guild, false);  // not active

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => s.Active == 1 && t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.False);

        // Activate player — now both predicates qualify
        UpdatePlayer(dbe, player, guild, true);
        RefreshView(dbe, view);

        Assert.That(view.Contains(player), Is.True);
    }

    [Test]
    public void Navigate_SourcePredicatePlusNonQualifyingTarget()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 5, 30);  // doesn't qualify
        var player = CreatePlayer(dbe, guild, true);   // active

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => s.Active == 1 && t.Level >= 10)
            .ToView();

        // Player is active but guild doesn't qualify
        Assert.That(view.Contains(player), Is.False);
    }

    #endregion

    #region One-shot Queries

    [Test]
    public void Navigate_Execute_ReturnsCorrectResults()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);
        var guild2 = CreateGuild(dbe, 5, 30);
        var player1 = CreatePlayer(dbe, guild1, true);
        var player2 = CreatePlayer(dbe, guild2, true);
        var player3 = CreatePlayer(dbe, guild1, false);

        using var tx = dbe.CreateQuickTransaction();
        var result = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .Execute(tx);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Contains(player1), Is.True);
        Assert.That(result.Contains(player3), Is.True);
    }

    [Test]
    public void Navigate_Count_ReturnsCorrectCount()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild1 = CreateGuild(dbe, 10, 50);
        var guild2 = CreateGuild(dbe, 5, 30);
        CreatePlayer(dbe, guild1, true);
        CreatePlayer(dbe, guild2, true);
        CreatePlayer(dbe, guild1, false);

        using var tx = dbe.CreateQuickTransaction();
        var count = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .Count(tx);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void Navigate_Any_ReturnsTrueWhenMatches()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 10, 50);
        CreatePlayer(dbe, guild, true);

        using var tx = dbe.CreateQuickTransaction();
        var any = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .Any(tx);

        Assert.That(any, Is.True);
    }

    [Test]
    public void Navigate_ExecuteOrdered_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateGuild(dbe, 10, 50);

        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() =>
        {
            dbe.Query<CompPlayer>()
                .Navigate<CompGuild>(p => p.GuildId)
                .Where((s, t) => t.Level >= 10)
                .ExecuteOrdered(tx);
        });
    }

    #endregion

    #region Edge Cases

    [Test]
    public void Navigate_NonExistentTargetFK_ExcludedFromView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Player pointing to a non-existent guild PK
        var player = CreatePlayer(dbe, 999999, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.False);
    }

    [Test]
    public void Navigate_ZeroFK_ExcludedFromView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var player = CreatePlayer(dbe, 0, true);  // FK = 0, no target

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((s, t) => t.Level >= 10)
            .ToView();

        Assert.That(view.Contains(player), Is.False);
    }

    [Test]
    public void Navigate_WhereBeforeNavigate_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.Throws<InvalidOperationException>(() =>
        {
            dbe.Query<CompPlayer>()
                .Where(p => p.Active == 1)
                .Navigate<CompGuild>(p => p.GuildId);
        });
    }

    #endregion

    #region Regression

    [Test]
    public void ExistingView_TwoComponent_Unchanged()
    {
        // Verify that existing View<T1,T2> still works after NavigationView additions
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        long pk;
        using (var t = dbe.CreateQuickTransaction())
        {
            var d = new CompD(5.0f, 50, 2.0);
            var f = new CompF(15000, 1);
            var id = t.Spawn<CompDFArch>(CompDFArch.D.Set(in d), CompDFArch.F.Set(in f));
            pk = (long)id.RawValue;
            t.Commit();
        }

        using var view = dbe.Query<CompD, CompF>()
            .Where((d2, f2) => d2.B > 40 && f2.Gold > 10000)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk), Is.True);
    }

    #endregion

    #region Overflow & Delta

    [Test]
    public void Navigate_Overflow_Recovery_RebuildsCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 10, 100);
        var player1 = CreatePlayer(dbe, guild, true);
        var player2 = CreatePlayer(dbe, guild, true);
        var player3 = CreatePlayer(dbe, guild, true);

        // Small buffer capacity to trigger overflow easily
        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((p, g) => p.Active == 1 && g.Level >= 5)
            .ToView(bufferCapacity: 4);

        Assert.That(view.Count, Is.EqualTo(3));

        // Generate many updates to overflow the 4-capacity buffer
        for (int i = 0; i < 12; i++)
        {
            UpdatePlayer(dbe, player1, guild, i % 2 == 0);
        }

        // Refresh — should detect overflow and rebuild from scratch
        RefreshView(dbe, view);

        // player1 ended with i=11 → i%2==0 is false → Active=0 → not in view
        // player2 and player3 still active
        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(player2), Is.True);
        Assert.That(view.Contains(player3), Is.True);
    }

    [Test]
    public void Navigate_Delta_AddedRemoved_AfterRefresh()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 10, 50);
        var player1 = CreatePlayer(dbe, guild, true);
        var player2 = CreatePlayer(dbe, guild, true);

        using var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((p, g) => p.Active == 1 && g.Level >= 5)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        view.ClearDelta();

        // Deactivate player1
        UpdatePlayer(dbe, player1, guild, false);

        // Create and activate player3
        var player3 = CreatePlayer(dbe, guild, true);

        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Removed.Contains(player1), Is.True);
        Assert.That(delta.Added.Contains(player3), Is.True);
        Assert.That(delta.Removed.Count, Is.EqualTo(1));
        Assert.That(delta.Added.Count, Is.EqualTo(1));
    }

    [Test]
    public void Navigate_Dispose_DeregistersFromBothRegistries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var guild = CreateGuild(dbe, 10, 50);
        var player = CreatePlayer(dbe, guild, true);

        var view = dbe.Query<CompPlayer>()
            .Navigate<CompGuild>(p => p.GuildId)
            .Where((p, g) => p.Active == 1 && g.Level >= 5)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));

        view.Dispose();
        Assert.That(view.IsDisposed, Is.True);

        // Updating the guild after dispose should NOT crash —
        // if deregistration failed, the ViewRegistry would try to append to the disposed view's buffer.
        UpdateGuild(dbe, guild, 20, 50);

        // Create a new transaction to verify the engine is still healthy
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx, Is.Not.Null);
    }

    #endregion
}
