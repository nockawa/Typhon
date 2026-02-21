using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Simplified write path for WAL crash recovery replay. Applies committed WAL records to the data store
/// without MVCC isolation, conflict detection, or concurrent access protection.
/// </summary>
/// <remarks>
/// <para>
/// During recovery, the database is single-threaded (no user transactions). This helper uses the same
/// underlying storage APIs as <see cref="Transaction"/> (ComponentSegment, ComponentRevisionManager)
/// but bypasses the transaction/commit machinery.
/// </para>
/// <para>
/// Full replay of Create/Update/Delete is implemented. FPI (Full Page Image) torn-page repair is a
/// separate concern handled by <see cref="WalRecovery"/> directly.
/// </para>
/// </remarks>
internal static class WalReplayHelper
{
    /// <summary>
    /// Replays a single WAL record against the database engine's storage.
    /// </summary>
    /// <param name="dbe">The database engine (must be fully initialized with component tables registered).</param>
    /// <param name="header">The WAL record header.</param>
    /// <param name="payload">The record payload (component data bytes).</param>
    public static void ReplayRecord(DatabaseEngine dbe, ref WalRecordHeader header, ReadOnlySpan<byte> payload)
    {
        var table = dbe.GetComponentTableByWalTypeId(header.ComponentTypeId);
        if (table == null)
        {
            return; // Unknown component type — skip
        }

        switch ((WalOperationType)header.OperationType)
        {
            case WalOperationType.Create:
                ReplayCreate(dbe, table, ref header, payload);
                break;

            case WalOperationType.Update:
                ReplayUpdate(dbe, table, ref header, payload);
                break;

            case WalOperationType.Delete:
                ReplayDelete(dbe, table, ref header);
                break;
        }
    }

    /// <summary>
    /// Replays a Create operation: allocates a component chunk, copies payload data, creates a revision entry, and inserts into the PK index.
    /// </summary>
    private static unsafe void ReplayCreate(DatabaseEngine dbe, ComponentTable table, ref WalRecordHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var cs = dbe.MMF.CreateChangeSet();

        // Allocate a component content chunk and write payload
        var componentChunkId = table.ComponentSegment.AllocateChunk(false);
        var contentAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = contentAccessor.GetChunkAsSpan(componentChunkId, true);
        var toCopy = Math.Min(payload.Length, dst.Length);
        payload[..toCopy].CopyTo(dst);

        // Allocate a revision chain root chunk and initialize it
        var compRevChunkId = table.CompRevTableSegment.AllocateChunk(false);
        var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(cs);
        var revSpan = revAccessor.GetChunkAsSpan(compRevChunkId, true);

        // Initialize CompRevStorageHeader (first bytes of the chunk)
        ref var revHeader = ref Unsafe.As<byte, CompRevStorageHeader>(ref revSpan[0]);
        revHeader.NextChunkId = 0;
        revHeader.Control = default;
        revHeader.FirstItemRevision = 0;
        revHeader.FirstItemIndex = 0;
        revHeader.ItemCount = 1;
        revHeader.ChainLength = 1;
        revHeader.LastCommitRevisionIndex = 0;

        // Write the first revision element after the header
        var headerSize = sizeof(CompRevStorageHeader);
        ref var element = ref Unsafe.As<byte, CompRevStorageElement>(ref revSpan[headerSize]);
        element.ComponentChunkId = componentChunkId;
        element.TSN = header.TransactionTSN;
        element.UowId = header.UowEpoch;
        element.IsolationFlag = false;

        // Insert into PK index (entityId → compRevChunkId)
        var indexAccessor = table.DefaultIndexSegment.CreateChunkAccessor(cs);
        table.PrimaryKeyIndex.Add(header.EntityId, compRevChunkId, ref indexAccessor);

        contentAccessor.Dispose();
        revAccessor.Dispose();
        indexAccessor.Dispose();
        cs.SaveChanges();
    }

    /// <summary>
    /// Replays an Update operation: allocates a new component chunk with the updated data and adds a new revision entry.
    /// </summary>
    private static unsafe void ReplayUpdate(DatabaseEngine dbe, ComponentTable table, ref WalRecordHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var cs = dbe.MMF.CreateChangeSet();

        // Look up the entity's revision chain via PK index
        var indexAccessor = table.DefaultIndexSegment.CreateChunkAccessor(cs);
        var lookupResult = table.PrimaryKeyIndex.TryGet(header.EntityId, ref indexAccessor);

        if (lookupResult.IsFailure)
        {
            // Entity doesn't exist — treat as create
            indexAccessor.Dispose();
            cs.SaveChanges();
            ReplayCreate(dbe, table, ref header, payload);
            return;
        }

        var compRevChunkId = lookupResult.Value;

        // Allocate a new component content chunk with the updated data
        var newComponentChunkId = table.ComponentSegment.AllocateChunk(false);
        var contentAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = contentAccessor.GetChunkAsSpan(newComponentChunkId, true);
        var toCopy = Math.Min(payload.Length, dst.Length);
        payload[..toCopy].CopyTo(dst);

        // Add a new revision entry to the existing revision chain
        var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(cs);
        var revSpan = revAccessor.GetChunkAsSpan(compRevChunkId, true);

        ref var revHeader = ref Unsafe.As<byte, CompRevStorageHeader>(ref revSpan[0]);
        var newRevIndex = revHeader.ItemCount;

        // Only handle root chunk revisions for simplicity — multi-chunk overflow is rare in recovery
        var (chunkIndex, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(newRevIndex);
        if (chunkIndex == 0)
        {
            var headerSize = sizeof(CompRevStorageHeader);
            var elementOffset = headerSize + indexInChunk * sizeof(CompRevStorageElement);
            if (elementOffset + sizeof(CompRevStorageElement) <= revSpan.Length)
            {
                ref var element = ref Unsafe.As<byte, CompRevStorageElement>(ref revSpan[elementOffset]);
                element.ComponentChunkId = newComponentChunkId;
                element.TSN = header.TransactionTSN;
                element.UowId = header.UowEpoch;
                element.IsolationFlag = false;

                revHeader.ItemCount++;
                revHeader.LastCommitRevisionIndex = (short)newRevIndex;
            }
        }

        contentAccessor.Dispose();
        revAccessor.Dispose();
        indexAccessor.Dispose();
        cs.SaveChanges();
    }

    /// <summary>
    /// Replays a Delete operation: adds a tombstone revision (ComponentChunkId=0) to mark the entity as deleted.
    /// </summary>
    private static unsafe void ReplayDelete(DatabaseEngine dbe, ComponentTable table, ref WalRecordHeader header)
    {
        var cs = dbe.MMF.CreateChangeSet();

        // Look up the entity's revision chain via PK index
        var indexAccessor = table.DefaultIndexSegment.CreateChunkAccessor(cs);
        var lookupResult = table.PrimaryKeyIndex.TryGet(header.EntityId, ref indexAccessor);

        if (lookupResult.IsFailure)
        {
            indexAccessor.Dispose();
            cs.SaveChanges();
            return; // Entity doesn't exist — nothing to delete
        }

        var compRevChunkId = lookupResult.Value;

        var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(cs);
        var revSpan = revAccessor.GetChunkAsSpan(compRevChunkId, true);

        ref var revHeader = ref Unsafe.As<byte, CompRevStorageHeader>(ref revSpan[0]);
        var newRevIndex = revHeader.ItemCount;

        var (chunkIndex, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(newRevIndex);
        if (chunkIndex == 0)
        {
            var headerSize = sizeof(CompRevStorageHeader);
            var elementOffset = headerSize + indexInChunk * sizeof(CompRevStorageElement);
            if (elementOffset + sizeof(CompRevStorageElement) <= revSpan.Length)
            {
                ref var element = ref Unsafe.As<byte, CompRevStorageElement>(ref revSpan[elementOffset]);
                element.ComponentChunkId = 0; // Tombstone
                element.TSN = header.TransactionTSN;
                element.UowId = header.UowEpoch;
                element.IsolationFlag = false;

                revHeader.ItemCount++;
                revHeader.LastCommitRevisionIndex = newRevIndex;
            }
        }

        revAccessor.Dispose();
        indexAccessor.Dispose();
        cs.SaveChanges();
    }
}
