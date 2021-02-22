using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using System;
using System.Collections;
using System.Linq;
using System.Threading;
using Typhon.Engine;

namespace Typhon.Collections.Benchmark
{
    [SimpleJob(warmupCount: 3, targetCount: 3)]
    public class ConcurrentBitmapBenchmark
    {
        private const int BitSize = 1024 * 1024;
        private const int SampleCount = 100_000_000;

        private readonly int[] _samples;

        private ConcurrentBitmap _concurrent;
        private ConcurrentBitmapL3 _concurrentL3;

        public ConcurrentBitmapBenchmark()
        {

            _samples = new int[SampleCount];

            var r = new Random(DateTime.UtcNow.Millisecond);
            for (int i = 0; i < SampleCount; i++)
            {
                _samples[i] = r.Next(0, BitSize);
            }
        }
        struct PageActivityInfo
        {
            public int AllocatedTime;
            public int HitCounter;
        }

        private PageActivityInfo[] _aList;
        private Memory<PageActivityInfo> _aMem;
        private int[] _iList;
        private int[] _vList;
        private int _pageCount;
        private VirtualDiskManager.PageInfo[] _pages;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _concurrent = new ConcurrentBitmap(BitSize);
            _concurrentL3 = new ConcurrentBitmapL3(BitSize);

            _pageCount = 1 << 19;
            
            _aList = new PageActivityInfo[_pageCount];
            _iList = new int[_pageCount];
            _vList = new int[_pageCount];
            _pages = new VirtualDiskManager.PageInfo[_pageCount];

            var r = new Random(DateTime.UtcNow.Millisecond);
            for (int i = 0; i < _pageCount; i++)
            {
                var at = (int)(TimeSpan.FromMilliseconds(r.Next(0, 1000 * 60 * 10)).Ticks >> 20);
                var hc = r.Next(1, 10000);
                _aList[i] = new PageActivityInfo { AllocatedTime = at, HitCounter = hc };
                _iList[i] = hc;
                _vList[i] = i;
                _pages[i] = new VirtualDiskManager.PageInfo(i);
            }

            _aMem = _aList.ToArray();
        }

        //[Benchmark(Baseline = true)]
        //public void BenchSpanConcurrent()
        //{
        //    var c = _concurrent;
        //    for (int i = 0; i < SampleCount; i+=10)
        //    {
        //        c.Set(_samples[i+0]);
        //        c.Set(_samples[i+1]);
        //        c.Set(_samples[i+2]);
        //        c.Set(_samples[i+3]);
        //        c.Set(_samples[i+4]);
        //        c.Set(_samples[i+5]);
        //        c.Set(_samples[i+6]);
        //        c.Set(_samples[i+7]);
        //        c.Set(_samples[i+8]);
        //        c.Set(_samples[i+9]);
        //    }
        //}

        ////[Benchmark]
        //public void BenchConcurrentL4()
        //{
        //    var c = _concurrentL3;
        //    for (int i = 0; i < SampleCount; i += 10)
        //    {
        //        c.Set(_samples[i + 0]);
        //        c.Set(_samples[i + 1]);
        //        c.Set(_samples[i + 2]);
        //        c.Set(_samples[i + 3]);
        //        c.Set(_samples[i + 4]);
        //        c.Set(_samples[i + 5]);
        //        c.Set(_samples[i + 6]);
        //        c.Set(_samples[i + 7]);
        //        c.Set(_samples[i + 8]);
        //        c.Set(_samples[i + 9]);
        //    }
        //}

        [Benchmark(Baseline = true)]
        public void BenchSortInt()
        {
            Array.Sort(_iList);

        }

        [Benchmark]
        public void BenchSortIntC()
        {
            //Array.Sort(_pages, (x, y) => x.MemPageId - y.MemPageId);
            Array.Sort(_iList, _vList);

        }

        //[Benchmark]
        //public void BenchSortIntV()
        //{
        //    Array.Sort(_iList, _vList);

        //}

        [Benchmark]
        public void BenchSortIntV2()
        {
            Array.Sort(_iList, _pages);

        }

        //[Benchmark]
        //public void BenchDecay()
        //{
        //    var c = _pageCount;
        //    var dT = (double)TimeSpan.FromMilliseconds(2).Ticks;
        //    for (int i = 0; i < c; i ++)
        //    {
        //        var t = (double)(_aList[i].AllocatedTime << 20);
        //        _aList[i].HitCounter = (int)(t / (t + dT) * _aList[i].HitCounter);
        //    }
        //}
    }

    class Program
    {
        static float SmoothStep(float t0, float t1, float x) => Math.Clamp((x - t0) / (t1 - t0), 0, 1);

        static void Main(string[] args)
        {
            //var o = new ConcurrentBitmapBenchmark();
            //o.GlobalSetup();
            //o.BenchDecay();

            var summary = BenchmarkRunner.Run<ConcurrentBitmapBenchmark>();

            var minDecay = 1f;
            var maxDecay = 20f;

            var start = DateTime.UtcNow;

            var v = 150f;
            var w = 1000f;
            var z = 10f;
            var now = start;
            var step = 0f;

            while (now < start + TimeSpan.FromSeconds(20))
            {
                var p = v;
                var dt = (now - start);
                var f = SmoothStep(minDecay, maxDecay, (float)dt.TotalSeconds);

                v = (v * (1 - (f * step)));
                w = (w * (1 - (f * step)));
                z = (z * (1 - (f * step)));

                Console.WriteLine($"[{dt.TotalSeconds:0.##}]\t Z {z:0.##}\t V {v:0.##}\t W {w:0.##}\t\tF {f:0.###}\tStep {step:0.##}");

                Thread.Sleep(200);

                var n = DateTime.UtcNow;
                step = (float)(n - now).TotalSeconds;
                now = n;
            }


            //Array.S

            //var o = new ConcurrentBitmapBenchmark();
            //o.GlobalSetup();
            //o.BenchConcurrentL4();
        }
    }
}
