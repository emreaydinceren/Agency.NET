using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Postgres.Migrations;
using System.Text;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for clustering persistence in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_Cluster_Tests
{
    [Fact]
    public async Task ApplyClusterAssignmentsAsync_WritesMemberOfEdgesWithKindProperty()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 2);
        Guid businessClusterId = Guid.NewGuid();
        Guid utilityClusterId = Guid.NewGuid();

        await fixture.Store.ApplyClusterAssignmentsAsync(
            new Dictionary<Guid, (Guid, string)>
            {
                [symbolIds[0]] = (businessClusterId, "primary"),
                [symbolIds[1]] = (utilityClusterId, "utility"),
            },
            cancellationToken);

        long memberOfCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE edge_kind = 'MemberOf';");
        var primaryRow = await fixture.QuerySingleAsync(
            """
            SELECT source_id, source_kind, target_id, target_kind, properties::text AS properties
            FROM edges
            WHERE source_id = @sourceId;
            """,
            static (dataSet, rowIndex) => new
            {
                SourceId = Assert.IsType<Guid>(dataSet["source_id", rowIndex]),
                SourceKind = Assert.IsType<string>(dataSet["source_kind", rowIndex]),
                TargetId = Assert.IsType<Guid>(dataSet["target_id", rowIndex]),
                TargetKind = Assert.IsType<string>(dataSet["target_kind", rowIndex]),
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Assert.IsType<string>(dataSet["properties", rowIndex])) ?? [],
            },
            new Dictionary<string, object?> { ["sourceId"] = symbolIds[0] });

        Assert.Equal(2L, memberOfCount);
        Assert.Equal(symbolIds[0], primaryRow.SourceId);
        Assert.Equal("symbol", primaryRow.SourceKind);
        Assert.Equal(businessClusterId, primaryRow.TargetId);
        Assert.Equal("cluster", primaryRow.TargetKind);
        Assert.Equal("primary", primaryRow.Properties["kind"].GetString());
    }

    [Fact]
    public async Task ReplaceClusterSummariesAtomicallyAsync_ReplacesClustersAndClearsOldMembershipEdges()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 1);
        Guid oldClusterId = Guid.NewGuid();
        Guid keptReferenceEdgeId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
            VALUES (@clusterId, 'Old cluster', 'old summary', @embedding::vector, 0.10, 'mixed', 0);

            INSERT INTO edges (id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
            VALUES
                (@memberEdgeId, @symbolId, 'symbol', @clusterId, 'cluster', 'MemberOf', 1.0, '["NameMatch"]'::jsonb, '{"kind":"primary"}'::jsonb),
                (@referenceEdgeId, @symbolId, 'symbol', @symbolId, 'symbol', 'References', 0.9, '["NameMatch"]'::jsonb, '{}'::jsonb);
            """,
            new Dictionary<string, object?>
            {
                ["clusterId"] = oldClusterId,
                ["embedding"] = PostgresGraphStoreFixture.ToVectorLiteral(CreateVector("old summary")),
                ["memberEdgeId"] = Guid.NewGuid(),
                ["referenceEdgeId"] = keptReferenceEdgeId,
                ["symbolId"] = symbolIds[0],
            });

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
                Summary = "Provides shared cross-cutting services.",
                Embedding = CreateVector("shared infrastructure"),
            },
        ],
        cancellationToken);

        long clusterCount = await fixture.CountAsync("SELECT COUNT(*) FROM clusters;");
        long oldClusterCount = await fixture.CountAsync("SELECT COUNT(*) FROM clusters WHERE id = @id;", new Dictionary<string, object?> { ["id"] = oldClusterId });
        long memberOfCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE edge_kind = 'MemberOf';");
        long referenceCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE id = @id;", new Dictionary<string, object?> { ["id"] = keptReferenceEdgeId });

        Assert.Equal(2L, clusterCount);
        Assert.Equal(0L, oldClusterCount);
        Assert.Equal(0L, memberOfCount);
        Assert.Equal(1L, referenceCount);
    }

    private static async Task<IReadOnlyList<Guid>> InsertSymbolGraphAsync(PostgresGraphStoreFixture fixture, int count)
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
            VALUES (@fileId, @repoId, @projectId, 'src\GraphRAG.Code\Clustering.cs', 'csharp', 'cluster-file', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
            });

        List<Guid> symbolIds = [];
        for (int index = 0; index < count; index++)
        {
            Guid symbolId = Guid.NewGuid();
            symbolIds.Add(symbolId);
            await fixture.ExecuteAsync(
                """
                INSERT INTO symbols (
                    id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                    embedding, content_hash, is_utility, source_range_start, source_range_end
                )
                VALUES (@id, @fileId, NULL, @name, @fqn, 'Class', NULL, NULL, NULL, NULL, @hash, FALSE, 1, 10);
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = symbolId,
                    ["fileId"] = fileId,
                    ["name"] = $"ClusterNode{index}",
                    ["fqn"] = $"Agency.GraphRAG.Code.ClusterNode{index}",
                    ["hash"] = $"cluster-node-{index}",
                });
        }

        return symbolIds;
    }

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
