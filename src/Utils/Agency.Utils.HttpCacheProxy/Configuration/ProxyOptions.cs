namespace Agency.Utils.HttpCacheProxy.Configuration;

internal sealed class ProxyOptions
{
    public const string SectionName = "Proxy";
    public List<ProxyRouteOptions> Routes { get; set; } = [];
}
