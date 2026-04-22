using Agency.VectorStore.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agency.Mcp.Memory;

/// <summary>
/// Hosted service that initializes the backing store schema before the MCP server starts accepting requests.
/// Runs synchronously inside <see cref="StartAsync"/> so the schema is guaranteed to exist before the first tool call.
/// </summary>
internal sealed class MemorySchemaInitializer(IServiceProvider services, IOptions<MemoryOptions> options) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(this.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var store = services.GetRequiredService<SqliteKVStore>();
            await store.InitializeSchemaAsync(cancellationToken: cancellationToken);
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string Provider => options.Value.Provider;
}
