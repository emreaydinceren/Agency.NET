using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.E2E.Test;

/// <summary>
/// End-to-end SQLite indexing tests against the Agency repository.
/// </summary>
[Trait("Category", "Functional")]
public sealed class SqliteFullIndexTests : IAsyncLifetime
{
    private SqliteHarness? _harness;
    private Repo? _repo;

    public async ValueTask InitializeAsync()
    {
        this._harness = E2ETestInfrastructure.CreateSqliteHarness();
        this._repo = new Repo
        {
            Id = Guid.Parse("a6260d5f-2f6f-4312-b40f-9d2d1d4535f8"),
            LocalPath = E2ETestInfrastructure.RepoRoot,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };

        AgencyRepoIndexer indexer = new(this._harness.Store, this._harness.EmbeddingGenerator);
        IndexArtifacts artifacts = await indexer.IndexAsync(this._repo, TestContext.Current.CancellationToken);
        this._repo = this._repo with { IndexedCommit = artifacts.IndexedCommit };
    }

    public async ValueTask DisposeAsync()
    {
        if (this._harness is not null)
        {
            await this._harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task FullIndex_PopulatesExpectedAgencySymbolsAndCheckpoint()
    {
        Assert.NotNull(this._harness);
        Assert.NotNull(this._repo);

        IReadOnlyList<long> counts = await this._harness!.Runner.QueryAsync(
            "SELECT COUNT(*) FROM symbols;",
            reader => Task.FromResult(reader.GetInt64(0)),
            cancellationToken: TestContext.Current.CancellationToken);
        long symbolCount = Assert.Single(counts);
        Assert.InRange(symbolCount, 200, 50000);

        foreach (string symbolName in AgencyRepoExpectations.ChatAgentSymbols.Concat(AgencyRepoExpectations.LlmClientSymbols))
        {
            IReadOnlyList<Symbol> matches = await this._harness.Store.FindSymbolsByNameAsync(symbolName, TestContext.Current.CancellationToken);
            Assert.NotEmpty(matches);
        }

        string? indexedCommit = await this._harness.Store.LoadIndexedCommitAsync(this._repo!.Id, TestContext.Current.CancellationToken);
        Assert.Equal(E2ETestInfrastructure.GetHeadCommit(E2ETestInfrastructure.RepoRoot), indexedCommit);
    }
}
