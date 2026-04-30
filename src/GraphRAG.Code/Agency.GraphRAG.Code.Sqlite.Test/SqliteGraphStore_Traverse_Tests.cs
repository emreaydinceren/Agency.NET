using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests traversal behavior for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Traverse_Tests
{
    [Fact]
    public async Task TraverseFromAsync_FollowsOutgoingEdgesAcrossThreeHops()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 4);

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(symbolIds[0], symbolIds[1], EdgeKind.References, 0.95),
            CreateEdge(symbolIds[1], symbolIds[2], EdgeKind.References, 0.90),
            CreateEdge(symbolIds[2], symbolIds[3], EdgeKind.References, 0.85),
        ],
        cancellationToken);

        IReadOnlyList<TraversalHop> results = await fixture.Store.TraverseFromAsync(
            new TraversalRequest
            {
                SeedSymbolIds = [symbolIds[0]],
                MaxHops = 3,
            },
            cancellationToken);

        Assert.Contains(results, hop => hop.SymbolId == symbolIds[1] && hop.Depth == 1);
        Assert.Contains(results, hop => hop.SymbolId == symbolIds[2] && hop.Depth == 2);
        Assert.Contains(results, hop => hop.SymbolId == symbolIds[3] && hop.Depth == 3);
    }

    [Fact]
    public async Task TraverseFromAsync_RespectsEdgeKindFilterAndConfidenceThreshold()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 4);

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(symbolIds[0], symbolIds[1], EdgeKind.References, 0.95),
            CreateEdge(symbolIds[1], symbolIds[2], EdgeKind.DependsOn, 0.99),
            CreateEdge(symbolIds[0], symbolIds[3], EdgeKind.References, 0.40),
        ],
        cancellationToken);

        IReadOnlyList<TraversalHop> results = await fixture.Store.TraverseFromAsync(
            new TraversalRequest
            {
                SeedSymbolIds = [symbolIds[0]],
                EdgeKinds = [EdgeKind.References],
                MinConfidence = 0.90,
                MaxHops = 2,
            },
            cancellationToken);

        Assert.Contains(results, hop => hop.SymbolId == symbolIds[1] && hop.Depth == 1);
        Assert.DoesNotContain(results, hop => hop.SymbolId == symbolIds[2]);
        Assert.DoesNotContain(results, hop => hop.SymbolId == symbolIds[3]);
    }

    [Fact]
    public async Task TraverseFromAsync_CanTraverseIncomingDirection()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        IReadOnlyList<Guid> symbolIds = await InsertSymbolGraphAsync(fixture, 3);

        await fixture.Store.UpsertEdgeBatchAsync(
        [
            CreateEdge(symbolIds[0], symbolIds[1], EdgeKind.References, 0.90),
            CreateEdge(symbolIds[1], symbolIds[2], EdgeKind.References, 0.90),
        ],
        cancellationToken);

        IReadOnlyList<TraversalHop> results = await fixture.Store.TraverseFromAsync(
            new TraversalRequest
            {
                SeedSymbolIds = [symbolIds[1]],
                Direction = TraversalDirection.Incoming,
                MaxHops = 1,
            },
            cancellationToken);

        Assert.Contains(results, hop => hop.SymbolId == symbolIds[0] && hop.Depth == 1);
        Assert.DoesNotContain(results, hop => hop.SymbolId == symbolIds[2] && hop.Depth == 1);
    }

    private static Edge CreateEdge(Guid sourceId, Guid targetId, EdgeKind kind, double confidence)
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
            Properties = new Dictionary<string, object?>(),
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
            VALUES ($fileId, $repoId, $projectId, 'src\GraphRAG.Code\Traversal.cs', 'csharp', 'file-hash', NULL);
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
                    ["$name"] = $"Node{index}",
                    ["$fqn"] = $"Agency.GraphRAG.Code.Node{index}",
                    ["$hash"] = $"node-{index}",
                });
        }

        return symbolIds;
    }
}
