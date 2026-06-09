using System.Collections.Concurrent;

namespace Agency.Utils.HttpCacheProxy.Proxy;

internal sealed record CachedResponse(
    int StatusCode,
    Dictionary<string, string[]> Headers,
    byte[] Body,
    DateTimeOffset ExpiresAt);

internal sealed class ResponseCache
{
    private readonly ConcurrentDictionary<string, CachedResponse> _store = new();

    internal bool TryGet(CacheKey key, out CachedResponse? response)
    {
        string k = ToKey(key);
        if (this._store.TryGetValue(k, out response))
        {
            if (response.ExpiresAt == DateTimeOffset.MaxValue ||
                DateTimeOffset.UtcNow <= response.ExpiresAt)
            {
                return true;
            }
            this._store.TryRemove(k, out _);
        }

        response = null;
        return false;
    }

    internal void Set(CacheKey key, CachedResponse response)
        => this._store[ToKey(key)] = response;

    private static string ToKey(CacheKey key)
        => $"{key.Method}|{key.PathAndQuery}|{key.BodyHash}";
}
