using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Tests unresolved-call-site staging for <c>SqliteGraphStore</c>.
/// </summary>
public sealed class SqliteGraphStore_Staging_Tests
{
    [Fact]
    public async Task StageAndDrainUnresolvedCallSitesAsync_SupportsPerFileDrain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
        (Guid firstFileId, Guid secondFileId, Guid firstSymbolId, Guid secondSymbolId) = await InsertSourceGraphAsync(fixture);

        UnresolvedCallSite first = new()
        {
            Id = Guid.NewGuid(),
            SourceSymbolId = firstSymbolId,
            SourceFileId = firstFileId,
            Identifier = "ResolveOrder",
            Scope = "Agency.GraphRAG.Code",
            LlmExtractedTarget = "Agency.GraphRAG.Code.OrderResolver.Resolve",
        };
        UnresolvedCallSite second = new()
        {
            Id = Guid.NewGuid(),
            SourceSymbolId = secondSymbolId,
            SourceFileId = secondFileId,
            Identifier = "PublishInvoice",
            Scope = "Agency.GraphRAG.Code",
            LlmExtractedTarget = "Agency.GraphRAG.Code.Invoices.InvoicePublisher.Publish",
        };

        await fixture.Store.StageUnresolvedCallSiteBatchAsync([first, second], cancellationToken);

        IReadOnlyList<UnresolvedCallSite> drained = await fixture.Store.DrainUnresolvedCallSitesAsync(firstFileId, cancellationToken);

        Assert.Single(drained);
        Assert.Equal(first.Id, drained[0].Id);
        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM unresolved_call_sites;"));
        Assert.Equal(1L, await fixture.CountAsync(
            "SELECT COUNT(*) FROM unresolved_call_sites WHERE source_file_id = $fileId;",
            new Dictionary<string, object?> { ["$fileId"] = secondFileId.ToString("D") }));
    }

    [Fact]
    public async Task StageAndDrainUnresolvedCallSitesAsync_SupportsGlobalDrain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteGraphStoreFixture.CreateAsync();
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

        IReadOnlyList<UnresolvedCallSite> drained = await fixture.Store.DrainUnresolvedCallSitesAsync(cancellationToken: cancellationToken);

        Assert.Equal(2, drained.Count);
        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM unresolved_call_sites;"));
    }

    private static async Task<(Guid FirstFileId, Guid SecondFileId, Guid FirstSymbolId, Guid SecondSymbolId)> InsertSourceGraphAsync(SqliteGraphStoreFixture fixture)
    {
        Guid repoId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid firstFileId = Guid.NewGuid();
        Guid secondFileId = Guid.NewGuid();
        Guid firstSymbolId = Guid.NewGuid();
        Guid secondSymbolId = Guid.NewGuid();

        await fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES ($repoId, 'https://example.test/repo.git', 'E:\Repos\Agency', NULL, NULL, 0);

            INSERT INTO projects (id, repo_id, name, relative_path, manifest_path, language, ecosystem)
            VALUES ($projectId, $repoId, 'Agency.GraphRAG.Code', 'src\GraphRAG.Code', 'src\GraphRAG.Code\Agency.GraphRAG.Code.csproj', 'csharp', NULL);

            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            VALUES
                ($firstFileId, $repoId, $projectId, 'src\GraphRAG.Code\OrderResolver.cs', 'csharp', 'file-1', NULL),
                ($secondFileId, $repoId, $projectId, 'src\GraphRAG.Code\InvoicePublisher.cs', 'csharp', 'file-2', NULL);

            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES
                ($firstSymbolId, $firstFileId, NULL, 'OrderResolver', 'Agency.GraphRAG.Code.OrderResolver', 'Class', NULL, NULL, NULL, NULL, 'symbol-1', 0, 1, 10),
                ($secondSymbolId, $secondFileId, NULL, 'InvoicePublisher', 'Agency.GraphRAG.Code.InvoicePublisher', 'Class', NULL, NULL, NULL, NULL, 'symbol-2', 0, 1, 10);
            """,
            new Dictionary<string, object?>
            {
                ["$repoId"] = repoId.ToString("D"),
                ["$projectId"] = projectId.ToString("D"),
                ["$firstFileId"] = firstFileId.ToString("D"),
                ["$secondFileId"] = secondFileId.ToString("D"),
                ["$firstSymbolId"] = firstSymbolId.ToString("D"),
                ["$secondSymbolId"] = secondSymbolId.ToString("D"),
            });

        return (firstFileId, secondFileId, firstSymbolId, secondSymbolId);
    }
}
