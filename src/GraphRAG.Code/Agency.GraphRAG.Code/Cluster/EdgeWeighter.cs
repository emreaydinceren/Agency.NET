using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Computes clustering weights from graph edges and symbol boundary metadata.
/// </summary>
public sealed class EdgeWeighter(ClusterOptions options)
{
    /// <summary>
    /// Computes the clustering weight for an edge.
    /// </summary>
    /// <param name="edge">The graph edge.</param>
    /// <param name="source">The source symbol metadata.</param>
    /// <param name="target">The target symbol metadata.</param>
    /// <returns>The adjusted clustering weight.</returns>
    public double Weight(Edge edge, ClusterNodeInfo source, ClusterNodeInfo target)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        double weight = edge.Confidence;

        if (edge.EdgeKind == EdgeKind.References && IsSameNamespace(source.Namespace, target.Namespace))
        {
            weight *= options.NamespaceWeightMultiplier;
        }

        if (!string.Equals(source.ProjectKey, target.ProjectKey, StringComparison.Ordinal))
        {
            weight = options.ProjectBoundaryMode switch
            {
                ProjectBoundaryMode.Off => weight,
                ProjectBoundaryMode.Soft => weight * options.InterProjectWeightMultiplier,
                ProjectBoundaryMode.Hard => 0d,
                _ => throw new ArgumentOutOfRangeException(),
            };
        }

        return weight;
    }

    /// <summary>
    /// Builds a weighted graph from raw graph edges.
    /// </summary>
    /// <param name="graph">The raw graph.</param>
    /// <returns>The weighted graph.</returns>
    public WeightedGraph Build(ClusterGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        WeightedEdge[] weightedEdges = graph.Edges
            .Where(edge => graph.Nodes.ContainsKey(edge.SourceId) && graph.Nodes.ContainsKey(edge.TargetId))
            .Select(edge => new WeightedEdge(
                edge.SourceId,
                edge.TargetId,
                Weight(edge, graph.Nodes[edge.SourceId], graph.Nodes[edge.TargetId])))
            .Where(static edge => edge.Weight > 0d)
            .ToArray();

        return new WeightedGraph(graph.Nodes, weightedEdges);
    }

    private static bool IsSameNamespace(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.Ordinal);
}

/// <summary>
/// Carries the boundary metadata required to weight a cluster edge.
/// </summary>
/// <param name="SymbolId">The symbol identifier.</param>
/// <param name="Namespace">The symbol namespace.</param>
/// <param name="ProjectKey">The owning project identifier or stable key.</param>
/// <param name="ProjectName">The human-readable project name, if known.</param>
public sealed record ClusterNodeInfo(Guid SymbolId, string? Namespace, string ProjectKey, string? ProjectName = null);
