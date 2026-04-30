using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests project and external-package persistence for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Project_Tests
{
    [Fact]
    public async Task UpsertProjectAsync_InsertsAndUpdatesProject()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid repoId = await InsertRepoAsync(fixture);
        var projectId = Guid.NewGuid();

        await fixture.Store.UpsertProjectAsync(new Project
        {
            Id = projectId,
            RepoId = repoId,
            Name = "Agency.GraphRAG.Code",
            RelativePath = @"src\GraphRAG.Code\Agency.GraphRAG.Code",
            ManifestPath = @"src\GraphRAG.Code\Agency.GraphRAG.Code\Agency.GraphRAG.Code.csproj",
            Language = "csharp",
        }, cancellationToken);

        await fixture.Store.UpsertProjectAsync(new Project
        {
            Id = projectId,
            RepoId = repoId,
            Name = "Agency.GraphRAG.Code.Updated",
            RelativePath = @"src\GraphRAG.Code\Agency.GraphRAG.Code",
            ManifestPath = @"src\GraphRAG.Code\Updated.csproj",
            Language = "fsharp",
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT repo_id, name, relative_path, manifest_path, language
            FROM projects
            WHERE id = $id;
            """,
            reader => new
            {
                RepoId = reader.GetString(0),
                Name = reader.GetString(1),
                RelativePath = reader.GetString(2),
                ManifestPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                Language = reader.GetString(4),
            },
            new Dictionary<string, object?> { ["$id"] = projectId.ToString("D") });

        Assert.Equal(repoId.ToString("D"), row.RepoId);
        Assert.Equal("Agency.GraphRAG.Code.Updated", row.Name);
        Assert.Equal(@"src\GraphRAG.Code\Agency.GraphRAG.Code", row.RelativePath);
        Assert.Equal(@"src\GraphRAG.Code\Updated.csproj", row.ManifestPath);
        Assert.Equal("fsharp", row.Language);
    }

    [Fact]
    public async Task UpsertExternalPackageBatchAsync_InsertsNewRowsAndUpdatesExistingRows()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid repoId = await InsertRepoAsync(fixture);
        Guid projectId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO projects (id, repo_id, name, relative_path, manifest_path, language, ecosystem)
            VALUES ($id, $repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'csharp', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["$id"] = projectId.ToString("D"),
                ["$repoId"] = repoId.ToString("D"),
            });

        Guid existingPackageId = Guid.NewGuid();
        Guid newPackageId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO external_packages (id, project_id, name, version, version_resolved, ecosystem, scope)
            VALUES ($id, $projectId, 'Dapper', '2.0.0', NULL, 'nuget', 'runtime');
            """,
            new Dictionary<string, object?>
            {
                ["$id"] = existingPackageId.ToString("D"),
                ["$projectId"] = projectId.ToString("D"),
            });

        await fixture.Store.UpsertExternalPackageBatchAsync(
        [
            new ExternalPackage
            {
                Id = existingPackageId,
                ProjectId = projectId,
                Name = "Dapper",
                Version = "2.1.0",
                Ecosystem = "nuget",
                Scope = "dev",
            },
            new ExternalPackage
            {
                Id = newPackageId,
                ProjectId = projectId,
                Name = "Microsoft.Data.Sqlite",
                Version = "9.0.0",
                Ecosystem = "nuget",
                Scope = "runtime",
            },
        ],
        cancellationToken);

        long rowCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM external_packages WHERE project_id = $projectId;",
            new Dictionary<string, object?> { ["$projectId"] = projectId.ToString("D") });

        var updatedRow = await fixture.QuerySingleAsync(
            """
            SELECT name, version, ecosystem, scope
            FROM external_packages
            WHERE id = $id;
            """,
            reader => new
            {
                Name = reader.GetString(0),
                Version = reader.IsDBNull(1) ? null : reader.GetString(1),
                Ecosystem = reader.GetString(2),
                Scope = reader.GetString(3),
            },
            new Dictionary<string, object?> { ["$id"] = existingPackageId.ToString("D") });

        var insertedRow = await fixture.QuerySingleAsync(
            """
            SELECT name, version, ecosystem, scope
            FROM external_packages
            WHERE id = $id;
            """,
            reader => new
            {
                Name = reader.GetString(0),
                Version = reader.IsDBNull(1) ? null : reader.GetString(1),
                Ecosystem = reader.GetString(2),
                Scope = reader.GetString(3),
            },
            new Dictionary<string, object?> { ["$id"] = newPackageId.ToString("D") });

        Assert.Equal(2L, rowCount);
        Assert.Equal("Dapper", updatedRow.Name);
        Assert.Equal("2.1.0", updatedRow.Version);
        Assert.Equal("nuget", updatedRow.Ecosystem);
        Assert.Equal("dev", updatedRow.Scope);
        Assert.Equal("Microsoft.Data.Sqlite", insertedRow.Name);
        Assert.Equal("9.0.0", insertedRow.Version);
        Assert.Equal("nuget", insertedRow.Ecosystem);
        Assert.Equal("runtime", insertedRow.Scope);
    }

    private static async Task<Guid> InsertRepoAsync(SqliteGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES ($id, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, 0);
            """,
            new Dictionary<string, object?> { ["$id"] = repoId.ToString("D") });
        return repoId;
    }
}
