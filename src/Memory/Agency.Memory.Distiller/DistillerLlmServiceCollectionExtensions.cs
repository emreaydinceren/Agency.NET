using Agency.Memory.Distiller.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Memory.Distiller;

/// <summary>
/// Extension methods for registering the distiller's LLM adapter with
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// This class is <c>internal</c> because <see cref="ILlmClientAdapter"/> is an internal
/// interface. Callers outside this assembly must be granted access via
/// <c>[assembly: InternalsVisibleTo(...)]</c>.
/// </remarks>
internal static class DistillerLlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the distiller's <see cref="ILlmClientAdapter"/> as a singleton using
    /// a host-supplied factory delegate.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for constructing the concrete adapter (e.g.
    /// <c>ChatClientLlmAdapter</c>) inside <paramref name="factory"/>. Keeping this
    /// helper generic avoids a direct reference to the concrete adapter type from
    /// outside the project.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="factory">
    /// A factory that receives the <see cref="IServiceProvider"/> and returns an
    /// <see cref="ILlmClientAdapter"/> instance.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    internal static IServiceCollection AddAgencyDistillerLlm(
        this IServiceCollection services,
        Func<IServiceProvider, ILlmClientAdapter> factory)
        => services.AddSingleton(factory);
}
