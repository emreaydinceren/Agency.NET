namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for file persistence and mutation behavior in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_File_Tests
{
    [Fact]
    public async Task UpsertFileAsync_InsertsAndUpdatesFile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        (Guid repoId, Guid projectId) = await InsertProjectGraphAsync(fixture);
        Guid fileId = Guid.NewGuid();

        await fixture.Store.UpsertFileAsync(new()
        {
            Id = fileId,
            RepoId = repoId,
            ProjectId = projectId,
            Path = @"src\GraphRAG.Code\Parser.cs",
            Language = "csharp",
            ContentHash = "hash-1",
        }, cancellationToken);

        await fixture.Store.UpsertFileAsync(new()
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
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                RepoId = Assert.IsType<Guid>(dataSet["repo_id", rowIndex]),
                ProjectId = Assert.IsType<Guid>(dataSet["project_id", rowIndex]),
                Path = Assert.IsType<string>(dataSet["path", rowIndex]),
                Language = Assert.IsType<string>(dataSet["language", rowIndex]),
                ContentHash = dataSet["content_hash", rowIndex] as string,
            },
            new Dictionary<string, object?> { ["id"] = fileId });

        Assert.Equal(repoId, row.RepoId);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal(@"src\GraphRAG.Code\Parser.Renamed.cs", row.Path);
        Assert.Equal("fsharp", row.Language);
        Assert.Equal("hash-2", row.ContentHash);
    }

    [Fact]
    public async Task RenameFileAsync_UpdatesPathWithoutChangingOwnedSymbolsOrEdges()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
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
            VALUES (@id, @sourceId, 'symbol', @targetId, 'symbol', 'References', 1.0, '["NameMatch"]'::jsonb, '{"kind":"primary"}'::jsonb);
            """,
            new Dictionary<string, object?>
            {
                ["id"] = edgeId,
                ["sourceId"] = symbolId,
                ["targetId"] = externalSymbolId,
            });

        await fixture.Store.RenameFileAsync(fileId, @"src\NewName.cs", cancellationToken);

        var fileRow = await fixture.QuerySingleAsync(
            "SELECT path FROM files WHERE id = @id;",
            static (dataSet, rowIndex) => Assert.IsType<string>(dataSet["path", rowIndex]),
            new Dictionary<string, object?> { ["id"] = fileId });

        var symbolRow = await fixture.QuerySingleAsync(
            "SELECT id, file_id FROM symbols WHERE id = @id;",
            static (dataSet, rowIndex) => new
            {
                Id = Assert.IsType<Guid>(dataSet["id", rowIndex]),
                FileId = Assert.IsType<Guid>(dataSet["file_id", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = symbolId });

        var edgeRow = await fixture.QuerySingleAsync(
            "SELECT id, source_id, target_id FROM edges WHERE id = @id;",
            static (dataSet, rowIndex) => new
            {
                Id = Assert.IsType<Guid>(dataSet["id", rowIndex]),
                SourceId = Assert.IsType<Guid>(dataSet["source_id", rowIndex]),
                TargetId = Assert.IsType<Guid>(dataSet["target_id", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = edgeId });

        Assert.Equal(@"src\NewName.cs", fileRow);
        Assert.Equal(symbolId, symbolRow.Id);
        Assert.Equal(fileId, symbolRow.FileId);
        Assert.Equal(edgeId, edgeRow.Id);
        Assert.Equal(symbolId, edgeRow.SourceId);
        Assert.Equal(externalSymbolId, edgeRow.TargetId);
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesOwnedSymbolsAndIncidentEdges()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
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
                (@deletedEdgeId, @deletedSymbolId, 'symbol', @retainedSymbolId, 'symbol', 'References', 0.95, '["NameMatch"]'::jsonb, '{}'::jsonb),
                (@retainedEdgeId, @retainedSymbolId, 'symbol', @retainedSymbolId, 'symbol', 'References', 0.10, '["Unresolved"]'::jsonb, '{}'::jsonb);
            """,
            new Dictionary<string, object?>
            {
                ["deletedEdgeId"] = deletedEdgeId,
                ["deletedSymbolId"] = deletedSymbolId,
                ["retainedSymbolId"] = retainedSymbolId,
                ["retainedEdgeId"] = retainedEdgeId,
            });

        await fixture.Store.DeleteFileAsync(deletedFileId, cancellationToken);

        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM files WHERE id = @id;", new Dictionary<string, object?> { ["id"] = deletedFileId }));
        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM symbols WHERE file_id = @id;", new Dictionary<string, object?> { ["id"] = deletedFileId }));
        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE id = @id;", new Dictionary<string, object?> { ["id"] = deletedEdgeId }));
        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM files WHERE id = @id;", new Dictionary<string, object?> { ["id"] = retainedFileId }));
        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE id = @id;", new Dictionary<string, object?> { ["id"] = retainedEdgeId }));
    }

    private static async Task<(Guid RepoId, Guid ProjectId)> InsertProjectGraphAsync(PostgresGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES (@repoId, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, FALSE);

            INSERT INTO projects (id, repo_id, name, manifest_path, path, language, ecosystem)
            VALUES (@projectId, @repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'src\GraphRAG.Code', 'csharp', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
            });

        return (repoId, projectId);
    }

    private static Task InsertFileAsync(PostgresGraphStoreFixture fixture, Guid fileId, Guid projectId, string path)
        => fixture.ExecuteAsync(
            """
            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            SELECT @fileId, repo_id, @projectId, @path, 'csharp', 'hash', NULL
            FROM projects
            WHERE id = @projectId;
            """,
            new Dictionary<string, object?>
            {
                ["fileId"] = fileId,
                ["projectId"] = projectId,
                ["path"] = path,
            });

    private static Task InsertSymbolAsync(PostgresGraphStoreFixture fixture, Guid symbolId, Guid fileId, string name)
        => fixture.ExecuteAsync(
            """
            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES (@id, @fileId, NULL, @name, NULL, 'Class', NULL, NULL, NULL, NULL, 'symbol-hash', FALSE, 1, 10);
            """,
            new Dictionary<string, object?>
            {
                ["id"] = symbolId,
                ["fileId"] = fileId,
                ["name"] = name,
            });
}
