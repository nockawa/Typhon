// unset

namespace Typhon.Engine;

/// <summary>
/// Shared revision chain walk logic used by <see cref="Transaction"/> to resolve visible revisions.
/// Extracted from the duplicated inner loops of the two <c>GetCompRevInfoFromIndex</c> overloads.
/// </summary>
internal static class RevisionChainReader
{
    /// <summary>
    /// Walks a revision chain and returns the <see cref="ComponentInfo.CompRevInfo"/> for the latest
    /// visible revision at the given <paramref name="transactionTSN"/>.
    /// </summary>
    /// <param name="compRevTableAccessor">Accessor for reading revision table chunks.</param>
    /// <param name="compRevFirstChunkId">First chunk ID of the entity's revision chain.</param>
    /// <param name="transactionTSN">The reader's snapshot TSN — entries with TSN &gt; this are invisible.</param>
    /// <returns>
    /// <see cref="RevisionReadStatus.Success"/> with revision metadata on success;
    /// <see cref="RevisionReadStatus.SnapshotInvisible"/> if no committed entry is visible;
    /// <see cref="RevisionReadStatus.Deleted"/> if the latest visible entry is a tombstone (ComponentChunkId == 0).
    /// </returns>
    internal static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChain(
        ref ChunkAccessor compRevTableAccessor, int compRevFirstChunkId, long transactionTSN)
    {
        short prevCompRevisionIndex = -1;
        short curCompRevisionIndex = -1;
        int prevCompChunkId = 0;
        int curCompChunkId = 0;

        // CommitSequence must be captured INSIDE the shared lock (held by RevisionEnumerator) so that ReadCS and the chain walk observe the same consistent
        // chain state. Capturing it outside the lock creates a race: cleanup or another commit can modify the chain between ReadCS capture and the lock
        // acquisition, leaving ReadCS consistent with a state the chain walk never sees.
        int readCommitSequence;

        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true);
            readCommitSequence = compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).CommitSequence;
            while (enumerator.MoveNext())
            {
                ref var element = ref enumerator.Current;

                // Skip voided entries (rolled-back revisions cleared by cleanup or explicit void)
                if (element.IsVoid)
                {
                    continue;
                }

                // Do NOT break on TSN > reader.TSN — entries in the chain are NOT guaranteed to be in monotonically increasing TSN order. A higher-TSN
                // transaction can write (AddCompRev) before a lower-TSN transaction, placing its entry at a lower index. Breaking early would miss
                // committed entries with lower TSN at higher indices.
                if (element.TSN > transactionTSN)
                {
                    continue;
                }

                // Update the current revision (and the previous) if a valid entry (tick == 0 means a rollbacked entry) and it's not an isolated one
                if ((element.TSN > 0) && !element.IsolationFlag)
                {
                    prevCompRevisionIndex = curCompRevisionIndex;
                    prevCompChunkId = curCompChunkId;
                    curCompRevisionIndex = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                    curCompChunkId = element.ComponentChunkId;
                }
            }
        }

        if (curCompRevisionIndex == -1)
        {
            return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(RevisionReadStatus.SnapshotInvisible);
        }

        var compRevInfo = new ComponentInfo.CompRevInfo
        {
            Operations = ComponentInfo.OperationType.Undefined,
            CompRevTableFirstChunkId = compRevFirstChunkId,
            CurCompContentChunkId = curCompChunkId,
            CurRevisionIndex = curCompRevisionIndex,
            PrevCompContentChunkId = prevCompChunkId,
            PrevRevisionIndex = prevCompRevisionIndex,
            ReadCommitSequence = readCommitSequence
        };

        // Tombstoned entity: carry the value (callers like UpdateComponent need revision metadata) but signal Deleted
        if (curCompChunkId == 0)
        {
            return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo, RevisionReadStatus.Deleted);
        }

        return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo);
    }
}
