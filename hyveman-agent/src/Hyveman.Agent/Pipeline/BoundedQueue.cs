namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Lock-based bounded ring queue with drop-oldest semantics (AGENT.md §6.3,
/// §16). Producers (EvtSubscribe callbacks) never block; on full, the oldest
/// item is dropped and the caller is told (exact drop counting).
/// </summary>
public sealed class BoundedQueue<T>
{
    private readonly T?[] _buffer;
    private readonly object _sync = new();
    private int _head;      // index of oldest item
    private int _count;

    public BoundedQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public bool IsEmpty
    {
        get { lock (_sync) return _count == 0; }
    }

    /// <summary>Adds an item. Returns 1 if the oldest item was dropped, else 0. Never blocks.</summary>
    public int TryAdd(T item)
    {
        lock (_sync)
        {
            int dropped = 0;
            if (_count == _buffer.Length)
            {
                // Drop oldest (ring head), count it.
                _buffer[_head] = default;
                _head = (_head + 1) % _buffer.Length;
                _count--;
                dropped = 1;
            }

            var tail = (_head + _count) % _buffer.Length;
            _buffer[tail] = item;
            _count++;
            Monitor.Pulse(_sync);
            return dropped;
        }
    }

    /// <summary>Removes the oldest item, waiting up to <paramref name="timeout"/>.</summary>
    public bool TryTake(out T? item, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_sync)
        {
            while (_count == 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    item = default;
                    return false;
                }
                Monitor.Wait(_sync, TimeSpan.FromMilliseconds(Math.Min(remaining, 250)));
            }

            item = _buffer[_head]!;
            _buffer[_head] = default;
            _head = (_head + 1) % _buffer.Length;
            _count--;
            return true;
        }
    }

    /// <summary>Drains everything currently buffered, oldest first.</summary>
    public List<T> Drain()
    {
        var list = new List<T>(_count);
        lock (_sync)
        {
            while (_count > 0)
            {
                list.Add(_buffer[_head]!);
                _buffer[_head] = default;
                _head = (_head + 1) % _buffer.Length;
                _count--;
            }
        }
        return list;
    }
}
