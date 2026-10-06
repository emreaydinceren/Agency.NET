using Agency.Embeddings.OpenAI;
using Microsoft.Extensions.Configuration;

namespace Agency.Indexer;

/// <summary>Which database backs the index.</summary>
internal enum StorageProvider
{
    /// <summary>A local SQLite file.</summary>
    Sqlite,

    /// <summary>PostgreSQL with the pgvector extension.</summary>
    Postgres,
}

/// <summary>
/// Connection and model settings, resolved from (highest precedence first) command-line options,
/// <c>AGENCY_INDEX_*</c> environment variables, and <c>~/.agency/indexer.json</c>.
/// </summary>
/// <param name="Provider">The storage backend.</param>
/// <param name="Database">The SQLite file path, or the PostgreSQL connection string.</param>
/// <param name="Embedding">The OpenAI-compatible embedding endpoint settings.</param>
/// <param name="ChunkSize">Maximum tokens per chunk.</param>
/// <param name="ChunkOverlap">Tokens shared between consecutive chunks.</param>
internal sealed record IndexerSettings(StorageProvider Provider, string Database, EmbeddingOptions Embedding, int ChunkSize, int ChunkOverlap)
{
    /// <summary>The prefix of the environment variables the settings are read from.</summary>
    public const string EnvironmentPrefix = "AGENCY_INDEX_";

    private static readonly Dictionary<string, string> OptionToKey = new(StringComparer.Ordinal)
    {
        ["provider"] = "Provider",
        ["db"] = "Database",
        ["embedding-url"] = "Embedding:BaseUrl",
        ["embedding-model"] = "Embedding:ModelId",
        ["embedding-key"] = "Embedding:ApiKey",
        ["dimensions"] = "Embedding:Dimensions",
    };

    /// <summary>The default per-user directory holding <c>indexer.json</c> and the default SQLite database.</summary>
    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agency");

    /// <summary>Resolves the settings for <paramref name="args"/>.</summary>
    public static IndexerSettings Resolve(CliArguments args, string home)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(home, "indexer.json"), optional: true)
            .AddEnvironmentVariables(EnvironmentPrefix)
            .AddInMemoryCollection(OptionToKey
                .Where(map => args.Get(map.Key) is not null)
                .Select(map => KeyValuePair.Create(map.Value, args.Get(map.Key))))
            .Build();

        StorageProvider provider = (config["Provider"] ?? "sqlite").ToLowerInvariant() switch
        {
            "sqlite" => StorageProvider.Sqlite,
            "postgres" => StorageProvider.Postgres,
            string other => throw new UsageException($"Unknown provider '{other}'. Expected 'sqlite' or 'postgres'."),
        };

        string database = config["Database"]
            ?? (provider == StorageProvider.Sqlite
                ? Path.Combine(home, "index.db")
                : throw new UsageException("Postgres needs a connection string: --db, AGENCY_INDEX_Database, or \"Database\" in indexer.json."));

        var embedding = new EmbeddingOptions();
        config.GetSection(EmbeddingOptions.SectionName).Bind(embedding);
        embedding.ApiKey = ResolveApiKey(embedding.BaseUrl, embedding.ApiKey);
        embedding.Dimensions ??= 1024;

        return new IndexerSettings(
            provider,
            database,
            embedding,
            config.GetValue("ChunkSize", 512),
            config.GetValue("ChunkOverlap", 64));
    }

    /// <summary>The key sent to endpoints that do not check it (LM Studio, Ollama); the OpenAI SDK needs a non-empty value.</summary>
    public const string NoKey = "unused";

    /// <summary>
    /// The environment variable that conventionally holds the API key for a hosted provider, or <see langword="null"/>
    /// for any other endpoint (a local server, which needs no key).
    /// </summary>
    public static string? KeyVariableFor(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.Host switch
        {
            string host when host.EndsWith("openrouter.ai", StringComparison.OrdinalIgnoreCase) => "OPENROUTER_API_KEY",
            string host when host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) => "OPENAI_API_KEY",
            _ => null,
        };
    }

    /// <summary>
    /// The key to send: an explicitly configured one, else (for a hosted provider) the provider's conventional
    /// environment variable, else <see cref="NoKey"/>. A key found in the environment is never written to configuration.
    /// </summary>
    /// <param name="baseUrl">The embeddings endpoint.</param>
    /// <param name="configured">The key from options, environment or <c>indexer.json</c>, if any.</param>
    /// <param name="getEnvironmentVariable">Environment lookup; defaults to the process environment.</param>
    public static string ResolveApiKey(string? baseUrl, string? configured, Func<string, string?>? getEnvironmentVariable = null)
    {
        if (!string.IsNullOrEmpty(configured) && configured != NoKey)
        {
            return configured;
        }

        string? variable = KeyVariableFor(baseUrl);
        string? fromEnvironment = variable is null ? null : (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(variable);
        return string.IsNullOrEmpty(fromEnvironment) ? NoKey : fromEnvironment;
    }

    /// <summary>Throws <see cref="UsageException"/> unless an embedding endpoint and model are configured.</summary>
    public void RequireEmbedding()
    {
        if (string.IsNullOrWhiteSpace(this.Embedding.BaseUrl) || string.IsNullOrWhiteSpace(this.Embedding.ModelId))
        {
            throw new UsageException(
                "No embedding endpoint configured. Set --embedding-url and --embedding-model, the AGENCY_INDEX_Embedding__BaseUrl / " +
                "AGENCY_INDEX_Embedding__ModelId environment variables, or \"Embedding\": { \"BaseUrl\", \"ModelId\" } in ~/.agency/indexer.json.");
        }
    }
}
