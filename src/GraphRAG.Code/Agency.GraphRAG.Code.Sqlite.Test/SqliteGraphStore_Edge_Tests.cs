using Agency.GraphRAG.Code.Domain;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests edge persistence for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Edge_Tests
{
    [Fact]
    public async Task UpsertEdgeBatchAsync_InsertsAllSupportedEdgeKinds_AndUpdatesReferenceMetadata()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 6);
        Guid moduleId = Guid.NewGuid();
        Guid fileId = await GetSingleFileIdAsync(fixture);

        await fixture.ExecuteAsync(
            """
            INSERT INTO modules (id, file_id, project_id, name, path, kind)
            SELECT $moduleId, $fileId, project_id, 'Agency.GraphRAG.Code', NULL, 'namespace'
            FROM files
            WHERE id = $fileId;
            """,
            new Dictionary<string, object?>
            {
                ["$moduleId"] = moduleId.ToString("D"),
                ["$fileId"] = fileId.ToString("D"),
            });

        Guid containsId = Guid.NewGuid();
        Guid definesId = Guid.NewGuid();
        Guid importsId = Guid.NewGuid();
        Guid dependsOnId = Guid.NewGuid();
        Guid referencesId = Guid.NewGuid();
        Guid memberOfId = Guid.NewGuid();

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(containsId, fileId, "file", moduleId, "module", EdgeKind.Contains),
            CreateEdge(definesId, moduleId, "module", symbolIds[0], "symbol", EdgeKind.Defines),
            CreateEdge(importsId, fileId, "file", symbolIds[1], "symbol", EdgeKind.Imports),
            CreateEdge(dependsOnId, symbolIds[1], "symbol", symbolIds[2], "symbol", EdgeKind.DependsOn),
            CreateEdge(
                referencesId,
                symbolIds[2],
                "symbol",
                symbolIds[3],
                "symbol",
                EdgeKind.References,
                0.45,
                [Signal.NameMatch],
                new Dictionary<string, object?> { ["origin"] = "initial" }),
            CreateEdge(
                memberOfId,
                symbolIds[4],
                "symbol",
                symbolIds[5],
                "symbol",
                EdgeKind.MemberOf,
                properties: new Dictionary<string, object?> { ["kind"] = "primary" }),
        ],
        cancellationToken);

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(
                referencesId,
                symbolIds[2],
                "symbol",
                symbolIds[3],
                "symbol",
                EdgeKind.References,
                0.92,
                [Signal.NameMatch, Signal.LlmExtraction],
                new Dictionary<string, object?> { ["origin"] = "updated", ["line"] = 27 }),
        ],
        cancellationToken);

        long rowCount = await fixture.CountAsync("SELECT COUNT(*) FROM edges;");
        var referenceRow = await fixture.QuerySingleAsync(
            """
            SELECT edge_kind, confidence, signals, properties
            FROM edges
            WHERE id = $id;
            """,
            reader => new
            {
                EdgeKind = reader.GetString(0),
                Confidence = reader.GetDouble(1),
                Signals = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [],
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(3)) ?? [],
            },
            new Dictionary<string, object?> { ["$id"] = referencesId.ToString("D") });

        var memberOfRow = await fixture.QuerySingleAsync(
            """
            SELECT edge_kind, properties
            FROM edges
            WHERE id = $id;
            """,
            reader => new
            {
                EdgeKind = reader.GetString(0),
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(1)) ?? [],
            },
            new Dictionary<string, object?> { ["$id"] = memberOfId.ToString("D") });

        Assert.Equal(6L, rowCount);
        Assert.Equal(nameof(EdgeKind.References), referenceRow.EdgeKind);
        Assert.Equal(0.92, referenceRow.Confidence, 3);
        Assert.Equal(["NameMatch", "LlmExtraction"], referenceRow.Signals);
        Assert.Equal("updated", referenceRow.Properties["origin"].GetString());
        Assert.Equal(27, referenceRow.Properties["line"].GetInt32());
        Assert.Equal(nameof(EdgeKind.MemberOf), memberOfRow.EdgeKind);
        Assert.Equal("primary", memberOfRow.Properties["kind"].GetString());
    }

    private static Edge CreateEdge(
        Guid id,
        Guid sourceId,
        string sourceKind,
        Guid targetId,
        string targetKind,
        EdgeKind edgeKind,
        double confidence = 1.0,
        IReadOnlyList<Signal>? signals = null,
        IReadOnlyDictionary<string, object?>? properties = null)
        => new()
        {
            Id = id,
            SourceId = sourceId,
            SourceKind = sourceKind,
            TargetId = targetId,
            TargetKind = targetKind,
            EdgeKind = edgeKind,
            Confidence = confidence,
            Signals = signals ?? [Signal.NameMatch],
            Properties = properties ?? new Dictionary<string, object?>(),
        };

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
            VALUES ($fileId, $repoId, $projectId, 'src\GraphRAG.Code\SqliteGraphStore.cs', 'csharp', 'file-hash', NULL);
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
                VALUES ($id, $fileId, NULL, $name, $fqn, 'Method', NULL, NULL, NULL, NULL, $hash, 0, 1, 10);
                """,
                new Dictionary<string, object?>
                {
                    ["$id"] = symbolId.ToString("D"),
                    ["$fileId"] = fileId.ToString("D"),
                    ["$name"] = $"Symbol{index}",
                    ["$fqn"] = $"Agency.GraphRAG.Code.Symbol{index}",
                    ["$hash"] = $"symbol-{index}",
                });
        }

        return symbolIds;
    }

    private static Task<Guid> GetSingleFileIdAsync(SqliteGraphStoreFixture fixture)
        => fixture.QuerySingleAsync("SELECT id FROM files LIMIT 1;", reader => Guid.Parse(reader.GetString(0)));
}
