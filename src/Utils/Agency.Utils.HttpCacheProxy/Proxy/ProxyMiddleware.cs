using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Agency.Utils.HttpCacheProxy.Configuration;
using Agency.Utils.HttpCacheProxy.Telemetry;
using Microsoft.Extensions.Options;

namespace Agency.Utils.HttpCacheProxy.Proxy;

internal sealed class ProxyMiddleware
{
    private static readonly HashSet<string> HopByHopHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "TE", "Trailers", "Transfer-Encoding", "Upgrade",
        };

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResponseCache _responseCache;
    private readonly List<ProxyRouteOptions> _routes;
    private readonly ILogger<ProxyMiddleware> _logger;

    public ProxyMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        ResponseCache responseCache,
        IOptions<ProxyOptions> proxyOptions,
        ILogger<ProxyMiddleware> logger)
    {
        this._next = next;
        this._httpClientFactory = httpClientFactory;
        this._responseCache = responseCache;
        this._logger = logger;
        this._routes = [.. proxyOptions.Value.Routes.OrderByDescending(r => r.PathPrefix.Length)];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();

        int localPort = context.Connection.LocalPort;
        string fullPath = context.Request.Path.Value ?? "/";

        ProxyRouteOptions? route = this._routes.FirstOrDefault(r =>
            r.LocalPort == localPort &&
            fullPath.StartsWith(r.PathPrefix, StringComparison.OrdinalIgnoreCase));

        if (route is null)
        {
            await this._next(context);
            return;
        }

        using Activity? activity = ProxyInstrumentation.Source
            .StartActivity($"proxy {route.Name}", ActivityKind.Server);
        activity?.SetTag("route", route.Name);
        activity?.SetTag("http.method", context.Request.Method);
        activity?.SetTag("http.path", fullPath);

        CacheKey cacheKey = await CacheKey.FromRequestAsync(context.Request);

        if (route.LogRequestBody)
        {
            LogBody(this._logger, "Request", route.Name, context.Request.Body);
            context.Request.Body.Position = 0;
        }

        bool cacheHit = false;
        CachedResponse? cached = null;

        if (route.Cache.Enabled && this._responseCache.TryGet(cacheKey, out cached) && cached is not null)
        {
            cacheHit = true;
            await WriteCachedResponseAsync(context, cached);
        }
        else
        {
            CachedResponse? forwarded = await this.ForwardRequestAsync(context, route, fullPath);
            if (forwarded is not null && route.Cache.Enabled)
            {
                this._responseCache.Set(cacheKey, forwarded);
            }
        }

        sw.Stop();

        Console.WriteLine(
            $"[{(cacheHit ? "HIT " : "MISS")}] {context.Request.Method} {fullPath}{context.Request.QueryString} → {context.Response.StatusCode} ({sw.ElapsedMilliseconds}ms)");

        ProxyInstrumentation.Requests.Add(1,
            new TagList
            {
                { "route", route.Name },
                { "cache_hit", cacheHit },
                { "status_code", context.Response.StatusCode },
            });

        ProxyInstrumentation.RequestDuration.Record(
            sw.Elapsed.TotalMilliseconds,
            new TagList { { "route", route.Name } });

        activity?.SetTag("cache_hit", cacheHit);
        activity?.SetTag("http.status_code", context.Response.StatusCode);
    }

    private static async Task WriteCachedResponseAsync(HttpContext context, CachedResponse cached)
    {
        context.Response.StatusCode = cached.StatusCode;
        foreach (var (name, values) in cached.Headers)
        {
            context.Response.Headers[name] = values;
        }
        context.Response.Headers.ContentLength = cached.Body.Length;
        await context.Response.Body.WriteAsync(cached.Body);
    }

    private async Task<CachedResponse?> ForwardRequestAsync(
        HttpContext context,
        ProxyRouteOptions route,
        string fullPath)
    {
        string prefixNoSlash = route.PathPrefix.TrimEnd('/');
        string suffix = fullPath[prefixNoSlash.Length..];
        string targetUrl = route.TargetUrl.TrimEnd('/') + suffix + context.Request.QueryString.Value;

        using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);

        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        foreach (var (name, values) in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(name))
            {
                continue;
            }
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] headerValues = values.Select(v => v ?? "").ToArray();
            if (!requestMessage.Headers.TryAddWithoutValidation(name, headerValues))
            {
                requestMessage.Content?.Headers.TryAddWithoutValidation(name, headerValues);
            }
        }

        requestMessage.Headers.TryAddWithoutValidation(
            "X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        requestMessage.Headers.TryAddWithoutValidation(
            "X-Forwarded-Host", context.Request.Host.Host);
        requestMessage.Headers.TryAddWithoutValidation(
            "X-Forwarded-Proto", context.Request.Scheme);

        HttpClient httpClient = this._httpClientFactory.CreateClient("proxy");
        var upstreamSw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(route.TimeoutSeconds));

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await httpClient.SendAsync(requestMessage, cts.Token);
        }
        catch (Exception ex)
        {
            upstreamSw.Stop();
            ProxyInstrumentation.UpstreamDuration.Record(
                upstreamSw.Elapsed.TotalMilliseconds,
                new TagList { { "route", route.Name } });

            this._logger.LogError(ex, "Upstream failed for route {Route} → {Target}", route.Name, targetUrl);
            context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
            await context.Response.WriteAsync("Bad Gateway: upstream request failed.");
            return null;
        }

        using (upstreamResponse)
        {
            upstreamSw.Stop();
            ProxyInstrumentation.UpstreamDuration.Record(
                upstreamSw.Elapsed.TotalMilliseconds,
                new TagList { { "route", route.Name } });

            byte[] body = await upstreamResponse.Content.ReadAsByteArrayAsync(cts.Token);

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in upstreamResponse.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                {
                    continue;
                }
                headers[header.Key] = [.. header.Value];
            }

            foreach (var header in upstreamResponse.Content.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                headers[header.Key] = [.. header.Value];
            }

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            foreach (var (name, values) in headers)
            {
                context.Response.Headers[name] = values;
            }
            context.Response.Headers.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, context.RequestAborted);

            if (route.LogResponseBody && body.Length > 0)
            {
                LogBody(this._logger, "Response", route.Name, new MemoryStream(body));
            }

            DateTimeOffset expiresAt = route.Cache.TtlSeconds <= 0
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow.AddSeconds(route.Cache.TtlSeconds);

            return new CachedResponse((int)upstreamResponse.StatusCode, headers, body, expiresAt);
        }
    }

    private static void LogBody(ILogger logger, string direction, string routeName, Stream bodyStream)
    {
        try
        {
            using var ms = new MemoryStream();
            bodyStream.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                return;
            }

            string snippet = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
            logger.LogInformation("{Direction} body (route={Route}, first 1KB): {Body}", direction, routeName, snippet);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not log {Direction} body for route {Route}", direction, routeName);
        }
    }
}
