using System.Text.Json;

namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="SearchResponse"/> filtering and shaping, and the settings that feed it.</summary>
public sealed class SearchResponseTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly SearchOptions Unfiltered = new(null, null, false, null);

    private static SearchResultHit Hit(string file, double score, string text = "some chunk text") => new(file, 0, score, text);

    /// <summary>Verifies a threshold above the noise floor drops an irrelevant query's hits but keeps the relevant ones.</summary>
    [Fact]
    public void From_MinScore_DropsNoiseAndKeepsRelevantHits()
    {
        SearchResponse sourdough = SearchResponse.From("docs", [Hit("a.md", 0.51), Hit("b.md", 0.49)], Unfiltered with { MinScore = 0.55 });
        SearchResponse library = SearchResponse.From("docs", [Hit("a.md", 0.59), Hit("b.md", 0.585), Hit("c.md", 0.58)], Unfiltered with { MinScore = 0.55 });

        Assert.Empty(sourdough.Hits);
        Assert.Equal(2, sourdough.Filtered);
        Assert.Equal(0.51, sourdough.BestScore);
        Assert.Equal("ok", sourdough.Status);
        Assert.Equal(3, library.Hits.Count);
        Assert.Null(library.Filtered);
        Assert.Null(library.BestScore);
    }

    /// <summary>Verifies the relative cutoff keeps only hits near the top score, with no calibration.</summary>
    [Fact]
    public void From_Within_KeepsHitsCloseToTheBest()
    {
        SearchResponse response = SearchResponse.From("docs", [Hit("a.md", 0.75), Hit("b.md", 0.70), Hit("c.md", 0.55)], Unfiltered with { Within = 0.10 });

        Assert.Equal(["a.md", "b.md"], response.Hits.Select(h => h.Path));
        Assert.Equal(1, response.Filtered);
        Assert.Equal(0.75, response.BestScore);
    }

    /// <summary>Verifies both cutoffs apply together.</summary>
    [Fact]
    public void From_MinScoreAndWithin_BothApply()
    {
        SearchResponse response = SearchResponse.From("docs", [Hit("a.md", 0.80), Hit("b.md", 0.72), Hit("c.md", 0.60)], Unfiltered with { MinScore = 0.65, Within = 0.20 });

        Assert.Equal(["a.md", "b.md"], response.Hits.Select(h => h.Path));
        Assert.Equal(1, response.Filtered);
    }

    /// <summary>Verifies a search with no hits at all is a plain empty result, not a filtered one.</summary>
    [Fact]
    public void From_NoHits_HasNoFilteredFields()
    {
        SearchResponse response = SearchResponse.From("docs", [], Unfiltered with { MinScore = 0.5 });

        Assert.Empty(response.Hits);
        Assert.Null(response.Filtered);
        Assert.Null(response.BestScore);
    }

    /// <summary>Verifies <c>--no-text</c> leaves the text out of the JSON entirely.</summary>
    [Fact]
    public void From_NoText_OmitsTheTextField()
    {
        SearchResponse response = SearchResponse.From("docs", [Hit("a.md", 0.7)], Unfiltered with { NoText = true });

        string json = JsonSerializer.Serialize(response, Json);

        Assert.DoesNotContain("\"text\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"a.md\"", json, StringComparison.Ordinal);
        Assert.Contains("\"score\":0.7", json, StringComparison.Ordinal);
        Assert.Contains("\"chunk\":0", json, StringComparison.Ordinal);
    }

    /// <summary>Verifies a snippet limit is a hard bound, and shorter text is left alone.</summary>
    [Fact]
    public void From_SnippetChars_NeverExceedsTheLimit()
    {
        string longText = new('x', 1000);

        SearchResponse response = SearchResponse.From("docs", [Hit("a.md", 0.7, longText), Hit("b.md", 0.6, "short")], Unfiltered with { SnippetChars = 200 });

        Assert.Equal(200, response.Hits[0].Text!.Length);
        Assert.Equal("short", response.Hits[1].Text);
    }

    /// <summary>Verifies a snippet is not cut between the halves of a surrogate pair.</summary>
    [Fact]
    public void From_SnippetCutInsideSurrogatePair_BacksOff()
    {
        string text = "ab\U0001F600cd";

        SearchResponse response = SearchResponse.From("docs", [Hit("a.md", 0.7, text)], Unfiltered with { SnippetChars = 3 });

        Assert.Equal("ab", response.Hits[0].Text);
    }

    /// <summary>Verifies the filtered fields serialize only when something was dropped.</summary>
    [Fact]
    public void From_Serialization_IncludesFilteredFieldsOnlyWhenHitsWereDropped()
    {
        string dropped = JsonSerializer.Serialize(SearchResponse.From("docs", [Hit("a.md", 0.5)], Unfiltered with { MinScore = 0.6 }), Json);
        string kept = JsonSerializer.Serialize(SearchResponse.From("docs", [Hit("a.md", 0.7)], Unfiltered with { MinScore = 0.6 }), Json);

        Assert.Contains("\"hits\":[]", dropped, StringComparison.Ordinal);
        Assert.Contains("\"filtered\":1", dropped, StringComparison.Ordinal);
        Assert.Contains("\"best_score\":0.5", dropped, StringComparison.Ordinal);
        Assert.DoesNotContain("filtered", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("best_score", kept, StringComparison.Ordinal);
    }

    /// <summary>Verifies the minimum score comes from the user file, is overridden by the command line, and is not read from a repo file.</summary>
    [Fact]
    public void Resolve_MinScore_UserFileThenCommandLine_NeverRepoFile()
    {
        using var dir = new TempDirectory();
        dir.Write("home/indexer.json", """{ "Search": { "MinScore": 0.55 } }""");
        dir.Write("repo/.agency-index.json", """{ "Index": "docs", "Search": { "MinScore": 0.1 } }""");
        string home = Path.Combine(dir.Path, "home");
        string repo = Path.Combine(dir.Path, "repo");

        IndexerSettings fromFile = IndexerSettings.Resolve(CliArguments.Parse(["search"]), home, repo);
        IndexerSettings fromCli = IndexerSettings.Resolve(CliArguments.Parse(["search", "--min-score", "0.7"]), home, repo);

        Assert.Equal(0.55, fromFile.SearchMinScore);
        Assert.Contains("Search", fromFile.Defaults.IgnoredRepoKeys);
        Assert.Equal(0.7, fromCli.SearchMinScore);
        Assert.Null(IndexerSettings.Resolve(CliArguments.Parse(["search"]), Path.Combine(dir.Path, "none")).SearchMinScore);
    }

    /// <summary>Verifies out-of-range or non-numeric thresholds are usage errors.</summary>
    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("high")]
    public void Resolve_InvalidMinScore_Throws(string value)
    {
        using var dir = new TempDirectory();

        Assert.Throws<UsageException>(() => IndexerSettings.Resolve(CliArguments.Parse(["search", "--min-score", value]), dir.Path));
    }

    /// <summary>Verifies <c>--within</c> parsing and <c>--no-text</c> being a flag that does not swallow the next option.</summary>
    [Fact]
    public void CliArguments_WithinAndNoText_Parse()
    {
        CliArguments args = CliArguments.Parse(["search", "--no-text", "--query", "x", "--within", "0.1"]);

        Assert.True(args.Flags.Contains("no-text"));
        Assert.Equal("x", args.Get("query"));
        Assert.Equal(0.1, args.GetFraction("within"));
        Assert.Null(CliArguments.Parse(["search"]).GetFraction("within"));
        Assert.Throws<UsageException>(() => CliArguments.Parse(["search", "--within", "2"]).GetFraction("within"));
    }

    /// <summary>Verifies the indexer waits between embedding retries by default, and the user file can change or switch it off.</summary>
    [Fact]
    public void Resolve_EmbeddingRetry_DefaultsToBackoffAndIsConfigurable()
    {
        using var dir = new TempDirectory();

        IndexerSettings defaults = IndexerSettings.Resolve(CliArguments.Parse(["index"]), dir.Path);
        dir.Write("indexer.json", """{ "Embedding": { "RetryDelayMs": 0, "MaxRetries": 5 } }""");
        IndexerSettings configured = IndexerSettings.Resolve(CliArguments.Parse(["index"]), dir.Path);

        Assert.Equal(IndexerSettings.DefaultRetryDelayMs, defaults.Embedding.RetryDelayMs);
        Assert.Null(defaults.Embedding.MaxRetries);
        Assert.Equal(0, configured.Embedding.RetryDelayMs);
        Assert.Equal(5, configured.Embedding.MaxRetries);
    }
}
