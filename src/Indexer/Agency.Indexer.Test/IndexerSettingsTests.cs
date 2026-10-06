namespace Agency.Indexer.Test;

/// <summary>
/// Tests for <see cref="IndexerSettings.Resolve"/>. Environment variables are process-wide, so every test that
/// sets one lives in this class (xUnit runs a class's tests sequentially) and restores it afterwards.
/// </summary>
public sealed class IndexerSettingsTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly List<string> _setVariables = [];

    /// <summary>Clears the environment variables the test set and deletes the temporary home.</summary>
    public void Dispose()
    {
        foreach (string name in this._setVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        this._home.Dispose();
    }

    /// <summary>Verifies the defaults when no configuration is present anywhere.</summary>
    [Fact]
    public void Resolve_NoConfiguration_UsesDefaults()
    {
        IndexerSettings settings = this.Resolve("search");

        Assert.Equal(StorageProvider.Sqlite, settings.Provider);
        Assert.Equal(Path.Combine(this._home.Path, "index.db"), settings.Database);
        Assert.Equal("unused", settings.Embedding.ApiKey);
        Assert.Equal(1024, settings.Embedding.Dimensions);
        Assert.Equal(512, settings.ChunkSize);
        Assert.Equal(64, settings.ChunkOverlap);
        Assert.Throws<UsageException>(settings.RequireEmbedding);
    }

    /// <summary>Verifies command-line options override environment variables, which override indexer.json.</summary>
    [Fact]
    public void Resolve_LayeredSources_OptionsBeatEnvironmentBeatFile()
    {
        this._home.Write("indexer.json", """
            {
              "Provider": "Postgres",
              "Database": "from-file",
              "ChunkSize": 300,
              "Embedding": { "BaseUrl": "http://file/v1", "ModelId": "file-model", "Dimensions": 768 }
            }
            """);
        this.SetVariable("Database", "from-env");
        this.SetVariable("Embedding__ModelId", "env-model");

        IndexerSettings settings = this.Resolve("index", "--db", "from-option", "--dimensions", "384");

        Assert.Equal(StorageProvider.Postgres, settings.Provider);
        Assert.Equal("from-option", settings.Database);
        Assert.Equal("env-model", settings.Embedding.ModelId);
        Assert.Equal("http://file/v1", settings.Embedding.BaseUrl);
        Assert.Equal(384, settings.Embedding.Dimensions);
        Assert.Equal(300, settings.ChunkSize);
        settings.RequireEmbedding();
    }

    /// <summary>Verifies the embedding options map onto the embedding settings.</summary>
    [Fact]
    public void Resolve_EmbeddingOptions_AreApplied()
    {
        IndexerSettings settings = this.Resolve(
            "index", "--embedding-url", "http://opt/v1", "--embedding-model", "opt-model", "--embedding-key", "secret");

        Assert.Equal("http://opt/v1", settings.Embedding.BaseUrl);
        Assert.Equal("opt-model", settings.Embedding.ModelId);
        Assert.Equal("secret", settings.Embedding.ApiKey);
    }

    /// <summary>Verifies an unknown provider is a usage error.</summary>
    [Fact]
    public void Resolve_UnknownProvider_Throws()
    {
        Assert.Throws<UsageException>(() => this.Resolve("index", "--provider", "mysql"));
    }

    /// <summary>Verifies Postgres without a connection string is a usage error.</summary>
    [Fact]
    public void Resolve_PostgresWithoutDatabase_Throws()
    {
        Assert.Throws<UsageException>(() => this.Resolve("index", "--provider", "postgres"));
    }

    private IndexerSettings Resolve(params string[] args) =>
        IndexerSettings.Resolve(CliArguments.Parse(args), this._home.Path);

    private void SetVariable(string key, string value)
    {
        string name = IndexerSettings.EnvironmentPrefix + key;
        this._setVariables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>Verifies <c>AGENCY_INDEX_HOME</c> relocates the user profile, and with it the home folder.</summary>
    [Fact]
    public void UserProfile_EnvironmentOverride_RelocatesHome()
    {
        string? saved = Environment.GetEnvironmentVariable("AGENCY_INDEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("AGENCY_INDEX_HOME", "C:/sandbox/profile");

            Assert.Equal("C:/sandbox/profile", IndexerSettings.UserProfile);
            Assert.Equal(Path.Combine("C:/sandbox/profile", ".agency"), IndexerSettings.DefaultHome);

            Environment.SetEnvironmentVariable("AGENCY_INDEX_HOME", null);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), IndexerSettings.UserProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENCY_INDEX_HOME", saved);
        }
    }
}
