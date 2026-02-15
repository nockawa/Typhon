using JetBrains.Annotations;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation adaptive spin-wait with optional deadline/cancellation support. Wraps .NET's <see cref="SpinWait"/> with <see cref="WaitContext"/> integration.
/// </summary>
/// <remarks>
/// <para>Progression strategy (delegated to <see cref="SpinWait.SpinOnce()"/>):</para>
/// <list type="bullet">
///   <item>First ~10 iterations: <c>Thread.SpinWait(N)</c> with increasing N</item>
///   <item>Then: <c>Thread.Yield()</c> (give up timeslice)</item>
///   <item>Then: <c>Thread.Sleep(0)</c> (yield to any ready thread)</item>
///   <item>Then: <c>Thread.Sleep(1)</c> (~1ms pause, real CPU relief)</item>
/// </list>
/// <para>This struct must not be copied after first use — the spin counter tracks
/// progression state. Pass by <c>ref</c> if shared across methods.</para>
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct AdaptiveWaiter
{
    private SpinWait _spinner;

    /// <summary>
    /// Perform one adaptive wait step, checking deadline/cancellation first. Returns <c>false</c> if the deadline expired or cancellation was requested
    /// (the caller should stop waiting). Returns <c>true</c> to continue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Wait(ref WaitContext ctx)
    {
        if (ctx.ShouldStop)
        {
            return false;
        }

        _spinner.SpinOnce();
        return true;
    }

    /// <summary>
    /// Perform one adaptive wait step without deadline/cancellation checking. Use when the caller checks <see cref="WaitContext.ShouldStop"/> in the outer loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Wait() => _spinner.SpinOnce();

    /// <summary>Number of times <see cref="Wait()"/> has been called.</summary>
    public int Count => _spinner.Count;

    /// <summary>
    /// <c>true</c> if the next <see cref="Wait()"/> will yield the processor instead of busy-spinning.
    /// </summary>
    public bool NextWaitWillYield => _spinner.NextSpinWillYield;

    /// <summary>
    /// Reset the spin counter. Use after a condition partially changed, and you want to re-enter the fast spin phase.
    /// </summary>
    public void Reset() => _spinner.Reset();
}
