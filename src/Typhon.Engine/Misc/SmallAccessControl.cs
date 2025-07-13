using JetBrains.Annotations;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public struct SmallAccessControl
{
    private int _data;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Enter()
    {
        SpinWait sw = new();
        while (Interlocked.CompareExchange(ref _data, 1, 0) != 0)
        {
            sw.SpinOnce();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Exit() => Interlocked.Exchange(ref _data, 0);
}