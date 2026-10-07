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
          search  --index <name>[,<name>...] --query <text> [--top 4] [--per-file 2] [--lines 2] [--max-line-chars 200] [--highlight] [--vector-only] [--raw-query]
                  [--path <glob>] [--min-score 0..1] [--within 0..1] [--log <file>]
                  Default output (--format lines): per file, the best lines as relative/path.md:LINE: text, then "  [score 0.63, +3.1 sd] heading", and a
                  "# top hit leads ..." / "# no clear winner ..." / "# no match above min score" line. Keyword matches of the files are fused with the vector ranking
                  (--vector-only turns that off); words found in most files are left out of the embedded query (--raw-query keeps them).
          search  ... --json   (or --format json) the JSON result: --top 5 --min-score --within --full --no-text --snippet-chars N --group-by-file | --per-file N --hybrid
                  --min-score (or Search:MinScore in indexer.json, or the value stored by 'calibrate --save') drops weaker hits; --within keeps hits within that distance of the best;
                  filtered hits are counted in "filtered" with the pre-filter "best_score". Text is left out unless --full (whole chunk) or --snippet-chars N is given; "hint" appears when the top hit clearly leads.
                  --hybrid also ranks by keyword match (a chunk containing an identifier from the query survives --min-score); --group-by-file keeps the best chunk of each file, --per-file N the best N.
          read    --index <name> --path <file> [--start N] [--end N | --around LINE] [--window 40] [--all] [--json]
          read    --hit N [...]   (the Nth file of the last search from this folder)
                  Plain text: a "# path lines A-B of N" header (with the stale flag) then numbered lines. Without a range: the first 40 lines, or 40 around --around;
                  --all returns the whole file; an explicit --start/--end returns at most 400 lines. --json gives the JSON form.
          calibrate --index <name> [--questions <file>] [--save]
                  Runs unrelated queries against the index and reports the noise ceiling and a suggested min score; --save stores it so search uses it when no min score is configured.
                  --questions takes a JSON array of {"question","expected_path"} rows and also reports the answer floor (10th percentile score of the expected files), how many
                  answers score no better than noise, and a minimum score inside the gap, or a warning when the two overlap.
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

            case "index" or "search" or "read" or "calibrate" or "list" or "indexes" or "drop":
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
                return await SearchCommandAsync(service, embeddings, settings, args, workingDirectory, ct);

            case "calibrate":
                return Write(ExitOk, new { status = "ok", calibration = await service.CalibrateAsync(IndexName(settings), args.Flags.Contains("save"), ct, LoadQuestions(args.Get("questions"))) });

            case "list":
                var (config, files) = await service.ListAsync(IndexName(settings), ct);
                return Write(ExitOk, IndexOutput.Of(IndexName(settings), config, files, args.Flags.Contains("summary")));

            case "read":
                return await ReadCommandAsync(service, settings, args, workingDirectory, ct);

            case "indexes":
                var all = await service.ListIndexesAsync(ct);
                return Write(ExitOk, new { status = "ok", indexes = all.Select(i => new { name = i.Index, root = i.Config.Root, embedding_model = i.Config.EmbeddingModel }) });

            default:
                DropResult dropped = await service.DropAsync(IndexName(settings), args.Flags.Contains("wait"), ct);
                return Write(ExitCodeFor(dropped.Status), dropped);
        }
    }

    /// <summary>Runs <c>search</c>: the grep-style lines output by default, the JSON result with <c>--json</c> or <c>--format json</c>.</summary>
    private static async Task<int> SearchCommandAsync(
        IndexService service, IEmbeddingGenerator embeddings, IndexerSettings settings, CliArguments args, string workingDirectory, CancellationToken ct)
    {
        string format = args.Flags.Contains("json") ? "json" : args.Get("format") ?? "lines";
        if (format is not ("lines" or "json"))
        {
            throw new UsageException($"Unknown --format '{format}'. Expected 'lines' or 'json'.");
        }

        string[] indexes = IndexNames(settings);
        double? suggested = null;
        foreach (string index in indexes)
        {
            IndexConfig config = await service.GetConfigAsync(index, ct);
            if (config.FormatVersion < IndexFormat.Current)
            {
                Console.Error.WriteLine($"warning: index '{index}' uses the older chunk-level format; run 'agency-index index --index {index} --rebuild' for passage-level search.");
            }

            if (config.Calibration?.SuggestedMinScore is { } stored)
            {
                suggested = Math.Max(suggested ?? 0, stored);
            }
        }

        string text;
        double? topScore;
        IReadOnlyList<PrintedHit> printed;
        if (format == "json")
        {
            SearchResponse response = await SearchAsync(service, settings, args, ct);
            text = Serialize(response);
            topScore = response.Hits.Count > 0 ? response.Hits[0].Score : null;
            printed = response.Hits.Select(h => new PrintedHit(h.Index ?? response.Index, h.Path, (int?)h.StartLine, (int?)h.EndLine)).ToList();
        }
        else
        {
            int perFile = args.Flags.Contains("group-by-file") ? 1 : args.GetPositiveInt("per-file", 2);
            var options = new LineSearchOptions(
                args.GetPositiveInt("top", 4),
                perFile,
                args.GetPositiveInt("lines", 2),
                args.GetPositiveInt("max-line-chars", 200),
                args.Flags.Contains("highlight"),
                settings.SearchMinScore ?? suggested,
                args.GetFraction("within"),
                !args.Flags.Contains("vector-only"),
                args.Flags.Contains("raw-query"),
                args.Get("path"));
            LineSearchResult result = await LineSearch.RunAsync(service, embeddings, indexes, args.Require("query"), options, ct);
            text = result.Text;
            topScore = result.TopScore;
            printed = result.Hits;
        }

        Console.Out.WriteLine(text);
        SearchSession.Save(IndexerSettings.DefaultHome, workingDirectory, printed);
        if (args.Get("log") is { } logFile)
        {
            string flags = string.Join(' ', args.Options.Where(o => o.Key is not ("query" or "index" or "log")).Select(o => $"--{o.Key} {o.Value}").Concat(args.Flags.Select(f => $"--{f}")));
            SearchSession.AppendLog(logFile, args.Require("query"), flags, text.Length, topScore);
        }

        return ExitOk;
    }

    /// <summary>Runs <c>read</c>: a plain-text window of an indexed file, or the JSON form with <c>--json</c>.</summary>
    private static async Task<int> ReadCommandAsync(IndexService service, IndexerSettings settings, CliArguments args, string workingDirectory, CancellationToken ct)
    {
        string index;
        string path;
        int? around;
        if (args.Get("hit") is not null)
        {
            int number = args.GetPositiveInt("hit", 1);
            IReadOnlyList<PrintedHit> hits = SearchSession.Load(IndexerSettings.DefaultHome, workingDirectory)
                ?? throw new UsageException("No previous search from this folder; run 'agency-index search' first, or pass --path.");
            PrintedHit hit = number <= hits.Count ? hits[number - 1] : throw new UsageException($"The last search printed {hits.Count} hit(s); there is no hit {number}.");
            (index, path, around) = (hit.Index, hit.Path, hit.StartLine);
        }
        else
        {
            (index, path) = (IndexName(settings), args.Require("path"));
            around = args.Get("around") is null ? null : args.GetPositiveInt("around", 1);
        }

        int window = args.GetPositiveInt("window", 40);
        bool all = args.Flags.Contains("all");
        int start;
        int? end;
        if (args.Get("start") is not null || args.Get("end") is not null)
        {
            (start, end) = (args.GetPositiveInt("start", 1), args.Get("end") is null ? null : args.GetPositiveInt("end", 1));
        }
        else if (all)
        {
            (start, end) = (1, null);
        }
        else if (around is { } line)
        {
            start = Math.Max(1, line - (window / 2));
            end = start + window - 1;
        }
        else
        {
            (start, end) = (1, window);
        }

        ReadResult result = await service.ReadAsync(index, path, start, end, ct, all);
        if (args.Flags.Contains("json"))
        {
            return Write(ExitOk, result);
        }

        string root = (await service.GetConfigAsync(index, ct)).Root;
        string rel = Path.GetRelativePath(root, result.Path).Replace(Path.DirectorySeparatorChar, '/');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"# {rel} lines {result.StartLine}-{result.EndLine} of {result.TotalLines}{(result.Stale ? " [stale: the file changed since it was indexed; run 'agency-index index']" : "")}");
        int number2 = result.StartLine;
        foreach (string text in result.Text.Split('\n'))
        {
            sb.Append(number2++).Append(": ").AppendLine(text);
        }

        Console.Out.Write(sb.ToString());
        return ExitOk;
    }

    private static List<CalibrationQuestion>? LoadQuestions(string? file)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            List<QuestionRow>? rows = JsonSerializer.Deserialize<List<QuestionRow>>(File.ReadAllText(file), JsonOptions);
            return rows is { Count: > 0 } && rows.All(r => !string.IsNullOrWhiteSpace(r.Question) && !string.IsNullOrWhiteSpace(r.ExpectedPath))
                ? rows.Select(r => new CalibrationQuestion(r.Question!, r.ExpectedPath!)).ToList()
                : throw new UsageException($"{file} must be a non-empty JSON array of {{\"question\", \"expected_path\"}} rows.");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new UsageException($"Cannot read questions from {file}: {ex.Message}");
        }
    }

    private sealed record QuestionRow(string? Question, string? ExpectedPath);

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
            args.Flags.Contains("no-text") || (!args.Flags.Contains("full") && args.Get("snippet-chars") is null),
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
            settings.Embedding.ModelId ?? "",
            settings.Passage);
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

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    private static int Write(int exitCode, object payload)
    {
        Console.Out.WriteLine(Serialize(payload));
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
