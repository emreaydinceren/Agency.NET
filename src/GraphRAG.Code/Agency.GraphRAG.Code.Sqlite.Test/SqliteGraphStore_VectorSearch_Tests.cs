using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using System.Globalization;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests vector-search behavior for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_VectorSearch_Tests
{
    [Fact]
    public async Task VectorSearchSymbolsAsync_ReturnsBestMatchesInScoreOrder()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid fileId = await InsertFileGraphAsync(fixture);
        Guid bestId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Guid thirdId = Guid.NewGuid();

        await fixture.Store.UpsertSymbolBatchAsync(
        [
            CreateSymbol(bestId, fileId, "PaymentsCoordinator", "payments orchestration"),
            CreateSymbol(secondId, fileId, "InvoiceCoordinator", "invoice processing"),
            CreateSymbol(thirdId, fileId, "LoggerBridge", "structured logging"),
        ],
        cancellationToken);

        IReadOnlyList<VectorSearchResult> results = await fixture.Store.VectorSearchSymbolsAsync("payments orchestration", 2, cancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(bestId, results[0].Id);
        Assert.InRange(results[0].Score, 0.0, 1.0);
        Assert.InRange(results[1].Score, 0.0, 1.0);
        Assert.True(results[0].Score >= results[1].Score);
    }

    [Fact]
    public async Task VectorSearchClustersAsync_ReturnsBestMatchesInScoreOrder()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        Guid bestClusterId = Guid.NewGuid();
        Guid secondClusterId = Guid.NewGuid();
        Guid thirdClusterId = Guid.NewGuid();

        await InsertClusterAsync(fixture, bestClusterId, "Payments", ClusterType.Business, "payments orchestration");
        await InsertClusterAsync(fixture, secondClusterId, "Invoicing", ClusterType.Business, "invoice processing");
        await InsertClusterAsync(fixture, thirdClusterId, "Logging", ClusterType.Infrastructure, "structured logging");

        IReadOnlyList<VectorSearchResult> results = await fixture.Store.VectorSearchClustersAsync("payments orchestration", 2, cancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(bestClusterId, results[0].Id);
        Assert.InRange(results[0].Score, 0.0, 1.0);
        Assert.InRange(results[1].Score, 0.0, 1.0);
        Assert.True(results[0].Score >= results[1].Score);
    }

    private static Symbol CreateSymbol(Guid id, Guid fileId, string name, string embeddingText)
        => new()
        {
            Id = id,
            FileId = fileId,
            Name = name,
            FullyQualifiedName = $"Agency.GraphRAG.Code.{name}",
            Kind = SymbolKind.Class,
            Signature = $"public sealed class {name}",
            Summary = embeddingText,
            OneLineSummary = embeddingText,
            ContentHash = $"{name}-hash",
            Embedding = FakeEmbeddingGenerator.CreateVector(embeddingText),
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

    private static Task InsertClusterAsync(
        SqliteGraphStoreFixture fixture,
        Guid clusterId,
        string label,
        ClusterType type,
        string embeddingText)
    {
        float[] embedding = FakeEmbeddingGenerator.CreateVector(embeddingText);
        return fixture.ExecuteAsync(
            """
            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
            VALUES ($id, $label, $summary, $embedding, 0.88, $type, 0);

            INSERT OR REPLACE INTO clusters_vec (cluster_id, embedding)
            VALUES ($id, $embeddingVec);
            """,
            new Dictionary<string, object?>
            {
                ["$id"] = clusterId.ToString("D"),
                ["$label"] = label,
                ["$summary"] = embeddingText,
                ["$embedding"] = ToSqlVector(embedding),
                ["$embeddingVec"] = ToSqlVector(embedding),
                ["$type"] = type.ToString().ToLowerInvariant(),
            });
    }

    private static string ToSqlVector(float[] embedding)
        => $"[{string.Join(",", embedding.Select(value => value.ToString(CultureInfo.InvariantCulture)))}]";
}
