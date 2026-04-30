using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for repository persistence behavior in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_Repo_Tests
{
    [Fact]
    public async Task UpsertRepoAsync_IsIdempotentAndPersistsUpdatedValues()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid repoId = Guid.NewGuid();

        Repo initial = new()
        {
            Id = repoId,
            LocalPath = @"E:\Repos\Agency",
            RemoteUrl = "https://example.test/original.git",
            IsShallow = true,
            IndexedCommit = null,
            IndexedAt = null,
        };

        Repo updated = initial with
        {
            LocalPath = @"E:\Repos\Agency.Renamed",
            RemoteUrl = "https://example.test/updated.git",
            IsShallow = false,
        };

        await fixture.Store.UpsertRepoAsync(initial, cancellationToken);
        await fixture.Store.UpsertRepoAsync(updated, cancellationToken);
        await fixture.Store.UpsertRepoAsync(updated, cancellationToken);

        long rowCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM repos WHERE id = @id;",
            new Dictionary<string, object?> { ["id"] = repoId });

        var row = await fixture.QuerySingleAsync(
            """
            SELECT root_path, remote_url, is_shallow
            FROM repos
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                RootPath = Assert.IsType<string>(dataSet["root_path", rowIndex]),
                RemoteUrl = Assert.IsType<string>(dataSet["remote_url", rowIndex]),
                IsShallow = Assert.IsType<bool>(dataSet["is_shallow", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = repoId });

        Assert.Equal(1L, rowCount);
        Assert.Equal(updated.LocalPath, row.RootPath);
        Assert.Equal(updated.RemoteUrl, row.RemoteUrl);
        Assert.False(row.IsShallow);
    }

    [Fact]
    public async Task SetIndexedCommitAsync_RoundTripsCommitAndPopulatesIndexedAt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid repoId = Guid.NewGuid();

        await fixture.Store.UpsertRepoAsync(new Repo
        {
            Id = repoId,
            LocalPath = @"E:\Repos\Agency",
            RemoteUrl = "https://example.test/repo.git",
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
        }, cancellationToken);

        Assert.Null(await fixture.Store.LoadIndexedCommitAsync(repoId, cancellationToken));

        await fixture.Store.SetIndexedCommitAsync(repoId, "abc123def", cancellationToken);

        string? indexedCommit = await fixture.Store.LoadIndexedCommitAsync(repoId, cancellationToken);
        var row = await fixture.QuerySingleAsync(
            """
            SELECT indexed_commit, indexed_at IS NOT NULL AS has_indexed_at
            FROM repos
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                IndexedCommit = dataSet["indexed_commit", rowIndex] as string,
                HasIndexedAt = Assert.IsType<bool>(dataSet["has_indexed_at", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = repoId });

        Assert.Equal("abc123def", indexedCommit);
        Assert.Equal("abc123def", row.IndexedCommit);
        Assert.True(row.HasIndexedAt);
    }
}
