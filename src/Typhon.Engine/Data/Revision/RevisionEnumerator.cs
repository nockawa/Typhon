using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

[PublicAPI]
internal ref struct RevisionEnumerator : IDisposable
{
    private ref ChunkAccessor _compRevTableAccessor;
    private ChunkHandle _firstChunkHandle;
    private ChunkHandle _curChunkHandle;
    private ref CompRevStorageHeader _header;
    private Span<CompRevStorageElement> _elements;
    private readonly int _firstChunkId;
    private short _itemCountLeft;
    private short _indexInChunk;
    private ref int _nextChunkId;
    private readonly bool _exclusiveAccess;
    private bool _hasLopped;
    private short _revisionIndex;

    public ref CompRevStorageHeader Header => ref _header;
    public int RevisionIndex => _revisionIndex;
    public int IndexInChunk => _indexInChunk;
    public bool HasLopped => _hasLopped;
        
    public ref CompRevStorageElement Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (_itemCountLeft >= 0)
            {
                return ref _elements[_indexInChunk];
            }
            return ref Unsafe.NullRef<CompRevStorageElement>();                
        }
    }
        
    public ref int NextChunkId => ref _nextChunkId;
    public Span<CompRevStorageElement> Elements => _elements;
    public Span<CompRevStorageElement> CurrentAsSpan => _elements.Slice(_indexInChunk, 1);
    public int CurChunkId { get; private set; }
    public bool IsFirstChunk => CurChunkId == _firstChunkId;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (--_itemCountLeft < 0)
        {
            return false;
        }
            
        ++_revisionIndex;
        if (++_indexInChunk == _elements.Length)
        {
            _indexInChunk = 0;
            if (!StepToChunk(1, true))
            {
                return false;
            }
        }

        return true;
    }

    public unsafe RevisionEnumerator(ref ChunkAccessor compRevTableAccessor, int compRevFirstChunkId, bool exclusiveAccess, bool goToFirstItem)
    {
        _compRevTableAccessor = ref compRevTableAccessor;
        _exclusiveAccess = exclusiveAccess;
        _firstChunkId = compRevFirstChunkId;
        _firstChunkHandle = compRevTableAccessor.GetChunkHandle(compRevFirstChunkId, false);
        _header = ref _firstChunkHandle.AsRef<CompRevStorageHeader>();
        if (!_header.Control.IsLockedByCurrentThread)
        {
            _header.Control.Enter(_exclusiveAccess, ref WaitContext.Null);
        }
        _itemCountLeft = _header.ItemCount;
        _nextChunkId = ref _header.NextChunkId;

        _indexInChunk = goToFirstItem ? _header.FirstItemIndex : (short)0;
        if (_indexInChunk < ComponentRevisionManager.CompRevCountInRoot)
        {
            var chunkContent = _firstChunkHandle.AsSpan();
            _nextChunkId = ref chunkContent.Cast<byte, int>()[0];
            _elements = chunkContent.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
            CurChunkId = compRevFirstChunkId;
        }
        else
        {
            var (chunkIndexInChain, index) = CompRevStorageHeader.GetRevisionLocation(_indexInChunk);
            _indexInChunk = (short)index;
            StepToChunk(chunkIndexInChain, false);
        }
        --_indexInChunk;        // We pre-increment in MoveNext, so we start one before
        _revisionIndex = -1;
    }

    public bool StepToChunk(int stepCount, bool loop)
    {
        for (int i = 0; i < stepCount; i++)
        {
            _curChunkHandle.Dispose();
            _curChunkHandle = default;
            if (_nextChunkId == 0)
            {
                if (loop)
                {
                    CurChunkId = _firstChunkId;
                    _curChunkHandle = _compRevTableAccessor.GetChunkHandle(_firstChunkId, false);
                    var stream = _curChunkHandle.AsStream();
                    _nextChunkId = ref stream.PopRef<int>();
                    _elements = stream.PopSpan<CompRevStorageElement>(ComponentRevisionManager.CompRevCountInRoot);
                    _hasLopped = true;
                    return true;
                }

                CurChunkId = -1;
                _nextChunkId = ref Unsafe.NullRef<int>();
                _elements = Span<CompRevStorageElement>.Empty;
                return false;
            }

            {
                CurChunkId = _nextChunkId;
                _curChunkHandle = _compRevTableAccessor.GetChunkHandle(_nextChunkId, false);
                var stream = _curChunkHandle.AsStream();
                _nextChunkId = ref stream.PopRef<int>();
                _elements = stream.PopSpan<CompRevStorageElement>(ComponentRevisionManager.CompRevCountInNext);
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (!_header.Control.IsLockedByCurrentThread)
        {
            _header.Control.Exit(_exclusiveAccess);
        }
        _firstChunkHandle.Dispose();
        _curChunkHandle.Dispose();
    }
}