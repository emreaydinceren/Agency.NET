using System.Reflection;
using Npgsql;

namespace Agency.Indexer;

/// <summary>One prerequisite of the indexer and whether it holds.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Ok">Whether the prerequisite holds.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Fix">What to do when <paramref name="Ok"/> is <see langword="false"/>; otherwise <see langword="null"/>.</param>
internal sealed record DoctorCheck(string Name, bool Ok, string Detail, string? Fix = null);

/// <summary>
/// The <c>doctor</c> command: a read-only report of everything the indexer needs, so an agent can branch on the
/// result instead of inferring state from an exit code.
/// </summary>
internal static class Doctor
{
    private const string ConfigExample = "{ \"Embedding\": { \"BaseUrl\": \"http://localhost:1234/v1\", \"ModelId\": \"<embedding model id>\", \"Dimensions\": <vector length> } }";

    /// <summary>Runs every check. A failing prerequisite is reported, never thrown.</summary>
    /// <param name="args">The parsed command line (connection options override the configuration file).</param>
    /// <param name="home">The directory holding <c>indexer.json</c> and the default SQLite database.</param>
    /// <param name="userProfile">The user profile folder, for the user-scope skill folders.</param>
    /// <param name="workingDirectory">The current directory, whose <c>.claude/skills</c> is the repo-scope skill folder.</param>
    /// <param name="http">The client used to probe the embedding endpoint.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlyList<DoctorCheck>> RunAsync(
        CliArguments args, string home, string userProfile, string workingDirectory, HttpClient http, CancellationToken ct)
    {
        var checks = new List<DoctorCheck> { ToolCheck(), await SkillCheckAsync(userProfile, workingDirectory, ct) };

        IndexerSettings settings;
        string configPath = Path.Combine(home, "indexer.json");
        try
        {
            settings = IndexerSettings.Resolve(args, home, workingDirectory);
        }
        catch (Exception ex) when (ex is UsageException or InvalidDataException or FormatException)
        {
            checks.Add(new DoctorCheck("config", false, $"{configPath}: {ex.Message}", $"Fix or remove {configPath}."));
            return checks;
        }

        checks.Add(new DoctorCheck(
            "config",
            true,
            File.Exists(configPath) ? $"{configPath} found." : $"{configPath} not found; using options, AGENCY_INDEX_* variables and defaults."));

        checks.Add(RepoConfigCheck(settings, workingDirectory));
        checks.Add(DefaultsCheck(settings));

        if (File.Exists(configPath) && StoresApiKey(await File.ReadAllTextAsync(configPath, ct)))
        {
            checks.Add(new DoctorCheck(
                "api_key_storage",
                false,
                $"{configPath} holds an Embedding:ApiKey in plain text.",
                "Move the key to the OPENAI_API_KEY / OPENROUTER_API_KEY environment variable (or AGENCY_INDEX_Embedding__ApiKey) and delete it from the file."));
        }

        await AddEmbeddingChecksAsync(checks, settings, http, ct);
        bool databaseOk = await AddDatabaseCheckAsync(checks, settings, ct);
        if (databaseOk)
        {
            await AddIndexChecksAsync(checks, settings, ct);
        }

        return checks;
    }

    private static DoctorCheck RepoConfigCheck(IndexerSettings settings, string workingDirectory)
    {
        IndexDefaults defaults = settings.Defaults;
        if (defaults.RepoConfigPath is null)
        {
            return new DoctorCheck("repo_config", true, $"No {RepoLocator.ConfigFileName} found from {workingDirectory} upwards; using the user-level config and command-line options.");
        }

        return defaults.IgnoredRepoKeys.Count == 0
            ? new DoctorCheck("repo_config", true, $"{defaults.RepoConfigPath} found.")
            : new DoctorCheck(
                "repo_config",
                false,
                $"{defaults.RepoConfigPath}: ignored {string.Join(", ", defaults.IgnoredRepoKeys)}. A repo file may only set {string.Join(", ", RepoConfig.AllowedKeys)}.",
                $"Remove those keys. Endpoint, model, database and provider belong in {Path.Combine(IndexerSettings.DefaultHome, "indexer.json")}; a repo file must not choose where document text is sent.");
    }

    private static DoctorCheck DefaultsCheck(IndexerSettings settings)
    {
        IndexDefaults d = settings.Defaults;
        var values = new (string Key, string? Value)[]
        {
            ("Index", d.Index), ("Root", d.Root), ("Extensions", d.Extensions), ("Names", d.Names), ("MaxFileKb", d.MaxFileKb?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        string detail = string.Join("; ", values.Where(v => v.Value is not null).Select(v => $"{v.Key}={v.Value} ({d.Sources.GetValueOrDefault(v.Key, "default")})"));
        return new DoctorCheck("defaults", true, detail.Length == 0 ? "No index defaults are set, so commands need --index (and --root on the first run)." : detail);
    }

    /// <summary>Whether the config text sets a real <c>Embedding:ApiKey</c> (the placeholder does not count).</summary>
    private static bool StoresApiKey(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonObject root
        && root["Embedding"] is System.Text.Json.Nodes.JsonObject section
        && section["ApiKey"]?.GetValue<string>() is { Length: > 0 } key
        && key != IndexerSettings.NoKey;

    private static DoctorCheck ToolCheck()
    {
        string version = typeof(Doctor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        bool prerelease = version.Split('+')[0].Contains('-', StringComparison.Ordinal);
        return new DoctorCheck("tool", true, $"agency-index {version}{(prerelease ? " (prerelease build)" : "")} at {Environment.ProcessPath}");
    }

    private static async Task<DoctorCheck> SkillCheckAsync(string userProfile, string workingDirectory, CancellationToken ct)
    {
        string embedded = (await SkillInstaller.ReadSkillAsync(ct)).ReplaceLineEndings("\n");
        IEnumerable<string> roots = SkillInstaller.DefaultRoots(userProfile).Prepend(Path.Combine(workingDirectory, ".claude", "skills"));

        var found = new List<string>();
        bool stale = false;
        foreach (string root in roots)
        {
            string path = Path.Combine(root, SkillInstaller.SkillName, "SKILL.md");
            if (!File.Exists(path))
            {
                continue;
            }

            bool current = (await File.ReadAllTextAsync(path, ct)).ReplaceLineEndings("\n") == embedded;
            stale |= !current;
            found.Add($"{path} ({(current ? "current" : "stale")})");
        }

        return found.Count switch
        {
            0 => new DoctorCheck("skill", false, "No agency-index skill found in this repo or the user folders.", "Run: agency-index install-skill --dir ./.claude/skills (this repo) or agency-index install-skill (all repos)."),
            _ when stale => new DoctorCheck("skill", false, string.Join("; ", found), "Re-run install-skill with the same --dir to refresh the stale copy."),
            _ => new DoctorCheck("skill", true, string.Join("; ", found)),
        };
    }

    private static async Task AddEmbeddingChecksAsync(List<DoctorCheck> checks, IndexerSettings settings, HttpClient http, CancellationToken ct)
    {
        var embedding = settings.Embedding;
        if (string.IsNullOrWhiteSpace(embedding.BaseUrl) || string.IsNullOrWhiteSpace(embedding.ModelId))
        {
            checks.Add(new DoctorCheck(
                "embedding_config",
                false,
                $"Embedding BaseUrl='{embedding.BaseUrl}', ModelId='{embedding.ModelId}'; index and search exit 2 until both are set.",
                $"Write {Path.Combine(IndexerSettings.DefaultHome, "indexer.json")}: {ConfigExample}"));
            return;
        }

        checks.Add(new DoctorCheck("embedding_config", true, $"{embedding.ModelId} at {embedding.BaseUrl}, dimensions {embedding.Dimensions}."));

        if (IndexerSettings.KeyVariableFor(embedding.BaseUrl) is { } variable)
        {
            if (embedding.ApiKey == IndexerSettings.NoKey)
            {
                checks.Add(new DoctorCheck(
                    "api_key",
                    false,
                    $"{new Uri(embedding.BaseUrl).Host} needs an API key and none was found.",
                    $"Set the {variable} environment variable (preferred) or Embedding:ApiKey, then re-run. The endpoint is not probed without a key."));
                return;
            }

            // The value is never reported, only where it came from.
            string source = embedding.ApiKey == Environment.GetEnvironmentVariable(variable) ? $"the {variable} environment variable" : "configuration";
            checks.Add(new DoctorCheck("api_key", true, $"An API key is set, from {source}."));
        }

        try
        {
            IReadOnlyList<string> models = await EndpointProbe.ListModelsAsync(http, embedding, ct);
            checks.Add(new DoctorCheck("endpoint", true, $"Reachable; {models.Count} model(s) listed."));
            checks.Add(models.Contains(embedding.ModelId, StringComparer.Ordinal)
                ? new DoctorCheck("model", true, $"'{embedding.ModelId}' is listed.")
                : new DoctorCheck("model", false, $"'{embedding.ModelId}' is not among: {string.Join(", ", models)}.", "Load the model on the server or set Embedding:ModelId to one of the listed ids."));
        }
        catch (Exception ex) when (IsProbeFailure(ex, ct))
        {
            checks.Add(new DoctorCheck("endpoint", false, $"{embedding.BaseUrl}: {ex.Message}", "Start the embeddings server or correct Embedding:BaseUrl."));
            return;
        }

        try
        {
            int measured = await EndpointProbe.MeasureDimensionsAsync(http, embedding, ct);
            checks.Add(measured == embedding.Dimensions
                ? new DoctorCheck("dimensions", true, $"The model produces {measured}-dimension vectors, as configured.")
                : new DoctorCheck("dimensions", false, $"The model produces {measured}-dimension vectors but {embedding.Dimensions} is configured.", $"Set Embedding:Dimensions to {measured} (drop and rebuild any index created with the old value)."));
        }
        catch (Exception ex) when (IsProbeFailure(ex, ct))
        {
            checks.Add(new DoctorCheck("dimensions", false, $"Could not embed a probe string: {ex.Message}", "Check that the model is an embedding model and is loaded; a timeout can also mean the server is busy (for example a running 'agency-index index')."));
        }
    }

    private static async Task<bool> AddDatabaseCheckAsync(List<DoctorCheck> checks, IndexerSettings settings, CancellationToken ct)
    {
        try
        {
            if (settings.Provider == StorageProvider.Sqlite)
            {
                string path = Path.GetFullPath(settings.Database);
                string existing = Path.GetDirectoryName(path)!;
                while (!Directory.Exists(existing))
                {
                    existing = Path.GetDirectoryName(existing) ?? throw new IOException($"No existing parent folder for {path}.");
                }

                string probe = Path.Combine(existing, $".agency-index-probe-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, "", ct);
                File.Delete(probe);
                checks.Add(new DoctorCheck("database", true, $"SQLite at {path} ({(File.Exists(path) ? "exists" : "will be created")}); {existing} is writable."));
                return true;
            }

            await using var connection = new NpgsqlConnection(settings.Database);
            await connection.OpenAsync(ct);
            checks.Add(new DoctorCheck("database", true, "PostgreSQL connection opened."));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NpgsqlException or InvalidOperationException or ArgumentException)
        {
            checks.Add(new DoctorCheck("database", false, ex.Message, "Fix the Database setting or its permissions."));
            return false;
        }
    }

    private static async Task AddIndexChecksAsync(List<DoctorCheck> checks, IndexerSettings settings, CancellationToken ct)
    {
        if (settings.Provider == StorageProvider.Sqlite && !File.Exists(Path.GetFullPath(settings.Database)))
        {
            checks.Add(new DoctorCheck("indexes", true, "No database yet, so no indexes."));
            return;
        }

        try
        {
            IndexService service = await Program.CreateServiceAsync(settings, new Program.MissingEmbeddingGenerator(), ct);
            IReadOnlyList<(string Index, IndexConfig Config)> indexes = await service.ListIndexesAsync(ct);
            if (indexes.Count == 0)
            {
                checks.Add(new DoctorCheck("indexes", true, "No indexes yet."));
                return;
            }

            var problems = new List<string>();
            foreach ((string index, IndexConfig config) in indexes)
            {
                if (!string.Equals(config.EmbeddingModel, settings.Embedding.ModelId, StringComparison.Ordinal))
                {
                    problems.Add($"'{index}' was built with '{config.EmbeddingModel}' but '{settings.Embedding.ModelId}' is configured");
                }
                else if (!Directory.Exists(config.Root))
                {
                    problems.Add($"'{index}' root {config.Root} no longer exists");
                }
            }

            string detail = string.Join("; ", indexes.Select(i => $"{i.Index} -> {i.Config.Root}"));
            checks.Add(problems.Count == 0
                ? new DoctorCheck("indexes", true, detail)
                : new DoctorCheck("indexes", false, $"{string.Join("; ", problems)}. Indexes: {detail}", "Restore the original model, or drop and rebuild the index; re-create it under a new --root if the folder moved."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new DoctorCheck("indexes", false, ex.Message, "Check the database setting and that no other tool holds the file."));
        }
    }

    /// <summary>A failed probe (server down, bad status, malformed body, timeout) rather than a user cancellation.</summary>
    private static bool IsProbeFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);
}
