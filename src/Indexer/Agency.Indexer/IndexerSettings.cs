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
/// The per-repo defaults for <c>index</c>/<c>search</c>/...: what to index and under which name. Resolved from the same
/// layers as <see cref="IndexerSettings"/>; <paramref name="Sources"/> records which layer each value came from.
/// </summary>
/// <param name="Index">The index name.</param>
/// <param name="Root">The directory to index.</param>
/// <param name="Extensions">The comma-separated extensions to select.</param>
/// <param name="Names">The comma-separated extensionless file names to select.</param>
/// <param name="MaxFileKb">The per-file size cap in KiB.</param>
/// <param name="Exclude">The comma-separated globs of files and folders to leave out.</param>
/// <param name="Sources">For each key that has a value, <c>command line</c>, <c>environment</c>, <c>repo file</c> or <c>user file</c>.</param>
/// <param name="RepoConfigPath">The repo config file in effect, if any.</param>
/// <param name="IgnoredRepoKeys">Keys in the repo file that are not allowed there and were ignored.</param>
internal sealed record IndexDefaults(
    string? Index,
    string? Root,
    string? Extensions,
    string? Names,
    int? MaxFileKb,
    string? Exclude,
    IReadOnlyDictionary<string, string> Sources,
    string? RepoConfigPath,
    IReadOnlyList<string> IgnoredRepoKeys)
{
    /// <summary>No defaults and no repo file.</summary>
    public static IndexDefaults None { get; } = new(null, null, null, null, null, null, new Dictionary<string, string>(), null, []);
}

/// <summary>
/// Connection and model settings, resolved from (highest precedence first) command-line options,
/// <c>AGENCY_INDEX_*</c> environment variables, the repo's <c>.agency-index.json</c> (index defaults only), and
/// <c>~/.agency/indexer.json</c>.
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
        ["min-score"] = "Search:MinScore",
    };

    /// <summary>The index defaults: configuration key, and the command-line option that sets it.</summary>
    private static readonly (string Key, string Option)[] DefaultKeys =
    [
        ("Index", "index"), ("Root", "root"), ("Extensions", "ext"), ("Names", "names"), ("MaxFileKb", "max-file-kb"), ("Exclude", "exclude"),
    ];

    /// <summary>
    /// The lowest score a search hit may have (<c>Search:MinScore</c>, <c>AGENCY_INDEX_Search__MinScore</c> or <c>--min-score</c>),
    /// or <see langword="null"/> for none. Scores depend on the embedding model, so this is a user-level setting with no default
    /// and a repo file cannot set it.
    /// </summary>
    public double? SearchMinScore { get; init; }

    /// <summary>The repo-level defaults and where they came from; <see cref="IndexDefaults.None"/> unless set by <see cref="Resolve"/>.</summary>
    public IndexDefaults Defaults { get; init; } = IndexDefaults.None;

    /// <summary>The default per-user directory holding <c>indexer.json</c> and the default SQLite database.</summary>
    public static string DefaultHome => Path.Combine(UserProfile, ".agency");

    /// <summary>
    /// The user profile folder that holds <c>.agency</c> and the user-scope skill folders: <c>AGENCY_INDEX_HOME</c> when set
    /// (to run against a sandbox, in CI or in tests, since .NET ignores a <c>USERPROFILE</c> override on Windows), else the
    /// real profile.
    /// </summary>
    public static string UserProfile =>
        Environment.GetEnvironmentVariable("AGENCY_INDEX_HOME") is { Length: > 0 } overridden
            ? overridden
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Resolves the settings for <paramref name="args"/>.</summary>
    /// <param name="args">The parsed command line.</param>
    /// <param name="home">The folder holding the user-level <c>indexer.json</c> and the default database.</param>
    /// <param name="workingDirectory">Where to start looking for a repo-level <c>.agency-index.json</c>, or <see langword="null"/> to use none.</param>
    public static IndexerSettings Resolve(CliArguments args, string home, string? workingDirectory = null)
    {
        string userFile = Path.Combine(home, "indexer.json");
        string? repoPath = workingDirectory is null ? null : RepoLocator.FindConfig(workingDirectory);
        RepoConfig? repo = repoPath is null ? null : RepoConfig.Load(repoPath);

        // Connection settings: user file < environment < command line. The repo file never contributes here.
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(userFile, optional: true)
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
                : throw new UsageException("Postgres needs a connection string: set the AGENCY_INDEX_Database environment variable (it contains the password, so keep it out of files), or pass --db."));

        // Indexing is unattended and a local model can time out under load, so transient failures wait before retrying.
        var embedding = new EmbeddingOptions { RetryDelayMs = DefaultRetryDelayMs };
        config.GetSection(EmbeddingOptions.SectionName).Bind(embedding);
        embedding.ApiKey = ResolveApiKey(embedding.BaseUrl, embedding.ApiKey);
        embedding.Dimensions ??= 1024;

        return new IndexerSettings(
            provider,
            database,
            embedding,
            config.GetValue("ChunkSize", 512),
            config.GetValue("ChunkOverlap", 64))
        {
            Defaults = ResolveDefaults(args, userFile, repo),
            SearchMinScore = ParseMinScore(config["Search:MinScore"]),
        };
    }

    private static double? ParseMinScore(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score) && score is >= 0 and <= 1
                ? score
                : throw new UsageException("Search:MinScore (--min-score) must be a number between 0 and 1.");

    /// <summary>
    /// Resolves each index default from the first layer that sets it: command line, environment, repo file, user file.
    /// A layer's value replaces the lower layers' value whole (a list is not merged element by element).
    /// </summary>
    private static IndexDefaults ResolveDefaults(CliArguments args, string userFile, RepoConfig? repo)
    {
        IConfiguration user = new ConfigurationBuilder().AddJsonFile(userFile, optional: true).Build();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string option) in DefaultKeys)
        {
            (string? value, string? source) = args.Get(option) is { } fromCli ? (fromCli, "command line")
                : Environment.GetEnvironmentVariable(EnvironmentPrefix + key) is { Length: > 0 } fromEnvironment ? (fromEnvironment, "environment")
                : repo is not null && repo.Values.TryGetValue(key, out string? fromRepo) ? (fromRepo, "repo file")
                : ListOrValue(user, key) is { } fromUser ? (fromUser, "user file")
                : (null, null);
            if (value is not null && source is not null)
            {
                values[key] = value;
                sources[key] = source;
            }
        }

        int? maxFileKb = null;
        if (values.TryGetValue("MaxFileKb", out string? rawMax))
        {
            maxFileKb = int.TryParse(rawMax, out int kb) && kb > 0
                ? kb
                : throw new UsageException($"MaxFileKb must be a positive integer (from the {sources["MaxFileKb"]}).");
        }

        return new IndexDefaults(
            values.GetValueOrDefault("Index"),
            values.GetValueOrDefault("Root"),
            values.GetValueOrDefault("Extensions"),
            values.GetValueOrDefault("Names"),
            maxFileKb,
            values.GetValueOrDefault("Exclude"),
            sources,
            repo?.Path,
            repo?.Ignored ?? []);
    }

    /// <summary>A scalar value, or a JSON array joined with commas (extensions and names are lists); <see langword="null"/> if unset.</summary>
    private static string? ListOrValue(IConfiguration config, string key)
    {
        IConfigurationSection section = config.GetSection(key);
        List<string?> items = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return items.Count > 0 ? string.Join(',', items) : (string.IsNullOrWhiteSpace(section.Value) ? null : section.Value);
    }

    /// <summary>The first retry wait for a transient embedding failure; each further retry doubles it.</summary>
    public const int DefaultRetryDelayMs = 1000;

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
