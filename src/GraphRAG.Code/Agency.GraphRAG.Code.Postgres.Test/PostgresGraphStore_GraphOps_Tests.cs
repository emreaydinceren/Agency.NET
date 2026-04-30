using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Postgres.Migrations;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for the remaining graph operations in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_GraphOps_Tests
{
    [Fact]
    public async Task UpsertEdgeBatchAsync_UpdatesExistingConflictKeyRow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 3);
        Guid edgeId = Guid.NewGuid();

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(
                edgeId,
                symbolIds[0],
                symbolIds[1],
                EdgeKind.References,
                0.35,
                [Signal.NameMatch],
                new Dictionary<string, object?> { ["origin"] = "initial" }),
        ],
        cancellationToken);

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(
                Guid.NewGuid(),
                symbolIds[0],
                symbolIds[1],
                EdgeKind.References,
                0.91,
                [Signal.NameMatch, Signal.LlmExtraction],
                new Dictionary<string, object?> { ["origin"] = "updated", ["line"] = 27L }),
        ],
        cancellationToken);

        long rowCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges;");
        var row = await fixture.QuerySingleAsync(
            """
            SELECT confidence, signals::text AS signals, properties::text AS properties
            FROM edges
            WHERE source_id = @sourceId
              AND target_id = @targetId
              AND edge_kind = 'References';
            """,
            static (dataSet, rowIndex) => new
            {
                Confidence = Convert.ToDouble(dataSet["confidence", rowIndex], CultureInfo.InvariantCulture),
                Signals = JsonSerializer.Deserialize<string[]>(Assert.IsType<string>(dataSet["signals", rowIndex])) ?? [],
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Assert.IsType<string>(dataSet["properties", rowIndex])) ?? [],
            },
            new Dictionary<string, object?>
            {
                ["sourceId"] = symbolIds[0],
                ["targetId"] = symbolIds[1],
            });

        Assert.Equal(1L, rowCount);
        Assert.Equal(0.91d, row.Confidence, 3);
        Assert.Equal(["NameMatch", "LlmExtraction"], row.Signals);
        Assert.Equal("updated", row.Properties["origin"].GetString());
        Assert.Equal(27, row.Properties["line"].GetInt32());
    }

    [Fact]
    public async Task SymbolLookupOperations_SupportIdAndFuzzyNameSearch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
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

        Symbol? symbol = await fixture.Store.GetSymbolByIdAsync(paymentHandlerId, cancellationToken);
        IReadOnlyList<Symbol> exactMatches = await fixture.Store.FindSymbolsByNameAsync("PaymentHandler", cancellationToken);
        IReadOnlyList<Symbol> fuzzyMatches = await fixture.Store.FindSymbolsByNameAsync("Payment", cancellationToken);

        Assert.NotNull(symbol);
        Assert.Equal(paymentHandlerId, symbol.Id);
        Assert.Contains(exactMatches, match => match.Id == paymentHandlerId);
        Assert.DoesNotContain(exactMatches, match => match.Id == reportBuilderId);
        Assert.Contains(fuzzyMatches, match => match.Id == paymentHandlerId);
        Assert.Contains(fuzzyMatches, match => match.Id == paymentHelperId);
        Assert.DoesNotContain(fuzzyMatches, match => match.Id == reportBuilderId);
    }

    [Fact]
    public async Task VectorSearchClustersAsync_ReturnsClosestMatchesByCosineSimilarity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        Guid bestClusterId = Guid.NewGuid();
        Guid secondClusterId = Guid.NewGuid();
        Guid thirdClusterId = Guid.NewGuid();

        await InsertClusterAsync(fixture, bestClusterId, "Payments", ClusterType.Business, "payments orchestration");
        await InsertClusterAsync(fixture, secondClusterId, "Invoicing", ClusterType.Business, "invoice processing");
        await InsertClusterAsync(fixture, thirdClusterId, "Logging", ClusterType.Infrastructure, "structured logging");

        IReadOnlyList<VectorSearchResult> results = await fixture.Store.VectorSearchClustersAsync("payments orchestration", 2, cancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(bestClusterId, results[0].Id);
        Assert.InRange(results[0].Score, 0.99d, 1.0d);
        Assert.True(results[0].Score >= results[1].Score);
    }

    [Fact]
    public async Task TraverseFromAsync_FollowsFilteredEdgesWithoutCycles()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 4);

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(Guid.NewGuid(), symbolIds[0], symbolIds[1], EdgeKind.References, 0.95),
            CreateEdge(Guid.NewGuid(), symbolIds[1], symbolIds[2], EdgeKind.References, 0.93),
            CreateEdge(Guid.NewGuid(), symbolIds[2], symbolIds[0], EdgeKind.References, 0.92),
            CreateEdge(Guid.NewGuid(), symbolIds[0], symbolIds[3], EdgeKind.DependsOn, 0.99),
        ],
        cancellationToken);

        IReadOnlyList<TraversalHop> results = await fixture.Store.TraverseFromAsync(
            new TraversalRequest
            {
                SeedSymbolIds = [symbolIds[0]],
                EdgeKinds = [EdgeKind.References],
                MinConfidence = 0.90,
                MaxHops = 3,
            },
            cancellationToken);

        Assert.Contains(results, hop => hop.SymbolId == symbolIds[0] && hop.Depth == 0);
        Assert.Contains(results, hop => hop.SymbolId == symbolIds[1] && hop.Depth == 1);
        Assert.Contains(results, hop => hop.SymbolId == symbolIds[2] && hop.Depth == 2);
        Assert.DoesNotContain(results, hop => hop.SymbolId == symbolIds[3]);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task StageAndDrainUnresolvedCallSitesAsync_SupportsScopedDrain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        (Guid firstFileId, Guid secondFileId, Guid firstSymbolId, Guid secondSymbolId) = await InsertSourceGraphAsync(fixture);

        await fixture.Store.StageUnresolvedCallSiteBatchAsync(
        [
            new UnresolvedCallSite
            {
                Id = Guid.NewGuid(),
                SourceSymbolId = firstSymbolId,
                SourceFileId = firstFileId,
                Identifier = "ResolveOrder",
                Scope = "Agency.GraphRAG.Code",
            },
            new UnresolvedCallSite
            {
                Id = Guid.NewGuid(),
                SourceSymbolId = secondSymbolId,
                SourceFileId = secondFileId,
                Identifier = "PublishInvoice",
                Scope = "Agency.GraphRAG.Code.Invoices",
            },
        ],
        cancellationToken);

        IReadOnlyList<UnresolvedCallSite> drained = await fixture.Store.DrainUnresolvedCallSitesAsync(firstFileId, cancellationToken);

        Assert.Single(drained);
        Assert.Equal(firstFileId, drained[0].SourceFileId);
        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM unresolved_call_sites;"));
    }

    [Fact]
    public async Task ClusterOperations_ApplyAssignmentsAndReplaceSummariesAtomically()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 2);
        Guid oldClusterId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
            VALUES (@id, 'Old cluster', 'old summary', @embedding::vector, 0.10, 'Mixed', 0);
            """,
            new Dictionary<string, object?>
            {
                ["id"] = oldClusterId,
                ["embedding"] = PostgresGraphStoreFixture.ToVectorLiteral(CreateVector("old summary")),
            });

        await fixture.Store.ApplyClusterAssignmentsAsync(
            new Dictionary<Guid, (Guid, string)>
            {
                [symbolIds[0]] = (oldClusterId, "primary"),
                [symbolIds[1]] = (oldClusterId, "utility"),
            },
            cancellationToken);

        await fixture.Store.ReplaceClusterSummariesAtomicallyAsync(
        [
            new Agency.GraphRAG.Code.Domain.Cluster
            {
                Id = Guid.NewGuid(),
                Label = "Payments",
                Type = ClusterType.Business,
                CoherenceScore = 0.91,
                Summary = "Owns payment orchestration.",
                Embedding = CreateVector("payments orchestration"),
            },
            new Agency.GraphRAG.Code.Domain.Cluster
            {
                Id = Guid.NewGuid(),
                Label = "Infrastructure",
                Type = ClusterType.Infrastructure,
                CoherenceScore = 0.82,
                Summary = "Provides shared services.",
                Embedding = CreateVector("shared services"),
            },
        ],
        cancellationToken);

        long clusterCount = await fixture.CountAsync("SELECT COUNT(*) FROM clusters;");
        long memberOfCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE edge_kind = 'MemberOf';");
        long oldClusterCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM clusters WHERE id = @id;",
            new Dictionary<string, object?> { ["id"] = oldClusterId });

        Assert.Equal(2L, clusterCount);
        Assert.Equal(0L, memberOfCount);
        Assert.Equal(0L, oldClusterCount);
    }

    private static Edge CreateEdge(
        Guid id,
        Guid sourceId,
        Guid targetId,
        EdgeKind edgeKind,
        double confidence,
        IReadOnlyList<Signal>? signals = null,
        IReadOnlyDictionary<string, object?>? properties = null)
        => new()
        {
            Id = id,
            SourceId = sourceId,
            SourceKind = "symbol",
            TargetId = targetId,
            TargetKind = "symbol",
            EdgeKind = edgeKind,
            Confidence = confidence,
            Signals = signals ?? [Signal.NameMatch],
            Properties = properties ?? new Dictionary<string, object?>(),
        };

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
            Embedding = CreateVector(name),
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
            VALUES (@projectId, @repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'src\GraphRAG.Code', 'csharp', 'nuget');

            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            VALUES (@fileId, @repoId, @projectId, 'src\GraphRAG.Code\PostgresGraphStore.cs', 'csharp', 'file-hash', NOW());
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
            });

        return fileId;
    }

    private static async Task<IReadOnlyList<Guid>> InsertSymbolGraphAsync(PostgresGraphStoreFixture fixture, int count)
    {
        Guid fileId = await InsertFileGraphAsync(fixture);
        List<Guid> symbolIds = [];
        for (int index = 0; index < count; index++)
        {
            Guid symbolId = Guid.NewGuid();
            symbolIds.Add(symbolId);
            await fixture.Store.UpsertSymbolAsync(
                new Symbol
                {
                    Id = symbolId,
                    FileId = fileId,
                    Name = $"Node{index}",
                    FullyQualifiedName = $"Agency.GraphRAG.Code.Node{index}",
                    Kind = SymbolKind.Method,
                    Signature = $"void Node{index}()",
                    Summary = $"Node {index}",
                    OneLineSummary = $"Node {index}",
                    ContentHash = $"node-{index}",
                    Embedding = CreateVector($"Node{index}"),
                    IsUtility = false,
                    SourceRangeStart = 1,
                    SourceRangeEnd = 10,
                },
                TestContext.Current.CancellationToken);
        }

        return symbolIds;
    }

    private static async Task<(Guid FirstFileId, Guid SecondFileId, Guid FirstSymbolId, Guid SecondSymbolId)> InsertSourceGraphAsync(PostgresGraphStoreFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid repoId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid firstFileId = Guid.NewGuid();
        Guid secondFileId = Guid.NewGuid();
        Guid firstSymbolId = Guid.NewGuid();
        Guid secondSymbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES (@repoId, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, FALSE);

            INSERT INTO projects (id, repo_id, name, manifest_path, path, language, ecosystem)
            VALUES (@projectId, @repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'src\GraphRAG.Code', 'csharp', 'nuget');

            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            VALUES
                (@firstFileId, @repoId, @projectId, 'src\GraphRAG.Code\OrderResolver.cs', 'csharp', 'file-1', NOW()),
                (@secondFileId, @repoId, @projectId, 'src\GraphRAG.Code\InvoicePublisher.cs', 'csharp', 'file-2', NOW());
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
                ["firstFileId"] = firstFileId,
                ["secondFileId"] = secondFileId,
            });

        await fixture.Store.UpsertSymbolBatchAsync(
        [
            new Symbol
            {
                Id = firstSymbolId,
                FileId = firstFileId,
                Name = "OrderResolver",
                FullyQualifiedName = "Agency.GraphRAG.Code.OrderResolver",
                Kind = SymbolKind.Class,
                Signature = "public sealed class OrderResolver",
                Summary = "Resolves orders.",
                OneLineSummary = "Resolves orders.",
                ContentHash = "symbol-1",
                Embedding = CreateVector("OrderResolver"),
                IsUtility = false,
                SourceRangeStart = 1,
                SourceRangeEnd = 10,
            },
            new Symbol
            {
                Id = secondSymbolId,
                FileId = secondFileId,
                Name = "InvoicePublisher",
                FullyQualifiedName = "Agency.GraphRAG.Code.InvoicePublisher",
                Kind = SymbolKind.Class,
                Signature = "public sealed class InvoicePublisher",
                Summary = "Publishes invoices.",
                OneLineSummary = "Publishes invoices.",
                ContentHash = "symbol-2",
                Embedding = CreateVector("InvoicePublisher"),
                IsUtility = false,
                SourceRangeStart = 1,
                SourceRangeEnd = 10,
            },
        ],
        cancellationToken);

        return (firstFileId, secondFileId, firstSymbolId, secondSymbolId);
    }

    private static Task InsertClusterAsync(
        PostgresGraphStoreFixture fixture,
        Guid clusterId,
        string label,
        ClusterType type,
        string embeddingText)
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
                ["type"] = type.ToString(),
            });

    private static float[] CreateVector(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte[] bytes = Encoding.UTF8.GetBytes(input);
        float[] vector = new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
        if (bytes.Length == 0)
        {
            return vector;
        }

        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = bytes[index % bytes.Length] / 255f;
        }

        return vector;
    }
}
