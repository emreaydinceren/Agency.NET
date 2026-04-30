using System.Reflection;
using AgencyEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using Agency.GraphRAG.Code.Agentic;
using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Manifest;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.References;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;
using Agency.Sql.Postgre;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.AI;
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

        services.TryAddSingleton<AgencyEmbeddingGenerator, ZeroEmbeddingGenerator>();
        services.TryAddSingleton<IChatClient, NullChatClient>();

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
        services.TryAddSingleton<IncrementalHydrator>(static sp =>
            new IncrementalHydrator(
                sp.GetRequiredService<IGraphStore>(),
                (request, cancellationToken) => sp.GetRequiredService<Phase1Writer>().WriteAsync(request, cancellationToken),
                (fileId, packages, cancellationToken) => sp.GetRequiredService<Phase2Resolver>().ResolveAsync(fileId, packages, cancellationToken),
                static (_, _) => Task.FromResult<Guid?>(null),
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
                (repo, _, cancellationToken) => sp.GetRequiredService<ManifestParserOrchestrator>().ParseAsync(repo, cancellationToken),
                static (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, Phase1WriteRequest>>(new Dictionary<string, Phase1WriteRequest>(StringComparer.Ordinal)),
                static (_, _) => Task.CompletedTask,
                sp.GetRequiredService<ChangeDetector.ChangeDetector>(),
                static (_, walkResult, _, _) => Task.FromResult(new ChangeSet
                {
                    AddedFiles = walkResult.Files.Where(static file => file.Status is WalkedFileStatus.Added).Select(static file => file.Path).ToArray(),
                    ModifiedFiles = walkResult.Files
                        .Where(static file => file.Status is WalkedFileStatus.Modified)
                        .Select(static file => new ModifiedFileChange(file.Path, []))
                        .ToArray(),
                    DeletedFiles = walkResult.Files.Where(static file => file.Status is WalkedFileStatus.Deleted).Select(static file => file.Path).ToArray(),
                    RenamedFiles = walkResult.Files
                        .Where(static file => file.Status is WalkedFileStatus.Renamed)
                        .Select(static file => new RenamedFileChange(file.OldPath ?? string.Empty, file.Path, []))
                        .ToArray(),
                    ManifestChanges = [],
                }),
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

    private sealed class ZeroEmbeddingGenerator : AgencyEmbeddingGenerator
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadOnlyMemory<float>.Empty);
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(static _ => ReadOnlyMemory<float>.Empty).ToArray());
        }
    }

    private sealed class NullChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("NullChatClient", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Code index chat client is not configured.")])
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming is not configured for the default code index chat client.");

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose()
        {
        }
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
}
