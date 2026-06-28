using Microsoft.Extensions.Configuration;

namespace Agency.Configuration.Test;

/// <summary>
/// Tests for <see cref="AgencyConfigurationBuilderExtensions.AddSharedConfiguration"/>.
/// </summary>
public sealed class SharedConfigurationTests : IDisposable
{
    // Isolated temp directory per test-class instance so parallel runs don't collide.
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AgencySharedConfigTest-{Guid.NewGuid():N}");

    public SharedConfigurationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>Writes JSON to a uniquely named file in the temp dir and returns the file name.</summary>
    private string WriteJson(string json)
    {
        var fileName = $"shared-{Guid.NewGuid():N}.json";
        File.WriteAllText(Path.Combine(_tempDir, fileName), json);
        return fileName;
    }

    // (a) Keys from a shared JSON file are visible in the merged configuration.
    [Fact]
    public void KeysFromSharedFile_AreVisibleInMergedConfig()
    {
        var fileName = WriteJson("""{ "Shared": { "Host": "http://example.local:1234" } }""");

        IConfigurationRoot config = new ConfigurationBuilder()
            .SetBasePath(_tempDir)
            .AddSharedConfiguration(fileName, optional: false)
            .Build();

        Assert.Equal("http://example.local:1234", config["Shared:Host"]);
    }

    // (b) optional: true with a missing file — Build() must NOT throw.
    [Fact]
    public void MissingFile_WithOptionalTrue_DoesNotThrow()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .SetBasePath(_tempDir)
            .AddSharedConfiguration("does-not-exist.json", optional: true)
            .Build();

        // The key is simply absent; no exception was thrown above.
        Assert.Null(config["Shared:Host"]);
    }

    // (c) Precedence: AddSharedConfiguration inserts at the FRONT (lowest precedence), so any
    //     other source — even one registered before the call — overrides the shared file. This
    //     mirrors the host scenario where appsettings/env/CLI sources must shadow shared defaults.
    [Fact]
    public void SharedFile_IsLowestPrecedence_OverriddenByOtherSource()
    {
        var fileName = WriteJson("""{ "Contest:Key": "from-shared-file" }""");

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Contest:Key"] = "from-memory" })
            .SetBasePath(_tempDir)
            .AddSharedConfiguration(fileName, optional: false)
            .Build();

        // Shared file was inserted at the front → the in-memory source wins.
        Assert.Equal("from-memory", config["Contest:Key"]);
    }

    // (d) End-to-end override model: a shared default referenced by a ${…} placeholder is
    //     overridden by a higher-precedence source (here an in-memory source standing in for an
    //     environment variable), and the override flows through the placeholder expansion.
    [Fact]
    public void EnvOverride_OfSharedDefault_FlowsThroughPlaceholder()
    {
        var fileName = WriteJson("""{ "LLmClients": { "OpenAI": { "BaseUrl": "http://shared-default:1234/v1" } } }""");

        IConfigurationRoot config = new ConfigurationBuilder()
            .SetBasePath(_tempDir)
            // appsettings-equivalent: a value that references the shared default by placeholder.
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Embedding:BaseUrl"] = "${LLmClients:OpenAI:BaseUrl}" })
            // env-var-equivalent: overrides the shared default (added after, so higher precedence).
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["LLmClients:OpenAI:BaseUrl"] = "http://override:9999/v1" })
            .AddSharedConfiguration(fileName, optional: false)   // inserted at the front (lowest)
            .AddPlaceholderResolver()                            // last — expands ${…}
            .Build();

        Assert.Equal("http://override:9999/v1", config["Embedding:BaseUrl"]);
    }

    // (e) Host-type regression guard: the same front-insertion + override-through-placeholder
    //     model must hold on a real ConfigurationManager (the type Host.CreateApplicationBuilder
    //     exposes as builder.Configuration), whose Build() returns this and which applies sources
    //     eagerly. This is the scenario the precedence fix actually targets.
    [Fact]
    public void OverConfigurationManager_SharedFile_IsLowestPrecedence_AndOverrideFlowsThroughPlaceholder()
    {
        var fileName = WriteJson("""{ "LLmClients": { "OpenAI": { "BaseUrl": "http://shared-default:1234/v1" } } }""");

        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).SetBasePath(_tempDir);

        // appsettings-equivalent: references the shared default by placeholder.
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Embedding:BaseUrl"] = "${LLmClients:OpenAI:BaseUrl}" });
        // env-var-equivalent: overrides the shared default.
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["LLmClients:OpenAI:BaseUrl"] = "http://override:9999/v1" });

        // Inserts the shared file at the FRONT even though these sources already exist.
        configuration.AddSharedConfiguration(fileName, optional: false);
        configuration.AddPlaceholderResolver();

        // Shared default is shadowed by the override, and the override flows through the token.
        Assert.Equal("http://override:9999/v1", configuration["LLmClients:OpenAI:BaseUrl"]);
        Assert.Equal("http://override:9999/v1", configuration["Embedding:BaseUrl"]);
    }

    // (f) Host-shaped repro: ConfigurationManager that ALREADY has a JSON FILE source (like the
    //     host's appsettings.json), then AddSharedConfiguration inserts another file at the front.
    //     The pre-existing file's keys must still be readable.
    [Fact]
    public void OverConfigurationManager_WithExistingFileSource_FrontInsert_PreservesExistingKeys()
    {
        var appsettings = WriteJson("""{ "Agent": { "DefaultModel": "google/gemma-4-e2b" } }""");
        var shared = WriteJson("""{ "LLmClients": { "OpenAI": { "BaseUrl": "http://shared:1234/v1" } } }""");

        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).SetBasePath(_tempDir);
        configuration.AddJsonFile(appsettings, optional: false, reloadOnChange: false);

        configuration.AddSharedConfiguration(shared, optional: false);

        Assert.Equal("google/gemma-4-e2b", configuration["Agent:DefaultModel"]);
        Assert.Equal("http://shared:1234/v1", configuration["LLmClients:OpenAI:BaseUrl"]);
    }
}
