// unset

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Tests
{
    class LogicalSegmentTests
    {
        private IServiceProvider _serviceProvider;
        private ServiceCollection _serviceCollection;
        private LogicalSegmentManager _lsm;
        private DatabaseConfiguration _configuration;

        private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

        [SetUp]
        public void Setup()
        {
            var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
            var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize") : (int)VirtualDiskManager.MinimumCacheSize;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override(typeof(LogicalSegmentManager).FullName, LogEventLevel.Debug)
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
                        dc.PagesDebugPattern = true;
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
                    builder.SetMinimumLevel(LogLevel.Information);
                });

            _serviceProvider = _serviceCollection.BuildServiceProvider();

            _lsm = _serviceProvider.GetRequiredService<LogicalSegmentManager>();
            _configuration = _serviceProvider.GetRequiredService<IConfiguration<DatabaseConfiguration>>().Value;

            _lsm.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            Thread.Sleep(500);
            _lsm?.Dispose();

            Log.CloseAndFlush();
        }


        [Test]
        public void CreateSegment()
        {
            var s0 = _lsm.AllocateSegment(PageBlockType.None, 10);
            var s1 = _lsm.AllocateSegment(PageBlockType.None, 100);
            _lsm.DeleteSegment(s0);
            var s2 = _lsm.AllocateSegment(PageBlockType.None, 10010);

            var s3 = _lsm.AllocateSegment(PageBlockType.None, 1000);

            _lsm.DeleteSegment(s2);
            _lsm.DeleteSegment(s1);
            _lsm.DeleteSegment(s3);
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

            var s0 = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(ChunkA));

            using var mo = s0.AllocateChunks(2000, false);

            using var ca = s0.GetChunkReadWriteRandomAccessor(4);

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
            const int itemCount = 1024*3;

            var s = _lsm.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);

            var vsb = new VariableSizedBufferSegment<long>(s);


            var id0 = vsb.AllocateBuffer();

            for (int i = 0; i < itemCount; i++)
            {
                vsb.AddElement(id0, 1234);
            }

            var loopCount = 0;
            var ba = vsb.GetReadOnlyAccessor(id0);
            do
            {
                var elements = ba.Elements;
                var c = elements.Length;
                for (int i = 0; i < c; i++)
                {
                    Assert.That(elements[i], Is.EqualTo(1234));
                    ++loopCount;
                }
            } while (ba.NextChunk());
            Assert.That(loopCount, Is.EqualTo(itemCount));
        }
    }
}