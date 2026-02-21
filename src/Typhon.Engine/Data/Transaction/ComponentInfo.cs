// unset

using System;
using System.Collections.Generic;
using Typhon.Engine.BPTree;

namespace Typhon.Engine;

internal abstract class ComponentInfoBase
{
    [Flags]
    public enum OperationType
    {
        Undefined = 0,
        Created   = 1,
        Read      = 2,
        Updated   = 4,
        Deleted   = 8
    }

    public struct CompRevInfo
    {
        // Current operation type on the component for this transaction
        public OperationType Operations;

        /// ChunkId of the first CompRevTable chunk for the component (the entry point of the chain with the CompRevStorageHeader being used)
        public int CompRevTableFirstChunkId;

        /// The index in the revision table of the revision BEFORE being changed in this transaction. -1 if there's none.
        public short PrevRevisionIndex;

        /// The index in the revision table of the revision being used in this transaction.
        /// This is NOT relative to <see cref="CompRevStorageHeader.FirstItemIndex"/> but to the start of the chain (first element of the first chunk).
        public short CurRevisionIndex;

        /// The ChunkId storing the component content revision BEFORE the transaction (the previous one). 0 if there's none.
        public int PrevCompContentChunkId;

        /// The ChunkId storing the component content corresponding to the revision of this CompRevInfo instance
        public int CurCompContentChunkId;

        /// Monotonic commit counter captured at read time. Compared against header.CommitSequence during commit to detect any commits since our
        /// read — immune to revision index ordering and cleanup compaction that can fool TSN/LCRI-based detection.
        public int ReadCommitSequence;

    }

    public abstract bool IsMultiple { get; }
    public abstract int EntryCount { get; }
    public ComponentTable ComponentTable;
    public ChunkBasedSegment CompContentSegment;
    public ChunkBasedSegment CompRevTableSegment;
    public BTree<long> PrimaryKeyIndex;
    public ChunkAccessor CompContentAccessor;
    public ChunkAccessor CompRevTableAccessor;
    public abstract void AddNew(long pk, CompRevInfo entry);

    /// <summary>
    /// Disposes the ChunkAccessor fields to flush dirty pages.
    /// </summary>
    public void DisposeAccessors()
    {
        CompContentAccessor.Dispose();
        CompRevTableAccessor.Dispose();
    }
}

internal class ComponentInfoSingle : ComponentInfoBase
{
    public override bool IsMultiple => false;
    public override int EntryCount => CompRevInfoCache.Count;

    public override void AddNew(long pk, CompRevInfo entry) => CompRevInfoCache.Add(pk, entry);

    public Dictionary<long, CompRevInfo> CompRevInfoCache;
}

internal class ComponentInfoMultiple : ComponentInfoBase
{
    public override bool IsMultiple => true;
    public override int EntryCount => CompRevInfoCache.Count;

    public override void AddNew(long pk, CompRevInfo entry)
    {
        // We might want to access this component again in the Transaction to let's cache the PK/CompRev
        if (!CompRevInfoCache.TryGetValue(pk, out var list))
        {
            list = [];
            CompRevInfoCache.Add(pk, list);
        }
        list.Add(entry);
    }

    public Dictionary<long, List<CompRevInfo>> CompRevInfoCache;
}
