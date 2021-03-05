using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.BPTree;

namespace Typhon.Engine.Tests.Database_Engine
{
    class BtreeTests
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
    }
}
