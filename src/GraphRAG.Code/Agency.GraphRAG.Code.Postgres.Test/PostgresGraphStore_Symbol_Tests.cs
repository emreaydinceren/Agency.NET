using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.GraphRAG.Code.Storage;
using System.Text;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for module and symbol persistence in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_Symbol_Tests
{
    [Fact]
    public async Task UpsertModuleAsync_InsertsAndUpdatesModule()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        (Guid projectId, Guid fileId) = await InsertFileGraphAsync(fixture);
        Guid moduleId = Guid.NewGuid();

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
            Name = "Agency.GraphRAG.Code.Postgres",
            Kind = "class",
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT file_id, project_id, name, kind
            FROM modules
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                FileId = Assert.IsType<Guid>(dataSet["file_id", rowIndex]),
                ProjectId = Assert.IsType<Guid>(dataSet["project_id", rowIndex]),
                Name = Assert.IsType<string>(dataSet["name", rowIndex]),
                Kind = dataSet["kind", rowIndex] as string,
            },
            new Dictionary<string, object?> { ["id"] = moduleId });

        Assert.Equal(fileId, row.FileId);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal("Agency.GraphRAG.Code.Postgres", row.Name);
        Assert.Equal("class", row.Kind);
    }

    [Fact]
    public async Task UpsertSymbolAsync_PersistsFieldChangesEmbeddingAndUtilityFlag()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        (Guid projectId, Guid fileId) = await InsertFileGraphAsync(fixture);
        Guid moduleId = Guid.NewGuid();
        Guid symbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO modules (id, project_id, file_id, name, path, kind)
            VALUES (@id, @projectId, @fileId, 'Agency.GraphRAG.Code', NULL, 'namespace');
            """,
            new Dictionary<string, object?>
            {
                ["id"] = moduleId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
            });

        float[] originalEmbedding = CreateVector("PostgresGraphStore-v1");
        float[] updatedEmbedding = CreateVector("PostgresGraphStore-v2");

        await fixture.Store.UpsertSymbolAsync(new Symbol
        {
            Id = symbolId,
            FileId = fileId,
            ModuleId = moduleId,
            Name = "PostgresGraphStore",
            FullyQualifiedName = "Agency.GraphRAG.Code.Postgres.PostgresGraphStore",
            Kind = SymbolKind.Class,
            Signature = "public sealed class PostgresGraphStore",
            Summary = "Stores graph state in PostgreSQL.",
            OneLineSummary = "PostgreSQL graph store.",
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
            Name = "PostgresGraphStore",
            FullyQualifiedName = "Agency.GraphRAG.Code.Postgres.PostgresGraphStore",
            Kind = SymbolKind.Class,
            Signature = "public sealed class PostgresGraphStore : IGraphStore",
            Summary = "Stores and queries graph state in PostgreSQL.",
            OneLineSummary = "PostgreSQL-backed graph store.",
            ContentHash = "symbol-v2",
            Embedding = updatedEmbedding,
            IsUtility = true,
            SourceRangeStart = 12,
            SourceRangeEnd = 90,
        }, cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT module_id, signature, summary, one_line_summary, embedding::text AS embedding, content_hash, is_utility, source_range_start, source_range_end
            FROM symbols
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                ModuleId = Assert.IsType<Guid>(dataSet["module_id", rowIndex]),
                Signature = dataSet["signature", rowIndex] as string,
                Summary = dataSet["summary", rowIndex] as string,
                OneLineSummary = dataSet["one_line_summary", rowIndex] as string,
                Embedding = PostgresGraphStoreFixture.ParseVectorLiteral(Assert.IsType<string>(dataSet["embedding", rowIndex])),
                ContentHash = dataSet["content_hash", rowIndex] as string,
                IsUtility = Assert.IsType<bool>(dataSet["is_utility", rowIndex]),
                SourceRangeStart = Assert.IsType<int>(dataSet["source_range_start", rowIndex]),
                SourceRangeEnd = Assert.IsType<int>(dataSet["source_range_end", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = symbolId });

        Assert.Equal(moduleId, row.ModuleId);
        Assert.Equal("public sealed class PostgresGraphStore : IGraphStore", row.Signature);
        Assert.Equal("Stores and queries graph state in PostgreSQL.", row.Summary);
        Assert.Equal("PostgreSQL-backed graph store.", row.OneLineSummary);
        Assert.Equal(updatedEmbedding, row.Embedding);
        Assert.Equal("symbol-v2", row.ContentHash);
        Assert.True(row.IsUtility);
        Assert.Equal(12, row.SourceRangeStart);
        Assert.Equal(90, row.SourceRangeEnd);
    }

    [Fact]
    public async Task VectorSearchSymbolsAsync_ReturnsClosestMatchesByCosineSimilarity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        (_, Guid fileId) = await InsertFileGraphAsync(fixture);
        Guid exactMatchId = Guid.NewGuid();
        Guid nearbyMatchId = Guid.NewGuid();
        Guid distantMatchId = Guid.NewGuid();
        string queryText = "AlphaUtility";

        await InsertSearchSymbolAsync(fixture, exactMatchId, fileId, "AlphaUtility", CreateVector(queryText), isUtility: false);
        await InsertSearchSymbolAsync(fixture, nearbyMatchId, fileId, "AlphaUtilityHelper", CreateVector("AlphaUtilityHelper"), isUtility: true);
        await InsertSearchSymbolAsync(fixture, distantMatchId, fileId, "ZetaImplementation", CreateVector("ZetaImplementation"), isUtility: false);

        IReadOnlyList<VectorSearchResult> results = await fixture.Store.VectorSearchSymbolsAsync(queryText, 3, cancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal(exactMatchId, results[0].Id);
        Assert.InRange(results[0].Score, 0.99d, 1.0d);
        Assert.True(results[0].Score >= results[1].Score);
        Assert.True(results[1].Score >= results[2].Score);
        Assert.Contains(results, result => result.Id == nearbyMatchId);
        Assert.Contains(results, result => result.Id == distantMatchId);
    }

    [Fact]
    public async Task UpsertSymbolBatchAsync_IsIdempotentForExistingAndNewRows()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        (Guid projectId, Guid fileId) = await InsertFileGraphAsync(fixture);
        Guid moduleId = Guid.NewGuid();
        Guid existingSymbolId = Guid.NewGuid();
        Guid newSymbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO modules (id, project_id, file_id, name, path, kind)
            VALUES (@moduleId, @projectId, @fileId, 'Agency.GraphRAG.Code', NULL, 'namespace');

            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES (
                @symbolId, @fileId, @moduleId, 'ExistingSymbol', 'Agency.GraphRAG.Code.ExistingSymbol', 'Method',
                'void ExistingSymbol()', 'Old summary', 'Old one line', NULL, 'old-hash', FALSE, 20, 30
            );
            """,
            new Dictionary<string, object?>
            {
                ["moduleId"] = moduleId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
                ["symbolId"] = existingSymbolId,
            });

        Symbol[] batch =
        [
            new()
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
                Embedding = CreateVector("ExistingSymbol"),
                IsUtility = false,
                SourceRangeStart = 21,
                SourceRangeEnd = 31,
            },
            new()
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
                Embedding = CreateVector("NewSymbol"),
                IsUtility = true,
                SourceRangeStart = 40,
                SourceRangeEnd = 50,
            },
        ];

        await fixture.Store.UpsertSymbolBatchAsync(batch, cancellationToken);
        await fixture.Store.UpsertSymbolBatchAsync(batch, cancellationToken);

        long rowCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM symbols WHERE file_id = @fileId;",
            new Dictionary<string, object?> { ["fileId"] = fileId });

        var updated = await fixture.QuerySingleAsync(
            """
            SELECT signature, summary, one_line_summary, embedding::text AS embedding, content_hash, is_utility
            FROM symbols
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                Signature = dataSet["signature", rowIndex] as string,
                Summary = dataSet["summary", rowIndex] as string,
                OneLineSummary = dataSet["one_line_summary", rowIndex] as string,
                Embedding = PostgresGraphStoreFixture.ParseVectorLiteral(Assert.IsType<string>(dataSet["embedding", rowIndex])),
                ContentHash = dataSet["content_hash", rowIndex] as string,
                IsUtility = Assert.IsType<bool>(dataSet["is_utility", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = existingSymbolId });

        var inserted = await fixture.QuerySingleAsync(
            """
            SELECT name, embedding::text AS embedding, content_hash, is_utility
            FROM symbols
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                Name = Assert.IsType<string>(dataSet["name", rowIndex]),
                Embedding = PostgresGraphStoreFixture.ParseVectorLiteral(Assert.IsType<string>(dataSet["embedding", rowIndex])),
                ContentHash = dataSet["content_hash", rowIndex] as string,
                IsUtility = Assert.IsType<bool>(dataSet["is_utility", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = newSymbolId });

        Assert.Equal(2L, rowCount);
        Assert.Equal("void ExistingSymbol(int value)", updated.Signature);
        Assert.Equal("Updated summary", updated.Summary);
        Assert.Equal("Updated one line", updated.OneLineSummary);
        Assert.Equal(CreateVector("ExistingSymbol"), updated.Embedding);
        Assert.Equal("updated-hash", updated.ContentHash);
        Assert.False(updated.IsUtility);
        Assert.Equal("NewSymbol", inserted.Name);
        Assert.Equal(CreateVector("NewSymbol"), inserted.Embedding);
        Assert.Equal("new-hash", inserted.ContentHash);
        Assert.True(inserted.IsUtility);
    }

    private static async Task<(Guid ProjectId, Guid FileId)> InsertFileGraphAsync(PostgresGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES (@repoId, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, FALSE);

            INSERT INTO projects (id, repo_id, name, manifest_path, path, language, ecosystem)
            VALUES (@projectId, @repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'src\GraphRAG.Code', 'csharp', NULL);

            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            VALUES (@fileId, @repoId, @projectId, 'src\GraphRAG.Code\PostgresGraphStore.cs', 'csharp', 'file-hash', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
            });

        return (projectId, fileId);
    }

    private static Task InsertSearchSymbolAsync(
        PostgresGraphStoreFixture fixture,
        Guid symbolId,
        Guid fileId,
        string name,
        float[] embedding,
        bool isUtility)
        => fixture.ExecuteAsync(
            """
            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES (
                @id, @fileId, NULL, @name, @fullyQualifiedName, 'Class', NULL, NULL, NULL,
                @embedding::vector, @contentHash, @isUtility, 1, 10
            );
            """,
            new Dictionary<string, object?>
            {
                ["id"] = symbolId,
                ["fileId"] = fileId,
                ["name"] = name,
                ["fullyQualifiedName"] = $"Agency.GraphRAG.Code.{name}",
                ["embedding"] = PostgresGraphStoreFixture.ToVectorLiteral(embedding),
                ["contentHash"] = $"{name}-hash",
                ["isUtility"] = isUtility,
            });

    private static float[] CreateVector(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte[] bytes = Encoding.UTF8.GetBytes(input);
        if (bytes.Length == 0)
        {
            return new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
        }

        float[] vector = new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = bytes[index % bytes.Length] / 255f;
        }

        return vector;
    }
}
