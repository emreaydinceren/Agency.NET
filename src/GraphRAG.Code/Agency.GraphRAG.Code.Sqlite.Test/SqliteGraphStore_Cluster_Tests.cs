using Agency.GraphRAG.Code.Domain;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests clustering persistence for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Cluster_Tests
{
    [Fact]
    public async Task ApplyClusterAssignmentsAsync_WritesMemberOfEdgesWithKindProperty()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
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
            SELECT source_id, source_kind, target_id, target_kind, properties
            FROM edges
            WHERE source_id = $sourceId;
            """,
            reader => new
            {
                SourceId = reader.GetString(0),
                SourceKind = reader.GetString(1),
                TargetId = reader.GetString(2),
                TargetKind = reader.GetString(3),
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(4)) ?? [],
            },
            new Dictionary<string, object?> { ["$sourceId"] = symbolIds[0].ToString("D") });

        Assert.Equal(2L, memberOfCount);
        Assert.Equal(symbolIds[0].ToString("D"), primaryRow.SourceId);
        Assert.Equal("symbol", primaryRow.SourceKind);
        Assert.Equal(businessClusterId.ToString("D"), primaryRow.TargetId);
        Assert.Equal("cluster", primaryRow.TargetKind);
        Assert.Equal("primary", primaryRow.Properties["kind"].GetString());
    }

    [Fact]
    public async Task ReplaceClusterSummariesAtomicallyAsync_ReplacesClustersAndClearsOldMembershipEdges()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 1);
        Guid oldClusterId = Guid.NewGuid();
        Guid keptReferenceEdgeId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
            VALUES ($clusterId, 'Old cluster', 'old summary', '[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8]', 0.10, 'mixed', 0);

            INSERT INTO edges (id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
            VALUES
                ($memberEdgeId, $symbolId, 'symbol', $clusterId, 'cluster', 'MemberOf', 1.0, '["NameMatch"]', '{"kind":"primary"}'),
                ($referenceEdgeId, $symbolId, 'symbol', $symbolId, 'symbol', 'References', 0.9, '["NameMatch"]', '{}');
            """,
            new Dictionary<string, object?>
            {
                ["$clusterId"] = oldClusterId.ToString("D"),
                ["$memberEdgeId"] = Guid.NewGuid().ToString("D"),
                ["$referenceEdgeId"] = keptReferenceEdgeId.ToString("D"),
                ["$symbolId"] = symbolIds[0].ToString("D"),
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
                Embedding = FakeEmbeddingGenerator.CreateVector("payments orchestration"),
            },
            new Agency.GraphRAG.Code.Domain.Cluster
            {
                Id = Guid.NewGuid(),
                Label = "Infrastructure",
                Type = ClusterType.Infrastructure,
                CoherenceScore = 0.82,
                Summary = "Provides shared cross-cutting services.",
                Embedding = FakeEmbeddingGenerator.CreateVector("shared infrastructure"),
            },
        ],
        cancellationToken);

        long clusterCount = await fixture.CountAsync("SELECT COUNT(*) FROM clusters;");
        long oldClusterCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM clusters WHERE id = $id;",
            new Dictionary<string, object?> { ["$id"] = oldClusterId.ToString("D") });
        long memberOfCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges WHERE edge_kind = 'MemberOf';");
        long referenceCount = await fixture.CountAsync(
            "SELECT COUNT(*) FROM edges WHERE id = $id;",
            new Dictionary<string, object?> { ["$id"] = keptReferenceEdgeId.ToString("D") });

        Assert.Equal(2L, clusterCount);
        Assert.Equal(0L, oldClusterCount);
        Assert.Equal(0L, memberOfCount);
        Assert.Equal(1L, referenceCount);
    }

    private static async Task<IReadOnlyList<Guid>> InsertSymbolGraphAsync(SqliteGraphStoreFixture fixture, int count)
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
            VALUES ($fileId, $repoId, $projectId, 'src\GraphRAG.Code\Clustering.cs', 'csharp', 'file-hash', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["$repoId"] = repoId.ToString("D"),
                ["$projectId"] = projectId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
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
                VALUES ($id, $fileId, NULL, $name, $fqn, 'Class', NULL, NULL, NULL, NULL, $hash, 0, 1, 10);
                """,
                new Dictionary<string, object?>
                {
                    ["$id"] = symbolId.ToString("D"),
                    ["$fileId"] = fileId.ToString("D"),
                    ["$name"] = $"ClusterNode{index}",
                    ["$fqn"] = $"Agency.GraphRAG.Code.ClusterNode{index}",
                    ["$hash"] = $"cluster-node-{index}",
                });
        }

        return symbolIds;
    }
}
