using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

public partial class PagedMMF
{
    internal class PageInfo
    {
        private const int ClockSweepMaxValue = 5;
        
        public readonly int MemPageIndex;
        public int FilePageIndex;
        public int ClockSweepCounter => _clockSweepCounter;
        public int DirtyCounter;

        public AccessControlSmall StateSyncRoot;
        public PageState PageState;                 // Must always be changed under StateSyncRoot lock
        public int LockedByThreadId;                // Same
        public int ConcurrentSharedCounter;         // Same

        /// <summary>
        /// The epoch at which this page was last accessed via epoch-based protection.
        /// Pages with AccessEpoch >= MinActiveEpoch cannot be evicted.
        /// Value 0 means "not epoch-tagged" (legacy access only).
        /// </summary>
        public long AccessEpoch;

        private int _clockSweepCounter;
        private Lazy<Task<int>> _ioReadTask;

        public void SetIOReadTask(ValueTask<int> task) => _ioReadTask = new Lazy<Task<int>>(task.AsTask);

        public Task<int> IOReadTask => _ioReadTask?.Value;

        public void ResetIOCompletionTask() => _ioReadTask = null;

        public PageInfo(int memPageIndex)
        {
            MemPageIndex = memPageIndex;
            FilePageIndex = -1;
            _clockSweepCounter = 0;
            ConcurrentSharedCounter = 0;
            StateSyncRoot = new AccessControlSmall();
        }

        public void IncrementClockSweepCounter()
        {
            var curValue = _clockSweepCounter;
            if (curValue == ClockSweepMaxValue)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref _clockSweepCounter, curValue + 1, curValue) != curValue)
            {
                curValue = _clockSweepCounter;
                if (curValue == ClockSweepMaxValue)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void DecrementClockSweepCounter()
        {
            var curValue = _clockSweepCounter;
            if (curValue == 0)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref _clockSweepCounter, curValue - 1, curValue) != curValue)
            {
                curValue = _clockSweepCounter;
                if (curValue == 0)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void ResetClockSweepCounter() => _clockSweepCounter = 0;
    }
}