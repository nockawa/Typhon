using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

[PublicAPI]
internal ref struct RevisionWalker : IDisposable
{
    private ref ChunkAccessor _accessor;
    private readonly int _firstChunkId;
    private ChunkHandle _firstChunkHandle;
    private ChunkHandle _curChunkHandle;
    private readonly ref CompRevStorageHeader _header;
    private Span<CompRevStorageElement> _elements;
    private ref int _nextChunkId;
    private int _curChunkId;
        
    public ref CompRevStorageHeader Header => ref _header;
    public int CurChunkId => _curChunkId;
    public ref int NextChunkId => ref _nextChunkId;
    public Span<CompRevStorageElement> Elements => _elements;

    public RevisionWalker(ref ChunkAccessor accessor, int firstChunkId)
    {
        _accessor = ref accessor;
        _firstChunkId = firstChunkId;
        _firstChunkHandle = accessor.GetChunkHandle(firstChunkId, false);
        _header = ref _firstChunkHandle.AsRef<CompRevStorageHeader>();
        _curChunkId = firstChunkId;
        _curChunkHandle = accessor.GetChunkHandle(firstChunkId, false);
        var stream = _curChunkHandle.AsStream();
        _nextChunkId = ref stream.PopRef<int>();
        _elements = stream.PopSpan<CompRevStorageElement>(ComponentRevisionManager.CompRevCountInRoot);
    }

    public bool Step(int stepCount, bool loop, out bool hasLopped)
    {
        hasLopped = false;
        for (int i = 0; i < stepCount; i++)
        {
            if (_nextChunkId == 0 && !loop)
            {
                return false;
            }
            var nextChunkId = _nextChunkId;
            if (_nextChunkId == 0)
            {
                hasLopped = true;
                nextChunkId = _firstChunkId;
            }

            _curChunkHandle.Dispose();
            _curChunkId = nextChunkId;
            _curChunkHandle = _accessor.GetChunkHandle(nextChunkId, false);
            var stream = _curChunkHandle.AsStream();
            _nextChunkId = ref stream.PopRef<int>();
            _elements = stream.PopSpan<CompRevStorageElement>(ComponentRevisionManager.CompRevCountInNext);
        }
        return true;
    }
        
    public void Dispose()
    {
        _firstChunkHandle.Dispose();
        _curChunkHandle.Dispose();
    }
}