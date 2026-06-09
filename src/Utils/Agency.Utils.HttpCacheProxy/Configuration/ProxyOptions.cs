namespace Agency.Utils.HttpCacheProxy.Configuration;

internal sealed class ProxyOptions
{
    public const string SectionName = "Proxy";
    public List<ProxyRouteOptions> Routes { get; set; } = [];
    public FileCacheOptions FileCache { get; set; } = new();
}

internal sealed class FileCacheOptions
{
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "cache";
}
