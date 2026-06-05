using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Sql.Sqlite;

/// <summary>
/// Extension methods for registering the SQLite-backed Agency memory store
/// and its supporting repositories with <see cref="IServiceCollection"/>.
/// </summary>
public static class SqliteMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite-backed memory store and its repositories:
    /// <see cref="SqliteWatermarkRepository"/>, <see cref="SqliteDeadLetterRepository"/>,
    /// <see cref="MemorySchemaInitializer"/>, and
    /// <see cref="IMemoryStore"/> → <see cref="SqliteMemoryStore"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IEmbeddingGenerator"/> and <see cref="IOptions{TOptions}"/> of
    /// <see cref="MemoryOptions"/> must be registered separately (e.g. via
    /// <c>AddAgencyEmbeddingsOpenAI</c> and <c>AddAgencyMemory</c>).
    /// The SQLite database file is created automatically on first access.
    /// No external server is required.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="connectionString">
    /// A valid SQLite connection string, e.g. <c>Data Source=memory.db</c>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyMemorySqlite(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.AddSingleton(_ => new SqliteWatermarkRepository(connectionString));
        services.AddSingleton(_ => new SqliteDeadLetterRepository(connectionString));
        services.AddSingleton(_ => new MemorySchemaInitializer(connectionString));

        // Provider-neutral abstractions so the Distiller and host can resolve storage without
        // referencing this concrete provider (enables config-driven provider selection).
        services.AddSingleton<IWatermarkStore>(sp => sp.GetRequiredService<SqliteWatermarkRepository>());
        services.AddSingleton<IDeadLetterStore>(sp => sp.GetRequiredService<SqliteDeadLetterRepository>());
        services.AddSingleton<IMemorySchemaInitializer>(sp => sp.GetRequiredService<MemorySchemaInitializer>());

        services.AddSingleton<IMemoryStore>(sp =>
            new SqliteMemoryStore(
                connectionString,
                sp.GetRequiredService<IEmbeddingGenerator>(),
                sp.GetRequiredService<IOptions<MemoryOptions>>(),
                sp.GetRequiredService<ILogger<SqliteMemoryStore>>()));

        return services;
    }
}