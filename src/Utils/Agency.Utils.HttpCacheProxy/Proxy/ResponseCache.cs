using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agency.Utils.HttpCacheProxy.Configuration;
using Microsoft.Extensions.Options;

namespace Agency.Utils.HttpCacheProxy.Proxy;

internal sealed record CachedResponse(
    int StatusCode,
    Dictionary<string, string[]> Headers,
    byte[] Body,
    DateTimeOffset ExpiresAt);

internal sealed class ResponseCache
{
    private readonly ConcurrentDictionary<string, CachedResponse> _store = new();
    private readonly ILogger<ResponseCache> _logger;
    private readonly bool _fileCacheEnabled;
    private readonly string? _directory;

    public ResponseCache(
        IOptions<ProxyOptions> options,
        IHostEnvironment environment,
        ILogger<ResponseCache> logger)
    {
        this._logger = logger;

        FileCacheOptions fileCache = options.Value.FileCache;
        this._fileCacheEnabled = fileCache.Enabled;

        if (this._fileCacheEnabled)
        {
            this._directory = Path.IsPathRooted(fileCache.Directory)
                ? fileCache.Directory
                : Path.Combine(environment.ContentRootPath, fileCache.Directory);

            this.LoadFromDisk();
        }
    }

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
    {
        if (this._fileCacheEnabled)
        {
            // File cache entries never expire; store the in-memory copy as non-expiring too so
            // both tiers stay consistent for the lifetime of the process.
            response = response with { ExpiresAt = DateTimeOffset.MaxValue };
            this._store[ToKey(key)] = response;
            this.WriteToDisk(key, response);
        }
        else
        {
            this._store[ToKey(key)] = response;
        }
    }

    private static string ToKey(CacheKey key)
        => $"{key.Method}|{key.PathAndQuery}|{key.BodyHash}";

    // ── Filesystem tier ───────────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        try
        {
            Directory.CreateDirectory(this._directory!);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Could not create file cache directory {Directory}", this._directory);
            return;
        }

        int loaded = 0;
        foreach (string file in Directory.EnumerateFiles(this._directory!, "*.json"))
        {
            try
            {
                CachedResponseBlob? blob = JsonSerializer.Deserialize<CachedResponseBlob>(
                    File.ReadAllBytes(file));
                if (blob is null)
                {
                    continue;
                }

                var key = new CacheKey(blob.Method, blob.PathAndQuery, blob.BodyHash);
                this._store[ToKey(key)] = new CachedResponse(
                    blob.StatusCode, blob.Headers, blob.Body, DateTimeOffset.MaxValue);
                loaded++;
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Skipping unreadable cache blob {File}", file);
            }
        }

        this._logger.LogInformation("Loaded {Count} cached responses from {Directory}", loaded, this._directory);
    }

    private void WriteToDisk(CacheKey key, CachedResponse response)
    {
        string dest = Path.Combine(this._directory!, BlobFileName(key));
        string tmp = $"{dest}.{Guid.NewGuid():N}.tmp";

        try
        {
            var blob = new CachedResponseBlob(
                key.Method, key.PathAndQuery, key.BodyHash,
                response.StatusCode, response.Headers, response.Body);

            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(blob, SerializerOptions));
            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Could not persist cache blob for {Key}", ToKey(key));
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup of the temp file; ignore.
            }
        }
    }

    private static string BlobFileName(CacheKey key)
    {
        string composite = ToKey(key);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));
        return $"{hash}.json";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private sealed record CachedResponseBlob(
        string Method,
        string PathAndQuery,
        string BodyHash,
        int StatusCode,
        Dictionary<string, string[]> Headers,
        byte[] Body);
}
