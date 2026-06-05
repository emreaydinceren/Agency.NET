using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Agency.Memory.Common.Test;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Agency.Memory.Sql.Sqlite.Test;

/// <summary>
/// Runs the <see cref="IMemoryStoreContractTests"/> abstract contract suite against
/// the SQLite-backed <see cref="SqliteMemoryStore"/>.
/// Each test gets its own isolated in-memory database — no external infrastructure required.
/// </summary>
public sealed class SqliteMemoryStoreContractTests : IMemoryStoreContractTests, IAsyncLifetime
{
    private string _connectionString = default!;
    private SqliteConnection _keepAlive = default!;

    /// <summary>Initialises an isolated in-memory DB and provisions the schema.</summary>
    public async ValueTask InitializeAsync()
    {
        this._connectionString = TestHelpers.BuildConnectionString();
        this._keepAlive = TestHelpers.OpenKeepAlive(this._connectionString);

        // Contract tests supply 2-element embedding arrays
        await new MemorySchemaInitializer(this._connectionString).InitializeAsync(2, TestContext.Current.CancellationToken);
    }

    /// <summary>Closes the keep-alive connection, destroying the in-memory DB.</summary>
    public async ValueTask DisposeAsync()
    {
        await this._keepAlive.CloseAsync();
        this._keepAlive.Dispose();
    }

    /// <inheritdoc/>
    protected override Task<IMemoryStore> CreateStoreAsync()
    {
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((input, _) =>
            {
                // Deterministic 2-dimension embedding sufficient for contract tests
                var rng = new Random(input.GetHashCode());
                float[] arr = [((float)rng.NextDouble() * 2) - 1, ((float)rng.NextDouble() * 2) - 1];
                float norm = MathF.Sqrt((arr[0] * arr[0]) + (arr[1] * arr[1]));
                if (norm > 0)
                {
                    arr[0] /= norm;
                    arr[1] /= norm;
                }

                return Task.FromResult((ReadOnlyMemory<float>)arr.AsMemory());
            });

        var options = Options.Create(new MemoryOptions());
        IMemoryStore store = new SqliteMemoryStore(
            this._connectionString,
            mock.Object,
            options,
            NullLogger<SqliteMemoryStore>.Instance);

        return Task.FromResult(store);
    }
}