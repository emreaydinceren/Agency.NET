using System.Reflection;
using AgencyEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using Agency.Embeddings.OpenAI;
using Agency.GraphRAG.Code.Agentic;
using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Manifest;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.References;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;
using Agency.Llm.Claude;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Agency.Sql.Postgre;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agency.GraphRAG.Code.DependencyInjection;

/// <summary>
/// Registers GraphRAG code-index services and the selected graph store implementation.
/// </summary>
public static class CodeIndexServiceCollectionExtensions
{
    /// <summary>
    /// Adds the code-index service graph and the configured backing store.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional options delegate.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance.</returns>
    public static IServiceCollection AddCodeIndex(
        this IServiceCollection services,
        Action<CodeIndexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<CodeIndexOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<EmbeddingOptions>();
        services.TryAddSingleton<AgencyEmbeddingGenerator, EmbeddingGenerator>();

        services.AddOptions<LlmClientOptions>()
            .Configure<IConfiguration>((opts, config) =>
            {
                opts.ClientType = config["LlmClient:ClientType"] ?? "OpenAI";
                opts.BaseUrl = config["LlmClient:BaseUrl"] ?? "http://llm-host.example:1234/v1";
                opts.ApiKey = config["LlmClient:ApiKey"] ?? "lmstudio";

                if (config["LlmClient:Timeout"] is string timeoutStr && TimeSpan.TryParse(timeoutStr, out var timeout))
                {
                    opts.Timeout = timeout;
                }
                else
                {
                    opts.Timeout = TimeSpan.FromMinutes(3);
                }

                if (int.TryParse(config["LlmClient:MaxRetries"], out int maxRetries))
                {
                    opts.MaxRetries = maxRetries;
                }
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "LlmClient:ApiKey is required")
            .ValidateOnStart();

        services.TryAddSingleton<ClaudeClient>();
        services.TryAddSingleton<OpenAIClient>();
        services.TryAddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmClientOptions>>().Value;
            return options.ClientType switch
            {
                "claude" => sp.GetRequiredService<ClaudeClient>().CreateChatClient(),
                _ => sp.GetRequiredService<OpenAIClient>().CreateChatClient(),
            };
        });

        services.TryAddSingleton<GitProcessRunner>();
        services.TryAddSingleton<RepoWalker>();
        services.TryAddSingleton<IManifestParser>(static _ => new CSharpManifestParserAdapter(new CSharpManifestParser()));
        services.TryAddSingleton<IManifestParser>(static _ => new NpmManifestParserAdapter(new NpmManifestParser()));
        services.TryAddSingleton<IManifestParser>(static _ => new PythonManifestParserAdapter(new PythonManifestParser()));
        services.TryAddSingleton<ManifestParserOrchestrator>();
        services.TryAddSingleton<ExternalPackageHeuristic>();
        services.TryAddSingleton<ReferenceScorer>();
        services.TryAddSingleton<ScopeResolver>();
        services.TryAddSingleton<Phase1Writer>();
        services.TryAddSingleton<Phase2Resolver>();
        services.TryAddSingleton<ChangeDetector.ChangeDetector>();
        services.AddOptions<SummarizerOptions>()
            .Configure<IConfiguration>((opts, config) =>
            {
                opts.StrongModel = config["Summarizer:StrongModel"] ?? string.Empty;
                opts.StandardModel = config["Summarizer:StandardModel"] ?? string.Empty;
                opts.CheapModel = config["Summarizer:CheapModel"] ?? string.Empty;
                opts.CheapestModel = config["Summarizer:CheapestModel"] ?? string.Empty;
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.StrongModel), "Summarizer:StrongModel is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.StandardModel), "Summarizer:StandardModel is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.CheapModel), "Summarizer:CheapModel is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.CheapestModel), "Summarizer:CheapestModel is required.")
            .ValidateOnStart();
        RegisterTreeSitterServices(services);
        services.TryAddSingleton<SummaryCache>(static sp =>
        {
            CodeIndexOptions opts = sp.GetRequiredService<IOptions<CodeIndexOptions>>().Value;
            string dbPath = Path.Combine(opts.WorkingDirectory, "graphrag-code.summaries.db");
            return new SummaryCache(dbPath);
        });
        services.TryAddSingleton<ModelTierSelector>();
        services.TryAddSingleton<SummarizationPromptBuilder>();
        services.TryAddSingleton<SymbolSummarizer>();
        services.TryAddSingleton<IncrementalHydrator>(static sp =>
            new IncrementalHydrator(
                sp.GetRequiredService<IGraphStore>(),
                (request, cancellationToken) => sp.GetRequiredService<Phase1Writer>().WriteAsync(request, cancellationToken),
                (fileId, packages, cancellationToken) => sp.GetRequiredService<Phase2Resolver>().ResolveAsync(fileId, packages, cancellationToken),
                async (path, cancellationToken) =>
                {
                    SourceFile? file = await sp.GetRequiredService<IGraphStore>().GetFileByPathAsync(path, cancellationToken).ConfigureAwait(false);
                    return file?.Id;
                },
                static (_, _) => Task.FromResult<IReadOnlyList<Guid>>([])));

        services.TryAddSingleton<IGraphStore>(static sp => CreateGraphStore(sp));
        services.TryAddSingleton(new QueryOptions
        {
            CheapestModel = "code-index-cheap",
            AnswerModel = "code-index-answer",
            ContextTokenBudget = 600,
        });
        services.TryAddSingleton<QueryClassifier>();
        services.TryAddSingleton<QueryPlanner>();
        services.TryAddSingleton<IClusterQuerySource, EmptyClusterQuerySource>();
        services.TryAddSingleton<ISymbolTextProvider, EmptySymbolTextProvider>();
        services.TryAddSingleton<HybridRetriever>();
        services.TryAddSingleton<ContextAssembler>();
        services.TryAddSingleton<QueryPipeline>();
        services.TryAddSingleton<ICodeIndex, CodeIndexCapability>();
        services.TryAddSingleton<CodeIndexAgentTool>();
        services.TryAddSingleton<IndexingPipeline>(static sp =>
            new IndexingPipeline(
                sp.GetRequiredService<IGraphStore>(),
                (repo, cancellationToken) => sp.GetRequiredService<RepoWalker>().WalkAsync(repo, cancellationToken),
                (repo, _, onProgress, cancellationToken) => sp.GetRequiredService<ManifestParserOrchestrator>().ParseAsync(repo, cancellationToken, onProgress),
                (repo, walkResult, progress, cancellationToken) => sp.GetRequiredService<IWriteRequestBuilder>().BuildAsync(repo, walkResult, cancellationToken, progress),
                async (requests, onProgress, cancellationToken) =>
                {
                    var summarizer = sp.GetRequiredService<SymbolSummarizer>();
                    IReadOnlyList<Chunk> allChunks = requests.SelectMany(r => r.Chunks).ToArray();
                    Action<int, int, int, string>? summarizerProgress = onProgress is null ? null :
                        (done, failed, total, symbolName) => onProgress($"Summarized {done}/{total} symbols ({failed} failed): {symbolName}");
                    SummarizationResult result = await summarizer.SummarizeAsync(allChunks, cancellationToken, summarizerProgress).ConfigureAwait(false);
                    foreach (Phase1WriteRequest request in requests)
                    {
                        foreach ((string id, SymbolSummary summary) in result.Summaries)
                        {
                            if (request.Chunks.Any(c => c.Id == id))
                            {
                                request.Summaries[id] = summary;
                            }
                        }
                    }
                },
                sp.GetRequiredService<ChangeDetector.ChangeDetector>(),
                async (repo, walkResult, writeRequests, cancellationToken) =>
                {
                    var paths = walkResult.Files
                        .Where(f => f.Status is WalkedFileStatus.Modified or WalkedFileStatus.Renamed)
                        .Select(f => f.Path)
                        .ToArray();
                    var store = sp.GetRequiredService<IGraphStore>();
                    var storedSymbolsByPath = await store.GetSymbolsByPathsAsync(paths, cancellationToken).ConfigureAwait(false);
                    var currentChunksByPath = writeRequests.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (IReadOnlyList<Chunk>)kvp.Value.Chunks,
                        StringComparer.Ordinal);
                    return sp.GetRequiredService<ChangeDetector.ChangeDetector>().Detect(walkResult, storedSymbolsByPath, currentChunksByPath);
                },
                sp.GetRequiredService<IncrementalHydrator>(),
                static _ => new Dictionary<Guid, IReadOnlyList<ExternalPackage>>()));

        return services;
    }

    private static IGraphStore CreateGraphStore(IServiceProvider serviceProvider)
    {
        CodeIndexOptions options = serviceProvider.GetRequiredService<IOptions<CodeIndexOptions>>().Value;

        return options.Store switch
        {
            CodeIndexStore.Sqlite => CreateSqliteGraphStore(serviceProvider, GetSqliteConnectionString(options)),
            CodeIndexStore.Postgres => CreatePostgresGraphStore(
                serviceProvider,
                !string.IsNullOrWhiteSpace(options.ConnectionString)
                    ? options.ConnectionString
                    : throw new InvalidOperationException("Postgres store requires a connection string.")),
            _ => throw new InvalidOperationException($"Unsupported store '{options.Store}'."),
        };
    }

    private static IGraphStore CreateSqliteGraphStore(IServiceProvider serviceProvider, string connectionString)
    {
        SqliteRunner runner = ActivatorUtilities.CreateInstance<SqliteRunner>(serviceProvider, connectionString);
        return CreateGraphStoreViaReflection(serviceProvider, "Agency.GraphRAG.Code.Sqlite", "Agency.GraphRAG.Code.Sqlite.SqliteGraphStore", runner);
    }

    private static IGraphStore CreatePostgresGraphStore(IServiceProvider serviceProvider, string connectionString)
    {
        PostgreSqlRunner runner = ActivatorUtilities.CreateInstance<PostgreSqlRunner>(serviceProvider, connectionString);
        return CreateGraphStoreViaReflection(serviceProvider, "Agency.GraphRAG.Code.Postgres", "Agency.GraphRAG.Code.Postgres.PostgresGraphStore", runner, connectionString);
    }

    private static IGraphStore CreateGraphStoreViaReflection(
        IServiceProvider serviceProvider,
        string assemblyName,
        string typeName,
        params object[] args)
    {
        Assembly assembly = Assembly.Load(assemblyName);
        Type type = assembly.GetType(typeName, throwOnError: true)!;
        return (IGraphStore)ActivatorUtilities.CreateInstance(serviceProvider, type, args);
    }

    private static string GetSqliteConnectionString(CodeIndexOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return options.ConnectionString;
        }

        string sqlitePath = !string.IsNullOrWhiteSpace(options.SqlitePath)
            ? options.SqlitePath
            : Path.Combine(options.WorkingDirectory, options.DefaultSqliteFileName);

        return $"Data Source={sqlitePath}";
    }

    private sealed class CSharpManifestParserAdapter(CSharpManifestParser parser) : IManifestParser
    {
        public bool CanParse(string manifestRelativePath) => parser.CanParse(manifestRelativePath);

        public Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(ManifestParserContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManifestProjectDefinition>>([ToProjectDefinition(parser.Parse(context.Repo.LocalPath, context.ManifestPath), "csharp")]);
    }

    private sealed class NpmManifestParserAdapter(NpmManifestParser parser) : IManifestParser
    {
        public bool CanParse(string manifestRelativePath) => parser.CanParse(manifestRelativePath);

        public Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(ManifestParserContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManifestProjectDefinition>>([ToProjectDefinition(parser.Parse(context.Repo.LocalPath, context.ManifestPath), "typescript")]);
    }

    private sealed class PythonManifestParserAdapter(PythonManifestParser parser) : IManifestParser
    {
        public bool CanParse(string manifestRelativePath) => parser.CanParse(manifestRelativePath);

        public Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(ManifestParserContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManifestProjectDefinition>>([ToProjectDefinition(parser.Parse(context.Repo.LocalPath, context.ManifestPath), "python")]);
    }

    private static ManifestProjectDefinition ToProjectDefinition(ManifestParseResult result, string language) =>
        new()
        {
            Name = result.ProjectName,
            Language = language,
            RelativePath = result.ProjectRelativePath,
            ManifestPath = result.ManifestRelativePath,
            ExternalPackages = result.ExternalDependencies
                .Select(package => new ManifestExternalPackage(package.Name, package.Version, result.Ecosystem, package.Scope))
                .ToArray(),
            ReferencedProjectPaths = result.ProjectReferences.Select(static project => project.ManifestRelativePath).ToArray(),
        };

    private sealed class EmptyClusterQuerySource : IClusterQuerySource
    {
        public Task<IReadOnlyList<Domain.Cluster>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Domain.Cluster>>([]);
    }

    private sealed class EmptySymbolTextProvider : ISymbolTextProvider
    {
        public Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private static void RegisterTreeSitterServices(IServiceCollection services)
    {
        Type? treeSitterClientType = Type.GetType(
            "Agency.GraphRAG.Code.TreeSitter.TreeSitterClient, Agency.GraphRAG.Code.TreeSitter",
            throwOnError: false);
        if (treeSitterClientType is null)
        {
            throw new InvalidOperationException("Tree-sitter assembly not loaded. Ensure Agency.GraphRAG.Code.TreeSitter is available.");
        }

        services.AddSingleton(treeSitterClientType);

        services.TryAddSingleton<ChunkerDispatcher>(static _ =>
        {
            var chunkers = new Dictionary<Language, IChunker>
            {
                [Language.CSharp] = new CSharpChunker(),
                [Language.TypeScript] = new TypeScriptChunker(),
                [Language.Tsx] = new TypeScriptChunker(),
                [Language.JavaScript] = new TypeScriptChunker(),
                [Language.Jsx] = new TypeScriptChunker(),
                [Language.Python] = new PythonChunker(),
            };
            return new ChunkerDispatcher(chunkers);
        });

        Type? writeRequestBuilderType = Type.GetType(
            "Agency.GraphRAG.Code.TreeSitter.Pipeline.WriteRequestBuilder, Agency.GraphRAG.Code.TreeSitter",
            throwOnError: false);
        if (writeRequestBuilderType is not null)
        {
            services.AddSingleton(typeof(IWriteRequestBuilder), writeRequestBuilderType);
        }
    }
}
