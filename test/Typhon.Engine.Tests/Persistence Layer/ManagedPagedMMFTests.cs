using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

public class ManagedPagedMMFTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private ILogger<ManagedPagedMMFTests> _logger;

    static readonly char[] charToRemove = ['(', ')'];
    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;

            foreach (var c in charToRemove)
            {
                testName = testName.Replace(c, '_');
            }
            
            return $"Typhon_{testName}_db";
        }
    }

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
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            });
        
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _logger = _serviceCollection.BuildServiceProvider().GetRequiredService<ILogger<ManagedPagedMMFTests>>();
    }

    [TearDown]
    public void TearDown()
    {
    }

    [Test]
    public void InitializationTest()
    {
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<ManagedPagedMMF>();
            
            var metrics = pmmf.GetMetrics();
            var cacheHit = metrics.MemPageCacheHit;

            pmmf.RequestPage(0, false, out var pa);
            using (pa)
            {
                metrics = pmmf.GetMetrics();
                Assert.That(metrics.MemPageCacheHit, Is.GreaterThan(cacheHit));
            }
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<ManagedPagedMMF>();
            pmmf.RequestPage(0, false, out var pa);
            using (pa)
            {
                ref var h = ref pa.WholePage.Cast<byte, RootFileHeader>()[0];
                Assert.That(h.HeaderSignatureString, Is.EqualTo(ManagedPagedMMF.HeaderSignature));
                Assert.That(h.DatabaseNameString, Is.EqualTo(CurrentDatabaseName));
            }
        }
    }
    
    [Test]
    public void FindNextUnsetL0Test()
    {
        var bitCount = 64 * 64 * 64 * 10;
        var pageCount = (int)Math.Ceiling((double)bitCount / (PagedMMF.PageRawDataSize * 8));
        
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var seg = pmmf.AllocateSegment(PageBlockType.None, pageCount);

        var c = new ManagedPagedMMF.BitmapL3(seg);

        var index = -1;
        long mask = 0L;

        //c.Set(0);
        c.SetL0(1);
        c.SetL0(2);

        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(0));

        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(3));

        var offset = 0;
        var range = 64 * 64 * 64 + 64 * 64 + 1;
        for (int i = offset; i < (offset + range); i++)
        {
            c.SetL0(i);
        }

        index = -1;
        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(range));

        offset = 64 * 64;
        range = 1;
        for (int i = offset; i < (offset + range); i++)
        {
            c.ClearL0(i);
        }

        index = -1;
        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(offset));

        pmmf.DeleteSegment(seg);
    }
    
    [Test]
    public void FindNextUnsetL1Test()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        
        var bitCount = 64 * 64 * 64 * 10;
        var pageCount = (int)Math.Ceiling((double)bitCount / (PagedMMF.PageRawDataSize * 8));

        var seg = pmmf.AllocateSegment(PageBlockType.None, pageCount);

        var l3 = new ManagedPagedMMF.BitmapL3(seg);

        var index = -1;
        long mask = 0L;
        l3.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(0));

        l3.SetL0(0);
        l3.SetL0(128);
        index = -1;
        mask = 0L;
        l3.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(1));

        l3.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(3));
        
        pmmf.DeleteSegment(seg);
    }

    [Test]
    public void SetL1Test()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var bitCount = 64 * 64 * 64 * 10;
        var pageCount = (int)Math.Ceiling((double)bitCount / (PagedMMF.PageRawDataSize * 8));

        var seg = pmmf.AllocateSegment(PageBlockType.None, pageCount);

        var c = new ManagedPagedMMF.BitmapL3(seg);

        c.SetL1(0);
        Assert.That(c.IsSet(0), Is.EqualTo(true));
        Assert.That(c.IsSet(63), Is.EqualTo(true));
        Assert.That(c.IsSet(64), Is.EqualTo(false));

        var index = -1;
        long mask = 0L;
        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(64));
            
        index = -1;
        mask = 0L;
        c.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(1));
        c.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(2));
        
        pmmf.DeleteSegment(seg);
    }
    
    [Test]
    public void CreateSegment()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var s0 = pmmf.AllocateSegment(PageBlockType.None, 10);
        var s1 = pmmf.AllocateSegment(PageBlockType.None, 50);
        pmmf.DeleteSegment(s0);
        var s2 = pmmf.AllocateSegment(PageBlockType.None, 100);

        var s3 = pmmf.AllocateSegment(PageBlockType.None, 100);

        pmmf.DeleteSegment(s2);
        pmmf.DeleteSegment(s1);
        pmmf.DeleteSegment(s3);
    }
    
    static object[] Cases = {
        new TestCaseData(5000).SetProperty("MemPageCount", 6000),
        new TestCaseData(4500).SetProperty("MemPageCount", 6000)
    };    

    [Test]
    [TestCaseSource(nameof(Cases))]
    public void CreateAndLoadBigSegment(int segmentLength)
    {
        int rootSegmentIndex;
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = pmmf.CreateChangeSet();

            var s0 = pmmf.AllocateSegment(PageBlockType.None, segmentLength, cs);
            
            for (int i = 0; i < segmentLength; i++)
            {
                s0.GetPageExclusiveAccessor(i, out var pa);
                cs.Add(pa);
                using (pa)
                {
                    var rd = pa.LogicalSegmentData.Cast<byte, int>();
                    rd[0] = i;
                    rd[^1] = i + 1000;
                }
            }
            cs.SaveChanges();
            rootSegmentIndex = s0.RootPageIndex;
        }
        
        {
            using var scope = _serviceProvider.CreateScope();
            var mpmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var s0 = mpmmf.GetSegment(rootSegmentIndex);

            for (int i = 0; i < segmentLength; i++)
            {
                s0.GetPageExclusiveAccessor(i, out var pa);
                using (pa)
                {
                    var rd = pa.LogicalSegmentData.Cast<byte, int>();
                    Assert.That(rd[0], Is.EqualTo(i));
                    Assert.That(rd[^1], Is.EqualTo(i + 1000));
                }
            }
        }
    }

    [Test]
    public void OccupancyMapSaveTest()
    {
        int[] s0Pages;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = mmf.CreateChangeSet();
            var s0 = mmf.AllocateSegment(PageBlockType.None, 10, cs);
            s0Pages = s0.Pages.ToArray();
            cs.SaveChanges();
        }
        
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var newS0 = mmf.AllocateSegment(PageBlockType.None, 10);

            int[] newS0Array = newS0.Pages.ToArray();
            Assert.That(newS0Array.All(p => p != 0 && p != 1), Is.True);       // The returned pages can't be 0 (header) or 1 (occupancy segment)
            Assert.That(newS0Array.All(p => !s0Pages.Contains(p)), Is.True);            
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ChunkA
    {
        public int A;
        public int B;
        public int C;
        public int D;
    }

    [Test]
    unsafe public void ChunkBasedSegmentTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        
        var s0 = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(ChunkA));
        var cap = s0.ChunkCapacity;

        using var mo = s0.AllocateChunks(2000, false);
        using var ca = s0.CreateChunkRandomAccessor(8);

        ref var obj = ref ca.GetChunk<ChunkA>(0);
        obj.A = -1;
        obj.B = -1;
        obj.C = -1;
        obj.D = -1;

        obj = ca.GetChunk<ChunkA>(1);
        obj.A = 1;

        obj = ca.GetChunk<ChunkA>(500);
        obj.A = 1;

    }

    [Test]
    public void VariableSizedBufferSegmentTest()
    {
        const int itemCount = 1024;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);
        using var accessor = s.CreateChunkRandomAccessor(8);

        var vsb = new VariableSizedBufferSegment<long>(s);

        var id0 = vsb.AllocateBuffer(accessor);

        for (int i = 0; i < itemCount; i++)
        {
            vsb.AddElement(id0, 1234, accessor);
        }

        var loopCount = 0;
        var ba = vsb.GetReadOnlyAccessor(id0);
        do
        {
            var elements = ba.ReadOnlyElements;
            var c = elements.Length;
            for (int i = 0; i < c; i++)
            {
                Assert.That(elements[i], Is.EqualTo(1234));
                ++loopCount;
            }
        } while (ba.NextChunk());
        Assert.That(loopCount, Is.EqualTo(itemCount));
    }

    [Test]
    public void VariableSizedBuffer_CloneBuffer([Values(1, 34, 67, 129)] int seeds)
    {
        var rand = new Random(seeds);
        const int itemCount = 1024;

        // Services
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);
        using var accessor = s.CreateChunkRandomAccessor(8);

        // VSBS
        var vsb = new VariableSizedBufferSegment<int>(s);

        // Buffer
        var id0 = vsb.AllocateBuffer(accessor);

        // Add the items, record their location and value
        var ids = new List<(int, int)>(itemCount);
        var co = 0;
        for (int i = 0; i < itemCount; i++)
        {
            co++;
            var value = rand.Next();
            ids.Add((vsb.AddElement(id0, value, accessor), value));
        }

        // Delete 1/16 of the items to create fragmentation
        const int deleteCount = itemCount >> 4;
        for (int i = 0; i < deleteCount; i++)
        {
            var itemIndex = rand.Next(0, itemCount - i);
            var record = ids[itemIndex];
            ids.RemoveAt(itemIndex);
            vsb.DeleteElement(id0, record.Item1, record.Item2, accessor);
        }
        
        // Clone the buffer
        var id1 = vsb.CloneBuffer(id0, accessor);
        
        var hashset = new HashSet<int>();
        hashset.EnsureCapacity(itemCount);
        hashset.UnionWith(ids.Select(item => item.Item2));
        
        var loopCount = 0;
        var ba = vsb.GetReadOnlyAccessor(id1);
        do
        {
            var elements = ba.ReadOnlyElements;
            var c = elements.Length;
            for (int i = 0; i < c; i++)
            {
                Assert.That(hashset.Contains(elements[i]), Is.True);
                ++loopCount;
            }
        } while (ba.NextChunk());
        Assert.That(loopCount, Is.EqualTo(itemCount - deleteCount));
    }
    
    [Test]
    public void VariableSizedBufferSegmentDeleteTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);
        using var accessor = s.CreateChunkRandomAccessor(8);

        var vsb = new VariableSizedBufferSegment<long>(s);

        var id0 = vsb.AllocateBuffer(accessor);
        var elIdList = new int[15];

        // 15 is spread into 3 chunks: 4, 7, 4
        for (int i = 0; i < 15; i++)
        {
            elIdList[i] = vsb.AddElement(id0, i, accessor);
        }

        // Delete all the elements of the second chunk
        for (int i = 4; i < 11; i++)
        {
            Assert.That(vsb.DeleteElement(id0, elIdList[i], i, accessor), Is.Not.EqualTo(-1));
        }

        // Trigger an enumeration that will remove the second chunk from the stored list and put it in the free list
        {
            int count = 0;
            int hops = 0;
            using var ba = vsb.GetReadOnlyAccessor(id0);
            do
            {
                count += ba.ReadOnlyElements.Length;
                ++hops;
            } while (ba.NextChunk());

            Assert.That(count, Is.EqualTo(8));
            Assert.That(hops, Is.EqualTo(2));
        }
    }
    
    private const string Muse =
        @"Home, It's becoming a killing field
There's a cross hair locked on my heart
With no recourse and there's no one behind the wheel
Hell fire, You're wiping me out, killed by
Drones, (killed by)
Drones(killed by)
You rule with lies and deceit
And the world is on your side
'Cause you've got the CIA, babe
But all you've done is brutalise
Drones!
War, war just moved up a gear
I don't think I can handle the truth
I'm just a pawn
And we're all expendable
Incidentally
Electronically erased
By your
Drones, (killed by)
Drones(killed by)
You kill by remote control
The world is on your side
You've got reapers and hawks babe
Now I am radicalized
Drones!
You rule with lies and deceit
And the world is on your side
'Cause you've got the CIA, babe
But all you've done is brutalise
You kill by remote control
The world is on your side
You've got reapers and hawks babe
Now I am radicalized
Here come the drones!
Here come the drones!
Here come the drones!";

    [Test]
    public void StringTableTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);

        var st = new StringTableSegment(s);

        var id = st.StoreString(Muse);

        string ns = st.LoadString(id);

        Assert.That(ns, Is.EqualTo(Muse));

        st.DeleteString(id);
    }

    public class LockStore
    {
        public int A;
        public int B;
        public int R;
    }

    [Test]
    public void LockTest()
    {
        const int iterationCount = 10_000;

        var s = new LockStore();

        var taskList = new List<Task>();
        var rwsl = new AccessControlSmall();
        var r = new Random(DateTime.UtcNow.Millisecond);

        for (int i = 0; i < 32; i++)
        {
            var t = Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"UnitTest_{Environment.CurrentManagedThreadId}";

                for (int j = 0; j < iterationCount; j++)
                {
                    var write = r.Next(0, 100) < 25;

                    // Write use case
                    if (write)
                    {
                        rwsl.EnterExclusiveAccess();

                        s.A = r.Next(0, 100000);
                        s.B = r.Next(0, 100000);
                        s.R = s.A + s.B;

                        rwsl.ExitExclusiveAccess();
                    }

                    // Read use case
                    else
                    {

                        rwsl.EnterSharedAccess();

                        Assert.That(s.R, Is.EqualTo(s.A + s.B));

                        rwsl.ExitSharedAccess();
                            
                    }
                }
            });

            taskList.Add(t);
        }

        Task.WaitAll(taskList.ToArray());

        Assert.That(rwsl.SharedUsedCounter, Is.EqualTo(0));
    }

    [Test, CancelAfter(10_000)]
    [Property("MemPageCount", 50000)]       // Must be larger than maxBeforeGrow defined below
    public void GrowOccupancyMapTest()
    {
        const int maxBeforeGrow = 
            ((PagedMMF.PageRawDataSize - LogicalSegment.RootHeaderIndexSectionLength) * 8) - ManagedPagedMMF.InitialReservedPageCount - 25;
        
        int rootSegmentIndex, segmentTotalLength;
        ReadOnlySpan<int> segmentPages;

        {
            Stopwatch sw = Stopwatch.StartNew();
            
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = pmmf.CreateChangeSet();

            var s0 = pmmf.AllocateSegment(PageBlockType.None, maxBeforeGrow, cs);
            rootSegmentIndex = s0.RootPageIndex;
            
            sw.Stop();
            _logger.LogInformation("Segment allocated in {Elapsed} ms", sw.ElapsedMilliseconds);
            
            sw.Restart();
            cs.SaveChanges();
            sw.Stop();
            _logger.LogInformation("Save segment of {Size} in {Elapsed} ms", (s0.Length * PagedMMF.PageSize).FriendlySize(), sw.ElapsedMilliseconds);
            
            // Grow the segment to trigger the occupancy map grow
            s0.Grow(s0.Length + 10, true, cs);
            cs.SaveChanges();

            segmentPages = s0.Pages;
            segmentTotalLength = s0.Length;
            
        }
        
        {
            using var scope = _serviceProvider.CreateScope();
            var mpmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var s0 = mpmmf.GetSegment(rootSegmentIndex);
            Assert.That(s0.Length, Is.EqualTo(segmentTotalLength));

            // Check the pages of the segment were loaded correctly
            for (int i = 0; i < s0.Pages.Length; i++)
            {
                Assert.That(segmentPages[i], Is.EqualTo(s0.Pages[i]));
            }
        }
    }

    [Test]
    [Property("MemPageCount", 2005)]        // 2000 for the amount of page in an index map (500 for the first, 2000 for the others), 5 for extra system pages
    public void LogicalSegmentGrowTest()
    {
        const int initialSize = 10;         // Header = 10
        const int firstGrowSize = 510;      // Header = 500, Map #1 (new) = 10
        const int secondGrowSize = 2510;    // Header = 500, Map #1 = 2000, Map #2 (new) = 10
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = pmmf.CreateChangeSet();

            var s0 = pmmf.AllocateSegment(PageBlockType.None, initialSize, cs);
            s0.Grow(firstGrowSize, true, cs);
            cs.SaveChanges();
            
            s0.Grow(secondGrowSize, true, cs);
            cs.SaveChanges();
        }
        
    }
}