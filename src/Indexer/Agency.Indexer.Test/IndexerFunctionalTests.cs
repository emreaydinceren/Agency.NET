using Agency.Embeddings.OpenAI;
using Microsoft.Extensions.Configuration;

namespace Agency.Indexer.Test;

/// <summary>
/// Functional tests that index real documents with the real embedding model served by LM Studio (through the
/// test cache proxy configured in <c>shared-test-appsettings.json</c>) and assert on search quality, which the
/// bag-of-words fake used by the other tests cannot do.
/// Run with: dotnet test --filter "Category=Functional"
/// Requires LM Studio with text-embedding-qwen3-embedding-0.6b loaded.
/// </summary>
[Trait("Category", "Functional")]
[Trait("Category", "RequiresLlm")]
public sealed class IndexerFunctionalTests : IDisposable
{
    private const string Index = "functional";

    private readonly TempDirectory _dir = new();
    private readonly string _docs;
    private readonly string _database;
    private readonly EmbeddingOptions _embedding = LoadEmbeddingOptions();

    /// <summary>Creates a docs folder and a database path inside a fresh temporary directory.</summary>
    public IndexerFunctionalTests()
    {
        this._docs = Path.Combine(this._dir.Path, "docs");
        this._database = Path.Combine(this._dir.Path, "db", "index.db");
        Directory.CreateDirectory(this._docs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose() => this._dir.Dispose();

    /// <summary>
    /// Verifies that paraphrased questions, which share no keywords with the documents, still retrieve the
    /// document about the same topic as the best hit.
    /// </summary>
    [Theory]
    [InlineData("how do I make bread at home?", "sourdough.md")]
    [InlineData("protecting database data against loss", "backup.md")]
    [InlineData("staying safe when out on a boat", "kayak.md")]
    [InlineData("when must I send in my income declaration?", "tax.md")]
    public async Task SearchAsync_ParaphrasedQuestion_RanksTheMatchingDocumentFirst(string query, string expectedFile)
    {
        this.WriteCorpus();
        IndexService service = await this.CreateServiceAsync();
        IndexResult indexed = await service.IndexAsync(Request(this._docs), Ct);
        Assert.Equal(IndexStatus.Ok, indexed.Status);
        Assert.Equal(4, indexed.Added.Count);

        IReadOnlyList<SearchResultHit> hits = await service.SearchAsync(Index, query, top: 4, Ct);

        Assert.Equal(expectedFile, Path.GetFileName(hits[0].Path));
        Assert.True(hits[0].Score > hits[^1].Score, "The best hit should score above the worst one.");
    }

    /// <summary>
    /// Verifies the incremental path with real embeddings: a rewritten file is searchable by its new topic and
    /// no longer by its old one, and a deleted file stops appearing in results.
    /// </summary>
    [Fact]
    public async Task IndexAsync_FileRewrittenAndAnotherDeleted_SearchReflectsTheChanges()
    {
        this.WriteCorpus();
        IndexService service = await this.CreateServiceAsync();
        await service.IndexAsync(Request(this._docs), Ct);

        File.WriteAllText(
            Path.Combine(this._docs, "sourdough.md"),
            "# Hiking\nPack waterproof boots, a map and plenty of water before climbing the mountain trail.");
        File.SetLastWriteTimeUtc(Path.Combine(this._docs, "sourdough.md"), DateTime.UtcNow.AddMinutes(5));
        File.Delete(Path.Combine(this._docs, "tax.md"));

        IndexResult second = await service.IndexAsync(Request(root: null), Ct);

        Assert.Equal(IndexStatus.Ok, second.Status);
        Assert.Equal(["sourdough.md"], second.Changed.Select(Path.GetFileName));
        Assert.Equal(["tax.md"], second.Removed.Select(Path.GetFileName));

        IReadOnlyList<SearchResultHit> hiking = await service.SearchAsync(Index, "what to bring on a mountain walk", top: 4, Ct);
        Assert.Equal("sourdough.md", Path.GetFileName(hiking[0].Path));
        Assert.Contains("boots", hiking[0].Text);

        IReadOnlyList<SearchResultHit> everything = await service.SearchAsync(Index, "annual tax return deadline", top: 10, Ct);
        Assert.DoesNotContain(everything, hit => Path.GetFileName(hit.Path) == "tax.md");
    }

    private static IndexRequest Request(string? root) =>
        new(Index, root, Extensions: null, Names: null, MaxFileBytes: 1024 * 1024, Wait: false);

    private static EmbeddingOptions LoadEmbeddingOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddSharedConfiguration("shared-test-appsettings.json")
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<IndexerFunctionalTests>(optional: true)
            .AddEnvironmentVariables()
            .AddPlaceholderResolver()
            .Build();

        var options = new EmbeddingOptions();
        configuration.GetSection(EmbeddingOptions.SectionName).Bind(options);
        return options;
    }

    private Task<IndexService> CreateServiceAsync() =>
        Program.CreateServiceAsync(
            new IndexerSettings(StorageProvider.Sqlite, this._database, this._embedding, ChunkSize: 512, ChunkOverlap: 64),
            new EmbeddingGenerator(this._embedding),
            Ct);

    private void WriteCorpus()
    {
        this._dir.Write("docs/sourdough.md", "# Sourdough\nMix flour, water and starter, let the dough proof overnight, then bake it in a hot dutch oven.");
        this._dir.Write("docs/backup.md", "# Postgres backups\nRun pg_dump every night and test restoring the dump on a spare server each week.");
        this._dir.Write("docs/kayak.md", "# Kayaking\nAlways wear a life jacket and check the tide table before paddling out onto the water.");
        this._dir.Write("docs/tax.md", "# Taxes\nFile your annual return before the April deadline and keep receipts for every deduction.");
    }
}
