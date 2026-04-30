using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests repository checkpoint behavior for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Repo_Tests
{
    [Fact]
    public async Task UpsertRepoAsync_InsertsAndUpdatesRepository()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        var repoId = Guid.NewGuid();

        await fixture.Store.UpsertRepoAsync(new Repo
        {
            Id = repoId,
            LocalPath = @"E:\Repos\Agency",
            RemoteUrl = "https://example.test/original.git",
            IsShallow = true,
            IndexedCommit = null,
            IndexedAt = null,
        }, cancellationToken);

        await fixture.Store.UpsertRepoAsync(new Repo
        {
            Id = repoId,
            LocalPath = @"E:\Repos\Agency.Renamed",
            RemoteUrl = "https://example.test/updated.git",
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT root_path, remote_url, is_shallow
            FROM repos
            WHERE id = $id;
            """,
            reader => new
            {
                RootPath = reader.GetString(0),
                RemoteUrl = reader.GetString(1),
                IsShallow = reader.GetInt64(2),
            },
            new Dictionary<string, object?> { ["$id"] = repoId.ToString("D") });

        Assert.Equal(@"E:\Repos\Agency.Renamed", row.RootPath);
        Assert.Equal("https://example.test/updated.git", row.RemoteUrl);
        Assert.Equal(0L, row.IsShallow);
    }

    [Fact]
    public async Task LoadIndexedCommitAsync_ReturnsNull_WhenRepositoryHasNoCheckpoint()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        var repoId = Guid.NewGuid();

        await fixture.Store.UpsertRepoAsync(new Repo
        {
            Id = repoId,
            LocalPath = @"E:\Repos\Agency",
            RemoteUrl = "https://example.test/repo.git",
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
        }, cancellationToken);

        string? commit = await fixture.Store.LoadIndexedCommitAsync(repoId, cancellationToken);

        Assert.Null(commit);
    }

    [Fact]
    public async Task SetIndexedCommitAsync_PersistsCheckpointAndIndexedAt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        var repoId = Guid.NewGuid();

        await fixture.Store.UpsertRepoAsync(new Repo
        {
            Id = repoId,
            LocalPath = @"E:\Repos\Agency",
            RemoteUrl = "https://example.test/repo.git",
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
        }, cancellationToken);

        await fixture.Store.SetIndexedCommitAsync(repoId, "abc123", cancellationToken);

        string? commit = await fixture.Store.LoadIndexedCommitAsync(repoId, cancellationToken);
        var row = await fixture.QuerySingleAsync(
            """
            SELECT indexed_commit, indexed_at
            FROM repos
            WHERE id = $id;
            """,
            reader => new
            {
                IndexedCommit = reader.IsDBNull(0) ? null : reader.GetString(0),
                IndexedAt = reader.IsDBNull(1) ? null : reader.GetString(1),
            },
            new Dictionary<string, object?> { ["$id"] = repoId.ToString("D") });

        Assert.Equal("abc123", commit);
        Assert.Equal("abc123", row.IndexedCommit);
        Assert.False(string.IsNullOrWhiteSpace(row.IndexedAt));
    }
}
