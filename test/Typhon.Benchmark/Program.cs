using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

class Program
{
    static float SmoothStep(float t0, float t1, float x) => Math.Clamp((x - t0) / (t1 - t0), 0, 1);

    static private ValueTask WriteData(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset)
    {
        return RandomAccess.WriteAsync(handle, data, offset);
    }
    
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<PagedVirtualMemoryFile_RandomAccess>();
        /*
        var pvmmft = new PagedVirtualMemoryFile_RandomAccess();
        pvmmft.GlobalSetup();
        pvmmft.TestRandomAccess();
        pvmmft.GlobalCleanup();
        */

        /*
        {
            // Thread-safe concurrent writes
            using SafeFileHandle handle = File.OpenHandle(
                "data.txt", FileMode.Create, FileAccess.Write,
                FileShare.None, FileOptions.Asynchronous);

            var arrays = new List<byte[]>();

            var size = 8192;
            for (int i = 0; i < 100; i++)
            {
                var a = new byte[size];
                for (int j = 0; j < size; j++)
                {
                    a[j] = (byte)(i + j);
                }
                arrays.Add(a);
            }

            long offset = 0;
            long totalIssued = 0;
            long totalCompleted = 0;
            for (int index = 0; index < arrays.Count; index++)
            {
                var i = index;
                byte[] array = arrays[i];
                var newOffset = Interlocked.Add(ref offset, size) - size;
                var threadId = Thread.CurrentThread.ManagedThreadId;
                var start = DateTime.UtcNow;
                Console.WriteLine($"[{i}][Thread:{threadId}]Issuing");
                var task = WriteData(handle, array, newOffset);
                var ts = DateTime.UtcNow - start;
                Interlocked.Add(ref totalIssued, ts.Ticks);
                Console.WriteLine($"[{i}][Thread:{threadId}][{ts.TotalMilliseconds}ms] Issued");
                await task.AsTask().ContinueWith(_ =>
                {
                    //RandomAccess.FlushToDisk(handle);
                    var tId = Thread.CurrentThread.ManagedThreadId;
                    var ts2 = DateTime.UtcNow - start;

                    Interlocked.Add(ref totalCompleted, ts2.Ticks);
                    Console.WriteLine($"[{i}][Thread:{tId}][{ts2.TotalMilliseconds}ms] Completed");
                });

            }

            Console.WriteLine($"Average Issued time {TimeSpan.FromTicks(totalIssued / arrays.Count).TotalMilliseconds}ms");
            Console.WriteLine($"Average Completed time {TimeSpan.FromTicks(totalCompleted / arrays.Count).TotalMilliseconds}ms");
        }
        */

        //var o = new ConcurrentBitmapBenchmark();
        //o.GlobalSetup();
        //o.BenchDecay();

        //var taskList = new List<Task>();
        //for (int i = 0; i < 1024; i++)
        //{
        //    taskList.Add(Task.Run(() =>
        //    {
        //        Console.WriteLine($"Thread Id {Thread.CurrentThread.ManagedThreadId}");
        //    }));
        //}

        //Task.WaitAll(taskList.ToArray());


        //var summary = BenchmarkRunner.Run<ClassVersusType>();

        // var o = new ClassVersusType();
        // o.BenchmarkClass();
        // GC.Collect();
        // o.BenchmarkClasWithMemory();

        //var o = new MemCopyBench();
        //o.GlobalSetup();

        //for (int i = 0; i < 100; i++)
        //{
        //    var sw = new Stopwatch();
        //    sw.Start();

        //    o.SpanCopy();

        //    sw.Stop();

        //    Console.WriteLine($"Copy time: {sw.Elapsed}ms, Tick: {sw.ElapsedTicks}");
        //}

        //o.GlobalCleanup();

        //var o = new DirectAccessBench();
        //o.GlobalSetup();
        //o.BenchmarkSegmentRefAccess();
        //o.GlobalCleanup();

        //var o = new PageAccessBenchmark();
        //o.GlobalSetup();
        //o.BenchmarkSegmentManualAccess();
        //o.BenchmarkSegmentForEachElement();
        //o.GlobalCleanup();

        //var summary = BenchmarkRunner.Run<PageAccessBenchmark>();

        //var minDecay = 1f;
        //var maxDecay = 20f;

        //var start = DateTime.UtcNow;

        //var v = 150f;
        //var w = 1000f;
        //var z = 10f;
        //var now = start;
        //var step = 0f;

        //while (now < start + TimeSpan.FromSeconds(20))
        //{
        //    var p = v;
        //    var dt = (now - start);
        //    var f = SmoothStep(minDecay, maxDecay, (float)dt.TotalSeconds);

        //    v = (v * (1 - (f * step)));
        //    w = (w * (1 - (f * step)));
        //    z = (z * (1 - (f * step)));

        //    Console.WriteLine($"[{dt.TotalSeconds:0.##}]\t Z {z:0.##}\t V {v:0.##}\t W {w:0.##}\t\tF {f:0.###}\tStep {step:0.##}");

        //    Thread.Sleep(200);

        //    var n = DateTime.UtcNow;
        //    step = (float)(n - now).TotalSeconds;
        //    now = n;
        //}


        //Array.S

        //var o = new ConcurrentBitmapBenchmark();
        //o.GlobalSetup();
        //o.BenchConcurrentL4();
    }
}