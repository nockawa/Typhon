using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Threading;

namespace Typhon.Engine.Tests
{
    class DiskPageAllocatorTests
    {
        private IServiceProvider _serviceProvider;
        private ServiceCollection _serviceCollection;
        private DiskPageAllocator _dpa;
        private DatabaseConfiguration _configuration;
        private LogicalSegmentManager _lsm;

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

            _dpa = _serviceProvider.GetRequiredService<DiskPageAllocator>();
            _dpa.Initialize();

            _configuration = _serviceProvider.GetRequiredService<IConfiguration<DatabaseConfiguration>>().Value;

            _lsm = _serviceProvider.GetRequiredService<LogicalSegmentManager>();
            _lsm.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            Thread.Sleep(500);
            _dpa?.Dispose();
            _lsm?.Dispose();

            Log.CloseAndFlush();
        }

        [Test]
        public void FindNextUnsetL0Test()
        {
            var bitCount = 64 * 64 * 64 * 10;
            var pageCount = (int)Math.Ceiling((double)bitCount / (VirtualDiskManager.PageRawDataSize * 8));

            var seg = _lsm.AllocateSegment(PageBlockType.None, pageCount);

            var c = new DiskPageAllocator.BitmapL3(bitCount, seg);

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

            _lsm.DeleteSegment(seg);
        }

        [Test]
        public void FindNextUnsetL1Test()
        {
            var bitCount = 64 * 64 * 64 * 10;
            var pageCount = (int)Math.Ceiling((double)bitCount / (VirtualDiskManager.PageRawDataSize * 8));

            var seg = _lsm.AllocateSegment(PageBlockType.None, pageCount);

            var c = new DiskPageAllocator.BitmapL3(bitCount, seg);

            var index = -1;
            long mask = 0L;
            c.FindNextUnsetL1(ref index, ref mask);
            Assert.That(index, Is.EqualTo(0));

            c.SetL0(0);
            c.SetL0(128);
            index = -1;
            mask = 0L;
            c.FindNextUnsetL1(ref index, ref mask);
            Assert.That(index, Is.EqualTo(1));

            c.FindNextUnsetL1(ref index, ref mask);
            Assert.That(index, Is.EqualTo(3));


            _lsm.DeleteSegment(seg);
        }

        [Test]
        public void SetL1Test()
        {
            var bitCount = 64 * 64 * 64 * 10;
            var pageCount = (int)Math.Ceiling((double)bitCount / (VirtualDiskManager.PageRawDataSize * 8));

            var seg = _lsm.AllocateSegment(PageBlockType.None, pageCount);

            var c = new DiskPageAllocator.BitmapL3(bitCount, seg);

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



            _lsm.DeleteSegment(seg);
        }



        [Test]
        public void CreateSegment()
        {
            //var ls = _lsm.GetOrCreateSegment(2, PageBlockType.None, 10);

        }
    }
}
