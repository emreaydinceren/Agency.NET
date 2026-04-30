using Agency.GraphRAG.Code.Cluster;
using Moq;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="TwoPassClusterer"/>.
/// </summary>
public sealed class TwoPassClustererTests
{
    [Fact]
    public void Cluster_ProducesPrimaryAndUtilityAssignments()
    {
        Mock<IUtilityNodeDetector> utilityDetector = new();
        utilityDetector
            .Setup(detector => detector.DetectUtilityNodes(It.IsAny<WeightedGraph>(), It.IsAny<Partition>(), It.IsAny<ClusterOptions>()))
            .Returns((WeightedGraph weightedGraph, Partition _, ClusterOptions _) => new HashSet<Guid> { weightedGraph.Nodes.Values.Single(node => node.ProjectName == "Payments.Shared").SymbolId });

        TwoPassClusterer clusterer = new(
            utilityDetector.Object,
            new EdgeWeighter(new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Off, LeidenResolution = 0.2d }),
            new HierarchicalProjectSeeder(new LeidenRunner()));
        ClusterGraph graph = CreateGraph();

        IReadOnlyList<ClusterAssignment> assignments = clusterer.Cluster(graph, new ClusterOptions
        {
            ProjectBoundaryMode = ProjectBoundaryMode.Off,
            LeidenResolution = 0.2d,
            UtilityAssignmentStrategy = UtilityAssignmentStrategy.Dedicated,
        });

        Assert.Equal(5, assignments.Count);
        Assert.Equal(1, assignments.Count(assignment => assignment.Kind == ClusterMembershipKind.Utility));
        Assert.True(assignments.Where(assignment => assignment.Kind == ClusterMembershipKind.Primary).Select(assignment => assignment.ClusterId).Distinct().Count() >= 2);
    }

    [Fact]
    public void Cluster_ByDefinition_AssignsUtilityToDominantPrimaryCluster()
    {
        Mock<IUtilityNodeDetector> utilityDetector = new();
        Guid utilityId = Guid.Empty;
        utilityDetector
            .Setup(detector => detector.DetectUtilityNodes(It.IsAny<WeightedGraph>(), It.IsAny<Partition>(), It.IsAny<ClusterOptions>()))
            .Returns((WeightedGraph weightedGraph, Partition _, ClusterOptions _) =>
            {
                utilityId = weightedGraph.Nodes.Values.Single(node => node.ProjectName == "Payments.Shared").SymbolId;
                return new HashSet<Guid> { utilityId };
            });

        TwoPassClusterer clusterer = new(
            utilityDetector.Object,
            new EdgeWeighter(new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Off, LeidenResolution = 0.2d }),
            new HierarchicalProjectSeeder(new LeidenRunner()));
        ClusterGraph graph = CreateGraph();

        IReadOnlyList<ClusterAssignment> assignments = clusterer.Cluster(graph, new ClusterOptions
        {
            ProjectBoundaryMode = ProjectBoundaryMode.Off,
            LeidenResolution = 0.2d,
            UtilityAssignmentStrategy = UtilityAssignmentStrategy.ByDefinition,
        });

        ClusterAssignment utilityAssignment = Assert.Single(assignments, assignment => assignment.Kind == ClusterMembershipKind.Utility);
        Assert.Contains(assignments.Where(assignment => assignment.Kind == ClusterMembershipKind.Primary).Select(static assignment => assignment.ClusterId), id => id == utilityAssignment.ClusterId);
        Assert.Equal(utilityId, utilityAssignment.SymbolId);
    }

    private static ClusterGraph CreateGraph()
    {
        ClusterNodeInfo auth1 = new(Guid.NewGuid(), "Payments.Auth", "project-a", "Payments.App");
        ClusterNodeInfo auth2 = new(Guid.NewGuid(), "Payments.Auth", "project-a", "Payments.App");
        ClusterNodeInfo order1 = new(Guid.NewGuid(), "Payments.Orders", "project-a", "Payments.App");
        ClusterNodeInfo order2 = new(Guid.NewGuid(), "Payments.Orders", "project-a", "Payments.App");
        ClusterNodeInfo logger = new(Guid.NewGuid(), "Payments.Shared", "project-a", "Payments.Shared");

        return new ClusterGraph(
            new Dictionary<Guid, ClusterNodeInfo>
            {
                [auth1.SymbolId] = auth1,
                [auth2.SymbolId] = auth2,
                [order1.SymbolId] = order1,
                [order2.SymbolId] = order2,
                [logger.SymbolId] = logger,
            },
            [
                Edge(auth1.SymbolId, auth2.SymbolId, 1d),
                Edge(order1.SymbolId, order2.SymbolId, 1d),
                Edge(logger.SymbolId, auth1.SymbolId, 1d),
                Edge(logger.SymbolId, auth2.SymbolId, 1d),
                Edge(logger.SymbolId, order1.SymbolId, 1d),
            ]);
    }

    private static Agency.GraphRAG.Code.Domain.Edge Edge(Guid sourceId, Guid targetId, double confidence) =>
        new()
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            SourceKind = "symbol",
            TargetId = targetId,
            TargetKind = "symbol",
            EdgeKind = Agency.GraphRAG.Code.Domain.EdgeKind.References,
            Confidence = confidence,
            Signals = [],
            Properties = new Dictionary<string, object?>(),
        };
}
