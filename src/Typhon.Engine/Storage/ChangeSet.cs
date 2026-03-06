using JetBrains.Annotations;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Typhon.Engine;

[PublicAPI]
public class ChangeSet
{
    private readonly PagedMMF _owner;
    private readonly HashSet<int> _changedMemoryPageIndices;
    private Task _saveTask;

    public ChangeSet(PagedMMF owner)
    {
        _owner = owner;
        _changedMemoryPageIndices = [];
    }

    /// <summary>
    /// Mark a page as dirty by its memory page index.
    /// Idempotent: only calls <see cref="PagedMMF.IncrementDirty"/> on the first add per page per ChangeSet lifetime.
    /// </summary>
    /// <returns><c>true</c> if this was the first registration for this page in this ChangeSet; <c>false</c> if already tracked.</returns>
    public bool AddByMemPageIndex(int memPageIndex)
    {
        if (!_changedMemoryPageIndices.Add(memPageIndex))
        {
            return false;
        }

        _owner.IncrementDirty(memPageIndex);
        return true;
    }

    public void SaveChanges() => SaveChangesAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public Task SaveChangesAsync()
    {
        if (_changedMemoryPageIndices.Count == 0)
        {
            return Task.CompletedTask;
        }

        var pages = _changedMemoryPageIndices.ToArray();
        _changedMemoryPageIndices.Clear();
        _saveTask = _owner.SavePages(pages);
        return _saveTask;
    }

    /// <summary>
    /// Caps the <c>DirtyCounter</c> of every page tracked by this ChangeSet at 1 (never decrementing to 0).
    /// <para>
    /// In WAL mode, <see cref="SaveChangesAsync"/> is never called because WAL records replace the need for dirty page writeback.
    /// However, <see cref="AddByMemPageIndex"/> still calls <c>IncrementDirty</c> for every page touched. Without this release, hot pages accumulate a
    /// DirtyCounter equal to the number of UoWs that touched them, making them permanently unevictable by the page cache clock-sweep.
    /// </para>
    /// <para>
    /// Capping at 1 (not 0) ensures pages remain marked as dirty for the next checkpoint cycle, which is the only path that writes WAL-protected pages to
    /// the data file and calls <see cref="PagedMMF.DecrementDirty"/>.
    /// </para>
    /// </summary>
    public void ReleaseExcessDirtyMarks()
    {
        foreach (var memPageIndex in _changedMemoryPageIndices)
        {
            _owner.DecrementDirtyToMin(memPageIndex, 1);
        }
        _changedMemoryPageIndices.Clear();
    }

    /// <summary>
    /// Undo all dirty marks tracked by this ChangeSet (used on transaction rollback).
    /// <para>
    /// Note: pages re-dirtied via <see cref="ChunkAccessor.MarkSlotDirty"/> (IncrementDirty on re-registration)
    /// may have DC &gt; 1. This method only decrements once per page. The remaining DC is cleaned up by checkpoint
    /// or by <see cref="ReleaseExcessDirtyMarks"/> in subsequent UoW disposal.
    /// </para>
    /// </summary>
    public void Reset()
    {
        var memPageIndices = _changedMemoryPageIndices.ToArray();
        foreach (var memPageIndex in memPageIndices)
        {
            _owner.DecrementDirty(memPageIndex);
        }
        _changedMemoryPageIndices.Clear();
        _saveTask = null;
    }
}