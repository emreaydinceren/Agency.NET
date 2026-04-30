using Agency.GraphRAG.Code.Cluster;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using Moq;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="ClusterWorker"/>.
/// </summary>
public sealed class ClusterWorkerTests
{
    [Fact]
    public async Task RunAsync_AppliesAssignments_ThenReplacesClusterSummariesOnceAtTheEnd()
    {
        Mock<IGraphStore> store = new(MockBehavior.Strict);
        Mock<IClusterGraphProvider> provider = new(MockBehavior.Strict);
        Mock<ITwoPassClusterer> clusterer = new(MockBehavior.Strict);
        Mock<IClusterSummarizer> summarizer = new(MockBehavior.Strict);
        ClusterWorkspace workspace = CreateWorkspace();
        List<string> callOrder = [];
        IReadOnlyList<ClusterAssignment> assignments =
        [
            new(workspace.Symbols.Keys.First(), Guid.NewGuid(), ClusterMembershipKind.Primary, 0, false),
            new(workspace.Symbols.Keys.Last(), Guid.NewGuid(), ClusterMembershipKind.Utility, -1, true),
        ];
        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> summaries =
        [
            new()
            {
                Id = assignments[0].ClusterId,
                Label = "Payments.Auth",
                Type = ClusterType.Business,
                CoherenceScore = 0.9d,
                Summary = "Handles auth.",
                Embedding = [1f],
            },
        ];

        provider.Setup(source => source.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        clusterer.Setup(worker => worker.Cluster(workspace.Graph, It.IsAny<ClusterOptions>())).Returns(assignments);
        store.Setup(graphStore => graphStore.ApplyClusterAssignmentsAsync(It.IsAny<IReadOnlyDictionary<Guid, (Guid, string)>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("apply"))
            .Returns(Task.CompletedTask);
        summarizer.Setup(worker => worker.SummarizeAsync(It.IsAny<IReadOnlyList<ClusterSummaryRequest>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("summarize"))
            .ReturnsAsync(summaries);
        store.Setup(graphStore => graphStore.ReplaceClusterSummariesAtomicallyAsync(summaries, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("replace"))
            .Returns(Task.CompletedTask);

        ClusterWorker worker = new(store.Object, provider.Object, clusterer.Object, summarizer.Object);

        await worker.RunAsync(new ClusterOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(["apply", "summarize", "replace"], callOrder);
        store.Verify(graphStore => graphStore.ReplaceClusterSummariesAtomicallyAsync(summaries, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ClusterWorkspace CreateWorkspace()
    {
        ClusterNodeInfo auth = new(Guid.NewGuid(), "Payments.Auth", "project-a", "Payments.App");
        ClusterNodeInfo utility = new(Guid.NewGuid(), "Payments.Shared", "project-a", "Payments.Shared");
        ClusterGraph graph = new(
            new Dictionary<Guid, ClusterNodeInfo>
            {
                [auth.SymbolId] = auth,
                [utility.SymbolId] = utility,
            },
            []);

        Dictionary<Guid, Symbol> symbols = new()
        {
            [auth.SymbolId] = CreateSymbol(auth.SymbolId, "Payments.Auth.Authorize"),
            [utility.SymbolId] = CreateSymbol(utility.SymbolId, "Payments.Shared.Logger"),
        };

        return new ClusterWorkspace(graph, symbols);
    }

    private static Symbol CreateSymbol(Guid id, string fullyQualifiedName) =>
        new()
        {
            Id = id,
            FileId = Guid.NewGuid(),
            Name = fullyQualifiedName.Split('.').Last(),
            FullyQualifiedName = fullyQualifiedName,
            Kind = SymbolKind.Class,
            IsUtility = fullyQualifiedName.Contains(".Shared.", StringComparison.Ordinal),
            SourceRangeStart = 1,
            SourceRangeEnd = 2,
        };
}
