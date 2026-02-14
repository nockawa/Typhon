using BenchmarkDotNet.Attributes;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Primitives: String64 Fixed-Length String Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Primitives", "Regression")]
public class String64Benchmarks
{
    private String64 _s1;
    private String64 _s2Equal;
    private String64 _s3Different;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _s1 = "HelloWorldTest";
        _s2Equal = "HelloWorldTest";
        _s3Different = "AnotherString42";
    }

    /// <summary>
    /// Construct a String64 from a managed string via implicit conversion.
    /// Measures the copy overhead of String64 creation.
    /// </summary>
    [Benchmark]
    public String64 Construct_FromString() => "BenchmarkTestStr";

    /// <summary>
    /// Compare two equal String64 values. Uses Span.SequenceCompareTo internally.
    /// </summary>
    [Benchmark]
    public int Compare_Equal() => _s1.CompareTo(_s2Equal);

    /// <summary>
    /// Compare two different String64 values. Measures ordering comparison cost.
    /// </summary>
    [Benchmark]
    public int Compare_Order() => _s1.CompareTo(_s3Different);

    /// <summary>
    /// GetHashCode via MurmurHash2 over the 64-byte fixed buffer.
    /// </summary>
    [Benchmark]
    public int HashCode() => _s1.GetHashCode();
}
