using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Allow access to chunks in a Chunk based segment
/// </summary>
/// <remarks>
/// This class is not thread-safe
/// </remarks>
public class ChunkRandomAccessor : IDisposable
{
    private static readonly ConcurrentBag<ChunkRandomAccessor> Pool;

    static ChunkRandomAccessor()
    {
        Pool = new ConcurrentBag<ChunkRandomAccessor>();
    }

    internal static ChunkRandomAccessor GetFromPool(ChunkBasedSegment owner, int cachedPagesCount, ChangeSet changeSet)
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
    private Memory<PageAccessor> _cachedPages;
    private Memory<CachedEntry> _cachedEntries;    // We will hit this one very often, so we favor cache locality by putting the PageAccessor in another array
    private int _stride;
    private ChangeSet _changeSet;

    [StructLayout(LayoutKind.Sequential)]
    unsafe private struct CachedEntry
    {
        public int SegmentIndex;
        public int HitCount;
        public short PinCounter;
        public PagedMMF.PageState CurrentPageState;
        public short IsDirty;
        public short PromoteCounter;
        public byte* BaseAddress;
    }

    unsafe public ref readonly T GetChunkReadOnly<T>(int index) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index));
    unsafe public ref T GetChunk<T>(int index, bool dirtyPage = false) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index, dirtyPage: dirtyPage));

    public ChunkBasedSegment Segment => _owner;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void UnpinChunk(int index)
    {
        var (si, _) = _owner.GetChunkLocation(index);
        var caches = _cachedEntries.Span;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (caches[i].SegmentIndex == si)
            {
                --caches[i].PinCounter;
                return;
            }
        }
    }

    internal bool TryPromoteChunk(int index)
    {
        var (si, _) = _owner.GetChunkLocation(index);

        var caches = _cachedEntries.Span;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (caches[i].SegmentIndex == si)
            {
                ref var page = ref caches[i];
                if (page.PromoteCounter > 0)
                {
                    ++page.PromoteCounter;
                    return true;
                }

                if (_cachedPages.Span[i].TryPromoteToExclusive())
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
        var (si, _) = _owner.GetChunkLocation(index);

        var caches = _cachedEntries.Span;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (caches[i].SegmentIndex == si)
            {
                ref var page = ref caches[i];
                if (--page.PromoteCounter == 0)
                {
                    _cachedPages.Span[i].DemoteExclusive();
                }
                return;
            }
        }
    }

    internal void DirtyChunk(int index)
    {
        var (si, _) = _owner.GetChunkLocation(index);

        var caches = _cachedEntries.Span;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            if (caches[i].SegmentIndex == si)
            {
                caches[i].IsDirty = 1;
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public unsafe Span<byte> GetChunkAsSpan(int index, bool pin = false, bool dirtyPage = false) => new(GetChunkAddress(index, pin, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public unsafe ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index, bool pin = false, bool dirtyPage = false) => new(GetChunkAddress(index, pin, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    unsafe internal byte* GetChunkAddress(int index, bool pin = false, bool dirtyPage = false)
    {
        var (si, off) = _owner.GetChunkLocation(index);

        var lowHit = int.MaxValue;
        var pageI = -1;

        var cachedEntries = _cachedEntries.Span;
        for (int i = 0; i < _cachedPagesCount; i++)
        {
            ref var entry = ref cachedEntries[i];
            if (entry.SegmentIndex == si)
            {
                if (entry.CurrentPageState == PagedMMF.PageState.Idle)
                {
                    _owner.GetPageSharedAccessor(si, out _cachedPages.Span[i]);
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
                return entry.BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
            }

            if ((entry.PinCounter == 0) && (entry.PromoteCounter == 0) && (entry.HitCount < lowHit))
            {
                lowHit = entry.HitCount;
                pageI = i;
            }
        }

        // Everything is pinned, that's bad...
        if (pageI == -1)
        {
            throw new NotImplementedException("No more available pages slot, all are occupied by pinned pages");
        }

        ref var cachedEntry = ref _cachedEntries.Span[pageI];
        var cachedPagesAccess = _cachedPages.Span;

        if (cachedEntry.IsDirty != 0 && _changeSet != null)
        {
            _changeSet.Add(cachedPagesAccess[pageI]);
            //cachedPagesAccess[pageI].SetPageDirty();
        }
        cachedPagesAccess[pageI].Dispose();

        cachedEntry.HitCount          = 1;
        cachedEntry.SegmentIndex      = si;
        cachedEntry.PinCounter        = pin ? (short)1 : (short)0;
        cachedEntry.PromoteCounter    = 0;
        cachedEntry.IsDirty           = (short)(dirtyPage ? 1 : 0);
        cachedEntry.CurrentPageState = PagedMMF.PageState.Shared;

        _owner.GetPageSharedAccessor(si, out cachedPagesAccess[pageI]);
        cachedEntry.BaseAddress = cachedPagesAccess[pageI].GetRawDataAddr();

        return cachedEntry.BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
    }

    unsafe internal void ClearChunk(int index)
    {
        var addr = GetChunkAddress(index);
        new Span<long>(addr, _stride / 8).Clear();
    }

    private void Initialize(ChunkBasedSegment owner, int cachedPagesCount, ChangeSet changeSet)
    {
        _owner = owner;
        _changeSet = changeSet;
        var curPagesCount = _cachedPagesCount;
        _cachedPagesCount = cachedPagesCount;

        if (curPagesCount != _cachedPagesCount)
        {
            _cachedPages   = new PageAccessor[cachedPagesCount];
            _cachedEntries = new CachedEntry[cachedPagesCount];
        }

        _stride = _owner.Stride;

        var span = _cachedEntries.Span;
        for (int i = 0; i < span.Length; i++)
        {
            span[i].SegmentIndex = -1;
        }
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
        var cachedPages = _cachedPages.Span;
        var cachedEntries = _cachedEntries.Span;

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
        var cachedPages = _cachedPages.Span;
        var cachedEntries = _cachedEntries.Span;
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
            cachedEntry.SegmentIndex = -1;
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