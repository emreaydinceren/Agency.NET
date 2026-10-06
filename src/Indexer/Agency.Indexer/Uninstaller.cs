namespace Agency.Indexer;

/// <summary>An index considered by <c>uninstall</c>.</summary>
/// <param name="Name">The index name.</param>
/// <param name="Root">The directory it indexes.</param>
/// <param name="ThisRepo">Whether <paramref name="Root"/> is inside the current directory.</param>
/// <param name="Action"><c>would_drop</c> or <c>keep</c> in a preview; <c>dropped</c>, <c>locked</c> or <c>keep</c> once applied.</param>
internal sealed record UninstallIndex(string Name, string Root, bool ThisRepo, string Action);

/// <summary>What <c>uninstall</c> did, or with no <c>--yes</c> would do.</summary>
/// <param name="Status"><c>preview</c> (nothing changed), <c>ok</c>, or <c>partial</c> (something could not be removed; see <paramref name="Message"/>).</param>
/// <param name="Scope"><c>repo</c> or <c>all</c>.</param>
/// <param name="Skills">The skill files found (preview) or removed.</param>
/// <param name="Indexes">The indexes in the database and what happens to each.</param>
/// <param name="DataFiles">The database files found (preview) or deleted; only for <c>all</c> on SQLite.</param>
/// <param name="ConfigFile">The <c>indexer.json</c> found (preview) or deleted; only for <c>all</c>.</param>
/// <param name="Remaining">Steps the tool cannot do itself.</param>
/// <param name="Message">What to know about the outcome.</param>
internal sealed record UninstallResult(
    string Status,
    string Scope,
    IReadOnlyList<string> Skills,
    IReadOnlyList<UninstallIndex> Indexes,
    IReadOnlyList<string> DataFiles,
    string? ConfigFile,
    IReadOnlyList<string> Remaining,
    string Message);

/// <summary>
/// The <c>uninstall</c> command. <c>--scope repo</c> (default) removes this repo's indexes and skill; <c>--scope all</c>
/// removes every index, every skill copy, the SQLite database files and <c>indexer.json</c>. It never removes the tool
/// itself (a running executable cannot delete itself): that step is returned in <see cref="UninstallResult.Remaining"/>.
/// Without <c>--yes</c> it only reports what it would do.
/// </summary>
internal static class Uninstaller
{
    private const string ToolUninstallCommand = "dotnet tool uninstall -g AgencyDotNet.Indexer";

    /// <summary>Runs the command; throws <see cref="UsageException"/> for an unknown scope.</summary>
    public static async Task<UninstallResult> RunAsync(CliArguments args, string home, string userProfile, string workingDirectory, CancellationToken ct)
    {
        string scope = args.Get("scope") ?? "repo";
        if (scope is not ("repo" or "all"))
        {
            throw new UsageException($"Unknown scope '{scope}'. Expected 'repo' (this repo only) or 'all'.");
        }

        bool apply = args.Flags.Contains("yes");
        bool all = scope == "all";
        IndexerSettings settings = IndexerSettings.Resolve(args, home);

        var skillRoots = new List<string> { Path.Combine(workingDirectory, ".claude", "skills") };
        if (all)
        {
            skillRoots.AddRange(SkillInstaller.DefaultRoots(userProfile));
        }

        if (args.Get("dir") is { } dir)
        {
            skillRoots.Add(dir);
        }

        List<string> skills = skillRoots
            .Select(root => Path.Combine(Path.GetFullPath(root), SkillInstaller.SkillName, "SKILL.md"))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        IndexService? service = await OpenServiceAsync(settings, ct);
        IReadOnlyList<(string Index, IndexConfig Config)> found = service is null ? [] : await service.ListIndexesAsync(ct);
        var indexes = found
            .Select(i => (i.Index, i.Config.Root, ThisRepo: IsUnder(i.Config.Root, workingDirectory)))
            .Select(i => new UninstallIndex(i.Index, i.Root, i.ThisRepo, all || i.ThisRepo ? "would_drop" : "keep"))
            .ToList();

        string databasePath = Path.GetFullPath(settings.Database);
        List<string> dataFiles = all && settings.Provider == StorageProvider.Sqlite ? SqliteFiles(databasePath) : [];
        string configPath = Path.Combine(home, "indexer.json");
        string? configFile = all && File.Exists(configPath) ? configPath : null;
        var remaining = new List<string>();
        if (all)
        {
            remaining.Add(ToolUninstallCommand);
            remaining.Add("Remove the API key environment variable (OPENAI_API_KEY / OPENROUTER_API_KEY / AGENCY_INDEX_Embedding__ApiKey) yourself; the tool never stored it.");
        }

        if (!apply)
        {
            return new UninstallResult("preview", scope, skills, indexes, dataFiles, configFile, remaining, "Nothing was changed. Re-run with --yes to apply.");
        }

        var problems = new List<string>();
        var applied = new List<UninstallIndex>();
        foreach (UninstallIndex index in indexes)
        {
            if (index.Action == "keep")
            {
                applied.Add(index);
                continue;
            }

            DropResult dropped = await service!.DropAsync(index.Name, wait: false, ct);
            applied.Add(index with { Action = dropped.Status == IndexStatus.Locked ? "locked" : "dropped" });
            if (dropped.Status == IndexStatus.Locked)
            {
                problems.Add($"Index '{index.Name}' is being written by another process; try again when it finishes.");
            }
        }

        IReadOnlyList<string> removedSkills = SkillInstaller.Uninstall(skillRoots).ToList();
        var removedData = new List<string>();
        string? removedConfig = null;
        if (all && problems.Count == 0)
        {
            // Pooled connections keep the file open, which blocks deleting it on Windows.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                foreach (string file in dataFiles)
                {
                    File.Delete(file);
                    removedData.Add(file);
                }

                if (configFile is not null)
                {
                    File.Delete(configFile);
                    removedConfig = configFile;
                }

                if (Directory.Exists(home) && !Directory.EnumerateFileSystemEntries(home).Any())
                {
                    Directory.Delete(home);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not delete a data file: {ex.Message}");
            }
        }
        else if (all)
        {
            problems.Add("The database and config were left in place because an index could not be dropped.");
        }

        return new UninstallResult(
            problems.Count == 0 ? "ok" : "partial",
            scope,
            removedSkills,
            applied,
            removedData,
            removedConfig,
            remaining,
            problems.Count == 0 ? "Done. Complete the remaining steps yourself." : string.Join(" ", problems));
    }

    /// <summary>Opens the index database without creating it: a missing SQLite file holds no indexes.</summary>
    private static async Task<IndexService?> OpenServiceAsync(IndexerSettings settings, CancellationToken ct) =>
        settings.Provider == StorageProvider.Sqlite && !File.Exists(Path.GetFullPath(settings.Database))
            ? null
            : await Program.CreateServiceAsync(settings, new Program.MissingEmbeddingGenerator(), ct);

    /// <summary>The SQLite database, its WAL and shared-memory files, and any per-index writer lock files that exist.</summary>
    private static List<string> SqliteFiles(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        string name = Path.GetFileName(databasePath);
        return Directory.EnumerateFiles(directory, name + "*")
            .Where(f =>
            {
                string suffix = Path.GetFileName(f)[name.Length..];
                return suffix.Length == 0 || suffix is "-wal" or "-shm" || (suffix.StartsWith('.') && suffix.EndsWith(".lock", StringComparison.Ordinal));
            })
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsUnder(string path, string directory)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
