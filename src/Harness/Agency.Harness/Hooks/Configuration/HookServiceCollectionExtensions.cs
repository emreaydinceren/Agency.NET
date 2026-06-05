using Agency.Harness;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Hooks.Configuration;

public static class HookServiceCollectionExtensions
{
    public static IServiceCollection AddAgencyConfiguredHooks(
        this IServiceCollection services,
        IConfiguration config,
        string sectionName = "Hooks")
    {
        services.AddOptions<HooksOptions>().Bind(config.GetSection(sectionName));
        services.AddHttpClient();
        services.AddSingleton<IHookHandlerFactory>(sp =>
            new HookHandlerFactory(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetService<ILoggerFactory>()));
        services.AddSingleton<HookRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<HooksOptions>>().Value;
            // Validate event names at registry-build time (Task 04 contract)
            HooksOptionsValidator.Validate(config.GetSection(sectionName), options);
            return new HookRegistry(
                options,
                sp.GetRequiredService<IHookHandlerFactory>(),
                sp.GetRequiredService<ILogger<HookRegistry>>());
        });
        services.AddSingleton<IPostConfigureOptions<AgentOptions>>(sp =>
            new PostConfigureOptions<AgentOptions>(null, o =>
                o.ConfiguredHooks = sp.GetRequiredService<HookRegistry>().ToAgentHooks()));
        return services;
    }
}
