using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using System;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class PagedFileTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private PagedFile _pagedFile;
    private DatabaseConfiguration _configuration;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public async Task Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize")! : (int)PagedMemoryMappedFile.MinimumCacheSize;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override(typeof(LogicalSegmentManager).FullName, LogEventLevel.Verbose)
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


    [Test]
    public void TestA()
    {
        
    }
}