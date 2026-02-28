// unset

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

public abstract partial class BTree<TKey>
{
    /// <summary>Result of an OLC insert attempt.</summary>
    private enum OlcInsertResult
    {
        /// <summary>Insert completed successfully.</summary>
        Completed,
        /// <summary>OLC validation failed — caller should retry or fall back.</summary>
        Restart,
        /// <summary>Target leaf is full — needs pessimistic path for split/spill.</summary>
        LeafFull,
    }
    /// <summary>Creates the insert value, handling AllowMultiple buffer creation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CreateInsertValue(ref InsertArguments args, ref ChunkAccessor accessor)
    {
        if (AllowMultiple)
        {
            var bufferId = _storage.CreateBuffer(ref accessor);
            args.ElementId = _storage.Append(bufferId, args.GetValue(), ref accessor);
            args.BufferRootId = bufferId;
            return bufferId;
        }
        return args.GetValue();
    }
    private void AddOrUpdateCore(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;

        // 1. Empty tree initialization.
        //    An empty tree has no root node, so OLC readers/writers have nothing to latch on.
        //    CAS on _rootChunkId atomically races to claim the init slot; loser sees non-zero root and proceeds.
        if (IsEmpty())
        {
            var newRoot = AllocNode(NodeStates.IsLeaf, ref accessor);
            if (Interlocked.CompareExchange(ref _rootChunkId, newRoot.ChunkId, 0) == 0)
            {
                // We won the race — initialize root, LinkList, ReverseLinkList
                _linkList = newRoot;
                _reverseLinkList = newRoot;
                Height++;
                int value = CreateInsertValue(ref args, ref accessor);
                newRoot.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                IncCount();
                _cachedLastKey = args.Key;
                _hasCachedLastKey = true;
                return;
            }
            // Another thread initialized the root — free our unused node and fall through
            _segment.FreeChunk(newRoot.ChunkId);
        }

        // 2. OLC retry loop — handles append/prepend fast paths + non-full leaf inserts.
        //    Zero writes to shared state except the single leaf being modified (WriteLocked).
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var result = TryInsertOlc(ref args);
            if (result == OlcInsertResult.Completed)
            {
                return;
            }
            if (result == OlcInsertResult.LeafFull)
            {
                break; // Need pessimistic path for split/spill
            }
            // Restart: version validation failed
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // 3. Pessimistic fallback — exclusive lock + WriteLock all modified nodes for OLC readers
        Interlocked.Increment(ref _pessimisticFallbacks);
        AddOrUpdateCorePessimistic(ref args);
    }

    /// <summary>
    /// OLC insert attempt: tries append/prepend fast paths and non-full leaf insert.
    /// Only modifies a single leaf node (WriteLocked). Returns LeafFull when the target leaf is full and needs split/spill (which requires the pessimistic path).
    /// </summary>
    private OlcInsertResult TryInsertOlc(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;

        // --- Append fast path: insert at end of rightmost leaf ---
        var rl = _reverseLinkList;
        if (rl.IsValid)
        {
            var latch = rl.GetLatch(ref accessor);
            var version = latch.ReadVersion();
            if (version != 0)
            {
                // Fast path: use cached last key (field read ~1ns vs 3 chunk accessor calls ~35ns).
                // Safe even if stale: ValidateVersionLocked after write-lock catches any inconsistency.
                TKey lastKey = default;
                bool tryAppend;
                if (_hasCachedLastKey)
                {
                    lastKey = _cachedLastKey;
                    tryAppend = true;
                }
                else
                {
                    int rlCount = rl.GetCount(ref accessor);
                    tryAppend = rlCount > 0;
                    if (tryAppend)
                    {
                        // Use GetItem directly with known count — avoids redundant GetCount inside GetLast
                        lastKey = rl.GetItem(rlCount - 1, ref accessor).Key;
                    }
                }

                if (tryAppend)
                {
                    if (!latch.ValidateVersion(version))
                    {
                        return OlcInsertResult.Restart;
                    }

                    int order = args.Compare(args.Key, lastKey);
                    if (order > 0)
                    {
                        if (!latch.TryWriteLock())
                        {
                            return OlcInsertResult.Restart;
                        }
                        if (!latch.ValidateVersionLocked(version))
                        {
                            latch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        // Safety: if rl was split concurrently, it's no longer the rightmost leaf.
                        // GetNext().IsValid means a new right sibling exists — abort and fall through.
                        bool isFull = rl.GetIsFull(ref accessor);
                        if (isFull || rl.GetNext(ref accessor).IsValid)
                        {
                            latch.AbortWriteLock();
                            return isFull ? OlcInsertResult.LeafFull : OlcInsertResult.Restart;
                        }
                        int value = CreateInsertValue(ref args, ref accessor);
                        rl.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                        _cachedLastKey = args.Key;
                        _hasCachedLastKey = true;
                        latch.WriteUnlock();
                        IncCount();
                        return OlcInsertResult.Completed;
                    }

                    if (order == 0)
                    {
                        if (AllowMultiple)
                        {
                            if (!latch.TryWriteLock())
                            {
                                return OlcInsertResult.Restart;
                            }
                            if (!latch.ValidateVersionLocked(version))
                            {
                                latch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            var bufferRootId = rl.GetLast(ref accessor).Value;
                            args.ElementId = _storage.Append(bufferRootId, args.GetValue(), ref accessor);
                            args.BufferRootId = bufferRootId;
                            latch.WriteUnlock();
                            return OlcInsertResult.Completed;
                        }
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }
        }

        // --- Prepend fast path: insert at beginning of leftmost leaf ---
        var ll = _linkList;
        if (ll.IsValid)
        {
            var llLatch = ll.GetLatch(ref accessor);
            var llVersion = llLatch.ReadVersion();
            if (llVersion != 0)
            {
                int llCount = ll.GetCount(ref accessor);
                if (llCount > 0)
                {
                    var firstKey = ll.GetFirst(ref accessor).Key;
                    if (!llLatch.ValidateVersion(llVersion))
                    {
                        return OlcInsertResult.Restart;
                    }

                    int order = args.Compare(args.Key, firstKey);
                    if (order < 0)
                    {
                        if (!llLatch.TryWriteLock())
                        {
                            return OlcInsertResult.Restart;
                        }
                        if (!llLatch.ValidateVersionLocked(llVersion))
                        {
                            llLatch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        if (ll.GetIsFull(ref accessor))
                        {
                            llLatch.WriteUnlock();
                            return OlcInsertResult.LeafFull;
                        }
                        int value = CreateInsertValue(ref args, ref accessor);
                        ll.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
                        llLatch.WriteUnlock();
                        IncCount();
                        return OlcInsertResult.Completed;
                    }

                    if (order == 0)
                    {
                        if (AllowMultiple)
                        {
                            if (!llLatch.TryWriteLock())
                            {
                                return OlcInsertResult.Restart;
                            }
                            if (!llLatch.ValidateVersionLocked(llVersion))
                            {
                                llLatch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            var bufferRootId = ll.GetFirst(ref accessor).Value;
                            args.ElementId = _storage.Append(bufferRootId, args.GetValue(), ref accessor);
                            args.BufferRootId = bufferRootId;
                            llLatch.WriteUnlock();
                            return OlcInsertResult.Completed;
                        }
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }
        }

        // --- General path: optimistic descent to leaf, non-full insert ---
        // followRightLink: false — inserts must not follow B-link because inserting into the right sibling bypasses the spill/split path that updates parent
        // separators. If the leaf was split concurrently, version validation will trigger a restart.
        var (leafChunkId, leafVersion, _) = OptimisticDescendToLeaf(args.Key, ref accessor, followRightLink: false);
        if (leafChunkId == 0)
        {
            return OlcInsertResult.Restart;
        }

        var leaf = new NodeWrapper(_storage, leafChunkId);
        var leafLatch = leaf.GetLatch(ref accessor);
        if (!leafLatch.TryWriteLock())
        {
            return OlcInsertResult.Restart;
        }
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock();
            return OlcInsertResult.Restart;
        }
        // Range check: stale separator may route to wrong leaf after a concurrent split.
        // High key is an exclusive upper bound, so key >= highKey means we're out of range.
        if (leaf.GetCount(ref accessor) > 0 && leaf.GetNext(ref accessor).IsValid && args.Compare(args.Key, leaf.GetHighKey(ref accessor)) >= 0)
        {
            leafLatch.WriteUnlock();
            return OlcInsertResult.Restart;
        }
        if (leaf.GetIsFull(ref accessor))
        {
            leafLatch.WriteUnlock();
            return OlcInsertResult.LeafFull;
        }

        // Re-search under lock (key positions may have shifted since optimistic read)
        var keyIndex = leaf.Find(args.Key, args.KeyComparer, ref accessor);
        if (keyIndex < 0)
        {
            keyIndex = ~keyIndex;
            int value = CreateInsertValue(ref args, ref accessor);
            leaf.Insert(keyIndex, new KeyValueItem(args.Key, value), ref accessor);
            leafLatch.WriteUnlock();
            IncCount();
            return OlcInsertResult.Completed;
        }

        // Key already exists
        if (AllowMultiple)
        {
            var curItem = leaf.GetItem(keyIndex, ref accessor);
            args.ElementId = _storage.Append(curItem.Value, args.GetValue(), ref accessor);
            args.BufferRootId = curItem.Value;
            leafLatch.WriteUnlock();
            return OlcInsertResult.Completed;
        }
        leafLatch.WriteUnlock();
        ThrowHelper.ThrowUniqueConstraintViolation();
        return OlcInsertResult.Restart; // unreachable — ThrowHelper always throws
    }

    /// <summary>
    /// Pessimistic insert fallback: uses InsertIterative with latch-coupled SMO.
    /// No global lock — concurrency is handled by per-node OLC latches.
    /// </summary>
    private void AddOrUpdateCorePessimistic(ref InsertArguments args)
    {
        try
        {
            ref var accessor = ref args.Accessor;

            if (IsEmpty())
            {
                Root = AllocNode(NodeStates.IsLeaf, ref accessor);
                _linkList = Root;
                _reverseLinkList = _linkList;
                Height++;
            }

            // Append fast path: lock the last leaf and insert if key > lastKey and leaf not full.
            // Bypass when leaf is contended and sufficiently populated — fall through to InsertIterative
            // which has the path recording needed for contention split propagation.
            // Capture local refs to avoid races from concurrent ReverseLinkList/LinkList updates.
            {
                var rl = _reverseLinkList;
                var bypassAppendFastPath = rl.IsValid && rl.GetContentionHint(ref accessor) >= ContentionSplitThreshold && rl.GetCount(ref accessor) > rl.GetCapacity() / 2;
                var order = IsEmpty() ? 1 : args.Compare(args.Key, _hasCachedLastKey ? _cachedLastKey : rl.GetLast(ref accessor).Key);
                if (!bypassAppendFastPath && order > 0 && !rl.GetIsFull(ref accessor))
                {
                    var rlLatch = rl.GetLatch(ref accessor);
                    SpinWriteLock(rlLatch);
                    // Re-validate under lock: leaf may now be full, another writer inserted a larger key,
                    // or a concurrent split made this leaf no longer the rightmost (GetNext becomes valid).
                    if (!rl.GetIsFull(ref accessor) && !rl.GetNext(ref accessor).IsValid && args.Compare(args.Key, rl.GetLast(ref accessor).Key) > 0)
                    {
                        int value = CreateInsertValue(ref args, ref accessor);
                        rl.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                        _cachedLastKey = args.Key;
                        _hasCachedLastKey = true;
                        rlLatch.WriteUnlock();
                        IncCount();
                        return;
                    }
                    rlLatch.AbortWriteLock();
                    // Fall through to general path
                }
                else if (order == 0 && AllowMultiple)
                {
                    var rlLatch = rl.GetLatch(ref accessor);
                    SpinWriteLock(rlLatch);
                    var lastEntry = rl.GetLast(ref accessor);
                    if (args.Compare(args.Key, lastEntry.Key) == 0)
                    {
                        args.ElementId = _storage.Append(lastEntry.Value, args.GetValue(), ref accessor);
                        args.BufferRootId = lastEntry.Value;
                        rlLatch.WriteUnlock();
                        return;
                    }
                    rlLatch.AbortWriteLock();
                    // Fall through
                }
                else if (order == 0)
                {
                    ThrowHelper.ThrowUniqueConstraintViolation();
                }
            }

            // Prepend fast path: lock the first leaf and insert if key < firstKey and leaf not full.
            if (!IsEmpty())
            {
                var ll = _linkList;
                int order = args.Compare(args.Key, ll.GetFirst(ref accessor).Key);
                if (order < 0 && !ll.GetIsFull(ref accessor))
                {
                    var llLatch = ll.GetLatch(ref accessor);
                    SpinWriteLock(llLatch);
                    if (!ll.GetIsFull(ref accessor) && args.Compare(args.Key, ll.GetFirst(ref accessor).Key) < 0)
                    {
                        int value = CreateInsertValue(ref args, ref accessor);
                        ll.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
                        llLatch.WriteUnlock();
                        IncCount();
                        return;
                    }
                    llLatch.AbortWriteLock();
                    // Fall through
                }
                else if (order == 0 && AllowMultiple)
                {
                    var llLatch = ll.GetLatch(ref accessor);
                    SpinWriteLock(llLatch);
                    var firstEntry = ll.GetFirst(ref accessor);
                    if (args.Compare(args.Key, firstEntry.Key) == 0)
                    {
                        args.ElementId = _storage.Append(firstEntry.Value, args.GetValue(), ref accessor);
                        args.BufferRootId = firstEntry.Value;
                        llLatch.WriteUnlock();
                        return;
                    }
                    llLatch.AbortWriteLock();
                    // Fall through
                }
                else if (order == 0)
                {
                    ThrowHelper.ThrowUniqueConstraintViolation();
                }
            }

            // General path with latch-coupled SMO — retry on lock contention
            // InsertIterative handles root splits internally under the root's write lock.
            SpinWait spin = default;
            while (true)
            {
                InsertIterative(ref args, ref accessor, out bool insertCompleted);
                if (insertCompleted)
                {
                    break;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                spin.SpinOnce();
            }

            if (args.Added)
            {
                IncCount();
            }
            else if (!AllowMultiple)
            {
                ThrowHelper.ThrowUniqueConstraintViolation();
            }

            var next = _reverseLinkList.GetNext(ref accessor);
            if (next.IsValid)
            {
                _reverseLinkList = next;
            }
            _cachedLastKey = GetLast(ref accessor).Key;
            _hasCachedLastKey = true;
        }
        finally
        {
            // Reclaim deferred nodes whose epoch is safe (all readers have exited).
            DeferredReclaim();
        }
    }

    /// <summary>
    /// Iterative insert with latch-coupled SMO: descends optimistically recording PathVersions, then locks bottom-up only as needed for structural modifications.
    /// Fast path (leaf not full): locks only the leaf node.
    /// Slow path (leaf full, new key): locks leaf + neighbors + path nodes with version validation.
    /// Returns null if no root split, non-null promoted key if root split needed.
    /// Sets <paramref name="completed"/> to false when lock acquisition fails and caller must retry.
    /// </summary>
    private void InsertIterative(ref InsertArguments args, ref ChunkAccessor accessor, out bool completed)
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
                return;
            }

            var index = node.Find(args.Key, args.KeyComparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            var child = node.GetChild(index, ref accessor);
            int parentCount = node.GetCount(ref accessor);

            // Validate: node wasn't modified during our unlocked read
            if (!latch.ValidateVersion(version))
            {
                return;
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
        // Between Phase 1 descent and lock acquisition, a concurrent writer may have split/modified this leaf. Snapshot the version before locking,
        // then validate after.
        var leafLatch = node.GetLatch(ref accessor);
        int leafVersion = leafLatch.ReadVersion();
        if (leafVersion == 0)
        {
            // Leaf is locked or obsolete. SpinWriteLock to wait for the current holder to release, then restart — we can't validate without a baseline version.
            SpinWriteLock(leafLatch);
            leafLatch.AbortWriteLock(); // release without version bump (we didn't modify anything)
            return;
        }
        bool leafAcquiredClean = SpinWriteLock(leafLatch);
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock(); // release without version bump — leaf was modified, not by us
            return;
        }

        // Update contention hint: saturating counter for detecting hot leaves
        {
            int hint = node.GetContentionHint(ref accessor);
            if (!leafAcquiredClean)
            {
                node.SetContentionHint(Math.Min(hint + 1, 255), ref accessor);
            }
            else if (hint > 0)
            {
                node.SetContentionHint(hint - 1, ref accessor);
            }
        }

        // B-link move_right (Lehman & Yao): if the key is beyond this leaf's range, a concurrent split moved some keys to a right sibling. Chain right using
        // lock coupling (lock next before releasing current) until we find the correct leaf. Forward progress is guaranteed:
        // all movement is strictly rightward with no cycle, and SpinWriteLock waits for busy siblings.
        bool movedRight = false;
        // High key is an exclusive upper bound, so key >= highKey means we're out of range.
        while (node.GetCount(ref accessor) > 0 && node.GetNext(ref accessor).IsValid && args.Compare(args.Key, node.GetHighKey(ref accessor)) >= 0)
        {
            Interlocked.Increment(ref _moveRightCount);
            var nextNode = node.GetNext(ref accessor);
            SpinWriteLock(nextNode.GetLatch(ref accessor));

            // Gap check: after locking next leaf, verify key belongs there.
            // Without this, move_right chains across subtree boundaries when the key space has gaps (e.g., leaves [14-26] → [201-213] with no intermediate leaves).
            // Key 27 would land in [201-213] where it doesn't belong → BST violation.
            if (nextNode.GetCount(ref accessor) > 0 &&
                args.Compare(args.Key, nextNode.GetFirst(ref accessor).Key) < 0)
            {
                nextNode.GetLatch(ref accessor).AbortWriteLock();
                break; // Key falls in a gap — belongs in current leaf (will split via slow path)
            }

            node.GetLatch(ref accessor).AbortWriteLock();    // release current
            node = nextNode;
            movedRight = true;
        }

        // Fast path: leaf not full → InsertLeaf only modifies this leaf (insert or duplicate append)
        // If contention is high and leaf is sufficiently populated, fall through to contention split.
        bool itemAlreadyInserted = false;
        if (!node.GetIsFull(ref accessor))
        {
            node.InsertLeaf(ref args, ref relatives, ref accessor);
            itemAlreadyInserted = true;

            bool shouldContentionSplit = !movedRight && node.GetContentionHint(ref accessor) >= ContentionSplitThreshold
                                                     && node.GetCount(ref accessor) > node.GetCapacity() / 2;

            if (!shouldContentionSplit)
            {
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return;
            }

            // Contention split: reset hint and fall through to split path
            node.SetContentionHint(0, ref accessor);
            Interlocked.Increment(ref _contentionSplitCount);
            // Fall through to split path below
        }

        // Check if key already exists in full leaf (buffer append, no structural change)
        if (!itemAlreadyInserted)
        {
            var idx = node.Find(args.Key, args.KeyComparer, ref accessor);
            if (idx >= 0)
            {
                node.InsertLeaf(ref args, ref relatives, ref accessor);
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return;
            }
        }

        // After move_right, PathVersions and relatives are stale (recorded for the original leaf's// path). The gap check above prevents wrong-subtree inserts.
        // Force split (skip spill which uses stale relatives) and don't propagate — B-link right-link chain provides correct routing until a future insert
        // naturally propagates the separator.
        // This avoids the restart livelock that occurs when the slow path repeatedly fails.
        if (movedRight)
        {
            // Lock the next neighbor for the split's linked list update (SetPrevious on next node)
            var mrNext = node.GetNext(ref accessor);
            if (mrNext.IsValid)
            {
                SpinWriteLock(mrNext.GetLatch(ref accessor));
            }

            node.InsertLeaf(ref args, ref relatives, ref accessor, forceSplit: true);

            if (mrNext.IsValid)
            {
                mrNext.GetLatch(ref accessor).WriteUnlock();
            }
            node.GetLatch(ref accessor).WriteUnlock();
            completed = true;
            return;
        }

        // Slow path: leaf full or contention split — structural modification needed.
        // For contention split, skip leafPrev lock (no spill needed — item already in, only need right neighbor for linked list).
        // On lock failure: contention split uses WriteUnlock + completed=true (item is in); regular uses AbortWriteLock + restart.
        var leafPrev = itemAlreadyInserted ? default : node.GetPrevious(ref accessor);
        var leafNext = node.GetNext(ref accessor);
        if (leafPrev.IsValid && !leafPrev.GetLatch(ref accessor).TryWriteLock())
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return;
        }
        if (leafNext.IsValid && !leafNext.GetLatch(ref accessor).TryWriteLock())
        {
            if (leafPrev.IsValid)
            {
                leafPrev.GetLatch(ref accessor).AbortWriteLock();
            }
            if (itemAlreadyInserted)
            {
                // Contention split abort: item is already inserted, just skip the proactive split
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return;
            }
            node.GetLatch(ref accessor).AbortWriteLock();
            return;
        }

        // Lock path nodes bottom-up with version validation.
        // Required for ancestor key updates during spill and split propagation.
        for (int i = ctx.Depth - 1; i >= 0; i--)
        {
            var pathLatch = ctx.PathNodes[i].GetLatch(ref accessor);
            if (!pathLatch.TryWriteLock())
            {
                // Unlock path nodes already acquired above this level
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
                if (itemAlreadyInserted)
                {
                    node.GetLatch(ref accessor).WriteUnlock();
                    completed = true;
                    return;
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return;
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
                if (itemAlreadyInserted)
                {
                    node.GetLatch(ref accessor).WriteUnlock();
                    completed = true;
                    return;
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return;
            }
        }

        // All needed nodes locked — Phase 2: Insert at leaf (may spill or split) or contention split
        KeyValueItem? promoted;
        if (itemAlreadyInserted)
        {
            // Contention split: item is already in the leaf, just redistribute via SplitLeafRight
            var rightNode = node.SplitLeafRight(ref accessor);
            promoted = new KeyValueItem(rightNode.GetFirst(ref accessor).Key, rightNode.ChunkId);
        }
        else
        {
            promoted = node.InsertLeaf(ref args, ref relatives, ref accessor);
        }

        // Phase 2.5: Unlock leaf neighbors (version bumped by WriteUnlock)
        if (leafNext.IsValid)
        {
            leafNext.GetLatch(ref accessor).WriteUnlock();
        }
        if (leafPrev.IsValid)
        {
            leafPrev.GetLatch(ref accessor).WriteUnlock();
        }
        // Defer leaf unlock if this is a root-leaf that split (need to hold lock for atomic root creation)
        if (!(ctx.Depth == 0 && promoted != null))
        {
            node.GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 3: Propagate splits upward through internal nodes
        while (ctx.Depth > 0 && promoted != null)
        {
            ctx.Depth--;
            node = ctx.PathNodes[ctx.Depth];
            relatives = ctx.PathRelatives[ctx.Depth];

            // Lock siblings that HandlePromotedInsert might spill to (only when node is full)
            NodeWrapper leftSib = default, rightSib = default;
            if (node.GetIsFull(ref accessor))
            {
                leftSib = relatives.GetLeftSibling(ref accessor);
                rightSib = relatives.GetRightSibling(ref accessor);
                if (leftSib.IsValid)
                {
                    SpinWriteLock(leftSib.GetLatch(ref accessor));
                }
                if (rightSib.IsValid)
                {
                    SpinWriteLock(rightSib.GetLatch(ref accessor));
                }
            }

            promoted = node.HandlePromotedInsert(ctx.PathChildIndices[ctx.Depth], promoted.Value, ref relatives, ref accessor);

            // Unlock siblings
            if (rightSib.IsValid)
            {
                rightSib.GetLatch(ref accessor).WriteUnlock();
            }
            if (leftSib.IsValid)
            {
                leftSib.GetLatch(ref accessor).WriteUnlock();
            }
            // Defer root unlock if root split (need to hold lock for atomic root creation)
            if (!(ctx.Depth == 0 && promoted != null))
            {
                node.GetLatch(ref accessor).WriteUnlock();
            }
        }

        // Phase 3.5: Unlock remaining path nodes above propagation level
        while (ctx.Depth > 0)
        {
            ctx.Depth--;
            ctx.PathNodes[ctx.Depth].GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 4: Root split — create new root while holding old root's write lock.
        // This prevents concurrent InsertIterative calls from racing to create multiple roots.
        if (promoted != null)
        {
            var newRoot = AllocNode(NodeStates.None, ref accessor);
            newRoot.SetLeft(Root, ref accessor);
            newRoot.Insert(0, promoted.Value, ref accessor);
            Root = newRoot;
            Height++;
            node.GetLatch(ref accessor).WriteUnlock(); // release old root after publishing new root
        }

        completed = true;
    }
}
