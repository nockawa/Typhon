using BenchmarkDotNet.Attributes;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Typhon.Benchmark;

/// <summary>
/// Benchmark comparing different methods of obtaining timestamps/time values.
/// Tests DateTime.UtcNow, Stopwatch.GetTimestamp(), Environment.TickCount64,
/// and various native interop approaches for QueryPerformanceCounter (Windows only).
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Timing")]
public unsafe class TimestampBenchmark
{
    private const int Iterations = 1000;

    /// <summary>
    /// Prevents dead code elimination by the JIT compiler.
    /// </summary>
    private static long _sink;

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Function pointer for QueryPerformanceCounter obtained via NativeLibrary
    private static readonly delegate* unmanaged[Stdcall]<long*, bool> s_qpcFunctionPtr;

    static TimestampBenchmark()
    {
        if (IsWindows)
        {
            var kernel32 = NativeLibrary.Load("kernel32.dll");
            s_qpcFunctionPtr = (delegate* unmanaged[Stdcall]<long*, bool>)
                NativeLibrary.GetExport(kernel32, "QueryPerformanceCounter");
        }
    }

    /// <summary>
    /// Standard P/Invoke to kernel32.dll QueryPerformanceCounter.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    /// <summary>
    /// P/Invoke with SuppressGCTransition - skips the cooperative-to-preemptive GC mode switch.
    /// Safe for QPC because it: executes in &lt;1μs, doesn't block, doesn't call back into runtime.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter")]
    [SuppressGCTransition]
    private static extern bool QueryPerformanceCounterSuppressGC(out long lpPerformanceCount);

    [Benchmark(Baseline = true)]
    public long DateTimeUtcNow()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += DateTime.UtcNow.Ticks;
        }
        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long StopwatchGetTimestamp()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += Stopwatch.GetTimestamp();
        }
        _sink = sum;
        return sum;
    }

    [Benchmark]
    [SupportedOSPlatform("windows")]
    public long QPC_PInvoke()
    {
        if (!IsWindows) return 0;

        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            QueryPerformanceCounter(out long counter);
            sum += counter;
        }
        _sink = sum;
        return sum;
    }

    [Benchmark]
    [SupportedOSPlatform("windows")]
    public long QPC_PInvoke_SuppressGCTransition()
    {
        if (!IsWindows) return 0;

        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            QueryPerformanceCounterSuppressGC(out long counter);
            sum += counter;
        }
        _sink = sum;
        return sum;
    }

    [Benchmark]
    [SupportedOSPlatform("windows")]
    public long QPC_FunctionPointer()
    {
        if (!IsWindows) return 0;

        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            long counter;
            s_qpcFunctionPtr(&counter);
            sum += counter;
        }
        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long EnvironmentTickCount64()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += Environment.TickCount64;
        }
        _sink = sum;
        return sum;
    }

    // === Single call benchmarks - per-call overhead without loop noise ===

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long DateTimeUtcNow_Single()
    {
        return DateTime.UtcNow.Ticks;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long StopwatchGetTimestamp_Single()
    {
        return Stopwatch.GetTimestamp();
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    public long QPC_PInvoke_Single()
    {
        if (!IsWindows) return 0;

        QueryPerformanceCounter(out long counter);
        return counter;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    public long QPC_PInvoke_SuppressGCTransition_Single()
    {
        if (!IsWindows) return 0;

        QueryPerformanceCounterSuppressGC(out long counter);
        return counter;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    public long QPC_FunctionPointer_Single()
    {
        if (!IsWindows) return 0;

        long counter;
        s_qpcFunctionPtr(&counter);
        return counter;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long EnvironmentTickCount64_Single()
    {
        return Environment.TickCount64;
    }
}
