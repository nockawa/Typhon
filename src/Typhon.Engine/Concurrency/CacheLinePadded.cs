using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Wraps an <see cref="int"/> in a 64-byte cache-line-sized struct to prevent false sharing.
/// Use for fields that are CAS targets or atomically modified from multiple threads,
/// where adjacent field writes on other cores would otherwise bounce the cache line.
/// </summary>
/// <remarks>
/// Access the value via <see cref="Value"/>. For <see cref="System.Threading.Interlocked"/>
/// operations, pass <c>ref field.Value</c>.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct CacheLinePaddedInt
{
    [FieldOffset(0)]
    public int Value;
}
