using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.BPTree;

namespace Typhon.Engine.Tests.Database_Engine;

class BtreeTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private LogicalSegmentManager _lsm;
    private DatabaseConfiguration _configuration;
    private ILogger<BtreeTests> _logger;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize") : (int)PagedMemoryMappedFile.MinimumCacheSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddTyphon(builder =>
            {
                builder.ConfigureDatabase(dc =>
                {
                    dc.DatabaseName = CurrentDatabaseName;
                    dc.RecreateDatabase = true;
                    dc.DeleteDatabaseOnDispose = true;
                    dc.DatabaseCacheSize = (ulong)dcs;
                });
            })

            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();

        _lsm = _serviceProvider.GetRequiredService<LogicalSegmentManager>();
        _configuration = _serviceProvider.GetRequiredService<IConfiguration<DatabaseConfiguration>>().Value;
        _logger = _serviceProvider.GetRequiredService<ILogger<BtreeTests>>();
        
        _lsm.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        Thread.Sleep(500);
        _lsm?.Dispose();
    }

    [Test]
    unsafe public void ForwardInsertionTest()
    {
        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new IntSingleBTree(segment);

        tree.Add(10, 10);
        Assert.That(tree[10], Is.EqualTo(10));
        tree.Add(15, 15);
        tree.Add(20, 20);
        Assert.That(tree[20], Is.EqualTo(20));
        tree.Add(50, 50);
        tree.Add(80, 80);
        Assert.That(tree[80], Is.EqualTo(80));
        tree.Add(90, 90);
        Assert.That(tree[90], Is.EqualTo(90));

        tree.Add(100, 100);
        Assert.That(tree[100], Is.EqualTo(100));
        tree.Add(120, 120);
        Assert.That(tree[120], Is.EqualTo(120));
        tree.Add(140, 140);
        Assert.That(tree[140], Is.EqualTo(140));
    }

    [Test]
    unsafe public void ForwardFloatInsertionTest()
    {
        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new FloatSingleBTree(segment);

        tree.Add(-0.10f, 10);
        Assert.That(tree[-0.10f], Is.EqualTo(10));
        tree.Add(0.15f, 15);
        tree.Add(0.20f, 20);
        Assert.That(tree[0.20f], Is.EqualTo(20));
        tree.Add(0.50f, 50);
        tree.Add(0.80f, 80);
        Assert.That(tree[0.80f], Is.EqualTo(80));
        tree.Add(-0.90f, 90);
        Assert.That(tree[-0.90f], Is.EqualTo(90));

        tree.Add(0.101f, 100);
        Assert.That(tree[0.101f], Is.EqualTo(100));
        tree.Add(0.121f, 120);
        Assert.That(tree[0.121f], Is.EqualTo(120));
        tree.Add(0.141f, 140);
        Assert.That(tree[0.141f], Is.EqualTo(140));
    }

    [Test]
    unsafe public void ReverseInsertionTest()
    {
        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new IntSingleBTree(segment);

        tree.Add(140, 140);
        Assert.That(tree[140], Is.EqualTo(140));
        tree.Add(120, 120);
        Assert.That(tree[120], Is.EqualTo(120));
        tree.Add(100, 100);
        Assert.That(tree[100], Is.EqualTo(100));
        tree.Add(90, 90);
        Assert.That(tree[90], Is.EqualTo(90));
        tree.Add(80, 80);
        Assert.That(tree[80], Is.EqualTo(80));
        tree.Add(50, 50);

        tree.Add(20, 20);
        Assert.That(tree[20], Is.EqualTo(20));
        tree.Add(15, 15);

        tree.Add(10, 10);
        Assert.That(tree[10], Is.EqualTo(10));


        tree.CheckConsistency();
    }

    [Test]
    unsafe public void ReverseString64InsertionTest()
    {
        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(IndexString64Chunk));
        var tree = new String64SingleBTree(segment);

        tree.Add("140", 140);
        Assert.That(tree["140"], Is.EqualTo(140));
        tree.Add("120", 120);
        Assert.That(tree["120"], Is.EqualTo(120));
        tree.Add("100", 100);
        Assert.That(tree["100"], Is.EqualTo(100));
        tree.Add("90", 90);
        Assert.That(tree["90"], Is.EqualTo(90));
        tree.Add("80", 80);
        Assert.That(tree["80"], Is.EqualTo(80));
        tree.Add("50", 50);

        tree.Add("20", 20);
        Assert.That(tree["20"], Is.EqualTo(20));
        tree.Add("15", 15);

        tree.Add("10", 10);
        Assert.That(tree["10"], Is.EqualTo(10));


        tree.CheckConsistency();
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

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new IntSingleBTree(segment);

        foreach (var v in values)
        {
            tree.Add(v, v);
            tree.CheckConsistency();
        }

        tree.CheckConsistency();
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

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new IntSingleBTree(segment);

        for (int loopC = 0; loopC < 2; loopC++)
        {
            foreach (var v in values)
            {
                tree.Add(v, v + 1);
            }

            Assert.That(tree.Remove(8080, out var _), Is.False);
            tree.CheckConsistency();

            foreach (var v in valuesToRemove)
            {
                Assert.That(tree.Remove(v, out var val), Is.True, () => $"Failed removed key {v}");
                Assert.That(val, Is.EqualTo(v + 1));
                tree.CheckConsistency();
            }

            for (int i = 0; i < values.Length; i++)
            {
                int value = values[i];
                if (valuesToRemove.Contains(value)) continue;

                Assert.That(tree.Remove(value, out var val), Is.True, () => $"Failed removed key {value}");
                Assert.That(val, Is.EqualTo(value + 1));
                tree.CheckConsistency();
            }

        }
    }

    [Test]
    unsafe public void BitRandomTest()
    {
        const int sampleCount = 10000;
        var samples = new HashSet<int>(sampleCount);
        var r = new Random(DateTime.UtcNow.Millisecond);

        while (samples.Count < sampleCount)
        {
            samples.Add(r.Next());
        }

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(Index32Chunk));
        var tree = new IntSingleBTree(segment);


        var array = samples.ToArray();
        var count = array.Length;

        var sw = new Stopwatch();
        sw.Start();

        for (int i = 0; i < count; i++)
        {
            var v = array[i];
            tree.Add(v, v);
        }

        sw.Stop();
        Console.WriteLine($"Insertion of {sampleCount} in {sw.ElapsedMilliseconds}ms");

        tree.CheckConsistency();
    }

    [Test]
    unsafe public void CheckMultipleTree()
    {

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new IntMultipleBTree(segment);

        var eid0 = tree.Add(1, 10);
        var eid1 = tree.Add(3, 30);
        var eid2 = tree.Add(2, 20);
        var eid3 = tree.Add(2, 21);
        var eid4 = tree.Add(1, 11);
        var eid5 = tree.Add(3, 31);
        var eid6 = tree.Add(2, 22);
        var eid7 = tree.Add(1, 12);

        {
            using var a = tree.TryGetMultiple(1);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(2);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(3);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.RemoveValue(1, eid0, 10);
        tree.RemoveValue(1, eid7, 12);
        tree.RemoveValue(1, eid4, 11);

        {
            using var a = tree.TryGetMultiple(1);
            Assert.That(a.IsValid, Is.False);
        }

        tree.CheckConsistency();
    }
    [Test]
    unsafe public void CheckByteMultipleTree()
    {

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index16Chunk));
        var tree = new ByteMultipleBTree(segment);

        var eid0 = tree.Add(1, 10);
        var eid1 = tree.Add(3, 30);
        var eid2 = tree.Add(2, 20);
        var eid3 = tree.Add(2, 21);
        var eid4 = tree.Add(1, 11);
        var eid5 = tree.Add(3, 31);
        var eid6 = tree.Add(2, 22);
        var eid7 = tree.Add(1, 12);

        {
            using var a = tree.TryGetMultiple(1);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(2);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(3);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.RemoveValue(1, eid0, 10);
        tree.RemoveValue(1, eid7, 12);
        tree.RemoveValue(1, eid4, 11);

        {
            using var a = tree.TryGetMultiple(1);
            Assert.That(a.IsValid, Is.False);
        }

        tree.CheckConsistency();
    }

    [Test]
    unsafe public void CheckFloatMultipleTree()
    {

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new FloatMultipleBTree(segment);

        var eid0 = tree.Add(1.1f, 10);
        var eid1 = tree.Add(3.1f, 30);
        var eid2 = tree.Add(2.1f, 20);
        var eid3 = tree.Add(2.1f, 21);
        var eid4 = tree.Add(1.1f, 11);
        var eid5 = tree.Add(3.1f, 31);
        var eid6 = tree.Add(2.1f, 22);
        var eid7 = tree.Add(1.1f, 12);

        {
            using var a = tree.TryGetMultiple(1.1f);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(2.1f);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
        }

        {
            using var a = tree.TryGetMultiple(3.1f);
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.RemoveValue(1.1f, eid0, 10);
        tree.RemoveValue(1.1f, eid7, 12);
        tree.RemoveValue(1.1f, eid4, 11);

        {
            using var a = tree.TryGetMultiple(1);
            Assert.That(a.IsValid, Is.False);
        }

        tree.CheckConsistency();
    }

    [Test]
    unsafe public void CheckMultipleTreeBigAmount()
    {
        const int itemCount = 1000;

        var segment = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var tree = new IntMultipleBTree(segment);

        var chunkCapacity = segment.ChunkCapacity;
        var freeChunkCount = segment.FreeChunkCount;

        var elemIdDic = new Dictionary<int, List<int>>(itemCount);

        int gc = 0;
        for (int i = 0; i < itemCount; i++)
        {
            var idList = new List<int>(i);
            elemIdDic.Add(i, idList);

            for (int j = 0; j < i; j++, gc++)
            {
                idList.Add(tree.Add(i, 10 + j));
            }
        }

        _logger.LogError("Total insertion count {gc}, chunk allocated {cc}", gc, segment.AllocatedChunkCount);

        // Parse every element buffers
        for (int i = 1; i < itemCount; i++)
        {
            var c = 0;
            using var a = tree.TryGetMultiple(i);
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
                    tree.RemoveValue(i, elemId, 10 + j);
                }
            }
        }
            
        _logger.LogError("Remove half key/values, chunk allocated {cc}", segment.AllocatedChunkCount);

        // Parse every element buffers
        for (int i = 1; i < itemCount; i++)
        {
            var c = 0;
            using var a = tree.TryGetMultiple(i);
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
                    tree.RemoveValue(i, elemId, 10 + j);
                }
            }
        }

        //tree.First

        _logger.LogError("Remove all key/values, chunk allocated {cc}", segment.AllocatedChunkCount);
    }

    [Test]
    public unsafe void CheckSingleTreeMultiThread()
    {
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

    }
}