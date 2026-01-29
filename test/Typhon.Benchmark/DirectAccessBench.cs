// unset

using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Benchmark;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TestDirect
{
    private byte* _addr;

    public int A;
    public int B;
    public int R;
    private int _padding0;

    public ref int GetA => ref DirectAccess<int>(8);
    public ref int GetB => ref DirectAccess<int>(12);
    public ref int GetR => ref DirectAccess<int>(16);

    public void Init(byte* addr)
    {
        _addr = addr;
    }

    private ref T DirectAccess<T>(int i) where T : unmanaged => ref Unsafe.AsRef<T>(_addr + i);
}

[SimpleJob(warmupCount: 3, iterationCount: 3)]
[BenchmarkCategory("Memory")]
public unsafe class DirectAccessBench
{
    private const int ElementCount = 1024 * 1024;

    private byte[] _buffer;
    private byte* _address;
    private int _elementSize;

    public ref TestDirect GetElement(int index) => ref Unsafe.AsRef<TestDirect>(_address + index*_elementSize);

    [GlobalSetup]
    public void GlobalSetup()
    {

        _buffer = GC.AllocateArray<byte>(sizeof(TestDirect) * ElementCount);
        fixed (byte* addr = _buffer) { _address = addr; }
        _elementSize = sizeof(TestDirect);

        byte* ca = _address;
        for (int i = 0; i < ElementCount; i++, ca+=_elementSize)
        {
            GetElement(i).Init(ca);
        }
    }


    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _buffer = null;
    }

    [Benchmark(Baseline = true)]
    public int BenchmarkManualAccess()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            ref var t0 = ref GetElement(i + 0);
            ref var t1 = ref GetElement(i + 1);
            ref var t2 = ref GetElement(i + 2);
            ref var t3 = ref GetElement(i + 3);

            for (int j = 0; j < 1000; j++)
            {
                t0.A = i + j;
                t0.B = i - j;
                t0.R = t0.A + t0.B;

                t1.A = i + j;
                t1.B = i - j;
                t1.R = t1.A + t1.B;

                t2.A = i + j;
                t2.B = i - j;
                t2.R = t2.A + t2.B;

                t3.A = i + j;
                t3.B = i - j;
                t3.R = t3.A + t3.B;
            }
        }

        return default;
    }

    [Benchmark]
    public int BenchmarkRefAccess()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            ref var t0 = ref GetElement(i + 0);
            ref var t1 = ref GetElement(i + 1);
            ref var t2 = ref GetElement(i + 2);
            ref var t3 = ref GetElement(i + 3);

            for (int j = 0; j < 1000; j++)
            {
                ref var t0a = ref t0.GetA;
                ref var t0b = ref t0.GetB;
                ref var t0r = ref t0.GetR;
                t0a = i + j;
                t0b = i - j;
                t0r = t0a + t0a;

                ref var t1a = ref t1.GetA;
                ref var t1b = ref t1.GetB;
                ref var t1r = ref t1.GetR;
                t1a = i + j;
                t1b = i - j;
                t1r = t1a + t1a;

                ref var t2a = ref t2.GetA;
                ref var t2b = ref t2.GetB;
                ref var t2r = ref t2.GetR;
                t2a = i + j;
                t2b = i - j;
                t2r = t2a + t2a;

                ref var t3a = ref t3.GetA;
                ref var t3b = ref t3.GetB;
                ref var t3r = ref t3.GetR;
                t3a = i + j;
                t3b = i - j;
                t3r = t3a + t3a;
            }
        }

        return default;
    }
}