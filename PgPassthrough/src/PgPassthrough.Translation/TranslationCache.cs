using System.Collections.Concurrent;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Translation;

/// <summary>
/// Thread-safe LRU cache for translation results.
/// Key: normalised SQL string (from NormalisedKeyPrinter).
/// Value: TranslationResult.
///
/// Uses a ConcurrentDictionary with a bounded size.
/// When the cache exceeds the max entries, the oldest 25% of entries
/// are evicted (approximate LRU via insertion order tracking).
/// </summary>
internal sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly int _maxEntries;
    private int _count;

    public TranslationCache(int maxEntries)
    {
        _maxEntries = maxEntries > 0 ? maxEntries : 10_000;
    }

    public int Count => _count;

    public bool TryGet(string key, out TranslationResult? result)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            entry.LastAccess = Environment.TickCount64;
            result = entry.Result;
            return true;
        }
        result = null;
        return false;
    }

    public void Set(string key, TranslationResult result)
    {
        var entry = new CacheEntry(result);
        if (_cache.TryAdd(key, entry))
        {
            _insertionOrder.Enqueue(key);
            Interlocked.Increment(ref _count);
            EvictIfNeeded();
        }
        else
        {
            // Key already exists — update
            _cache[key] = entry;
        }
    }

    private void EvictIfNeeded()
    {
        if (_count <= _maxEntries) return;

        // Evict ~25% of entries (approximate LRU via insertion order)
        int toEvict = _maxEntries / 4;
        for (int i = 0; i < toEvict && _insertionOrder.TryDequeue(out var key); i++)
        {
            if (_cache.TryRemove(key, out _))
                Interlocked.Decrement(ref _count);
        }
    }

    private sealed class CacheEntry
    {
        public TranslationResult Result { get; }
        public long LastAccess { get; set; }

        public CacheEntry(TranslationResult result)
        {
            Result = result;
            LastAccess = Environment.TickCount64;
        }
    }
}
