using System.Text.Json;

namespace Agency.Indexer.Test;

/// <summary>Tests for passage-level indexing, the grep-style lines output, keyword fusion, calibration with questions and the search session.</summary>
public sealed class PassageSearchTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _docs;
    private readonly string _database;
    private readonly FakeEmbeddingGenerator _embeddings = new();

    /// <summary>Creates a docs folder and a database path inside a fresh temporary directory.</summary>
    public PassageSearchTests()
    {
        this._docs = Path.Combine(this._dir.Path, "docs");
        this._database = Path.Combine(this._dir.Path, "db", "index.db");
        Directory.CreateDirectory(this._docs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose() => this._dir.Dispose();

    /// <summary>Verifies passages hold whole lines with exact line numbers, never cross a heading, and carry their heading path.</summary>
    [Fact]
    public void PassageSplitter_CutsWholeLinesWithExactRanges()
    {
        string content = "# Guide\n\nintro line one\nintro line two\nintro line three\n\n## Budget\n\n" + string.Join("\n", Enumerable.Range(1, 10).Select(i => $"budget line {i}"));

        IReadOnlyList<Passage> passages = PassageSplitter.Split(content, markdown: true, new PassageOptions(4, 1));

        string[] lines = content.Split('\n');
        Assert.All(passages, p => Assert.Equal(string.Join('\n', lines[(p.StartLine - 1)..p.EndLine]), p.Text));
        Assert.All(passages, p => Assert.True(p.Text.Split('\n').Count(l => l.Length > 0) <= 4));
        Assert.Equal("Guide", passages[0].Heading);
        Assert.Equal(7, passages.First(p => p.Heading == "Guide > Budget").StartLine);
        Assert.DoesNotContain(passages, p => p.Heading == "Guide" && p.EndLine >= 7);
        Assert.Equal(10 + 8, passages[^1].EndLine);
    }

    /// <summary>Verifies consecutive passages of one section share their overlap line, and the tail is not repeated.</summary>
    [Fact]
    public void PassageSplitter_Overlap_SharesLinesAndEndsOnce()
    {
        string content = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"line {i}"));

        IReadOnlyList<Passage> passages = PassageSplitter.Split(content, markdown: false, new PassageOptions(4, 1));

        Assert.Equal([(1, 4), (4, 7), (7, 9)], passages.Select(p => (p.StartLine, p.EndLine)));
        Assert.Equal(passages.Count, passages.Select(p => p.StartLine).Distinct().Count());
    }

    /// <summary>Verifies the embedded text starts with the heading and the stored value can be turned back into the passage.</summary>
    [Fact]
    public void Passage_EmbeddedText_RoundTrips()
    {
        var passage = new Passage(3, 4, "a\nb", "Room > Budget");

        Assert.Equal("Room > Budget\na\nb", passage.Embedded);
        Assert.Equal("a\nb", Passage.StripHeading(passage.Embedded, passage.Heading));
        Assert.Equal("a\nb", Passage.StripHeading("a\nb", null));
    }

    /// <summary>Verifies a new index is passage-level: hits are short, carry exact lines, and the format is stored.</summary>
    [Fact]
    public async Task IndexAsync_NewIndex_IsPassageLevel()
    {
        string text = "# Rooms\n\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => i == 17 ? "The zebrafish limit is nine replies per room." : $"Filler sentence number {i} about nothing."));
        this.Write("rooms.md", text);
        IndexService service = await this.IndexedAsync();

        IndexConfig config = await service.GetConfigAsync("docs", Ct);
        IReadOnlyList<SearchResultHit> hits = await service.SearchAsync("docs", "zebrafish limit nine replies room", 1, Ct);

        Assert.Equal(IndexFormat.Passages, config.FormatVersion);
        Assert.Equal((6, 1), (config.PassageLines, config.PassageOverlap));
        SearchResultHit hit = Assert.Single(hits);
        Assert.Equal("Rooms", hit.Heading);
        Assert.InRange(hit.EndLine!.Value - hit.StartLine!.Value, 0, 8);
        Assert.Contains("zebrafish", hit.Text, StringComparison.Ordinal);
        Assert.StartsWith("Rooms\n", hit.Text, StringComparison.Ordinal);
        Assert.InRange(19, hit.StartLine.Value, hit.EndLine.Value);
    }

    /// <summary>Verifies a changed passage size is refused until a rebuild, which re-cuts the index.</summary>
    [Fact]
    public async Task IndexAsync_PassageSizeChanged_NeedsRebuild()
    {
        this.Write("a.md", string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line number {i} of the file")));
        await this.IndexedAsync();
        IndexService other = await Program.CreateServiceAsync(
            new IndexerSettings(StorageProvider.Sqlite, this._database, new Agency.Embeddings.OpenAI.EmbeddingOptions { ModelId = "fake-model", Dimensions = FakeEmbeddingGenerator.Dimensions }, 64, 0, new PassageOptions(3, 0)),
            this._embeddings,
            Ct);

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() => other.IndexAsync(Request(), Ct));
        IndexResult rebuilt = await other.IndexAsync(Request() with { Rebuild = true }, Ct);

        Assert.Contains("--rebuild", ex.Message, StringComparison.Ordinal);
        Assert.Equal(IndexStatus.Ok, rebuilt.Status);
        Assert.Equal(3, (await other.GetConfigAsync("docs", Ct)).PassageLines);
    }

    /// <summary>Verifies an index run measures the noise distribution used to normalize scores.</summary>
    [Fact]
    public async Task IndexAsync_MeasuresNoiseStatistics()
    {
        this.Write("a.md", "release engineering and versioning of packages");
        IndexService service = await this.IndexedAsync();

        NoiseStats? noise = (await service.GetConfigAsync("docs", Ct)).Noise;

        Assert.NotNull(noise);
        Assert.True(noise.Ceiling >= noise.Mean);
        Assert.Equal(2.0, noise.Normalize(noise.Mean + (2 * noise.StdDev)), 6);
    }

    /// <summary>Verifies the lines output: <c>path:line: text</c> with true line numbers, a score line, and a verdict line.</summary>
    [Fact]
    public async Task LineSearch_PrintsGrepStyleLines()
    {
        string text = "# Rooms\n\nOther notes live elsewhere in these docs.\nThe zebrafish limit is nine replies per room.\nMore notes follow below.";
        string path = this.Write("guide/rooms.md", text);
        this.Write("other.md", "Something entirely different about gardening and tomatoes.");
        IndexService service = await this.IndexedAsync();
        int markerLine = text.Split('\n').ToList().FindIndex(l => l.Contains("zebrafish", StringComparison.Ordinal)) + 1;

        LineSearchResult result = await LineSearch.RunAsync(service, this._embeddings, ["docs"], "zebrafish limit nine replies room", new LineSearchOptions(), Ct);

        string[] lines = result.Text.Split('\n');
        Assert.Contains($"guide/rooms.md:{markerLine}: The zebrafish limit is nine replies per room.", lines);
        Assert.Contains(lines, l => l.StartsWith("  [score ", StringComparison.Ordinal) && l.EndsWith("Rooms", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain('\\', l));
        Assert.StartsWith("# ", lines[^1], StringComparison.Ordinal);
        Assert.Equal(path, result.Hits[0].Path);
    }

    /// <summary>Verifies <c>--highlight</c> marks query words and <c>--max-line-chars</c> cuts long lines.</summary>
    [Fact]
    public async Task LineSearch_HighlightAndMaxLineChars()
    {
        this.Write("a.md", "The zebrafish limit is nine replies per room and this sentence keeps going for quite a while longer.");
        IndexService service = await this.IndexedAsync();

        LineSearchResult marked = await LineSearch.RunAsync(service, this._embeddings, ["docs"], "zebrafish limit", new LineSearchOptions(Highlight: true), Ct);
        LineSearchResult cut = await LineSearch.RunAsync(service, this._embeddings, ["docs"], "zebrafish limit", new LineSearchOptions(MaxLineChars: 30), Ct);

        Assert.Contains("**zebrafish**", marked.Text, StringComparison.Ordinal);
        Assert.Contains("…", cut.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("quite a while", cut.Text, StringComparison.Ordinal);
    }

    /// <summary>Verifies a query about a topic that is not in the docs prints the explicit no-match line and no hits.</summary>
    [Fact]
    public async Task LineSearch_NothingAboveNoise_PrintsNoMatch()
    {
        this.Write("a.md", "release engineering and versioning of packages");
        IndexService service = await this.IndexedAsync();

        LineSearchResult result = await LineSearch.RunAsync(service, this._embeddings, ["docs"], "xylophone quartet orchestra", new LineSearchOptions(), Ct);

        Assert.StartsWith("# no match above min score", result.Text, StringComparison.Ordinal);
        Assert.Empty(result.Hits);
        Assert.Null(result.TopScore);
    }

    /// <summary>Verifies keyword fusion finds a passage the vector search ranked outside its pool, by its exact term.</summary>
    [Fact]
    public async Task LineSearch_Hybrid_FindsExactTermTheVectorsMissed()
    {
        for (int i = 0; i < 60; i++)
        {
            this.Write($"noise/n{i:00}.md", $"query words alpha beta gamma delta number {i} appear here repeatedly alpha beta gamma delta");
        }

        this.Write("deep/needle.md", "The LibraryPathResolver class decides where skills are loaded from.");
        IndexService service = await this.IndexedAsync();

        LineSearchResult hybrid = await LineSearch.RunAsync(service, this._embeddings, ["docs"], "alpha beta gamma delta LibraryPathResolver", new LineSearchOptions(TopFiles: 6), Ct);

        Assert.Contains("deep/needle.md:1:", hybrid.Text, StringComparison.Ordinal);
    }

    /// <summary>Verifies a word found in most files is left out of the query, unless that would leave it too short.</summary>
    [Fact]
    public void Lexical_CommonTerms_AreDroppedFromTheQuery()
    {
        string[] files = Enumerable.Range(0, 12).Select(i => i < 10 ? $"Huddle page {i} about topic{i}" : $"page {i} about topic{i}").ToArray();

        IReadOnlyList<string> common = Lexical.CommonTerms(files);
        (string dropped, IReadOnlyList<string> words) = Lexical.DropCommon("How does Huddle bill customers through Stripe", common);
        (string kept, IReadOnlyList<string> none) = Lexical.DropCommon("Huddle billing", common);

        Assert.Contains("huddle", common);
        Assert.Equal("How does bill customers through Stripe", dropped);
        Assert.Equal(["huddle"], words);
        Assert.Equal("Huddle billing", kept);
        Assert.Empty(none);
        Assert.Empty(Lexical.CommonTerms(files.Take(5)));
    }

    /// <summary>Verifies the keyword search ranks by rarity of the term and reports the passage's real lines.</summary>
    [Fact]
    public void Lexical_Search_RanksRareTermsAndReportsLines()
    {
        string a = this._dir.Write("docs/a.md", "# A\n\nplain words about rooms\nmore plain words\n\nthe Sprint planning notes live here\n");
        string b = this._dir.Write("docs/b.md", "plain words about rooms and rooms\n");

        IReadOnlyList<LexicalHit> hits = Lexical.Search([a, b], "Sprint rooms", PassageOptions.Default, 5, _ => true);

        Assert.Equal(a, hits[0].Path);
        Assert.Contains("Sprint", hits[0].Text, StringComparison.Ordinal);
        Assert.Equal("A", hits[0].Heading);
    }

    /// <summary>Verifies calibration with questions reports the answer floor and suggests a threshold inside a real gap.</summary>
    [Fact]
    public async Task Calibrate_WithQuestions_ReportsFloorAndGap()
    {
        string path = this.Write("a.md", "release engineering and versioning of packages");
        this.Write("b.md", "gardening tomatoes and watering the vegetable beds every morning");
        IndexService service = await this.IndexedAsync();

        CalibrationResult result = await service.CalibrateAsync(
            "docs",
            save: true,
            Ct,
            [new CalibrationQuestion("release engineering versioning packages", "a.md"), new CalibrationQuestion("versioning of release packages", path)]);

        Assert.Equal(2, result.Questions);
        Assert.Equal(0, result.Missed);
        Assert.True(result.AnswerFloor > result.NoiseCeiling);
        Assert.Null(result.Warning);
        Assert.InRange(result.SuggestedMinScore, result.NoiseCeiling, result.AnswerFloor!.Value);
        Assert.Equal(result.AnswerFloor, (await service.GetConfigAsync("docs", Ct)).Calibration!.AnswerFloor);
    }

    /// <summary>Verifies answers that score no better than noise produce a warning and no saved threshold.</summary>
    [Fact]
    public async Task Calibrate_WithQuestions_WarnsWhenAnswersOverlapNoise()
    {
        this.Write("a.md", "release engineering and versioning of packages");
        this.Write("b.md", "gardening tomatoes and watering the vegetable beds every morning");
        IndexService service = await this.IndexedAsync();

        CalibrationResult result = await service.CalibrateAsync("docs", save: true, Ct, [new CalibrationQuestion("zzz qqq www", "a.md")]);

        Assert.NotNull(result.Warning);
        Assert.False(result.Saved);
        Assert.Null((await service.GetConfigAsync("docs", Ct)).Calibration);
        Assert.NotNull((await service.GetConfigAsync("docs", Ct)).Noise);
    }

    /// <summary>Verifies the last-search cache round-trips per folder and the search log appends one JSON line per call.</summary>
    [Fact]
    public void SearchSession_CacheAndLog()
    {
        string home = Path.Combine(this._dir.Path, "home");
        PrintedHit[] hits = [new("docs", "/x/a.md", 12, 18), new("docs", "/x/b.md", null, null)];

        SearchSession.Save(home, "/work/one", hits);

        Assert.Equal(hits, SearchSession.Load(home, "/work/one"));
        Assert.Null(SearchSession.Load(home, "/work/two"));
        string log = Path.Combine(this._dir.Path, "logs", "search.log");
        SearchSession.AppendLog(log, "why?", "--top 4", 321, 0.63);
        SearchSession.AppendLog(log, "again", "", 10, null);
        string[] rows = File.ReadAllLines(log);
        Assert.Equal(2, rows.Length);
        using JsonDocument first = JsonDocument.Parse(rows[0]);
        Assert.Equal("why?", first.RootElement.GetProperty("query").GetString());
        Assert.Equal(321, first.RootElement.GetProperty("chars").GetInt32());
        Assert.Equal(0.63, first.RootElement.GetProperty("top_score").GetDouble());
    }

    /// <summary>Verifies <c>--all</c> lifts the 400-line cap that explicit ranges keep.</summary>
    [Fact]
    public async Task ReadAsync_All_ReturnsTheWholeFile()
    {
        string path = this.Write("long.md", string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}")));
        IndexService service = await this.IndexedAsync();

        ReadResult capped = await service.ReadAsync("docs", path, 1, null, Ct);
        ReadResult whole = await service.ReadAsync("docs", path, 1, null, Ct, all: true);

        Assert.Equal(400, capped.EndLine);
        Assert.Equal(500, whole.EndLine);
    }

    /// <summary>Verifies the settings reject passage sizes that cannot work.</summary>
    [Theory]
    [InlineData("""{ "PassageLines": 0 }""")]
    [InlineData("""{ "PassageLines": 4, "PassageOverlap": 4 }""")]
    public void Resolve_InvalidPassageSettings_Throw(string json)
    {
        using var dir = new TempDirectory();
        dir.Write("indexer.json", json);

        Assert.Throws<UsageException>(() => IndexerSettings.Resolve(CliArguments.Parse(["index"]), dir.Path));
    }

    private static IndexRequest Request(string? root = null) =>
        new("docs", root, null, null, FileScanner.DefaultMaxFileBytes, Wait: false);

    private async Task<IndexService> IndexedAsync()
    {
        IndexService service = await Services.SqliteAsync(this._database, this._embeddings);
        await service.IndexAsync(Request(root: this._docs), Ct);
        return service;
    }

    private string Write(string relativePath, string content)
    {
        string path = this._dir.Write(Path.Combine("docs", relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        return path;
    }
}
