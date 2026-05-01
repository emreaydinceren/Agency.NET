using Agency.GraphRAG.Code.DependencyInjection;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console.Cli;

namespace Agency.GraphRAG.Code.Cli;

/// <summary>
/// Builds the GraphRAG.Code command-line surface using Spectre.Console.
/// </summary>
public static class CliApplication
{
    /// <summary>
    /// Creates and configures the CLI application.
    /// </summary>
    /// <param name="workingDirectory">The working directory used for default SQLite selection.</param>
    /// <returns>The configured command app ready to execute.</returns>
    public static CommandApp BuildApplication(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("graphrag-code");
            config.SetApplicationVersion("1.0.0");
            config.AddCommand<IndexCommand>("index")
                .WithDescription("Indexes a repository into the code graph");
            config.AddCommand<QueryCommand>("query")
                .WithDescription("Queries the indexed code graph");
        });

        return app;
    }

    /// <summary>
    /// Command for indexing a repository.
    /// </summary>
    private sealed class IndexCommand : Command<IndexSettings>
    {
        protected override int Execute(CommandContext context, IndexSettings settings, CancellationToken cancellationToken)
        {
            return ExecuteAsync(settings, cancellationToken).GetAwaiter().GetResult();
        }

        private static async Task<int> ExecuteAsync(IndexSettings settings, CancellationToken cancellationToken)
        {
            string workingDirectory = Directory.GetCurrentDirectory();
            CliInvocation invocation = CreateIndexInvocation(
                settings.Repo!,
                settings.Store,
                settings.Connection,
                workingDirectory);

            using IHost host = CreateHost(invocation);
            IGraphStore graphStore = host.Services.GetRequiredService<IGraphStore>();
            await graphStore.InitializeSchemaAsync().ConfigureAwait(false);
            await host.Services.GetRequiredService<IndexingPipeline>()
                .RunAsync(invocation.Repo!, cancellationToken)
                .ConfigureAwait(false);

            return 0;
        }
    }

    /// <summary>
    /// Settings for the index command.
    /// </summary>
    private sealed class IndexSettings : CommandSettings
    {
        [CommandArgument(0, "[repo]")]
        public string? Repo { get; init; }

        [CommandOption("--store")]
        public string Store { get; init; } = "sqlite";

        [CommandOption("--connection")]
        public string? Connection { get; init; }
    }

    /// <summary>
    /// Command for querying the code graph.
    /// </summary>
    private sealed class QueryCommand : AsyncCommand<QuerySettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, QuerySettings settings, CancellationToken cancellationToken)
        {
            string workingDirectory = Directory.GetCurrentDirectory();
            CliInvocation invocation = CreateQueryInvocation(
                settings.Question!,
                settings.Store,
                settings.Connection,
                settings.TopK,
                workingDirectory);

            using IHost host = CreateHost(invocation);
            IGraphStore graphStore = host.Services.GetRequiredService<IGraphStore>();
            await graphStore.InitializeSchemaAsync().ConfigureAwait(false);
            string response = (await host.Services.GetRequiredService<QueryPipeline>()
                .ExecuteAsync(invocation.Question!, cancellationToken)
                .ConfigureAwait(false)).Answer;
            Console.Out.WriteLine(response);

            return 0;
        }
    }

    /// <summary>
    /// Settings for the query command.
    /// </summary>
    private sealed class QuerySettings : CommandSettings
    {
        [CommandArgument(0, "[question]")]
        public string? Question { get; init; }

        [CommandOption("--store")]
        public string Store { get; init; } = "sqlite";

        [CommandOption("--connection")]
        public string? Connection { get; init; }

        [CommandOption("--top-k")]
        public int TopK { get; init; } = 5;
    }

    /// <summary>
    /// Computes runtime settings for the index command.
    /// </summary>
    public static CliInvocation CreateIndexInvocation(
        string repo,
        string store,
        string? connection,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        return new CliInvocation(
            BuildOptions(store, connection, workingDirectory),
            Repo: new Repo
            {
                Id = Guid.NewGuid(),
                LocalPath = repo,
                IsShallow = false,
                IndexedCommit = null,
                IndexedAt = null,
                RemoteUrl = null,
            });
    }

    /// <summary>
    /// Computes runtime settings for the query command.
    /// </summary>
    public static CliInvocation CreateQueryInvocation(
        string question,
        string store,
        string? connection,
        int topK,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        return new CliInvocation(
            BuildOptions(store, connection, workingDirectory),
            Question: question,
            TopK: topK);
    }

    private static IHost CreateHost(CliInvocation invocation)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddCodeIndex(options =>
        {
            options.Store = invocation.Options.Store;
            options.ConnectionString = invocation.Options.ConnectionString;
            options.SqlitePath = invocation.Options.SqlitePath;
            options.WorkingDirectory = invocation.Options.WorkingDirectory;
            options.DefaultSqliteFileName = invocation.Options.DefaultSqliteFileName;
        });

        return builder.Build();
    }

    private static CodeIndexOptions BuildOptions(string store, string? connection, string workingDirectory)
    {
        CodeIndexStore normalizedStore = ParseStore(store);
        if (normalizedStore is CodeIndexStore.Postgres && string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("Postgres store requires a connection string.");
        }

        string sqlitePath = Path.Combine(workingDirectory, "graphrag-code.db");
        return new CodeIndexOptions
        {
            Store = normalizedStore,
            ConnectionString = normalizedStore is CodeIndexStore.Sqlite
                ? connection ?? $"Data Source={sqlitePath}"
                : connection,
            SqlitePath = normalizedStore is CodeIndexStore.Sqlite ? sqlitePath : null,
            WorkingDirectory = workingDirectory,
        };
    }

    private static CodeIndexStore ParseStore(string store) =>
        store.ToLowerInvariant() switch
        {
            "sqlite" => CodeIndexStore.Sqlite,
            "postgres" => CodeIndexStore.Postgres,
            _ => throw new InvalidOperationException($"Unsupported store '{store}'."),
        };
}

/// <summary>
/// Captures resolved CLI runtime settings.
/// </summary>
/// <param name="Options">The resolved code-index options.</param>
/// <param name="Repo">The repository to index, when applicable.</param>
/// <param name="Question">The question to ask, when applicable.</param>
/// <param name="TopK">The result limit for queries.</param>
public sealed record CliInvocation(
    CodeIndexOptions Options,
    Repo? Repo = null,
    string? Question = null,
    int TopK = 5);
