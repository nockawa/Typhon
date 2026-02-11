using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

public class DatabaseSchemaTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize")! : (int)PagedMMF.MinimumCacheSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(options =>
            {
            });
        
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    public struct DBObject
    {
        
    }
    [Test]
    public void TestDatabaseSchema()
    {
        var dc = new DatabaseDefinitions();

        dc.CreateFromAccessor<FieldR1>();

        dc.CreateComponentBuilder("DBObject", 1)
            .WithPOCO<DBObject>()
            .WithField<String64>(-1, "DBObjectTypeName", 0).IsStatic()
            .WithField<long>(0, "ID", 0)
            .Build();
    }

    [Test]
    unsafe public void TestSchemaStore()
    {

        //Assert.That(_dbe.IsInitialized, Is.True);

    }
}