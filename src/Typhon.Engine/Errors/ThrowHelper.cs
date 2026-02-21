using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Centralized throw helpers with <see cref="MethodImplOptions.NoInlining"/> to keep hot-path method bodies small.
/// The JIT won't inline throw paths into callers, preserving cache-friendly code layout.
/// </summary>
internal static class ThrowHelper
{
    // --- Existing (moved from ChunkAccessor.cs) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgument(string message) => throw new ArgumentException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidOp(string message) => throw new InvalidOperationException(message);

    // --- New — Tier 1 ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowLockTimeout(string resourceName, TimeSpan waitDuration) => throw new LockTimeoutException(resourceName, waitDuration);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowResourceExhausted(string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        => throw new ResourceExhaustedException(resourcePath, resourceType, currentUsage, limit);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowCorruption(string componentName, int pageIndex, string detail) => throw new CorruptionException(componentName, pageIndex, detail);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowEpochRegistryExhausted() => throw new ResourceExhaustedException("Concurrency/EpochThreadRegistry", 
        ResourceType.Synchronization, EpochThreadRegistry.MaxSlots, EpochThreadRegistry.MaxSlots);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowTransactionTimeout(long transactionId, TimeSpan waitDuration) => throw new TransactionTimeoutException(transactionId, waitDuration);

    // --- Index ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowUniqueConstraintViolation() => throw new UniqueConstraintViolationException();

    // --- Durability ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalBackPressureTimeout(int requestedBytes, TimeSpan waitDuration) => throw new WalBackPressureTimeoutException(requestedBytes, waitDuration);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalClaimTooLarge(int requestedBytes, int bufferCapacity) => throw new WalClaimTooLargeException(requestedBytes, bufferCapacity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalWriteFailure(Exception innerException) => throw new WalWriteException(innerException);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalSegmentError(string segmentPath, string detail) => throw new WalSegmentException(segmentPath, detail);
}
