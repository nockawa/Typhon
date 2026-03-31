using NUnit.Framework;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

[TestFixture]
public class ViewSubscriptionTests
{
    private static CachedEntity MakeEntity(long id, params (ushort compId, byte[] data)[] components)
    {
        var snapshots = new ComponentSnapshot[components.Length];
        for (var i = 0; i < components.Length; i++)
        {
            snapshots[i] = new ComponentSnapshot { ComponentId = components[i].compId, Data = components[i].data };
        }

        return new CachedEntity(id, snapshots);
    }

    [Test]
    public void AddEntity_AppearsInCache_FiresCallback()
    {
        var sub = new ViewSubscription("test_view");
        CachedEntity received = null;
        sub.OnEntityAdded += e => received = e;

        var entity = MakeEntity(42, (1, [10, 20, 30]));
        sub.AddEntity(entity);

        Assert.That(sub.Entities.Count, Is.EqualTo(1));
        Assert.That(sub.Entities.ContainsKey(42), Is.True);
        Assert.That(received, Is.Not.Null);
        Assert.That(received.Id, Is.EqualTo(42));
    }

    [Test]
    public void RemoveEntity_RemovedFromCache_FiresCallback()
    {
        var sub = new ViewSubscription("test_view");
        sub.AddEntity(MakeEntity(42, (1, [10])));

        long removedId = 0;
        sub.OnEntityRemoved += id => removedId = id;

        sub.RemoveEntity(42);

        Assert.That(sub.Entities.Count, Is.EqualTo(0));
        Assert.That(removedId, Is.EqualTo(42));
    }

    [Test]
    public void ModifyEntity_UpdatesCache_FiresCallback()
    {
        var sub = new ViewSubscription("test_view");
        sub.AddEntity(MakeEntity(42, (1, [10, 20, 30])));

        ComponentFieldUpdate[] receivedUpdates = null;
        sub.OnEntityModified += (_, updates) => receivedUpdates = updates;

        var updates = new[]
        {
            new ComponentFieldUpdate { ComponentId = 1, FieldDirtyBits = ~0UL, FieldValues = [99, 88, 77] }
        };
        sub.ModifyEntity(42, updates);

        Assert.That(receivedUpdates, Is.Not.Null);
        Assert.That(sub.Entities[42].Components[0].Data, Is.EqualTo(new byte[] { 99, 88, 77 }));
    }

    [Test]
    public void ModifyEntity_UnknownEntity_SkipsSilently()
    {
        var sub = new ViewSubscription("test_view");
        var fired = false;
        sub.OnEntityModified += (_, _) => fired = true;

        sub.ModifyEntity(999, [new ComponentFieldUpdate { ComponentId = 1, FieldDirtyBits = ~0UL, FieldValues = [1] }]);

        Assert.That(fired, Is.False);
    }

    [Test]
    public void Clear_EmptiesCache()
    {
        var sub = new ViewSubscription("test_view");
        sub.AddEntity(MakeEntity(1, (1, [10])));
        sub.AddEntity(MakeEntity(2, (1, [20])));

        sub.Clear();

        Assert.That(sub.Entities.Count, Is.EqualTo(0));
    }

    [Test]
    public void FireSyncComplete_SetsSyncedAndFiresCallback()
    {
        var sub = new ViewSubscription("test_view");
        var fired = false;
        sub.OnSyncComplete += () => fired = true;

        Assert.That(sub.IsSynced, Is.False);
        sub.FireSyncComplete();

        Assert.That(sub.IsSynced, Is.True);
        Assert.That(fired, Is.True);
    }

    [Test]
    public void FireResync_ClearsCacheAndResetsSync()
    {
        var sub = new ViewSubscription("test_view");
        sub.AddEntity(MakeEntity(1, (1, [10])));
        sub.FireSyncComplete();

        var resyncFired = false;
        sub.OnResync += () => resyncFired = true;

        sub.FireResync();

        Assert.That(sub.IsSynced, Is.False);
        Assert.That(sub.Entities.Count, Is.EqualTo(0));
        Assert.That(resyncFired, Is.True);
    }

    [Test]
    public void MultipleEntities_IndependentCallbacks()
    {
        var sub = new ViewSubscription("test_view");
        var addedIds = new List<long>();
        sub.OnEntityAdded += e => addedIds.Add(e.Id);

        sub.AddEntity(MakeEntity(1, (1, [10])));
        sub.AddEntity(MakeEntity(2, (1, [20])));
        sub.AddEntity(MakeEntity(3, (1, [30])));

        Assert.That(addedIds, Is.EqualTo(new long[] { 1, 2, 3 }));
        Assert.That(sub.Entities.Count, Is.EqualTo(3));
    }
}
