// unset

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

public partial class PagedMemoryMappedFile
{
    internal class ConcurrentCollection<T> : IEnumerable<T> where T : class
    {
        private readonly Memory<T> _data;
        private readonly ConcurrentBitmapL3Any _map;
        private readonly int _capacity;
        private int _count;

        public ConcurrentCollection(int capacity)
        {
            _data = new T[capacity];
            _map = new ConcurrentBitmapL3Any(capacity);
            _capacity = capacity;
            _count = 0;
        }
        public int Count => _count;
        public int Capacity => _capacity;

        public void Clear()
        {
            _data.Span.Clear();
            _map.Reset();
            _count = 0;
        }

        // Not thread-safe
        public int Add(T obj)
        {
            var span = _data.Span;
            if (_count >= _capacity)
            {
                ThrowCapacityReached();
            }
            var index = _count;
            _map.Set(_count);
            span[_count++] = obj;

            return index;
        }

        public bool Pick(int index, out T result)
        {
            result = Interlocked.Exchange(ref _data.Span[index], null);
            return result != null;
        }

        public void PutBack(int index, T obj)
        {
            var prev = Interlocked.CompareExchange(ref _data.Span[index], obj, null);
            if (prev != null)
            {
                ThrowInvalidPutBack(index);
            }
        }

        public void Release(int index)
        {
            _map.Clear(index);
        }

        private static void ThrowInvalidPutBack(int index) => throw new Exception($"Invalid put back at location {index}");
        private static void ThrowCapacityReached() => throw new Exception("Can add a new element, the array capacity is reached");

        public readonly struct Enumerator : IEnumerator<T>
        {
            private readonly ConcurrentCollection<T> _owner;
            private readonly IEnumerator<int> _e;

            public Enumerator(ConcurrentCollection<T> owner)
            {
                _owner = owner;
                _e = _owner._map.GetEnumerator();
            }
            public bool MoveNext() => _e.MoveNext();
            public void Reset() => _e.Reset();
            public int CurrentIndex => _e.Current;
            public T Current => _owner._data.Span[_e.Current];
            object IEnumerator.Current => Current;
            public void Dispose() => _e?.Dispose();
        }

        public IEnumerator<T> GetEnumerator() => new Enumerator(this);
        public Enumerator GetSpecializedEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}