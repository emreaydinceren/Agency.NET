using Agency.GraphRAG.Code.Cli.Telemetry;
using Agency.GraphRAG.Code.DependencyInjection;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

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
            try
            {
                string workingDirectory = Directory.GetCurrentDirectory();
                CliInvocation invocation = CreateIndexInvocation(
                    settings.Repo!,
                    settings.Store,
                    settings.Connection,
                    settings.EmbeddingBaseUrl,
                    settings.EmbeddingModelId,
                    settings.EmbeddingApiKey,
                    workingDirectory);

                AnsiConsole.MarkupLine($"[bold blue]Indexing repository:[/] {invocation.Repo!.LocalPath}");
                AnsiConsole.MarkupLine($"[dim]Store:[/] {invocation.Options.Store} | [dim]Working directory:[/] {workingDirectory}");
                AnsiConsole.WriteLine();

                using IHost host = CreateHost(invocation);
                IGraphStore graphStore = host.Services.GetRequiredService<IGraphStore>();

                AnsiConsole.MarkupLine("[yellow]→[/] Initializing database schema...");
                var stopwatch = Stopwatch.StartNew();
                await graphStore.InitializeSchemaAsync().ConfigureAwait(false);
                stopwatch.Stop();
                AnsiConsole.MarkupLine($"[green]✓[/] Schema initialized ({stopwatch.ElapsedMilliseconds}ms)");
                AnsiConsole.WriteLine();

                AnsiConsole.MarkupLine("[yellow]→[/] Running indexing pipeline...");
                stopwatch.Restart();

                var pipelineStopwatch = Stopwatch.StartNew();
                void ReportProgress(string message)
                {
                    pipelineStopwatch.Stop();
                    AnsiConsole.MarkupLine($"  [dim]({pipelineStopwatch.ElapsedMilliseconds}ms)[/] {Markup.Escape(message)}");
                    pipelineStopwatch.Restart();
                }

                try
                {
                    await host.Services.GetRequiredService<IndexingPipeline>()
                        .RunAsync(invocation.Repo!, cancellationToken, ReportProgress)
                        .ConfigureAwait(false);
                }
                finally
                {
                    pipelineStopwatch.Stop();
                }

                stopwatch.Stop();
                AnsiConsole.MarkupLine($"[green]✓[/] Indexing complete ({stopwatch.ElapsedMilliseconds}ms)");
                AnsiConsole.WriteLine();

                AnsiConsole.MarkupLine("[green bold]✓ Indexing succeeded[/]");
                return 0;
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Indexing cancelled by user[/]");
                return 130;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red bold]✗ Indexing failed[/]");
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.GetType().Name)}:[/] {Markup.Escape(ex.Message)}");
                if (!string.IsNullOrWhiteSpace(ex.InnerException?.Message))
                {
                    AnsiConsole.MarkupLine($"[dim red]Cause:[/] {Markup.Escape(ex.InnerException.Message)}");
                }
                AnsiConsole.WriteLine();
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
            }
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

        [CommandOption("--embedding-base-url")]
        public string? EmbeddingBaseUrl { get; init; }

        [CommandOption("--embedding-model-id")]
        public string? EmbeddingModelId { get; init; }

        [CommandOption("--embedding-api-key")]
        public string? EmbeddingApiKey { get; init; }

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
                settings.EmbeddingBaseUrl,
                settings.EmbeddingModelId,
                settings.EmbeddingApiKey,
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

        [CommandOption("--embedding-base-url")]
        public string? EmbeddingBaseUrl { get; init; }

        [CommandOption("--embedding-model-id")]
        public string? EmbeddingModelId { get; init; }

        [CommandOption("--embedding-api-key")]
        public string? EmbeddingApiKey { get; init; }
    }

    /// <summary>
    /// Computes runtime settings for the index command.
    /// </summary>
    public static CliInvocation CreateIndexInvocation(
        string repo,
        string store,
        string? connection,
        string? embeddingBaseUrl,
        string? embeddingModelId,
        string? embeddingApiKey,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        return new CliInvocation(
            BuildOptions(store, connection, workingDirectory),
            Embedding: new CliEmbeddingOptions(embeddingBaseUrl, embeddingModelId, embeddingApiKey),
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
        string? embeddingBaseUrl,
        string? embeddingModelId,
        string? embeddingApiKey,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        return new CliInvocation(
            BuildOptions(store, connection, workingDirectory),
            Embedding: new CliEmbeddingOptions(embeddingBaseUrl, embeddingModelId, embeddingApiKey),
            Question: question,
            TopK: topK);
    }

    private static IHost CreateHost(CliInvocation invocation)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false);

        builder.Services
            .AddOptions<Agency.Embeddings.OpenAI.EmbeddingOptions>()
            .Bind(builder.Configuration.GetSection(Agency.Embeddings.OpenAI.EmbeddingOptions.SectionName))
            .PostConfigure(options =>
            {
                if (!string.IsNullOrWhiteSpace(invocation.Embedding.BaseUrl))
                {
                    options.BaseUrl = invocation.Embedding.BaseUrl;
                }

                if (!string.IsNullOrWhiteSpace(invocation.Embedding.ModelId))
                {
                    options.ModelId = invocation.Embedding.ModelId;
                }

                if (!string.IsNullOrWhiteSpace(invocation.Embedding.ApiKey))
                {
                    options.ApiKey = invocation.Embedding.ApiKey;
                }

                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    options.ApiKey = "lmstudio";
                }
            })
            .Validate(static o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Embedding:BaseUrl is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.ApiKey), "Embedding:ApiKey is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.ModelId), "Embedding:ModelId is required.")
            .ValidateOnStart();

        builder.Services.AddTelemetry(builder.Configuration);

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
    CliEmbeddingOptions Embedding,
    Repo? Repo = null,
    string? Question = null,
    int TopK = 5);

/// <summary>
/// Captures resolved embedding configuration for CLI execution.
/// </summary>
/// <param name="BaseUrl">Embedding service base URL.</param>
/// <param name="ModelId">Embedding model identifier.</param>
/// <param name="ApiKey">Embedding API key.</param>
public sealed record CliEmbeddingOptions(
    string? BaseUrl,
    string? ModelId,
    string? ApiKey);
