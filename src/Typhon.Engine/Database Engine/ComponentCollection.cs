using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct ComponentCollection<T> where T : unmanaged
{
    internal int _bufferId;
}

[PublicAPI]
public ref struct ComponentCollectionAccessor<T> : IDisposable where T : unmanaged
{
    private VariableSizedBufferSegment<T> _vsbs;
    private ref ComponentCollection<T> _field;
    private readonly ChunkRandomAccessor _cbsa;
    private readonly int _initialBufferId;
    private readonly ChangeSet _changeSet;

    public ComponentCollectionAccessor(ChangeSet changeSet, VariableSizedBufferSegment<T> vsbs, ref ComponentCollection<T> field)
    {
        _vsbs = vsbs;
        _changeSet = changeSet;
        _field = ref field;
        _cbsa = _vsbs.Segment.CreateChunkRandomAccessor(8, changeSet);
        _initialBufferId = field._bufferId;
    }

    public void Dispose() => _cbsa.CommitChanges();

    public void Add(T value)
    {
        // First time adding an item?
        if (_field._bufferId == 0)
        {
            _field._bufferId = _vsbs.AllocateBuffer(_cbsa);
        } 
        
        // Need to clone the buffer as we mutate its content
        else if (_initialBufferId == _field._bufferId)
        {
            _field._bufferId = _vsbs.CloneBuffer(_initialBufferId, _cbsa);
        }
        
        _vsbs.AddElement(_field._bufferId, value, _cbsa);
    }

    public int ElementCount
    {
        get
        {
            using var a = new VariableSizedBufferAccessor<T>(_vsbs, _field._bufferId);
            return a.TotalCount;            
        }
    }

    public int GetAllElements(Span<T> dest)
    {
        using var a = new VariableSizedBufferAccessor<T>(_vsbs, _field._bufferId);
        if (dest.Length < a.TotalCount)
        {
            return 0;
        }
        var destI = 0;
        do
        {
            var elements = a.ReadOnlyElements;
            elements.CopyTo(dest.Slice(destI));
            destI += elements.Length;
        } while (a.NextChunk());

        return destI;
    }
    
    public T[] GetAllElements()
    {
        using var a = new VariableSizedBufferAccessor<T>(_vsbs, _field._bufferId);
        var dest = new T[a.TotalCount];

        var destI = 0;
        var destSpan = dest.AsSpan();
        do
        {
            var elements = a.ReadOnlyElements;
            elements.CopyTo(destSpan.Slice(destI));
            destI += elements.Length;
        } while (a.NextChunk());

        return dest;
    }
}