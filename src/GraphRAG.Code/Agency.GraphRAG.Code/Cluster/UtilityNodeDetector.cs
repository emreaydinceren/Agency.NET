using System.Text.RegularExpressions;

namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Detects structural utility nodes that should be excluded from the main clustering objective.
/// </summary>
public interface IUtilityNodeDetector
{
    /// <summary>
    /// Detects utility nodes from a trial partition.
    /// </summary>
    IReadOnlySet<Guid> DetectUtilityNodes(WeightedGraph graph, Partition trialPartition, ClusterOptions options);
}

/// <summary>
/// Applies the Phase 14 utility-node detection rules.
/// </summary>
public sealed class UtilityNodeDetector : IUtilityNodeDetector
{
    private const double BorderlineFactor = 0.9d;

    /// <inheritdoc />
    public IReadOnlySet<Guid> DetectUtilityNodes(WeightedGraph graph, Partition trialPartition, ClusterOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(trialPartition);
        ArgumentNullException.ThrowIfNull(options);

        HashSet<Guid> utilityNodes = [];
        foreach (Guid nodeId in graph.Nodes.Keys)
        {
            bool statistical = HasStatisticalSignal(nodeId, graph, options);
            double spread = GetNormalizedClusterSpread(nodeId, graph, trialPartition);
            bool topological = spread >= options.UtilityClusterSpreadThreshold;
            bool namingHint = MatchesNamingHint(graph.Nodes[nodeId], options.UtilityNamingHints);
            bool borderlineSpread = spread >= options.UtilityClusterSpreadThreshold * BorderlineFactor;
            bool borderlineDegree = graph.GetDegree(nodeId) >= GetDegreeThreshold(graph, options) * BorderlineFactor;

            if ((statistical && topological)
                || (namingHint && statistical && borderlineSpread)
                || (namingHint && topological && borderlineDegree))
            {
                utilityNodes.Add(nodeId);
            }
        }

        return utilityNodes;
    }

    /// <summary>
    /// Determines whether a node is in the configured high-degree tail.
    /// </summary>
    public bool HasStatisticalSignal(Guid nodeId, WeightedGraph graph, ClusterOptions options) =>
        graph.GetDegree(nodeId) >= GetDegreeThreshold(graph, options);

    /// <summary>
    /// Determines whether a node's neighbors are spread across many clusters.
    /// </summary>
    public bool HasTopologicalSignal(Guid nodeId, WeightedGraph graph, Partition trialPartition, ClusterOptions options) =>
        GetNormalizedClusterSpread(nodeId, graph, trialPartition) >= options.UtilityClusterSpreadThreshold;

    /// <summary>
    /// Gets the normalized spread of a node's neighbors across trial communities.
    /// </summary>
    public double GetNormalizedClusterSpread(Guid nodeId, WeightedGraph graph, Partition trialPartition)
    {
        IReadOnlyList<Guid> neighbors = graph.GetNeighbors(nodeId);
        if (neighbors.Count <= 1)
        {
            return 0d;
        }

        IReadOnlyList<int> clusterIds = neighbors.Select(neighbor => trialPartition.Assignments[neighbor]).ToArray();
        int distinctClusterCount = clusterIds.Distinct().Count();
        if (distinctClusterCount <= 1)
        {
            return 0d;
        }

        double total = clusterIds.Count;
        double entropy = clusterIds
            .GroupBy(static clusterId => clusterId)
            .Select(group =>
            {
                double probability = group.Count() / total;
                return -probability * Math.Log(probability, 2);
            })
            .Sum();

        return entropy / Math.Log(distinctClusterCount, 2);
    }

    /// <summary>
    /// Determines whether the node matches any configured utility naming hint.
    /// </summary>
    public bool MatchesNamingHint(ClusterNodeInfo node, IReadOnlyList<string> namingHints)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(namingHints);

        string candidate = node.ProjectName ?? node.ProjectKey;
        return namingHints.Any(pattern => GlobMatch(candidate, pattern));
    }

    internal int GetDegreeThreshold(WeightedGraph graph, ClusterOptions options)
    {
        int[] degrees = graph.Nodes.Keys.Select(graph.GetDegree).OrderBy(static value => value).ToArray();
        if (degrees.Length == 0)
        {
            return options.UtilityDegreeFloor;
        }

        int percentileIndex = (int)Math.Ceiling((options.UtilityDegreePercentile / 100d) * degrees.Length) - 1;
        percentileIndex = Math.Clamp(percentileIndex, 0, degrees.Length - 1);
        return Math.Max(options.UtilityDegreeFloor, degrees[percentileIndex]);
    }

    private static bool GlobMatch(string candidate, string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(candidate, regexPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
