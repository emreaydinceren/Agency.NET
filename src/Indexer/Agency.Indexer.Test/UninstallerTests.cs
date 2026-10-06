namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="Uninstaller"/> over a temporary home, repo and user profile.</summary>
public sealed class UninstallerTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose() => this._dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Home => Path.Combine(this._dir.Path, "home");

    private string Work => Path.Combine(this._dir.Path, "work");

    private string Profile => Path.Combine(this._dir.Path, "profile");

    private string Database => Path.Combine(this.Home, "index.db");

    /// <summary>Verifies a preview lists everything for the scope and changes nothing.</summary>
    [Fact]
    public async Task RunAsync_WithoutYes_PreviewsAndChangesNothing()
    {
        await this.ArrangeAsync();

        UninstallResult result = await this.RunAsync("uninstall", "--scope", "all");

        Assert.Equal("preview", result.Status);
        Assert.Equal(2, result.Skills.Count);
        Assert.Equal(["mine", "theirs"], result.Indexes.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.All(result.Indexes, i => Assert.Equal("would_drop", i.Action));
        Assert.Contains(result.DataFiles, f => f.EndsWith("index.db", StringComparison.Ordinal));
        Assert.NotNull(result.ConfigFile);
        Assert.Contains(result.Remaining, r => r.StartsWith("dotnet tool uninstall", StringComparison.Ordinal));
        Assert.True(File.Exists(this.Database));
        Assert.True(File.Exists(Path.Combine(this.Home, "indexer.json")));
        Assert.True(File.Exists(this.RepoSkill));
        Assert.True(File.Exists(this.UserSkill));
    }

    /// <summary>Verifies the default repo scope drops only this repo's index and skill and leaves everything shared.</summary>
    [Fact]
    public async Task RunAsync_RepoScopeWithYes_RemovesOnlyThisRepo()
    {
        await this.ArrangeAsync();

        UninstallResult result = await this.RunAsync("uninstall", "--yes");

        Assert.Equal("ok", result.Status);
        Assert.Equal("dropped", result.Indexes.Single(i => i.Name == "mine").Action);
        Assert.Equal("keep", result.Indexes.Single(i => i.Name == "theirs").Action);
        Assert.False(File.Exists(this.RepoSkill));
        Assert.True(File.Exists(this.UserSkill));
        Assert.True(File.Exists(this.Database));
        Assert.True(File.Exists(Path.Combine(this.Home, "indexer.json")));
        Assert.Empty(result.Remaining);

        IndexService service = await Services.SqliteAsync(this.Database, new FakeEmbeddingGenerator());
        Assert.Equal(["theirs"], (await service.ListIndexesAsync(Ct)).Select(i => i.Index));
    }

    /// <summary>Verifies the all scope removes every skill copy, the database files and the config, and hands back the tool step.</summary>
    [Fact]
    public async Task RunAsync_AllScopeWithYes_RemovesEverythingButTheTool()
    {
        await this.ArrangeAsync();

        UninstallResult result = await this.RunAsync("uninstall", "--scope", "all", "--yes");

        Assert.Equal("ok", result.Status);
        Assert.False(File.Exists(this.RepoSkill));
        Assert.False(File.Exists(this.UserSkill));
        Assert.False(File.Exists(this.Database));
        Assert.False(Directory.Exists(this.Home));
        Assert.Contains(result.Remaining, r => r.StartsWith("dotnet tool uninstall", StringComparison.Ordinal));
    }

    /// <summary>Verifies an index being written blocks deleting the data, and the outcome says so.</summary>
    [Fact]
    public async Task RunAsync_AllScopeWithIndexLocked_LeavesDataAndReportsPartial()
    {
        await this.ArrangeAsync();
        await using IAsyncDisposable? held = await new FileWriterLock(this.Database).TryAcquireAsync("theirs", Ct);

        UninstallResult result = await this.RunAsync("uninstall", "--scope", "all", "--yes");

        Assert.Equal("partial", result.Status);
        Assert.Equal("locked", result.Indexes.Single(i => i.Name == "theirs").Action);
        Assert.True(File.Exists(this.Database));
        Assert.True(File.Exists(Path.Combine(this.Home, "indexer.json")));
        Assert.Contains("theirs", result.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies nothing installed means nothing to do, and no database is created by looking.</summary>
    [Fact]
    public async Task RunAsync_NothingInstalled_IsOkAndCreatesNothing()
    {
        UninstallResult result = await this.RunAsync("uninstall", "--scope", "all", "--yes");

        Assert.Equal("ok", result.Status);
        Assert.Empty(result.Skills);
        Assert.Empty(result.Indexes);
        Assert.False(File.Exists(this.Database));
    }

    /// <summary>Verifies an unknown scope is a usage error.</summary>
    [Fact]
    public async Task RunAsync_UnknownScope_Throws() =>
        await Assert.ThrowsAsync<UsageException>(() => this.RunAsync("uninstall", "--scope", "user"));

    private string RepoSkill => Path.Combine(this.Work, ".claude", "skills", "agency-index", "SKILL.md");

    private string UserSkill => Path.Combine(this.Profile, ".claude", "skills", "agency-index", "SKILL.md");

    private Task<UninstallResult> RunAsync(params string[] args) =>
        Uninstaller.RunAsync(CliArguments.Parse(args), this.Home, this.Profile, this.Work, Ct);

    /// <summary>One index inside the repo, one outside it, both skill scopes and a config file.</summary>
    private async Task ArrangeAsync()
    {
        this._dir.Write("work/docs/a.md", "alpha");
        this._dir.Write("other/b.md", "beta");
        this._dir.Write("home/indexer.json", """{ "Embedding": { "BaseUrl": "http://localhost:1234/v1", "ModelId": "fake-model" } }""");
        IndexService service = await Services.SqliteAsync(this.Database, new FakeEmbeddingGenerator());
        await service.IndexAsync(new IndexRequest("mine", Path.Combine(this.Work, "docs"), null, null, FileScanner.DefaultMaxFileBytes, Wait: false), Ct);
        await service.IndexAsync(new IndexRequest("theirs", Path.Combine(this._dir.Path, "other"), null, null, FileScanner.DefaultMaxFileBytes, Wait: false), Ct);
        await SkillInstaller.InstallAsync([Path.Combine(this.Work, ".claude", "skills")], Ct);
        await SkillInstaller.InstallAsync([Path.Combine(this.Profile, ".claude", "skills")], Ct);
    }
}
