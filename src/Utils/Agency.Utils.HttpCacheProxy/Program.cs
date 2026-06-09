using Agency.Utils.HttpCacheProxy.Configuration;
using Agency.Utils.HttpCacheProxy.Proxy;
using Agency.Utils.HttpCacheProxy.Telemetry;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;

namespace Agency.Utils.HttpCacheProxy;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddTelemetry(builder.Configuration);

        builder.Services.AddOptions<ProxyOptions>()
            .BindConfiguration(ProxyOptions.SectionName)
            .ValidateOnStart();

        builder.Services.AddHttpClient("proxy")
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            });

        builder.Services.AddSingleton<ResponseCache>();

        ProxyOptions proxyOptions = builder.Configuration
            .GetSection(ProxyOptions.SectionName)
            .Get<ProxyOptions>() ?? new ProxyOptions();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (int port in proxyOptions.Routes.Select(r => r.LocalPort).Distinct())
            {
                kestrel.ListenLocalhost(port, o => o.Protocols = HttpProtocols.Http1);
            }
        });

        WebApplication app = builder.Build();

        app.UseMiddleware<ProxyMiddleware>();

        await app.RunAsync();
        await Log.CloseAndFlushAsync();
    }
}
