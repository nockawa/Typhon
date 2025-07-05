// unset

using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    public class LogicalSegmentManager : IInitializable, IDisposable
    {
        private readonly ILogger<LogicalSegmentManager> _log;
        private readonly IConfiguration<DatabaseConfiguration> _dbc;
        private readonly VirtualDiskManager _vdm;
        private readonly DiskPageAllocator _dpa;

        internal VirtualDiskManager VDM => _vdm;
        internal DiskPageAllocator DPA => _dpa;

        private ConcurrentDictionary<uint, LogicalSegment> _segments;

        public LogicalSegmentManager(IConfiguration<DatabaseConfiguration> dbc, VirtualDiskManager vdm, DiskPageAllocator dpa, ILogger<LogicalSegmentManager> log)
        {
            _dbc = dbc;
            _vdm = vdm;
            _dpa = dpa;
            _log = log;

            _segments = new ConcurrentDictionary<uint, LogicalSegment>();
        }

        public LogicalSegment GetSegment(uint pageId)
        {
            var dic = _segments;
            return dic?.GetOrAdd(pageId, pid =>
            {
                var segment = new LogicalSegment(this);
                segment.Load(pid);
                return segment;
            });
        }

        internal LogicalSegment CreateOccupancySegment(uint pageId, PageBlockType type, int length)
        {
            var dic = _segments;
            if (dic == null)
            {
                return null;
            }

            var segment = new LogicalSegment(this);
            if (dic.TryAdd(pageId, segment) == false)
            {
                return null;
            }

            if (segment.Create(type, pageId, true) == false)
            {
                return null;
            }

            _log.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
            return segment;
        }

        public LogicalSegment AllocateSegment(PageBlockType type, int length)
        {
            var dic = _segments;
            if (dic == null)
            {
                return null;
            }

            Span<uint> pages = stackalloc uint[length];
            _dpa.AllocatePages(ref pages);

            var segment = new LogicalSegment(this);
            if (dic.TryAdd(pages[0], segment) == false)
            {
                Debug.Assert(true);
            }

            if (segment.Create(type, pages, false) == false)
            {
                return null;
            }

            _log.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
            return segment;
        }

        public ChunkBasedSegment AllocateChunkBasedSegment(PageBlockType type, int length, int stride)
        {
            var dic = _segments;
            if (dic == null)
            {
                return null;
            }

            Span<uint> pages = stackalloc uint[length];
            _dpa.AllocatePages(ref pages);

            var segment = new ChunkBasedSegment(this, stride);
            if (dic.TryAdd(pages[0], segment) == false)
            {
                Debug.Assert(true);
            }

            if (segment.Create(type, pages, false) == false)
            {
                return null;
            }

            _log.LogDebug("Create Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
            return segment;
        }

        public bool DeleteSegment(uint pageId)
        {
            var dic = _segments;
            if (dic == null)
            {
                return false;
            }

            if (dic.TryRemove(pageId, out var segment) == false)
            {
                return false;
            }

            _dpa.FreePages(segment.Pages);
            return true;
        }

        public bool DeleteSegment(LogicalSegment segment) => DeleteSegment(segment.RootPageId);

        public void Initialize()
        {
            ++ReferenceCounter;
            if (IsInitialized)
            {
                return;
            }
            _vdm.Initialize();
            _dpa.Initialize();

            IsInitialized = true;
        }

        public void Dispose()
        {
            if (IsDisposed || --ReferenceCounter != 0)
            {
                return;
            }

            var dic = Interlocked.Exchange(ref _segments, null);
            foreach (var segment in dic.Values)
            {
                segment.Dispose();
            }

            _dpa.Dispose();
            _vdm.Dispose();

            IsDisposed = true;
        }

        public bool IsInitialized { get; private set; }
        public bool IsDisposed { get; private set; }
        public int ReferenceCounter { get; private set; }
    }

    internal static class SpanHelpers
    {
        public static Span<TTo> Cast<TFRom, TTo>(this Span<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
        public static ReadOnlySpan<TTo> Cast<TFRom, TTo>(this ReadOnlySpan<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
    }
}