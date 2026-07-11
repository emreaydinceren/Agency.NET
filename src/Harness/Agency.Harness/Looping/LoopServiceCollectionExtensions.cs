using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Harness.Looping;

/// <summary>
/// Extension methods for registering Loop Kit services with <see cref="IServiceCollection"/>.
/// </summary>
public static class LoopServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LoopOptions"/> from the <c>"Loop"</c> configuration section,
    /// mirroring the <c>AddOptions&lt;AgentOptions&gt;().BindConfiguration("Agent")</c> pattern.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="config">
    /// Application configuration containing the <c>"Loop"</c> section.
    /// </param>
    /// <param name="sectionName">
    /// Name of the configuration section to bind; defaults to <c>"Loop"</c>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddAgencyLoop(
        this IServiceCollection services,
        IConfiguration config,
        string sectionName = "Loop")
    {
        ArgumentNullException.ThrowIfNull(config);

        services.AddOptions<LoopOptions>().Bind(config.GetSection(sectionName));
        return services;
    }
}
