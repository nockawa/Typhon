using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests
{
    public class DatabaseSchemaTests
    {
        private IServiceProvider _serviceProvider;
        private ServiceCollection _serviceCollection;
        private DatabaseEngine _dbe;

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

            _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
            _dbe.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            _dbe?.Dispose();
            _dbe = null;

            Log.CloseAndFlush();
        }

        [Test]
        public void TestDatabaseSchema()
        {
            var dc = new DatabaseDefinitions();

            dc.CreateFromRowAccessor<FieldRow>();

            dc.CreateComponentBuilder("DBObject")
                .WithField(-1, "DBObjectTypeName", FieldType.String64, 0).IsStatic()
                .WithField(0, "ID", FieldType.Long, 0)
                .Build();
        }

        [Test]
        unsafe public void TestSchemaStore()
        {

            Assert.That(_dbe.IsInitialized, Is.True);

        }
    }
}
