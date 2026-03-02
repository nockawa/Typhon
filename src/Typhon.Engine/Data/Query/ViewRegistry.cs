using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

internal class ViewRegistry
{
    private readonly IView[][] _viewsByField;     // [fieldIndex] -> IView[] (copy-on-write)
    private readonly Lock _writeLock = new();
    private int _viewCount;

    public ViewRegistry(int fieldCount)
    {
        _viewsByField = new IView[fieldCount][];
        for (var i = 0; i < fieldCount; i++)
        {
            _viewsByField[i] = [];
        }
    }

    public int ViewCount => _viewCount;

    public int FieldCount => _viewsByField.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<IView> GetViewsForField(int fieldIndex)
    {
        var views = _viewsByField;
        if ((uint)fieldIndex >= (uint)views.Length)
        {
            return ReadOnlySpan<IView>.Empty;
        }
        return views[fieldIndex];
    }

    public void RegisterView(IView view)
    {
        lock (_writeLock)
        {
            var deps = view.FieldDependencies;
            for (var i = 0; i < deps.Length; i++)
            {
                var fieldIndex = deps[i];
                if ((uint)fieldIndex >= (uint)_viewsByField.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(view), $"View {view.ViewId} declares field dependency {fieldIndex} but registry only has {_viewsByField.Length} fields.");
                }

                var existing = _viewsByField[fieldIndex];

                // Idempotent: skip if already present
                var found = false;
                for (var j = 0; j < existing.Length; j++)
                {
                    if (ReferenceEquals(existing[j], view))
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    continue;
                }

                // Copy-on-write: create new array +1
                var newArray = new IView[existing.Length + 1];
                Array.Copy(existing, newArray, existing.Length);
                newArray[existing.Length] = view;
                _viewsByField[fieldIndex] = newArray;
            }
            _viewCount++;
        }
    }

    public void DeregisterView(IView view)
    {
        lock (_writeLock)
        {
            var deps = view.FieldDependencies;
            var removedAny = false;
            for (var i = 0; i < deps.Length; i++)
            {
                var fieldIndex = deps[i];
                if ((uint)fieldIndex >= (uint)_viewsByField.Length)
                {
                    continue;
                }

                var existing = _viewsByField[fieldIndex];
                var idx = -1;
                for (var j = 0; j < existing.Length; j++)
                {
                    if (ReferenceEquals(existing[j], view))
                    {
                        idx = j;
                        break;
                    }
                }

                if (idx < 0)
                {
                    continue;
                }

                removedAny = true;
                if (existing.Length == 1)
                {
                    _viewsByField[fieldIndex] = [];
                }
                else
                {
                    var newArray = new IView[existing.Length - 1];
                    Array.Copy(existing, 0, newArray, 0, idx);
                    Array.Copy(existing, idx + 1, newArray, idx, existing.Length - idx - 1);
                    _viewsByField[fieldIndex] = newArray;
                }
            }
            if (removedAny)
            {
                _viewCount--;
            }
        }
    }
}