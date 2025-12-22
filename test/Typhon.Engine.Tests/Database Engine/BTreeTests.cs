using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Typhon.Engine.BPTree;

namespace Typhon.Engine.Tests.Database_Engine;

class BtreeTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private ILogger<BtreeTests> _logger;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! : PagedMMF.DefaultMemPageCount;
        dcs *= PagedMMF.PageSize;

#if DEBUG
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithCurrentFrame()
            .WriteTo.Seq("http://localhost:5341")
            .CreateLogger();
#endif

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
#if DEBUG
                // builder.AddSerilog(dispose: true);
#endif
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
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = true;
            });
     
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        
        _logger = _serviceCollection.BuildServiceProvider().GetRequiredService<ILogger<BtreeTests>>();
    }

    [Test]
    unsafe public void ForwardInsertionTest()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree(segment, ref accessor);

        tree.Add(10, 10, ref accessor);
        Assert.That(tree[10], Is.EqualTo(10));
        tree.Add(15, 15, ref accessor);
        tree.Add(20, 20, ref accessor);
        Assert.That(tree[20], Is.EqualTo(20));
        tree.Add(50, 50, ref accessor);
        tree.Add(80, 80, ref accessor);
        Assert.That(tree[80], Is.EqualTo(80));
        tree.Add(90, 90, ref accessor);
        Assert.That(tree[90], Is.EqualTo(90));

        tree.Add(100, 100, ref accessor);
        Assert.That(tree[100], Is.EqualTo(100));
        tree.Add(120, 120, ref accessor);
        Assert.That(tree[120], Is.EqualTo(120));
        tree.Add(140, 140, ref accessor);
        Assert.That(tree[140], Is.EqualTo(140));
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void ForwardFloatInsertionTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new FloatSingleBTree(segment, ref accessor);

        tree.Add(-0.10f, 10, ref accessor);
        Assert.That(tree[-0.10f], Is.EqualTo(10));
        tree.Add(0.15f, 15, ref accessor);
        tree.Add(0.20f, 20, ref accessor);
        Assert.That(tree[0.20f], Is.EqualTo(20));
        tree.Add(0.50f, 50, ref accessor);
        tree.Add(0.80f, 80, ref accessor);
        Assert.That(tree[0.80f], Is.EqualTo(80));
        tree.Add(-0.90f, 90, ref accessor);
        Assert.That(tree[-0.90f], Is.EqualTo(90));

        tree.Add(0.101f, 100, ref accessor);
        Assert.That(tree[0.101f], Is.EqualTo(100));
        tree.Add(0.121f, 120, ref accessor);
        Assert.That(tree[0.121f], Is.EqualTo(120));
        tree.Add(0.141f, 140, ref accessor);
        Assert.That(tree[0.141f], Is.EqualTo(140));
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void ReverseInsertionTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree(segment, ref accessor);

        tree.Add(140, 140, ref accessor);
        Assert.That(tree[140], Is.EqualTo(140));
        tree.Add(120, 120, ref accessor);
        Assert.That(tree[120], Is.EqualTo(120));
        tree.Add(100, 100, ref accessor);
        Assert.That(tree[100], Is.EqualTo(100));
        tree.Add(90, 90, ref accessor);
        Assert.That(tree[90], Is.EqualTo(90));
        tree.Add(80, 80, ref accessor);
        Assert.That(tree[80], Is.EqualTo(80));
        tree.Add(50, 50, ref accessor);

        tree.Add(20, 20, ref accessor);
        Assert.That(tree[20], Is.EqualTo(20));
        tree.Add(15, 15, ref accessor);

        tree.Add(10, 10, ref accessor);
        Assert.That(tree[10], Is.EqualTo(10));

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void ReverseString64InsertionTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(IndexString64Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new String64SingleBTree(segment, ref accessor);

        tree.Add("140", 140, ref accessor);
        Assert.That(tree["140"], Is.EqualTo(140));
        tree.Add("120", 120, ref accessor);
        Assert.That(tree["120"], Is.EqualTo(120));
        tree.Add("100", 100, ref accessor);
        Assert.That(tree["100"], Is.EqualTo(100));
        tree.Add("90", 90, ref accessor);
        Assert.That(tree["90"], Is.EqualTo(90));
        tree.Add("80", 80, ref accessor);
        Assert.That(tree["80"], Is.EqualTo(80));
        tree.Add("50", 50, ref accessor);

        tree.Add("20", 20, ref accessor);
        Assert.That(tree["20"], Is.EqualTo(20));
        tree.Add("15", 15, ref accessor);

        tree.Add("10", 10, ref accessor);
        Assert.That(tree["10"], Is.EqualTo(10));

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void CheckTree()
    {
        var values = new int[] { 
            1, 2, 3, 10, 100, 20, 33, 5, 50, 70, 
            35, 9, 99, 101, 109, 103, 102, 40, 51, 200, 
            241, 148, 400, 123, 89, 77, 91, 142, 22, 88,
            404, 6, 221, 301, 298, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree(segment, ref accessor);

        foreach (var v in values)
        {
            tree.Add(v, v, ref accessor);
            tree.CheckConsistency(ref accessor);
        }

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }


    [Test]
    unsafe public void CheckRemove()
    {
        var values = new int[] {
            1, 2, 3, 10, 100, 20, 33, 5, 50, 70,
            35, 9, 99, 101, 109, 103, 102, 40, 51, 200,
            241, 148, 400, 123, 89, 77, 91, 142, 22, 88,
            404, 6, 221, 301, 298, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        var valuesToRemove = new int[] {
            200, 10, 100, 5, 50, 70,
            35, 9, 99, 3,
            241, 148, 77, 91, 142, 22, 88,
            404, 6, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree(segment, ref accessor);

        for (int loopC = 0; loopC < 2; loopC++)
        {
            foreach (var v in values)
            {
                tree.Add(v, v + 1, ref accessor);
            }

            Assert.That(tree.Remove(8080, out var _, ref accessor), Is.False);
            tree.CheckConsistency(ref accessor);

            foreach (var v in valuesToRemove)
            {
                Assert.That(tree.Remove(v, out var val, ref accessor), Is.True, () => $"Failed removed key {v}");
                Assert.That(val, Is.EqualTo(v + 1));
                tree.CheckConsistency(ref accessor);
            }

            for (int i = 0; i < values.Length; i++)
            {
                int value = values[i];
                if (valuesToRemove.Contains(value)) continue;

                Assert.That(tree.Remove(value, out var val, ref accessor), Is.True, () => $"Failed removed key {value}");
                Assert.That(val, Is.EqualTo(value + 1));
                tree.CheckConsistency(ref accessor);
            }
        }
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void BitRandomTest()
    {
        const int sampleCount = 10000;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        
        var samples = new HashSet<int>(sampleCount);
        var r = new Random(DateTime.UtcNow.Millisecond);

        while (samples.Count < sampleCount)
        {
            samples.Add(r.Next());
        }

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree(segment, ref accessor);

        var array = samples.ToArray();
        var count = array.Length;

        var sw = new Stopwatch();
        sw.Start();

        for (int i = 0; i < count; i++)
        {
            var v = array[i];
            tree.Add(v, v, ref accessor);
        }

        sw.Stop();
        Console.WriteLine($"Insertion of {sampleCount} in {sw.ElapsedMilliseconds}ms");

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void CheckMultipleTree()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree(segment, ref accessor);

        var eid0 = tree.Add(1, 10, ref accessor);
        var eid1 = tree.Add(3, 30, ref accessor);
        var eid2 = tree.Add(2, 20, ref accessor);
        var eid3 = tree.Add(2, 21, ref accessor);
        var eid4 = tree.Add(1, 11, ref accessor);
        var eid5 = tree.Add(3, 31, ref accessor);
        var eid6 = tree.Add(2, 22, ref accessor);
        var eid7 = tree.Add(1, 12, ref accessor);

        {
            using var a = tree.TryGetMultiple(1, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(2, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(3, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.RemoveValue(1, eid0, 10, ref accessor);
        tree.RemoveValue(1, eid7, 12, ref accessor);
        tree.RemoveValue(1, eid4, 11, ref accessor);

        {
            using var a = tree.TryGetMultiple(1, ref accessor);
            Assert.That(a.IsValid, Is.False);
        }

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }
    
    [Test]
    unsafe public void CheckByteMultipleTree()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index16Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new ByteMultipleBTree(segment, ref accessor);

        var eid0 = tree.Add(1, 10, ref accessor);
        var eid1 = tree.Add(3, 30, ref accessor);
        var eid2 = tree.Add(2, 20, ref accessor);
        var eid3 = tree.Add(2, 21, ref accessor);
        var eid4 = tree.Add(1, 11, ref accessor);
        var eid5 = tree.Add(3, 31, ref accessor);
        var eid6 = tree.Add(2, 22, ref accessor);
        var eid7 = tree.Add(1, 12, ref accessor);

        {
            using var a = tree.TryGetMultiple(1, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(2, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(3, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.RemoveValue(1, eid0, 10, ref accessor);
        tree.RemoveValue(1, eid7, 12, ref accessor);
        tree.RemoveValue(1, eid4, 11, ref accessor);

        {
            using var a = tree.TryGetMultiple(1, ref accessor);
            Assert.That(a.IsValid, Is.False);
        }

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }

    [Test]
    unsafe public void CheckFloatMultipleTree()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new FloatMultipleBTree(segment, ref accessor);

        var eid0 = tree.Add(1.1f, 10, ref accessor);
        var eid1 = tree.Add(3.1f, 30, ref accessor);
        var eid2 = tree.Add(2.1f, 20, ref accessor);
        var eid3 = tree.Add(2.1f, 21, ref accessor);
        var eid4 = tree.Add(1.1f, 11, ref accessor);
        var eid5 = tree.Add(3.1f, 31, ref accessor);
        var eid6 = tree.Add(2.1f, 22, ref accessor);
        var eid7 = tree.Add(1.1f, 12, ref accessor);

        {
            using var a = tree.TryGetMultiple(1.1f, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(2.1f, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(3.1f, ref accessor);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.RemoveValue(1.1f, eid0, 10, ref accessor);
        tree.RemoveValue(1.1f, eid7, 12, ref accessor);
        tree.RemoveValue(1.1f, eid4, 11, ref accessor);

        {
            using var a = tree.TryGetMultiple(1, ref accessor);
            Assert.That(a.IsValid, Is.False);
        }

        tree.CheckConsistency(ref accessor);
        
        accessor.Dispose();
    }

    [Test]
    [Property("MemPageCount", 1024)]
    unsafe public void CheckSingleWithPersistence()
    {
        const int itemCount = 10000;

        Dictionary<float, int> items = new Dictionary<float, int>(itemCount);
        var segmentIndex = 0;
        
        {
            using var scope = _serviceProvider.CreateScope();
            using var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            var changeSet = mmf.CreateChangeSet();

            var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk), changeSet);
            segmentIndex = segment.RootPageIndex;
            var accessor = segment.CreateChunkAccessor(changeSet);
            var tree = new FloatSingleBTree(segment, ref accessor);

            var rand = new Random(1234);
            var curValue = 12;

            var sw = new Stopwatch();

            for (int i = 0; i < itemCount; i++)
            {
                float key;
                while (true)
                {
                    key = rand.NextSingle();
                    if (items.TryAdd(key, curValue))
                    {
                        break;
                    }
                }
                tree.Add(key, curValue, ref accessor);
                curValue++;
            }
            
            changeSet.SaveChanges();
        
            accessor.Dispose();
        }

        {
            using var scope = _serviceProvider.CreateScope();
            using var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            var segment = mmf.LoadChunkBasedSegment(segmentIndex, sizeof(Index32Chunk));
            var accessor = segment.CreateChunkAccessor();
            var tree = new FloatSingleBTree(segment, ref accessor, true);

            foreach (var kvp in items)
            {
                var res = tree.TryGet(kvp.Key, out var value, ref accessor);
                Assert.That(res, Is.True);
                Assert.That(value, Is.EqualTo(kvp.Value), $"Failed to get value for key {kvp.Key}");
            }
        
            accessor.Dispose();
        }
        
        /*
        tree.CheckConsistency(ref accessor);
        
        _logger.LogInformation("Total insertion count {itemCount}, chunk allocated {SegmentAllocatedChunkCount}, time per insert {time}", 
            itemCount, segment.AllocatedChunkCount, (sw.Elapsed / itemCount).TotalSeconds.FriendlyTime());
    */
    }

    [Test]
    unsafe public void CheckSingleTreeBigAmount()
    {
        const int itemCount = 100000;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new FloatSingleBTree(segment, ref accessor);

        var rand = new Random(1234);
        var hashset = new HashSet<float>();

        var sw = new Stopwatch();

        for (int i = 0; i < itemCount; i++)
        {
            float val;
            while (true)
            {
                val = rand.NextSingle();
                if (hashset.Add(val))
                {
                    break;
                }
            }
            sw.Start();
            tree.Add(val, 1, ref accessor);
            sw.Stop();
        }

        tree.CheckConsistency(ref accessor);
        
        _logger.LogInformation("Total insertion count {itemCount}, chunk allocated {SegmentAllocatedChunkCount}, time per insert {time}", 
            itemCount, segment.AllocatedChunkCount, (sw.Elapsed / itemCount).TotalSeconds.FriendlyTime());
        
        accessor.Dispose();
    }

    [Test]
    [Property("MemPageCount", 1024)]
    unsafe public void CheckMultipleTreeBigAmount()
    {
        const int itemCount = 1000;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree(segment, ref accessor);

        var chunkCapacity = segment.ChunkCapacity;
        var freeChunkCount = segment.FreeChunkCount;

        var elemIdDic = new Dictionary<int, List<int>>(itemCount);
        
        var sw = new Stopwatch();
        
        int gc = 0;
        for (int i = 0; i < itemCount; i++)
        {
            var idList = new List<int>(i);
            elemIdDic.Add(i, idList);

            for (int j = 0; j < i; j++, gc++)
            {
                sw.Start();
                var item = tree.Add(i, 10 + j, ref accessor);
                sw.Stop();
                idList.Add(item);
            }
        }

        _logger.LogError("Total insertion count {Gc}, chunk allocated {SegmentAllocatedChunkCount}, time per insert {FriendlyTime}", gc, segment.AllocatedChunkCount, (sw.Elapsed / gc).TotalSeconds.FriendlyTime());

        // Parse every element buffers
        for (int i = 1; i < itemCount; i++)
        {
            var c = 0;
            using var a = tree.TryGetMultiple(i, ref accessor);
            Assert.That(a.IsValid, Is.True);
            do
            {
                c += a.ReadOnlyElements.Length;
            } while (a.NextChunk());

            Assert.That(c, Is.EqualTo(i));
        }

        // Now this is the nasty part, we delete half of the chunk of the buffer to create fragmentation that
        //  will be solved during the next parsing...
        for (int i = 0; i < itemCount; i++)
        {
            var idList = elemIdDic[i];

            for (int j = 0; j < i; j++, gc++)
            {
                var elemId = idList[j];
                if (((elemId + i) & 1) != 0)                // Use 'i'  to alternate deleting either odd or even chunks
                {
                    tree.RemoveValue(i, elemId, 10 + j, ref accessor);
                }
            }
        }
            
        _logger.LogError("Remove half key/values, chunk allocated {cc}", segment.AllocatedChunkCount);

        // Parse every element buffers
        for (int i = 1; i < itemCount; i++)
        {
            var c = 0;
            using var a = tree.TryGetMultiple(i, ref accessor);
            if (a.IsValid == false) continue;
                
            //Assert.That(a.IsValid, Is.True);
            do
            {
                c += a.ReadOnlyElements.Length;
            } while (a.NextChunk());

            //Assert.That(c, Is.EqualTo(i));
        }
        _logger.LogError("Remove half key/values, chunk allocated after collect {cc}", segment.AllocatedChunkCount);

        // Delete the rest
        for (int i = 0; i < itemCount; i++)
        {
            var idList = elemIdDic[i];

            for (int j = 0; j < i; j++, gc++)
            {
                var elemId = idList[j];
                if (((elemId + i) & 1) == 0)                // Use 'i'  to alternate deleting either odd or even chunks
                {
                    tree.RemoveValue(i, elemId, 10 + j, ref accessor);
                }
            }
        }

        //tree.First
        
        accessor.Dispose();

        _logger.LogError("Remove all key/values, chunk allocated {cc}", segment.AllocatedChunkCount);
    }

    [Test]
    public unsafe void CheckSingleTreeMultiThread()
    {
        /*
        const int sampleCount = 10000;
        const int threadCount = 8;

        var samples = new HashSet<int>(sampleCount*threadCount);

        var r = new Random(DateTime.UtcNow.Millisecond);
        while (samples.Count < (sampleCount*threadCount))
        {
            samples.Add(r.Next());
        }

        var samplesArray = samples.ToArray();

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var tree = new IntSingleBTree(segment);

        var sw = new Stopwatch();
        sw.Start();

        var tasks = new List<Task>();
        for (int i = 0; i < threadCount; i++)
        {
            var threadIndex = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < sampleCount; j++)
                {
                    var v = samplesArray[j + threadIndex*sampleCount];
                    tree.Add(v, v);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        sw.Stop();
        Console.WriteLine($"Insertion of {sampleCount*threadCount} spread in {threadCount} threads done in {sw.ElapsedMilliseconds}ms");

        tree.CheckConsistency();
        */
    }
}