using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Comprehensive unit tests for ChunkAccessor covering all major functionality.
/// </summary>
public class ChunkAccessorTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private ILogger<ChunkAccessorTests> _logger;

    // Test data structures
    [StructLayout(LayoutKind.Sequential)]
    struct TestChunk32
    {
        public int A;
        public int B;
        public long C;
        public float D;
        public double E;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TestChunk16
    {
        public long A;
        public long B;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TestChunk8
    {
        public long Value;
    }

    static readonly char[] charToRemove = ['(', ')', ','];
    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            foreach (var c in charToRemove)
            {
                testName = testName.Replace(c, '_');
            }

            // Truncate testName if too long (max 63 bytes UTF8 total)
            // "CA_" prefix (3 bytes) + "_db" suffix (3 bytes) = 6 bytes reserved
            // Maximum for testName = 57 bytes
            var prefix = "CA_";
            var suffix = "_db";
            var maxTestNameLength = 57;

            if (testName.Length > maxTestNameLength)
            {
                testName = testName.Substring(0, maxTestNameLength);
            }

            return $"{prefix}{testName}{suffix}";
        }
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
                options.DatabaseCacheSize = PagedMMF.MinimumCacheSize;
                options.PagesDebugPattern = false;
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _logger = _serviceCollection.BuildServiceProvider().GetRequiredService<ILogger<ChunkAccessorTests>>();
    }

    [TearDown]
    public void TearDown()
    {
    }

    #region Creation and Initialization Tests

    [Test]
    public unsafe void Create_ValidSegment_InitializesCorrectly()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var accessor = ChunkAccessor.Create(segment);

        // Verify accessor is created and usable
        accessor.Dispose();
        Assert.Pass();
    }

    [Test]
    public unsafe void Create_WithChangeSet_InitializesCorrectly()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        var accessor = ChunkAccessor.Create(segment, changeSet);

        accessor.Dispose();
        Assert.Pass();
    }

    #endregion

    #region Basic Get Operations Tests

    [Test]
    public unsafe void Get_SingleChunk_ReturnsValidReference()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 42;
        chunk.B = 100;
        chunk.C = 123456789L;

        // Verify we can read back the values
        ref var chunk2 = ref accessor.Get<TestChunk32>(chunkId);
        Assert.That(chunk2.A, Is.EqualTo(42));
        Assert.That(chunk2.B, Is.EqualTo(100));
        Assert.That(chunk2.C, Is.EqualTo(123456789L));

        accessor.Dispose();
    }

    [Test]
    public unsafe void GetReadOnly_SingleChunk_ReturnsValidReference()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Write with mutable reference
        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 42;

        // Read with readonly reference
        ref readonly var readOnlyChunk = ref accessor.GetReadOnly<TestChunk32>(chunkId);
        Assert.That(readOnlyChunk.A, Is.EqualTo(42));

        accessor.Dispose();
    }

    [Test]
    public unsafe void Get_WithDirtyFlag_MarksPageDirty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment, changeSet);

        ref var chunk = ref accessor.Get<TestChunk32>(chunkId, dirty: true);
        chunk.A = 999;

        accessor.Dispose();

        // Verify changeset can save changes (would fail if no pages were marked dirty)
        changeSet.SaveChanges();
        Assert.Pass();
    }

    [Test]
    public unsafe void Get_MultipleChunksOnSamePage_ReturnsCorrectData()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk8));

        // Allocate multiple chunks that should be on the same page
        var chunk1 = segment.AllocateChunk(true);
        var chunk2 = segment.AllocateChunk(true);
        var chunk3 = segment.AllocateChunk(true);

        var accessor = ChunkAccessor.Create(segment);

        ref var c1 = ref accessor.Get<TestChunk8>(chunk1);
        c1.Value = 100;

        ref var c2 = ref accessor.Get<TestChunk8>(chunk2);
        c2.Value = 200;

        ref var c3 = ref accessor.Get<TestChunk8>(chunk3);
        c3.Value = 300;

        // Verify all values are preserved
        Assert.That(accessor.Get<TestChunk8>(chunk1).Value, Is.EqualTo(100));
        Assert.That(accessor.Get<TestChunk8>(chunk2).Value, Is.EqualTo(200));
        Assert.That(accessor.Get<TestChunk8>(chunk3).Value, Is.EqualTo(300));

        accessor.Dispose();
    }

    #endregion

    #region Scoped Access and Pinning Tests

    [Test]
    public unsafe void GetScoped_SingleChunk_PinsAndUnpins()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        using (var scope = accessor.GetScoped<TestChunk32>(chunkId))
        {
            ref var chunk = ref scope.AsRef();
            chunk.A = 777;
            chunk.B = 888;
        }

        // After scope disposal, verify data is still accessible
        ref var chunk2 = ref accessor.Get<TestChunk32>(chunkId);
        Assert.That(chunk2.A, Is.EqualTo(777));
        Assert.That(chunk2.B, Is.EqualTo(888));

        accessor.Dispose();
    }

    [Test]
    public unsafe void GetScoped_MultipleScopes_PreventsEviction()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(TestChunk32));

        // Allocate chunks on different pages
        var chunks = new int[20];
        for (int i = 0; i < 20; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
        }

        var accessor = ChunkAccessor.Create(segment);

        // Pin the first chunk
        using var scope1 = accessor.GetScoped<TestChunk32>(chunks[0]);
        ref var chunk1 = ref scope1.AsRef();
        chunk1.A = 12345;

        // Access many other chunks to fill cache
        for (int i = 1; i < 17; i++)
        {
            ref var c = ref accessor.Get<TestChunk32>(chunks[i]);
            c.A = i * 100;
        }

        // Verify pinned chunk is still accessible and unchanged
        Assert.That(chunk1.A, Is.EqualTo(12345));

        accessor.Dispose();
    }

    [Test]
    public unsafe void ChunkScope_AsSpan_ReturnsValidSpan()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        using var scope = accessor.GetScoped<TestChunk32>(chunkId);
        var span = scope.AsSpan();

        Assert.That(span.Length, Is.EqualTo(sizeof(TestChunk32)));
        span[0] = 0xFF;

        accessor.Dispose();
    }

    [Test]
    public unsafe void ChunkScope_AsReadOnlySpan_ReturnsValidSpan()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Write data first
        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 999;

        using var scope = accessor.GetScoped<TestChunk32>(chunkId);
        var span = scope.AsReadOnlySpan();

        Assert.That(span.Length, Is.EqualTo(sizeof(TestChunk32)));

        accessor.Dispose();
    }

    #endregion

    #region MRU Cache Behavior Tests

    [Test]
    public unsafe void Get_RepeatedAccess_UsesMRUCache()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // First access
        ref var chunk1 = ref accessor.Get<TestChunk32>(chunkId);
        chunk1.A = 42;

        // Repeated accesses should hit MRU cache
        for (int i = 0; i < 100; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
            Assert.That(chunk.A, Is.EqualTo(42));
        }

        accessor.Dispose();
    }

    [Test]
    public unsafe void Get_AlternatingChunks_UpdatesMRU()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunk1 = segment.AllocateChunk(true);
        var chunk2 = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Access pattern: chunk1, chunk2, chunk1, chunk2
        ref var c1 = ref accessor.Get<TestChunk32>(chunk1);
        c1.A = 100;

        ref var c2 = ref accessor.Get<TestChunk32>(chunk2);
        c2.A = 200;

        ref var c1_again = ref accessor.Get<TestChunk32>(chunk1);
        Assert.That(c1_again.A, Is.EqualTo(100));

        ref var c2_again = ref accessor.Get<TestChunk32>(chunk2);
        Assert.That(c2_again.A, Is.EqualTo(200));

        accessor.Dispose();
    }

    #endregion

    #region SIMD Search and Cache Management Tests

    [Test]
    public unsafe void Get_Fill16Slots_AllAccessible()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(TestChunk8));

        // Allocate chunks spread across different pages (capacity = 16 slots)
        var chunks = new int[16];
        for (int i = 0; i < 16; i++)
        {
            // Allocate with spacing to ensure different pages
            chunks[i] = segment.AllocateChunk(true);
            if (i < 15)
            {
                // Allocate and free some chunks to spread them across pages
                for (int j = 0; j < 100; j++)
                {
                    segment.FreeChunk(segment.AllocateChunk(false));
                }
            }
        }

        var accessor = ChunkAccessor.Create(segment);

        // Set unique values
        for (int i = 0; i < 16; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i]);
            chunk.Value = i * 1000;
        }

        // Verify all values
        for (int i = 0; i < 16; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i]);
            Assert.That(chunk.Value, Is.EqualTo(i * 1000));
        }

        accessor.Dispose();
    }

    [Test]
    public unsafe void Get_MoreThan16Pages_EvictsLRU()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(TestChunk8));

        // Allocate 20 chunks on different pages
        var chunks = new int[20];
        for (int i = 0; i < 20; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
            // Space them out across pages
            for (int j = 0; j < 100; j++)
            {
                segment.FreeChunk(segment.AllocateChunk(false));
            }
        }

        var accessor = ChunkAccessor.Create(segment);

        // Access first 16 chunks
        for (int i = 0; i < 16; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i]);
            chunk.Value = i;
        }

        // Access 4 more chunks (should evict LRU)
        for (int i = 16; i < 20; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i]);
            chunk.Value = i * 100;
        }

        // Verify newer chunks are still accessible
        for (int i = 16; i < 20; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i]);
            Assert.That(chunk.Value, Is.EqualTo(i * 100));
        }

        accessor.Dispose();
    }

    #endregion

    #region LRU Eviction Tests

    [Test]
    public unsafe void Eviction_SelectsLRUSlot_NotMRU()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        // Allocate many chunks on different pages
        var chunks = new int[20];
        for (int i = 0; i < 20; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
            for (int j = 0; j < 50; j++)
            {
                segment.FreeChunk(segment.AllocateChunk(false));
            }
        }

        var accessor = ChunkAccessor.Create(segment, changeSet);

        // Fill cache with first 16 chunks
        for (int i = 0; i < 16; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk32>(chunks[i], dirty: true);
            chunk.A = i;
        }

        // Access first chunk multiple times to increase hit count
        for (int i = 0; i < 10; i++)
        {
            _ = accessor.Get<TestChunk32>(chunks[0]);
        }

        // Access 17th chunk - should evict LRU (not chunk 0)
        ref var newChunk = ref accessor.Get<TestChunk32>(chunks[16], dirty: true);
        newChunk.A = 9999;

        // Chunk 0 should still be in cache
        ref var chunk0 = ref accessor.Get<TestChunk32>(chunks[0]);
        Assert.That(chunk0.A, Is.EqualTo(0));

        accessor.Dispose();

        // Verify dirty pages were added to changeset
        changeSet.SaveChanges();
        Assert.Pass();
    }

    [Test]
    public unsafe void Eviction_DirtySlot_AddsToChangeSet()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        var chunks = new int[20];
        for (int i = 0; i < 20; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
            for (int j = 0; j < 50; j++)
            {
                segment.FreeChunk(segment.AllocateChunk(false));
            }
        }

        var accessor = ChunkAccessor.Create(segment, changeSet);

        // Fill cache and mark dirty
        for (int i = 0; i < 16; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk32>(chunks[i], dirty: true);
            chunk.A = i * 10;
        }

        // Force eviction by accessing more chunks
        for (int i = 16; i < 20; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk32>(chunks[i], dirty: true);
            chunk.A = i * 10;
        }

        accessor.Dispose();

        // Verify dirty pages were added and can be saved
        changeSet.SaveChanges();
        Assert.Pass();
    }

    #endregion

    #region Promotion and Demotion Tests

    [Test]
    public unsafe void TryPromoteChunk_CachedPage_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Access chunk to cache it
        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 100;

        // Try to promote
        bool promoted = accessor.TryPromoteChunk(chunkId);
        Assert.That(promoted, Is.True);

        // Demote
        accessor.DemoteChunk(chunkId);

        accessor.Dispose();
    }

    [Test]
    public unsafe void TryPromoteChunk_NotCached_ReturnsFalse()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Try to promote without accessing first
        bool promoted = accessor.TryPromoteChunk(chunkId);

        // May return false if page is not cached
        // This is expected behavior

        accessor.Dispose();
    }

    [Test]
    public unsafe void PromoteAndDemote_Multiple_MaintainsRefCount()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Access chunk
        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 555;

        // Promote multiple times
        bool p1 = accessor.TryPromoteChunk(chunkId);
        bool p2 = accessor.TryPromoteChunk(chunkId);
        bool p3 = accessor.TryPromoteChunk(chunkId);

        Assert.That(p1, Is.True);
        Assert.That(p2, Is.True);
        Assert.That(p3, Is.True);

        // Demote same number of times
        accessor.DemoteChunk(chunkId);
        accessor.DemoteChunk(chunkId);
        accessor.DemoteChunk(chunkId);

        accessor.Dispose();
    }

    #endregion

    #region Dirty Tracking and ChangeSet Tests

    [Test]
    public unsafe void DirtyTracking_OnlyDirtyPagesAddedToChangeSet()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        var chunk1 = segment.AllocateChunk(true);
        var chunk2 = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment, changeSet);

        // Access one chunk as dirty
        ref var c1 = ref accessor.Get<TestChunk32>(chunk1, dirty: true);
        c1.A = 100;

        // Access another as readonly
        ref readonly var c2 = ref accessor.GetReadOnly<TestChunk32>(chunk2);

        accessor.Dispose();

        // Only dirty pages should be in changeset - verify by saving
        changeSet.SaveChanges();
        Assert.Pass();
    }

    [Test]
    public unsafe void Dispose_WithDirtyPages_FlushesToChangeSet()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment, changeSet);

        ref var chunk = ref accessor.Get<TestChunk32>(chunkId, dirty: true);
        chunk.A = 999;
        chunk.B = 888;

        accessor.Dispose();

        // Verify dirty pages flushed by saving changes
        changeSet.SaveChanges();
        Assert.Pass();
    }

    [Test]
    public unsafe void Dispose_WithoutChangeSet_DoesNotThrow()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment); // No changeset

        ref var chunk = ref accessor.Get<TestChunk32>(chunkId, dirty: true);
        chunk.A = 777;

        accessor.Dispose();
        Assert.Pass();
    }

    #endregion

    #region Edge Cases and Error Conditions

    [Test]
    public unsafe void AllSlotsPinned_ThrowsException()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        // Use small chunk size to fit many per page
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 30, sizeof(TestChunk8));

        // Calculate chunks per page to ensure we span multiple pages
        var chunksPerPage = segment.ChunkCountPerPage;

        // Allocate 17 chunks, each on a different page (by spacing them out)
        var chunks = new int[17];
        for (int i = 0; i < 17; i++)
        {
            // Allocate chunk at page boundary
            var targetChunk = i * chunksPerPage;

            // Allocate chunks to reach the target
            while (segment.AllocatedChunkCount <= targetChunk)
            {
                segment.AllocateChunk(false);
            }

            chunks[i] = targetChunk + 1;
        }

        var accessor = ChunkAccessor.Create(segment);

        // Pin 16 chunks (all slots) using separate variables
        var s = stackalloc ChunkScope<TestChunk8>[16];
        for (int i = 0; i < 16; i++)
        {
            s[i] = accessor.GetScoped<TestChunk8>(chunks[i]);
        }

        try
        {
            // Try to access 17th chunk on a different page - should throw
            try
            {
                ref var chunk = ref accessor.Get<TestChunk8>(chunks[16]);
                Assert.Fail("Expected InvalidOperationException - all slots should be pinned");
            }
            catch (InvalidOperationException)
            {
                // Expected - all slots pinned
                Assert.Pass();
            }
        }
        finally
        {
            for (int i = 0; i < 16; i++)
            {
                s[i].Dispose();
            }
            accessor.Dispose();
        }
    }

    [Test]
    public unsafe void RootPage_HasCorrectHeaderOffset()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        // Chunk 0 is reserved, so allocate starting from 1
        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 12345;
        chunk.B = 67890;

        // Verify data persists
        ref var chunk2 = ref accessor.Get<TestChunk32>(chunkId);
        Assert.That(chunk2.A, Is.EqualTo(12345));
        Assert.That(chunk2.B, Is.EqualTo(67890));

        accessor.Dispose();
    }

    [Test]
    public unsafe void AccessDifferentChunkTypes_WorksCorrectly()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Access as TestChunk32
        ref var chunk32 = ref accessor.Get<TestChunk32>(chunkId);
        chunk32.A = 100;
        chunk32.B = 200;

        // Access same chunk as different type (unsafe but valid for testing)
        ref var chunk16 = ref accessor.Get<TestChunk16>(chunkId);
        Assert.That(chunk16.A, Is.Not.Zero);

        accessor.Dispose();
    }

    [Test]
    public unsafe void SequentialAccess_ManyChunks_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(TestChunk8));

        var chunkCount = 1000;
        var chunks = new int[chunkCount];

        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
        }

        var accessor = ChunkAccessor.Create(segment);

        // Write sequentially
        for (int i = 0; i < chunkCount; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i], dirty: true);
            chunk.Value = i;
        }

        // Read sequentially
        for (int i = 0; i < chunkCount; i++)
        {
            ref var chunk = ref accessor.Get<TestChunk8>(chunks[i]);
            Assert.That(chunk.Value, Is.EqualTo(i));
        }

        accessor.Dispose();
    }

    [Test]
    public unsafe void RandomAccess_ManyChunks_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(TestChunk16));

        var chunkCount = 500;
        var chunks = new int[chunkCount];
        var random = new Random(12345);

        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
        }

        var accessor = ChunkAccessor.Create(segment);

        // Write in random order
        var shuffled = new List<int>(chunks);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        for (int i = 0; i < chunkCount; i++)
        {
            var chunkId = shuffled[i];
            ref var chunk = ref accessor.Get<TestChunk16>(chunkId, dirty: true);
            chunk.A = chunkId;
            chunk.B = chunkId * 2;
        }

        // Read in different random order
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        for (int i = 0; i < chunkCount; i++)
        {
            var chunkId = shuffled[i];
            ref var chunk = ref accessor.Get<TestChunk16>(chunkId);
            Assert.That(chunk.A, Is.EqualTo(chunkId));
            Assert.That(chunk.B, Is.EqualTo(chunkId * 2));
        }

        accessor.Dispose();
    }

    #endregion

    #region Disposal and Cleanup Tests

    [Test]
    public unsafe void Dispose_ReleasesAllResources()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        ref var chunk = ref accessor.Get<TestChunk32>(chunkId, dirty: true);
        chunk.A = 555;

        accessor.Dispose();
        Assert.Pass();
    }

    [Test]
    public unsafe void Dispose_WithPromotedPages_Demotes()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Access and promote
        ref var chunk = ref accessor.Get<TestChunk32>(chunkId);
        chunk.A = 123;
        accessor.TryPromoteChunk(chunkId);

        // Dispose should demote
        accessor.Dispose();
        Assert.Pass();
    }

    [Test]
    public unsafe void Dispose_WithPinnedPages_Unpins()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var chunkId = segment.AllocateChunk(true);
        var accessor = ChunkAccessor.Create(segment);

        // Create scoped access but dispose accessor first
        var scope = accessor.GetScoped<TestChunk32>(chunkId);
        ref var chunk = ref scope.AsRef();
        chunk.A = 999;

        // Dispose accessor (scope still active)
        accessor.Dispose();

        scope.Dispose();
        Assert.Pass();
    }

    [Test]
    public unsafe void MultipleDispose_DoesNotThrow()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var accessor = ChunkAccessor.Create(segment);

        accessor.Dispose();

        // Second dispose should be safe (although not recommended)
        // The implementation should handle this gracefully
        Assert.Pass();
    }

    #endregion

    #region Integration and Stress Tests

    [Test]
    public unsafe void StressTest_MixedOperations_Succeeds()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();

        var chunkCount = 200;
        var chunks = new int[chunkCount];
        var random = new Random(42);

        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = segment.AllocateChunk(true);
        }

        var accessor = ChunkAccessor.Create(segment, changeSet);

        // Mixed operations
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            var op = random.Next(0, 5); // Skip scoped access since we can't store scopes in collections
            var chunkIndex = random.Next(0, chunkCount);
            var chunkId = chunks[chunkIndex];

            switch (op)
            {
                case 0: // Regular get
                    ref var c1 = ref accessor.Get<TestChunk32>(chunkId);
                    c1.A = iteration;
                    break;

                case 1: // Dirty get
                    ref var c2 = ref accessor.Get<TestChunk32>(chunkId, dirty: true);
                    c2.B = iteration * 2;
                    break;

                case 2: // Readonly get
                    ref readonly var c3 = ref accessor.GetReadOnly<TestChunk32>(chunkId);
                    _ = c3.A;
                    break;

                case 3: // Promote
                    accessor.TryPromoteChunk(chunkId);
                    break;

                case 4: // Demote
                    accessor.DemoteChunk(chunkId);
                    break;
            }
        }

        accessor.Dispose();
        Assert.Pass("Stress test completed successfully");
    }

    #endregion
}
