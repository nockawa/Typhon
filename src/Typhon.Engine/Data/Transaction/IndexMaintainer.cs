// unset

using System;

namespace Typhon.Engine;

internal static unsafe class IndexMaintainer
{
    internal static void UpdateIndices(long pk, ComponentInfo info, ComponentInfo.CompRevInfo compRevInfo, int prevCompChunkId, ChangeSet changeSet, long tsn)
    {
        // If there's a previous revision, we need to update the indices if some indexed fields changed
        var startChunkId = compRevInfo.CompRevTableFirstChunkId;
        if (prevCompChunkId != 0)
        {
            var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId);
            var prevSpan = new Span<byte>(prev, info.ComponentTable.ComponentTotalSize);
            var curSpan = new Span<byte>(cur, info.ComponentTable.ComponentTotalSize);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                // The update changed the field?
                if (prevSpan.Slice(ifi.OffsetToField, ifi.Size).SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)) == false)
                {
                    var accessor = ifi.Index.Segment.CreateChunkAccessor(changeSet);
                    if (ifi.Index.AllowMultiple)
                    {
                        var tailVSBS = info.ComponentTable.TailVSBS;

                        // Compound MoveValue: atomic remove-from-old + insert-under-new in a single traversal.
                        // With TAIL tracking, preserveEmptyBuffer keeps the old HEAD buffer alive for tombstone writes.
                        *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.MoveValue(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField],
                            *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor,
                            out var oldHeadBufferId, out var newHeadBufferId, preserveEmptyBuffer: tailVSBS != null);

                        if (tailVSBS != null)
                        {
                            var tailAccessor = tailVSBS.Segment.CreateChunkAccessor(changeSet);

                            // Tombstone on old key's TAIL buffer
                            if (oldHeadBufferId >= 0)
                            {
                                var oldTailBufferId = GetOrCreateTailBuffer(oldHeadBufferId, tailVSBS, ref accessor, ref tailAccessor);
                                tailVSBS.AddElement(oldTailBufferId, VersionedIndexEntry.Tombstone(startChunkId, tsn), ref tailAccessor);
                            }

                            // Active on new key's TAIL buffer
                            if (newHeadBufferId >= 0)
                            {
                                var newTailBufferId = GetOrCreateTailBuffer(newHeadBufferId, tailVSBS, ref accessor, ref tailAccessor);
                                tailVSBS.AddElement(newTailBufferId, VersionedIndexEntry.Active(startChunkId, tsn), ref tailAccessor);
                            }

                            tailAccessor.Dispose();
                        }
                    }
                    else
                    {
                        // Unique index — compound Move for atomic single-traversal move
                        ifi.Index.Move(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField], startChunkId, ref accessor);
                    }
                    accessor.Dispose();
                }
                else if (ifi.Index.AllowMultiple)
                {
                    // Carry forward the elementId for unchanged AllowMultiple fields so that
                    // the new content chunk has valid buffer references for later removal (e.g., on delete).
                    *(int*)&cur[ifi.OffsetToIndexElementId] = *(int*)&prev[ifi.OffsetToIndexElementId];
                }
            }
        }

        // No previous revision, it means we're adding the first component revision, add the indices
        // But only if this is truly a new component (Created operation), not a resurrection (Updated operation with prevCompChunkId == 0)
        else if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
        {
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId);

            // Update the index with this new entry
            {
                var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(changeSet);
                info.PrimaryKeyIndex.Add(pk, startChunkId, ref accessor);
                accessor.Dispose();
            }

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];

                var accessor = ifi.Index.Segment.CreateChunkAccessor(changeSet);
                if (ifi.Index.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor, out var headBufferId);

                    // TAIL: append Active entry for newly created entity
                    var tailVSBS = info.ComponentTable.TailVSBS;
                    if (tailVSBS != null)
                    {
                        var tailAccessor = tailVSBS.Segment.CreateChunkAccessor(changeSet);
                        var tailBufferId = GetOrCreateTailBuffer(headBufferId, tailVSBS, ref accessor, ref tailAccessor);
                        tailVSBS.AddElement(tailBufferId, VersionedIndexEntry.Active(startChunkId, tsn), ref tailAccessor);
                        tailAccessor.Dispose();
                    }
                }
                else
                {
                    ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                }
                accessor.Dispose();
            }
        }
    }

    internal static void RemoveSecondaryIndices(ComponentInfo info, int prevCompChunkId, int startChunkId, ChangeSet changeSet, long tsn)
    {
        var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
        var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            var accessor = ifi.Index.Segment.CreateChunkAccessor(changeSet);
            if (ifi.Index.AllowMultiple)
            {
                var tailVSBS = info.ComponentTable.TailVSBS;

                // When TAIL tracking is active, preserve the BTree key even if the HEAD buffer empties.
                // This keeps the TAIL version-history buffer reachable for temporal queries.
                ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor,
                    preserveEmptyBuffer: tailVSBS != null);

                // TAIL: append Tombstone for the deleted entity.
                // Since preserveEmptyBuffer keeps the key alive, we can TryGet after RemoveValue
                // and use GetOrCreateTailBuffer which properly links the TAIL to the HEAD root header.
                if (tailVSBS != null)
                {
                    var tailAccessor = tailVSBS.Segment.CreateChunkAccessor(changeSet);
                    var headResult = ifi.Index.TryGet(&prev[ifi.OffsetToField], ref accessor);
                    if (headResult.IsSuccess)
                    {
                        var tailBufId = GetOrCreateTailBuffer(headResult.Value, tailVSBS, ref accessor, ref tailAccessor);
                        tailVSBS.AddElement(tailBufId, VersionedIndexEntry.Tombstone(startChunkId, tsn), ref tailAccessor);
                    }
                    tailAccessor.Dispose();
                }
            }
            else
            {
                ifi.Index.Remove(&prev[ifi.OffsetToField], out _, ref accessor);
            }
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Gets the existing TAIL buffer ID or lazily allocates a new one, storing the link in the HEAD root header's extra header.
    /// </summary>
    /// <param name="headBufferId">Root chunk ID of the HEAD buffer.</param>
    /// <param name="tailVSBS">The TAIL VSBS for VersionedIndexEntry storage.</param>
    /// <param name="headAccessor">ChunkAccessor for the BTree's segment (to read/write HEAD root header).</param>
    /// <param name="tailAccessor">ChunkAccessor for the TailIndexSegment.</param>
    /// <returns>The TAIL buffer ID (existing or newly allocated).</returns>
    private static int GetOrCreateTailBuffer(int headBufferId, VariableSizedBufferSegment<VersionedIndexEntry> tailVSBS,
        ref ChunkAccessor headAccessor, ref ChunkAccessor tailAccessor)
    {
        var chunkAddr = headAccessor.GetChunkAddress(headBufferId, true);
        ref var extra = ref IndexBufferExtraHeader.FromChunkAddress(chunkAddr);
        if (extra.TailBufferId != 0)
        {
            return extra.TailBufferId;
        }

        // Allocate a new TAIL buffer and link it in the extra header
        var tailBufferId = tailVSBS.AllocateBuffer(ref tailAccessor);
        extra.TailBufferId = tailBufferId;
        return tailBufferId;
    }
}
