using JetBrains.Annotations;

namespace Typhon.Engine;

[PublicAPI]
internal ref struct ComponentRevision
{
    private ref EpochChunkAccessor _accessor;
    private readonly Transaction.ComponentInfoBase _info;
    private readonly int _firstChunkId;
    private readonly ref Transaction.ComponentInfoBase.CompRevInfo _compRevInfo;

    internal ComponentRevision(Transaction.ComponentInfoBase info, ref Transaction.ComponentInfoBase.CompRevInfo compRevInfo, int firstChunkId,
        ref EpochChunkAccessor accessor)
    {
        _accessor = ref accessor;
        _info = info;
        _firstChunkId = firstChunkId;
        _compRevInfo = ref compRevInfo;
    }

    internal short LastCommitRevisionIndex => _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId).LastCommitRevisionIndex;
    internal void SetLastCommitRevisionIndex(short index) => _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId, true).LastCommitRevisionIndex = index;

    internal ComponentRevisionManager.ElementRevisionHandle GetRevisionElement(short revisionIndex)
        => ComponentRevisionManager.GetRevisionElement(ref _accessor, _firstChunkId, revisionIndex);
    internal void AddCompRev(long tsn, bool isDelete)
        => ComponentRevisionManager.AddCompRev(_info, ref _compRevInfo, tsn, isDelete);
    internal int AllocCompRevStorage(long tsn) => ComponentRevisionManager.AllocCompRevStorage(_info, tsn, _firstChunkId);
    internal bool CleanUpUnusedEntries(long nextMinTSN)
        => ComponentRevisionManager.CleanUpUnusedEntries(_info, ref _compRevInfo, ref _accessor, nextMinTSN);

    public void VoidElement(ComponentRevisionManager.ElementRevisionHandle elementRevisionHandle)
    {
        ref var firstHeader = ref _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId, true);
        --firstHeader.ItemCount;
        elementRevisionHandle.Element.Void();
    }
}
