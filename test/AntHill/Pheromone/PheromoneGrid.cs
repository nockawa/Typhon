using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace AntHill;

/// <summary>
/// Two-channel pheromone grid: food trail + home trail.
/// Flat float arrays, O(1) lookup by world coordinate. No ECS dependency.
/// </summary>
public sealed class PheromoneGrid
{
    public const int GridSize = 1000;           // 1000×1000 cells
    public const float CellSize = 20f;          // 20 world units per cell
    public const float InvCellSize = 1f / CellSize;
    public const float MaxPheromone = 255f;

    /// <summary>Food trail: deposited by returning ants, followed by foraging ants.</summary>
    public readonly float[] Food = new float[GridSize * GridSize];

    /// <summary>Home trail: deposited by foraging ants, followed by returning ants.</summary>
    public readonly float[] Home = new float[GridSize * GridSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WorldToIndex(float wx, float wy)
    {
        int gx = Math.Clamp((int)(wx * InvCellSize), 0, GridSize - 1);
        int gy = Math.Clamp((int)(wy * InvCellSize), 0, GridSize - 1);
        return gy * GridSize + gx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFood(float wx, float wy) => Food[WorldToIndex(wx, wy)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadHome(float wx, float wy) => Home[WorldToIndex(wx, wy)];

    /// <summary>
    /// Deposit pheromone. No synchronization — rare lost updates on same 20×20 cell
    /// are acceptable for pheromone simulation. Avoids Interlocked overhead per ant.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Deposit(float[] channel, int index, float amount)
    {
        float val = channel[index] + amount;
        if (val > MaxPheromone) val = MaxPheromone;
        channel[index] = val;
    }

    private const int EvaporateChunks = 16;

    /// <summary>
    /// Evaporate both channels in parallel. Splits the grid into 16 stripes,
    /// each processed with AVX intrinsics. Memory-bandwidth bound, parallelized
    /// across cores for aggregate L3 bandwidth.
    /// </summary>
    public void Evaporate(float decayFactor)
    {
        int len = Food.Length;
        // Chunk size must be a multiple of Vector256<float>.Count (8) so each worker's
        // SIMD loop is clean and we don't need per-chunk tail handling inside the parallel body.
        int vecSize = Vector256<float>.Count;
        int chunkSize = ((len / EvaporateChunks) / vecSize) * vecSize;
        int parallelEnd = chunkSize * EvaporateChunks;

        var food = Food;
        var home = Home;

        Parallel.For(0, EvaporateChunks, chunk =>
        {
            int start = chunk * chunkSize;
            int end = start + chunkSize;
            EvaporateRange(food, home, start, end, decayFactor);
        });

        // Scalar tail for the last few elements not covered by parallel chunks
        for (int i = parallelEnd; i < len; i++)
        {
            food[i] *= decayFactor;
            home[i] *= decayFactor;
        }
    }

    private static void EvaporateRange(float[] food, float[] home, int start, int end, float decayFactor)
    {
        ref float fp = ref MemoryMarshal.GetArrayDataReference(food);
        ref float hp = ref MemoryMarshal.GetArrayDataReference(home);

        if (Avx.IsSupported)
        {
            var decay256 = Vector256.Create(decayFactor);
            int vecSize = Vector256<float>.Count;
            nuint i = (nuint)start;
            nuint simdEnd = (nuint)end;
            for (; i + (nuint)vecSize <= simdEnd; i += (nuint)vecSize)
            {
                var f = Vector256.LoadUnsafe(ref fp, i);
                var h = Vector256.LoadUnsafe(ref hp, i);
                Avx.Multiply(f, decay256).StoreUnsafe(ref fp, i);
                Avx.Multiply(h, decay256).StoreUnsafe(ref hp, i);
            }
        }
        else
        {
            for (int i = start; i < end; i++)
            {
                Unsafe.Add(ref fp, i) *= decayFactor;
                Unsafe.Add(ref hp, i) *= decayFactor;
            }
        }
    }
}
