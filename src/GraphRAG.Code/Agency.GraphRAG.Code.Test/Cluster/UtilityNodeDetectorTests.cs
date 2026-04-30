using Agency.GraphRAG.Code.Cluster;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="UtilityNodeDetector"/>.
/// </summary>
public sealed class UtilityNodeDetectorTests
{
    [Fact]
    public void HasStatisticalSignal_UsesPercentileAndFloor()
    {
        UtilityNodeDetector detector = new();
        WeightedGraph graph = CreateHighDegreeGraph();
        ClusterOptions options = new()
        {
            UtilityDegreePercentile = 90d,
            UtilityDegreeFloor = 4,
        };

        bool highDegree = detector.HasStatisticalSignal(graph.Nodes.Keys.First(), graph, options);
        bool lowDegree = detector.HasStatisticalSignal(graph.Nodes.Keys.Last(), graph, options);

        Assert.True(highDegree);
        Assert.False(lowDegree);
    }

    [Fact]
    public void HasTopologicalSignal_UsesNormalizedEntropyAcrossTrialClusters()
    {
        UtilityNodeDetector detector = new();
        (WeightedGraph graph, Guid centerId, Partition spreadPartition) = CreateSpreadGraph();

        double spread = detector.GetNormalizedClusterSpread(centerId, graph, spreadPartition);

        Assert.True(spread > 0.6d);
        Assert.True(detector.HasTopologicalSignal(centerId, graph, spreadPartition, new ClusterOptions()));
    }

    [Fact]
    public void MatchesNamingHint_UsesGlobPatterns()
    {
        UtilityNodeDetector detector = new();

        bool match = detector.MatchesNamingHint(
            new ClusterNodeInfo(Guid.NewGuid(), "Example.Logger", "project-a", "Payments.Shared"),
            ["*.Shared"]);

        Assert.True(match);
    }

    [Fact]
    public void DetectUtilityNodes_RequiresStatisticalAndTopologicalSignals()
    {
        UtilityNodeDetector detector = new();
        (WeightedGraph graph, Guid centerId, Partition partition) = CreateSpreadGraph();
        ClusterOptions options = new()
        {
            UtilityDegreePercentile = 90d,
            UtilityDegreeFloor = 3,
            UtilityClusterSpreadThreshold = 0.6d,
        };

        IReadOnlySet<Guid> utilityNodes = detector.DetectUtilityNodes(graph, partition, options);

        Assert.Contains(centerId, utilityNodes);
        Assert.DoesNotContain(graph.Nodes.Keys.First(id => id != centerId), utilityNodes);
    }

    [Fact]
    public void DetectUtilityNodes_NamingHintBreaksBorderlineTie()
    {
        UtilityNodeDetector detector = new();
        (WeightedGraph graph, Guid centerId, Partition partition) = CreateBorderlineGraph();
        ClusterOptions options = new()
        {
            UtilityDegreePercentile = 90d,
            UtilityDegreeFloor = 3,
            UtilityClusterSpreadThreshold = 0.8d,
            UtilityNamingHints = ["*.Shared"],
        };

        IReadOnlySet<Guid> utilityNodes = detector.DetectUtilityNodes(graph, partition, options);

        Assert.Contains(centerId, utilityNodes);
    }

    private static WeightedGraph CreateHighDegreeGraph()
    {
        ClusterNodeInfo center = new(Guid.NewGuid(), "Example.Shared.Logger", "project-a", "Payments.Shared");
        ClusterNodeInfo[] leaves = Enumerable.Range(0, 5).Select(index => new ClusterNodeInfo(Guid.NewGuid(), $"Example.Feature{index}", "project-a", "Payments.App")).ToArray();
        List<WeightedEdge> edges = leaves.Select(leaf => new WeightedEdge(center.SymbolId, leaf.SymbolId, 1d)).ToList();
        return WeightedGraph.Create([center, .. leaves], [.. edges]);
    }

    private static (WeightedGraph Graph, Guid CenterId, Partition Partition) CreateSpreadGraph()
    {
        ClusterNodeInfo center = new(Guid.NewGuid(), "Example.Shared.Logger", "project-a", "Payments.Shared");
        ClusterNodeInfo left = new(Guid.NewGuid(), "Example.Auth.A", "project-a", "Payments.App");
        ClusterNodeInfo middle = new(Guid.NewGuid(), "Example.Orders.B", "project-a", "Payments.App");
        ClusterNodeInfo right = new(Guid.NewGuid(), "Example.Payments.C", "project-a", "Payments.App");
        WeightedGraph graph = WeightedGraph.Create(
            [center, left, middle, right],
            new WeightedEdge(center.SymbolId, left.SymbolId, 1d),
            new WeightedEdge(center.SymbolId, middle.SymbolId, 1d),
            new WeightedEdge(center.SymbolId, right.SymbolId, 1d));
        Partition partition = new(new Dictionary<Guid, int>
        {
            [center.SymbolId] = 0,
            [left.SymbolId] = 1,
            [middle.SymbolId] = 2,
            [right.SymbolId] = 3,
        });
        return (graph, center.SymbolId, partition);
    }

    private static (WeightedGraph Graph, Guid CenterId, Partition Partition) CreateBorderlineGraph()
    {
        ClusterNodeInfo center = new(Guid.NewGuid(), "Example.Shared.Logger", "project-a", "Payments.Shared");
        ClusterNodeInfo left = new(Guid.NewGuid(), "Example.Auth.A", "project-a", "Payments.App");
        ClusterNodeInfo middle = new(Guid.NewGuid(), "Example.Orders.B", "project-a", "Payments.App");
        ClusterNodeInfo right = new(Guid.NewGuid(), "Example.Payments.C", "project-a", "Payments.App");
        ClusterNodeInfo helper = new(Guid.NewGuid(), "Example.Payments.Helper", "project-a", "Payments.App");
        WeightedGraph graph = WeightedGraph.Create(
            [center, left, middle, right, helper],
            new WeightedEdge(center.SymbolId, left.SymbolId, 1d),
            new WeightedEdge(center.SymbolId, middle.SymbolId, 1d),
            new WeightedEdge(center.SymbolId, right.SymbolId, 1d),
            new WeightedEdge(center.SymbolId, helper.SymbolId, 1d));
        Partition partition = new(new Dictionary<Guid, int>
        {
            [center.SymbolId] = 0,
            [left.SymbolId] = 1,
            [middle.SymbolId] = 2,
            [right.SymbolId] = 2,
            [helper.SymbolId] = 3,
        });
        return (graph, center.SymbolId, partition);
    }
}
