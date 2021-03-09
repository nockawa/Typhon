// unset

using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    /// <summary>
    /// Synchronization type that allow multiple concurrent readers and one exclusive writer.
    /// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReaderWriterSpinLock
    {
        public void Reset()
        {
            _lockedByThreadId = 0;
            _concurrentUsedCounter = 0;
        }

        private volatile int _lockedByThreadId;
        private volatile int _concurrentUsedCounter;

        public int LockedByThreadId => _lockedByThreadId;
        public int ConcurrentUsedCounter => _concurrentUsedCounter;

        public void EnterRead()
        {
            // Currently exclusively locked, wait it's over
            if (_lockedByThreadId != 0)
            {
                var sw = new SpinWait();
                while (_lockedByThreadId != 0)
                {
                    sw.SpinOnce();
                }
            }

            // Increment concurrent usage
            Interlocked.Increment(ref _concurrentUsedCounter);

            // Double check on exclusive, in a loop because we need to restore the concurrent counter to prevent deadlock
            // So we loop until we meet the criteria
            while (_lockedByThreadId != 0)
            {
                Interlocked.Decrement(ref _concurrentUsedCounter);
                var sw = new SpinWait();
                while (_lockedByThreadId != 0)
                {
                    sw.SpinOnce();
                }
                Interlocked.Increment(ref _concurrentUsedCounter);
            }
        }

        public void ExitRead() => Interlocked.Decrement(ref _concurrentUsedCounter);

        public void EnterWrite()
        {
            var ct = Thread.CurrentThread.ManagedThreadId;

            // Fast path: exclusive lock works immediately
            if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) == 0)
            {
                // No concurrent use: we're good to go
                if (_concurrentUsedCounter == 0)
                {
                    return;
                }

                // Otherwise wait the concurrent use is over
                var sw = new SpinWait();
                while (_concurrentUsedCounter != 0)
                {
                    sw.SpinOnce();
                }

                return;
            }

            // Slow path: wait the current concurrent use if over
            {
                var sw = new SpinWait();
                while (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
                {
                    sw.SpinOnce();
                }

                // Exit if there's no concurrent access neither
                if (_concurrentUsedCounter == 0)
                {
                    return;
                }

                // Otherwise wait the concurrent access to be over
                while (_concurrentUsedCounter != 0)
                {
                    sw.SpinOnce();
                }
            }
        }

        public bool TryPromoteToWrite()
        {
            var ct = Thread.CurrentThread.ManagedThreadId;

            // We can enter only if we are the only user (counter == 1)
            if (_concurrentUsedCounter != 1)
            {
                return false;
            }

            // Try to exclusively lock
            if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
            {
                return false;
            }

            // Double check now we're locked that we're still the only concurrent user
            if (_concurrentUsedCounter != 1)
            {
                // Another concurrent user came at the same time, remove exclusive access and quit with failure
                _lockedByThreadId = 0;
                return false;
            }

            return true;
        }

        public void DemoteWriteAccess() => _lockedByThreadId = 0;

        public void ExitWrite() => _lockedByThreadId = 0;
    }
}