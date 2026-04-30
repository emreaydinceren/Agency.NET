using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for project and external-package persistence in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_Project_Tests
{
    [Fact]
    public async Task UpsertProjectAsync_InsertsAndUpdatesProject()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid repoId = await InsertRepoAsync(fixture);
        Guid projectId = Guid.NewGuid();

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
            SELECT repo_id, name, path, manifest_path, language
            FROM projects
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                RepoId = Assert.IsType<Guid>(dataSet["repo_id", rowIndex]),
                Name = Assert.IsType<string>(dataSet["name", rowIndex]),
                Path = Assert.IsType<string>(dataSet["path", rowIndex]),
                ManifestPath = dataSet["manifest_path", rowIndex] as string,
                Language = Assert.IsType<string>(dataSet["language", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = projectId });

        Assert.Equal(repoId, row.RepoId);
        Assert.Equal("Agency.GraphRAG.Code.Updated", row.Name);
        Assert.Equal(@"src\GraphRAG.Code\Agency.GraphRAG.Code", row.Path);
        Assert.Equal(@"src\GraphRAG.Code\Updated.csproj", row.ManifestPath);
        Assert.Equal("fsharp", row.Language);
    }

    [Fact]
    public async Task UpsertExternalPackageBatchAsync_InsertsNewRowsAndUpdatesExistingRows()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid repoId = await InsertRepoAsync(fixture);
        Guid projectId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO projects (id, repo_id, name, manifest_path, path, language, ecosystem)
            VALUES (@id, @repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'src\GraphRAG.Code', 'csharp', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["id"] = projectId,
                ["repoId"] = repoId,
            });

        Guid existingPackageId = Guid.NewGuid();
        Guid newPackageId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO external_packages (id, project_id, name, version, version_resolved, ecosystem, scope)
            VALUES (@id, @projectId, 'Dapper', '2.0.0', NULL, 'nuget', 'runtime');
            """,
            new Dictionary<string, object?>
            {
                ["id"] = existingPackageId,
                ["projectId"] = projectId,
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
            "SELECT COUNT(*) FROM external_packages WHERE project_id = @projectId;",
            new Dictionary<string, object?> { ["projectId"] = projectId });

        var updatedRow = await fixture.QuerySingleAsync(
            """
            SELECT name, version, ecosystem, scope
            FROM external_packages
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                Name = Assert.IsType<string>(dataSet["name", rowIndex]),
                Version = dataSet["version", rowIndex] as string,
                Ecosystem = Assert.IsType<string>(dataSet["ecosystem", rowIndex]),
                Scope = Assert.IsType<string>(dataSet["scope", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = existingPackageId });

        var insertedRow = await fixture.QuerySingleAsync(
            """
            SELECT name, version, ecosystem, scope
            FROM external_packages
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                Name = Assert.IsType<string>(dataSet["name", rowIndex]),
                Version = dataSet["version", rowIndex] as string,
                Ecosystem = Assert.IsType<string>(dataSet["ecosystem", rowIndex]),
                Scope = Assert.IsType<string>(dataSet["scope", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = newPackageId });

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

    [Fact]
    public async Task UpsertExternalPackageBatchAsync_HandlesLargeBatchIdempotently()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid repoId = await InsertRepoAsync(fixture);
        Guid projectId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO projects (id, repo_id, name, manifest_path, path, language, ecosystem)
            VALUES (@id, @repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'src\GraphRAG.Code', 'csharp', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["id"] = projectId,
                ["repoId"] = repoId,
            });

        ExternalPackage[] batch = Enumerable.Range(0, 128)
            .Select(index => new ExternalPackage
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = $"Package.{index:D3}",
                Version = $"1.0.{index}",
                Ecosystem = "nuget",
                Scope = index % 2 == 0 ? "runtime" : "dev",
            })
            .ToArray();

        await fixture.Store.UpsertExternalPackageBatchAsync(batch, cancellationToken);
        await fixture.Store.UpsertExternalPackageBatchAsync(batch, cancellationToken);

        long rowCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM external_packages WHERE project_id = @projectId;",
            new Dictionary<string, object?> { ["projectId"] = projectId });

        var sample = await fixture.QuerySingleAsync(
            """
            SELECT name, version, ecosystem, scope
            FROM external_packages
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                Name = Assert.IsType<string>(dataSet["name", rowIndex]),
                Version = dataSet["version", rowIndex] as string,
                Ecosystem = Assert.IsType<string>(dataSet["ecosystem", rowIndex]),
                Scope = Assert.IsType<string>(dataSet["scope", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = batch[87].Id });

        Assert.Equal(batch.Length, rowCount);
        Assert.Equal(batch[87].Name, sample.Name);
        Assert.Equal(batch[87].Version, sample.Version);
        Assert.Equal(batch[87].Ecosystem, sample.Ecosystem);
        Assert.Equal(batch[87].Scope, sample.Scope);
    }

    private static async Task<Guid> InsertRepoAsync(PostgresGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES (@id, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, FALSE);
            """,
            new Dictionary<string, object?> { ["id"] = repoId });
        return repoId;
    }
}
