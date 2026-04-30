using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.E2E.Test;

/// <summary>
/// End-to-end incremental SQLite indexing tests against a git worktree of the Agency repository.
/// </summary>
[Trait("Category", "Functional")]
public sealed class SqliteIncrementalTests : IAsyncDisposable
{
    private readonly SqliteHarness _harness = E2ETestInfrastructure.CreateSqliteHarness();
    private string? _worktreePath;

    [Fact]
    public async Task IncrementalReindex_OnlyChangesTouchedFileSymbols()
    {
        this._worktreePath = E2ETestInfrastructure.CreateWorktree(E2ETestInfrastructure.RepoRoot);
        Repo repo = new()
        {
            Id = Guid.Parse("b4f48cff-e6ca-4586-bf57-7c8cd97dfd41"),
            LocalPath = this._worktreePath,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };

        AgencyRepoIndexer indexer = new(this._harness.Store, this._harness.EmbeddingGenerator);
        IndexArtifacts firstIndex = await indexer.IndexAsync(repo, TestContext.Current.CancellationToken);
        repo = repo with { IndexedCommit = firstIndex.IndexedCommit };

        Dictionary<Guid, (string Path, string Hash, string Embedding)> before = await LoadSnapshotAsync();
        E2ETestInfrastructure.TouchChatSessionForIncrementalTest(this._worktreePath);
        IndexArtifacts secondIndex = await indexer.IndexAsync(repo, TestContext.Current.CancellationToken);
        repo = repo with { IndexedCommit = secondIndex.IndexedCommit };
        Dictionary<Guid, (string Path, string Hash, string Embedding)> after = await LoadSnapshotAsync();

        HashSet<Guid> changedSymbolIds = before.Keys
            .Intersect(after.Keys)
            .Where(id => before[id].Hash != after[id].Hash || before[id].Embedding != after[id].Embedding)
            .ToHashSet();

        Assert.NotEmpty(changedSymbolIds);
        Assert.All(changedSymbolIds, id => Assert.Contains("ChatSession.cs", after[id].Path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            changedSymbolIds,
            id => !after[id].Path.Contains("ChatSession.cs", StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask DisposeAsync()
    {
        if (this._worktreePath is not null)
        {
            E2ETestInfrastructure.RemoveWorktree(E2ETestInfrastructure.RepoRoot, this._worktreePath);
        }

        await this._harness.DisposeAsync();
    }

    private async Task<Dictionary<Guid, (string Path, string Hash, string Embedding)>> LoadSnapshotAsync()
    {
        IReadOnlyList<(string Path, Guid Id, string Hash, string Embedding)> rows = await this._harness.Runner.QueryAsync(
            E2ETestInfrastructure.ReadSymbolHashSnapshotSql,
            reader => Task.FromResult((
                reader.GetString(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3))),
            cancellationToken: TestContext.Current.CancellationToken);

        return rows.ToDictionary(row => row.Id, row => (row.Path, row.Hash, row.Embedding));
    }
}
