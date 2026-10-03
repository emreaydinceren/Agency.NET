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
          index   --index <name> [--root <dir>] [--ext .md,.txt,...] [--names README,...] [--max-file-kb 1024] [--wait]
          search  --index <name> --query <text> [--top 5]
          list    --index <name>
          indexes
          drop    --index <name> [--wait]
          install-skill [--dir <skills-root>]

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
        switch (args.Command)
        {
            case "help":
                Console.Out.WriteLine(Usage);
                return ExitOk;

            case "install-skill":
                string? dir = args.Get("dir");
                IReadOnlyList<string> roots = dir is not null
                    ? [dir]
                    : SkillInstaller.DefaultRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                return Write(ExitOk, new { status = "ok", installed = await SkillInstaller.InstallAsync(roots, ct) });

            case "index" or "search" or "list" or "indexes" or "drop":
                break;

            default:
                throw new UsageException($"Unknown command '{args.Command}'. Run 'agency-index help'.");
        }

        var settings = IndexerSettings.Resolve(args, IndexerSettings.DefaultHome);
        if (args.Command is "index" or "search")
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
                IndexResult indexed = await service.IndexAsync(
                    new IndexRequest(
                        IndexName(args),
                        args.Get("root"),
                        args.Get("ext") is { } ext ? FileScanner.ParseExtensions(ext) : null,
                        args.Get("names") is { } names ? FileScanner.SplitList(names) : null,
                        args.GetPositiveInt("max-file-kb", (int)(FileScanner.DefaultMaxFileBytes / 1024)) * 1024L,
                        args.Flags.Contains("wait")),
                    ct);
                return Write(ExitCodeFor(indexed.Status), indexed);

            case "search":
                IReadOnlyList<SearchResultHit> hits = await service.SearchAsync(IndexName(args), args.Require("query"), args.GetPositiveInt("top", 5), ct);
                return Write(ExitOk, new { status = "ok", index = IndexName(args), hits });

            case "list":
                var (config, files) = await service.ListAsync(IndexName(args), ct);
                return Write(ExitOk, new
                {
                    status = "ok",
                    index = IndexName(args),
                    config,
                    files = files.Select(f => new { path = f.Path, size = f.Size, last_write_utc = new DateTime(f.LastWriteTicks, DateTimeKind.Utc), chunks = f.Chunks }),
                });

            case "indexes":
                var all = await service.ListIndexesAsync(ct);
                return Write(ExitOk, new { status = "ok", indexes = all.Select(i => new { name = i.Index, root = i.Config.Root, embedding_model = i.Config.EmbeddingModel }) });

            default:
                DropResult dropped = await service.DropAsync(IndexName(args), args.Flags.Contains("wait"), ct);
                return Write(ExitCodeFor(dropped.Status), dropped);
        }
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

    private static string IndexName(CliArguments args) =>
        ProjectName.TryNormalize(args.Require("index"), out string canonical, out string? error)
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
    private sealed class MissingEmbeddingGenerator : IEmbeddingGenerator
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
            throw new UsageException("No embedding endpoint configured.");

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            throw new UsageException("No embedding endpoint configured.");
    }
}
