using static Agency.Indexer.Test.DoctorTests;

namespace Agency.Indexer.Test;

/// <summary>
/// Tests for how the embeddings API key is resolved and kept out of files. Tests that change environment variables
/// live in this one class (xUnit runs a class's tests one at a time) and restore them on dispose.
/// </summary>
public sealed class ApiKeyTests : IDisposable
{
    private const string Secret = "sk-test-secret-123";
    private readonly Dictionary<string, string?> _saved = [];

    /// <summary>Restores every environment variable a test changed.</summary>
    public void Dispose()
    {
        foreach ((string name, string? value) in this._saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Verifies an explicitly configured key wins over the environment.</summary>
    [Fact]
    public void ResolveApiKey_ConfiguredKey_Wins() =>
        Assert.Equal("configured", IndexerSettings.ResolveApiKey("https://api.openai.com/v1", "configured", _ => "from-env"));

    /// <summary>Verifies a hosted provider's conventional variable is read when no key is configured.</summary>
    [Theory]
    [InlineData("https://api.openai.com/v1", "OPENAI_API_KEY")]
    [InlineData("https://openrouter.ai/api/v1", "OPENROUTER_API_KEY")]
    public void ResolveApiKey_HostedProvider_ReadsConventionalVariable(string baseUrl, string variable)
    {
        string key = IndexerSettings.ResolveApiKey(baseUrl, null, name => name == variable ? "from-env" : null);

        Assert.Equal("from-env", key);
        Assert.Equal(variable, IndexerSettings.KeyVariableFor(baseUrl));
    }

    /// <summary>Verifies a local server gets the placeholder and never reads a provider's variable.</summary>
    [Fact]
    public void ResolveApiKey_LocalServer_ReturnsPlaceholderWithoutReadingEnvironment()
    {
        string key = IndexerSettings.ResolveApiKey("http://localhost:1234/v1", null, _ => throw new InvalidOperationException("must not be read"));

        Assert.Equal(IndexerSettings.NoKey, key);
        Assert.Null(IndexerSettings.KeyVariableFor("http://localhost:1234/v1"));
    }

    /// <summary>Verifies setup refuses a hosted endpoint with no key, naming the variable to set.</summary>
    [Fact]
    public async Task Setup_HostedEndpointWithoutKey_ThrowsNamingVariable()
    {
        using var dir = new TempDirectory();
        this.SetEnvironment("OPENAI_API_KEY", null);

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() =>
            RunSetupAsync(dir, ["setup", "--endpoint", "openai"], new StubEndpoint()));

        Assert.Contains("OPENAI_API_KEY", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a key from the environment is sent to the endpoint but never written to any file or the output.</summary>
    [Fact]
    public async Task Setup_KeyFromEnvironment_IsSentButNeverWritten()
    {
        using var dir = new TempDirectory();
        this.SetEnvironment("OPENROUTER_API_KEY", Secret);
        var endpoint = new StubEndpoint { Models = ["openai/text-embedding-3-small"], VectorLength = 4 };

        SetupResult result = await RunSetupAsync(dir, ["setup", "--endpoint", "openrouter", "--yes"], endpoint);

        Assert.Equal($"Bearer {Secret}", endpoint.LastAuthorization);
        Assert.DoesNotContain(Secret, result.ConfigAfter, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(dir.Path, "home", "indexer.json")), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>Verifies a key passed on the command line is used for the probe but not saved, and the caller is told.</summary>
    [Fact]
    public async Task Setup_KeyFromCommandLine_IsNotSavedAndWarns()
    {
        using var dir = new TempDirectory();
        this.SetEnvironment("OPENAI_API_KEY", null);
        var endpoint = new StubEndpoint { Models = ["text-embedding-3-small"], VectorLength = 4 };

        SetupResult result = await RunSetupAsync(dir, ["setup", "--endpoint", "openai", "--embedding-key", Secret, "--yes"], endpoint);

        Assert.Equal($"Bearer {Secret}", endpoint.LastAuthorization);
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(dir.Path, "home", "indexer.json")), StringComparison.Ordinal);
        Assert.Contains("not saved", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a key that came from the old endpoint's variable is not carried to a different host.</summary>
    [Fact]
    public async Task Setup_SwitchingHost_DoesNotSendTheOldProvidersKey()
    {
        using var dir = new TempDirectory();
        this.SetEnvironment("OPENAI_API_KEY", Secret);
        this.SetEnvironment("OPENROUTER_API_KEY", null);
        dir.Write("home/indexer.json", """{ "Embedding": { "BaseUrl": "https://api.openai.com/v1" } }""");

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() =>
            RunSetupAsync(dir, ["setup", "--endpoint", "openrouter"], new StubEndpoint()));

        Assert.Contains("OPENROUTER_API_KEY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies doctor reports a hosted endpoint with no key, names the variable and does not probe the endpoint.</summary>
    [Fact]
    public async Task Doctor_HostedEndpointWithoutKey_ReportsApiKeyCheck()
    {
        using var dir = new TempDirectory();
        this.SetEnvironment("OPENROUTER_API_KEY", null);
        dir.Write("home/indexer.json", """{ "Embedding": { "BaseUrl": "https://openrouter.ai/api/v1", "ModelId": "m" } }""");

        IReadOnlyList<DoctorCheck> checks = await RunDoctorAsync(dir);

        DoctorCheck apiKey = Assert.Single(checks, c => c.Name == "api_key");
        Assert.False(apiKey.Ok);
        Assert.Contains("OPENROUTER_API_KEY", apiKey.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(checks, c => c.Name == "endpoint");
    }

    /// <summary>Verifies doctor reports where a key came from without ever printing it.</summary>
    [Fact]
    public async Task Doctor_KeyFromEnvironment_NamesSourceNotValue()
    {
        using var dir = new TempDirectory();
        this.SetEnvironment("OPENROUTER_API_KEY", Secret);
        dir.Write("home/indexer.json", """{ "Embedding": { "BaseUrl": "https://openrouter.ai/api/v1", "ModelId": "m" } }""");

        IReadOnlyList<DoctorCheck> checks = await RunDoctorAsync(dir);

        DoctorCheck apiKey = Assert.Single(checks, c => c.Name == "api_key");
        Assert.True(apiKey.Ok);
        Assert.Contains("OPENROUTER_API_KEY", apiKey.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(checks), StringComparison.Ordinal);
    }

    /// <summary>Verifies doctor flags a key stored in plain text in indexer.json.</summary>
    [Fact]
    public async Task Doctor_KeyInConfigFile_ReportsPlainTextStorage()
    {
        using var dir = new TempDirectory();
        dir.Write("home/indexer.json", $$"""{ "Embedding": { "BaseUrl": "http://localhost:1234/v1", "ModelId": "m", "ApiKey": "{{Secret}}" } }""");

        IReadOnlyList<DoctorCheck> checks = await RunDoctorAsync(dir);

        DoctorCheck storage = Assert.Single(checks, c => c.Name == "api_key_storage");
        Assert.False(storage.Ok);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(checks), StringComparison.Ordinal);
    }

    private void SetEnvironment(string name, string? value)
    {
        this._saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private static Task<SetupResult> RunSetupAsync(TempDirectory dir, string[] args, StubEndpoint endpoint)
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

    private static Task<IReadOnlyList<DoctorCheck>> RunDoctorAsync(TempDirectory dir)
    {
        string work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(work);
        return Doctor.RunAsync(
            CliArguments.Parse(["doctor"]),
            Path.Combine(dir.Path, "home"),
            Path.Combine(dir.Path, "profile"),
            work,
            new HttpClient(new StubEndpoint { Down = true }),
            TestContext.Current.CancellationToken);
    }
}
