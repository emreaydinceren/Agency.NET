using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests lookup behavior for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Lookup_Tests
{
    [Fact]
    public async Task FindSymbolsByNameAsync_SupportsExactAndFuzzyMatches()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid fileId = await InsertFileGraphAsync(fixture);
        Guid paymentHandlerId = Guid.NewGuid();
        Guid paymentHelperId = Guid.NewGuid();
        Guid reportBuilderId = Guid.NewGuid();

        await fixture.Store.UpsertSymbolBatchAsync(
        [
            CreateSymbol(paymentHandlerId, fileId, "PaymentHandler"),
            CreateSymbol(paymentHelperId, fileId, "PaymentHelper"),
            CreateSymbol(reportBuilderId, fileId, "ReportBuilder"),
        ],
        cancellationToken);

        IReadOnlyList<Symbol> exactMatches = await fixture.Store.FindSymbolsByNameAsync("PaymentHandler", cancellationToken);
        IReadOnlyList<Symbol> fuzzyMatches = await fixture.Store.FindSymbolsByNameAsync("Payment", cancellationToken);

        Assert.Contains(exactMatches, symbol => symbol.Id == paymentHandlerId);
        Assert.DoesNotContain(exactMatches, symbol => symbol.Id == reportBuilderId);
        Assert.Contains(fuzzyMatches, symbol => symbol.Id == paymentHandlerId);
        Assert.Contains(fuzzyMatches, symbol => symbol.Id == paymentHelperId);
        Assert.DoesNotContain(fuzzyMatches, symbol => symbol.Id == reportBuilderId);
    }

    [Fact]
    public async Task GetSymbolByIdAsync_ReturnsPersistedSymbol()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid fileId = await InsertFileGraphAsync(fixture);
        Guid moduleId = Guid.NewGuid();
        Guid symbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO modules (id, file_id, project_id, name, path, kind)
            SELECT $moduleId, $fileId, project_id, 'Agency.GraphRAG.Code.Sqlite', NULL, 'class'
            FROM files
            WHERE id = $fileId;
            """,
            new Dictionary<string, object?>
            {
                ["$moduleId"] = moduleId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
            });

        await fixture.Store.UpsertSymbolAsync(
            new Symbol
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
                ContentHash = "sqlite-store-v1",
                Embedding = FakeEmbeddingGenerator.CreateVector("SqliteGraphStore"),
                IsUtility = true,
                SourceRangeStart = 12,
                SourceRangeEnd = 90,
            },
            cancellationToken);

        Symbol? symbol = await fixture.Store.GetSymbolByIdAsync(symbolId, cancellationToken);

        Assert.NotNull(symbol);
        Assert.Equal(symbolId, symbol.Id);
        Assert.Equal(fileId, symbol.FileId);
        Assert.Equal(moduleId, symbol.ModuleId);
        Assert.Equal("SqliteGraphStore", symbol.Name);
        Assert.Equal("Agency.GraphRAG.Code.Sqlite.SqliteGraphStore", symbol.FullyQualifiedName);
        Assert.Equal(SymbolKind.Class, symbol.Kind);
        Assert.Equal("public sealed class SqliteGraphStore : IGraphStore", symbol.Signature);
        Assert.Equal("Stores and queries graph state in SQLite.", symbol.Summary);
        Assert.Equal("SQLite-backed graph store.", symbol.OneLineSummary);
        Assert.Equal("sqlite-store-v1", symbol.ContentHash);
        Assert.Equal(FakeEmbeddingGenerator.CreateVector("SqliteGraphStore"), symbol.Embedding);
        Assert.True(symbol.IsUtility);
        Assert.Equal(12, symbol.SourceRangeStart);
        Assert.Equal(90, symbol.SourceRangeEnd);
    }

    private static Symbol CreateSymbol(Guid id, Guid fileId, string name)
        => new()
        {
            Id = id,
            FileId = fileId,
            Name = name,
            FullyQualifiedName = $"Agency.GraphRAG.Code.{name}",
            Kind = SymbolKind.Class,
            Signature = $"public sealed class {name}",
            Summary = $"{name} summary",
            OneLineSummary = $"{name} one-line summary",
            ContentHash = $"{name}-hash",
            Embedding = FakeEmbeddingGenerator.CreateVector(name),
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 20,
        };

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
