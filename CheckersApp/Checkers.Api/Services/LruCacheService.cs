using Checkers.Api.Models;

namespace Checkers.Api.Services;

public interface ILruCacheService
{
    bool TryGet(string key, out SuggestResponse? value);
    void Set(string key, SuggestResponse value);
}

public class LruCacheService : ILruCacheService
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();

    private readonly Dictionary<string, LinkedListNode<CacheItem>> _map = new();
    private readonly LinkedList<CacheItem> _list = new();

    private record CacheItem(string Key, SuggestResponse Value, DateTime ExpireAt);

    public LruCacheService(IConfiguration config)
    {
        _capacity = config.GetValue<int>("Cache:Capacity", 20000);
        int ttlMinutes = config.GetValue<int>("Cache:TtlMinutes", 15);
        _ttl = TimeSpan.FromMinutes(ttlMinutes);
    }

    public bool TryGet(string key, out SuggestResponse? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (DateTime.UtcNow > node.Value.ExpireAt)
                {
                    _list.Remove(node);
                    _map.Remove(key);
                    value = null;
                    return false;
                }

                // Move to front of list (most recently used)
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = null;
            return false;
        }
    }

    public void Set(string key, SuggestResponse value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existingNode))
            {
                _list.Remove(existingNode);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                // Evict the oldest item from the tail of the list
                var oldest = _list.Last;
                if (oldest != null)
                {
                    _map.Remove(oldest.Value.Key);
                    _list.RemoveLast();
                }
            }

            var newItem = new CacheItem(key, value, DateTime.UtcNow.Add(_ttl));
            var newNode = _list.AddFirst(newItem);
            _map[key] = newNode;
        }
    }
}