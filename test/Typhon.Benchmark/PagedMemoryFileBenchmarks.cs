using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Typhon.Engine;
using Typhon.Engine.Tests;

namespace Typhon.Benchmark;

[SimpleJob(warmupCount: 3, iterationCount: 3)]
[JsonExporterAttribute.Full]
public class PagedMemoryFileBenchmarks
{
    private const ulong CacheSize = 128 * PMMF.PageSize;
    
    private ServiceCollection _serviceCollection;
    private ServiceProvider _serviceProvider;
    private PMMF _pmmf;
    private TimeManager _tm;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        /*
        _serviceCollection
            .AddTyphonSingleton(builder =>
            {
                builder.ConfigureDatabase(dc =>
                {
                    dc.DatabaseName = $"BenchDatabase{DateTime.UtcNow.Ticks}";
                    dc.RecreateDatabase = true;
                    dc.DeleteDatabaseOnDispose = true;
                    dc.DatabaseCacheSize = CacheSize;
                });
            });
        */
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

        _pmmf = _serviceProvider.GetRequiredService<PMMF>();
        /*
        _pmmf.Initialize();
        */
        
        _tm = _serviceProvider.GetRequiredService<TimeManager>();

    }

    struct OpInfo
    {
        public int FilePageIndex;
        public uint Value;
        public bool ReadOnly;
    }

    [Benchmark]
    unsafe public void TestRandomAccess()
    {
        var frameCount = 1;
        var readWriteRatio = 0.75f;
        var pagesCount = (int)(CacheSize * 8 / PMMF.PageSize);
        var opsPerFrame = 1024;

        // Generate IO ops for all the frames
        var frames = new List<List<OpInfo>>(frameCount);

        // Initialize the Pages access scenario
        var rand = new Random(123);
        var range = (int)(1.0f / (1.0f - readWriteRatio));
        var readCut = (int)(readWriteRatio / (1.0f - readWriteRatio));

        for (int i = 0; i < frameCount; i++)
        {
            var ops = new List<OpInfo>(opsPerFrame);
            for (int j = 0; j < opsPerFrame; j++)
            {
                var ro = rand.Next(0, range) < readCut;
                var pageId = rand.Next(0, pagesCount);
                ops.Add(new OpInfo{ FilePageIndex = pageId, ReadOnly = ro });
            }
            frames.Add(ops);
        }
        
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
                    _pmmf.RequestPage(info.FilePageIndex, false, out var a);
                    using (a)
                    {
                        var actual = *(uint*)a.PageAddress;
                        info.Value += actual;
                    }
                }
                else
                {
                    _pmmf.RequestPage(info.FilePageIndex, true, out var a);
                    using (a)
                    {
                        a.SetPageDirty();
                        var pa = (uint*)a.PageAddress;
                        var actual = ++*pa;
                    }
                }
            });

            //_pmmf.FlushToDiskAsync(true).Wait();
            _tm.BumpFrame();
        }
    }
    
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _pmmf?.Dispose();
        _pmmf = null;
    }

    
}