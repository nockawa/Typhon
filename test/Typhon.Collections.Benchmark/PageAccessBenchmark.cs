// unset

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine;

namespace Typhon.Collections.Benchmark
{
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, targetCount: 3)]
    public class PageAccessBenchmark
    {
        private ServiceCollection _serviceCollection;
        private ServiceProvider _serviceProvider;
        private LogicalSegmentManager _lsm;
        private LogicalSegment _segment;

        [GlobalSetup]
        public void GlobalSetup()
        {
            var serviceCollection = new ServiceCollection();
            _serviceCollection = serviceCollection;
            _serviceCollection
                .AddTyphon(builder =>
                {
                    builder.ConfigureDatabase(dc =>
                    {
                        dc.DatabaseName = $"BenchDatabase{DateTime.UtcNow.Ticks}";
                        dc.RecreateDatabase = true;
                        dc.DeleteDatabaseOnDispose = true;
                        dc.DatabaseCacheSize = (ulong)32 * 1024 * 8192;
                    });
                });
            _serviceCollection.AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
            });
            _serviceProvider = _serviceCollection.BuildServiceProvider();

            _lsm = _serviceProvider.GetRequiredService<LogicalSegmentManager>();
            _lsm.Initialize();
            _segment = _lsm.AllocateSegment(PageBlockType.None, 10);

        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _lsm?.Dispose();
        }

        [Benchmark(Baseline = true)]
        public int BenchmarkSegmentManualAccess()
        {
            var v = 0;

            var length = _segment.Length;
            for (int i = 0; i < length; i++)
            {
                using var page = _segment.GetPageReadOnly(i);

                var rd = page.LogicalSegmentData.Cast<byte, int>();
                var c = rd.Length;
                for (int j = 0; j < c; j++)
                {
                    v |= rd[j];
                }
            }

            return v;
        }

        [Benchmark]
        public int BenchmarkSegmentForEachElement()
        {
            var v = 0;

            _segment.ForEachReadOnly((ref LogicalSegment.ReadOnlyEnumerator<int> e) =>
            {
                v |= e.Current;
                return true;
            });
            return v;
        }
    }

}