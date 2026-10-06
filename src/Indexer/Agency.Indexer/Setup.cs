using System.Text.Json;
using System.Text.Json.Nodes;
using Agency.Embeddings.OpenAI;
using Agency.VectorStore.Common;

namespace Agency.Indexer;

/// <summary>The outcome of the optional first index and smoke-test search run by <c>setup</c>.</summary>
/// <param name="Name">The index name.</param>
/// <param name="Root">The indexed directory.</param>
/// <param name="Status">The status of the index run.</param>
/// <param name="ChunksWritten">The chunks written.</param>
/// <param name="FailedFiles">The number of files that failed.</param>
/// <param name="TopHit">The best hit of the smoke-test search, or <see langword="null"/> if there was none.</param>
internal sealed record SetupIndex(string Name, string Root, IndexStatus Status, int ChunksWritten, int FailedFiles, SearchResultHit? TopHit);

/// <summary>What <c>setup</c> did, or with no <c>--yes</c> would do.</summary>
/// <param name="Status"><c>preview</c> (nothing written), <c>ok</c>, or <c>name_taken</c> (the index name belongs to another root).</param>
/// <param name="Message">A hint for the caller, if any.</param>
/// <param name="SkillScope">The skill scope used.</param>
/// <param name="Skill">The skill files written (or that would be written).</param>
/// <param name="ConfigPath">The <c>indexer.json</c> path.</param>
/// <param name="ConfigBefore">The existing file's text, or <see langword="null"/> if there is none.</param>
/// <param name="ConfigAfter">The text written (or that would be written).</param>
/// <param name="BaseUrl">The embeddings endpoint.</param>
/// <param name="ModelId">The embedding model.</param>
/// <param name="Dimensions">The vector length measured from the endpoint.</param>
/// <param name="Index">The first index, when one was requested and applied.</param>
/// <param name="RepoConfigPath">The repo-level <c>.agency-index.json</c> written (or that would be), when <c>--scope repo</c> and an index were given.</param>
/// <param name="RepoConfigBefore">The existing repo config's text, or <see langword="null"/> if there is none.</param>
/// <param name="RepoConfigAfter">The repo config text written (or that would be).</param>
internal sealed record SetupResult(
    string Status,
    string? Message,
    string SkillScope,
    IReadOnlyList<string> Skill,
    string ConfigPath,
    string? ConfigBefore,
    string ConfigAfter,
    string BaseUrl,
    string ModelId,
    int Dimensions,
    SetupIndex? Index,
    string? RepoConfigPath = null,
    string? RepoConfigBefore = null,
    string? RepoConfigAfter = null);

/// <summary>
/// The <c>setup</c> command: installs the skill, probes the embeddings endpoint (picking the model and measuring
/// its dimensions), merges the result into <c>indexer.json</c> and optionally runs a first index and a smoke-test
/// search. Without <c>--yes</c> it only reports what it would do.
/// </summary>
internal static class Setup
{
    private const string DefaultQuery = "what is this documentation about?";

    private static readonly Dictionary<string, string> EndpointPresets = new(StringComparer.Ordinal)
    {
        ["lmstudio"] = "http://localhost:1234/v1",
        ["ollama"] = "http://localhost:11434/v1",
        ["openai"] = "https://api.openai.com/v1",
        ["openrouter"] = "https://openrouter.ai/api/v1",
    };

    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = true };

    /// <summary>Runs setup; throws <see cref="UsageException"/> when a choice cannot be made without the user.</summary>
    public static async Task<SetupResult> RunAsync(
        CliArguments args, string home, string userProfile, string workingDirectory, HttpClient http, CancellationToken ct)
    {
        bool apply = args.Flags.Contains("yes");
        string scope = args.Get("scope") ?? "repo";
        IReadOnlyList<string> skillRoots = SkillInstaller.ResolveRoots(args.Get("dir"), scope, userProfile, workingDirectory);

        IndexerSettings settings = IndexerSettings.Resolve(args, home, workingDirectory);
        EmbeddingOptions embedding = await ProbeEmbeddingAsync(args, settings.Embedding, http, ct);
        settings = settings with { Embedding = embedding };

        string configPath = Path.Combine(home, "indexer.json");
        string? before = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, ct) : null;
        string after = MergeConfig(before, embedding);
        var skillPaths = skillRoots.Select(r => Path.Combine(Path.GetFullPath(r), SkillInstaller.SkillName, "SKILL.md")).ToList();

        string? repoPath = null;
        string? repoBefore = null;
        string? repoAfter = null;
        IndexRequest? request = IndexRequestFor(args, workingDirectory);
        if (request is not null && await TakenByAnotherRootAsync(settings, request, ct) is { } owner)
        {
            string suggestion = $"{Path.GetFileName(Path.GetDirectoryName(workingDirectory.TrimEnd('\\', '/')))}-{request.Index}".ToLowerInvariant();
            return Result("name_taken", $"Index '{request.Index}' already belongs to {owner}. Nothing was changed; re-run with --index {suggestion} (or another name).");
        }

        // The repo file records which index and root belong to this repo, so later commands work from any folder of it.
        if (scope == "repo" && request is not null)
        {
            repoPath = settings.Defaults.RepoConfigPath ?? Path.Combine(workingDirectory, RepoLocator.ConfigFileName);
            repoBefore = File.Exists(repoPath) ? await File.ReadAllTextAsync(repoPath, ct) : null;
            repoAfter = MergeRepoConfig(repoBefore, repoPath, request);
        }

        string? keyWarning = args.Get("embedding-key") is null
            ? null
            : " The API key from --embedding-key was used for this run only and was not saved: it is a secret. Set it in the environment (OPENAI_API_KEY / OPENROUTER_API_KEY, or AGENCY_INDEX_Embedding__ApiKey for another endpoint) so index and search can use it.";

        if (!apply)
        {
            return Result("preview", "Nothing was written. Re-run with --yes to apply." + keyWarning);
        }

        IReadOnlyList<InstalledSkill> installed = await SkillInstaller.InstallAsync(skillRoots, ct);
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(configPath, after, ct);
        if (repoPath is not null)
        {
            await File.WriteAllTextAsync(repoPath, repoAfter!, ct);
        }

        SetupIndex? index = request is null ? null : await IndexAndSearchAsync(settings, request, args.Get("query") ?? DefaultQuery, ct);
        return Result("ok", keyWarning?.Trim(), installed.Select(i => i.Path).ToList(), index);

        SetupResult Result(string status, string? message, IReadOnlyList<string>? skill = null, SetupIndex? ran = null) =>
            new(status, message, scope, skill ?? skillPaths, configPath, before, after, embedding.BaseUrl!, embedding.ModelId!, embedding.Dimensions!.Value, ran, repoPath, repoBefore, repoAfter);
    }

    private static async Task<EmbeddingOptions> ProbeEmbeddingAsync(CliArguments args, EmbeddingOptions configured, HttpClient http, CancellationToken ct)
    {
        string? preset = args.Get("endpoint");
        if (preset is not null && !EndpointPresets.ContainsKey(preset))
        {
            throw new UsageException($"Unknown --endpoint '{preset}'. Expected {string.Join(", ", EndpointPresets.Keys)}, or pass --embedding-url <url>.");
        }

        string? baseUrl = args.Get("embedding-url") ?? (preset is null ? configured.BaseUrl : EndpointPresets[preset]);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new UsageException("No embedding endpoint: pass --endpoint lmstudio|ollama|openai or --embedding-url <url>.");
        }

        // A key that came from the old endpoint's environment variable must not be sent to a different host.
        string? carriedKey = configured.ApiKey == IndexerSettings.ResolveApiKey(configured.BaseUrl, null) ? null : configured.ApiKey;
        string key = IndexerSettings.ResolveApiKey(baseUrl, carriedKey);
        if (key == IndexerSettings.NoKey && IndexerSettings.KeyVariableFor(baseUrl) is { } variable)
        {
            throw new UsageException($"{new Uri(baseUrl).Host} needs an API key: set the {variable} environment variable. It is a secret and is never written to a file.");
        }

        var embedding = new EmbeddingOptions { BaseUrl = baseUrl, ModelId = configured.ModelId, ApiKey = key, Dimensions = configured.Dimensions };
        try
        {
            embedding.ModelId = args.Get("embedding-model") ?? await ChooseModelAsync(http, embedding, ct);
            int measured = await EndpointProbe.MeasureDimensionsAsync(http, embedding, ct);
            if (args.Get("dimensions") is { } given && int.TryParse(given, out int wanted) && wanted != measured)
            {
                throw new UsageException($"--dimensions {wanted} does not match the model, which produces {measured}-dimension vectors. Omit --dimensions to use the measured value.");
            }

            embedding.Dimensions = measured;
            return embedding;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new UsageException($"Could not use the embeddings endpoint {baseUrl}: {ex.Message}");
        }
    }

    /// <summary>The configured model if the endpoint lists it, else the only embedding-looking model it lists.</summary>
    private static async Task<string> ChooseModelAsync(HttpClient http, EmbeddingOptions embedding, CancellationToken ct)
    {
        IReadOnlyList<string> models = await EndpointProbe.ListModelsAsync(http, embedding, ct);
        if (embedding.ModelId is { Length: > 0 } configured && models.Contains(configured, StringComparer.Ordinal))
        {
            return configured;
        }

        List<string> candidates = models.Where(m => m.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1
            ? candidates[0]
            : throw new UsageException($"Could not pick an embedding model automatically. Pass --embedding-model <id>; the endpoint lists: {string.Join(", ", models)}.");
    }

    /// <summary>
    /// Sets the endpoint, model and dimensions in the existing config (or a new one), leaving every other key as it was.
    /// The API key is a secret and is never written.
    /// </summary>
    private static string MergeConfig(string? existing, EmbeddingOptions embedding)
    {
        JsonObject root;
        try
        {
            root = existing is null ? [] : JsonNode.Parse(existing) as JsonObject ?? throw new UsageException("indexer.json is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new UsageException($"indexer.json is not valid JSON ({ex.Message}); fix or remove it first.");
        }

        if (root["Embedding"] is not JsonObject section)
        {
            section = [];
            root["Embedding"] = section;
        }

        section["BaseUrl"] = embedding.BaseUrl;
        section["ModelId"] = embedding.ModelId;
        section["Dimensions"] = embedding.Dimensions;

        return root.ToJsonString(ConfigJson);
    }

    /// <summary>Sets <c>Index</c> and <c>Root</c> (relative to the file when inside its folder) in the repo config, keeping every other key.</summary>
    private static string MergeRepoConfig(string? existing, string path, IndexRequest request)
    {
        JsonObject root;
        try
        {
            root = existing is null ? [] : JsonNode.Parse(existing) as JsonObject ?? throw new UsageException($"{path} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new UsageException($"{path} is not valid JSON ({ex.Message}); fix or remove it first.");
        }

        string relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, request.Root!);
        root["Index"] = request.Index;
        root["Root"] = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? request.Root : relative.Replace('\\', '/');
        return root.ToJsonString(ConfigJson);
    }

    /// <summary>The first-index request when <c>--index</c> or <c>--root</c> was given, with the folder name and <c>docs/</c> as defaults.</summary>
    private static IndexRequest? IndexRequestFor(CliArguments args, string workingDirectory)
    {
        if (args.Get("index") is null && args.Get("root") is null)
        {
            return null;
        }

        string name = args.Get("index") ?? Path.GetFileName(workingDirectory.TrimEnd('\\', '/')).ToLowerInvariant();
        if (!ProjectName.TryNormalize(name, out string canonical, out string? error))
        {
            throw new UsageException($"Invalid index name '{name}': {error}");
        }

        string docs = Path.Combine(workingDirectory, "docs");
        string root = Path.GetFullPath(args.Get("root") ?? (Directory.Exists(docs) ? docs : workingDirectory));
        return new IndexRequest(canonical, root, null, null, FileScanner.DefaultMaxFileBytes, Wait: false);
    }

    /// <summary>The root of an existing index of that name when it differs from the requested root; otherwise <see langword="null"/>.</summary>
    private static async Task<string?> TakenByAnotherRootAsync(IndexerSettings settings, IndexRequest request, CancellationToken ct)
    {
        // A SQLite database that does not exist yet cannot hold the name, and probing must not create it.
        if (settings.Provider == StorageProvider.Sqlite && !File.Exists(Path.GetFullPath(settings.Database)))
        {
            return null;
        }

        IndexService service = await Program.CreateServiceAsync(settings, new Program.MissingEmbeddingGenerator(), ct);
        IReadOnlyList<(string Index, IndexConfig Config)> indexes = await service.ListIndexesAsync(ct);
        return indexes.FirstOrDefault(i => i.Index == request.Index).Config is { } config
               && !string.Equals(config.Root, request.Root, StringComparison.Ordinal)
            ? config.Root
            : null;
    }

    private static async Task<SetupIndex> IndexAndSearchAsync(IndexerSettings settings, IndexRequest request, string query, CancellationToken ct)
    {
        IndexService service = await Program.CreateServiceAsync(settings, new EmbeddingGenerator(settings.Embedding), ct);
        IndexResult result = await service.IndexAsync(request, ct);
        IReadOnlyList<SearchResultHit> hits = result.Status == IndexStatus.Locked
            ? []
            : await service.SearchAsync(request.Index, query, 1, ct);
        return new SetupIndex(request.Index, request.Root!, result.Status, result.ChunksWritten, result.Failed.Count, hits.Count > 0 ? hits[0] : null);
    }
}
