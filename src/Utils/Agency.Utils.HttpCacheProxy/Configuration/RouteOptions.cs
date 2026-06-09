namespace Agency.Utils.HttpCacheProxy.Configuration;

internal sealed class ProxyRouteOptions
{
    public string Name { get; set; } = "";
    public int LocalPort { get; set; }
    public string PathPrefix { get; set; } = "/";
    public string TargetUrl { get; set; } = "";
    public RouteCacheOptions Cache { get; set; } = new();
    public bool LogRequestBody { get; set; }
    public bool LogResponseBody { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

internal sealed class RouteCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int TtlSeconds { get; set; } = 300;
}
