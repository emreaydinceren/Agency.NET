using Agency.Embeddings.Common;
using Agency.Memory.Common.Storage;
using Agency.Memory.Common.Test;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Runs the <see cref="IMemoryStoreContractTests"/> abstract contract suite against
/// the PostgreSQL-backed <see cref="PostgresMemoryStore"/>.
/// All tests are functional and require a running PostgreSQL instance.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresMemoryStoreContractTests : IMemoryStoreContractTests, IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;

    /// <summary>Initialises the data source and schema before tests run.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<PostgresMemoryStoreContractTests>();

        // Contract tests use 2-element embedding arrays. We must recreate the schema with dim=2.
        // TestHelpers.ResetSchemaAsync drops the records table first so no mismatch occurs.
        await TestHelpers.ResetSchemaAsync(this._dataSource, 2, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

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
                float norm = MathF.Sqrt(arr[0] * arr[0] + arr[1] * arr[1]);
                if (norm > 0)
                {
                    arr[0] /= norm;
                    arr[1] /= norm;
                }

                return Task.FromResult((ReadOnlyMemory<float>)arr.AsMemory());
            });

        var options = Options.Create(new Agency.Memory.Common.Options.MemoryOptions());
        IMemoryStore store = new PostgresMemoryStore(
            this._dataSource, mock.Object, options, NullLogger<PostgresMemoryStore>.Instance);

        return Task.FromResult(store);
    }

}
