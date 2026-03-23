using System;
using System.Collections.Generic;
using System.Threading;

namespace SerialPortService.Services
{
    internal sealed class RingBuffer<T>
    {
        private readonly T?[] _items;
        private int _nextIndex;
        private int _count;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _items = new T?[capacity];
        }

        public void Add(T item)
        {
            lock (_items)
            {
                _items[_nextIndex] = item;
                _nextIndex = (_nextIndex + 1) % _items.Length;
                if (_count < _items.Length)
                {
                    _count++;
                }
            }
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_items)
            {
                var result = new List<T>(_count);
                var start = _count == _items.Length ? _nextIndex : 0;
                for (var i = 0; i < _count; i++)
                {
                    var index = (start + i) % _items.Length;
                    var item = _items[index];
                    if (item is not null)
                    {
                        result.Add(item);
                    }
                }

                return result;
            }
        }
    }
}

