using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Utils.HttpCacheProxy.Telemetry;

internal static class ProxyInstrumentation
{
    internal const string SourceName = "Agency.Utils.HttpCacheProxy";

    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
    internal static readonly Meter Meter = new(SourceName, "1.0.0");

    internal static readonly Counter<long> Requests =
        Meter.CreateCounter<long>("proxy.requests", "{request}", "Total proxy requests handled.");

    internal static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("proxy.request.duration", "ms", "End-to-end proxy request duration.");

    internal static readonly Histogram<double> UpstreamDuration =
        Meter.CreateHistogram<double>("proxy.upstream.duration", "ms", "Time waiting for the upstream HTTP response.");
}
