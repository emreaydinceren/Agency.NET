using Agency.GraphRAG.Code.DependencyInjection;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.CommandLine;

namespace Agency.GraphRAG.Code.Cli;

/// <summary>
/// Builds the GraphRAG.Code command-line surface.
/// </summary>
public static class CliApplication
{
    /// <summary>
    /// Creates the root command for the CLI.
    /// </summary>
    /// <param name="workingDirectory">The working directory used for default SQLite selection.</param>
    /// <returns>The configured root command.</returns>
    public static RootCommand BuildRootCommand(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var storeOption = new Option<string>("--store", () => "sqlite", "Graph store provider: sqlite or postgres.");
        var connectionOption = new Option<string?>("--connection", "Explicit connection string.");

        var indexCommand = new Command("index", "Indexes a repository into the code graph.");
        var repoArgument = new Argument<string>("repo", "The repository path to index.");
        indexCommand.AddArgument(repoArgument);
        indexCommand.AddOption(storeOption);
        indexCommand.AddOption(connectionOption);
        indexCommand.SetHandler(
            async (string repo, string store, string? connection) =>
            {
                CliInvocation invocation = CreateIndexInvocation(repo, store, connection, workingDirectory);
                using IHost host = CreateHost(invocation);
                IGraphStore graphStore = host.Services.GetRequiredService<IGraphStore>();
                await graphStore.InitializeSchemaAsync().ConfigureAwait(false);
                await host.Services.GetRequiredService<IndexingPipeline>().RunAsync(invocation.Repo!, CancellationToken.None).ConfigureAwait(false);
            },
            repoArgument,
            storeOption,
            connectionOption);

        var queryCommand = new Command("query", "Queries the indexed code graph.");
        var questionArgument = new Argument<string>("question", "The question to ask.");
        var topKOption = new Option<int>("--top-k", () => 5, "Maximum number of results.");
        queryCommand.AddArgument(questionArgument);
        queryCommand.AddOption(storeOption);
        queryCommand.AddOption(connectionOption);
        queryCommand.AddOption(topKOption);
        queryCommand.SetHandler(
            async (string question, string store, string? connection, int topK) =>
            {
                CliInvocation invocation = CreateQueryInvocation(question, store, connection, topK, workingDirectory);
                using IHost host = CreateHost(invocation);
                IGraphStore graphStore = host.Services.GetRequiredService<IGraphStore>();
                await graphStore.InitializeSchemaAsync().ConfigureAwait(false);
                string response = (await host.Services.GetRequiredService<QueryPipeline>()
                    .ExecuteAsync(invocation.Question!)
                    .ConfigureAwait(false)).Answer;
                Console.Out.WriteLine(response);
            },
            questionArgument,
            storeOption,
            connectionOption,
            topKOption);

        var rootCommand = new RootCommand("GraphRAG.Code CLI");
        rootCommand.AddCommand(indexCommand);
        rootCommand.AddCommand(queryCommand);
        return rootCommand;
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
