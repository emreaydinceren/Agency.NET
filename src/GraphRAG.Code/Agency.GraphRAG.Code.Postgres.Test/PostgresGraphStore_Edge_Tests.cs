using Agency.GraphRAG.Code.Domain;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for edge persistence in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_Edge_Tests
{
    [Fact]
    public async Task UpsertEdgeBatchAsync_PersistsAllSupportedEdgeKinds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 6);
        Edge[] edges =
        [
            CreateEdge(symbolIds[0], symbolIds[1], EdgeKind.Contains, 0.91),
            CreateEdge(symbolIds[1], symbolIds[2], EdgeKind.DependsOn, 0.92),
            CreateEdge(symbolIds[2], symbolIds[3], EdgeKind.Imports, 0.93),
            CreateEdge(symbolIds[3], symbolIds[4], EdgeKind.References, 0.94),
            CreateEdge(symbolIds[4], symbolIds[5], EdgeKind.Defines, 0.95),
            CreateEdge(symbolIds[5], symbolIds[0], EdgeKind.MemberOf, 0.96, properties: new Dictionary<string, object?> { ["kind"] = "primary" }),
        ];

        await fixture.Store.UpsertEdgeBatchAsync(edges, cancellationToken);

        long totalCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges;");
        long distinctKinds = await fixture.CountAsync("SELECT COUNT(DISTINCT edge_kind) FROM edges;");

        Assert.Equal(edges.Length, totalCount);
        Assert.Equal(6L, distinctKinds);
    }

    [Fact]
    public async Task UpsertEdgeBatchAsync_PersistsSignalsAndJsonbProperties()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 2);
        Guid edgeId = Guid.NewGuid();

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            new Edge
            {
                Id = edgeId,
                SourceId = symbolIds[0],
                SourceKind = "symbol",
                TargetId = symbolIds[1],
                TargetKind = "symbol",
                EdgeKind = EdgeKind.References,
                Confidence = 0.87,
                Signals = [Signal.NameMatch, Signal.LlmExtraction, Signal.ExternalLikely],
                Properties = new Dictionary<string, object?>
                {
                    ["weight"] = 3,
                    ["reason"] = "semantic match",
                    ["active"] = true,
                },
            },
        ],
        cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT signals::text AS signals, properties::text AS properties
            FROM edges
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                Signals = Assert.IsType<string>(dataSet["signals", rowIndex]),
                Properties = Assert.IsType<string>(dataSet["properties", rowIndex]),
            },
            new Dictionary<string, object?> { ["id"] = edgeId });

        JsonElement[] signals = JsonSerializer.Deserialize<JsonElement[]>(row.Signals) ?? [];
        Dictionary<string, JsonElement> properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Properties) ?? [];

        Assert.Equal(
            new[] { "NameMatch", "LlmExtraction", "ExternalLikely" },
            signals.Select(signal => signal.GetString() ?? string.Empty).ToArray());
        Assert.Equal(3, properties["weight"].GetInt32());
        Assert.Equal("semantic match", properties["reason"].GetString());
        Assert.True(properties["active"].GetBoolean());
    }

    [Fact]
    public async Task UpsertEdgeBatchAsync_RoundTripsMemberOfKindProperty()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 2);
        Guid edgeId = Guid.NewGuid();

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            new Edge
            {
                Id = edgeId,
                SourceId = symbolIds[0],
                SourceKind = "symbol",
                TargetId = symbolIds[1],
                TargetKind = "cluster",
                EdgeKind = EdgeKind.MemberOf,
                Confidence = 1.0,
                Signals = [Signal.NameMatch],
                Properties = new Dictionary<string, object?> { ["kind"] = "member_of_primary" },
            },
        ],
        cancellationToken);

        var row = await fixture.QuerySingleAsync(
            """
            SELECT edge_kind, properties->>'kind' AS member_kind
            FROM edges
            WHERE id = @id;
            """,
            static (dataSet, rowIndex) => new
            {
                EdgeKind = Assert.IsType<string>(dataSet["edge_kind", rowIndex]),
                MemberKind = dataSet["member_kind", rowIndex] as string,
            },
            new Dictionary<string, object?> { ["id"] = edgeId });

        Assert.Equal("MemberOf", row.EdgeKind);
        Assert.Equal("member_of_primary", row.MemberKind);
    }

    private static Edge CreateEdge(Guid sourceId, Guid targetId, EdgeKind kind, double confidence, IReadOnlyDictionary<string, object?>? properties = null)
        => new()
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            SourceKind = "symbol",
            TargetId = targetId,
            TargetKind = "symbol",
            EdgeKind = kind,
            Confidence = confidence,
            Signals = [Signal.NameMatch],
            Properties = properties ?? new Dictionary<string, object?>(),
        };

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
            VALUES (@fileId, @repoId, @projectId, 'src\GraphRAG.Code\Edges.cs', 'csharp', 'edge-file', NULL);
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
                    ["name"] = $"EdgeNode{index}",
                    ["fqn"] = $"Agency.GraphRAG.Code.EdgeNode{index}",
                    ["hash"] = $"edge-node-{index}",
                });
        }

        return symbolIds;
    }
}
