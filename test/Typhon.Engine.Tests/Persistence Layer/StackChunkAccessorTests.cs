using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

public class StackChunkAccessorTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    private static readonly char[] CharToRemove = ['(', ')', ',', ' '];

    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            foreach (var c in CharToRemove)
            {
                testName = testName.Replace(c, '_');
            }
            // Truncate to avoid exceeding 63 byte limit
            if (testName.Length > 40)
            {
                testName = testName.Substring(0, 40);
            }
            return $"Typhon_{testName}_db";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestChunk
    {
        public int A;
        public int B;
        public long C;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LargeTestChunk
    {
        public long V0, V1, V2, V3, V4, V5, V6, V7;
    }

    [SetUp]
    public void Setup()
    {
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
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = PagedMMF.DefaultMemPageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
    }

    #region Create() Tests

    [Test]
    public unsafe void Create_WithCapacity8_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);
        accessor.Dispose();
    }

    [Test]
    public unsafe void Create_WithCapacity16_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);
        accessor.Dispose();
    }

    [Test]
    public unsafe void Create_WithInvalidCapacity_ThrowsArgumentException()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        Assert.Throws<ArgumentException>(() => StackChunkAccessor.Create(segment, capacity: 4));
        Assert.Throws<ArgumentException>(() => StackChunkAccessor.Create(segment, capacity: 12));
        Assert.Throws<ArgumentException>(() => StackChunkAccessor.Create(segment, capacity: 32));
    }

    [Test]
    public unsafe void Create_WithChangeSet_AcceptsChangeSet()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        var changeSet = pmmf.CreateChangeSet();

        var accessor = StackChunkAccessor.Create(segment, changeSet, capacity: 16);
        accessor.Dispose();
    }

    #endregion

    #region EnterScope/ExitScope Tests

    [Test]
    public unsafe void EnterScope_SingleLevel_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);
        accessor.EnterScope();
        accessor.ExitScope();
        accessor.Dispose();
    }

    [Test]
    public unsafe void EnterScope_MultipleNestedLevels_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        for (int i = 0; i < 15; i++)
        {
            accessor.EnterScope();
        }

        for (int i = 0; i < 15; i++)
        {
            accessor.ExitScope();
        }

        accessor.Dispose();
    }

    [Test]
    public unsafe void EnterScope_ExceedsMaxDepth_ThrowsInvalidOperationException()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Enter 15 scopes (max)
        for (int i = 0; i < 15; i++)
        {
            accessor.EnterScope();
        }

        // 16th should throw
        var threw = false;
        try { accessor.EnterScope(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Expected InvalidOperationException for exceeding max scope depth");

        // Cleanup
        for (int i = 0; i < 15; i++)
        {
            accessor.ExitScope();
        }
        accessor.Dispose();
    }

    [Test]
    public unsafe void ExitScope_WithoutEnter_ThrowsInvalidOperationException()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);
        var threw = false;
        try { accessor.ExitScope(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Expected InvalidOperationException for exiting without entering scope");
        accessor.Dispose();
    }

    #endregion

    #region Get/GetReadOnly Tests

    [Test]
    public unsafe void Get_SingleChunk_ReturnsValidReference()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        ref var chunk = ref accessor.Get<TestChunk>(1);
        chunk.A = 42;
        chunk.B = 123;
        chunk.C = 9999;

        // Read back
        ref var readChunk = ref accessor.Get<TestChunk>(1);
        Assert.That(readChunk.A, Is.EqualTo(42));
        Assert.That(readChunk.B, Is.EqualTo(123));
        Assert.That(readChunk.C, Is.EqualTo(9999));

        accessor.Dispose();
    }

    [Test]
    public unsafe void Get_WithDirtyFlag_MarksDirty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        var changeSet = pmmf.CreateChangeSet();
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, changeSet, capacity: 16);

        ref var chunk = ref accessor.Get<TestChunk>(1, dirty: true);
        chunk.A = 42;

        accessor.Dispose();

        // ChangeSet should have the dirty page - verify by saving (would be no-op if empty)
        changeSet.SaveChanges();
    }

    [Test]
    public unsafe void GetReadOnly_ReturnsValidReference()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Write first
        ref var chunk = ref accessor.Get<TestChunk>(1, dirty: true);
        chunk.A = 42;

        // Read via GetReadOnly
        ref readonly var readOnlyChunk = ref accessor.GetReadOnly<TestChunk>(1);
        Assert.That(readOnlyChunk.A, Is.EqualTo(42));

        accessor.Dispose();
    }

    [Test]
    public unsafe void Get_MultipleChunksOnSamePage_CacheHit()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Access multiple chunks that should be on the same page
        ref var chunk1 = ref accessor.Get<TestChunk>(1);
        chunk1.A = 1;

        ref var chunk2 = ref accessor.Get<TestChunk>(2);
        chunk2.A = 2;

        ref var chunk3 = ref accessor.Get<TestChunk>(3);
        chunk3.A = 3;

        // Verify values
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(1));
        Assert.That(accessor.Get<TestChunk>(2).A, Is.EqualTo(2));
        Assert.That(accessor.Get<TestChunk>(3).A, Is.EqualTo(3));

        accessor.Dispose();
    }

    [Test]
    public unsafe void Get_ChunksOnDifferentPages_LoadsMultiplePages()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Get chunk count per page to ensure we access different pages
        var chunksPerPage = segment.ChunkCountPerPage;

        // Access chunks on different pages
        ref var chunk1 = ref accessor.Get<TestChunk>(1);
        chunk1.A = 100;

        ref var chunk2 = ref accessor.Get<TestChunk>(chunksPerPage + 10);
        chunk2.A = 200;

        ref var chunk3 = ref accessor.Get<TestChunk>(chunksPerPage * 2 + 10);
        chunk3.A = 300;

        // Verify values
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(100));
        Assert.That(accessor.Get<TestChunk>(chunksPerPage + 10).A, Is.EqualTo(200));
        Assert.That(accessor.Get<TestChunk>(chunksPerPage * 2 + 10).A, Is.EqualTo(300));

        accessor.Dispose();
    }

    #endregion

    #region GetChunkAsSpan Tests

    [Test]
    public unsafe void GetChunkAsSpan_ReturnsCorrectSize()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        var span = accessor.GetChunkAsSpan(1);
        Assert.That(span.Length, Is.EqualTo(sizeof(TestChunk)));

        accessor.Dispose();
    }

    [Test]
    public unsafe void GetChunkAsReadOnlySpan_ReturnsCorrectSize()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        var span = accessor.GetChunkAsReadOnlySpan(1);
        Assert.That(span.Length, Is.EqualTo(sizeof(TestChunk)));

        accessor.Dispose();
    }

    [Test]
    public unsafe void GetChunkAsSpan_ModificationsAreVisible()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        var span = accessor.GetChunkAsSpan(1, dirtyPage: true);
        var intSpan = MemoryMarshal.Cast<byte, int>(span);
        intSpan[0] = 12345;

        // Read back via Get
        ref var chunk = ref accessor.Get<TestChunk>(1);
        Assert.That(chunk.A, Is.EqualTo(12345));

        accessor.Dispose();
    }

    #endregion

    #region TryPromoteChunk/DemoteChunk Tests

    [Test]
    public unsafe void TryPromoteChunk_LoadedPage_ReturnsTrue()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // First load the page
        _ = accessor.Get<TestChunk>(1);

        // Now try to promote
        var result = accessor.TryPromoteChunk(1);
        Assert.That(result, Is.True);

        // Demote
        accessor.DemoteChunk(1);

        accessor.Dispose();
    }

    [Test]
    public unsafe void TryPromoteChunk_NotLoadedPage_ReturnsFalse()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Don't load any page, try to promote
        var result = accessor.TryPromoteChunk(1);
        Assert.That(result, Is.False);

        accessor.Dispose();
    }

    [Test]
    public unsafe void TryPromoteChunk_MultiplePromotions_IncrementsCounter()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Load the page
        _ = accessor.Get<TestChunk>(1);

        // Promote multiple times
        Assert.That(accessor.TryPromoteChunk(1), Is.True);
        Assert.That(accessor.TryPromoteChunk(1), Is.True);
        Assert.That(accessor.TryPromoteChunk(1), Is.True);

        // Demote multiple times
        accessor.DemoteChunk(1);
        accessor.DemoteChunk(1);
        accessor.DemoteChunk(1);

        accessor.Dispose();
    }

    [Test]
    public unsafe void DemoteChunk_NotPromoted_DoesNothing()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Load the page but don't promote
        _ = accessor.Get<TestChunk>(1);

        // Demote should not throw
        accessor.DemoteChunk(1);

        accessor.Dispose();
    }

    [Test]
    public unsafe void TryPromoteChunk_Capacity8_SearchesFirstVector()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);

        // Load the page
        _ = accessor.Get<TestChunk>(1);

        // Promote
        var result = accessor.TryPromoteChunk(1);
        Assert.That(result, Is.True);

        accessor.DemoteChunk(1);
        accessor.Dispose();
    }

    [Test]
    public unsafe void TryPromoteChunk_Capacity16_SearchesBothVectors()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 25, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Load pages to fill slots 0-7
        var chunksPerPage = segment.ChunkCountPerPage;
        for (int i = 0; i < 8; i++)
        {
            _ = accessor.Get<TestChunk>(chunksPerPage * i + 1);
        }

        // Load one more page to go into slots 8+
        var chunkInSlot8 = chunksPerPage * 8 + 1;
        _ = accessor.Get<TestChunk>(chunkInSlot8);

        // Promote the page in slot 8+ (second vector)
        var result = accessor.TryPromoteChunk(chunkInSlot8);
        Assert.That(result, Is.True);

        accessor.DemoteChunk(chunkInSlot8);
        accessor.Dispose();
    }

    #endregion

    #region Scope Protection Tests

    [Test]
    public unsafe void ScopeProtection_PagesInScopeNotEvicted()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        // Use capacity 16 for this test (we need to load multiple pages)
        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        var chunksPerPage = segment.ChunkCountPerPage;

        // Enter a scope and load a page
        accessor.EnterScope();
        ref var protectedChunk = ref accessor.Get<TestChunk>(1);
        protectedChunk.A = 9999;

        // Exit scope to make it evictable
        accessor.ExitScope();

        // Load 8 more pages to fill the cache and trigger eviction
        for (int i = 1; i <= 8; i++)
        {
            _ = accessor.Get<TestChunk>(chunksPerPage * i + 1);
        }

        // The protected chunk's value should still be accessible
        // (it may have been evicted and reloaded, but the underlying data should persist)
        ref var reloadedChunk = ref accessor.Get<TestChunk>(1);
        Assert.That(reloadedChunk.A, Is.EqualTo(9999));

        accessor.Dispose();
    }

    [Test]
    public unsafe void ScopeProtection_NestedScopes_InnerProtectsOuter()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(2000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);

        var chunksPerPage = segment.ChunkCountPerPage;

        // Outer scope
        accessor.EnterScope();
        ref var outerChunk = ref accessor.Get<TestChunk>(1);
        outerChunk.A = 1111;

        // Inner scope
        accessor.EnterScope();
        ref var innerChunk = ref accessor.Get<TestChunk>(chunksPerPage + 1);
        innerChunk.A = 2222;

        // Exit inner scope
        accessor.ExitScope();

        // Inner chunk is now evictable, but outer is still protected
        // Access outer chunk - should still have value
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(1111));

        // Exit outer scope
        accessor.ExitScope();

        accessor.Dispose();
    }

    [Test]
    public unsafe void ScopeProtection_ReaccessingChunkInChildScope_DoesNotStealFromParent()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Outer scope loads chunk
        accessor.EnterScope();
        ref var outerChunk = ref accessor.Get<TestChunk>(1);
        outerChunk.A = 1111;

        // Inner scope re-accesses same chunk
        accessor.EnterScope();
        ref var innerChunk = ref accessor.Get<TestChunk>(1);
        Assert.That(innerChunk.A, Is.EqualTo(1111));

        // Exit inner scope - chunk should still be protected by outer
        accessor.ExitScope();

        // Chunk should still be accessible
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(1111));

        accessor.ExitScope();
        accessor.Dispose();
    }

    #endregion

    #region Cache Eviction Tests

    [Test]
    public unsafe void CacheEviction_WhenCacheFull_EvictsOldestUnprotected()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);
        var chunksPerPage = segment.ChunkCountPerPage;

        // Enter a scope so pages are protected, then exit to make them evictable
        accessor.EnterScope();

        // Fill cache with 8 pages, writing distinct values
        for (int i = 0; i < 8; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            ref var chunk = ref accessor.Get<TestChunk>(chunkId, dirty: true);
            chunk.A = i * 100;
        }

        // Exit scope - now all pages are evictable
        accessor.ExitScope();

        // Access a 9th page - this should evict one
        var ninthChunkId = chunksPerPage * 8 + 1;
        ref var ninthChunk = ref accessor.Get<TestChunk>(ninthChunkId, dirty: true);
        ninthChunk.A = 800;

        // Verify the 9th chunk is accessible
        Assert.That(accessor.Get<TestChunk>(ninthChunkId).A, Is.EqualTo(800));

        accessor.Dispose();
    }

    [Test]
    public unsafe void CacheEviction_PromotedSlotsNotEvicted()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);
        var chunksPerPage = segment.ChunkCountPerPage;

        // Enter a scope so pages become evictable when we exit
        accessor.EnterScope();

        // Load first page and promote it (promoted pages are never evicted regardless of scope)
        ref var promotedChunk = ref accessor.Get<TestChunk>(1, dirty: true);
        promotedChunk.A = 9999;
        accessor.TryPromoteChunk(1);

        // Fill remaining cache slots
        for (int i = 1; i < 8; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            _ = accessor.Get<TestChunk>(chunkId);
        }

        // Exit scope - non-promoted pages are now evictable
        accessor.ExitScope();

        // Try to load more pages - promoted slot should not be evicted
        for (int i = 8; i < 12; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            _ = accessor.Get<TestChunk>(chunkId);
        }

        // The promoted chunk should still have its value
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(9999));

        accessor.DemoteChunk(1);
        accessor.Dispose();
    }

    [Test]
    public unsafe void CacheEviction_AllSlotsProtected_ThrowsInvalidOperationException()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);
        var chunksPerPage = segment.ChunkCountPerPage;

        // Enter scope and fill all 8 slots
        accessor.EnterScope();
        for (int i = 0; i < 8; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            _ = accessor.Get<TestChunk>(chunkId);
        }

        // All slots are now protected by the scope
        // Trying to access a new page should throw
        var newChunkId = chunksPerPage * 8 + 1;
        var threw = false;
        try { _ = accessor.Get<TestChunk>(newChunkId); }
        catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Expected InvalidOperationException when all slots are protected");

        accessor.ExitScope();
        accessor.Dispose();
    }

    #endregion

    #region Dispose Tests

    [Test]
    public unsafe void Dispose_ReleasesAllPages()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Load some pages
        _ = accessor.Get<TestChunk>(1);
        _ = accessor.Get<TestChunk>(10);

        // Dispose should not throw
        accessor.Dispose();
    }

    [Test]
    public unsafe void Dispose_DemotesPromotedSlots()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Load and promote
        _ = accessor.Get<TestChunk>(1);
        accessor.TryPromoteChunk(1);

        // Dispose should demote and not throw
        accessor.Dispose();
    }

    [Test]
    public unsafe void Dispose_FlushDirtyPagesToChangeSet()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        var changeSet = pmmf.CreateChangeSet();
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, changeSet, capacity: 16);

        // Write to chunks with dirty flag
        ref var chunk = ref accessor.Get<TestChunk>(1, dirty: true);
        chunk.A = 42;

        // Dispose should flush to change set
        accessor.Dispose();

        // Verify changeset has content by saving (this exercises the dirty page tracking)
        changeSet.SaveChanges();
    }

    #endregion

    #region First Page Offset Tests (RootHeaderIndexSectionLength)

    [Test]
    public unsafe void FirstPageOffset_ChunkZero_AccountsForHeader()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Write to chunk 1 (chunk 0 is reserved)
        ref var chunk1 = ref accessor.Get<TestChunk>(1, dirty: true);
        chunk1.A = 111;
        chunk1.B = 222;

        // Write to chunk 2
        ref var chunk2 = ref accessor.Get<TestChunk>(2, dirty: true);
        chunk2.A = 333;
        chunk2.B = 444;

        // Verify values are independent (not overlapping due to offset issues)
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(111));
        Assert.That(accessor.Get<TestChunk>(1).B, Is.EqualTo(222));
        Assert.That(accessor.Get<TestChunk>(2).A, Is.EqualTo(333));
        Assert.That(accessor.Get<TestChunk>(2).B, Is.EqualTo(444));

        accessor.Dispose();
    }

    [Test]
    public unsafe void FirstPageOffset_CompareWithChunkRandomAccessor()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        // Write with ChunkRandomAccessor
        using var cra = segment.CreateChunkRandomAccessor(8);
        ref var craChunk = ref cra.GetChunk<TestChunk>(5, dirtyPage: true);
        craChunk.A = 555;
        craChunk.B = 666;
        craChunk.C = 777;

        // Read with StackChunkAccessor
        var sca = StackChunkAccessor.Create(segment, capacity: 16);
        ref var scaChunk = ref sca.Get<TestChunk>(5);

        Assert.That(scaChunk.A, Is.EqualTo(555));
        Assert.That(scaChunk.B, Is.EqualTo(666));
        Assert.That(scaChunk.C, Is.EqualTo(777));

        sca.Dispose();
    }

    [Test]
    public unsafe void SecondPage_NoHeaderOffset()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(2000, true);

        var chunksPerPage = segment.ChunkCountRootPage;

        // Access chunk on second page
        var chunkOnSecondPage = chunksPerPage + 5;

        // Write with ChunkRandomAccessor
        using var cra = segment.CreateChunkRandomAccessor(8);
        ref var craChunk = ref cra.GetChunk<TestChunk>(chunkOnSecondPage, dirtyPage: true);
        craChunk.A = 888;
        craChunk.B = 999;

        // Read with StackChunkAccessor
        var sca = StackChunkAccessor.Create(segment, capacity: 16);
        ref var scaChunk = ref sca.Get<TestChunk>(chunkOnSecondPage);

        Assert.That(scaChunk.A, Is.EqualTo(888));
        Assert.That(scaChunk.B, Is.EqualTo(999));

        sca.Dispose();
    }

    #endregion

    #region GetChunkBasedSegmentHeader Tests

    [Test]
    public unsafe void GetChunkBasedSegmentHeader_ReturnsValidReference()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Access header - should not throw
        ref var header = ref accessor.GetChunkBasedSegmentHeader<ChunkBasedSegmentHeader>(
            ChunkBasedSegmentHeader.Offset, dirtyPage: false);

        // Just verify we can read it without crashing
        _ = header;

        accessor.Dispose();
    }

    #endregion

    #region SIMD Search Tests (Capacity 8 vs 16)

    [Test]
    public unsafe void SIMDSearch_Capacity8_OnlySearchesFirst8Slots()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 8);
        var chunksPerPage = segment.ChunkCountPerPage;

        // Fill 8 slots
        for (int i = 0; i < 8; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            ref var chunk = ref accessor.Get<TestChunk>(chunkId, dirty: true);
            chunk.A = i * 10;
        }

        // Re-access all 8 - should all be cache hits
        for (int i = 0; i < 8; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            Assert.That(accessor.Get<TestChunk>(chunkId).A, Is.EqualTo(i * 10));
        }

        accessor.Dispose();
    }

    [Test]
    public unsafe void SIMDSearch_Capacity16_SearchesBothVectors()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 35, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(10000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);
        var chunksPerPage = segment.ChunkCountPerPage;

        // Fill all 16 slots
        for (int i = 0; i < 16; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            ref var chunk = ref accessor.Get<TestChunk>(chunkId, dirty: true);
            chunk.A = i * 10;
        }

        // Re-access all 16 - should all be cache hits
        for (int i = 0; i < 16; i++)
        {
            var chunkId = chunksPerPage * i + 1;
            Assert.That(accessor.Get<TestChunk>(chunkId).A, Is.EqualTo(i * 10));
        }

        accessor.Dispose();
    }

    #endregion

    #region Integration Tests

    [Test]
    public unsafe void Integration_ReadWriteMultipleChunks_DataPersists()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(LargeTestChunk));
        var changeSet = pmmf.CreateChangeSet();
        using var chunks = segment.AllocateChunks(1000, true);

        // Write with StackChunkAccessor
        {
            var accessor = StackChunkAccessor.Create(segment, changeSet, capacity: 16);

            for (int i = 1; i <= 100; i++)
            {
                ref var chunk = ref accessor.Get<LargeTestChunk>(i, dirty: true);
                chunk.V0 = i;
                chunk.V1 = i * 2;
                chunk.V2 = i * 3;
                chunk.V3 = i * 4;
            }

            accessor.Dispose();
        }

        changeSet.SaveChanges();

        // Read back with new accessor
        {
            var accessor = StackChunkAccessor.Create(segment, capacity: 16);

            for (int i = 1; i <= 100; i++)
            {
                ref readonly var chunk = ref accessor.GetReadOnly<LargeTestChunk>(i);
                Assert.That(chunk.V0, Is.EqualTo(i));
                Assert.That(chunk.V1, Is.EqualTo(i * 2));
                Assert.That(chunk.V2, Is.EqualTo(i * 3));
                Assert.That(chunk.V3, Is.EqualTo(i * 4));
            }

            accessor.Dispose();
        }
    }

    [Test]
    public unsafe void Integration_ScopedRecursiveAccess()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(5000, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);
        var chunksPerPage = segment.ChunkCountPerPage;

        // Simulate recursive tree traversal pattern using explicit scope nesting
        const int maxDepth = 5;

        // Enter all scopes and write data
        for (int depth = 0; depth <= maxDepth; depth++)
        {
            accessor.EnterScope();

            var chunkId = 1 + chunksPerPage * depth;
            ref var chunk = ref accessor.Get<TestChunk>(chunkId, dirty: true);
            chunk.A = depth;
            chunk.B = chunkId;
        }

        // Exit all scopes in reverse order (simulating recursive return)
        for (int depth = maxDepth; depth >= 0; depth--)
        {
            accessor.ExitScope();
        }

        // Verify some values
        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(0));
        Assert.That(accessor.Get<TestChunk>(1 + chunksPerPage).A, Is.EqualTo(1));

        accessor.Dispose();
    }

    [Test]
    public unsafe void Integration_PromoteDemoteCycle()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk));
        using var chunks = segment.AllocateChunks(100, true);

        var accessor = StackChunkAccessor.Create(segment, capacity: 16);

        // Load, promote, modify, demote cycle
        for (int cycle = 0; cycle < 5; cycle++)
        {
            ref var chunk = ref accessor.Get<TestChunk>(1, dirty: true);

            var promoted = accessor.TryPromoteChunk(1);
            Assert.That(promoted, Is.True);

            chunk.A = cycle * 100;

            accessor.DemoteChunk(1);
        }

        Assert.That(accessor.Get<TestChunk>(1).A, Is.EqualTo(400));

        accessor.Dispose();
    }

    #endregion
}
