// unset

using System;

namespace Typhon.Engine;

internal static unsafe class IndexMaintainer
{
    internal static void UpdateIndices(long pk, ComponentInfo info, ComponentInfo.CompRevInfo compRevInfo, int prevCompChunkId, ChangeSet changeSet)
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
                        ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor);
                        *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                    }
                    else
                    {
                        ifi.Index.Remove(&prev[ifi.OffsetToField], out var val, ref accessor);
                        ifi.Index.Add(&cur[ifi.OffsetToField], val, ref accessor);
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
                    *(int*)&cur[ifi.OffsetToIndexElementId] = ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                }
                else
                {
                    ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                }
                accessor.Dispose();
            }
        }
    }

    internal static void RemoveSecondaryIndices(ComponentInfo info, int prevCompChunkId, int startChunkId, ChangeSet changeSet)
    {
        var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
        var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            var accessor = ifi.Index.Segment.CreateChunkAccessor(changeSet);
            if (ifi.Index.AllowMultiple)
            {
                ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor);
            }
            else
            {
                ifi.Index.Remove(&prev[ifi.OffsetToField], out _, ref accessor);
            }
            accessor.Dispose();
        }
    }
}
