using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Manifest;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Test.Manifest;

/// <summary>
/// Tests for <see cref="ManifestParserOrchestrator"/>.
/// </summary>
public sealed class ManifestParserOrchestratorTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(ManifestParserOrchestratorTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ParseAsync_DiscoversSupportedManifests_AndPersistsProjectsPackagesAndEdges()
    {
        Directory.CreateDirectory(Path.Combine(this.testRoot, "src", "Api"));
        Directory.CreateDirectory(Path.Combine(this.testRoot, "src", "Shared"));
        File.WriteAllText(Path.Combine(this.testRoot, "src", "Api", "Api.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(this.testRoot, "src", "Shared", "package.json"), "{}");
        File.WriteAllText(Path.Combine(this.testRoot, "README.md"), "ignore");

        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = this.testRoot,
            IsShallow = false,
        };

        FakeManifestParser csharpParser = new(
            manifestPath => manifestPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase),
            context =>
            [
                new ManifestProjectDefinition
                {
                    Name = "Api",
                    Language = "csharp",
                    RelativePath = "src/Api",
                    ManifestPath = context.ManifestRelativePath,
                    ExternalPackages =
                    [
                        new ManifestExternalPackage("Newtonsoft.Json", "13.0.3", "nuget", "runtime"),
                    ],
                    ReferencedProjectPaths = ["src/Shared"],
                },
            ]);
        FakeManifestParser npmParser = new(
            manifestPath => manifestPath.EndsWith("package.json", StringComparison.OrdinalIgnoreCase),
            context =>
            [
                new ManifestProjectDefinition
                {
                    Name = "Shared",
                    Language = "typescript",
                    RelativePath = "src/Shared",
                    ManifestPath = context.ManifestRelativePath,
                    ExternalPackages =
                    [
                        new ManifestExternalPackage("react", "18.3.1", "npm", "runtime"),
                    ],
                },
            ]);
        FakeGraphStore graphStore = new();
        ManifestParserOrchestrator orchestrator = new(graphStore, [csharpParser, npmParser]);

        await orchestrator.ParseAsync(repo, TestContext.Current.CancellationToken);

        Assert.Equal(["src/Api/Api.csproj"], csharpParser.ParsedManifestPaths);
        Assert.Equal(["src/Shared/package.json"], npmParser.ParsedManifestPaths);

        Assert.Collection(
            graphStore.Projects.OrderBy(project => project.RelativePath, StringComparer.Ordinal),
            api =>
            {
                Assert.Equal(repo.Id, api.RepoId);
                Assert.Equal("csharp", api.Language);
                Assert.Equal("Api", api.Name);
                Assert.Equal("src/Api", api.RelativePath);
                Assert.Equal("src/Api/Api.csproj", api.ManifestPath);
            },
            shared =>
            {
                Assert.Equal(repo.Id, shared.RepoId);
                Assert.Equal("typescript", shared.Language);
                Assert.Equal("Shared", shared.Name);
                Assert.Equal("src/Shared", shared.RelativePath);
                Assert.Equal("src/Shared/package.json", shared.ManifestPath);
            });

        Assert.Collection(
            graphStore.ExternalPackages.OrderBy(package => package.Name, StringComparer.Ordinal),
            nugetPackage =>
            {
                Assert.Equal("Newtonsoft.Json", nugetPackage.Name);
                Assert.Equal("13.0.3", nugetPackage.Version);
                Assert.Equal("nuget", nugetPackage.Ecosystem);
                Assert.Equal("runtime", nugetPackage.Scope);
            },
            npmPackage =>
            {
                Assert.Equal("react", npmPackage.Name);
                Assert.Equal("18.3.1", npmPackage.Version);
                Assert.Equal("npm", npmPackage.Ecosystem);
                Assert.Equal("runtime", npmPackage.Scope);
            });

        Edge dependencyEdge = Assert.Single(graphStore.Edges);
        Project apiProject = Assert.Single(graphStore.Projects, project => project.Name == "Api");
        Project sharedProject = Assert.Single(graphStore.Projects, project => project.Name == "Shared");
        Assert.Equal(apiProject.Id, dependencyEdge.SourceId);
        Assert.Equal(sharedProject.Id, dependencyEdge.TargetId);
        Assert.Equal(nameof(Project), dependencyEdge.SourceKind);
        Assert.Equal(nameof(Project), dependencyEdge.TargetKind);
        Assert.Equal(EdgeKind.DependsOn, dependencyEdge.EdgeKind);
        Assert.Equal(1.0d, dependencyEdge.Confidence);
    }

    [Fact]
    public async Task ParseAsync_DeduplicatesIntraRepoReferencesAcrossProjectAndManifestPaths()
    {
        Directory.CreateDirectory(Path.Combine(this.testRoot, "src", "App"));
        Directory.CreateDirectory(Path.Combine(this.testRoot, "src", "Library"));
        File.WriteAllText(Path.Combine(this.testRoot, "src", "App", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(this.testRoot, "src", "Library", "Library.csproj"), "<Project />");

        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = this.testRoot,
            IsShallow = false,
        };

        FakeManifestParser parser = new(
            manifestPath => manifestPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase),
            context => context.ManifestRelativePath switch
            {
                "src/App/App.csproj" =>
                [
                    new ManifestProjectDefinition
                    {
                        Name = "App",
                        Language = "csharp",
                        RelativePath = "src/App",
                        ManifestPath = context.ManifestRelativePath,
                        ReferencedProjectPaths =
                        [
                            "src/Library",
                            "src/Library/Library.csproj",
                        ],
                    },
                ],
                "src/Library/Library.csproj" =>
                [
                    new ManifestProjectDefinition
                    {
                        Name = "Library",
                        Language = "csharp",
                        RelativePath = "src/Library",
                        ManifestPath = context.ManifestRelativePath,
                    },
                ],
                _ => throw new InvalidOperationException("Unexpected manifest."),
            });
        FakeGraphStore graphStore = new();
        ManifestParserOrchestrator orchestrator = new(graphStore, [parser]);

        await orchestrator.ParseAsync(repo, TestContext.Current.CancellationToken);

        Edge dependencyEdge = Assert.Single(graphStore.Edges);
        Project appProject = Assert.Single(graphStore.Projects, project => project.Name == "App");
        Project libraryProject = Assert.Single(graphStore.Projects, project => project.Name == "Library");
        Assert.Equal(appProject.Id, dependencyEdge.SourceId);
        Assert.Equal(libraryProject.Id, dependencyEdge.TargetId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(this.testRoot))
        {
            Directory.Delete(this.testRoot, recursive: true);
        }
    }

    private sealed class FakeManifestParser(
        Func<string, bool> canParse,
        Func<ManifestParserContext, IReadOnlyList<ManifestProjectDefinition>> parse)
        : IManifestParser
    {
        public List<string> ParsedManifestPaths { get; } = [];

        public bool CanParse(string manifestRelativePath) => canParse(manifestRelativePath);

        public Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(
            ManifestParserContext context,
            CancellationToken cancellationToken = default)
        {
            this.ParsedManifestPaths.Add(context.ManifestRelativePath);
            return Task.FromResult(parse(context));
        }
    }

    private sealed class FakeGraphStore : IGraphStore
    {
        public List<Project> Projects { get; } = [];

        public List<ExternalPackage> ExternalPackages { get; } = [];

        public List<Edge> Edges { get; } = [];

        public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default)
        {
            this.Projects.Add(project);
            return Task.CompletedTask;
        }

        public Task UpsertExternalPackageBatchAsync(
            IReadOnlyList<ExternalPackage> packages,
            CancellationToken cancellationToken = default)
        {
            this.ExternalPackages.AddRange(packages);
            return Task.CompletedTask;
        }

        public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertModuleAsync(Agency.GraphRAG.Code.Domain.Module module, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default)
        {
            this.Edges.AddRange(edges);
            return Task.CompletedTask;
        }

        public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TraversalHop>>([]);

        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult<Symbol?>(null);

        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([]);

        public Task StageUnresolvedCallSiteBatchAsync(
            IReadOnlyList<UnresolvedCallSite> callSites,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(
            Guid? sourceFileId = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);

        public Task ApplyClusterAssignmentsAsync(
            IReadOnlyDictionary<Guid, (Guid, string)> assignments,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReplaceClusterSummariesAtomicallyAsync(
            IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>>(new Dictionary<string, IReadOnlyList<Symbol>>());

        public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<SourceFile?>(null);
    }
}
