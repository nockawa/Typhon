using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class PagedFileTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private PagedFile _pagedFile;
    private DatabaseConfiguration _configuration;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_db";

    [SetUp]
    public async Task Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! : PagedFile.DefaultMemPageCount;
        dcs *= PagedFile.PageSize;

#if DEBUG
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override(typeof(LogicalSegmentManager).FullName, LogEventLevel.Verbose)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithCurrentFrame()
            .WriteTo.Seq("http://localhost:5341", compact: true)
            .CreateLogger();
#endif

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddTyphon(builder =>
            {
                builder.ConfigureDatabase(dc =>
                {
                    dc.DatabaseName = CurrentDatabaseName;
                    dc.RecreateDatabase = false;
                    dc.DeleteDatabaseOnDispose = true;
                    dc.DatabaseCacheSize = (ulong)dcs;
                    dc.PagesDebugPattern = false;
                });
            })

            .AddLogging(builder =>
            {
#if DEBUG
                builder.AddSerilog(dispose: true);
#endif
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();

        _pagedFile = _serviceProvider.GetRequiredService<PagedFile>();
        _configuration = _serviceProvider.GetRequiredService<IConfiguration<DatabaseConfiguration>>().Value;

        await _pagedFile.InitializeAsync();
    }
    

    [TearDown]
    public async Task TearDown()
    {
        _pagedFile?.DisposeAsync();
        Log.CloseAndFlush();
    }


    private const int CreateFillPagesThenReadThemMemPageCount = 256;
    [Test]
    [Property("MemPageCount", CreateFillPagesThenReadThemMemPageCount)]
    public async Task CreateFillPagesThenReadThem()
    {
        const int pageCount = 128;
        
        Assert.That(_pagedFile.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
        
        // Fill pages with data
        {
            var now = DateTime.UtcNow;
            var cs = _pagedFile.CreateChangeSet();
            for (int i = 0; i < pageCount; i++)
            {
                using var accessor = await _pagedFile.RequestPageAsync(i, true);
                var ispan = accessor.PageRawData.Cast<byte, int>();
                ispan[0] = i; // Set first element
                ispan[^1] = i; // Set last element
                cs.Add(accessor);
            }
            Console.WriteLine($"Average time to fill one page {((DateTime.UtcNow - now).TotalSeconds / pageCount).FriendlyTime()}");

            Assert.That(_pagedFile.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount - pageCount));
            now = DateTime.UtcNow;
            await cs.SaveChangesAsync();
            
            Console.WriteLine($"Average time to save one page {((DateTime.UtcNow - now).TotalSeconds / pageCount).FriendlyTime()}");
        }
        
        Assert.That(_pagedFile.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));

        // Reset the instance
        await _pagedFile.ResetAsync();

        Assert.That(_pagedFile.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
        long totalRequest = 0;
        long totalAccess = 0;
        
        // The file exists and has content, load it
        {
            for (int i = 0; i < pageCount; i++)
            {
                var now = DateTime.UtcNow;
                
                using var accessor = await _pagedFile.RequestPageAsync(i, true);
                totalRequest += (DateTime.UtcNow - now).Ticks;
                
                now = DateTime.UtcNow;
                var content = accessor.PageRawData.Cast<byte, int>();
                totalAccess += (DateTime.UtcNow - now).Ticks;
                
                Assert.That(content[0], Is.EqualTo(i), $"Page {i} content mismatch after reset.");
                Assert.That(content[^1], Is.EqualTo(i), $"Page {i} content mismatch after reset.");
            }
        }
        
        Assert.That(_pagedFile.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
        Console.WriteLine($"Average Request Time: {TimeSpan.FromTicks(totalRequest / pageCount).TotalSeconds.FriendlyTime()}, Average Access Time: {TimeSpan.FromTicks(totalAccess / pageCount).TotalSeconds.FriendlyTime()}");
    }

    [Test]
    [Property("MemPageCount", 4)]
    public void MemPageStarvation()
    {
        var waitTask0PagesCreated = new ManualResetEventSlim();
        
        
        var tasks = new Task[2];
        tasks[0] = Task.Run(async () =>
        {
            var accessors = new PageAccessor[4];
            for (int i = 0; i < 4; i++)
            {
                accessors[i] = await _pagedFile.RequestPageAsync(i, false);
            }
            waitTask0PagesCreated.Set();
            
            for (int i = 0; i < 4; i++)
            {
                Thread.Sleep(500);
                accessors[i].Dispose();
            }
            
            
        });

        tasks[1] = Task.Run(async () =>
        {
            waitTask0PagesCreated.Wait();

            var accessors = new PageAccessor[4];
            for (int i = 0; i < 4; i++)
            {
                var now = DateTime.UtcNow;
                var pa = await _pagedFile.RequestPageAsync(i + 4, false);
                
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
}