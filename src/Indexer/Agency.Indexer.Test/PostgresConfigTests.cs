using static Agency.Indexer.Test.DoctorTests;

namespace Agency.Indexer.Test;

/// <summary>
/// Tests for how <c>setup</c> and <c>doctor</c> treat the storage provider and the PostgreSQL connection string, which
/// carries the password and so must never be written to a file. None of these need a PostgreSQL server.
/// </summary>
public sealed class PostgresConfigTests
{
    private static readonly Agency.Embeddings.OpenAI.EmbeddingOptions Embedding = new() { BaseUrl = "http://stub/v1", ModelId = "m", Dimensions = 4 };

    /// <summary>Verifies choosing PostgreSQL saves the provider but not the connection string, and keeps unrelated keys.</summary>
    [Fact]
    public void MergeConfig_Postgres_WritesProviderButNeverTheConnectionString()
    {
        string merged = Setup.MergeConfig("""{ "ChunkSize": 256 }""", Embedding, StorageProvider.Postgres, sqlitePath: "Host=h;Password=secret");

        Assert.Contains("\"Provider\": \"postgres\"", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("Database", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", merged, StringComparison.Ordinal);
        Assert.Contains("\"ChunkSize\": 256", merged, StringComparison.Ordinal);
    }

    /// <summary>Verifies choosing SQLite saves the provider and the file path, and no choice writes neither.</summary>
    [Fact]
    public void MergeConfig_SqliteOrNoChoice_WritesPathOnlyWhenChosen()
    {
        string sqlite = Setup.MergeConfig(null, Embedding, StorageProvider.Sqlite, sqlitePath: "C:/data/index.db");
        string none = Setup.MergeConfig(null, Embedding);

        Assert.Contains("\"Provider\": \"sqlite\"", sqlite, StringComparison.Ordinal);
        Assert.Contains("\"Database\": \"C:/data/index.db\"", sqlite, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider", none, StringComparison.Ordinal);
        Assert.DoesNotContain("Database", none, StringComparison.Ordinal);
    }

    /// <summary>Verifies setup saves the provider choice, warns that the connection string was not saved, and an unreachable server is a clear usage error.</summary>
    [Fact]
    public async Task Setup_PostgresUnreachable_ExplainsInsteadOfFailingObscurely()
    {
        using var dir = new TempDirectory();
        string work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(work);

        UsageException ex = await Assert.ThrowsAsync<UsageException>(() => Setup.RunAsync(
            CliArguments.Parse(["setup", "--provider", "postgres", "--db", "Host=127.0.0.1;Port=9;Username=u;Password=topsecret;Timeout=2", "--embedding-url", "http://stub/v1", "--embedding-model", "m", "--no-index"]),
            Path.Combine(dir.Path, "home"),
            Path.Combine(dir.Path, "profile"),
            work,
            new HttpClient(new StubEndpoint { Models = ["m"], VectorLength = 4 }),
            TestContext.Current.CancellationToken));

        Assert.Contains("AGENCY_INDEX_Database", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a password in a stored connection string is detected in both common forms, and a passwordless one is not.</summary>
    [Theory]
    [InlineData("""{ "Database": "Host=db;Username=u;Password=s3cret;Database=x" }""", true)]
    [InlineData("""{ "Database": "Host=db;Username=u;pwd=s3cret" }""", true)]
    [InlineData("""{ "Database": "postgresql://u:s3cret@db:5432/x" }""", true)]
    [InlineData("""{ "Database": "Host=db;Username=u;Database=x" }""", false)]
    [InlineData("""{ "Database": "C:/Users/me/.agency/index.db" }""", false)]
    [InlineData("""{ "Provider": "postgres" }""", false)]
    public void StoresDatabasePassword_DetectsPasswords(string json, bool expected) =>
        Assert.Equal(expected, Doctor.StoresDatabasePassword(json));

    /// <summary>Verifies doctor reports a stored password by where it is, never by printing it.</summary>
    [Fact]
    public async Task Doctor_PasswordInConfigFile_ReportsLocationNotValue()
    {
        using var dir = new TempDirectory();
        dir.Write("home/indexer.json", """
            { "Provider": "postgres", "Database": "Host=127.0.0.1;Port=9;Username=u;Password=topsecret;Timeout=2",
              "Embedding": { "BaseUrl": "http://localhost:1234/v1", "ModelId": "m", "Dimensions": 4 } }
            """);
        string work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(work);

        IReadOnlyList<DoctorCheck> checks = await Doctor.RunAsync(
            CliArguments.Parse(["doctor"]),
            Path.Combine(dir.Path, "home"),
            Path.Combine(dir.Path, "profile"),
            work,
            new HttpClient(new StubEndpoint { Down = true }),
            TestContext.Current.CancellationToken);

        DoctorCheck credentials = Assert.Single(checks, c => c.Name == "database_credentials");
        Assert.False(credentials.Ok);
        Assert.Contains("AGENCY_INDEX_Database", credentials.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", System.Text.Json.JsonSerializer.Serialize(checks), StringComparison.Ordinal);
    }
}
