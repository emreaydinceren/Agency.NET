using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Runs the Phase 14 two-pass clustering workflow.
/// </summary>
public interface ITwoPassClusterer
{
    /// <summary>
    /// Clusters the supplied graph and returns symbol-to-cluster assignments.
    /// </summary>
    IReadOnlyList<ClusterAssignment> Cluster(ClusterGraph graph, ClusterOptions options);
}

/// <summary>
/// Implements the utility-aware two-pass clustering flow.
/// </summary>
public sealed class TwoPassClusterer(
    IUtilityNodeDetector utilityNodeDetector,
    EdgeWeighter edgeWeighter,
    IHierarchicalProjectSeeder hierarchicalProjectSeeder) : ITwoPassClusterer
{
    /// <inheritdoc />
    public IReadOnlyList<ClusterAssignment> Cluster(ClusterGraph graph, ClusterOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        WeightedGraph weightedGraph = edgeWeighter.Build(graph);
        Partition trialPartition = hierarchicalProjectSeeder.Seed(weightedGraph, options);
        IReadOnlySet<Guid> utilityNodes = utilityNodeDetector.DetectUtilityNodes(weightedGraph, trialPartition, options);
        WeightedGraph cleanedGraph = weightedGraph.WithoutIncidentEdges(utilityNodes);
        Partition finalPartition = hierarchicalProjectSeeder.Seed(cleanedGraph, options);

        List<ClusterAssignment> assignments = [];
        Dictionary<int, Guid> primaryClusterIds = finalPartition.GetCommunities()
            .ToDictionary(
                static pair => pair.Key,
                pair => CreateStableGuid($"primary:{string.Join("|", pair.Value.OrderBy(static id => id))}"));

        foreach ((Guid nodeId, int communityId) in finalPartition.Assignments.OrderBy(static pair => pair.Value).ThenBy(static pair => pair.Key))
        {
            assignments.Add(new ClusterAssignment(
                nodeId,
                primaryClusterIds[communityId],
                ClusterMembershipKind.Primary,
                finalPartition[nodeId],
                false));
        }

        foreach (Guid utilityNodeId in utilityNodes.OrderBy(static id => id))
        {
            ClusterNodeInfo node = graph.Nodes[utilityNodeId];
            Guid clusterId = options.UtilityAssignmentStrategy switch
            {
                UtilityAssignmentStrategy.ByDefinition => FindDefinitionClusterId(utilityNodeId, weightedGraph, finalPartition, primaryClusterIds, node.ProjectKey),
                _ => CreateStableGuid($"utility:{node.ProjectKey}"),
            };

            assignments.RemoveAll(assignment => assignment.SymbolId == utilityNodeId);
            assignments.Add(new ClusterAssignment(
                utilityNodeId,
                clusterId,
                ClusterMembershipKind.Utility,
                -1,
                true));
        }

        return assignments
            .OrderBy(static assignment => assignment.ClusterId)
            .ThenBy(static assignment => assignment.SymbolId)
            .ToArray();
    }

    private static Guid FindDefinitionClusterId(
        Guid utilityNodeId,
        WeightedGraph graph,
        Partition finalPartition,
        IReadOnlyDictionary<int, Guid> primaryClusterIds,
        string projectKey)
    {
        Guid? clusterId = graph.Edges
            .Where(edge => edge.SourceId == utilityNodeId || edge.TargetId == utilityNodeId)
            .Select(edge => edge.SourceId == utilityNodeId ? edge.TargetId : edge.SourceId)
            .Where(finalPartition.Assignments.ContainsKey)
            .GroupBy(neighborId => finalPartition.Assignments[neighborId])
            .OrderByDescending(group => group.Count())
            .Select(group => (Guid?)primaryClusterIds[group.Key])
            .FirstOrDefault();

        return clusterId ?? CreateStableGuid($"utility:{projectKey}");
    }

    private static Guid CreateStableGuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}

/// <summary>
/// Represents the raw graph passed into the clusterer.
/// </summary>
public sealed class ClusterGraph
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterGraph"/> class.
    /// </summary>
    public ClusterGraph(IReadOnlyDictionary<Guid, ClusterNodeInfo> nodes, IReadOnlyList<Edge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        Nodes = new Dictionary<Guid, ClusterNodeInfo>(nodes);
        Edges = edges.ToArray();
    }

    /// <summary>
    /// Gets the node metadata keyed by symbol identifier.
    /// </summary>
    public IReadOnlyDictionary<Guid, ClusterNodeInfo> Nodes { get; }

    /// <summary>
    /// Gets the graph edges.
    /// </summary>
    public IReadOnlyList<Edge> Edges { get; }
}

/// <summary>
/// Represents a symbol's cluster assignment.
/// </summary>
/// <param name="SymbolId">The symbol identifier.</param>
/// <param name="ClusterId">The assigned cluster identifier.</param>
/// <param name="Kind">The membership kind.</param>
/// <param name="ClusterOrdinal">The numeric community identifier when available.</param>
/// <param name="IsUtilityCluster">A value indicating whether the target cluster is a utility cluster.</param>
public sealed record ClusterAssignment(
    Guid SymbolId,
    Guid ClusterId,
    ClusterMembershipKind Kind,
    int ClusterOrdinal,
    bool IsUtilityCluster);

/// <summary>
/// Describes the strength of a symbol's membership in a cluster.
/// </summary>
public enum ClusterMembershipKind
{
    /// <summary>The symbol is a primary member of the cluster.</summary>
    Primary,

    /// <summary>The symbol is assigned as a utility member.</summary>
    Utility,
}
