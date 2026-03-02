using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[TestFixture]
class ViewRegistryTests
{
    private class MockView : IView
    {
        public int ViewId { get; set; }
        public int[] FieldDependencies { get; set; }
        public bool IsDisposed { get; set; }
    }

    [Test]
    public void EmptyRegistry_AllFieldsReturnEmptySpan()
    {
        var registry = new ViewRegistry(4);

        for (var i = 0; i < 4; i++)
        {
            Assert.That(registry.GetViewsForField(i).Length, Is.EqualTo(0));
        }
    }

    [Test]
    public void RegisterSingleView_SingleFieldDependency_ReturnedByGetViewsForField()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView { ViewId = 1, FieldDependencies = [2] };

        registry.RegisterView(view);

        var views = registry.GetViewsForField(2);
        Assert.That(views.Length, Is.EqualTo(1));
        Assert.That(views[0], Is.SameAs(view));
    }

    [Test]
    public void RegisterSingleView_MultipleFieldDependencies_ReturnedForEachField()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView { ViewId = 1, FieldDependencies = [0, 2, 3] };

        registry.RegisterView(view);

        Assert.That(registry.GetViewsForField(0).Length, Is.EqualTo(1));
        Assert.That(registry.GetViewsForField(0)[0], Is.SameAs(view));

        Assert.That(registry.GetViewsForField(1).Length, Is.EqualTo(0), "Field 1 not in dependencies");

        Assert.That(registry.GetViewsForField(2).Length, Is.EqualTo(1));
        Assert.That(registry.GetViewsForField(2)[0], Is.SameAs(view));

        Assert.That(registry.GetViewsForField(3).Length, Is.EqualTo(1));
        Assert.That(registry.GetViewsForField(3)[0], Is.SameAs(view));
    }

    [Test]
    public void RegisterMultipleViews_SameField_AllReturned()
    {
        var registry = new ViewRegistry(4);
        var viewA = new MockView { ViewId = 1, FieldDependencies = [1] };
        var viewB = new MockView { ViewId = 2, FieldDependencies = [1] };
        var viewC = new MockView { ViewId = 3, FieldDependencies = [1] };

        registry.RegisterView(viewA);
        registry.RegisterView(viewB);
        registry.RegisterView(viewC);

        var views = registry.GetViewsForField(1);
        Assert.That(views.Length, Is.EqualTo(3));
        Assert.That(views[0], Is.SameAs(viewA));
        Assert.That(views[1], Is.SameAs(viewB));
        Assert.That(views[2], Is.SameAs(viewC));
    }

    [Test]
    public void DeregisterView_Removed_OthersRemain()
    {
        var registry = new ViewRegistry(4);
        var viewA = new MockView { ViewId = 1, FieldDependencies = [0, 1] };
        var viewB = new MockView { ViewId = 2, FieldDependencies = [0, 1] };

        registry.RegisterView(viewA);
        registry.RegisterView(viewB);

        registry.DeregisterView(viewA);

        var field0 = registry.GetViewsForField(0);
        Assert.That(field0.Length, Is.EqualTo(1));
        Assert.That(field0[0], Is.SameAs(viewB));

        var field1 = registry.GetViewsForField(1);
        Assert.That(field1.Length, Is.EqualTo(1));
        Assert.That(field1[0], Is.SameAs(viewB));
    }

    [Test]
    public void DeregisterNonExistentView_NoCrash()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView { ViewId = 1, FieldDependencies = [0, 1] };

        // Deregister a view that was never registered
        Assert.DoesNotThrow(() => registry.DeregisterView(view));
    }

    [Test]
    public void RegisterIdempotent_SameViewTwice_NoDuplicate()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView { ViewId = 1, FieldDependencies = [0, 2] };

        registry.RegisterView(view);
        registry.RegisterView(view);

        var field0 = registry.GetViewsForField(0);
        Assert.That(field0.Length, Is.EqualTo(1));
        Assert.That(field0[0], Is.SameAs(view));

        var field2 = registry.GetViewsForField(2);
        Assert.That(field2.Length, Is.EqualTo(1));
        Assert.That(field2[0], Is.SameAs(view));
    }

    [Test]
    public void ViewCount_TracksCorrectly()
    {
        var registry = new ViewRegistry(4);
        Assert.That(registry.ViewCount, Is.EqualTo(0));

        var viewA = new MockView { ViewId = 1, FieldDependencies = [0] };
        var viewB = new MockView { ViewId = 2, FieldDependencies = [1] };

        registry.RegisterView(viewA);
        Assert.That(registry.ViewCount, Is.EqualTo(1));

        registry.RegisterView(viewB);
        Assert.That(registry.ViewCount, Is.EqualTo(2));

        registry.DeregisterView(viewA);
        Assert.That(registry.ViewCount, Is.EqualTo(1));

        registry.DeregisterView(viewB);
        Assert.That(registry.ViewCount, Is.EqualTo(0));
    }

    [Test]
    public void RegisterView_FieldDependencyOutOfRange_Throws()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView { ViewId = 1, FieldDependencies = [2, 5] };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => registry.RegisterView(view));
        Assert.That(ex.Message, Does.Contain("field dependency 5"));
        Assert.That(ex.Message, Does.Contain("4 fields"));
    }

    [Test]
    public void GetViewsForField_OutOfRange_ReturnsEmptySpan()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView { ViewId = 1, FieldDependencies = [0] };
        registry.RegisterView(view);

        Assert.That(registry.GetViewsForField(-1).Length, Is.EqualTo(0));
        Assert.That(registry.GetViewsForField(4).Length, Is.EqualTo(0));
        Assert.That(registry.GetViewsForField(int.MaxValue).Length, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(5000)]
    public void ConcurrentReadDuringWrite_NoTornReads()
    {
        var registry = new ViewRegistry(8);
        var running = 1;
        var errors = 0;

        // Writer thread: continuously registers and deregisters views
        var writer = Task.Run(() =>
        {
            var views = new MockView[20];
            for (var i = 0; i < views.Length; i++)
            {
                views[i] = new MockView { ViewId = i, FieldDependencies = [i % 8] };
            }

            while (Volatile.Read(ref running) == 1)
            {
                for (var i = 0; i < views.Length; i++)
                {
                    registry.RegisterView(views[i]);
                }
                for (var i = 0; i < views.Length; i++)
                {
                    registry.DeregisterView(views[i]);
                }
            }
        });

        // 4 reader threads: continuously read views for all fields
        var readers = new Task[4];
        for (var r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                while (Volatile.Read(ref running) == 1)
                {
                    for (var f = 0; f < 8; f++)
                    {
                        try
                        {
                            var span = registry.GetViewsForField(f);
                            // Access every element to detect torn reads
                            for (var i = 0; i < span.Length; i++)
                            {
                                var v = span[i];
                                if (v == null)
                                {
                                    Interlocked.Increment(ref errors);
                                }
                            }
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                }
            });
        }

        // Run for ~100ms
        Thread.Sleep(100);
        Volatile.Write(ref running, 0);

        Task.WaitAll([writer, ..readers]);

        Assert.That(errors, Is.EqualTo(0), "No torn reads or null entries should be observed");
    }
}