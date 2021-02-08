using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Persistence_Layer
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

            var serviceCollection = new ServiceCollection();
            _serviceCollection = serviceCollection;
            _serviceCollection.AddTyphon(builder =>
            {
                builder.ConfigureDatabase(dc =>
                {
                    dc.DatabaseName = CurrentDatabaseName;
                    dc.RecreateDatabase = true;
                    dc.DeleteDatabaseOnDispose = true;
                    dc.DatabaseCacheSize = (ulong)dcs;
                });
            });

            _serviceProvider = _serviceCollection.BuildServiceProvider();

            _vdm = _serviceProvider.GetRequiredService<VirtualDiskManager>();
            _tm = _serviceProvider.GetRequiredService<TimeManager>();
            _configuration = _serviceProvider.GetRequiredService<IConfiguration<DatabaseConfiguration>>().Value;
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

            using (_vdm.RequestPageReadOnly(0))
            {
                debug = _vdm.GetDebugInfo();
                Assert.That(debug.MemPageCacheHit, Is.GreaterThan(cacheHit));
            }

            _vdm.ResetDiskManager();

            using (var pa = _vdm.RequestPageReadOnly(0))
            {
                unsafe
                {
                    var h = (RootFileHeader*)pa.Page;
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
                using var p1 = _vdm.RequestPageReadWrite(10);
                using var p2 = _vdm.RequestPageReadWrite(11);
                using var p3 = _vdm.RequestPageReadWrite(12);
                var a = (int*)p1.Page;
                *a = 1;
                
                a = (int*)p2.Page;
                *a = 2;
                
                a = (int*)p3.Page;
                *a = 3;
            }

            _vdm.FlushToDiskAsync(true, out _).Wait();

            Assert.That(_vdm.GetDebugInfo().WriteToDiskCount, Is.EqualTo(writeCount+1));
        }

        [Test]
        unsafe public void MRU()
        {
            var debug = _vdm.GetDebugInfo();

            // Frame 1
            {
                using var p1 = _vdm.RequestPageReadWrite(10);
                using var p2 = _vdm.RequestPageReadWrite(11);
                using var p3 = _vdm.RequestPageReadWrite(12);
                var a = (int*)p1.Page;
                *a = 1;
                
                a = (int*)p2.Page;
                *a = 2;
                
                a = (int*)p3.Page;
                *a = 3;
            }

            _vdm.FlushToDiskAsync(true, out _).Wait();

            // Frame 2
            _tm.BumpFrame();
            _vdm.GetPageInfoOf(11, out var pi2);
            var flushFrame = pi2->PreviousUsedFrame;

            {
                using var p2 = _vdm.RequestPageReadOnly(11);
            }

            _vdm.FlushToDiskAsync(true, out _).Wait();
            Assert.That(pi2->PreviousUsedFrame, Is.EqualTo(flushFrame+1));

            // Frame 3
            _tm.BumpFrame();

            {
                using var p1 = _vdm.RequestPageReadWrite(10);
                using var p2 = _vdm.RequestPageReadWrite(11);
            }
            _vdm.FlushToDiskAsync(true, out _).Wait();
            
            _vdm.GetPageInfoOf(10, out var pi1);

        }

        [TestCase(16)]
        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        unsafe public void MemMoveTest(int sizeMb)
        {
            var size = sizeMb * 1024 * 1024;
            var srcA = new byte[size];
            var dstA = new byte[size];
            var dst = new Memory<byte>(dstA);
            var sw = new Stopwatch();

            for (int i = 0; i < 10; i++)
            {
                sw.Start();
                srcA.CopyTo(dst);
                sw.Stop();
                Console.WriteLine($"Copy time {sw.ElapsedMilliseconds}ms, {(size/sw.Elapsed.TotalSeconds)/(1024*1024)}MiB/sec");
            }
        }

        struct OPInfo
        {
            public uint PageId;
            public bool ReadOnly;
            public int ExpectedValue;
        }

        [Test]
        [Property("CacheSize", 8*8*1024)]
        unsafe public void ReliabilityTest()
        {
            var cacheFactor = 4;   // This is nasty...we are going to have a lot of cache miss...
            var frameCount = 20;
            var opsPerFrame = 1000;
            var readWriteRatio = 0.75f;

            // Size configured in the Property attribute above, right now it's 8 pages cached, which is vicious because
            //  my actual computer has more thread, which means multiple thread compete for the same memory page.
            var cacheSize = _configuration.DatabaseCacheSize;
            var pagesCount = ((int)cacheSize* cacheFactor) / (int)VirtualDiskManager.PageSize;

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

            for (int i = 0; i < frameCount; i++)
            {
                var ops = new List<OPInfo>(opsPerFrame);
                int opsCount = Math.Min(opsPerFrame, pagesCount);
                var ioPages = GenerateRandomAccess(1, pagesCount, opsCount);
                for (int j = 0; j < opsCount; j++)
                {
                    var ro = rand.Next(0, range) < readCut;
                    uint pageId = (uint)ioPages[j];
                    var curValue = values[pageId];
                    ops.Add(new OPInfo{ PageId = pageId, ReadOnly = ro, ExpectedValue = curValue});
                    if (ro == false)
                    {
                        ++values[pageId];
                    }
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
                    using var a = _vdm.RequestPageReadWrite((uint)i);

                    var dest = (int*)a.Page;
                    *dest = i;
                }
            }
            else
            {
                Parallel.ForEach(Enumerable.Range(1, pagesCount), i =>
                {
                    using var a = _vdm.RequestPageReadWrite((uint)i);

                    var dest = (int*)a.Page;
                    *dest = i;
                });
            }

            _vdm.FlushToDiskAsync(false, out _).Wait();

            var di = _vdm.GetDebugInfo();
            Console.WriteLine($"Generated file in {sw.ElapsedMilliseconds}ms, Write counts: {di.WriteToDiskCount}, Total Pages {di.PagesWrittenCount}, Avg Pages Count per write: {di.PagesWrittenCount/(float)di.WriteToDiskCount}");

            // Reset the Disk Manager to start checking from a brand new one
            _vdm.ResetDiskManager();

            // Check the initial page of each page
            for (int i = 1; i < pagesCount; i++)
            {
                using var a = _vdm.RequestPageReadOnly((uint)i);

                var dest = (int*)a.Page;
                Assert.That(*dest, Is.EqualTo(i), () => $"Bad DiskPageId {i}");
            }

            // Simulate accesses
            for (int curF = 0; curF < frameCount; curF++)
            {
                Console.WriteLine($"\r\n************** Simulating Frame {curF} ************** \r\n");
                var frameInfo = frames[curF];
                Parallel.ForEach(frameInfo, info =>
                {
                    var ro = info.ReadOnly;
                    if (ro)
                    {
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Check Page {info.PageId} is {info.ExpectedValue}");

                        using var a = _vdm.RequestPageReadOnly(info.PageId);
                        int actual = *(int*)a.Page;
                        Assert.That(actual, Is.EqualTo(info.ExpectedValue), $"Frame {curF}, Page {info.PageId} should be {info.ExpectedValue} but is {actual}");
                    }
                    else
                    {
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Page {info.PageId} bumped to {info.ExpectedValue+1}");

                        using var a = _vdm.RequestPageReadWrite(info.PageId);
                        var pa = (int*)a.Page;
                        ++*pa;
                    }
                });

                _vdm.FlushToDiskAsync(true, out _).Wait();
            }
        }


        [TearDown]
        public void TearDown()
        {
            _vdm?.Dispose();
            _vdm = null;
        }

    }
}
