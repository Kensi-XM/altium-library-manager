namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// LRU cache bounded by entry count and, optionally, by total size: each value gets a size from
    /// <c>sizeOf</c> when added, and the least recently used entries are dropped while either limit is exceeded.
    /// The newest entry is always kept, even if it alone is over the size limit.
    /// Not thread-safe; the caller is responsible for synchronization.
    /// </summary>
    public class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly long _maxSize;
        private readonly Func<TValue, long>? _sizeOf;
        private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
        private readonly LinkedList<Entry> _order = new();

        private record struct Entry(TKey Key, TValue Value, long Size);

        public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null,
            Func<TValue, long>? sizeOf = null, long maxSize = long.MaxValue)
        {
            _capacity = capacity;
            _sizeOf = sizeOf;
            _maxSize = maxSize;
            _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
        }

        /// <summary>Total size of the cached values, as reported by <c>sizeOf</c>.</summary>
        public long Size { get; private set; }

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }

        public void Add(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                Size -= existing.Value.Size;
            }

            var size = _sizeOf?.Invoke(value) ?? 0;
            _map[key] = _order.AddFirst(new Entry(key, value, size));
            Size += size;

            while (_order.Count > 1 && (_order.Count > _capacity || Size > _maxSize))
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _map.Remove(oldest.Key);
                Size -= oldest.Size;
            }
        }
    }
}
