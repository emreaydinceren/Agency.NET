using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Instructions;

/// <summary>
/// Extension methods for registering the Agency instruction resolver with
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class InstructionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IInstructionResolver"/> as a singleton, bound from the
    /// <paramref name="config"/> section named <paramref name="sectionName"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="config">Application configuration containing the instructions section.</param>
    /// <param name="sectionName">
    /// Name of the configuration section to bind; defaults to <c>"Instructions"</c>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="InstructionsOptions.FallbackFilenames"/> is empty.</exception>
    public static IServiceCollection AddAgencyInstructions(
        this IServiceCollection services, IConfiguration config, string sectionName = "Instructions")
    {
        ArgumentNullException.ThrowIfNull(config);

        services.AddOptions<InstructionsOptions>().Bind(config.GetSection(sectionName));
        services.AddSingleton<IInstructionResolver>(sp =>
        {
            InstructionsOptions options = sp.GetRequiredService<IOptions<InstructionsOptions>>().Value;
            if (options.FallbackFilenames.Length == 0)
            {
                throw new ArgumentException(
                    "Instructions:FallbackFilenames must contain at least one filename.");
            }

            return new ProjectInstructionResolver(options.FallbackFilenames);
        });
        return services;
    }
}
