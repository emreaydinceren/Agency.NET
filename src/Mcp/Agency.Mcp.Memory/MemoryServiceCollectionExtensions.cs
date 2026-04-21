using Agency.Sql.Postgre;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Postgre;
using Agency.VectorStore.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Mcp.Memory;

public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddKVStore(this IServiceCollection services)
    {
        services.AddSingleton<PostgreKVStore>(sp => {
            var options = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            var runner = ActivatorUtilities.CreateInstance<PostgreSqlRunner>(sp, options.ConnectionString);
            return ActivatorUtilities.CreateInstance<PostgreKVStore>(sp, runner);
        });

        services.AddSingleton<SqliteKVStore>(sp => {
            var options = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            var runner = ActivatorUtilities.CreateInstance<SqliteRunner>(sp, options.ConnectionString);
            return ActivatorUtilities.CreateInstance<SqliteKVStore>(sp, runner);
        });

        // Now register the interface based on config
        services.AddSingleton<IKVStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;

            return options.Provider.ToLower() switch
            {
                "postgres" => sp.GetRequiredService<PostgreKVStore>(),
                "sqlite" => sp.GetRequiredService<SqliteKVStore>(),
                _ => throw new InvalidOperationException($"Unsupported provider: {options.Provider}")
            };
        });
        return services;
    }
}

