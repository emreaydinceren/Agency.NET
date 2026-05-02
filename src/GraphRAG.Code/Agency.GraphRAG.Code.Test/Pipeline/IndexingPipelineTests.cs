using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Pipeline;

/// <summary>
/// Tests for <see cref="IndexingPipeline"/>.
/// </summary>
public sealed class IndexingPipelineTests
{
    [Fact]
    public async Task RunAsync_ExecutesStagesInOrder_AndUpdatesIndexedCommit()
    {
        List<string> stages = [];
        TrackingGraphStore graphStore = new();
        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = @"E:\Repos\Sample",
            IsShallow = false,
            IndexedCommit = "old",
            IndexedAt = null,
            RemoteUrl = null,
        };
        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            Files = [new WalkedFile { Path = @"src\Service.cs", Status = WalkedFileStatus.Modified, Language = Language.CSharp }],
            HeadCommit = "new-head",
            IsShallowRepository = false,
        };
        Phase1WriteRequest request = new(
            new SourceFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                RepoId = repo.Id,
                Path = @"src\Service.cs",
                Language = "csharp",
                ContentHash = null,
            },
            null,
            [],
            new Dictionary<string, Agency.GraphRAG.Code.Summarizer.SymbolSummary>(StringComparer.Ordinal),
            []);
        IncrementalHydrator incrementalHydrator = new(
            graphStore,
            (_, _) => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<Guid?>(null),
            (_, _) => Task.FromResult<IReadOnlyList<Guid>>([]));
        IndexingPipeline pipeline = new(
            graphStore,
            (inputRepo, _) =>
            {
                stages.Add("walk");
                return Task.FromResult(walkResult);
            },
            (_, _, _) =>
            {
                stages.Add("manifest");
                return Task.CompletedTask;
            },
            (_, _, _) =>
            {
                stages.Add("chunk");
                return Task.FromResult<IReadOnlyDictionary<string, Phase1WriteRequest>>(new Dictionary<string, Phase1WriteRequest>(StringComparer.Ordinal)
                {
                    [request.File.Path] = request,
                });
            },
            (requests, _) =>
            {
                stages.Add("summarize");
                Assert.Single(requests);
                return Task.CompletedTask;
            },
            new Agency.GraphRAG.Code.ChangeDetector.ChangeDetector(),
            (_, _, requests, _) =>
            {
                stages.Add("detect");
                return Task.FromResult(new ChangeSet
                {
                    AddedFiles = [],
                    ModifiedFiles = [new ModifiedFileChange(request.File.Path, [])],
                    DeletedFiles = [],
                    RenamedFiles = [],
                    ManifestChanges = [],
                });
            },
            incrementalHydrator,
            requests =>
            {
                stages.Add("packages");
                return new Dictionary<Guid, IReadOnlyList<ExternalPackage>>
                {
                    [request.File.Id] = [],
                };
            });

        await pipeline.RunAsync(repo, TestContext.Current.CancellationToken);

        Assert.Equal(["walk", "manifest", "chunk", "summarize", "detect", "packages"], stages);
        Assert.Equal((repo.Id, "new-head"), Assert.Single(graphStore.IndexedCommits));
    }

    private sealed class TrackingGraphStore : IGraphStore
    {
        public List<(Guid RepoId, string Commit)> IndexedCommits { get; } = [];

        public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default)
        {
            IndexedCommits.Add((repoId, indexedCommit));
            return Task.CompletedTask;
        }

        public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TraversalHop>>([]);
        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult<Symbol?>(null);
        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Symbol>>([]);
        public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);
        public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>>(new Dictionary<string, IReadOnlyList<Symbol>>());
        public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<SourceFile?>(null);
    }
}
