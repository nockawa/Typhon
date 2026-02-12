using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

[TestFixture]
class EpochPageCacheTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_db";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! : PagedMMF.DefaultMemPageCount;
        dcs *= PagedMMF.PageSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = true;
                options.OverrideDatabaseCacheMinSize = true;
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => Log.CloseAndFlush();

    // ========================================
    // Epoch-based eviction protection
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void EpochTaggedPage_NotEvictedWhileScopeActive()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        using var epochManager = new EpochManager("test-epoch", null);
        pmmf.SetEpochManager(epochManager);

        // Step 1: Load pages 0-3 via legacy RequestPage, then dispose (→ Idle, no epoch tag)
        for (int i = 0; i < 4; i++)
        {
            pmmf.RequestPage(i, false, out var accessor);
            accessor.Dispose();
        }

        // Step 2: Enter epoch scope and load pages 4-7 via RequestPageEpoch (→ Idle + epoch-tagged)
        var guard = EpochGuard.Enter(epochManager);
        var currentEpoch = epochManager.GlobalEpoch;

        for (int i = 4; i < 8; i++)
        {
            Assert.That(pmmf.RequestPageEpoch(i, currentEpoch, out _), Is.True, $"RequestPageEpoch should succeed for page {i}");
        }

        // Step 3: Request 4 more pages (8-11) via legacy — should evict pages 0-3 (not 4-7)
        var newAccessors = new PageAccessor[4];
        for (int i = 0; i < 4; i++)
        {
            Assert.That(pmmf.RequestPage(i + 8, false, out newAccessors[i]), Is.True, $"RequestPage should succeed for page {i + 8}");
        }

        // Step 4: Verify epoch-tagged pages 4-7 are still in cache (cache hit, not evicted)
        var metrics = pmmf.GetMetrics();
        var cacheMissBefore = metrics.MemPageCacheMiss;
        for (int i = 4; i < 8; i++)
        {
            Assert.That(pmmf.RequestPageEpoch(i, currentEpoch, out _), Is.True, $"Epoch-tagged page {i} should still be in cache");
        }
        Assert.That(metrics.MemPageCacheMiss, Is.EqualTo(cacheMissBefore), "Epoch-tagged pages should be cache hits (not evicted)");

        // Cleanup
        for (int i = 0; i < 4; i++)
        {
            newAccessors[i].Dispose();
        }
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void EpochTaggedPage_EvictedAfterScopeExit()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        using var epochManager = new EpochManager("test-epoch", null);
        pmmf.SetEpochManager(epochManager);

        // Step 1: Enter epoch scope and fill all 8 cache slots via RequestPageEpoch
        long currentEpoch;
        {
            var guard = EpochGuard.Enter(epochManager);
            currentEpoch = epochManager.GlobalEpoch;

            for (int i = 0; i < 8; i++)
            {
                Assert.That(pmmf.RequestPageEpoch(i, currentEpoch, out _), Is.True);
            }

            guard.Dispose();
        }

        // Step 2: After scope exit, epoch advanced — pages are now evictable
        // MinActiveEpoch = GlobalEpoch (no active scopes), which is > the AccessEpoch of all tagged pages
        Assert.That(epochManager.GlobalEpoch, Is.GreaterThan(currentEpoch), "Epoch should have advanced after scope exit");

        // Step 3: Request new pages — should be able to evict old epoch-tagged pages
        var metrics = pmmf.GetMetrics();
        var cacheMissBefore = metrics.MemPageCacheMiss;
        for (int i = 0; i < 4; i++)
        {
            pmmf.RequestPage(i + 100, false, out var accessor);
            accessor.Dispose();
        }

        // Verify eviction occurred (cache misses for the new pages)
        Assert.That(metrics.MemPageCacheMiss, Is.EqualTo(cacheMissBefore + 4), "New pages should cause cache misses (old pages evicted)");
    }

    // ========================================
    // Legacy backward compatibility
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void DualMode_LegacyRefCount_StillWorks()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        using var epochManager = new EpochManager("test-epoch", null);
        pmmf.SetEpochManager(epochManager);

        // Legacy RequestPage should work normally even with epoch manager wired in
        Assert.That(pmmf.RequestPage(0, false, out var readAccessor), Is.True);

        // Page should be in Shared state
        Assert.That(pmmf.GetPageState(readAccessor.MemPageIndex), Is.EqualTo(PagedMMF.PageState.Shared));

        // AccessEpoch should be 0 (not set by legacy path)
        Assert.That(pmmf.GetPageAccessEpoch(readAccessor.MemPageIndex), Is.EqualTo(0));

        readAccessor.Dispose();

        // After release, page should be Idle
        Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(8));
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void DualMode_MixedAccess_BothProtected()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        using var epochManager = new EpochManager("test-epoch", null);
        pmmf.SetEpochManager(epochManager);

        // Load page 0 via legacy RequestPage (Shared) — hold the accessor
        Assert.That(pmmf.RequestPage(0, false, out var sharedAccessor), Is.True);

        // Load page 1 via RequestPageEpoch (epoch-tagged)
        var guard = EpochGuard.Enter(epochManager);
        var currentEpoch = epochManager.GlobalEpoch;
        Assert.That(pmmf.RequestPageEpoch(1, currentEpoch, out var epochMemPage), Is.True);

        // Fill remaining 6 slots with disposable pages (legacy)
        for (int i = 2; i < 8; i++)
        {
            pmmf.RequestPage(i, false, out var accessor);
            accessor.Dispose();
        }

        // Request 6 new pages — should evict the 6 disposable pages, NOT page 0 (Shared) or page 1 (epoch)
        for (int i = 0; i < 6; i++)
        {
            Assert.That(pmmf.RequestPage(i + 100, false, out var newAccessor), Is.True, $"Should evict disposable pages for page {i + 100}");
            newAccessor.Dispose();
        }

        // Verify page 0 is still Shared (held by accessor)
        Assert.That(pmmf.GetPageState(sharedAccessor.MemPageIndex), Is.EqualTo(PagedMMF.PageState.Shared));

        // Verify page 1 is still in cache (epoch-tagged, not evicted)
        var metrics = pmmf.GetMetrics();
        var cacheMissBefore = metrics.MemPageCacheMiss;
        Assert.That(pmmf.RequestPageEpoch(1, currentEpoch, out var recheckMemPage), Is.True);
        Assert.That(metrics.MemPageCacheMiss, Is.EqualTo(cacheMissBefore), "Epoch-tagged page 1 should still be cached");
        Assert.That(recheckMemPage, Is.EqualTo(epochMemPage), "Page 1 should map to the same memory page");

        // Cleanup
        sharedAccessor.Dispose();
        guard.Dispose();
    }

    // ========================================
    // Epoch tagging verification
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void RequestPageEpoch_TagsCorrectEpoch()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        using var epochManager = new EpochManager("test-epoch", null);
        pmmf.SetEpochManager(epochManager);

        var guard = EpochGuard.Enter(epochManager);
        var currentEpoch = epochManager.GlobalEpoch;

        Assert.That(pmmf.RequestPageEpoch(0, currentEpoch, out var memPageIndex), Is.True);

        // Verify the page's AccessEpoch matches the epoch we passed in
        Assert.That(pmmf.GetPageAccessEpoch(memPageIndex), Is.EqualTo(currentEpoch));

        // The page should be in Idle state (epoch-mode doesn't hold Shared/Exclusive)
        Assert.That(pmmf.GetPageState(memPageIndex), Is.EqualTo(PagedMMF.PageState.Idle));

        guard.Dispose();
    }

    // ========================================
    // ChangeSet epoch-mode support
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void AddByMemPageIndex_MarksDirty()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

        // Use legacy RequestPage to get a page in Shared state (required by IncrementDirty assert)
        Assert.That(pmmf.RequestPage(0, true, out var accessor), Is.True);
        var memPageIndex = accessor.MemPageIndex;

        var cs = pmmf.CreateChangeSet();

        // Mark dirty using the new AddByMemPageIndex method
        cs.AddByMemPageIndex(memPageIndex);

        // Release the accessor — page should go to IdleAndDirty (dirty counter > 0)
        accessor.Dispose();

        // The page should be IdleAndDirty since we incremented the dirty counter
        Assert.That(pmmf.GetPageState(memPageIndex), Is.EqualTo(PagedMMF.PageState.IdleAndDirty));

        // Reset should decrement dirty and transition back to Idle
        cs.Reset();
        Assert.That(pmmf.GetPageState(memPageIndex), Is.EqualTo(PagedMMF.PageState.Idle));
    }
}
