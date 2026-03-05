// unset

using System.Threading;

namespace Typhon.Engine;

public abstract partial class BTree<TKey>
{
    /// <summary>Result of an OLC remove attempt.</summary>
    private enum OlcRemoveResult
    {
        /// <summary>Remove completed successfully.</summary>
        Completed,
        /// <summary>OLC validation failed — caller should retry.</summary>
        Restart,
        /// <summary>Key not found in the tree (confirmed by OLC validation).</summary>
        NotFound,
        /// <summary>Remove requires merge/borrow or structural change — needs pessimistic path.</summary>
        NeedsPessimistic,
    }
    /// <summary>
    /// OLC-dispatching remove: tries optimistic fast paths first, then falls back to pessimistic.
    /// Begin/end remove fast paths and general mid-leaf remove operate without exclusive lock when the leaf has enough items (no merge/borrow needed).
    /// All other cases use the pessimistic path.
    /// </summary>
    private void RemoveCore(ref RemoveArguments args)
    {
        if (IsEmpty())
        {
            return;
        }

        // OLC retry loop — handles begin/end fast paths + general non-merge removes
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var result = TryRemoveOlc(ref args);
            if (result == OlcRemoveResult.Completed || result == OlcRemoveResult.NotFound)
            {
                return;
            }
            if (result == OlcRemoveResult.NeedsPessimistic)
            {
                break;
            }
            // Restart: continue loop
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback
        Interlocked.Increment(ref _pessimisticFallbacks);
        RemoveCorePessimistic(ref args);
    }

    /// <summary>
    /// OLC remove attempt: tries begin/end fast paths (first/last key of first/last leaf) and general mid-leaf remove via optimistic descent.
    /// Only modifies a single leaf node (WriteLocked). Returns NeedsPessimistic when the leaf is too small (merge/borrow would be needed).
    /// </summary>
    private OlcRemoveResult TryRemoveOlc(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;

        // --- Begin-remove fast path: remove first key of leftmost leaf ---
        {
            var ll = _linkList;
            if (!ll.IsValid)
            {
                return OlcRemoveResult.Restart;
            }
            var llLatch = ll.GetLatch(ref accessor);
            int llVersion = llLatch.ReadVersion();
            if (llVersion == 0)
            {
                return OlcRemoveResult.Restart;
            }

            var firstKey = ll.GetFirst(ref accessor).Key;
            if (!llLatch.ValidateVersion(llVersion))
            {
                return OlcRemoveResult.Restart;
            }

            int order = args.Compare(args.Key, firstKey);
            if (order < 0)
            {
                return OlcRemoveResult.NotFound; // key < first key → definitely not in tree
            }

            if (order == 0)
            {
                int count = ll.GetCount(ref accessor);
                bool isRoot = _rootChunkId == ll.ChunkId;
                int capacity = ll.GetCapacity();
                if (!llLatch.ValidateVersion(llVersion))
                {
                    return OlcRemoveResult.Restart;
                }

                // Safe if: root leaf with count > 1, or non-root leaf above half-full
                if ((isRoot && count > 1) || (!isRoot && count > capacity / 2))
                {
                    ll.PreDirtyForWrite(ref accessor);
                    if (!llLatch.TryWriteLock())
                    {
                        return OlcRemoveResult.Restart;
                    }
                    if (!llLatch.ValidateVersionLocked(llVersion))
                    {
                        llLatch.AbortWriteLock();
                        return OlcRemoveResult.Restart;
                    }
                    // Re-verify first key under lock (concurrent OLC writer might have removed it)
                    if (args.Compare(args.Key, ll.GetFirst(ref accessor).Key) != 0)
                    {
                        llLatch.WriteUnlock();
                        return OlcRemoveResult.Restart;
                    }

                    args.SetRemovedValue(ll.PopFirstInternal(ref accessor).Value);
                    llLatch.WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    return OlcRemoveResult.Completed;
                }

                return OlcRemoveResult.NeedsPessimistic; // merge/borrow possible or tree might become empty
            }
        }

        // --- End-remove fast path: remove last key of rightmost leaf ---
        {
            var rll = _reverseLinkList;
            if (!rll.IsValid)
            {
                return OlcRemoveResult.Restart;
            }
            var rllLatch = rll.GetLatch(ref accessor);
            int rllVersion = rllLatch.ReadVersion();
            if (rllVersion == 0)
            {
                return OlcRemoveResult.Restart;
            }

            int rllCount = rll.GetCount(ref accessor);
            if (rllCount == 0)
            {
                return OlcRemoveResult.Restart; // transient empty state during concurrent tree emptying
            }
            var lastKey = rll.GetItem(rllCount - 1, ref accessor).Key;
            if (!rllLatch.ValidateVersion(rllVersion))
            {
                return OlcRemoveResult.Restart;
            }

            int order = args.Compare(args.Key, lastKey);
            if (order > 0)
            {
                return OlcRemoveResult.NotFound; // key > last key → definitely not in tree
            }

            if (order == 0)
            {
                bool isRoot = _rootChunkId == rll.ChunkId;
                int capacity = rll.GetCapacity();
                if (!rllLatch.ValidateVersion(rllVersion))
                {
                    return OlcRemoveResult.Restart;
                }

                if ((isRoot && rllCount > 1) || (!isRoot && rllCount > capacity / 2))
                {
                    rll.PreDirtyForWrite(ref accessor);
                    if (!rllLatch.TryWriteLock())
                    {
                        return OlcRemoveResult.Restart;
                    }
                    if (!rllLatch.ValidateVersionLocked(rllVersion))
                    {
                        rllLatch.AbortWriteLock();
                        return OlcRemoveResult.Restart;
                    }
                    // Re-verify last key under lock
                    if (args.Compare(args.Key, rll.GetLast(ref accessor).Key) != 0)
                    {
                        rllLatch.WriteUnlock();
                        return OlcRemoveResult.Restart;
                    }

                    args.SetRemovedValue(rll.PopLastInternal(ref accessor).Value);
                    rllLatch.WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    return OlcRemoveResult.Completed;
                }

                return OlcRemoveResult.NeedsPessimistic;
            }
        }

        // --- General path: optimistic descent to leaf, remove if safe (no merge/borrow) ---
        var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(args.Key, ref accessor);
        if (leafChunkId == 0)
        {
            return OlcRemoveResult.Restart;
        }

        if (keyIndex < 0)
        {
            // Key not found — validate version to confirm
            var nfLeaf = new NodeWrapper(_storage, leafChunkId);
            if (!nfLeaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return OlcRemoveResult.Restart;
            }
            return OlcRemoveResult.NotFound;
        }

        // Key found — check if safe to remove under OLC (no merge/borrow needed)
        {
            var leaf = new NodeWrapper(_storage, leafChunkId);
            var leafLatch = leaf.GetLatch(ref accessor);
            int count = leaf.GetCount(ref accessor);
            bool isRoot = _rootChunkId == leafChunkId;
            int capacity = leaf.GetCapacity();
            if (!leafLatch.ValidateVersion(leafVersion))
            {
                return OlcRemoveResult.Restart;
            }

            if ((isRoot && count > 1) || (!isRoot && count > capacity / 2))
            {
                leaf.PreDirtyForWrite(ref accessor);
                if (!leafLatch.TryWriteLock())
                {
                    return OlcRemoveResult.Restart;
                }
                if (!leafLatch.ValidateVersionLocked(leafVersion))
                {
                    leafLatch.AbortWriteLock();
                    return OlcRemoveResult.Restart;
                }

                // Re-find key under lock (index might have shifted due to concurrent modification)
                var reIndex = leaf.Find(args.Key, args.Comparer, ref accessor);
                if (reIndex < 0)
                {
                    leafLatch.WriteUnlock();
                    return OlcRemoveResult.NotFound; // concurrent writer already removed it
                }

                args.SetRemovedValue(leaf.RemoveAtInternal(reIndex, ref accessor).Value);
                leafLatch.WriteUnlock();
                _hasCachedLastKey = false;
                DecCount();
                return OlcRemoveResult.Completed;
            }

            return OlcRemoveResult.NeedsPessimistic;
        }
    }

    /// <summary>
    /// Pessimistic remove fallback: uses WriteLock/WriteUnlock on individual nodes so concurrent OLC readers detect changes. No global lock — concurrency is
    /// handled by per-node OLC latches and latch-coupled SMO in RemoveIterative.
    /// </summary>
    private void RemoveCorePessimistic(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;
        try
        {
            if (IsEmpty())
            {
                return;
            }

            // Begin-remove fast path (WriteLock protects against concurrent OLC writers)
            {
                var ll = _linkList;
                ll.PreDirtyForWrite(ref accessor);
                SpinWriteLock(ll.GetLatch(ref accessor));
                int order = args.Compare(args.Key, ll.GetFirst(ref accessor).Key);
                if (order < 0)
                {
                    ll.GetLatch(ref accessor).AbortWriteLock(); // key not in tree — didn't modify node
                    return;
                }

                if (order == 0 && (Root == ll || ll.GetCount(ref accessor) > ll.GetCapacity() / 2))
                {
                    args.SetRemovedValue(ll.PopFirstInternal(ref accessor).Value);
                    ll.GetLatch(ref accessor).WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    if (IsEmpty())
                    {
                        Root = _linkList = _reverseLinkList = default;
                        Height--;
                    }
                    return;
                }
                ll.GetLatch(ref accessor).AbortWriteLock(); // condition failed — didn't modify node
            }

            // End-remove fast path
            {
                var rll = _reverseLinkList;
                rll.PreDirtyForWrite(ref accessor);
                SpinWriteLock(rll.GetLatch(ref accessor));

                // Safety: if rll was split concurrently, it's no longer the rightmost leaf.
                // Fall through to general path which handles stale pointers correctly.
                if (rll.GetNext(ref accessor).IsValid)
                {
                    rll.GetLatch(ref accessor).AbortWriteLock();
                }
                else
                {
                    int order = args.Compare(args.Key, rll.GetLast(ref accessor).Key);
                    if (order > 0)
                    {
                        rll.GetLatch(ref accessor).AbortWriteLock(); // key not in tree — didn't modify node
                        return;
                    }

                    if (order == 0 && (Root == rll || rll.GetCount(ref accessor) > rll.GetCapacity() / 2))
                    {
                        args.SetRemovedValue(rll.PopLastInternal(ref accessor).Value);
                        rll.GetLatch(ref accessor).WriteUnlock();
                        _hasCachedLastKey = false;
                        DecCount();
                        return;
                    }
                    rll.GetLatch(ref accessor).AbortWriteLock(); // condition failed — didn't modify node
                }
            }

            // General remove path with latch-coupled SMO — retry on lock contention
            _hasCachedLastKey = false;
            bool merge;
            SpinWait spin = default;
            while (true)
            {
                merge = RemoveIterative(ref args, ref accessor, out bool removeCompleted);
                if (removeCompleted)
                {
                    break;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                spin.SpinOnce();
            }

            if (args.Removed)
            {
                DecCount();
            }

            if (merge && Root.GetLength(ref accessor) == 0)
            {
                Root = Root.GetChild(-1, ref accessor); // left most child becomes root. (returns null for leafs)
                if (Root.IsValid == false)
                {
                    _linkList = default;
                    _reverseLinkList = default;
                }
                Height--;
            }

            if (_reverseLinkList.IsValid && _reverseLinkList.GetPrevious(ref accessor).IsValid && _reverseLinkList.GetPrevious(ref accessor).GetNext(ref accessor).IsValid == false)
            {
                _reverseLinkList = _reverseLinkList.GetPrevious(ref accessor);
            }
        }
        finally
        {
            // Reclaim deferred nodes whose epoch is safe (all readers have exited).
            DeferredReclaim();
        }
    }
    /// <summary>
    /// Iterative remove with latch-coupled SMO: descends optimistically recording PathVersions, then locks bottom-up only as needed for structural modifications.
    /// Fast path (leaf stays half-full or root leaf): locks only the leaf node.
    /// Slow path (leaf underflows): locks leaf + neighbors + path nodes with version validation.
    /// Sets <paramref name="completed"/> to false when lock acquisition fails and caller must retry.
    /// </summary>
    private bool RemoveIterative(ref RemoveArguments args, ref ChunkAccessor accessor, out bool completed)
    {
        completed = false;
        MutationContext ctx = default;
        var node = Root;
        var relatives = new NodeRelatives();

        // Phase 1: Descend from root to leaf, recording path + PathVersions for validation.
        // OLC protocol: read version BEFORE data, validate AFTER — ensures (index, version) are consistent.
        while (!node.GetIsLeaf(ref accessor))
        {
            var latch = node.GetLatch(ref accessor);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return false; // node locked or obsolete — restart
            }

            var index = node.Find(args.Key, args.Comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            var child = node.GetChild(index, ref accessor);
            int parentCount = node.GetCount(ref accessor);

            // Validate: node wasn't modified during our unlocked read
            if (!latch.ValidateVersion(version))
            {
                return false; // node modified between version read and data read — restart
            }

            NodeRelatives.Create(child, index, node, parentCount, ref relatives, out var childRelatives, ref accessor);

            ctx.PathNodes[ctx.Depth] = node;
            ctx.PathChildIndices[ctx.Depth] = index;
            ctx.PathVersions[ctx.Depth] = version;

            // Store after Create so lazy-resolved siblings are cached in the stored copy
            ctx.PathRelatives[ctx.Depth] = relatives;

            node = child;
            relatives = childRelatives;
            ctx.Depth++;
        }

        // Phase 1.5A: Lock leaf with version validation.
        // Between Phase 1 descent and lock acquisition, a concurrent writer may have split/modified
        // this leaf. Snapshot the version before locking, then validate after.
        node.PreDirtyForWrite(ref accessor);
        var leafLatch = node.GetLatch(ref accessor);
        int leafVersion = leafLatch.ReadVersion();
        if (leafVersion == 0)
        {
            // Leaf is locked or obsolete. SpinWriteLock to wait, then restart.
            SpinWriteLock(leafLatch);
            leafLatch.AbortWriteLock();
            return false;
        }
        SpinWriteLock(leafLatch);
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock();
            return false; // restart — leaf was modified between descent and lock
        }

        // Check if key exists in this leaf
        var keyIndex = node.Find(args.Key, args.Comparer, ref accessor);
        if (keyIndex < 0)
        {
            node.GetLatch(ref accessor).AbortWriteLock(); // key not found — didn't modify leaf
            completed = true;
            return false; // key not found — no merge
        }

        // Fast path: leaf won't underflow after remove (count > capacity/2) or root leaf (depth == 0).
        // RemoveLeaf only modifies the leaf in this case (no borrow/merge needed).
        int count = node.GetCount(ref accessor);
        if (count > node.GetCapacity() / 2 || ctx.Depth == 0)
        {
            bool fastMerged = node.RemoveLeaf(ref args, ref relatives, ref accessor);
            node.GetLatch(ref accessor).WriteUnlock();
            completed = true;
            return fastMerged;
        }

        // Slow path: leaf may underflow → need neighbors + path for borrow/merge.
        // Lock leaf neighbors for potential borrow or merge.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        var leafPrev = node.GetPrevious(ref accessor);
        var leafNext = node.GetNext(ref accessor);
        if (leafPrev.IsValid)
        {
            leafPrev.PreDirtyForWrite(ref accessor);
        }
        if (leafPrev.IsValid && !leafPrev.GetLatch(ref accessor).TryWriteLock())
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return false; // restart
        }
        if (leafNext.IsValid)
        {
            leafNext.PreDirtyForWrite(ref accessor);
        }
        if (leafNext.IsValid && !leafNext.GetLatch(ref accessor).TryWriteLock())
        {
            if (leafPrev.IsValid)
            {
                leafPrev.GetLatch(ref accessor).AbortWriteLock();
            }
            node.GetLatch(ref accessor).AbortWriteLock();
            return false; // restart
        }

        // Lock path nodes bottom-up with version validation.
        // Required for ancestor key updates during borrow and merge propagation.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        for (int i = ctx.Depth - 1; i >= 0; i--)
        {
            ctx.PathNodes[i].PreDirtyForWrite(ref accessor);
            var pathLatch = ctx.PathNodes[i].GetLatch(ref accessor);
            if (!pathLatch.TryWriteLock())
            {
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref accessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return false; // restart
            }
            if (!pathLatch.ValidateVersionLocked(ctx.PathVersions[i]))
            {
                pathLatch.AbortWriteLock();
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref accessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return false; // restart
            }
        }

        // All needed nodes locked — Phase 2: Remove at leaf (may borrow/merge)
        var merged = node.RemoveLeaf(ref args, ref relatives, ref accessor);

        // Phase 2.5: Mark obsolete merged leaf + unlock leaf neighbors + leaf (version bumped by WriteUnlock)
        var retireEpoch = _segment.Manager.EpochManager.GlobalEpoch;
        if (merged)
        {
            Interlocked.Increment(ref _mergeCount);
            if (relatives.HasTrueLeftSibling)
            {
                // Current node was merged into its left sibling — mark current obsolete
                node.GetLatch(ref accessor).MarkObsolete();
                DeferredAdd(node.ChunkId, retireEpoch);
            }
            else if (relatives.HasTrueRightSibling && leafNext.IsValid)
            {
                // Right sibling was merged into current — mark right sibling obsolete
                leafNext.GetLatch(ref accessor).MarkObsolete();
                DeferredAdd(leafNext.ChunkId, retireEpoch);
            }
        }
        if (leafNext.IsValid)
        {
            leafNext.GetLatch(ref accessor).WriteUnlock();
        }
        if (leafPrev.IsValid)
        {
            leafPrev.GetLatch(ref accessor).WriteUnlock();
        }
        node.GetLatch(ref accessor).WriteUnlock();

        // Phase 3: Propagate merges upward through internal nodes
        while (ctx.Depth > 0 && merged)
        {
            ctx.Depth--;
            node = ctx.PathNodes[ctx.Depth];
            relatives = ctx.PathRelatives[ctx.Depth];

            // Lock siblings that HandleChildMerge might borrow from or merge with
            NodeWrapper leftSib = relatives.GetLeftSibling(ref accessor);
            NodeWrapper rightSib = relatives.GetRightSibling(ref accessor);
            if (leftSib.IsValid)
            {
                leftSib.PreDirtyForWrite(ref accessor);
                SpinWriteLock(leftSib.GetLatch(ref accessor));
            }
            if (rightSib.IsValid)
            {
                rightSib.PreDirtyForWrite(ref accessor);
                SpinWriteLock(rightSib.GetLatch(ref accessor));
            }

            merged = node.HandleChildMerge(ctx.PathChildIndices[ctx.Depth], ref relatives, ref accessor);

            // Mark obsolete internal node that was merged
            if (merged)
            {
                Interlocked.Increment(ref _mergeCount);
                if (relatives.HasTrueLeftSibling)
                {
                    // Current internal node merged into left sibling
                    node.GetLatch(ref accessor).MarkObsolete();
                    DeferredAdd(node.ChunkId, retireEpoch);
                }
                else if (relatives.HasTrueRightSibling && rightSib.IsValid)
                {
                    // Right sibling merged into current
                    rightSib.GetLatch(ref accessor).MarkObsolete();
                    DeferredAdd(rightSib.ChunkId, retireEpoch);
                }
            }

            // Unlock siblings + this path node
            if (rightSib.IsValid)
            {
                rightSib.GetLatch(ref accessor).WriteUnlock();
            }
            if (leftSib.IsValid)
            {
                leftSib.GetLatch(ref accessor).WriteUnlock();
            }
            node.GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 3.5: Unlock remaining path nodes above propagation level
        while (ctx.Depth > 0)
        {
            ctx.Depth--;
            ctx.PathNodes[ctx.Depth].GetLatch(ref accessor).WriteUnlock();
        }

        completed = true;
        return merged;
    }
}
