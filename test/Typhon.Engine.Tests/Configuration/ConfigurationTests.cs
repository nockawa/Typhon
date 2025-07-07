using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

public class ConfigurationTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private DatabaseEngine _databaseEngine;

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection.AddTyphon(builder =>
        {
            builder.ConfigureDatabase(dc =>
            {
                dc.DatabaseName = $"{TestContext.CurrentContext.Test.Name}_database";
                dc.DeleteDatabaseOnDispose = true;
            });
        });

        _serviceProvider = _serviceCollection.BuildServiceProvider();

        _databaseEngine = _serviceProvider.GetRequiredService<DatabaseEngine>();

    }

    [TearDown]
    public void TearDown()
    {
        _databaseEngine?.Dispose();
        _databaseEngine = null;
    }

}