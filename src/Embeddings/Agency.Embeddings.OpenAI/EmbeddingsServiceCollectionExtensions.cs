using Agency.Embeddings.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Embeddings.OpenAI;

/// <summary>
/// Extension methods for registering the OpenAI-compatible embedding generator
/// with <see cref="IServiceCollection"/>.
/// </summary>
public static class EmbeddingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmbeddingGenerator"/> as a singleton backed by
    /// <see cref="EmbeddingGenerator"/>, configured via <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">
    /// An action that populates an <see cref="EmbeddingOptions"/> instance with the
    /// desired base URL, API key, model identifier, and optional dimensions.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyEmbeddingsOpenAI(
        this IServiceCollection services,
        Action<EmbeddingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IEmbeddingGenerator>(_ =>
        {
            var options = new EmbeddingOptions();
            configure(options);
            return new EmbeddingGenerator(options);
        });

        return services;
    }
}
