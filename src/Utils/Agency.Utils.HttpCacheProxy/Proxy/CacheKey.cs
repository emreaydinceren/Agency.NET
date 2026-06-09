using System.Security.Cryptography;

namespace Agency.Utils.HttpCacheProxy.Proxy;

internal readonly record struct CacheKey(string Method, string PathAndQuery, string BodyHash)
{
    internal static async Task<CacheKey> FromRequestAsync(HttpRequest request)
    {
        request.EnableBuffering();

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        request.Body.Position = 0;

        string hash = Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
        return new(
            request.Method.ToUpperInvariant(),
            request.Path.Value + request.QueryString.Value,
            hash);
    }
}
