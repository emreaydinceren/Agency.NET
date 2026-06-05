using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Sql.Postgres;

/// <summary>
/// Extension methods for registering the PostgreSQL-backed Agency memory store
/// and its supporting repositories with <see cref="IServiceCollection"/>.
/// </summary>
public static class PostgresMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL-backed memory store and its repositories: an
    /// <see cref="NpgsqlDataSource"/> (with pgvector enabled),
    /// <see cref="WatermarkRepository"/>, <see cref="DeadLetterRepository"/>,
    /// <see cref="IMemoryStore"/> → <see cref="PostgresMemoryStore"/>, and
    /// <see cref="MemorySchemaInitializer"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IEmbeddingGenerator"/> and <see cref="IOptions{TOptions}"/> of
    /// <see cref="MemoryOptions"/> must be registered separately (e.g. via
    /// <c>AddAgencyEmbeddingsOpenAI</c> and <c>AddAgencyMemory</c>).
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="connectionString">
    /// A valid Npgsql connection string pointing at the target PostgreSQL database.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyMemoryPostgres(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();
            return builder.Build();
        });

        services.AddSingleton<WatermarkRepository>();
        services.AddSingleton<DeadLetterRepository>();
        services.AddSingleton<MemorySchemaInitializer>();

        // Provider-neutral abstractions so the Distiller and host can resolve storage without
        // referencing this concrete provider (enables config-driven provider selection).
        services.AddSingleton<IWatermarkStore>(sp => sp.GetRequiredService<WatermarkRepository>());
        services.AddSingleton<IDeadLetterStore>(sp => sp.GetRequiredService<DeadLetterRepository>());
        services.AddSingleton<IMemorySchemaInitializer>(sp => sp.GetRequiredService<MemorySchemaInitializer>());

        services.AddSingleton<IMemoryStore>(sp =>
            new PostgresMemoryStore(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IEmbeddingGenerator>(),
                sp.GetRequiredService<IOptions<MemoryOptions>>(),
                sp.GetRequiredService<ILogger<PostgresMemoryStore>>()));

        return services;
    }
}
