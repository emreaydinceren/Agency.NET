using System.Text.Json;
using Agency.Embeddings.Common;
using Agency.Embeddings.OpenAI;
using Agency.Ingestion.SemanticKernel;
using Agency.Sql.Postgres;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Microsoft.Extensions.Logging.Abstractions;
using KvPostgres = Agency.KeyValueStore.Sql.Postgres.PostgresKVStore;
using KvSqlite = Agency.KeyValueStore.Sql.Sqlite.SqliteKVStore;
using VectorPostgres = Agency.VectorStore.Sql.Postgres.PostgresKVStore;
using VectorSqlite = Agency.VectorStore.Sql.Sqlite.SqliteKVStore;

namespace Agency.Indexer;

/// <summary>
/// <c>agency-index</c>: incremental semantic indexing for agents. Every command prints one JSON object to
/// stdout. Exit codes: 0 success, 1 failure (including a run where some files failed), 2 usage or
/// configuration error, 3 another process holds the index's writer lock.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;
    private const int ExitLocked = 3;

    private const string Usage = """
        agency-index — incremental semantic index over a folder of text documents.

        Commands:
          index   --index <name> [--root <dir>] [--ext .md,.txt,...] [--names README,...] [--exclude <glob>,...] [--max-file-kb 1024] [--wait] [--dry-run] [--rebuild] [--summary] [--log <file>]
                  (progress and each failed file with its reason go to stderr, and to --log <file> with timestamps; --dry-run reports the delta, chunk count and a time estimate without writing;
                  --exclude takes gitignore-style globs relative to the root, e.g. docs/manual-tests,**/*.draft.md; --rebuild re-embeds every file, which is how to switch embedding models;
                  paths in the result are relative to "root"; --summary prints counts instead of file lists)
          search  --index <name>[,<name>...] --query <text> [--top 5] [--min-score 0..1] [--within 0..1] [--no-text] [--snippet-chars N]
                  [--path <glob>] [--hybrid] [--group-by-file | --per-file N]
                  --min-score (or Search:MinScore in indexer.json, or the value stored by 'calibrate --save') drops weaker hits; --within keeps hits within that distance of the best;
                  filtered hits are counted in "filtered" with the pre-filter "best_score". --no-text / --snippet-chars shrink the output.
                  --path keeps files whose path under the index root matches the glob; --hybrid also ranks by keyword match (a chunk containing an identifier from the query survives --min-score);
                  --group-by-file keeps the best chunk of each file, --per-file N the best N. Hits carry heading, start_line and end_line when the index recorded them.
          calibrate --index <name> [--save]
                  Runs unrelated queries against the index and reports the noise ceiling and a suggested min score; --save stores it so search uses it when no min score is configured.
          list    --index <name> [--summary]
          indexes
          drop    --index <name> [--wait]
          install-skill [--dir <skills-root> | --scope repo|user]    (default scope: user; "setup" defaults to repo)
          uninstall-skill [--dir <skills-root> | --scope repo|user]
          uninstall [--scope repo|all] [--dir <skills-root>] [--yes]
                  repo (default): drop this repo's indexes and remove its skill. all: every index, every skill copy, the SQLite
                  database files and indexer.json. Never removes the tool itself (see "remaining"). Without --yes it only previews.
          setup   [--scope repo|user] [--endpoint lmstudio|ollama|openai|openrouter | --embedding-url <url>] [--embedding-model <id>]
                  [--index <name>] [--root <dir>] [--exclude <glob>,...] [--no-index] [--query <text>] [--yes]
                  Installs the skill (default scope: repo), picks the embedding model and measures its dimensions, merges
                  ~/.agency/indexer.json and, if --index/--root is given, runs a first index and a smoke search
                  (--no-index: write the config and repo file but do not index, so you can --dry-run first).
                  Without --yes it only reports what it would do.
          doctor        (read-only report of every prerequisite: {"status":"ok|problems","checks":[{name,ok,detail,fix}]}; exit 0)

        Connection options (or AGENCY_INDEX_* environment variables, or ~/.agency/indexer.json):
          --provider sqlite|postgres   --db <sqlite path | postgres connection string>
          --embedding-url <url>        --embedding-model <id>   --embedding-key <key>   --dimensions <n>

        Exit codes: 0 ok, 1 failure, 2 usage/configuration error, 3 index locked by another writer.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,

        // Output goes to a terminal or an agent, never into HTML, so keep quotes and angle brackets readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        // Indexed documents contain non-ASCII text (arrows, dashes). With stdout redirected, Windows would encode it in the
        // OEM code page and emit a substitute control character (0x1A), making the JSON invalid for the calling agent.
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await RunAsync(CliArguments.Parse(args), cts.Token);
        }
        catch (UsageException ex)
        {
            return Fail(ExitUsage, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(ExitFailure, "Cancelled.");
        }
        catch (Exception ex)
        {
            // Process boundary: report anything unexpected (database unreachable, endpoint down, ...) as JSON
            // with exit code 1 instead of an unhandled-exception crash the calling agent cannot parse.
            return Fail(ExitFailure, ex.Message);
        }
    }

    private static async Task<int> RunAsync(CliArguments args, CancellationToken ct)
    {
        string workingDirectory = Directory.GetCurrentDirectory();

        // "This repo" is the nearest folder with a .agency-index.json or .git, so a leaf folder behaves like the root.
        string repoRoot = RepoLocator.FindRoot(workingDirectory);

        switch (args.Command)
        {
            case "help":
                Console.Out.WriteLine(Usage);
                return ExitOk;

            case "install-skill":
                return Write(ExitOk, new { status = "ok", installed = await SkillInstaller.InstallAsync(SkillRootsFor(args, "user"), ct) });

            case "uninstall-skill":
                return Write(ExitOk, new { status = "ok", removed = SkillInstaller.Uninstall(SkillRootsFor(args, "user")) });

            case "uninstall":
                UninstallResult uninstall = await Uninstaller.RunAsync(
                    args,
                    IndexerSettings.DefaultHome,
                    IndexerSettings.UserProfile,
                    repoRoot,
                    ct);
                return Write(uninstall.Status == "partial" ? ExitFailure : ExitOk, uninstall);

            case "setup":
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    SetupResult setup = await Setup.RunAsync(
                        args,
                        IndexerSettings.DefaultHome,
                        IndexerSettings.UserProfile,
                        repoRoot,
                        http,
                        ct);
                    return Write(
                        setup.Status is "name_taken" or "index_model_mismatch" ? ExitUsage : setup.Index is { Status: not IndexStatus.Ok } ? ExitFailure : ExitOk,
                        setup);
                }

            case "doctor":
                using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    IReadOnlyList<DoctorCheck> checks = await Doctor.RunAsync(
                        args,
                        IndexerSettings.DefaultHome,
                        IndexerSettings.UserProfile,
                        repoRoot,
                        probe,
                        ct);
                    return Write(ExitOk, new { status = checks.All(c => c.Ok) ? "ok" : "problems", checks });
                }

            case "index" or "search" or "calibrate" or "list" or "indexes" or "drop":
                break;

            default:
                throw new UsageException($"Unknown command '{args.Command}'. Run 'agency-index help'.");
        }

        var settings = IndexerSettings.Resolve(args, IndexerSettings.DefaultHome, workingDirectory);
        if (args.Command is "index" or "search" or "calibrate")
        {
            settings.RequireEmbedding();
        }

        IEmbeddingGenerator embeddings = string.IsNullOrWhiteSpace(settings.Embedding.BaseUrl)
            ? new MissingEmbeddingGenerator()
            : new EmbeddingGenerator(settings.Embedding);
        IndexService service = await CreateServiceAsync(settings, embeddings, ct);

        switch (args.Command)
        {
            case "index":
                var request = new IndexRequest(
                    IndexName(settings),
                    settings.Defaults.Root,
                    settings.Defaults.Extensions is { } ext ? FileScanner.ParseExtensions(ext) : null,
                    settings.Defaults.Names is { } names ? FileScanner.SplitList(names) : null,
                    (settings.Defaults.MaxFileKb ?? (int)(FileScanner.DefaultMaxFileBytes / 1024)) * 1024L,
                    args.Flags.Contains("wait"),
                    settings.Defaults.Exclude is { } exclude ? FileScanner.SplitList(exclude) : null,
                    args.Flags.Contains("rebuild"));
                bool summary = args.Flags.Contains("summary");
                if (args.Flags.Contains("dry-run"))
                {
                    return Write(ExitOk, new { status = "dry_run", plan = IndexOutput.Of(await service.DryRunAsync(request, embeddings, ct), summary) });
                }

                // Progress and failures go to stderr (and --log) so stdout stays the single JSON object the calling agent parses.
                using (var log = new RunLog(args.Get("log")))
                {
                    IndexResult indexed = await service.IndexAsync(request, ct, log.Write);
                    log.Write($"finished: {indexed.Status}, {indexed.Added.Count} added, {indexed.Changed.Count} changed, {indexed.Failed.Count} failed, {indexed.DurationMs} ms");
                    return Write(ExitCodeFor(indexed.Status), IndexOutput.Of(indexed, summary));
                }

            case "search":
                return Write(ExitOk, await SearchAsync(service, settings, args, ct));

            case "calibrate":
                return Write(ExitOk, new { status = "ok", calibration = await service.CalibrateAsync(IndexName(settings), args.Flags.Contains("save"), ct) });

            case "list":
                var (config, files) = await service.ListAsync(IndexName(settings), ct);
                return Write(ExitOk, IndexOutput.Of(IndexName(settings), config, files, args.Flags.Contains("summary")));

            case "indexes":
                var all = await service.ListIndexesAsync(ct);
                return Write(ExitOk, new { status = "ok", indexes = all.Select(i => new { name = i.Index, root = i.Config.Root, embedding_model = i.Config.EmbeddingModel }) });

            default:
                DropResult dropped = await service.DropAsync(IndexName(settings), args.Flags.Contains("wait"), ct);
                return Write(ExitCodeFor(dropped.Status), dropped);
        }
    }

    /// <summary>The candidates a grouped, merged or hybrid search retrieves, so there is something to regroup and rerank.</summary>
    private const int CandidatePool = 50;

    internal static async Task<SearchResponse> SearchAsync(IndexService service, IndexerSettings settings, CliArguments args, CancellationToken ct)
    {
        string[] indexes = IndexNames(settings);
        string query = args.Require("query");
        int top = args.GetPositiveInt("top", 5);
        bool hybrid = args.Flags.Contains("hybrid");
        int? perFile = args.Flags.Contains("group-by-file") ? 1 : args.Get("per-file") is null ? null : args.GetPositiveInt("per-file", 1);
        bool multi = indexes.Length > 1;

        // Regrouping, reranking and merging indexes all need more candidates than will be returned.
        int pool = hybrid || perFile is not null || multi ? Math.Max(CandidatePool, top * 10) : 0;

        var hits = new List<SearchResultHit>();
        double? suggested = null;
        foreach (string index in indexes)
        {
            hits.AddRange(await service.SearchAsync(index, query, top, ct, args.Get("path"), pool));
            if (await service.GetSuggestedMinScoreAsync(index, ct) is { } stored)
            {
                suggested = Math.Max(suggested ?? 0, stored);
            }
        }

        IReadOnlyList<SearchResultHit> ranked = hits.OrderByDescending(h => h.Score).ToList();
        if (hybrid)
        {
            ranked = HybridRanker.Rank(query, ranked);
        }

        var options = new SearchOptions(
            settings.SearchMinScore ?? suggested,
            args.GetFraction("within"),
            args.Flags.Contains("no-text"),
            args.Get("snippet-chars") is null ? null : args.GetPositiveInt("snippet-chars", 1),
            perFile,
            pool > 0 ? top : null,
            multi);
        return SearchResponse.From(string.Join(',', indexes), ranked, options);
    }

    /// <summary>Builds the stores for the configured backend and initializes their schemas.</summary>
    internal static async Task<IndexService> CreateServiceAsync(IndexerSettings settings, IEmbeddingGenerator embeddings, CancellationToken ct)
    {
        int dimensions = settings.Embedding.Dimensions!.Value;

        IVectorStore vectorStore;
        ManifestStore manifest;
        IWriterLock writerLock;

        if (settings.Provider == StorageProvider.Sqlite)
        {
            string path = Path.GetFullPath(settings.Database);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var runner = new SqliteRunner($"Data Source={path}", VectorSqlite.RegisterVectorFunctions);

            // WAL lets readers query while the single writer indexes; the mode is stored in the database file.
            await runner.ExecuteAsync("PRAGMA journal_mode=WAL;", null, ct);

            var vectors = new VectorSqlite(embeddings, runner);
            await vectors.InitializeSchemaAsync(dimensions, ct);
            var kv = new KvSqlite(runner);
            await kv.InitializeSchemaAsync(ct);

            vectorStore = vectors;
            manifest = new ManifestStore(kv);
            writerLock = new FileWriterLock(path);
        }
        else
        {
            var runner = new PostgreSqlRunner(settings.Database);
            var vectors = new VectorPostgres(embeddings, runner, NullLogger<VectorPostgres>.Instance);
            await vectors.InitializeSchemaAsync(dimensions, ct);
            var kv = new KvPostgres(runner, NullLogger<KvPostgres>.Instance);
            await kv.InitializeSchemaAsync(ct);

            vectorStore = vectors;
            manifest = new ManifestStore(kv);
            writerLock = new PostgresWriterLock(settings.Database);
        }

        return new IndexService(
            vectorStore,
            manifest,
            new SemanticKernelTextSplitter(settings.ChunkSize, settings.ChunkOverlap),
            writerLock,
            settings.Embedding.ModelId ?? "");
    }

    private static IReadOnlyList<string> SkillRootsFor(CliArguments args, string defaultScope) =>
        SkillInstaller.ResolveRoots(
            args.Get("dir"),
            args.Get("scope") ?? defaultScope,
            IndexerSettings.UserProfile,
            RepoLocator.FindRoot(Directory.GetCurrentDirectory()));

    /// <summary>The index names of a <c>search</c>: <c>--index a,b</c> searches both.</summary>
    private static string[] IndexNames(IndexerSettings settings) =>
        settings.Defaults.Index is not { } names
            ? throw new UsageException("No index name: pass --index <name>, or set \"Index\" in .agency-index.json (see 'agency-index setup').")
            : FileScanner.SplitList(names).Select(CanonicalName).Distinct(StringComparer.Ordinal).ToArray();

    private static string CanonicalName(string name) =>
        ProjectName.TryNormalize(name, out string canonical, out string? error)
            ? canonical
            : throw new UsageException($"Invalid index name: {error}");

    private static string IndexName(IndexerSettings settings) =>
        settings.Defaults.Index is not { } name
            ? throw new UsageException("No index name: pass --index <name>, or set \"Index\" in .agency-index.json (see 'agency-index setup').")
            : ProjectName.TryNormalize(name, out string canonical, out string? error)
                ? canonical
                : throw new UsageException($"Invalid index name: {error}");

    private static int ExitCodeFor(IndexStatus status) => status switch
    {
        IndexStatus.Ok => ExitOk,
        IndexStatus.Locked => ExitLocked,
        _ => ExitFailure,
    };

    private static int Write(int exitCode, object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return exitCode;
    }

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine(message);
        return Write(exitCode, new { status = "error", message });
    }

    /// <summary>
    /// Stands in for the embedding generator on commands that never embed (list, indexes, drop), so they work
    /// without an embedding endpoint configured.
    /// </summary>
    internal sealed class MissingEmbeddingGenerator : IEmbeddingGenerator
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
            throw new UsageException("No embedding endpoint configured.");

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            throw new UsageException("No embedding endpoint configured.");
    }
}
