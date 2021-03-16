using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests
{
    class VirtualDiskManagerTests
    {
        private IServiceProvider _serviceProvider;
        private ServiceCollection _serviceCollection;
        private VirtualDiskManager _vdm;
        private TimeManager _tm;
        private DatabaseConfiguration _configuration;

        private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

        [SetUp]
        public void Setup()
        {
            var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
            var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize") : (int)VirtualDiskManager.MinimumCacheSize;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .Enrich.WithCurrentFrame()
                .WriteTo.Seq("http://localhost:5341", compact: true)
                .CreateLogger();

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
                    builder.AddSerilog(dispose: true);
                    builder.AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.IncludeScopes = true;
                        options.TimestampFormat = "mm:ss.fff ";
                    });
                });

            _serviceProvider = _serviceCollection.BuildServiceProvider();

            _vdm = _serviceProvider.GetRequiredService<VirtualDiskManager>();
            _tm = _serviceProvider.GetRequiredService<TimeManager>();
            _configuration = _serviceProvider.GetRequiredService<IConfiguration<DatabaseConfiguration>>().Value;

            _vdm.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            Thread.Sleep(500);
            _vdm?.Dispose();
            _vdm = null;

            Log.CloseAndFlush();
        }

        private List<int> GenerateRandomAccess(int min, int max, int count=0)
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

        [Test]
        public void InitializationTest()
        {
            var debug = _vdm.GetDebugInfo();
            var cacheHit = debug.MemPageCacheHit;

            using (_vdm.RequestPageShared(0))
            {
                debug = _vdm.GetDebugInfo();
                Assert.That(debug.MemPageCacheHit, Is.GreaterThan(cacheHit));
            }

            _vdm.ResetDiskManager();

            using (var pa = _vdm.RequestPageShared(0))
            {
                unsafe
                {
                    var h = (RootFileHeader*)pa.PageAddress;
                    Assert.That(h->HeaderSignatureString, Is.EqualTo(VirtualDiskManager.HeaderSignature));
                    Assert.That(h->DatabaseNameString, Is.EqualTo(CurrentDatabaseName));
                }
            }
        }

        [Test]
        unsafe public void SequentialWrites()
        {
            var debug = _vdm.GetDebugInfo();
            var writeCount = debug.WriteToDiskCount;

            {
                using var p1 = _vdm.RequestPageExclusive(10);
                using var p2 = _vdm.RequestPageExclusive(11);
                using var p3 = _vdm.RequestPageExclusive(12);
                var a = (int*)p1.PageAddress;
                p1.SetPageDirty();
                *a = 1;
                
                a = (int*)p2.PageAddress;
                p2.SetPageDirty();
                *a = 2;
                
                a = (int*)p3.PageAddress;
                p3.SetPageDirty();
                *a = 3;
            }

            _vdm.FlushToDiskAsync(true).Wait();

            Assert.That(_vdm.GetDebugInfo().WriteToDiskCount, Is.EqualTo(writeCount+1));
        }

        struct OPInfo
        {
            public uint PageId;
            public bool ReadOnly;
            public int ExpectedValue;
        }

        [Test]
        [Property("CacheSize", 8*1024*1024)]
        unsafe public void ReliabilityTest()
        {
            var cacheFactor = 4f;   // This is nasty...we are going to have a lot of cache miss...
            var frameCount = 50;
            var opsPerFrame = 1000;
            var readWriteRatio = 0.75f;

            // Size configured in the Property attribute above, right now it's 8 pages cached, which is vicious because
            //  my actual computer has more thread, which means multiple thread compete for the same memory page.
            var cacheSize = _configuration.DatabaseCacheSize;
            var pagesCount = (int)(cacheSize* cacheFactor) / VirtualDiskManager.PageSize;

            // Generate IO ops for all the frames
            var frames = new List<List<OPInfo>>(frameCount);

            // Initialize the Pages access scenario
            var rand = new Random(DateTime.UtcNow.Millisecond);
            var range = (int)(1.0f / (1.0f - readWriteRatio));
            var readCut = (int)(readWriteRatio / (1.0f - readWriteRatio));
            var values = new Dictionary<uint, int>(pagesCount);
            for (int i = 0; i < pagesCount; i++)
            {
                values.Add((uint)i+1, i+1);
            }

            int trueOpsCount = Math.Min(opsPerFrame, pagesCount);
            for (int i = 0; i < frameCount; i++)
            {
                var ops = new List<OPInfo>(opsPerFrame);
                int opsCount = trueOpsCount;
                var ioPages = GenerateRandomAccess(1, pagesCount, opsCount);
                for (int j = 0; j < opsCount; j++)
                {
                    var ro = rand.Next(0, range) < readCut;
                    uint pageId = (uint)ioPages[j];
                    if (ro == false)
                    {
                        ++values[pageId];
                    }
                    var curValue = values[pageId];
                    ops.Add(new OPInfo{ PageId = pageId, ReadOnly = ro, ExpectedValue = curValue});
                }
                frames.Add(ops);
            }

            // Setup initial value of each page
            var sw = new Stopwatch();
            sw.Start();

            var singleThreadCreate = false;

            if (singleThreadCreate)
            {
                for (int i = 1; i < pagesCount; i++)
                {
                    using var a = _vdm.RequestPageExclusive((uint)i);
                    a.SetPageDirty();

                    var dest = (int*)a.PageAddress;
                    *dest = i;
                }
            }
            else
            {
                Parallel.ForEach(Enumerable.Range(1, pagesCount), i =>
                {
                    using var a = _vdm.RequestPageExclusive((uint)i);
                    a.SetPageDirty();

                    var dest = (int*)a.PageAddress;
                    *dest = i;
                });
            }

            _vdm.FlushToDiskAsync(false).Wait();

            var di = _vdm.GetDebugInfo();
            Console.WriteLine($"Generated file in {sw.ElapsedMilliseconds}ms, Write counts: {di.WriteToDiskCount}, Total Pages {di.PagesWrittenCount}, Avg Pages Count per write: {di.PagesWrittenCount/(float)di.WriteToDiskCount}, Generated a total of {frameCount*trueOpsCount} Pages operations");

            // Reset the Disk Manager to start checking from a brand new one
            _vdm.ResetDiskManager();

            // Check the initial page of each page
            for (int i = 1; i < pagesCount; i++)
            {
                using var a = _vdm.RequestPageShared((uint)i);

                var dest = (int*)a.PageAddress;
                Assert.That(*dest, Is.EqualTo(i), () => $"Bad DiskPageId {i}");
            }

            var log = Log.Logger;

            // Simulate accesses
            for (int curF = 0; curF < frameCount; curF++)
            {
                var curFrame = _tm.ExecutionFrame;

                Console.WriteLine($"\r\n************** Simulating Frame {curFrame} ************** \r\n");
                var frameInfo = frames[curF];
                Parallel.ForEach(frameInfo, info =>
                {
                    var ro = info.ReadOnly;
                    if (ro)
                    {
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Check Page {info.PageId} is {info.ExpectedValue}");

                        using var a = _vdm.RequestPageShared(info.PageId);
                        int actual = *(int*)a.PageAddress;

                        Log.Fatal("Check Page {PageId} has Value {ExpectedValue} and has {value}", info.PageId, info.ExpectedValue, actual);
                        Assert.That(actual, Is.EqualTo(info.ExpectedValue), $"Frame {curFrame}, Page {info.PageId} should be {info.ExpectedValue} but is {actual}");
                    }
                    else
                    {
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Page {info.PageId} bumped to {info.ExpectedValue}");

                        using var a = _vdm.RequestPageExclusive(info.PageId);
                        a.SetPageDirty();
                        var pa = (int*)a.PageAddress;
                        ++*pa;
                        Log.Fatal("Bump Page {PageId} to {value}, expected {ExpectedValue}", info.PageId, *pa, info.ExpectedValue);
                    }
                });

                _vdm.FlushToDiskAsync(true).Wait();
                _tm.BumpFrame();
            }
        }
    }
}
