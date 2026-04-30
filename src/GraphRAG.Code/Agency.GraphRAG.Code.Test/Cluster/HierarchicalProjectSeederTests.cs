using Agency.GraphRAG.Code.Cluster;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="HierarchicalProjectSeeder"/>.
/// </summary>
public sealed class HierarchicalProjectSeederTests
{
    [Fact]
    public void Seed_HardMode_DoesNotMixProjectsAcrossCommunities()
    {
        HierarchicalProjectSeeder seeder = new(new LeidenRunner());
        WeightedGraph graph = CreateCrossProjectGraph();

        Partition partition = seeder.Seed(graph, new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Hard, LeidenResolution = 0.2d });
        IReadOnlyDictionary<int, IReadOnlyList<Guid>> communities = partition.GetCommunities();

        Assert.All(
            communities.Values,
            members =>
            {
                string[] projectKeys = members.Select(id => graph.Nodes[id].ProjectKey).Distinct(StringComparer.Ordinal).ToArray();
                Assert.Single(projectKeys);
            });
    }

    [Fact]
    public void Seed_HardMode_ProducesNestedPerProjectPartition()
    {
        HierarchicalProjectSeeder seeder = new(new LeidenRunner());
        WeightedGraph graph = CreateCrossProjectGraph();

        Partition partition = seeder.Seed(graph, new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Hard, LeidenResolution = 0.2d });

        Assert.Equal(3, partition.CommunityCount);
    }

    private static WeightedGraph CreateCrossProjectGraph()
    {
        ClusterNodeInfo a1 = Node("A1", "project-a");
        ClusterNodeInfo a2 = Node("A2", "project-a");
        ClusterNodeInfo a3 = Node("A3", "project-a");
        ClusterNodeInfo a4 = Node("A4", "project-a");
        ClusterNodeInfo b1 = Node("B1", "project-b");
        ClusterNodeInfo b2 = Node("B2", "project-b");

        return WeightedGraph.Create(
            [a1, a2, a3, a4, b1, b2],
            new WeightedEdge(a1.SymbolId, a2.SymbolId, 1d),
            new WeightedEdge(a3.SymbolId, a4.SymbolId, 1d),
            new WeightedEdge(a2.SymbolId, a3.SymbolId, 0.1d),
            new WeightedEdge(b1.SymbolId, b2.SymbolId, 1d),
            new WeightedEdge(a4.SymbolId, b1.SymbolId, 1d));
    }

    private static ClusterNodeInfo Node(string name, string projectKey) =>
        new(Guid.NewGuid(), $"Example.{name}", projectKey, projectKey);
}
