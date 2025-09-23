using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[PublicAPI]
public unsafe ref struct ChunkHandle : IDisposable
{
    private ChunkRandomAccessor _owner;
    private byte* _chunkDataAddress;        // Unfortunately, storing a Span<T> would take 16 bytes, then rounding up this struct to 32.
    private int _chunkDataLength;           // By storing the address and size manually we save 8 bytes.
    private int _entryIndex;

    public ChunkHandle(ChunkRandomAccessor owner, int entryIndex, byte* chunkDataAddress, int chunkDataLength)
    {
        _owner = owner;
        _chunkDataAddress = chunkDataAddress;
        _chunkDataLength = chunkDataLength;
        _entryIndex = entryIndex;
    }

    public void Dispose()
    {
        _owner?.UnpinEntry(_entryIndex);
        _chunkDataAddress = null;
    }
    
    public bool IsDisposed => _chunkDataAddress == null;
    public bool IsDefault => _chunkDataAddress == null;
    public void Dirty(int index) => _owner.DirtyEntry(index);
    
    public Span<byte> AsSpan() => new(_chunkDataAddress, _chunkDataLength);
    public ReadOnlySpan<byte> AsReadOnlySpan() => new(_chunkDataAddress, _chunkDataLength);
    public ref T AsRef<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_chunkDataAddress);
    public SpanStream AsStream() => new SpanStream(new Span<byte>(_chunkDataAddress, _chunkDataLength));
    public readonly ref T AsReadOnlyRef<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_chunkDataAddress);
}

/// <summary>
/// Allow access to chunks in a Chunk based segment
/// </summary>
/// <remarks>
/// This class is not thread-safe.
/// When you access a Chunk, you have the possibility to pin it or not. Pinning is mandatory if you will make other chunk access while you use the first one.
/// Accessing another chunk might kick the page of the one you're using, pinning prevent that. But as long as you don't request another chunk, you are safe.
/// </remarks>
public unsafe class ChunkRandomAccessor : IDisposable
{
    private static readonly ConcurrentBag<ChunkRandomAccessor> Pool;

    static ChunkRandomAccessor()
    {
        Pool = new ConcurrentBag<ChunkRandomAccessor>();
    }

    internal static ChunkRandomAccessor GetFromPool(ChunkBasedSegment owner, int cachedPagesCount, ChangeSet changeSet = null)
    {
        if (!Pool.TryTake(out var cra))
        {
            cra = new ChunkRandomAccessor();
        }

        cra.Initialize(owner, cachedPagesCount, changeSet);
        return cra;
    }

    private ChunkBasedSegment _owner;
    private int _cachedPagesCount;
    private PageAccessor[] _cachedPages;
    private CachedEntry[] _cachedEntries;
    private int[] _pageIndices;
    private int _stride;
    private ChangeSet _changeSet;

    [StructLayout(LayoutKind.Sequential)]
    private struct CachedEntry
    {
        public int HitCount;
        public short PinCounter;
        public PagedMMF.PageState CurrentPageState;
        public short IsDirty;
        public short PromoteCounter;
        public byte* BaseAddress;
    }

    public ref readonly T GetChunkReadOnly<T>(int index) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index));
    public ref T GetChunk<T>(int index, bool dirtyPage = false) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index, dirtyPage: dirtyPage));

    public ChunkBasedSegment Segment => _owner;
    public ChangeSet ChangeSet => _changeSet;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void UnpinChunk(int index)
    {
        (int si, _) = _owner.GetChunkLocation(index);
        var caches = _cachedEntries;
        var pageIndices = _pageIndices;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (pageIndices[i] == si)
            {
                --caches[i].PinCounter;
                return;
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void UnpinEntry(int entryIndex) => --_cachedEntries[entryIndex].PinCounter;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void DirtyEntry(int entryIndex) => _cachedEntries[entryIndex].IsDirty = 1;


    internal bool TryPromoteChunk(int index)
    {
        (int si, _) = _owner.GetChunkLocation(index);

        var caches = _cachedEntries;
        var pageIndices = _pageIndices;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (pageIndices[i] == si)
            {
                ref var page = ref caches[i];
                if (page.PromoteCounter > 0)
                {
                    ++page.PromoteCounter;
                    return true;
                }

                if (_cachedPages[i].TryPromoteToExclusive())
                {
                    page.PromoteCounter = 1;
                    page.CurrentPageState = PagedMMF.PageState.Exclusive;
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    internal void DemoteChunk(int index)
    {
        (int si, _) = _owner.GetChunkLocation(index);

        var caches = _cachedEntries;
        var pageIndices = _pageIndices;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (pageIndices[i] == si)
            {
                ref var page = ref caches[i];
                if (--page.PromoteCounter == 0)
                {
                    _cachedPages[i].DemoteExclusive();
                }
                return;
            }
        }
    }

    internal void DirtyChunk(int index)
    {
        var (si, _) = _owner.GetChunkLocation(index);

        var caches = _cachedEntries;
        var pageIndices = _pageIndices;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (pageIndices[i] == si)
            {
                caches[i].IsDirty = 1;
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Span<byte> GetChunkAsSpan(int index, bool pin = false, bool dirtyPage = false) => new(GetChunkAddress(index, pin, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index, bool pin = false, bool dirtyPage = false) => new(GetChunkAddress(index, pin, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal byte* GetChunkAddress(int index, bool pin = false, bool dirtyPage = false)
    {
        (int si, int off) = _owner.GetChunkLocation(index);

        var baseAddress = GetPageRawDataAddr(si, pin, dirtyPage, out _);
        return baseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ChunkHandle GetChunkHandle(int index, bool dirty)
    {
        (int si, int off) = _owner.GetChunkLocation(index);
        var baseAddress = GetPageRawDataAddr(si, true, false, out var entryIndex);
        var chunkAddress = baseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
        if (dirty && _changeSet != null)
        {
            _changeSet.Add(_cachedPages[entryIndex]);
        }
        return new ChunkHandle(this, entryIndex, chunkAddress, _stride);
    }
    
    internal ref T GetChunkBasedSegmentHeader<T>(int offset, bool dirtyPage, out int cacheEntryIndex) where T : unmanaged
    {
        var baseAddress = GetPageRawDataAddr(0, true, dirtyPage, out cacheEntryIndex) - PagedMMF.PageHeaderSize;
        return ref Unsafe.AsRef<T>(baseAddress + offset);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void UnpinChunkBasedSegmentHeader(int cacheEntryIndex) => --_cachedEntries[cacheEntryIndex].PinCounter;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private byte* GetPageRawDataAddr(int pageIndex, bool pin, bool dirtyPage, out int cacheEntryIndex)
    {
        var lowHit = int.MaxValue;
        var pageI = -1;

        var cachedEntries = _cachedEntries;
        var cachedPagesAccess = _cachedPages;
        var pageIndices = _pageIndices;
        for (cacheEntryIndex = 0; cacheEntryIndex < _cachedPagesCount; cacheEntryIndex++)
        {
            ref var entry = ref cachedEntries[cacheEntryIndex];
            if (pageIndices[cacheEntryIndex] == pageIndex)
            {
                if (entry.CurrentPageState == PagedMMF.PageState.Idle)
                {
                    _owner.GetPageSharedAccessor(pageIndex, out cachedPagesAccess[cacheEntryIndex]);
                    entry.CurrentPageState = PagedMMF.PageState.Shared;
                }

                if (pin)
                {
                    ++entry.PinCounter;
                }

                if (dirtyPage)
                {
                    entry.IsDirty = 1;
                }
                ++entry.HitCount;
                return entry.BaseAddress;
            }

            if ((entry.PinCounter == 0) && (entry.PromoteCounter == 0) && (entry.HitCount < lowHit))
            {
                lowHit = entry.HitCount;
                pageI = cacheEntryIndex;
            }
        }

        // Everything is pinned, that's bad...
        if (pageI == -1)
        {
            throw new NotImplementedException("No more available pages slot, all are occupied by pinned pages");
        }

        cacheEntryIndex = pageI;
        ref var cachedEntry = ref _cachedEntries[pageI];

        if (cachedEntry.IsDirty != 0 && _changeSet != null)
        {
            _changeSet.Add(cachedPagesAccess[pageI]);
            //cachedPagesAccess[pageI].SetPageDirty();
        }
        cachedPagesAccess[pageI].Dispose();

        pageIndices[pageI]            = pageIndex;
        cachedEntry.HitCount          = 1;
        cachedEntry.PinCounter        = pin ? (short)1 : (short)0;
        cachedEntry.PromoteCounter    = 0;
        cachedEntry.IsDirty           = (short)(dirtyPage ? 1 : 0);
        cachedEntry.CurrentPageState  = PagedMMF.PageState.Shared;

        _owner.GetPageSharedAccessor(pageIndex, out cachedPagesAccess[pageI]);
        cachedEntry.BaseAddress = cachedPagesAccess[pageI].GetRawDataAddr();
        
        return cachedEntry.BaseAddress;
    }

    internal void ClearChunk(int index)
    {
        var addr = GetChunkAddress(index);
        new Span<long>(addr, _stride / 8).Clear();
    }

    private void Initialize(ChunkBasedSegment owner, int cachedPagesCount, ChangeSet changeSet = null)
    {
        _owner = owner;
        _changeSet = changeSet;
        var curPagesCount = _cachedPagesCount;
        _cachedPagesCount = cachedPagesCount;

        if (curPagesCount < _cachedPagesCount)
        {
            _cachedPages   = new PageAccessor[cachedPagesCount];
            _cachedEntries = new CachedEntry[cachedPagesCount];
            _pageIndices = new int[cachedPagesCount];
        }

        _stride = _owner.Stride;
        _cachedEntries.AsSpan().Clear();
        _pageIndices.AsSpan().Fill(-1);
    }

    /// <summary>
    /// Commit the dirty state of each page and release the shared access.
    /// </summary>
    /// <remarks>
    /// Typically call this method at the end of an atomic operation to update the <see cref="PagedMMF"/> accordingly.
    /// If the page was promoted in exclusive mode, it won't be release, just simply ignored.
    /// </remarks>
    public void CommitChanges()
    {
        var cachedPages = _cachedPages;
        var cachedEntries = _cachedEntries;

        for (int i = 0; i < _cachedPagesCount; i++)
        {
            ref var cachedEntry = ref cachedEntries[i];
            if (cachedEntry.CurrentPageState != PagedMMF.PageState.Shared ||
                cachedEntry.PromoteCounter != 0 ||
                cachedEntry.PinCounter != 0) continue;

            ref var cachedPage = ref cachedPages[i];

            if (cachedEntry.IsDirty != 0)
            {
                _changeSet?.Add(cachedPage);
                // cachedPage.SetPageDirty();
                cachedEntry.IsDirty = 0;
            }

            cachedEntry.CurrentPageState = PagedMMF.PageState.Idle;
            cachedPage.Dispose();
        }
    }

    public bool DisposePageAccessors()
    {
        var cachedPages = _cachedPages;
        var cachedEntries = _cachedEntries;
        var res = true;

        for (int i = 0; i < _cachedPagesCount; i++)
        {
            ref var cachedPage = ref cachedPages[i];
            ref var cachedEntry = ref cachedEntries[i];

            if (cachedEntry.IsDirty != 0)
            {
                _changeSet?.Add(cachedPage);
                // cachedPage.SetPageDirty();
                cachedEntry.IsDirty = 0;
            }

            // Can't dispose if there are still operations ongoing that required their counterpart method to finish them
            if ((cachedEntry.PromoteCounter != 0) || (cachedEntry.PinCounter != 0))
            {
                res = false;
                continue;
            }
                
            cachedPage.Dispose();
            cachedEntry.CurrentPageState = PagedMMF.PageState.Idle;
            cachedEntry.HitCount = 0;
            _pageIndices[i] = -1;
        }

        return res;
    }

    public void Dispose()
    {
        if (!DisposePageAccessors())
        {
            throw new InvalidOperationException("Can't dispose the ChunkRandomAccess: some pages are still promoted and/or pinned.");
        }
        Pool.Add(this);
    }
}