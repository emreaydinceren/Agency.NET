using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.GraphRAG.Code.Storage;
using System.Text;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for vector-search behavior in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_VectorSearch_Tests
{
    [Fact]
    public async Task VectorSearchSymbolsAsync_ReturnsBestMatchesInScoreOrder()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
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

        IReadOnlyList<VectorSearchResult> results = await fixture.Store.VectorSearchSymbolsAsync("payments orchestration", 3, cancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal(bestId, results[0].Id);
        Assert.True(results[0].Score >= results[1].Score);
        Assert.True(results[1].Score >= results[2].Score);
    }

    [Fact]
    public async Task VectorSearchClustersAsync_ReturnsBestMatchesInScoreOrder()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid bestClusterId = Guid.NewGuid();
        Guid secondClusterId = Guid.NewGuid();
        Guid thirdClusterId = Guid.NewGuid();

        await InsertClusterAsync(fixture, bestClusterId, "Payments", ClusterType.Business, "payments orchestration");
        await InsertClusterAsync(fixture, secondClusterId, "Invoicing", ClusterType.Business, "invoice processing");
        await InsertClusterAsync(fixture, thirdClusterId, "Logging", ClusterType.Infrastructure, "structured logging");

        IReadOnlyList<VectorSearchResult> results = await fixture.Store.VectorSearchClustersAsync("payments orchestration", 3, cancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal(bestClusterId, results[0].Id);
        Assert.True(results[0].Score >= results[1].Score);
        Assert.True(results[1].Score >= results[2].Score);
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
            Embedding = CreateVector(embeddingText),
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 20,
        };

    private static async Task<Guid> InsertFileGraphAsync(PostgresGraphStoreFixture fixture)
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
            VALUES (@fileId, @repoId, @projectId, 'src\GraphRAG.Code\VectorSearch.cs', 'csharp', 'vector-file', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
            });

        return fileId;
    }

    private static Task InsertClusterAsync(PostgresGraphStoreFixture fixture, Guid clusterId, string label, ClusterType type, string embeddingText)
        => fixture.ExecuteAsync(
            """
            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
            VALUES (@id, @label, @summary, @embedding::vector, 0.88, @type, 0);
            """,
            new Dictionary<string, object?>
            {
                ["id"] = clusterId,
                ["label"] = label,
                ["summary"] = embeddingText,
                ["embedding"] = PostgresGraphStoreFixture.ToVectorLiteral(CreateVector(embeddingText)),
                ["type"] = type.ToString().ToLowerInvariant(),
            });

    private static float[] CreateVector(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        float[] vector = new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = bytes[index % bytes.Length] / 255f;
        }

        return vector;
    }
}
