using System;
using System.Diagnostics;

namespace Typhon.Engine
{
    public class PersistentDataAccess : IDisposable
    {
        private readonly DatabaseConfiguration _configuration;
        private readonly TimeManager _timeManager;
        
        //private readonly List<MemoryMappedFile> _memoryMappedFiles;
        private bool _isDisposed;
        //private int _fileChunkSizePower2;
        //private int _fileChunkIndexShift;
        //private uint _fileChunkMask;
        //private int _pageBlockPerDatabaseChunk;
        //public int PageBlockPerDatabaseChunk => _pageBlockPerDatabaseChunk;

        //private class ViewInfo : IDisposable
        //{
        //    public int _fileChunkIndex;
        //    public int _pageBlockComboLength;
        //    public MemoryMappedViewAccessor _accessor;
        //    public int _lastWriteFrame;
        //    public void Dispose() => _accessor?.Dispose();
        //}

        //private readonly ConcurrentDictionary<uint, ViewInfo> _views;

        public PersistentDataAccess(IConfiguration<DatabaseConfiguration> dc, TimeManager timeManager)
        {
            try
            {
                _configuration = dc.Value;
                _timeManager = timeManager;
                
                //_fileChunkSizePower2 = DatabaseConfiguration.FirstSetBitPos((long)_configuration.DatabaseFileChunkSize);
                //_fileChunkIndexShift = _fileChunkSizePower2 - 13;
                //_pageBlockPerDatabaseChunk = (int)Math.Pow(2, _fileChunkIndexShift);
                //_fileChunkMask = (uint)_pageBlockPerDatabaseChunk - 1;
                //_views = new ConcurrentDictionary<uint, ViewInfo>();

                // Create the database root file if not existing
                //var rootFilePathName = BuildDatabasePathFileName(0);
                //var fi = new FileInfo(rootFilePathName);
                //var isCreationMode = fi.Exists == false;

                //// Open the root memory mapped file
                //_memoryMappedFiles = new List<MemoryMappedFile>(16);

                //if (isCreationMode)
                //{
                //    //CreateDatabaseFiles();
                //}
                //else
                //{
                //    //LoadDatabaseFiles();
                //}
            }
            catch
            {
                Dispose();
                throw;
            }
        }


        //public MemoryMappedViewAccessor GetPageBlockView(uint pbid, int comboLength, bool writeIntent)
        //{
        //    ViewInfo res = null;
        //    var vi = _views.GetOrAdd(pbid, id =>
        //    {
        //        var (ci, iic) = GetPageBlockLocation(pbid);
        //        res = new ViewInfo
        //        {
        //            _fileChunkIndex = ci,
        //            _pageBlockComboLength = comboLength,
        //            _accessor = _memoryMappedFiles[ci].CreateViewAccessor(PageSize * iic, PageSize * comboLength),
        //            _lastWriteFrame = writeIntent ? _timeManager.ExecutionFrame : 0
        //        };
        //        return res;
        //    });
            
        //    // Detect if another thread beat us at inserting the ViewInfo into the dictionary, dispose ours if it's the case
        //    if (res!=null && object.ReferenceEquals(vi, res) == false)
        //    {
        //        res.Dispose();
        //    }

        //    Debug.Assert(vi._pageBlockComboLength == comboLength, $"Error, GetPageBlockView request with comboLength of '{comboLength}' but found a different value '{vi._pageBlockComboLength}' in cache.");

        //    if (writeIntent)
        //    {
        //        vi._lastWriteFrame = _timeManager.ExecutionFrame;
        //    }
        //    return vi._accessor;
        //}

        //public void FlushAllViews()
        //{
        //    foreach (var kvp in _views)
        //    {
        //        kvp.Value._accessor.Flush();
        //    }
        //}

        //public void FlushAsyncAllViews()
        //{
        //    foreach (var kvp in _views)
        //    {
        //        kvp.Value._accessor.FlushAsync();
        //    }
        //}

        private void DisposeAllViews()
        {
            //foreach (var kvp in _views)
            //{
            //    kvp.Value._accessor.Dispose();
            //}
            //_views.Clear();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            DisposeAllViews();

            // Close the Memory Mapped Files
            //foreach (var mmf in _memoryMappedFiles)
            //{
            //    mmf.Dispose();    
            //}

            //if (_configuration.DeleteDatabaseOnDispose)
            //{
            //    DeleteDatabaseFiles();
            //}
            _isDisposed = true;
        }
    }

    public static class ProfilerHelper
    {
        public static TimeSpan Profile(Action methodToProfile)
        {
            var sw = new Stopwatch();
            sw.Start();

            methodToProfile();

            sw.Stop();
            return sw.Elapsed;
        }
    }
}
