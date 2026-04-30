using Agency.GraphRAG.Code.Cluster;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="LeidenRunner"/>.
/// </summary>
public sealed class LeidenRunnerTests
{
    [Fact]
    public void Run_TwoCliquesWithBridge_ReturnsTwoCommunities()
    {
        LeidenRunner runner = new();
        WeightedGraph graph = CreateTwoCliquesGraph();

        Partition partition = runner.Run(graph, resolution: 0.2d, seed: 17);

        Assert.Equal(2, partition.CommunityCount);
    }

    [Fact]
    public void Run_BarbellGraph_ReturnsTwoCommunities()
    {
        LeidenRunner runner = new();
        WeightedGraph graph = CreateBarbellGraph();

        Partition partition = runner.Run(graph, resolution: 0.2d, seed: 17);

        Assert.Equal(2, partition.CommunityCount);
    }

    [Fact]
    public void Run_CompleteGraph_ReturnsOneCommunity()
    {
        LeidenRunner runner = new();
        WeightedGraph graph = CreateCompleteGraph(4, "project-a");

        Partition partition = runner.Run(graph, resolution: 0.2d, seed: 17);

        Assert.Equal(1, partition.CommunityCount);
    }

    [Fact]
    public void Run_HigherResolution_SplitsWeaklyBridgedGraphMoreAggressively()
    {
        LeidenRunner runner = new();
        WeightedGraph graph = CreateTwoCliquesGraph();

        Partition lowResolution = runner.Run(graph, resolution: 0.05d, seed: 17);
        Partition highResolution = runner.Run(graph, resolution: 0.2d, seed: 17);

        Assert.Equal(1, lowResolution.CommunityCount);
        Assert.Equal(2, highResolution.CommunityCount);
    }

    private static WeightedGraph CreateTwoCliquesGraph()
    {
        ClusterNodeInfo a1 = Node("A1", "project-a");
        ClusterNodeInfo a2 = Node("A2", "project-a");
        ClusterNodeInfo a3 = Node("A3", "project-a");
        ClusterNodeInfo b1 = Node("B1", "project-a");
        ClusterNodeInfo b2 = Node("B2", "project-a");
        ClusterNodeInfo b3 = Node("B3", "project-a");

        return WeightedGraph.Create(
            [a1, a2, a3, b1, b2, b3],
            new WeightedEdge(a1.SymbolId, a2.SymbolId, 1d),
            new WeightedEdge(a1.SymbolId, a3.SymbolId, 1d),
            new WeightedEdge(a2.SymbolId, a3.SymbolId, 1d),
            new WeightedEdge(b1.SymbolId, b2.SymbolId, 1d),
            new WeightedEdge(b1.SymbolId, b3.SymbolId, 1d),
            new WeightedEdge(b2.SymbolId, b3.SymbolId, 1d),
            new WeightedEdge(a3.SymbolId, b1.SymbolId, 0.1d));
    }

    private static WeightedGraph CreateBarbellGraph()
    {
        ClusterNodeInfo left1 = Node("L1", "project-a");
        ClusterNodeInfo left2 = Node("L2", "project-a");
        ClusterNodeInfo left3 = Node("L3", "project-a");
        ClusterNodeInfo right1 = Node("R1", "project-a");
        ClusterNodeInfo right2 = Node("R2", "project-a");
        ClusterNodeInfo right3 = Node("R3", "project-a");

        return WeightedGraph.Create(
            [left1, left2, left3, right1, right2, right3],
            new WeightedEdge(left1.SymbolId, left2.SymbolId, 1d),
            new WeightedEdge(left1.SymbolId, left3.SymbolId, 1d),
            new WeightedEdge(left2.SymbolId, left3.SymbolId, 1d),
            new WeightedEdge(right1.SymbolId, right2.SymbolId, 1d),
            new WeightedEdge(right1.SymbolId, right3.SymbolId, 1d),
            new WeightedEdge(right2.SymbolId, right3.SymbolId, 1d),
            new WeightedEdge(left3.SymbolId, right1.SymbolId, 0.1d));
    }

    private static WeightedGraph CreateCompleteGraph(int count, string projectKey)
    {
        ClusterNodeInfo[] nodes = Enumerable.Range(0, count).Select(index => Node($"N{index}", projectKey)).ToArray();
        List<WeightedEdge> edges = [];

        for (int left = 0; left < nodes.Length; left++)
        {
            for (int right = left + 1; right < nodes.Length; right++)
            {
                edges.Add(new WeightedEdge(nodes[left].SymbolId, nodes[right].SymbolId, 1d));
            }
        }

        return WeightedGraph.Create(nodes, [.. edges]);
    }

    private static ClusterNodeInfo Node(string name, string projectKey) =>
        new(Guid.NewGuid(), $"Example.{name}", projectKey, projectKey);
}
