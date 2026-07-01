using Agency.Harness.Hooks.Configuration.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Hooks.Configuration;

/// <summary>Dependency-injection registration helpers for wiring configuration-driven agent hooks.</summary>
public static class HookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the infrastructure that builds <see cref="AgentOptions.ConfiguredHooks"/> from
    /// <paramref name="config"/>: binds <see cref="HooksOptions"/> from <paramref name="sectionName"/>,
    /// registers an <see cref="IHookHandlerFactory"/> and a <see cref="HookRegistry"/> (validated at
    /// resolution time), and wires a <see cref="PostConfigureOptions{TOptions}"/> that folds the
    /// registry into <see cref="AgentOptions.ConfiguredHooks"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="config">The configuration used to bind <see cref="HooksOptions"/>.</param>
    /// <param name="sectionName">The configuration section name to bind. Defaults to <c>"Hooks"</c>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
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
