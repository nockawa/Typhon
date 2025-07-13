using JetBrains.Annotations;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

[PublicAPI]
public class AdaptiveWaiter
{
    private SpinWait _spinWait;

    public AdaptiveWaiter()
    {
        _spinWait = new SpinWait();
    }
    
    public async Task SpinAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // SpinWait automatically adapts its behavior:
        // - First ~10 iterations: Pure spinning (microsecond precision)
        // - After threshold: Yields to other threads
        // - Eventually: Uses Thread.Sleep(0) and Thread.Sleep(1)
        if (_spinWait.NextSpinWillYield)
        {
            // For longer waits, yield control back to async context
            await Task.Yield();
        }
        else
        {
            // Fast spinning for microsecond-level waits
            _spinWait.SpinOnce();
        }
    }
}