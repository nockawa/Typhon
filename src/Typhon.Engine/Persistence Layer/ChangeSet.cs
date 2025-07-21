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

    public void Add(PageAccessor accessor)
    {
        if (_changedMemoryPageIndices.Add(accessor.MemPageIndex))
        {
            _owner.IncrementDirty(accessor.MemPageIndex);
        }
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