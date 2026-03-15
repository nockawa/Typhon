using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

unsafe class RawValueHashMapTests
{
    private IServiceProvider _serviceProvider;
    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name.Replace("(", "_").Replace(")", "_").Replace(",", "_")}_database";

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
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
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)(PagedMMF.DefaultMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    // ═══════════════════════════════════════════════════════════════════════
    // RecommendedStride
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RecommendedStride_SmallValue_256()
    {
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(22, 4);
        Assert.That(stride, Is.EqualTo(256));
    }

    [Test]
    public void RecommendedStride_LargeValue_512()
    {
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(78, 4);
        Assert.That(stride, Is.EqualTo(512));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Insert + TryGet
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void InsertAndGet_SingleEntry_RoundTrip()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2); // 22 bytes
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Build entity record
        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 2);
        EntityRecordAccessor.GetHeader(record).BornTSN = 42;
        EntityRecordAccessor.GetHeader(record).EnabledBits = 0b11;
        EntityRecordAccessor.SetLocation(record, 0, 100);
        EntityRecordAccessor.SetLocation(record, 1, 200);

        // Insert
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(1));

        // Read back
        byte* readBuf = stackalloc byte[valueSize];
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool found = map.TryGet(1L, readBuf, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True);
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(42));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).EnabledBits, Is.EqualTo(0b11));
        Assert.That(EntityRecordAccessor.GetLocation(readBuf, 0), Is.EqualTo(100));
        Assert.That(EntityRecordAccessor.GetLocation(readBuf, 1), Is.EqualTo(200));
    }

    [Test]
    public void InsertAndGet_MultipleEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Insert 10 entries
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 10; i++)
            {
                byte* record = stackalloc byte[valueSize];
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i * 10;
                EntityRecordAccessor.SetLocation(record, 0, i * 100);
                EntityRecordAccessor.SetLocation(record, 1, i * 200);
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(10));

        // Verify each entry
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 10; i++)
            {
                byte* readBuf = stackalloc byte[valueSize];
                bool found = map.TryGet(i, readBuf, ref accessor);
                Assert.That(found, Is.True, $"Entry {i} not found");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i * 10));
                Assert.That(EntityRecordAccessor.GetLocation(readBuf, 0), Is.EqualTo(i * 100));
            }
            accessor.Dispose();
        }
    }

    [Test]
    public void Insert_Duplicate_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.Insert(1L, record, ref accessor, null), Is.True);
            Assert.That(map.Insert(1L, record, ref accessor, null), Is.False);
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Upsert
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Upsert_UpdatesExisting()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 2);
        EntityRecordAccessor.GetHeader(record).BornTSN = 10;

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Upsert(1L, record, ref accessor, null);

            EntityRecordAccessor.GetHeader(record).DiedTSN = 50;
            map.Upsert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        byte* readBuf = stackalloc byte[valueSize];
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.TryGet(1L, readBuf, ref accessor);
            accessor.Dispose();
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(10));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).DiedTSN, Is.EqualTo(50));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Remove
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Remove_ExistingKey_ReturnsTrue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(1));

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool removed = map.Remove(1L, ref accessor, null);
            accessor.Dispose();
            Assert.That(removed, Is.True);
        }

        Assert.That(map.EntryCount, Is.EqualTo(0));
    }

    [Test]
    public void Remove_NonExistentKey_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool removed = map.Remove(999L, ref accessor, null);
            accessor.Dispose();
            Assert.That(removed, Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split behavior
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Insert_TriggersLinearHashSplit()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);
        int initialBucketCount = map.BucketCount;

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 100; i++)
            {
                byte* record = stackalloc byte[valueSize];
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i;
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(100));
        Assert.That(map.BucketCount, Is.GreaterThan(initialBucketCount));

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 100; i++)
            {
                byte* readBuf = stackalloc byte[valueSize];
                bool found = map.TryGet(i, readBuf, ref accessor);
                Assert.That(found, Is.True, $"Entry {i} not found after splits");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i));
            }
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Various value sizes
    // ═══════════════════════════════════════════════════════════════════════

    [TestCase(1, Description = "1 component = 18 bytes")]
    [TestCase(4, Description = "4 components = 30 bytes")]
    [TestCase(8, Description = "8 components = 46 bytes")]
    [TestCase(16, Description = "16 components = 78 bytes")]
    public void InsertGet_VariousComponentCounts(int componentCount)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(componentCount);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, componentCount);
        EntityRecordAccessor.GetHeader(record).BornTSN = 999;
        EntityRecordAccessor.GetHeader(record).EnabledBits = (ushort)((1 << componentCount) - 1);
        for (int s = 0; s < componentCount; s++)
        {
            EntityRecordAccessor.SetLocation(record, s, (s + 1) * 1000);
        }

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(42L, record, ref accessor, null);
            accessor.Dispose();
        }

        byte* readBuf = stackalloc byte[valueSize];
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool found = map.TryGet(42L, readBuf, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True);
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(999));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).EnabledBits, Is.EqualTo((1 << componentCount) - 1));
        for (int s = 0; s < componentCount; s++)
        {
            Assert.That(EntityRecordAccessor.GetLocation(readBuf, s), Is.EqualTo((s + 1) * 1000), $"Location[{s}]");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 48-bit TSN round-trip through HashMap
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TSN_48Bit_SurvivesHashMapRoundTrip()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValueHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValueHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        long largeTsn = (1L << 47) - 1;

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);
        EntityRecordAccessor.GetHeader(record).BornTSN = largeTsn;
        EntityRecordAccessor.GetHeader(record).DiedTSN = largeTsn - 1000;

        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        byte* readBuf = stackalloc byte[valueSize];
        using (var guard = EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.TryGet(1L, readBuf, ref accessor);
            accessor.Dispose();
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(largeTsn));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).DiedTSN, Is.EqualTo(largeTsn - 1000));
    }
}
