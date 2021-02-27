// unset

namespace Typhon.Engine
{
    public class TimeManager
    {
        public static TimeManager Singleton { get; internal set; }

        public int ExecutionFrame { get; private set; }

        public TimeManager()
        {
            Singleton = this;
            ExecutionFrame = 1;
        }

        internal void BumpFrame() => ++ExecutionFrame;
    }
}