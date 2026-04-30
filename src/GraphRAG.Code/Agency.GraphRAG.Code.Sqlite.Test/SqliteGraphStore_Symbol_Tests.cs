using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests module and symbol persistence for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Symbol_Tests
{
    [Fact]
    public async Task UpsertModuleAsync_InsertsAndUpdatesModule()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid fileId = await InsertFileGraphAsync(fixture);
        var moduleId = Guid.NewGuid();

        await fixture.Store.UpsertModuleAsync(new Module
        {
            Id = moduleId,
            FileId = fileId,
            Name = "Agency.GraphRAG.Code",
            Kind = "namespace",
        }, cancellationToken);

        await fixture.Store.UpsertModuleAsync(new Module
        {
            Id = moduleId,
            FileId = fileId,
            Name = "Agency.GraphRAG.Code.Sqlite",
            Kind = "class",
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT file_id, name, kind
            FROM modules
            WHERE id = $id;
            """,
            reader => new
            {
                FileId = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.IsDBNull(2) ? null : reader.GetString(2),
            },
            new Dictionary<string, object?> { ["$id"] = moduleId.ToString("D") });

        Assert.Equal(fileId.ToString("D"), row.FileId);
        Assert.Equal("Agency.GraphRAG.Code.Sqlite", row.Name);
        Assert.Equal("class", row.Kind);
    }

    [Fact]
    public async Task UpsertSymbolAsync_PersistsFieldChangesAndEmbeddingRoundTrip()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid fileId = await InsertFileGraphAsync(fixture);
        Guid moduleId = Guid.NewGuid();
        Guid symbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO modules (id, file_id, project_id, name, path, kind)
            VALUES ($id, $fileId, NULL, 'Agency.GraphRAG.Code', NULL, 'namespace');
            """,
            new Dictionary<string, object?>
            {
                ["$id"] = moduleId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
            });

        float[] originalEmbedding = [0.25f, 0.5f, 0.75f, 1.0f, 0.1f, 0.2f, 0.3f, 0.4f];
        float[] updatedEmbedding = FakeEmbeddingGenerator.CreateVector("SqliteGraphStore");

        await fixture.Store.UpsertSymbolAsync(new Symbol
        {
            Id = symbolId,
            FileId = fileId,
            ModuleId = moduleId,
            Name = "SqliteGraphStore",
            FullyQualifiedName = "Agency.GraphRAG.Code.Sqlite.SqliteGraphStore",
            Kind = SymbolKind.Class,
            Signature = "public sealed class SqliteGraphStore",
            Summary = "Stores graph state in SQLite.",
            OneLineSummary = "SQLite graph store.",
            ContentHash = "symbol-v1",
            Embedding = originalEmbedding,
            IsUtility = false,
            SourceRangeStart = 10,
            SourceRangeEnd = 80,
        }, cancellationToken);

        await fixture.Store.UpsertSymbolAsync(new Symbol
        {
            Id = symbolId,
            FileId = fileId,
            ModuleId = moduleId,
            Name = "SqliteGraphStore",
            FullyQualifiedName = "Agency.GraphRAG.Code.Sqlite.SqliteGraphStore",
            Kind = SymbolKind.Class,
            Signature = "public sealed class SqliteGraphStore : IGraphStore",
            Summary = "Stores and queries graph state in SQLite.",
            OneLineSummary = "SQLite-backed graph store.",
            ContentHash = "symbol-v2",
            Embedding = updatedEmbedding,
            IsUtility = true,
            SourceRangeStart = 12,
            SourceRangeEnd = 90,
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT module_id, signature, summary, one_line_summary, embedding, content_hash, is_utility, source_range_start, source_range_end
            FROM symbols
            WHERE id = $id;
            """,
            reader => new
            {
                ModuleId = reader.IsDBNull(0) ? null : reader.GetString(0),
                Signature = reader.IsDBNull(1) ? null : reader.GetString(1),
                Summary = reader.IsDBNull(2) ? null : reader.GetString(2),
                OneLineSummary = reader.IsDBNull(3) ? null : reader.GetString(3),
                Embedding = SqliteGraphStoreFixture.ReadStoredEmbedding(reader.GetValue(4)),
                ContentHash = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsUtility = reader.GetInt64(6),
                SourceRangeStart = reader.GetInt32(7),
                SourceRangeEnd = reader.GetInt32(8),
            },
            new Dictionary<string, object?> { ["$id"] = symbolId.ToString("D") });

        Assert.Equal(moduleId.ToString("D"), row.ModuleId);
        Assert.Equal("public sealed class SqliteGraphStore : IGraphStore", row.Signature);
        Assert.Equal("Stores and queries graph state in SQLite.", row.Summary);
        Assert.Equal("SQLite-backed graph store.", row.OneLineSummary);
        Assert.Equal(updatedEmbedding, row.Embedding);
        Assert.Equal("symbol-v2", row.ContentHash);
        Assert.Equal(1L, row.IsUtility);
        Assert.Equal(12, row.SourceRangeStart);
        Assert.Equal(90, row.SourceRangeEnd);
    }

    [Fact]
    public async Task UpsertSymbolBatchAsync_UpsertsMultipleSymbolsWithoutDuplicates()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid fileId = await InsertFileGraphAsync(fixture);
        Guid moduleId = Guid.NewGuid();
        Guid existingSymbolId = Guid.NewGuid();
        Guid newSymbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO modules (id, file_id, project_id, name, path, kind)
            VALUES ($moduleId, $fileId, NULL, 'Agency.GraphRAG.Code', NULL, 'namespace');

            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES (
                $symbolId, $fileId, $moduleId, 'ExistingSymbol', 'Agency.GraphRAG.Code.ExistingSymbol', 'Method',
                'void ExistingSymbol()', 'Old summary', 'Old one line', NULL, 'old-hash', 0, 20, 30
            );
            """,
            new Dictionary<string, object?>
            {
                ["$moduleId"] = moduleId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
                ["$symbolId"] = existingSymbolId.ToString("D"),
            });

        float[] existingEmbedding = FakeEmbeddingGenerator.CreateVector("ExistingSymbol");
        float[] newEmbedding = FakeEmbeddingGenerator.CreateVector("NewSymbol");

        await fixture.Store.UpsertSymbolBatchAsync(
        [
            new Symbol
            {
                Id = existingSymbolId,
                FileId = fileId,
                ModuleId = moduleId,
                Name = "ExistingSymbol",
                FullyQualifiedName = "Agency.GraphRAG.Code.ExistingSymbol",
                Kind = SymbolKind.Method,
                Signature = "void ExistingSymbol(int value)",
                Summary = "Updated summary",
                OneLineSummary = "Updated one line",
                ContentHash = "updated-hash",
                Embedding = existingEmbedding,
                IsUtility = false,
                SourceRangeStart = 21,
                SourceRangeEnd = 31,
            },
            new Symbol
            {
                Id = newSymbolId,
                FileId = fileId,
                ModuleId = moduleId,
                Name = "NewSymbol",
                FullyQualifiedName = "Agency.GraphRAG.Code.NewSymbol",
                Kind = SymbolKind.Method,
                Signature = "void NewSymbol()",
                Summary = "Brand new summary",
                OneLineSummary = "Brand new one line",
                ContentHash = "new-hash",
                Embedding = newEmbedding,
                IsUtility = true,
                SourceRangeStart = 40,
                SourceRangeEnd = 50,
            },
        ],
        cancellationToken);

        long rowCount = await fixture.CountAsync("SELECT COUNT(*) FROM symbols WHERE file_id = $fileId;", new Dictionary<string, object?> { ["$fileId"] = fileId.ToString("D") });

        var updated = await fixture.QuerySingleAsync(
            """
            SELECT signature, summary, one_line_summary, embedding, content_hash, is_utility
            FROM symbols
            WHERE id = $id;
            """,
            reader => new
            {
                Signature = reader.IsDBNull(0) ? null : reader.GetString(0),
                Summary = reader.IsDBNull(1) ? null : reader.GetString(1),
                OneLineSummary = reader.IsDBNull(2) ? null : reader.GetString(2),
                Embedding = SqliteGraphStoreFixture.ReadStoredEmbedding(reader.GetValue(3)),
                ContentHash = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsUtility = reader.GetInt64(5),
            },
            new Dictionary<string, object?> { ["$id"] = existingSymbolId.ToString("D") });

        var inserted = await fixture.QuerySingleAsync(
            """
            SELECT name, embedding, content_hash, is_utility
            FROM symbols
            WHERE id = $id;
            """,
            reader => new
            {
                Name = reader.GetString(0),
                Embedding = SqliteGraphStoreFixture.ReadStoredEmbedding(reader.GetValue(1)),
                ContentHash = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsUtility = reader.GetInt64(3),
            },
            new Dictionary<string, object?> { ["$id"] = newSymbolId.ToString("D") });

        Assert.Equal(2L, rowCount);
        Assert.Equal("void ExistingSymbol(int value)", updated.Signature);
        Assert.Equal("Updated summary", updated.Summary);
        Assert.Equal("Updated one line", updated.OneLineSummary);
        Assert.Equal(existingEmbedding, updated.Embedding);
        Assert.Equal("updated-hash", updated.ContentHash);
        Assert.Equal(0L, updated.IsUtility);
        Assert.Equal("NewSymbol", inserted.Name);
        Assert.Equal(newEmbedding, inserted.Embedding);
        Assert.Equal("new-hash", inserted.ContentHash);
        Assert.Equal(1L, inserted.IsUtility);
    }

    private static async Task<Guid> InsertFileGraphAsync(SqliteGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES ($repoId, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, 0);

            INSERT INTO projects (id, repo_id, name, relative_path, manifest_path, language, ecosystem)
            VALUES ($projectId, $repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'csharp', NULL);

            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            VALUES ($fileId, $repoId, $projectId, 'src\GraphRAG.Code\SqliteGraphStore.cs', 'csharp', 'file-hash', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["$repoId"] = repoId.ToString("D"),
                ["$projectId"] = projectId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
            });

        return fileId;
    }
}
