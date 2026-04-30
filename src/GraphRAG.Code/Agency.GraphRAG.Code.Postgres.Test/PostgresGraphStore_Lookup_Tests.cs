using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Postgres.Migrations;
using System.Text;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Functional tests for lookup behavior in <c>PostgresGraphStore</c>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStore_Lookup_Tests
{
    [Fact]
    public async Task FindSymbolsByNameAsync_SupportsExactAndFuzzyMatches()
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
            CreateSymbol(paymentHelperId, fileId, "PaymentHelpers"),
            CreateSymbol(reportBuilderId, fileId, "ReportBuilder"),
        ],
        cancellationToken);

        IReadOnlyList<Symbol> exactMatches = await fixture.Store.FindSymbolsByNameAsync("PaymentHandler", cancellationToken);
        IReadOnlyList<Symbol> fuzzyMatches = await fixture.Store.FindSymbolsByNameAsync("PaymntHandlr", cancellationToken);

        Assert.Contains(exactMatches, symbol => symbol.Id == paymentHandlerId);
        Assert.DoesNotContain(exactMatches, symbol => symbol.Id == reportBuilderId);
        Assert.Contains(fuzzyMatches, symbol => symbol.Id == paymentHandlerId);
        Assert.DoesNotContain(fuzzyMatches, symbol => symbol.Id == reportBuilderId);
    }

    [Fact]
    public async Task GetSymbolByIdAsync_ReturnsNullWhenSymbolDoesNotExist()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await PostgresGraphStoreFixture.CreateAsync();

        Symbol? symbol = await fixture.Store.GetSymbolByIdAsync(Guid.NewGuid(), cancellationToken);

        Assert.Null(symbol);
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
            Embedding = CreateVector(name),
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 20,
        };

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
            VALUES (@fileId, @repoId, @projectId, 'src\GraphRAG.Code\Lookups.cs', 'csharp', 'lookup-file', NULL);
            """,
            new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["projectId"] = projectId,
                ["fileId"] = fileId,
            });

        return fileId;
    }
}
