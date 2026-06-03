using Agency.Memory.Distiller.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Memory.Distiller;

/// <summary>
/// Extension methods for registering the distiller's LLM adapter with
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class DistillerLlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the distiller LLM adapter wrapping the given chat client and model identifier.
    /// </summary>
    /// <remarks>
    /// Use this overload from external assemblies that cannot reference the internal
    /// <see cref="ILlmClientAdapter"/> type directly.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="client">The <see cref="IChatClient"/> the adapter will wrap.</param>
    /// <param name="model">The model identifier sent with every distillation request.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyDistillerLlm(
        this IServiceCollection services,
        IChatClient client,
        string model)
        => services.AddSingleton<ILlmClientAdapter>(_ => new Services.ChatClientLlmAdapter(client, model));

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
