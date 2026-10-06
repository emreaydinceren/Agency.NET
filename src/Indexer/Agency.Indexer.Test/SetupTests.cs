using static Agency.Indexer.Test.DoctorTests;

namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="Setup"/> against a stubbed embeddings endpoint.</summary>
public sealed class SetupTests
{
    private const string Url = "http://stub/v1";

    /// <summary>Verifies that without <c>--yes</c> nothing is written, but the plan is reported.</summary>
    [Fact]
    public async Task RunAsync_WithoutYes_PreviewsAndWritesNothing()
    {
        using var dir = new TempDirectory();

        SetupResult result = await RunAsync(dir, ["setup", "--embedding-url", Url], new StubEndpoint { Models = ["text-embedding-x"], VectorLength = 7 });

        Assert.Equal("preview", result.Status);
        Assert.Equal("text-embedding-x", result.ModelId);
        Assert.Equal(7, result.Dimensions);
        Assert.Contains("\"Dimensions\": 7", result.ConfigAfter, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "home")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "work", ".claude")));
    }

    /// <summary>Verifies <c>--yes</c> installs the repo-scope skill and merges the config without losing other keys.</summary>
    [Fact]
    public async Task RunAsync_WithYes_InstallsSkillAndMergesConfig()
    {
        using var dir = new TempDirectory();
        dir.Write("home/indexer.json", """{ "ChunkSize": 256, "Embedding": { "ApiKey": "keep-me", "Dimensions": 1024 } }""");

        SetupResult result = await RunAsync(dir, ["setup", "--embedding-url", Url, "--yes"], new StubEndpoint { Models = ["text-embedding-x"], VectorLength = 7 });

        Assert.Equal("ok", result.Status);
        Assert.True(File.Exists(Path.Combine(dir.Path, "work", ".claude", "skills", "agency-index", "SKILL.md")));
        string written = File.ReadAllText(Path.Combine(dir.Path, "home", "indexer.json"));
        Assert.Contains("\"ChunkSize\": 256", written, StringComparison.Ordinal);
        Assert.Contains("\"ApiKey\": \"keep-me\"", written, StringComparison.Ordinal);
        Assert.Contains("\"Dimensions\": 7", written, StringComparison.Ordinal);
        Assert.Contains("\"ModelId\": \"text-embedding-x\"", written, StringComparison.Ordinal);
        Assert.Equal(written, result.ConfigAfter);
    }

    /// <summary>Verifies the endpoint preset supplies the URL, and an ambiguous model list asks the user to choose.</summary>
    [Fact]
    public async Task RunAsync_SeveralEmbeddingModels_ThrowsListingThem()
    {
        using var dir = new TempDirectory();

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() =>
            RunAsync(dir, ["setup", "--embedding-url", Url], new StubEndpoint { Models = ["embed-a", "embed-b", "chat-model"] }));

        Assert.Contains("--embedding-model", ex.Message, StringComparison.Ordinal);
        Assert.Contains("embed-b", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a <c>--dimensions</c> value that contradicts the model is rejected.</summary>
    [Fact]
    public async Task RunAsync_DimensionsContradictModel_Throws()
    {
        using var dir = new TempDirectory();

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() =>
            RunAsync(dir, ["setup", "--embedding-url", Url, "--embedding-model", "m", "--dimensions", "768"], new StubEndpoint { VectorLength = 3 }));

        Assert.Contains("3-dimension", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies an unreachable endpoint is a usage error naming the endpoint.</summary>
    [Fact]
    public async Task RunAsync_EndpointDown_ThrowsUsageError()
    {
        using var dir = new TempDirectory();

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() =>
            RunAsync(dir, ["setup", "--embedding-url", Url], new StubEndpoint { Down = true }));

        Assert.Contains(Url, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies an index name owned by another root stops setup before anything is written.</summary>
    [Fact]
    public async Task RunAsync_IndexNameOwnedByAnotherRoot_ReturnsNameTakenAndWritesNothing()
    {
        using var dir = new TempDirectory();
        string otherRoot = Path.Combine(dir.Path, "other");
        dir.Write("other/readme.md", "# Other repo");
        Directory.CreateDirectory(Path.Combine(dir.Path, "home"));
        IndexService existing = await Services.SqliteAsync(Path.Combine(dir.Path, "home", "index.db"), new FakeEmbeddingGenerator());
        await existing.IndexAsync(new IndexRequest("docs", otherRoot, null, null, FileScanner.DefaultMaxFileBytes, Wait: false), TestContext.Current.CancellationToken);

        SetupResult result = await RunAsync(
            dir,
            ["setup", "--embedding-url", Url, "--index", "docs", "--root", Path.Combine(dir.Path, "work"), "--yes"],
            new StubEndpoint { Models = ["text-embedding-x"], VectorLength = FakeEmbeddingGenerator.Dimensions });

        Assert.Equal("name_taken", result.Status);
        Assert.Contains(otherRoot, result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir.Path, "home", "indexer.json")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "work", ".claude")));
    }

    private static Task<SetupResult> RunAsync(TempDirectory dir, string[] args, StubEndpoint endpoint)
    {
        string work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(work);
        return Setup.RunAsync(
            CliArguments.Parse(args),
            Path.Combine(dir.Path, "home"),
            Path.Combine(dir.Path, "profile"),
            work,
            new HttpClient(endpoint),
            TestContext.Current.CancellationToken);
    }
}
