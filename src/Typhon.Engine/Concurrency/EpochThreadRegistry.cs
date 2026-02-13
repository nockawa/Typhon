using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Fixed-size registry of per-thread epoch pins. Each thread claims a slot on first use
/// and releases it on thread death (via <see cref="EpochSlotHandle"/> CriticalFinalizerObject).
/// </summary>
/// <remarks>
/// <para>Memory layout: three parallel arrays (SOA) for cache-friendly MinActiveEpoch scan.</para>
/// <para><c>_pinnedEpochs[256]</c> is the hot array — scanned every time MinActiveEpoch is computed.
/// <c>_slotStates[256]</c> and <c>_depths[256]</c> are warm (accessed on enter/exit).
/// <c>_ownerThreads[256]</c> is cold (accessed only on registration and liveness checks).</para>
/// <para><b>Thread-static binding:</b> A thread can only be registered in one registry at a time.
/// If a thread encounters a different registry instance (e.g., in tests that create multiple
/// EpochManagers), it re-registers in the new one. Per ADR-004, production has a single
/// DatabaseEngine per process, so this is only relevant for testing.</para>
/// </remarks>
internal sealed class EpochThreadRegistry : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════════

    public const int MaxSlots = 256;

    // int (not byte) because Interlocked.CompareExchange has no byte overload
    internal const int SlotFree = 0;
    internal const int SlotActive = 1;

    // ═══════════════════════════════════════════════════════════════════════
    // SOA Storage
    // ═══════════════════════════════════════════════════════════════════════

    // Hot: scanned on every MinActiveEpoch computation
    private readonly long[] _pinnedEpochs = new long[MaxSlots];     // 0 = not pinned

    // Warm: read/written on every EnterScope/ExitScope
    private readonly int[] _slotStates = new int[MaxSlots];         // SlotFree / SlotActive
    private readonly int[] _depths = new int[MaxSlots];             // Nesting depth per slot

    // Cold: registration and liveness checks
    private readonly Thread[] _ownerThreads = new Thread[MaxSlots];

    // Slot allocation tracking
    private int _highWaterMark;  // Next slot to try for allocation (grows monotonically)
    private int _activeSlotCount;

    // Per-thread slot index (O(1) lookup after first registration).
    // _threadRegistry tracks which registry instance the thread's slot belongs to,
    // allowing re-registration when a thread encounters a different instance (e.g., in tests).
    [ThreadStatic]
    private static int _threadSlotIndex;

    [ThreadStatic]
    private static EpochThreadRegistry _threadRegistry;

    // Roots the handle to prevent premature GC — must live as long as the thread.
    [ThreadStatic]
    private static EpochSlotHandle _threadSlotHandle;

    /// <summary>Number of slots currently owned by active threads.</summary>
    public int ActiveSlotCount => _activeSlotCount;

    /// <summary>
    /// Returns true if the current thread is inside an epoch scope (depth &gt; 0).
    /// </summary>
    public bool IsCurrentThreadInScope => _threadRegistry == this && _depths[_threadSlotIndex] > 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Slot Management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pin the current thread to the given epoch. Claims a slot on first call.
    /// </summary>
    /// <returns>Nesting depth before this call (0 = outermost).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PinCurrentThread(long epoch)
    {
        if (_threadRegistry != this)
        {
            ClaimSlot();
        }

        var slot = _threadSlotIndex;
        var depth = _depths[slot];
        _depths[slot] = depth + 1;

        if (depth == 0)
        {
            // Outermost scope: pin to current epoch
            _pinnedEpochs[slot] = epoch;
        }

        return depth;
    }

    /// <summary>
    /// Unpin the current thread if this is the outermost scope exit.
    /// </summary>
    /// <param name="expectedDepth">Depth returned by the matching <see cref="PinCurrentThread"/>.</param>
    /// <returns>True if this was the outermost scope exit (thread is now unpinned).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool UnpinCurrentThread(int expectedDepth)
    {
        var slot = _threadSlotIndex;
        var currentDepth = _depths[slot];

        // Depth validation: catch copy-safety violations
        if (currentDepth != expectedDepth + 1)
        {
            ThrowHelper.ThrowInvalidOp(
                $"EpochGuard depth mismatch: expected {expectedDepth + 1}, got {currentDepth}. " +
                "Probable cause: EpochGuard was copied or disposed out of order.");
        }

        _depths[slot] = expectedDepth;

        if (expectedDepth == 0)
        {
            // Outermost scope: unpin
            _pinnedEpochs[slot] = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Unpin the current thread without enforcing LIFO ordering. Decrements the nesting depth
    /// and unpins when it reaches zero. Used by <see cref="Transaction"/> which can be disposed
    /// in any order (not just LIFO).
    /// </summary>
    /// <returns>True if this was the outermost scope exit (thread is now unpinned).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool UnpinCurrentThreadUnordered()
    {
        var slot = _threadSlotIndex;
        var currentDepth = _depths[slot];

        if (currentDepth <= 0)
        {
            ThrowHelper.ThrowInvalidOp("Epoch scope underflow: attempted to exit scope when not in any scope.");
        }

        _depths[slot] = currentDepth - 1;

        if (currentDepth == 1)
        {
            // Was the outermost scope: unpin
            _pinnedEpochs[slot] = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Claim a free slot for the current thread. Called once per thread lifetime
    /// (or when a thread encounters a new registry instance).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]  // Keep hot path (PinCurrentThread) small
    private void ClaimSlot()
    {
        var thread = Thread.CurrentThread;

        // Scan from high water mark first (fast path: no contention on new slots)
        for (int attempts = 0; attempts < MaxSlots; attempts++)
        {
            var candidate = Interlocked.Increment(ref _highWaterMark) - 1;
            if (candidate >= MaxSlots)
            {
                // Wrap around — need to reclaim dead thread slots
                break;
            }

            if (TryClaimSlot(candidate, thread))
            {
                return;
            }
        }

        // Slow path: scan all slots looking for dead-thread slots to reclaim
        for (int i = 0; i < MaxSlots; i++)
        {
            if (_slotStates[i] == SlotActive)
            {
                var owner = _ownerThreads[i];
                if (owner != null && !owner.IsAlive)
                {
                    // Thread died without cleanup — reclaim the slot
                    if (TryReclaimDeadSlot(i, thread))
                    {
                        return;
                    }
                }
            }
            else if (_slotStates[i] == SlotFree)
            {
                if (TryClaimSlot(i, thread))
                {
                    return;
                }
            }
        }

        ThrowHelper.ThrowEpochRegistryExhausted();
    }

    private bool TryClaimSlot(int index, Thread thread)
    {
        if (Interlocked.CompareExchange(ref _slotStates[index], SlotActive, SlotFree) == SlotFree)
        {
            _ownerThreads[index] = thread;
            _pinnedEpochs[index] = 0;
            _depths[index] = 0;
            _threadSlotIndex = index;
            _threadRegistry = this;
            Interlocked.Increment(ref _activeSlotCount);

            // Register finalizer for thread death cleanup.
            // Store in [ThreadStatic] field to root the handle for the thread's lifetime.
            _threadSlotHandle = new EpochSlotHandle(this, index);
            return true;
        }

        return false;
    }

    private bool TryReclaimDeadSlot(int index, Thread newOwner)
    {
        // The slot is marked active but the owning thread is dead.
        // Use CAS on the owner thread reference to claim it atomically.
        var oldOwner = _ownerThreads[index];
        if (oldOwner != null && !oldOwner.IsAlive &&
            Interlocked.CompareExchange(ref _ownerThreads[index], newOwner, oldOwner) == oldOwner)
        {
            _pinnedEpochs[index] = 0;
            _depths[index] = 0;
            _threadSlotIndex = index;
            _threadRegistry = this;
            // activeSlotCount doesn't change — we're reusing an active slot

            // Root the handle for this thread
            _threadSlotHandle = new EpochSlotHandle(this, index);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Release a slot. Called by <see cref="EpochSlotHandle"/> finalizer on thread death.
    /// </summary>
    internal void FreeSlot(int index)
    {
        _pinnedEpochs[index] = 0;
        _depths[index] = 0;
        _ownerThreads[index] = null;

        // CAS to make FreeSlot idempotent: ComputeMinActiveEpoch and EpochSlotHandle finalizer
        // can both call FreeSlot for the same dead-thread slot concurrently.
        if (Interlocked.CompareExchange(ref _slotStates[index], SlotFree, SlotActive) == SlotActive)
        {
            Interlocked.Decrement(ref _activeSlotCount);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MinActiveEpoch
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the minimum epoch pinned by any active thread.
    /// Returns <paramref name="currentGlobalEpoch"/> if no threads are pinned.
    /// </summary>
    /// <remarks>
    /// Scans all 256 slots. The _pinnedEpochs array is 2KB (256 × 8 bytes = 32 cache lines).
    /// At ~4 cycles per cache line, a full scan takes ~128 cycles (~40ns at 3.5GHz).
    /// This is called during eviction, which is already a slow path.
    /// </remarks>
    public long ComputeMinActiveEpoch(long currentGlobalEpoch)
    {
        var min = currentGlobalEpoch;

        // Scan with liveness check: skip slots whose owning thread has died
        for (int i = 0; i < MaxSlots; i++)
        {
            var pinned = _pinnedEpochs[i];
            if (pinned == 0)
            {
                continue;
            }

            // Liveness check: if the thread died, clear the slot
            if (_slotStates[i] == SlotActive)
            {
                var thread = _ownerThreads[i];
                if (thread != null && !thread.IsAlive)
                {
                    FreeSlot(i);
                    continue;
                }
            }

            if (pinned < min)
            {
                min = pinned;
            }
        }

        return min;
    }

    public void Dispose()
    {
        // Clear all slots — finalizers may still run but will see SlotFree
        for (int i = 0; i < MaxSlots; i++)
        {
            _slotStates[i] = SlotFree;
            _pinnedEpochs[i] = 0;
            _ownerThreads[i] = null;
        }
    }
}

/// <summary>
/// CriticalFinalizerObject attached to each thread that claims an epoch slot.
/// When the thread dies (and GC collects the ThreadStatic reference), the finalizer
/// releases the slot back to the registry.
/// </summary>
internal sealed class EpochSlotHandle : System.Runtime.ConstrainedExecution.CriticalFinalizerObject
{
    private readonly EpochThreadRegistry _registry;
    private readonly int _slotIndex;

    internal EpochSlotHandle(EpochThreadRegistry registry, int slotIndex)
    {
        _registry = registry;
        _slotIndex = slotIndex;
    }

    ~EpochSlotHandle()
    {
        _registry.FreeSlot(_slotIndex);
    }
}
