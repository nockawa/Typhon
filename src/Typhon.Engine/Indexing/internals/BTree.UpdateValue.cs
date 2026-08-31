using System.Threading;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Overwrite the value stored under <paramref name="key"/> in place, without touching the key or any structure. Unique indexes.
    /// </summary>
    /// <param name="key">The key whose value is replaced. Must already exist; this operation never inserts.</param>
    /// <param name="newValue">The value to store.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet. A per-operation warm accessor is rented for the descent.</param>
    /// <returns><c>true</c> if the key was found and its value written; <c>false</c> if the key is absent, in which case nothing is mutated.</returns>
    /// <remarks>
    /// <para>
    /// Exists because migration changes a value while the key stays the same, and <c>Remove</c> + <c>Add</c> pays two full root-to-leaf descents to do it —
    /// the dominant per-indexed-field cost in the migration path (#872 C9). This is one descent and one 4-byte store.
    /// </para>
    /// <para>
    /// <b>Structurally inert by construction, not by care.</b> It cannot split, merge, shift an item, allocate a node or move the root, because it calls
    /// nothing that does — the entire mutation is <c>SetValueOnly</c>. That is why <c>IXW-05</c> (a writer creates a new root only while it still holds the
    /// current root) has nothing to say here, and why the count, the key array, the separators and the <c>HighKey</c> are unchanged as a matter of which
    /// bytes are written rather than of what is written into them.
    /// </para>
    /// <para>
    /// <b>Against a concurrent reader</b> this is strictly better than the pair it replaces: the value is published with a release store, so a reader sees
    /// the old value or the new one and never a torn one, and there is no window in which the entry is ABSENT — which is exactly the false negative
    /// <c>Remove</c> + <c>Add</c> exposes (<c>IX-06</c>).
    /// </para>
    /// <para>
    /// <b>Unlock discipline.</b> The one path that writes releases with <c>WriteUnlock</c>; every bail uses <c>AbortWriteLock</c>. The difference is that
    /// <c>WriteUnlock</c> bumps the node version, telling every optimistic reader holding a snapshot that it is stale — true after a write, a lie after a
    /// bail, and #679 measured those spurious bumps as the cause of a restart storm. See the remarks on <c>Move</c> in <c>BTree.Move.cs</c>.
    /// </para>
    /// </remarks>
    public bool TryUpdateValue(TKey key, int newValue, ref ChunkAccessor<TStore> accessor)
    {
        // try/finally, not a bare rent: the warm accessor is pooled per segment and a path that leaves without returning it makes the NEXT rent a double-rent.
        // Debug.Fail catches that, which is how this was found — but only on the second call, so a single-test run would have looked clean.
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            for (var attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
            {
                // followRightLink stays at its default `true`: this looks up an EXISTING key, which is the case the B-link right-walk exists for. Move passes
                // false for its second descent because that one picks an INSERTION target for a key that exists nowhere yet, so the walk would run past the leaf
                // whose separator range owns it (see BTree.Move.cs).
                var (leafId, version, keyIndex) = OptimisticDescendToLeaf(key, ref opAccessor);
                if (leafId == 0)
                {
                    if (IsEmpty())
                    {
                        return false;
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (keyIndex < 0)
                {
                    // Absent — but a stale read looks identical, so believe it only if the leaf has not moved under us.
                    var checkLeaf = _storage.LoadNode(leafId);
                    if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(version))
                    {
                        return false;
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                var leaf = _storage.LoadNode(leafId);
                leaf.PreDirtyForWrite(ref opAccessor);
                var latch = leaf.GetLatch(ref opAccessor);
                if (!latch.TryWriteLock())
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (!latch.ValidateVersionLocked(version))
                {
                    latch.AbortWriteLock();
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // Authority re-check under the lock: a split between descent and lock can leave this leaf no longer owning the key's range.
                if (KeyOutsideLeafAuthority(leaf, key, Comparer, ref opAccessor))
                {
                    latch.AbortWriteLock();
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // Re-find under the lock — the index from the optimistic descent may have shifted.
                var index = leaf.Find(key, Comparer, ref opAccessor);
                if (index < 0)
                {
                    latch.AbortWriteLock();
                    return false;
                }

                _storage.SetValueOnly(leaf, index, newValue, ref opAccessor);
                latch.WriteUnlock();
                return true;
            }

            // Bounded like every other OLC loop here. Unlike Insert there is no pessimistic fallback to take: a value update has no structural work to
            // serialise, so exhausting the budget means sustained contention on one leaf and the honest answer is to report that rather than spin further.
            // The caller (migration) retries on the next tick.
            return false;
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }
    }

    /// <summary>
    /// Overwrite one element's value inside an <c>AllowMultiple</c> key's buffer, in place. Siblings at the same key and the element's own id are unchanged.
    /// </summary>
    /// <param name="key">The indexed key. Must already exist.</param>
    /// <param name="elementId">The element's chunk id, as returned by <c>AddValue</c> and taken by <c>RemoveValue</c>.</param>
    /// <param name="oldValue">The value currently stored. Required because elements are addressed BY VALUE within their chunk, not by position.</param>
    /// <param name="newValue">The value to store.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <returns><c>true</c> if the key and the element were found and the value written; otherwise <c>false</c>, with nothing mutated.</returns>
    /// <remarks>
    /// <para>
    /// <b>The signature carries an <paramref name="oldValue"/> the design did not anticipate.</b> A buffer element has no positional address: the id is the
    /// CHUNK holding it, and <c>DeleteElement</c> finds the element by scanning that chunk for a value match. Locating it for an update needs the same
    /// input. Migration has it — the old <c>ClusterLocation</c> is precisely what is being replaced.
    /// </para>
    /// <para>
    /// <b>Nothing moves.</b> Unlike remove-then-append, this changes no element count and performs no swap-with-last, so the caller's
    /// <paramref name="elementId"/> stays valid and every sibling keeps its position — which is what makes it safe to hold element ids across a migration
    /// (#872 AC-4.3).
    /// </para>
    /// <para>
    /// A separate sibling accessor is rented for the buffer pages, mirroring <c>MoveValue</c>: VSBS chunks and index nodes evict each other out of one warm
    /// accessor, and the descent above still needs its leaf.
    /// </para>
    /// </remarks>
    public bool TryUpdateValueAt(TKey key, int elementId, int oldValue, int newValue, ref ChunkAccessor<TStore> accessor)
    {
        if (!AllowMultiple)
        {
            return false;
        }

        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
            try
            {
                for (var attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
                {
                    var (leafId, version, keyIndex) = OptimisticDescendToLeaf(key, ref opAccessor);
                    if (leafId == 0)
                    {
                        if (IsEmpty())
                        {
                            return false;
                        }
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (keyIndex < 0)
                    {
                        var checkLeaf = _storage.LoadNode(leafId);
                        if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(version))
                        {
                            return false;
                        }
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // The leaf entry is locked while the bufferId is read, because a concurrent structural change could otherwise hand us a stale one. The
                    // buffer write itself is serialised by the buffer's own lock inside UpdateInBuffer.
                    var leaf = _storage.LoadNode(leafId);
                    leaf.PreDirtyForWrite(ref opAccessor);
                    var latch = leaf.GetLatch(ref opAccessor);
                    if (!latch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!latch.ValidateVersionLocked(version))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (KeyOutsideLeafAuthority(leaf, key, Comparer, ref opAccessor))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var index = leaf.Find(key, Comparer, ref opAccessor);
                    if (index < 0)
                    {
                        latch.AbortWriteLock();
                        return false;
                    }

                    var bufferId = leaf.GetItem(index, ref opAccessor).Value;
                    var updated = _storage.UpdateInBuffer(bufferId, elementId, oldValue, newValue, ref sibAccessor);

                    // WriteUnlock either way: a buffer write has already touched storage even when the element was not found, and Move's discipline is
                    // explicit that anything which has written to a buffer is not a no-op. AbortWriteLock here would be the clever choice and the wrong one.
                    latch.WriteUnlock();
                    return updated;
                }

                return false;
            }
            finally
            {
                _segment.ReturnWarmSiblingAccessor();
            }
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }
    }
}
