using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;
using Typhon.Engine.Tests;

namespace Typhon.Benchmark;

[SimpleJob(warmupCount: 3, iterationCount: 3)]
[JsonExporterAttribute.Full]
[JsonExporterAttribute.FullCompressed]
public class PagedVirtualMemoryFile_RandomAccess
{
    private ServiceCollection _serviceCollection;
    private ServiceProvider _serviceProvider;
    private PagedMemoryMappedFile _pmmf;
    private TimeManager _tm;

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

        _pmmf = _serviceProvider.GetRequiredService<PagedMemoryMappedFile>();
        _pmmf.Initialize();
        
        _tm = _serviceProvider.GetRequiredService<TimeManager>();

    }

    struct OPInfo
    {
        public uint PageId;
        public bool ReadOnly;
    }

    // [Params(0.5f, 1f, 4f)]
    [Params(0.5f)]
    public float CacheFactor { get; set; }

    // [Params(10, 100, 1000)]
    [Params(100)]
    public int OpsPerFrame { get; set; }
    
    [Benchmark]
    unsafe public void TestRandomAccess()
    {
        var cacheFactor = CacheFactor;
        var frameCount = 80;
        var opsPerFrame = OpsPerFrame;
        var readWriteRatio = 0.75f;

        // Size configured in the Property attribute above, right now it's 8 pages cached, which is vicious because
        //  my actual computer has more thread, which means multiple thread compete for the same memory page.
        var cacheSize = 8 * 1024 * 1024;
        var pagesCount = (int)(cacheSize * cacheFactor) / PagedMemoryMappedFile.PageSize;

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
            var ioPages = PagedMemoryMappedFileTests.GenerateRandomAccess(1, pagesCount, opsCount);
            for (int j = 0; j < opsCount; j++)
            {
                var ro = rand.Next(0, range) < readCut;
                uint pageId = (uint)ioPages[j];
                if (ro == false)
                {
                    ++values[pageId];
                }
                ops.Add(new OPInfo{ PageId = pageId, ReadOnly = ro });
            }
            frames.Add(ops);
        }
        
        // Simulate accesses
        for (int curF = 0; curF < frameCount; curF++)
        {
            var curFrame = _tm.ExecutionFrame;

            //Console.WriteLine($"\r\n************** Simulating Frame {curFrame} ************** \r\n");
            var frameInfo = frames[curF];
            Parallel.ForEach(frameInfo, info =>
            {
                var ro = info.ReadOnly;
                if (ro)
                {
                    using var a = _pmmf.RequestPageShared(info.PageId);
                    int actual = *(int*)a.PageAddress;
                }
                else
                {
                    using var a = _pmmf.RequestPageExclusive(info.PageId);
                    a.SetPageDirty();
                    var pa = (int*)a.PageAddress;
                    var actual = ++*pa;
                }
            });

            _pmmf.FlushToDiskAsync(true).Wait();
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