using static Agency.Indexer.Test.DoctorTests;

namespace Agency.Indexer.Test;

/// <summary>
/// Tests for the repo-level <c>.agency-index.json</c>: discovery, the allowed keys, precedence against the user file,
/// environment and command line, and how <c>setup</c>, <c>doctor</c> and <c>uninstall</c> use it. Tests that set
/// environment variables restore them on dispose.
/// </summary>
public sealed class RepoConfigTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly Dictionary<string, string?> _savedEnvironment = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Home => Path.Combine(this._dir.Path, "home");

    private string Repo => Path.Combine(this._dir.Path, "repo");

    private string Leaf => Path.Combine(this.Repo, "docs", "deep", "leaf");

    /// <summary>Deletes the temporary directory and restores environment variables.</summary>
    public void Dispose()
    {
        foreach ((string name, string? value) in this._savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        this._dir.Dispose();
    }

    /// <summary>Verifies the config file is found from a leaf folder, and the nearest of several wins.</summary>
    [Fact]
    public void FindConfig_FromLeafFolder_FindsNearestFileAbove()
    {
        string outer = Path.GetFullPath(this._dir.Write("repo/.agency-index.json", "{}"));
        Directory.CreateDirectory(this.Leaf);
        Assert.Equal(outer, RepoLocator.FindConfig(this.Leaf));

        string inner = Path.GetFullPath(this._dir.Write("repo/docs/.agency-index.json", "{}"));
        Assert.Equal(inner, RepoLocator.FindConfig(this.Leaf));
        Assert.Equal(outer, RepoLocator.FindConfig(this.Repo));
    }

    /// <summary>Verifies the repo root is the nearest folder with a config file or a <c>.git</c> entry, else the start folder.</summary>
    [Fact]
    public void FindRoot_ConfigOrGit_FirstHitWalkingUpWins()
    {
        Directory.CreateDirectory(this.Leaf);
        Assert.Equal(this.Leaf, RepoLocator.FindRoot(this.Leaf));

        Directory.CreateDirectory(Path.Combine(this.Repo, ".git"));
        Assert.Equal(this.Repo, RepoLocator.FindRoot(this.Leaf));

        this._dir.Write("repo/docs/.agency-index.json", "{}");
        Assert.Equal(Path.Combine(this.Repo, "docs"), RepoLocator.FindRoot(this.Leaf));
    }

    /// <summary>Verifies a <c>.git</c> file (a worktree or submodule) also marks a repo root.</summary>
    [Fact]
    public void FindRoot_GitFile_MarksRoot()
    {
        this._dir.Write("repo/.git", "gitdir: elsewhere");
        Directory.CreateDirectory(this.Leaf);

        Assert.Equal(this.Repo, RepoLocator.FindRoot(this.Leaf));
    }

    /// <summary>Verifies only the allowed keys are accepted, a relative root is made absolute against the file, and the rest is reported.</summary>
    [Fact]
    public void Load_AcceptsOnlyAllowedKeysAndReportsTheRest()
    {
        string path = this._dir.Write("repo/.agency-index.json", """
            { "Index": "docs", "Root": "docs", "extensions": [".md", ".txt"], "MaxFileKb": 512,
              "Embedding": { "BaseUrl": "http://evil.example/v1" }, "Database": "x", "ApiKey": "k" }
            """);

        RepoConfig config = RepoConfig.Load(path);

        Assert.Equal("docs", config.Values["Index"]);
        Assert.Equal(Path.Combine(this.Repo, "docs"), config.Values["Root"]);
        Assert.Equal(".md,.txt", config.Values["Extensions"]);
        Assert.Equal("512", config.Values["MaxFileKb"]);
        Assert.Equal(["Embedding", "Database", "ApiKey"], config.Ignored);
    }

    /// <summary>Verifies malformed JSON is a usage error that names the file.</summary>
    [Fact]
    public void Load_InvalidJson_ThrowsNamingFile()
    {
        string path = this._dir.Write("repo/.agency-index.json", "{ not json");

        UsageException ex = Assert.Throws<UsageException>(() => RepoConfig.Load(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the security rule: a repo file cannot change where text or credentials are sent, nor which database is used.
    /// </summary>
    [Fact]
    public void Resolve_RepoFileCannotRedirectEmbeddingsOrDatabase()
    {
        this._dir.Write("home/indexer.json", """{ "Embedding": { "BaseUrl": "http://localhost:1234/v1", "ModelId": "good" } }""");
        this._dir.Write("repo/.agency-index.json", """
            { "Index": "docs", "Embedding": { "BaseUrl": "http://evil.example/v1", "ModelId": "evil" },
              "Database": "C:/evil.db", "Provider": "postgres", "ChunkSize": 1 }
            """);

        IndexerSettings settings = IndexerSettings.Resolve(CliArguments.Parse(["index"]), this.Home, this.Repo);

        Assert.Equal("http://localhost:1234/v1", settings.Embedding.BaseUrl);
        Assert.Equal("good", settings.Embedding.ModelId);
        Assert.Equal(StorageProvider.Sqlite, settings.Provider);
        Assert.Equal(Path.Combine(this.Home, "index.db"), settings.Database);
        Assert.Equal(512, settings.ChunkSize);
        Assert.Equal("docs", settings.Defaults.Index);
        Assert.Contains("Embedding", settings.Defaults.IgnoredRepoKeys);
        Assert.Contains("Database", settings.Defaults.IgnoredRepoKeys);
    }

    /// <summary>Verifies the repo file overrides the user file per key, and the command line overrides both.</summary>
    [Fact]
    public void Resolve_PrecedenceUserThenRepoThenCommandLine()
    {
        this._dir.Write("home/indexer.json", """{ "Index": "from-user", "Root": "C:/user-root", "Names": ["README"] }""");
        this._dir.Write("repo/.agency-index.json", """{ "Index": "from-repo", "Names": "NOTES,TODO" }""");
        Directory.CreateDirectory(this.Leaf);

        IndexDefaults fromRepo = IndexerSettings.Resolve(CliArguments.Parse(["index"]), this.Home, this.Leaf).Defaults;
        IndexDefaults fromCli = IndexerSettings.Resolve(CliArguments.Parse(["index", "--index", "from-cli"]), this.Home, this.Leaf).Defaults;

        Assert.Equal("from-repo", fromRepo.Index);
        Assert.Equal("repo file", fromRepo.Sources["Index"]);
        Assert.Equal("C:/user-root", fromRepo.Root);
        Assert.Equal("user file", fromRepo.Sources["Root"]);
        Assert.Equal("NOTES,TODO", fromRepo.Names);
        Assert.Equal("from-cli", fromCli.Index);
        Assert.Equal("command line", fromCli.Sources["Index"]);
    }

    /// <summary>Verifies an environment variable beats the repo file but not the command line.</summary>
    [Fact]
    public void Resolve_EnvironmentBeatsRepoFileButNotCommandLine()
    {
        this._dir.Write("repo/.agency-index.json", """{ "Index": "from-repo" }""");
        this.SetEnvironment("AGENCY_INDEX_Index", "from-env");

        IndexDefaults fromEnv = IndexerSettings.Resolve(CliArguments.Parse(["index"]), this.Home, this.Repo).Defaults;
        IndexDefaults fromCli = IndexerSettings.Resolve(CliArguments.Parse(["index", "--index", "from-cli"]), this.Home, this.Repo).Defaults;

        Assert.Equal("from-env", fromEnv.Index);
        Assert.Equal("environment", fromEnv.Sources["Index"]);
        Assert.Equal("from-cli", fromCli.Index);
    }

    /// <summary>Verifies a relative root in the repo file means the same folder from any working directory.</summary>
    [Fact]
    public void Resolve_RelativeRootIsRelativeToTheRepoFile()
    {
        this._dir.Write("repo/.agency-index.json", """{ "Index": "docs", "Root": "docs" }""");
        Directory.CreateDirectory(this.Leaf);

        IndexDefaults defaults = IndexerSettings.Resolve(CliArguments.Parse(["index"]), this.Home, this.Leaf).Defaults;

        Assert.Equal(Path.Combine(this.Repo, "docs"), defaults.Root);
    }

    /// <summary>Verifies no repo file is read when no working directory is given, and an invalid size is a usage error.</summary>
    [Fact]
    public void Resolve_NoWorkingDirectory_IgnoresRepoFile_AndRejectsBadMaxFileKb()
    {
        this._dir.Write("repo/.agency-index.json", """{ "Index": "docs" }""");

        Assert.Null(IndexerSettings.Resolve(CliArguments.Parse(["index"]), this.Home).Defaults.Index);

        this._dir.Write("repo/.agency-index.json", """{ "MaxFileKb": "lots" }""");
        Assert.Throws<UsageException>(() => IndexerSettings.Resolve(CliArguments.Parse(["index"]), this.Home, this.Repo));
    }

    /// <summary>Verifies setup previews the repo file it would write, keeping the keys it does not own.</summary>
    [Fact]
    public async Task Setup_WithIndex_PreviewsRepoConfigAndKeepsOtherKeys()
    {
        this._dir.Write("repo/.agency-index.json", """{ "Index": "old", "Extensions": [".md"], "Note": 1 }""");
        string root = Path.Combine(this.Repo, "docs");
        Directory.CreateDirectory(root);

        SetupResult result = await Setup.RunAsync(
            CliArguments.Parse(["setup", "--embedding-url", "http://stub/v1", "--index", "docs", "--root", root]),
            this.Home,
            Path.Combine(this._dir.Path, "profile"),
            this.Repo,
            new HttpClient(new StubEndpoint { Models = ["text-embedding-x"], VectorLength = 4 }),
            Ct);

        Assert.Equal("preview", result.Status);
        Assert.Equal(Path.Combine(this.Repo, ".agency-index.json"), result.RepoConfigPath);
        Assert.Contains("\"Index\": \"docs\"", result.RepoConfigAfter, StringComparison.Ordinal);
        Assert.Contains("\"Root\": \"docs\"", result.RepoConfigAfter, StringComparison.Ordinal);
        Assert.Contains("\"Note\": 1", result.RepoConfigAfter, StringComparison.Ordinal);
        Assert.Contains("\".md\"", result.RepoConfigAfter, StringComparison.Ordinal);
        Assert.Contains("\"Index\": \"old\"", File.ReadAllText(Path.Combine(this.Repo, ".agency-index.json")), StringComparison.Ordinal);
    }

    /// <summary>Verifies doctor reports the repo file, warns about ignored keys, and shows where each default came from.</summary>
    [Fact]
    public async Task Doctor_ReportsRepoConfigIgnoredKeysAndDefaultSources()
    {
        this._dir.Write("repo/.agency-index.json", """{ "Index": "docs", "Embedding": { "BaseUrl": "http://evil.example/v1" } }""");

        IReadOnlyList<DoctorCheck> checks = await Doctor.RunAsync(
            CliArguments.Parse(["doctor"]),
            this.Home,
            Path.Combine(this._dir.Path, "profile"),
            this.Repo,
            new HttpClient(new StubEndpoint { Down = true }),
            Ct);

        DoctorCheck repo = Assert.Single(checks, c => c.Name == "repo_config");
        Assert.False(repo.Ok);
        Assert.Contains("Embedding", repo.Detail, StringComparison.Ordinal);
        Assert.Contains("indexer.json", repo.Fix, StringComparison.Ordinal);
        Assert.Contains("Index=docs (repo file)", Assert.Single(checks, c => c.Name == "defaults").Detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies uninstall previews and then removes this repo's config file along with its index and skill.</summary>
    [Fact]
    public async Task Uninstall_RepoScope_RemovesRepoConfigFile()
    {
        string file = Path.GetFullPath(this._dir.Write("repo/.agency-index.json", """{ "Index": "docs" }"""));

        UninstallResult preview = await Uninstaller.RunAsync(CliArguments.Parse(["uninstall"]), this.Home, Path.Combine(this._dir.Path, "profile"), this.Repo, Ct);
        Assert.Equal(file, preview.RepoConfigFile);
        Assert.True(File.Exists(file));

        UninstallResult applied = await Uninstaller.RunAsync(CliArguments.Parse(["uninstall", "--yes"]), this.Home, Path.Combine(this._dir.Path, "profile"), this.Repo, Ct);
        Assert.Equal(file, applied.RepoConfigFile);
        Assert.False(File.Exists(file));
    }

    private void SetEnvironment(string name, string? value)
    {
        this._savedEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
}
