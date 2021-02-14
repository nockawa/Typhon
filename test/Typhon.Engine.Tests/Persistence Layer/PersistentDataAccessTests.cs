using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests
{
    public class PersistentDataAccessTests
    {
        private const ulong DatabaseChunkSize = 128 * 1024 * 1024;
        private const int DatabaseFileChunkCount = 4;
        private IServiceProvider _serviceProvider;
        private ServiceCollection _serviceCollection;
        private PersistentDataAccess _pda;

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

            _pda = _serviceProvider.GetRequiredService<PersistentDataAccess>();

        }

        //[Test]
        //public void PageBlockViewTest()
        //{
        //    var view = _pda.GetPageBlockView(1, 1, false);
        //    Assert.That(view.PointerOffset, Is.EqualTo(PersistentDataAccess.PageSize));

        //    var pagesPerChunk = DatabaseChunkSize / PersistentDataAccess.PageSize;
        //    view = _pda.GetPageBlockView((uint) pagesPerChunk, 1, false);
        //    Assert.That(view.PointerOffset, Is.EqualTo(0));
        //}

        private List<int> GenerateRandomAccess(int min, int max, int count=0)
        {
            int inputCount = max - min + 1;
            var input = new List<int>(inputCount);
            for (int i = min; i <= max; i++)
            {
                input.Add(i);
            }
            var r = new Random(DateTime.UtcNow.Millisecond);

            if (count == 0)
            {
                count = inputCount;
            }

            var res = new List<int>(inputCount);
            
            for (int i = 0; i < count; i++)
            {
                var p = r.Next(input.Count);
                res.Add(input[p]);
                input.RemoveAt(p);
            }

            return res;
        }

        //[Test]
        //public void PageBlockHeavyLoadTest()
        //{
        //    var totalBlockCount = _pda.PageBlockPerDatabaseChunk * DatabaseFileChunkCount;
        //    int blockCount = totalBlockCount/4;
        //    var l = GenerateRandomAccess(1, totalBlockCount, blockCount);

        //    var tasks = new Task[blockCount];

        //    Console.WriteLine($"Writing to {blockCount} Page Blocks among a total of {totalBlockCount}");

        //    var sw = new Stopwatch();
        //    sw.Start();
        //    for (int i = 0; i < blockCount; i++)
        //    {
        //        var pbid = i;
        //        tasks[pbid] = Task.Run(() =>
        //        {
        //            var view = _pda.GetPageBlockView((uint)l[pbid], 1, true);
        //            view.Write(0, 123+pbid);
        //        });
        //    }

        //    Task.WaitAll(tasks);

        //    sw.Stop();
        //    Console.WriteLine($"Written to all pages in memory, in {sw.ElapsedMilliseconds}ms for {blockCount*PersistentDataAccess.PageSize/(1024*1024)}MiB");

        //    sw.Start();
        //    _pda.FlushAsyncAllViews();
        //    sw.Stop();
        //    Console.WriteLine($"Written to all pages to disk, in {sw.ElapsedMilliseconds}ms");

        //}

        [TearDown]
        public void TearDown()
        {
            _pda?.Dispose();
            _pda = null;
        }
    }
}