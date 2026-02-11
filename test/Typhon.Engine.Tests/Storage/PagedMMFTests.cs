using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class PagedMMFTests
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

    private const int CreateFillPagesThenReadThemMemPageCount = 512;
    [Test]
    [Property("MemPageCount", CreateFillPagesThenReadThemMemPageCount)]
    public void CreateFillPagesThenReadThem()
    {
        const int pageCount = 128;

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
        
            // Fill pages with data
            {
                var now = DateTime.UtcNow;
                var cs = pmmf.CreateChangeSet();
                for (int i = 0; i < pageCount; i++)
                {
                    pmmf.RequestPage(i, true, out var accessor);
                    using (accessor)
                    {
                        var ispan = accessor.PageRawData.Cast<byte, int>();
                        ispan[0] = i; // Set first element
                        ispan[^1] = i; // Set last element
                        cs.Add(accessor);
                    }
                }
                Console.WriteLine($"Average time to fill one page {((DateTime.UtcNow - now).TotalSeconds / pageCount).FriendlyTime()}");

                Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount - pageCount));
                now = DateTime.UtcNow;
                cs.SaveChanges();
            
                Console.WriteLine($"Average time to save one page {((DateTime.UtcNow - now).TotalSeconds / pageCount).FriendlyTime()}");
            }
        
            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
            long totalRequest = 0;
            long totalAccess = 0;
        
            // The file exists and has content, load it
            {
                for (int i = 0; i < pageCount; i++)
                {
                    var now = DateTime.UtcNow;
                
                    pmmf.RequestPage(i, true, out var accessor);
                    using (accessor)
                    {
                        totalRequest += (DateTime.UtcNow - now).Ticks;
                
                        now = DateTime.UtcNow;
                        var content = accessor.PageRawData.Cast<byte, int>();
                        totalAccess += (DateTime.UtcNow - now).Ticks;
                
                        Assert.That(content[0], Is.EqualTo(i), $"Page {i} content mismatch after reset.");
                        Assert.That(content[^1], Is.EqualTo(i), $"Page {i} content mismatch after reset.");
                    }
                }
            }
        
            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
            Console.WriteLine($"Average Request Time: {TimeSpan.FromTicks(totalRequest / pageCount).TotalSeconds.FriendlyTime()}, Average Access Time: {TimeSpan.FromTicks(totalAccess / pageCount).TotalSeconds.FriendlyTime()}");
        }
    }

    [Test]
    [Property("MemPageCount", 4)]
    public void MemPageStarvation()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        
        var waitTask0PagesCreated = new ManualResetEventSlim();
        
        var tasks = new Task[2];
        tasks[0] = Task.Run(() =>
        {
            var accessors = new PageAccessor[4];
            for (int i = 0; i < 4; i++)
            {
                pmmf.RequestPage(i, false, out accessors[i]);
            }
            waitTask0PagesCreated.Set();
            
            for (int i = 0; i < 4; i++)
            {
                Thread.Sleep(500);
                accessors[i].Dispose();
            }

            return Task.CompletedTask;
        });

        tasks[1] = Task.Run(() =>
        {
            waitTask0PagesCreated.Wait();

            var accessors = new PageAccessor[4];
            for (int i = 0; i < 4; i++)
            {
                var now = DateTime.UtcNow;
                pmmf.RequestPage(i + 4, false, out var pa);
                
                var delay = (DateTime.UtcNow - now).TotalSeconds;
                Assert.That(delay, Is.GreaterThanOrEqualTo(0.4), $"Page {i + 4} should take at least 0.4sec to access as it is the time for the first thread to release one.");
                Console.WriteLine($"Page {i + 4} created in {delay.FriendlyTime()}");
                
                pa.PageRawData[0] = 123;
                accessors[i] = pa;
            }

            foreach (PageAccessor pa in accessors)
            {
                pa.Dispose();
            }

        });
        
        Task.WaitAll(tasks);
    }
    
    private static List<int> GenerateRandomAccess(int min, int max, int count=0)
    {
        int inputCount = max - min + 1;
        var input = new List<int>(inputCount);
        for (int i = min; i <= max; i++)
        {
            input.Add(i);
        }
        var r = new Random(DateTime.UtcNow.Millisecond);

        if (count == 0)
        {
            count = inputCount;
        }

        var res = new List<int>(inputCount);
            
        for (int i = 0; i < count; i++)
        {
            var p = r.Next(input.Count);
            res.Add(input[p]);
            input.RemoveAt(p);
        }

        return res;
    }

    /// <summary>
    /// Modifying multiple consecutive pages should trigger a single write on disk
    /// </summary>
    /// <remarks>
    /// This statement is valid only for an unfragmented/empty file (two consecutive DiskPages should have consecutive MemPages) and can't be kept
    ///  when the memory cache is being pressured.
    /// </remarks>
    [Test]
    unsafe public void SequentialWrites()
    {
        // Write
        {
            using var scope = _serviceProvider.CreateScope();
            using var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            var metrics = pmmf.GetMetrics();
            var pageWrittenCount = metrics.PageWrittenToDiskCount;
            var writtenIOCount = metrics.WrittenOperationCount;
            var cs = pmmf.CreateChangeSet();
            pmmf.RequestPage(10, true, out var p1);    // The page 10, 11 and 12 will also be consecutive in the memory cache, 
            pmmf.RequestPage(11, true, out var p2);    //  allowing a single write
            pmmf.RequestPage(12, true, out var p3);
            pmmf.RequestPage(14, true, out var p4);
            var a = p1.WholePage.Cast<byte, int>();
            a[0] = 10;
            cs.Add(p1);
                
            a = p2.WholePage.Cast<byte, int>();
            a[0] = 11;
            cs.Add(p2);
                
            a = p3.WholePage.Cast<byte, int>();
            a[0] = 12;
            cs.Add(p3);
            
            a = p4.WholePage.Cast<byte, int>();
            a[0] = 14;
            cs.Add(p4);
            
            p1.Dispose();
            p2.Dispose();
            p3.Dispose();
            p4.Dispose();
            
            cs.SaveChanges();
            Assert.That(metrics.PageWrittenToDiskCount, Is.EqualTo(pageWrittenCount+4));
            Assert.That(metrics.WrittenOperationCount, Is.EqualTo(writtenIOCount+2));
        }

        // Check read
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            pmmf.RequestPage(10, true, out var p1);
            pmmf.RequestPage(11, true, out var p2);
            pmmf.RequestPage(12, true, out var p3);
            pmmf.RequestPage(14, true, out var p4);

            var a = p1.WholePage.Cast<byte, int>();
            Assert.That(a[0], Is.EqualTo(10), "Page 10 should be 10");

            var b = p2.WholePage.Cast<byte, int>();
            Assert.That(b[0], Is.EqualTo(11), "Page 11 should be 11");
            
            var c = p3.WholePage.Cast<byte, int>();
            Assert.That(c[0], Is.EqualTo(12), "Page 12 should be 12");
            
            var d = p4.WholePage.Cast<byte, int>();
            Assert.That(d[0], Is.EqualTo(14), "Page 14 should be 14");
            
            p1.Dispose();
            p2.Dispose();
            p3.Dispose();
            p4.Dispose();
        }
    }

    struct OPInfo
    {
        public int PageId;
        public bool ReadOnly;
        public int ExpectedValue;
    }

    [Test]
    [Property("MemPageCount", 1024)]
    unsafe public void ReliabilityTest()
    {
        var cacheFactor = 0.75f;   // This is nasty...we are going to have a lot of cache miss...
        var frameCount = 50;
        var opsPerFrame = 1000;
        var readWriteRatio = 0.75f;

        // Size configured in the Property attribute above, right now it's 8 pages cached, which is vicious because
        //  my actual computer has more thread, which means multiple thread compete for the same memory page.
        var cacheSize = _serviceProvider.GetRequiredService<IOptions<PagedMMFOptions>>().Value.DatabaseCacheSize;
        var pagesCount = (int)(cacheSize * cacheFactor) / PagedMMF.PageSize;
        var coreCount = Environment.ProcessorCount / 2;
        pagesCount = pagesCount / coreCount * coreCount;                        // Make sure we have a multiple of the core count

        // Generate IO ops for all the frames
        var frames = new List<List<OPInfo>>(frameCount);

        // Initialize the Pages access scenario
        var rand = new Random(DateTime.UtcNow.Millisecond);
        var range = (int)(1.0f / (1.0f - readWriteRatio));
        var readCut = (int)(readWriteRatio / (1.0f - readWriteRatio));
        var values = new Dictionary<int, int>(pagesCount);
        for (int i = 0; i < pagesCount; i++)
        {
            values.Add(i, i);
        }

        int trueOpsCount = Math.Min(opsPerFrame, pagesCount);
        for (int i = 0; i < frameCount; i++)
        {
            var ops = new List<OPInfo>(opsPerFrame);
            int opsCount = trueOpsCount;
            var ioPages = GenerateRandomAccess(0, pagesCount-1, opsCount);
            for (int j = 0; j < opsCount; j++)
            {
                var ro = rand.Next(0, range) < readCut;
                var pageId = ioPages[j];
                if (ro == false)
                {
                    ++values[pageId];
                }
                var curValue = values[pageId];
                ops.Add(new OPInfo{ PageId = pageId, ReadOnly = ro, ExpectedValue = curValue});
            }
            frames.Add(ops);
        }

        var ranges = new ConcurrentBag<(int, int)>();
        {
            var heapCount = pagesCount / coreCount;
            var remaining = pagesCount;
            for (int i = 0; i < coreCount; i++)
            {
                ranges.Add((i * heapCount, (remaining < heapCount) ? remaining : heapCount));
                remaining -= heapCount;
            }
        }
        
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            // Setup initial value of each page
            var sw = new Stopwatch();
            sw.Start();
        
            Parallel.ForEach(Enumerable.Range(0, coreCount), i =>
            {
                if (ranges.TryTake(out var operation) == false)
                {
                    return;
                }

                var firstPageIndex = operation.Item1;
                var heapCount = operation.Item2;

                var cs = pmmf.CreateChangeSet();
                for (int j = 0; j < heapCount; j++)
                {
                    var pageIndex = firstPageIndex + j;
                    pmmf.RequestPage(pageIndex, true, out var a);
                    cs.Add(a);

                    var dest = a.WholePage.Cast<byte, int>();
                    dest[0] = pageIndex;
                    a.Dispose();
                }
                cs.SaveChanges();
            });
            
            var di = pmmf.GetMetrics();
            Console.WriteLine($"Generated file in {sw.ElapsedMilliseconds}ms, Write counts: {di.PageWrittenToDiskCount}, Generated a total of {frameCount*trueOpsCount} Pages operations");
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
            var tm = scope.ServiceProvider.GetRequiredService<TimeManager>();

            // Check the initial page of each page
            for (int i = 0; i < pagesCount; i++)
            {
                pmmf.RequestPage(i, false, out var a);

                var dest = a.WholePage.Cast<byte, int>();
                var localI = i;
                Assert.That(dest[0], Is.EqualTo(i), () => $"Bad DiskPageId {localI}");
            
                a.Dispose();
            }

            // Simulate accesses
            for (int curF = 0; curF < frameCount; curF++)
            {
                var curFrame = tm.ExecutionFrame;

                // Console.WriteLine($"\r\n************** Simulating Frame {curFrame} ************** \r\n");
                var frameInfo = frames[curF];
                Parallel.ForEach(frameInfo, info =>
                {
                    var ro = info.ReadOnly;
                    if (ro)
                    {
                        // Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Check Page {info.PageId} is {info.ExpectedValue}");

                        pmmf.RequestPage(info.PageId, false, out var a);
                        int actual = a.WholePage.Cast<byte, int>()[0];

                        // _logger.LogCritical("Check Page {PageId} has Value {ExpectedValue} and has {value}", info.PageId, info.ExpectedValue, actual);
                        Assert.That(actual, Is.EqualTo(info.ExpectedValue), $"Frame {curFrame}, Page {info.PageId} should be {info.ExpectedValue} but is {actual}");
                    
                        a.Dispose();
                    }
                    else
                    {
                        var cs = pmmf.CreateChangeSet();
                        // Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Page {info.PageId} bumped to {info.ExpectedValue}");

                        pmmf.RequestPage(info.PageId, true, out var a);
                        cs.Add(a);
                        var pa = a.WholePage.Cast<byte, int>();
                        ++pa[0]; // Bump the value
                        var actual = pa[0];
                    
                        // _logger.LogCritical("Bump Page {PageId} to {value}, expected {ExpectedValue}", info.PageId, *pa, info.ExpectedValue);
                        Assert.That(actual, Is.EqualTo(info.ExpectedValue), $"Frame {curFrame}, Page {info.PageId} should be {info.ExpectedValue} but is {actual}");
                    
                        a.Dispose();
                        cs.SaveChanges();
                    }
                });

                tm.BumpFrame();
            }
        }
    }
}