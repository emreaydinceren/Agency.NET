using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Test.Hydration;

/// <summary>
/// Tests for <see cref="IncrementalHydrator"/>.
/// </summary>
public sealed class IncrementalHydratorTests
{
    [Fact]
    public async Task HydrateAsync_AppliesForwardAndReverseInvalidationFlow()
    {
        TrackingGraphStore graphStore = new();
        RecordingPhase1Writer phase1Writer = new();
        RecordingPhase2Resolver phase2Resolver = new();
        Guid modifiedFileId = Guid.NewGuid();
        Guid reverseAffectedFileId = Guid.NewGuid();
        IncrementalHydrator hydrator = new(
            graphStore,
            (request, _) =>
            {
                phase1Writer.Requests.Add(request);
                return Task.CompletedTask;
            },
            (fileId, packages, _) =>
            {
                phase2Resolver.Calls.Add((fileId, packages));
                return Task.CompletedTask;
            },
            (path, _) => Task.FromResult<Guid?>(path switch
            {
                @"src\Deleted.cs" => Guid.Parse("11111111-1111-1111-1111-111111111111"),
                @"src\OldName.cs" => Guid.Parse("22222222-2222-2222-2222-222222222222"),
                _ => null,
            }),
            (_, _) => Task.FromResult<IReadOnlyList<Guid>>([reverseAffectedFileId]));
        ChangeSet changeSet = new()
        {
            AddedFiles = [@"src\Added.cs"],
            ModifiedFiles = [new ModifiedFileChange(@"src\Modified.cs", [])],
            DeletedFiles = [@"src\Deleted.cs"],
            RenamedFiles = [new RenamedFileChange(@"src\OldName.cs", @"src\NewName.cs", [])],
            ManifestChanges = [],
        };
        Dictionary<string, Phase1WriteRequest> requests = new(StringComparer.Ordinal)
        {
            [@"src\Added.cs"] = CreateRequest(Guid.NewGuid(), @"src\Added.cs"),
            [@"src\Modified.cs"] = CreateRequest(modifiedFileId, @"src\Modified.cs"),
            [@"src\NewName.cs"] = CreateRequest(Guid.NewGuid(), @"src\NewName.cs"),
        };
        Dictionary<Guid, IReadOnlyList<ExternalPackage>> packagesByFileId = new()
        {
            [modifiedFileId] = [CreatePackage("Shared.Logging")],
            [reverseAffectedFileId] = [CreatePackage("Shared.Abstractions")],
        };

        await hydrator.HydrateAsync(changeSet, requests, packagesByFileId, TestContext.Current.CancellationToken);

        Assert.Equal([Guid.Parse("11111111-1111-1111-1111-111111111111")], graphStore.DeletedFileIds);
        Assert.Equal([(Guid.Parse("22222222-2222-2222-2222-222222222222"), @"src\NewName.cs")], graphStore.Renames);
        Assert.Equal([@"src\Added.cs", @"src\Modified.cs", @"src\NewName.cs"], phase1Writer.Requests.Select(static request => request.File.Path).OrderBy(static path => path).ToArray());
        Assert.Equal(4, phase2Resolver.Calls.Count);
        Assert.Contains(phase2Resolver.Calls, call => call.FileId == modifiedFileId && call.Packages.Count == 1);
        Assert.Contains(phase2Resolver.Calls, call => call.FileId == reverseAffectedFileId && call.Packages.Count == 1);
    }

    private static Phase1WriteRequest CreateRequest(Guid fileId, string path) =>
        new(
            new SourceFile
            {
                Id = fileId,
                ProjectId = Guid.NewGuid(),
                RepoId = Guid.NewGuid(),
                Path = path,
                Language = "csharp",
                ContentHash = null,
            },
            null,
            [],
            new Dictionary<string, Agency.GraphRAG.Code.Summarizer.SymbolSummary>(StringComparer.Ordinal),
            []);

    private static ExternalPackage CreatePackage(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = name,
            Version = "1.0.0",
            Ecosystem = "nuget",
            Scope = "runtime",
        };

    private sealed class TrackingGraphStore : IGraphStore
    {
        public List<Guid> DeletedFileIds { get; } = [];
        public List<(Guid FileId, string NewPath)> Renames { get; } = [];

        public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) { DeletedFileIds.Add(fileId); return Task.CompletedTask; }
        public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) { Renames.Add((fileId, newPath)); return Task.CompletedTask; }
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
        public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TraversalHop>>([]);
        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult<Symbol?>(null);
        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Symbol>>([]);
        public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);
        public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingPhase1Writer
    {
        public List<Phase1WriteRequest> Requests { get; } = [];
    }

    private sealed class RecordingPhase2Resolver
    {
        public List<(Guid FileId, IReadOnlyList<ExternalPackage> Packages)> Calls { get; } = [];
    }
}
