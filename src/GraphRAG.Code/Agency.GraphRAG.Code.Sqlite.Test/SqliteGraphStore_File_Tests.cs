using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests file persistence and mutation behavior for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_File_Tests
{
    [Fact]
    public async Task UpsertFileAsync_InsertsAndUpdatesFile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        (Guid repoId, Guid projectId) = await InsertProjectGraphAsync(fixture);
        var fileId = Guid.NewGuid();

        await fixture.Store.UpsertFileAsync(new SourceFile
        {
            Id = fileId,
            RepoId = repoId,
            ProjectId = projectId,
            Path = @"src\GraphRAG.Code\Parser.cs",
            Language = "csharp",
            ContentHash = "hash-1",
        }, cancellationToken);

        await fixture.Store.UpsertFileAsync(new SourceFile
        {
            Id = fileId,
            RepoId = repoId,
            ProjectId = projectId,
            Path = @"src\GraphRAG.Code\Parser.Renamed.cs",
            Language = "fsharp",
            ContentHash = "hash-2",
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT repo_id, project_id, path, language, content_hash
            FROM files
            WHERE id = $id;
            """,
            reader => new
            {
                RepoId = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Path = reader.GetString(2),
                Language = reader.GetString(3),
                ContentHash = reader.IsDBNull(4) ? null : reader.GetString(4),
            },
            new Dictionary<string, object?> { ["$id"] = fileId.ToString("D") });

        Assert.Equal(repoId.ToString("D"), row.RepoId);
        Assert.Equal(projectId.ToString("D"), row.ProjectId);
        Assert.Equal(@"src\GraphRAG.Code\Parser.Renamed.cs", row.Path);
        Assert.Equal("fsharp", row.Language);
        Assert.Equal("hash-2", row.ContentHash);
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesOwnedSymbolsAndIncidentEdges()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        (_, Guid projectId) = await InsertProjectGraphAsync(fixture);
        Guid deletedFileId = Guid.NewGuid();
        Guid retainedFileId = Guid.NewGuid();
        Guid deletedSymbolId = Guid.NewGuid();
        Guid retainedSymbolId = Guid.NewGuid();
        Guid deletedEdgeId = Guid.NewGuid();
        Guid retainedEdgeId = Guid.NewGuid();

        await InsertFileAsync(fixture, deletedFileId, projectId, @"src\DeleteMe.cs");
        await InsertFileAsync(fixture, retainedFileId, projectId, @"src\KeepMe.cs");
        await InsertSymbolAsync(fixture, deletedSymbolId, deletedFileId, "DeleteMe");
        await InsertSymbolAsync(fixture, retainedSymbolId, retainedFileId, "KeepMe");

        await fixture.ExecuteAsync(
            """
            INSERT INTO edges (id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
            VALUES
                ($deletedEdgeId, $deletedSymbolId, 'symbol', $retainedSymbolId, 'symbol', 'References', 0.95, '["NameMatch"]', '{}'),
                ($retainedEdgeId, $retainedSymbolId, 'symbol', $retainedSymbolId, 'symbol', 'References', 0.10, '["Unresolved"]', '{}');
            """,
            new Dictionary<string, object?>
            {
                ["$deletedEdgeId"] = deletedEdgeId.ToString("D"),
                ["$deletedSymbolId"] = deletedSymbolId.ToString("D"),
                ["$retainedSymbolId"] = retainedSymbolId.ToString("D"),
                ["$retainedEdgeId"] = retainedEdgeId.ToString("D"),
            });

        await fixture.Store.DeleteFileAsync(deletedFileId, cancellationToken);

        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM files WHERE id = $id;", new Dictionary<string, object?> { ["$id"] = deletedFileId.ToString("D") }));
        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM symbols WHERE file_id = $id;", new Dictionary<string, object?> { ["$id"] = deletedFileId.ToString("D") }));
        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE id = $id;", new Dictionary<string, object?> { ["$id"] = deletedEdgeId.ToString("D") }));
        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM files WHERE id = $id;", new Dictionary<string, object?> { ["$id"] = retainedFileId.ToString("D") }));
        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE id = $id;", new Dictionary<string, object?> { ["$id"] = retainedEdgeId.ToString("D") }));
    }

    [Fact]
    public async Task RenameFileAsync_UpdatesPathWithoutChangingOwnedSymbolsOrEdges()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        (_, Guid projectId) = await InsertProjectGraphAsync(fixture);
        Guid fileId = Guid.NewGuid();
        Guid symbolId = Guid.NewGuid();
        Guid externalFileId = Guid.NewGuid();
        Guid externalSymbolId = Guid.NewGuid();
        Guid edgeId = Guid.NewGuid();

        await InsertFileAsync(fixture, fileId, projectId, @"src\OldName.cs");
        await InsertFileAsync(fixture, externalFileId, projectId, @"src\Dependency.cs");
        await InsertSymbolAsync(fixture, symbolId, fileId, "RenamedFileSymbol");
        await InsertSymbolAsync(fixture, externalSymbolId, externalFileId, "DependencySymbol");

        await fixture.ExecuteAsync(
            """
            INSERT INTO edges (id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
            VALUES ($id, $sourceId, 'symbol', $targetId, 'symbol', 'References', 1.0, '["NameMatch"]', '{"kind":"primary"}');
            """,
            new Dictionary<string, object?>
            {
                ["$id"] = edgeId.ToString("D"),
                ["$sourceId"] = symbolId.ToString("D"),
                ["$targetId"] = externalSymbolId.ToString("D"),
            });

        await fixture.Store.RenameFileAsync(fileId, @"src\NewName.cs", cancellationToken);

        var fileRow = await fixture.QuerySingleAsync(
            "SELECT path FROM files WHERE id = $id;",
            reader => reader.GetString(0),
            new Dictionary<string, object?> { ["$id"] = fileId.ToString("D") });

        var symbolRow = await fixture.QuerySingleAsync(
            "SELECT id, file_id FROM symbols WHERE id = $id;",
            reader => new
            {
                Id = reader.GetString(0),
                FileId = reader.GetString(1),
            },
            new Dictionary<string, object?> { ["$id"] = symbolId.ToString("D") });

        var edgeRow = await fixture.QuerySingleAsync(
            "SELECT id, source_id, target_id FROM edges WHERE id = $id;",
            reader => new
            {
                Id = reader.GetString(0),
                SourceId = reader.GetString(1),
                TargetId = reader.GetString(2),
            },
            new Dictionary<string, object?> { ["$id"] = edgeId.ToString("D") });

        Assert.Equal(@"src\NewName.cs", fileRow);
        Assert.Equal(symbolId.ToString("D"), symbolRow.Id);
        Assert.Equal(fileId.ToString("D"), symbolRow.FileId);
        Assert.Equal(edgeId.ToString("D"), edgeRow.Id);
        Assert.Equal(symbolId.ToString("D"), edgeRow.SourceId);
        Assert.Equal(externalSymbolId.ToString("D"), edgeRow.TargetId);
    }

    private static async Task<(Guid RepoId, Guid ProjectId)> InsertProjectGraphAsync(SqliteGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES ($repoId, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, 0);

            INSERT INTO projects (id, repo_id, name, relative_path, manifest_path, language, ecosystem)
            VALUES ($projectId, $repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'csharp', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["$repoId"] = repoId.ToString("D"),
                ["$projectId"] = projectId.ToString("D"),
            });

        return (repoId, projectId);
    }

    private static Task InsertFileAsync(SqliteGraphStoreFixture fixture, Guid fileId, Guid projectId, string path)
        => fixture.ExecuteAsync(
            """
            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            SELECT $fileId, repo_id, $projectId, $path, 'csharp', 'hash', NULL
            FROM projects
            WHERE id = $projectId;
            """,
            new Dictionary<string, object?>
            {
                ["$fileId"] = fileId.ToString("D"),
                ["$projectId"] = projectId.ToString("D"),
                ["$path"] = path,
            });

    /// <summary>Tests that GetFileByPathAsync returns the correct file when path matches.</summary>
    [Fact]
    public async Task GetFileByPathAsync_ReturnsFileWhenPathMatches()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        (Guid repoId, Guid projectId) = await InsertProjectGraphAsync(fixture);
        var fileId = Guid.NewGuid();
        string filePath = @"src\GraphRAG.Code\Parser.cs";

        await InsertFileAsync(fixture, fileId, projectId, filePath);

        var result = await fixture.Store.GetFileByPathAsync(filePath, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(fileId, result.Id);
        Assert.Equal(filePath, result.Path);
        Assert.Equal(projectId, result.ProjectId);
    }

    /// <summary>Tests that GetFileByPathAsync returns null when path does not exist.</summary>
    [Fact]
    public async Task GetFileByPathAsync_ReturnsNullWhenPathDoesNotExist()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();

        var result = await fixture.Store.GetFileByPathAsync(@"src\NonExistent.cs", cancellationToken);

        Assert.Null(result);
    }

    private static Task InsertSymbolAsync(SqliteGraphStoreFixture fixture, Guid symbolId, Guid fileId, string name)
        => fixture.ExecuteAsync(
            """
            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES ($id, $fileId, NULL, $name, NULL, 'Class', NULL, NULL, NULL, NULL, 'symbol-hash', 0, 1, 10);
            """,
            new Dictionary<string, object?>
            {
                ["$id"] = symbolId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
                ["$name"] = name,
            });
}
