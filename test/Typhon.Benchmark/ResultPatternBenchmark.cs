using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Benchmark;

/// <summary>
/// Benchmark comparing three return patterns for hot-path methods:
/// 1. Classic bool + out (current TryGet pattern)
/// 2. Result&lt;TValue&gt; single-generic with global status enum
/// 3. Result&lt;TValue, TStatus&gt; dual-generic with per-subsystem status enum
///
/// Context: Issue #36 / D4 — evaluating Result types for B+Tree lookups,
/// revision reads, and chunk access where exception overhead is unacceptable.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
[BenchmarkCategory("ResultPattern")]
public class ResultPatternBenchmark
{
    // ─── Status enums ───────────────────────────────────────────

    /// <summary>Global status enum (Option B) — accumulates all subsystems.</summary>
    public enum GlobalResultStatus : byte
    {
        Success = 0,
        NotFound,
        SnapshotInvisible,
        Deleted,
        NotLoaded,
    }

    /// <summary>Per-subsystem status enum (Option C) — BTree lookup only.</summary>
    public enum BTreeLookupStatus : byte
    {
        Success = 0,
        NotFound,
    }

    /// <summary>Per-subsystem status enum (Option C) — revision reads.</summary>
    public enum RevisionReadStatus : byte
    {
        Success = 0,
        NotFound,
        SnapshotInvisible,
        Deleted,
    }

    // ─── Result structs ─────────────────────────────────────────

    /// <summary>Single-generic Result with global status enum (Option B).</summary>
    public readonly struct ResultSingle<TValue> where TValue : unmanaged
    {
        private readonly TValue _value;
        public readonly GlobalResultStatus Status;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ResultSingle(TValue value, GlobalResultStatus status)
        {
            _value = value;
            Status = status;
        }

        public bool IsSuccess
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Status == GlobalResultStatus.Success;
        }

        public TValue Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResultSingle<TValue> Success(TValue value) => new(value, GlobalResultStatus.Success);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResultSingle<TValue> Fail(GlobalResultStatus status) => new(default, status);
    }

    /// <summary>Dual-generic Result with per-subsystem status enum (Option C).</summary>
    public readonly struct ResultDual<TValue, TStatus>
        where TValue : unmanaged
        where TStatus : unmanaged, Enum
    {
        private readonly TValue _value;
        public readonly TStatus Status;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ResultDual(TValue value, TStatus status)
        {
            _value = value;
            Status = status;
        }

        public bool IsSuccess
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<TStatus, byte>(ref Unsafe.AsRef(in Status)) == 0;
        }

        public TValue Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResultDual<TValue, TStatus> Success(TValue value) => new(value, default);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResultDual<TValue, TStatus> Fail(TStatus status) => new(default, status);
    }

    // ─── Simulated data store ───────────────────────────────────

    // Simulates a small lookup table (like a B+Tree leaf node).
    // Keys 0..Size-1 exist; other keys are "not found".
    private const int Size = 64;
    private readonly long[] _keys = new long[Size];
    private readonly long[] _values = new long[Size];

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < Size; i++)
        {
            _keys[i] = i;
            _values[i] = i * 1000;
        }
    }

    // ─── Simulated lookup methods ───────────────────────────────
    // Each mirrors a realistic B+Tree leaf scan: linear search over a
    // small array (≤64 entries), returning either the found value or
    // a "not found" indication. This captures real call-site patterns.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetBoolOut(long key, out long value)
    {
        for (int i = 0; i < Size; i++)
        {
            if (_keys[i] == key)
            {
                value = _values[i];
                return true;
            }
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ResultSingle<long> TryGetResultSingle(long key)
    {
        for (int i = 0; i < Size; i++)
        {
            if (_keys[i] == key)
            {
                return ResultSingle<long>.Success(_values[i]);
            }
        }

        return ResultSingle<long>.Fail(GlobalResultStatus.NotFound);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ResultDual<long, BTreeLookupStatus> TryGetResultDual(long key)
    {
        for (int i = 0; i < Size; i++)
        {
            if (_keys[i] == key)
            {
                return ResultDual<long, BTreeLookupStatus>.Success(_values[i]);
            }
        }

        return ResultDual<long, BTreeLookupStatus>.Fail(BTreeLookupStatus.NotFound);
    }

    // ─── Sink to prevent dead-code elimination ──────────────────

    private static long _sink;

    // ─── Benchmarks: Found path (key exists) ────────────────────

    [Benchmark(Baseline = true)]
    public long BoolOut_Found()
    {
        long sum = 0;
        for (long k = 0; k < Size; k++)
        {
            if (TryGetBoolOut(k, out long val))
            {
                sum += val;
            }
        }

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long ResultSingle_Found()
    {
        long sum = 0;
        for (long k = 0; k < Size; k++)
        {
            var result = TryGetResultSingle(k);
            if (result.IsSuccess)
            {
                sum += result.Value;
            }
        }

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long ResultDual_Found()
    {
        long sum = 0;
        for (long k = 0; k < Size; k++)
        {
            var result = TryGetResultDual(k);
            if (result.IsSuccess)
            {
                sum += result.Value;
            }
        }

        _sink = sum;
        return sum;
    }

    // ─── Benchmarks: Not-found path (key does not exist) ────────

    [Benchmark]
    public long BoolOut_NotFound()
    {
        long sum = 0;
        for (long k = Size; k < Size * 2; k++)
        {
            if (!TryGetBoolOut(k, out long val))
            {
                sum++;
            }
        }

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long ResultSingle_NotFound()
    {
        long sum = 0;
        for (long k = Size; k < Size * 2; k++)
        {
            var result = TryGetResultSingle(k);
            if (!result.IsSuccess)
            {
                sum++;
            }
        }

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long ResultDual_NotFound()
    {
        long sum = 0;
        for (long k = Size; k < Size * 2; k++)
        {
            var result = TryGetResultDual(k);
            if (!result.IsSuccess)
            {
                sum++;
            }
        }

        _sink = sum;
        return sum;
    }

    // ─── Benchmarks: Mixed path (50% hit rate) ─────────────────

    [Benchmark]
    public long BoolOut_Mixed()
    {
        long sum = 0;
        for (long k = 0; k < Size * 2; k += 2)
        {
            // k=0,2,4...62 → found; k+1=1,3,5...63 → found; but k≥64 → not found
            // Alternate: even keys found (0..62), odd shifted keys not found
            if (TryGetBoolOut(k, out long val))
            {
                sum += val;
            }

            if (!TryGetBoolOut(k + Size, out _))
            {
                sum++;
            }
        }

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long ResultSingle_Mixed()
    {
        long sum = 0;
        for (long k = 0; k < Size * 2; k += 2)
        {
            var r1 = TryGetResultSingle(k);
            if (r1.IsSuccess)
            {
                sum += r1.Value;
            }

            var r2 = TryGetResultSingle(k + Size);
            if (!r2.IsSuccess)
            {
                sum++;
            }
        }

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public long ResultDual_Mixed()
    {
        long sum = 0;
        for (long k = 0; k < Size * 2; k += 2)
        {
            var r1 = TryGetResultDual(k);
            if (r1.IsSuccess)
            {
                sum += r1.Value;
            }

            var r2 = TryGetResultDual(k + Size);
            if (!r2.IsSuccess)
            {
                sum++;
            }
        }

        _sink = sum;
        return sum;
    }

    // ─── Benchmarks: Status discrimination (Result advantage) ───
    // This tests the scenario where Result<TValue, TStatus> shines:
    // the caller needs to distinguish between multiple failure modes.
    // With bool+out, the caller gets no information about WHY it failed.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ResultDual<long, RevisionReadStatus> TryReadRevisionDual(long key)
    {
        // Simulates MVCC revision read with multiple outcomes
        if (key < Size / 4)
        {
            return ResultDual<long, RevisionReadStatus>.Success(_values[key]);
        }

        if (key < Size / 2)
        {
            return ResultDual<long, RevisionReadStatus>.Fail(RevisionReadStatus.NotFound);
        }

        if (key < Size * 3 / 4)
        {
            return ResultDual<long, RevisionReadStatus>.Fail(RevisionReadStatus.SnapshotInvisible);
        }

        return ResultDual<long, RevisionReadStatus>.Fail(RevisionReadStatus.Deleted);
    }

    [Benchmark]
    public long ResultDual_StatusSwitch()
    {
        long sum = 0;
        for (long k = 0; k < Size; k++)
        {
            var result = TryReadRevisionDual(k);
            switch (result.Status)
            {
                case RevisionReadStatus.Success:
                    sum += result.Value;
                    break;
                case RevisionReadStatus.NotFound:
                    sum--;
                    break;
                case RevisionReadStatus.SnapshotInvisible:
                    // Skip — not visible at this snapshot
                    break;
                case RevisionReadStatus.Deleted:
                    sum -= 2;
                    break;
            }
        }

        _sink = sum;
        return sum;
    }
}
