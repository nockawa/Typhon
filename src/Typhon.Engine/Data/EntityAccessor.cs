// EntityAccessor — lightweight base for Transaction and PointInTimeAccessor.
// Contains the minimum state needed for MVCC-correct entity reads and SV/Transient writes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Base class providing MVCC-correct entity access at a frozen TSN. Holds the minimum state
/// needed for entity reads and SingleVersion/Transient writes: engine reference, epoch scope,
/// component accessor cache, and ChangeSet.
/// <para>
/// <see cref="Transaction"/> extends this with spawn/destroy, commit/rollback, and TransactionChain
/// insertion. <see cref="PointInTimeAccessor"/> wraps per-thread instances of this class for
/// lock-free parallel entity access.
/// </para>
/// </summary>
[PublicAPI]
public partial class EntityAccessor : IDisposable
{
    private protected const int ComponentInfosMaxCapacity = 131;

    /// <summary>
    /// Number of entity operations between epoch refreshes. Each operation touches ~4-20 pages.
    /// At 128 ops × ~10 pages/op = ~1280 pages — refreshes before saturating a 1024-page cache.
    /// </summary>
    private protected const int EpochRefreshInterval = 128;

    private protected bool _isDisposed;
    private protected DatabaseEngine _dbe;
    internal DatabaseEngine DBE => _dbe;
    private protected EpochManager _epochManager;

#if DEBUG
    private protected int _debugOwningThreadId;
#endif

    private protected Dictionary<Type, ComponentInfo> _componentInfos;

    /// <summary>
    /// Cached EntityMap accessor for same-archetype repeated lookups.
    /// Reused across multiple calls targeting the same archetype. Disposed in ResetCore().
    /// </summary>
    private protected ushort _entityMapCacheArchId;
    private protected ChunkAccessor<PersistentStore> _entityMapCacheAccessor;
    private protected bool _hasEntityMapCache;

    private protected int _entityOperationCount;
    private protected ChangeSet _changeSet;

    public long TSN { get; private protected set; }

    public EntityAccessor()
    {
        _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
    }

    /// <summary>
    /// Initialize this accessor for lightweight point-in-time access.
    /// Creates a per-thread ChangeSet for dirty page tracking.
    /// Does NOT enter a persistent epoch scope — entity resolution uses per-call EpochGuard, and ChunkAccessors protect their own pages via ref counting.
    /// Does NOT insert into TransactionChain.
    /// </summary>
    internal void InitLightweight(DatabaseEngine dbe, long tsn)
    {
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        // Enter epoch scope on calling thread — required for ChunkAccessor creation.
        // Epoch exit is intentionally omitted from Dispose() because PointInTimeAccessor disposes per-thread accessors from a different (cleanup) thread.
        // The epoch scope is cleaned up when the EpochManager is disposed with the DatabaseEngine.
        // Runtime integration (#211) will add proper per-worker epoch cleanup hooks.
        _ = _epochManager.EnterScope();
        _isDisposed = false;
#if DEBUG
        _debugOwningThreadId = Environment.CurrentManagedThreadId;
#endif
        _entityOperationCount = 0;
        _changeSet = _dbe.MMF.CreateChangeSet();
        TSN = tsn;
    }

    [Conditional("DEBUG")]
    private protected void AssertThreadAffinity()
    {
#if DEBUG
        Debug.Assert(
            _debugOwningThreadId == Environment.CurrentManagedThreadId,
            "EntityAccessor thread affinity violation: current thread differs from the creating thread. " +
            "Each EntityAccessor instance must be used only from its creating thread.");
#endif
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component accessor cache
    // ═══════════════════════════════════════════════════════════════════════

    private protected ComponentInfo GetComponentInfo(Type componentType)
    {
        if (_componentInfos.TryGetValue(componentType, out var info))
        {
            return info;
        }

        var ct = _dbe.GetComponentTable(componentType) ?? _dbe.FindComponentTableBySchemaName(componentType);
        if (ct == null)
        {
            throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");
        }

        var isMultiple = ct.Definition.AllowMultiple;
        info = new ComponentInfo(isMultiple)
        {
            ComponentTypeId = ArchetypeRegistry.GetComponentTypeId(componentType),
            ComponentTable = ct,
            ComponentOverhead = ct.ComponentOverhead,
            SingleCache    = isMultiple ? null : new Dictionary<long, ComponentInfo.CompRevInfo>(),
            MultipleCache  = isMultiple ? new Dictionary<long, List<ComponentInfo.CompRevInfo>>() : null,
        };

        switch (ct.StorageMode)
        {
            case StorageMode.Transient:
                info.TransientCompContentAccessor = ct.TransientComponentSegment.CreateChunkAccessor();
                break;
            case StorageMode.SingleVersion:
                info.CompContentSegment  = ct.ComponentSegment;
                info.CompContentAccessor = ct.ComponentSegment.CreateChunkAccessor(_changeSet);
                break;
            default: // Versioned
                info.CompContentSegment   = ct.ComponentSegment;
                info.CompRevTableSegment  = ct.CompRevTableSegment;
                info.CompContentAccessor  = ct.ComponentSegment.CreateChunkAccessor(_changeSet);
                info.CompRevTableAccessor = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet);
                break;
        }

        _componentInfos.Add(componentType, info);
        return info;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Epoch management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flush all pending dirty state and advance the epoch within this accessor.
    /// Must only be called at a quiescent point — no B+Tree OLC write locks held,
    /// no ChunkAccessor mid-operation.
    /// </summary>
    private protected void FlushAndRefreshEpoch()
    {
        foreach (var ci in _componentInfos.Values)
        {
            if (ci.ComponentTable.StorageMode == StorageMode.Transient)
            {
                ci.TransientCompContentAccessor.CommitChanges();
            }
            else
            {
                ci.CompContentAccessor.CommitChanges();
                if (ci.ComponentTable.StorageMode == StorageMode.Versioned)
                {
                    ci.CompRevTableAccessor.CommitChanges();
                }
            }
        }

        _changeSet?.ReleaseExcessDirtyMarks();
        var newEpoch = _epochManager.RefreshScope();
        ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(newEpoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CheckEpochRefresh()
    {
        if (++_entityOperationCount >= EpochRefreshInterval)
        {
            FlushAndRefreshEpoch();
            _entityOperationCount = 0;
        }
    }

    /// <summary>
    /// Epoch refresh for bulk enumerators. Subclasses may override to add shortcuts (e.g. read-only skip).
    /// </summary>
    internal virtual void EnumerateRefreshEpoch() => FlushAndRefreshEpoch();

    // ═══════════════════════════════════════════════════════════════════════
    // Accessor lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reset this accessor for a new MVCC snapshot without reallocating.
    /// Flushes dirty ChunkAccessor state and caps DirtyCounter, then updates TSN.
    /// ComponentInfo cache and ChunkAccessors are preserved — page caches stay warm.
    /// Called by <see cref="PointInTimeAccessor"/> at the start of each tick to reuse
    /// per-thread accessors across ticks (zero allocation after warmup).
    /// </summary>
    internal void ResetForNewSnapshot(long newTsn)
    {
        // Flush pending dirty state from previous tick
        foreach (var ci in _componentInfos.Values)
        {
            if (ci.ComponentTable.StorageMode == StorageMode.Transient)
            {
                ci.TransientCompContentAccessor.CommitChanges();
            }
            else
            {
                ci.CompContentAccessor.CommitChanges();
                if (ci.ComponentTable.StorageMode == StorageMode.Versioned)
                {
                    ci.CompRevTableAccessor.CommitChanges();
                }
            }
        }

        _changeSet?.ReleaseExcessDirtyMarks();

        // Update snapshot — ComponentInfo cache stays warm (ChunkAccessor page caches preserved)
        TSN = newTsn;
        _entityOperationCount = 0;
    }

    /// <summary>Dispose all ChunkAccessors to flush dirty pages.</summary>
    private protected void FlushAccessors()
    {
        foreach (var info in _componentInfos.Values)
        {
            info.DisposeAccessors();
        }
    }

    /// <summary>Reset base fields for reuse. Subclasses call this AFTER their own cleanup.</summary>
    private protected virtual void ResetCore()
    {
        _dbe = null;
        _epochManager = null;
#if DEBUG
        _debugOwningThreadId = 0;
#endif
        if (_hasEntityMapCache)
        {
            _entityMapCacheAccessor.Dispose();
            _hasEntityMapCache = false;
        }
        if (_componentInfos.Capacity <= ComponentInfosMaxCapacity)
        {
            _componentInfos.Clear();
        }
        else
        {
            _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
        }

        TSN = 0;
        _changeSet = null;
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
                _hasEntityMapCache = false;
            }
            return;
        }

        // No thread affinity assert — PointInTimeAccessor disposes per-thread
        // accessors from the cleanup thread (different from the creating thread).
        // Transaction.Dispose overrides and adds its own affinity check + epoch exit.
        FlushAccessors();
        _changeSet?.ReleaseExcessDirtyMarks();
        _isDisposed = true;
        // No epoch exit here — InitLightweight does not enter a persistent epoch scope.
        // Transaction.Dispose overrides and exits its own epoch scope.
        ResetCore();
    }
}
