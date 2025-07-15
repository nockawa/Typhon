using JetBrains.Annotations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

[PublicAPI]
public class AdaptiveWaiter
{
    static readonly TimeSpan WaitDelay = TimeSpan.FromMicroseconds(100);
    private int _iterationCount;
    private int _curCount;

    public AdaptiveWaiter()
    {
        _iterationCount = 1 << 16;
        _curCount = 0;
    }
    
    public async Task SpinAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Environment.ProcessorCount == 1)
        {
            await Task.Delay(WaitDelay, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_iterationCount == ++_curCount)
        {
            _iterationCount >>= 1;
            _iterationCount = Math.Max(_iterationCount, 10);
            _curCount = 0;
            await Task.Delay(WaitDelay, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Thread.SpinWait(100);
        }
    }
}