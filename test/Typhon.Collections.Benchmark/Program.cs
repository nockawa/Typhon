using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Collections.Benchmark
{
    class Program
    {
        static float SmoothStep(float t0, float t1, float x) => Math.Clamp((x - t0) / (t1 - t0), 0, 1);

        static void Main(string[] args)
        {
            //var o = new ConcurrentBitmapBenchmark();
            //o.GlobalSetup();
            //o.BenchDecay();


            //var summary = BenchmarkRunner.Run<MemCopyBench>();

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
}
