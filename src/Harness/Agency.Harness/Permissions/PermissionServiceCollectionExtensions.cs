using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Permissions;

/// <summary>
/// Extension methods for registering the Agency permission layer with
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class PermissionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPermissionEvaluator"/> as a singleton, bound from the
    /// <paramref name="config"/> section named <paramref name="sectionName"/>.
    /// Malformed rules in the configuration section cause a fail-fast
    /// <see cref="InvalidOperationException"/> at first resolution.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="config">Application configuration containing the permissions section.</param>
    /// <param name="sectionName">
    /// Name of the configuration section to bind; defaults to <c>"Permissions"</c>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddAgencyPermissions(
        this IServiceCollection services, IConfiguration config, string sectionName = "Permissions")
    {
        services.AddOptions<PermissionsOptions>().Bind(config.GetSection(sectionName));
        services.AddSingleton<IPermissionEvaluator>(sp =>
        {
            PermissionsOptions options = sp.GetRequiredService<IOptions<PermissionsOptions>>().Value;
            PermissionsOptionsValidator.Validate(options);
            return new PermissionEvaluator(options, sp.GetService<ILogger<PermissionEvaluator>>());
        });
        return services;
    }
}
