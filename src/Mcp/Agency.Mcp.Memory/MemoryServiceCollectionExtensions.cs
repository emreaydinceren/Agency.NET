using Agency.KeyValueStore.Common;
using Agency.KeyValueStore.Sql.Postgre;
using Agency.KeyValueStore.Sql.Sqlite;
using Agency.Sql.Postgre;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Mcp.Memory;

/// <summary>
/// Extension methods for registering the memory KV store and supporting services.
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IKVStore"/> implementation chosen by <see cref="MemoryOptions.Provider"/>
    /// and a hosted service that initializes the store schema on startup.
    /// </summary>
    public static IServiceCollection AddKVStore(this IServiceCollection services)
    {
        services.AddSingleton<PostgreKVStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            var runner = ActivatorUtilities.CreateInstance<PostgreSqlRunner>(sp, opts.ConnectionString);
            return ActivatorUtilities.CreateInstance<PostgreKVStore>(sp, runner);
        });

        services.AddSingleton<SqliteKVStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            var runner = new SqliteRunner(opts.ConnectionString);
            return ActivatorUtilities.CreateInstance<SqliteKVStore>(sp, runner);
        });

        services.AddSingleton<IKVStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            return opts.Provider.ToLowerInvariant() switch
            {
                "postgres" => sp.GetRequiredService<PostgreKVStore>(),
                "sqlite" => sp.GetRequiredService<SqliteKVStore>(),
                _ => throw new InvalidOperationException($"Unsupported provider: {opts.Provider}")
            };
        });

        services.AddHostedService<MemorySchemaInitializer>();

        return services;
    }
}
