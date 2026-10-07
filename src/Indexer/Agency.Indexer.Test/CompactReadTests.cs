namespace Agency.Indexer.Test;

/// <summary>Tests for compact-by-default search, the top-hit hint, the <c>read</c> command and the split skill.</summary>
public sealed class CompactReadTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _docs;
    private readonly string _database;
    private readonly FakeEmbeddingGenerator _embeddings = new();

    /// <summary>Creates a docs folder and a database path inside a fresh temporary directory.</summary>
    public CompactReadTests()
    {
        this._docs = Path.Combine(this._dir.Path, "docs");
        this._database = Path.Combine(this._dir.Path, "db", "index.db");
        Directory.CreateDirectory(this._docs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose() => this._dir.Dispose();

    /// <summary>Verifies the CLI leaves chunk text out by default and restores it with <c>--full</c> or <c>--snippet-chars</c>.</summary>
    [Fact]
    public async Task Search_TextIsOmittedUnlessFullOrSnippet()
    {
        this.Write("release.md", "How to publish a release: tag the commit and push the tag.");
        IndexService service = await this.IndexedAsync();

        SearchResponse compact = await this.SearchAsync(service, "--query", "publish a release tag");
        SearchResponse full = await this.SearchAsync(service, "--query", "publish a release tag", "--full");
        SearchResponse snippet = await this.SearchAsync(service, "--query", "publish a release tag", "--snippet-chars", "10");

        Assert.Null(compact.Hits[0].Text);
        Assert.Equal("How to publish a release: tag the commit and push the tag.", full.Hits[0].Text);
        Assert.Equal("How to pub", snippet.Hits[0].Text);
    }

    /// <summary>Verifies the hint appears only when the top hit clearly leads, and <c>top_gap</c> needs two hits.</summary>
    [Fact]
    public void From_TopHitLeads_AddsHint()
    {
        var options = new SearchOptions(null, null, false, null);
        SearchResultHit Hit(double score) => new("p", 0, score, "t");

        SearchResponse leads = SearchResponse.From("docs", [Hit(0.81), Hit(0.62)], options);
        SearchResponse close = SearchResponse.From("docs", [Hit(0.81), Hit(0.78)], options);
        SearchResponse single = SearchResponse.From("docs", [Hit(0.9)], options);

        Assert.Equal(0.19, leads.TopGap);
        Assert.Contains("stop searching", leads.Hint);
        Assert.Equal(0.03, close.TopGap);
        Assert.Null(close.Hint);
        Assert.Null(single.TopGap);
        Assert.Null(single.Hint);
    }

    /// <summary>Verifies read returns the requested lines, clamps the end, and flags a changed file as stale.</summary>
    [Fact]
    public async Task ReadAsync_ReturnsLineRange_AndFlagsStaleFiles()
    {
        string path = this.Write("notes.md", "one\ntwo\nthree\nfour");
        IndexService service = await this.IndexedAsync();

        ReadResult middle = await service.ReadAsync("docs", path, 2, 3, Ct);
        ReadResult relative = await service.ReadAsync("docs", "notes.md", 1, 1, Ct);
        ReadResult clamped = await service.ReadAsync("docs", path, 3, 99, Ct);
        this.Write("notes.md", "one\ntwo\nthree\nfour\nfive", touchSeconds: 9);
        ReadResult stale = await service.ReadAsync("docs", path, 1, 1, Ct);

        Assert.Equal("two\nthree", middle.Text);
        Assert.False(middle.Stale);
        Assert.Equal("one", relative.Text);
        Assert.Equal((3, 4, "three\nfour"), (clamped.StartLine, clamped.EndLine, clamped.Text));
        Assert.True(stale.Stale);
    }

    /// <summary>Verifies read refuses files outside the index and ranges outside the file.</summary>
    [Fact]
    public async Task ReadAsync_InvalidRequests_ThrowUsageException()
    {
        string path = this.Write("notes.md", "one\ntwo");
        string outside = this._dir.Write("secret.txt", "nope");
        IndexService service = await this.IndexedAsync();

        await Assert.ThrowsAsync<UsageException>(() => service.ReadAsync("docs", outside, 1, 1, Ct));
        await Assert.ThrowsAsync<UsageException>(() => service.ReadAsync("docs", path, 5, 6, Ct));
        await Assert.ThrowsAsync<UsageException>(() => service.ReadAsync("missing", path, 1, 1, Ct));
    }

    /// <summary>Verifies install writes the reference beside the skill and uninstall removes both.</summary>
    [Fact]
    public async Task Skill_InstallWritesReference_UninstallRemovesBoth()
    {
        using var dir = new TempDirectory();
        string root = Path.Combine(dir.Path, "a");

        await SkillInstaller.InstallAsync([root], Ct);
        string folder = Path.Combine(root, SkillInstaller.SkillName);
        Assert.True(File.Exists(Path.Combine(folder, "REFERENCE.md")));

        SkillInstaller.Uninstall([root]);
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>Verifies the skill stays short; detail belongs in <c>REFERENCE.md</c>, which agents load on demand.</summary>
    [Fact]
    public async Task SkillMd_StaysUnderBudget()
    {
        string content = await SkillInstaller.ReadSkillAsync(Ct);

        Assert.True(content.Length < 1500, $"SKILL.md is {content.Length} characters.");
    }

    private async Task<IndexService> IndexedAsync()
    {
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(new IndexRequest("docs", this._docs, null, null, FileScanner.DefaultMaxFileBytes, Wait: false), Ct);
        return service;
    }

    private Task<SearchResponse> SearchAsync(IndexService service, params string[] extra)
    {
        string[] argv = ["search", "--index", "docs", .. extra];
        var args = CliArguments.Parse(argv);
        return Program.SearchAsync(service, IndexerSettings.Resolve(args, Path.Combine(this._dir.Path, "home")), args, Ct);
    }

    private string Write(string relativePath, string content, int touchSeconds = 1)
    {
        string path = this._dir.Write(Path.Combine("docs", relativePath), content);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(touchSeconds));
        return path;
    }
}
