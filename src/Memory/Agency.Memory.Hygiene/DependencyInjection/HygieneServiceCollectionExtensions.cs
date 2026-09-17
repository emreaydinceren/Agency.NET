using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Hygiene.DependencyInjection;

/// <summary>
/// Extension methods for registering the hygiene sweeper background service with
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// Call <c>AddAgencyHygiene()</c> after an <see cref="IMemoryStore"/> implementation has been
/// registered. The sweeper resolves <see cref="TimeProvider"/> from the container so that
/// functional tests can register a <c>FakeTimeProvider</c> to advance virtual time and control
/// the sweep schedule without wall-clock delays.
/// If no <see cref="TimeProvider"/> is registered, <see cref="TimeProvider.System"/> is used.
/// </remarks>
public static class HygieneServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HygieneSweeperBackgroundService"/> as a hosted service.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">
    /// Optional configuration root. When supplied, <see cref="MemoryOptions"/> is bound from its
    /// <c>Memory</c> section before <paramref name="configure"/> runs — configuration binds first,
    /// the action applies after, so a code-based override still wins.
    /// </param>
    /// <param name="configure">Optional action to configure <see cref="MemoryOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyHygiene(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<MemoryOptions>? configure = null)
    {
        OptionsBuilder<MemoryOptions> optionsBuilder = services.AddOptions<MemoryOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection("Memory"));
        }

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHostedService<HygieneSweeperBackgroundService>(sp => new HygieneSweeperBackgroundService(
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<IOptions<MemoryOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<HygieneSweeperBackgroundService>>()));

        return services;
    }
}
