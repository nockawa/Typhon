// unset

using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Benchmark;

/// <summary>
/// Benchmark to measure the overhead of adding thread-local dictionary tracking
/// to lock operations that use Interlocked.CompareExchange.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Concurrency")]
public unsafe class ThreadLocalDictOverheadBenchmark
{
    private const int OperationCount = 1_000_000;

    // Simulates multiple lock instances (like multiple AccessControlSmall structs)
    private const int LockCount = 64;

    // Array of "locks" (just ints for CAS operations)
    private int[] _locks = null!;

    // Thread-local dictionary for tracking
    [ThreadStatic]
    private static Dictionary<nint, ThreadLockState>? t_lockStates;

    private struct ThreadLockState
    {
        public short SharedCount;
        public short ExclusiveCount;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _locks = new int[LockCount];
        // Clear thread-local state
        t_lockStates = null;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _locks = null!;
        t_lockStates = null;
    }

    /// <summary>
    /// Baseline: Just Interlocked.CompareExchange (simulates current lock behavior)
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = OperationCount)]
    public int Baseline_InterlockedOnly()
    {
        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            var lockIndex = i % LockCount;
            ref var lockData = ref _locks[lockIndex];

            // Simulate enter: CAS from 0 to 1
            var original = Interlocked.CompareExchange(ref lockData, 1, 0);
            sum += original;

            // Simulate exit: CAS from 1 to 0
            Interlocked.CompareExchange(ref lockData, 0, 1);
        }
        return sum;
    }

    /// <summary>
    /// With thread-local tracking using CollectionsMarshal.GetValueRefOrAddDefault
    /// </summary>
    [Benchmark(OperationsPerInvoke = OperationCount)]
    public int WithThreadLocal_CollectionsMarshal()
    {
        t_lockStates ??= new Dictionary<nint, ThreadLockState>();
        var dict = t_lockStates;

        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            var lockIndex = i % LockCount;
            ref var lockData = ref _locks[lockIndex];

            // Get lock address (simulate getting address of AccessControlSmall)
            var lockAddr = (nint)Unsafe.AsPointer(ref lockData);

            // Thread-local lookup using CollectionsMarshal
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, lockAddr, out var exists);

            if (!exists || state.SharedCount == 0)
            {
                // First acquisition - do the actual CAS
                var original = Interlocked.CompareExchange(ref lockData, 1, 0);
                sum += original;
                state.SharedCount = 1;
            }
            else
            {
                // Reentrant - just increment counter
                state.SharedCount++;
            }

            // Exit
            state.SharedCount--;
            if (state.SharedCount == 0)
            {
                dict.Remove(lockAddr);
                Interlocked.CompareExchange(ref lockData, 0, 1);
            }
        }
        return sum;
    }

    /// <summary>
    /// Worst case: Many different locks (cold dictionary lookups)
    /// This simulates accessing many different AccessControlSmall instances
    /// </summary>
    [Benchmark(OperationsPerInvoke = OperationCount)]
    public int WithThreadLocal_ManyLocks()
    {
        t_lockStates ??= new Dictionary<nint, ThreadLockState>();
        var dict = t_lockStates;
        dict.Clear(); // Ensure fresh start

        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            var lockIndex = i % LockCount;
            ref var lockData = ref _locks[lockIndex];

            var lockAddr = (nint)Unsafe.AsPointer(ref lockData);

            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, lockAddr, out var exists);

            if (!exists || state.SharedCount == 0)
            {
                var original = Interlocked.CompareExchange(ref lockData, 1, 0);
                sum += original;
                state.SharedCount = 1;
            }
            else
            {
                state.SharedCount++;
            }

            // Exit
            state.SharedCount--;
            if (state.SharedCount == 0)
            {
                dict.Remove(lockAddr);
                Interlocked.CompareExchange(ref lockData, 0, 1);
            }
        }
        return sum;
    }

    /// <summary>
    /// Best case: Same lock repeatedly (hot dictionary lookup, reentrant path)
    /// </summary>
    [Benchmark(OperationsPerInvoke = OperationCount)]
    public int WithThreadLocal_SameLock_Reentrant()
    {
        t_lockStates ??= new Dictionary<nint, ThreadLockState>();
        var dict = t_lockStates;
        dict.Clear();

        ref var lockData = ref _locks[0];
        var lockAddr = (nint)Unsafe.AsPointer(ref lockData);

        // Initial acquisition
        Interlocked.CompareExchange(ref lockData, 1, 0);
        dict[lockAddr] = new ThreadLockState { SharedCount = 1 };

        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            // Reentrant enter
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, lockAddr, out _);
            state.SharedCount++;
            sum += state.SharedCount;

            // Reentrant exit (not last)
            state.SharedCount--;
        }

        // Final cleanup
        dict.Remove(lockAddr);
        Interlocked.CompareExchange(ref lockData, 0, 1);

        return sum;
    }
}
