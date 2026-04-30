namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Partitions graphs while respecting project boundaries when configured.
/// </summary>
public interface IHierarchicalProjectSeeder
{
    /// <summary>
    /// Partitions a weighted graph using the configured project-boundary rules.
    /// </summary>
    Partition Seed(WeightedGraph graph, ClusterOptions options);
}

/// <summary>
/// Applies project-aware seeding before the final clustering pass.
/// </summary>
public sealed class HierarchicalProjectSeeder(ILeidenRunner leidenRunner) : IHierarchicalProjectSeeder
{
    /// <inheritdoc />
    public Partition Seed(WeightedGraph graph, ClusterOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProjectBoundaryMode != ProjectBoundaryMode.Hard)
        {
            return leidenRunner.Run(graph, options.LeidenResolution, options.Seed);
        }

        Dictionary<Guid, int> assignments = [];
        int nextCommunityId = 0;

        foreach (IGrouping<string, KeyValuePair<Guid, ClusterNodeInfo>> projectGroup in graph.Nodes.GroupBy(static pair => pair.Value.ProjectKey, StringComparer.Ordinal))
        {
            Dictionary<Guid, ClusterNodeInfo> projectNodes = projectGroup.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            WeightedGraph subgraph = new(
                projectNodes,
                graph.Edges.Where(edge => projectNodes.ContainsKey(edge.SourceId) && projectNodes.ContainsKey(edge.TargetId)).ToArray());

            Partition partition = leidenRunner.Run(subgraph, options.LeidenResolution, options.Seed);
            IReadOnlyDictionary<int, IReadOnlyList<Guid>> communities = partition.GetCommunities();
            foreach ((_, IReadOnlyList<Guid> nodeIds) in communities.OrderBy(static pair => pair.Key))
            {
                foreach (Guid nodeId in nodeIds)
                {
                    assignments[nodeId] = nextCommunityId;
                }

                nextCommunityId++;
            }
        }

        return new Partition(assignments);
    }
}
