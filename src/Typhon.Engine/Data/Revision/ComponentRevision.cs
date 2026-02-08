using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

[PublicAPI]
internal ref struct ComponentRevision : IDisposable
{
    private ref ChunkAccessor _accessor;
    private readonly Transaction.ComponentInfoBase _info;
    private readonly int _firstChunkId;
    private readonly ref Transaction.ComponentInfoBase.CompRevInfo _compRevInfo;
    private ChunkHandle _firstHandle;

    internal ComponentRevision(Transaction.ComponentInfoBase info, ref Transaction.ComponentInfoBase.CompRevInfo compRevInfo, int firstChunkId,
        ref ChunkAccessor accessor)
    {
        _accessor = ref accessor;
        _info = info;
        _firstChunkId = firstChunkId;
        _compRevInfo = ref compRevInfo;
        _firstHandle = _accessor.GetChunkHandle(firstChunkId, false);
    }
    
    internal short LastCommitRevisionIndex => _firstHandle.AsRef<CompRevStorageHeader>().LastCommitRevisionIndex;
    internal void SetLastCommitRevisionIndex(short index) => _firstHandle.AsRef<CompRevStorageHeader>().LastCommitRevisionIndex = index;
    
    internal ComponentRevisionManager.ElementRevisionHandle GetRevisionElement(short revisionIndex)
        => ComponentRevisionManager.GetRevisionElement(ref _accessor, _firstChunkId, revisionIndex);
    internal void AddCompRev(long tsn, bool isDelete)
        => ComponentRevisionManager.AddCompRev(_info, ref _compRevInfo, tsn, isDelete);
    internal int AllocCompRevStorage(long tsn) => ComponentRevisionManager.AllocCompRevStorage(_info, tsn, _firstChunkId);
    internal bool CleanUpUnusedEntries(long nextMinTSN)
        => ComponentRevisionManager.CleanUpUnusedEntries(_info, ref _compRevInfo, ref _accessor, nextMinTSN);
    
    public void VoidElement(ComponentRevisionManager.ElementRevisionHandle elementRevisionHandle)
    {
        using var firstHandle = _accessor.GetChunkHandle(_firstChunkId, false);
        ref var firstHeader = ref firstHandle.AsRef<CompRevStorageHeader>();
        --firstHeader.ItemCount;
        elementRevisionHandle.Element.Void();
    }

    public void Dispose() => _firstHandle.Dispose();
}