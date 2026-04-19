using JetBrains.Annotations;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Direction of a <see cref="TraceEventKind.MemoryAllocEvent"/>: allocation or free.
/// </summary>
/// <remarks>
/// Numeric values are wire-stable; never renumber.
/// </remarks>
[PublicAPI]
public enum MemoryAllocDirection : byte
{
    /// <summary>The record represents a new allocation — memory was reserved.</summary>
    Alloc = 0,

    /// <summary>The record represents a release — memory was returned to the OS.</summary>
    Free = 1,
}

/// <summary>
/// Interned call-site tags for <see cref="TraceEventKind.MemoryAllocEvent"/>. The <c>u16 sourceTag</c> field of the record identifies
/// which logical subsystem performed the allocation so the viewer can colour-code markers and compute per-subsystem aggregates.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the profiler's string-interning table (<c>NamedSpan</c>), these are compile-time constants — no runtime name table is needed.
/// Numeric values are wire-stable; append new entries to the end, never renumber.
/// </para>
/// <para>
/// Value <see cref="Unattributed"/> (0) is the sentinel for allocations emitted without an explicit tag — useful during incremental
/// instrumentation rollout when a call site hasn't been classified yet.
/// </para>
/// </remarks>
[PublicAPI]
public static class MemoryAllocSource
{
    /// <summary>Allocation without an explicit source tag. Default when a call site has not been classified.</summary>
    public const ushort Unattributed = 0;

    /// <summary>WAL staging buffers rented from <c>StagingBufferPool</c>.</summary>
    public const ushort WalStaging = 1;

    /// <summary>Page cache backing storage allocated by <c>PagedMMF</c> at construction.</summary>
    public const ushort PageCache = 2;

    /// <summary>TransientStore block allocations.</summary>
    public const ushort TransientStore = 3;

    /// <summary>WAL commit buffer backing storage.</summary>
    public const ushort WalCommitBuffer = 4;

    /// <summary>General-purpose <c>MemoryBlockArray</c> allocations (managed blocks under <c>GC.AllocateArray</c>).</summary>
    public const ushort MemoryBlockArray = 5;
}
