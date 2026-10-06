using System.Text.Json;

namespace Agency.Indexer.Test;

/// <summary>
/// Tests for exclusion globs, rebuilds, chunk locations, grouped and hybrid search, calibration, output shaping and the
/// dry-run estimate.
/// </summary>
public sealed class FeedbackBatchTests : IDisposable
{
    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TempDirectory _dir = new();
    private readonly string _docs;
    private readonly string _database;
    private readonly FakeEmbeddingGenerator _embeddings = new();

    /// <summary>Creates a docs folder and a database path inside a fresh temporary directory.</summary>
    public FeedbackBatchTests()
    {
        this._docs = Path.Combine(this._dir.Path, "docs");
        this._database = Path.Combine(this._dir.Path, "db", "index.db");
        Directory.CreateDirectory(this._docs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose() => this._dir.Dispose();

    /// <summary>Verifies the glob forms: a bare name at any depth, a folder prefix, <c>**</c>, <c>*</c> staying inside a segment.</summary>
    [Theory]
    [InlineData("manual-tests", "docs/manual-tests/a.md", true)]
    [InlineData("manual-tests", "manual-tests", true)]
    [InlineData("manual-tests", "docs/manual-tests-extra/a.md", false)]
    [InlineData("docs/manual-tests", "docs/manual-tests/deep/a.md", true)]
    [InlineData("docs/manual-tests", "other/docs/manual-tests/a.md", false)]
    [InlineData("**/*.draft.md", "a/b/x.draft.md", true)]
    [InlineData("**/*.draft.md", "x.md", false)]
    [InlineData("docs/*.md", "docs/a.md", true)]
    [InlineData("docs/*.md", "docs/sub/a.md", false)]
    [InlineData("docs/**/a.md", "docs/a.md", true)]
    [InlineData("docs/**/a.md", "docs/x/y/a.md", true)]
    [InlineData("./Docs/ADR/", "docs/adr/0006.md", true)]
    public void GlobFilter_Matches(string glob, string path, bool expected) =>
        Assert.Equal(expected, new GlobFilter([glob]).Matches(path));

    /// <summary>Verifies an empty or blank filter matches nothing.</summary>
    [Fact]
    public void GlobFilter_Empty_MatchesNothing()
    {
        Assert.False(new GlobFilter(null).Matches("a.md"));
        Assert.False(new GlobFilter(["  "]).Matches("a.md"));
    }

    /// <summary>Verifies the scanner leaves out excluded folders and files.</summary>
    [Fact]
    public void FileScanner_Exclude_SkipsFoldersAndFiles()
    {
        this.Write("keep.md", "keep");
        this.Write("manual-tests/a.md", "skip");
        this.Write("sub/b.draft.md", "skip");
        this.Write("sub/c.md", "keep");

        ScanResult result = FileScanner.Scan(new ScanOptions(this._docs, FileScanner.DefaultExtensions, FileScanner.DefaultNames, 1024, ["manual-tests", "**/*.draft.md"]));

        Assert.Equal(["keep.md", Path.Combine("sub", "c.md")], result.Files.Select(f => Path.GetRelativePath(this._docs, f.Path)));
    }

    /// <summary>Verifies the repo file may carry an Exclude list, and it is resolved like the other defaults.</summary>
    [Fact]
    public void RepoConfig_Exclude_IsAllowedAndResolved()
    {
        string file = this._dir.Write("repo/.agency-index.json", """{ "Index": "docs", "Exclude": ["docs/manual-tests", "**/*.draft.md"] }""");

        RepoConfig config = RepoConfig.Load(file);

        Assert.Equal("docs/manual-tests,**/*.draft.md", config.Values["Exclude"]);
        Assert.Empty(config.Ignored);
    }

    /// <summary>Verifies adding an exclusion removes the files it covers from the index, and the setting is remembered.</summary>
    [Fact]
    public async Task IndexAsync_ExcludeAdded_RemovesCoveredFilesAndRemembersTheSetting()
    {
        string keep = this.Write("keep.md", "alpha");
        string noisy = this.Write("manual-tests/run.md", "beta");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        IndexResult result = await service.IndexAsync(Request() with { Exclude = ["manual-tests"] }, Ct);
        IndexResult again = await service.IndexAsync(Request(), Ct);

        Assert.Equal([noisy], result.Removed);
        Assert.Empty(again.Removed);
        var (config, files) = await service.ListAsync("docs", Ct);
        Assert.Equal(["manual-tests"], config.Excludes);
        Assert.Equal([keep], files.Select(f => f.Path));
    }

    /// <summary>Verifies a rebuild re-embeds unchanged files under a new model, which a plain run refuses.</summary>
    [Fact]
    public async Task IndexAsync_Rebuild_ReembedsEverythingAndSwitchesModel()
    {
        this.Write("a.md", "alpha document");
        this.Write("b.md", "beta document");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);
        await service.CalibrateAsync("docs", save: true, Ct);
        IndexService other = await Services.SqliteAsync(this._database, this._embeddings, model: "other-model");

        await Assert.ThrowsAsync<UsageException>(() => other.IndexAsync(Request(), Ct));
        this._embeddings.EmbeddedInputs.Clear();
        IndexResult rebuilt = await other.IndexAsync(Request() with { Rebuild = true }, Ct);

        Assert.Equal(IndexStatus.Ok, rebuilt.Status);
        Assert.Equal(2, rebuilt.Added.Count);
        Assert.Equal(2, this._embeddings.EmbeddedInputs.Count);
        var (config, _) = await other.ListAsync("docs", Ct);
        Assert.Equal("other-model", config.EmbeddingModel);
        Assert.Null(config.Calibration);
        Assert.NotEmpty(await other.SearchAsync("docs", "alpha", 1, Ct));
    }

    /// <summary>Verifies a rebuild drops the chunks of files deleted since, and refuses an index that does not exist.</summary>
    [Fact]
    public async Task IndexAsync_Rebuild_RemovesVanishedFilesAndRejectsMissingIndex()
    {
        this.Write("keep.md", "alpha");
        string gone = this.Write("gone.md", "alpha too");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await Assert.ThrowsAsync<UsageException>(() => service.IndexAsync(Request(root: this._docs) with { Rebuild = true }, Ct));
        await service.IndexAsync(Request(root: this._docs), Ct);
        File.Delete(gone);

        IndexResult rebuilt = await service.IndexAsync(Request() with { Rebuild = true }, Ct);

        Assert.Equal([gone], rebuilt.Removed);
        Assert.All(await service.SearchAsync("docs", "alpha", 10, Ct), h => Assert.NotEqual(gone, h.Path));
    }

    /// <summary>Verifies searching with a different model than the index was built with explains the fix instead of returning noise.</summary>
    [Fact]
    public async Task SearchAsync_ModelDiffers_Throws()
    {
        this.Write("a.md", "alpha");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);
        IndexService other = await Services.SqliteAsync(this._database, this._embeddings, model: "other-model");

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() => other.SearchAsync("docs", "alpha", 1, Ct));

        Assert.Contains("--rebuild", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies every markdown hit carries its heading path and the lines it spans.</summary>
    [Fact]
    public async Task SearchAsync_MarkdownHit_CarriesHeadingAndLineRange()
    {
        var lines = new List<string> { "# Guide", "" };
        lines.AddRange(Enumerable.Range(0, 40).SelectMany(i => new[] { $"Overview sentence number {i} about general matters of the guide.", "" }));
        lines.Add("## Install");
        lines.Add("");
        lines.AddRange(Enumerable.Range(0, 20).SelectMany(i => new[] { $"Install step {i} describes copying files to the machine.", "" }));
        lines.Add("The zebrafish marker sits in the middle of the install section.");
        lines.Add("");
        lines.AddRange(Enumerable.Range(20, 20).SelectMany(i => new[] { $"Install step {i} describes copying files to the machine.", "" }));
        this.Write("guide.md", string.Join("\n", lines));
        int markerLine = lines.FindIndex(l => l.Contains("zebrafish", StringComparison.Ordinal)) + 1;
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        IReadOnlyList<SearchResultHit> hits = await service.SearchAsync("docs", "zebrafish marker middle install section", 1, Ct);

        SearchResultHit hit = Assert.Single(hits);
        Assert.Equal("Guide > Install", hit.Heading);
        Assert.True(hit.StartLine <= markerLine && markerLine <= hit.EndLine, $"marker on line {markerLine} but hit spans {hit.StartLine}-{hit.EndLine}");
    }

    /// <summary>Verifies the locator reports no location rather than a wrong one for text it cannot find.</summary>
    [Fact]
    public void ChunkLocator_UnknownText_HasNoLocation()
    {
        IReadOnlyList<ChunkLocation> where = ChunkLocator.Locate("# A\n\nsome text\n", ["not in the document"], markdown: true);

        Assert.Null(where[0].StartLine);
        Assert.Null(where[0].Heading);
    }

    /// <summary>Verifies headings inside code fences are not headings, and a deeper heading nests under its parent.</summary>
    [Fact]
    public void ChunkLocator_Headings_NestAndIgnoreFences()
    {
        const string Content = "# One\n\n```\n# not a heading\n```\n\n## Two\n\ntext under two\n\n# Three\n\ntext under three\n";

        IReadOnlyList<ChunkLocation> where = ChunkLocator.Locate(Content, ["text under two", "text under three"], markdown: true);

        Assert.Equal("One > Two", where[0].Heading);
        Assert.Equal(9, where[0].StartLine);
        Assert.Equal("Three", where[1].Heading);
    }

    /// <summary>Verifies <c>--path</c> keeps only matching files.</summary>
    [Fact]
    public async Task SearchAsync_PathGlob_KeepsOnlyMatchingFiles()
    {
        this.Write("adr/0006.md", "reply limit per room");
        this.Write("manual-tests/run.md", "reply limit per room");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        IReadOnlyList<SearchResultHit> hits = await service.SearchAsync("docs", "reply limit per room", 5, Ct, pathGlob: "adr");

        Assert.Equal(Path.Combine(this._docs, "adr", "0006.md"), Assert.Single(hits).Path);
    }

    /// <summary>Verifies calibration reports a noise ceiling, stores a suggestion only when asked, and an index keeps it across runs.</summary>
    [Fact]
    public async Task CalibrateAsync_SavesSuggestionOnlyWhenAsked_AndKeepsItAcrossRuns()
    {
        this.Write("a.md", "release engineering and versioning of packages");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);

        CalibrationResult preview = await service.CalibrateAsync("docs", save: false, Ct);
        Assert.Null(await service.GetSuggestedMinScoreAsync("docs", Ct));
        CalibrationResult saved = await service.CalibrateAsync("docs", save: true, Ct);
        await service.IndexAsync(Request(), Ct);

        Assert.Equal(12, preview.Probes);
        Assert.False(preview.Saved);
        Assert.True(saved.SuggestedMinScore >= saved.NoiseCeiling);
        Assert.Equal(saved.SuggestedMinScore, await service.GetSuggestedMinScoreAsync("docs", Ct));
    }

    /// <summary>Verifies a per-file cap and a top limit apply after the score filter, in rank order.</summary>
    [Fact]
    public void SearchResponse_PerFileAndTop_CapAfterFiltering()
    {
        SearchResultHit[] hits =
        [
            new("a.md", 0, 0.9, "a0"), new("a.md", 1, 0.8, "a1"), new("b.md", 0, 0.7, "b0"), new("a.md", 2, 0.6, "a2"), new("c.md", 0, 0.5, "c0"),
        ];

        SearchResponse grouped = SearchResponse.From("docs", hits, new SearchOptions(0.55, null, false, null, PerFile: 1, Top: 2));

        Assert.Equal(["a0", "b0"], grouped.Hits.Select(h => h.Text));
        Assert.Equal(1, grouped.Filtered);
        Assert.Equal(0.55, grouped.MinScore);
    }

    /// <summary>Verifies the hit's location and index fields are printed only when present.</summary>
    [Fact]
    public void SearchResponse_Location_IsPrintedOnlyWhenKnown()
    {
        SearchResultHit[] hits = [new("a.md", 0, 0.9, "t", "Guide > Install", 3, 9, "docs"), new("b.md", 0, 0.8, "t")];

        string json = JsonSerializer.Serialize(SearchResponse.From("docs", hits, new SearchOptions(null, null, false, null)), SnakeCase);

        Assert.Contains("\"heading\":\"Guide > Install\"", json, StringComparison.Ordinal);
        Assert.Contains("\"start_line\":3", json, StringComparison.Ordinal);
        Assert.Equal(json.IndexOf("start_line", StringComparison.Ordinal), json.LastIndexOf("start_line", StringComparison.Ordinal));
        Assert.Equal(json.IndexOf("\"index\":", StringComparison.Ordinal), json.LastIndexOf("\"index\":", StringComparison.Ordinal));
    }

    /// <summary>Verifies keyword rank lifts a chunk that contains the query's exact words above vector-only order.</summary>
    [Fact]
    public void HybridRanker_KeywordMatch_LiftsChunk()
    {
        SearchResultHit[] pool =
        [
            new("a.md", 0, 0.70, "general notes about analyzers and configuration"),
            new("b.md", 0, 0.69, "more notes about settings"),
            new("c.md", 0, 0.68, "the AGENCY_MAX_RETRIES setting controls retry count"),
        ];

        IReadOnlyList<SearchResultHit> ranked = HybridRanker.Rank("AGENCY_MAX_RETRIES", pool);

        Assert.Equal("c.md", ranked[0].Path);
        Assert.True(ranked[0].ExactMatch);
        Assert.All(ranked.Skip(1), h => Assert.False(h.ExactMatch));
    }

    /// <summary>Verifies which query words count as identifiers.</summary>
    [Fact]
    public void HybridRanker_IdentifierTokens_AreDetected()
    {
        string[] ids = HybridRanker.IdentifierTokens("why does ADR-0006 limit \"reply_gate\" in RoomOptions.MaxReplies or camelCase and plain words?");

        Assert.Equal(["ADR-0006", "reply_gate", "RoomOptions.MaxReplies", "camelCase"], ids);
    }

    /// <summary>Verifies the result lists use paths relative to the root, and the summary form carries counts only.</summary>
    [Fact]
    public void IndexOutput_RelativePathsAndSummary()
    {
        string root = Path.Combine(this._dir.Path, "docs");
        var result = new IndexResult(IndexStatus.Ok, "docs", root, [Path.Combine(root, "a", "x.md")], [], [Path.Combine(root, "gone.md")], 3, [], [], 4, 10);

        string full = JsonSerializer.Serialize(IndexOutput.Of(result, summary: false));
        string summary = JsonSerializer.Serialize(IndexOutput.Of(result, summary: true));

        Assert.Contains("\"added\":[\"a/x.md\"]", full, StringComparison.Ordinal);
        Assert.Contains("\"removed\":[\"gone.md\"]", full, StringComparison.Ordinal);
        Assert.Contains("\"added\":1", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("x.md", summary, StringComparison.Ordinal);
    }

    /// <summary>Verifies <c>list --summary</c> reports counts and the full form lists relative paths.</summary>
    [Fact]
    public void IndexOutput_List_RelativeAndSummary()
    {
        string root = Path.Combine(this._dir.Path, "docs");
        var config = new IndexConfig(root, [".md"], [], "m");
        ManifestEntry[] files = [new(Path.Combine(root, "a", "x.md"), 10, 0, 2), new(Path.Combine(root, "y.md"), 10, 0, 3)];

        string full = JsonSerializer.Serialize(IndexOutput.Of("docs", config, files, summary: false));
        string summary = JsonSerializer.Serialize(IndexOutput.Of("docs", config, files, summary: true));

        Assert.Contains("\"path\":\"a/x.md\"", full, StringComparison.Ordinal);
        Assert.Contains("\"file_count\":2", summary, StringComparison.Ordinal);
        Assert.Contains("\"chunk_count\":5", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("x.md", summary, StringComparison.Ordinal);
    }

    /// <summary>Verifies the dry-run sample is spread over the whole set (not its first files) and capped.</summary>
    [Fact]
    public async Task DryRunAsync_SamplesAcrossTheWholeSet()
    {
        for (int i = 0; i < 200; i++)
        {
            this.Write($"doc{i:000}.md", $"Document {i:000} says something about topic{i:000}.");
        }

        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);

        DryRunResult plan = await service.DryRunAsync(Request(root: this._docs), this._embeddings, Ct);

        Assert.Equal(200, plan.EstimatedChunks);
        Assert.InRange(this._embeddings.EmbeddedInputs.Count, 40, 48);
        Assert.Contains(this._embeddings.EmbeddedInputs, t => t.Contains("topic195", StringComparison.Ordinal));
    }

    /// <summary>Verifies a file with many chunks is reported as chunk progress ("N/M chunks") with the total up front.</summary>
    [Fact]
    public async Task IndexAsync_Progress_CountsChunks()
    {
        this.Write("long.md", string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i} about release engineering and versioning.")));
        this.Write("short.md", "tiny");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        var lines = new List<string>();

        IndexResult result = await service.IndexAsync(Request(root: this._docs), Ct, lines.Add);

        Assert.Contains($"{result.ChunksWritten} chunks", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("indexing 1/2 files", StringComparison.Ordinal) && l.Contains($"/{result.ChunksWritten} chunks", StringComparison.Ordinal));
    }

    /// <summary>Verifies one search over two indexes merges by score, names each hit's index, and caps hits per file.</summary>
    [Fact]
    public async Task Program_Search_MultipleIndexes_MergesAndNamesTheIndex()
    {
        this.Write("a/one.md", "reply limit per room");
        this.Write("b/two.md", "reply limit per room and more words");
        this.Write("b/three.md", "reply limit per room extra");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(new IndexRequest("first", Path.Combine(this._docs, "a"), null, null, FileScanner.DefaultMaxFileBytes, Wait: false), Ct);
        await service.IndexAsync(new IndexRequest("second", Path.Combine(this._docs, "b"), null, null, FileScanner.DefaultMaxFileBytes, Wait: false), Ct);

        var settings = IndexerSettings.Resolve(CliArguments.Parse(["search", "--index", "first,second", "--query", "reply limit per room", "--top", "2"]), Path.Combine(this._dir.Path, "home"));
        SearchResponse response = await Program.SearchAsync(service, settings, CliArguments.Parse(["search", "--index", "first,second", "--query", "reply limit per room", "--top", "2"]), Ct);

        Assert.Equal("first,second", response.Index);
        Assert.Equal(2, response.Hits.Count);
        Assert.All(response.Hits, h => Assert.NotNull(h.Index));
        Assert.Equal(["first", "second"], response.Hits.Select(h => h.Index).Order(StringComparer.Ordinal));
        Assert.Equal("first", response.Hits[0].Index);
    }

    /// <summary>Verifies <c>--group-by-file</c> returns one chunk per file and <c>--hybrid</c> runs.</summary>
    [Fact]
    public async Task Program_Search_GroupByFileAndHybrid_ReturnOneChunkPerFile()
    {
        this.Write("long.md", string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i} about release engineering and versioning.")));
        this.Write("other.md", "release engineering notes");
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);
        string[] argv = ["search", "--index", "docs", "--query", "release engineering versioning", "--group-by-file", "--hybrid", "--top", "5"];

        SearchResponse response = await Program.SearchAsync(service, IndexerSettings.Resolve(CliArguments.Parse(argv), Path.Combine(this._dir.Path, "home")), CliArguments.Parse(argv), Ct);

        Assert.Equal(2, response.Hits.Count);
        Assert.Equal(2, response.Hits.Select(h => h.Path).Distinct(StringComparer.Ordinal).Count());
    }

    private static IndexRequest Request(string? root = null) =>
        new("docs", root, null, null, FileScanner.DefaultMaxFileBytes, Wait: false);

    private string Write(string relativePath, string content)
    {
        string path = this._dir.Write(Path.Combine("docs", relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        return path;
    }
}
