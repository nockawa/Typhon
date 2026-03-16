using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Minimal CRUD helper for engine-internal system schema persistence (ComponentR1, ArchetypeR1, etc.).
/// Single-threaded, no MVCC, no WAL, no conflict detection, no revision tracking.
/// Operates directly on ComponentTable's PK B+Tree and ComponentSegment.
/// </summary>
internal static unsafe class SystemCrud
{
    /// <summary>
    /// Create a system entity: allocate chunk, copy data, insert PK → chunkId in B+Tree.
    /// </summary>
    public static void Create<T>(ComponentTable table, long pk, ref T data, EpochManager epochManager, ChangeSet cs) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(epochManager);

        // Allocate chunk for component data
        int chunkId = table.ComponentSegment.AllocateChunk(false, cs);

        // Copy data into chunk (at ComponentOverhead offset)
        var compAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = compAccessor.GetChunkAsSpan(chunkId, true);
        int overhead = table.ComponentOverhead;
        int compSize = Math.Min(sizeof(T), table.ComponentStorageSize);
        new Span<byte>(Unsafe.AsPointer(ref data), compSize).CopyTo(dst.Slice(overhead));
        compAccessor.Dispose();

        // Insert into PK B+Tree: pk → chunkId
        var pkAccessor = table.PrimaryKeyIndex.Segment.CreateChunkAccessor(cs);
        table.PrimaryKeyIndex.Add(&pk, chunkId, ref pkAccessor);
        pkAccessor.Dispose();
    }

    /// <summary>
    /// Read a system entity by PK: lookup in B+Tree → read chunk data.
    /// </summary>
    public static bool Read<T>(ComponentTable table, long pk, out T data, EpochManager epochManager) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(epochManager);

        // Lookup PK in B+Tree
        var pkAccessor = table.PrimaryKeyIndex.Segment.CreateChunkAccessor();
        var result = table.PrimaryKeyIndex.TryGet(&pk, ref pkAccessor);
        pkAccessor.Dispose();

        if (result.IsFailure)
        {
            data = default;
            return false;
        }

        int chunkId = result.Value;

        // Read chunk data
        var compAccessor = table.ComponentSegment.CreateChunkAccessor();
        var src = compAccessor.GetChunkAsReadOnlySpan(chunkId);
        int overhead = table.ComponentOverhead;
        int compSize = Math.Min(sizeof(T), table.ComponentStorageSize);

        data = default;
        src.Slice(overhead, compSize).CopyTo(new Span<byte>(Unsafe.AsPointer(ref data), compSize));
        compAccessor.Dispose();

        return true;
    }

    /// <summary>
    /// Update a system entity: allocate new chunk, copy new data, update PK → newChunkId, free old chunk.
    /// </summary>
    public static bool Update<T>(ComponentTable table, long pk, ref T data, EpochManager epochManager, ChangeSet cs) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(epochManager);

        // Lookup old chunkId
        var pkAccessor = table.PrimaryKeyIndex.Segment.CreateChunkAccessor(cs);
        var result = table.PrimaryKeyIndex.TryGet(&pk, ref pkAccessor);

        if (result.IsFailure)
        {
            pkAccessor.Dispose();
            return false;
        }

        int oldChunkId = result.Value;

        // Allocate new chunk
        int newChunkId = table.ComponentSegment.AllocateChunk(false, cs);

        // Copy new data
        var compAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = compAccessor.GetChunkAsSpan(newChunkId, true);
        int overhead = table.ComponentOverhead;
        int compSize = Math.Min(sizeof(T), table.ComponentStorageSize);
        new Span<byte>(Unsafe.AsPointer(ref data), compSize).CopyTo(dst.Slice(overhead));
        compAccessor.Dispose();

        // Update PK B+Tree: remove old, insert new
        table.PrimaryKeyIndex.Remove(&pk, out _, ref pkAccessor);
        table.PrimaryKeyIndex.Add(&pk, newChunkId, ref pkAccessor);
        pkAccessor.Dispose();

        // Free old chunk
        table.ComponentSegment.FreeChunk(oldChunkId);

        return true;
    }
}
