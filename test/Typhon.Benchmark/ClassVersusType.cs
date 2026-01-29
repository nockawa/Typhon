// unset

using BenchmarkDotNet.Attributes;
using System;

namespace Typhon.Benchmark;

[SimpleJob(warmupCount: 3, iterationCount: 3)]
[BenchmarkCategory("Memory")]
public class ClassVersusType
{
    private const int ElementCount = 10 * 1024;

    public struct InnerStruct
    {
        public int A;
        public int B;
        public int C;
        public int D;
    }
    public class ClassType
    {
        private readonly InnerStruct[] _inner;

        public ClassType()
        {
            _inner = new InnerStruct[16];
        }
    }

    public class ClassWithMemory
    {
        private Memory<InnerStruct> _inner;

        public ClassWithMemory()
        {
            _inner = new InnerStruct[16];
        }
    }

    [GlobalSetup]
    public void GlobalSetup()
    {

    }


    [GlobalCleanup]
    public void GlobalCleanup()
    {
    }

    [Benchmark(Baseline = true)]
    public int BenchmarkClass()
    {
        for (int k = 0; k < 10; k++)
        {
            var array = new ClassType[ElementCount];
            for (int i = 0; i < ElementCount; i += 4)
            {
                array[i] = new ClassType();
            }
        }

        return default;
    }

    [Benchmark]
    public int BenchmarkClasWithMemory()
    {
        for (int k = 0; k < 10; k++)
        {
            var array = new ClassWithMemory[ElementCount];
            for (int i = 0; i < ElementCount; i += 4)
            {
                array[i] = new ClassWithMemory();
            }
        }

        return default;
    }
}