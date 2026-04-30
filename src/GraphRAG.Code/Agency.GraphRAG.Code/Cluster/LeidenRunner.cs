namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Partitions weighted graphs into connected communities using the configured resolution threshold.
/// </summary>
public interface ILeidenRunner
{
    /// <summary>
    /// Runs the in-process partitioner.
    /// </summary>
    Partition Run(WeightedGraph graph, double resolution, int seed);
}

/// <summary>
/// Minimal in-process partitioner used for V1 clustering.
/// </summary>
public sealed class LeidenRunner : ILeidenRunner
{
    /// <inheritdoc />
    public Partition Run(WeightedGraph graph, double resolution, int seed)
    {
        ArgumentNullException.ThrowIfNull(graph);

        Dictionary<Guid, int> assignments = [];
        HashSet<Guid> visited = [];
        int communityId = 0;

        foreach (Guid nodeId in graph.Nodes.Keys.OrderBy(static id => id))
        {
            if (!visited.Add(nodeId))
            {
                continue;
            }

            Queue<Guid> queue = new();
            queue.Enqueue(nodeId);
            assignments[nodeId] = communityId;

            while (queue.Count > 0)
            {
                Guid current = queue.Dequeue();
                foreach (Guid neighbor in graph.GetNeighbors(current, resolution))
                {
                    if (!visited.Add(neighbor))
                    {
                        continue;
                    }

                    assignments[neighbor] = communityId;
                    queue.Enqueue(neighbor);
                }
            }

            communityId++;
        }

        return new Partition(assignments);
    }
}

/// <summary>
/// Represents a weighted graph used for clustering.
/// </summary>
public sealed class WeightedGraph
{
    private readonly Dictionary<Guid, List<WeightedEdge>> _adjacency;

    /// <summary>
    /// Initializes a new instance of the <see cref="WeightedGraph"/> class.
    /// </summary>
    public WeightedGraph(IReadOnlyDictionary<Guid, ClusterNodeInfo> nodes, IReadOnlyList<WeightedEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        Nodes = new Dictionary<Guid, ClusterNodeInfo>(nodes);
        Edges = edges.ToArray();
        _adjacency = Nodes.Keys.ToDictionary(static key => key, static _ => new List<WeightedEdge>());

        foreach (WeightedEdge edge in Edges)
        {
            if (!_adjacency.TryGetValue(edge.SourceId, out List<WeightedEdge>? sourceEdges)
                || !_adjacency.TryGetValue(edge.TargetId, out List<WeightedEdge>? targetEdges))
            {
                continue;
            }

            sourceEdges.Add(edge);
            targetEdges.Add(edge);
        }
    }

    /// <summary>
    /// Gets the nodes in the graph.
    /// </summary>
    public IReadOnlyDictionary<Guid, ClusterNodeInfo> Nodes { get; }

    /// <summary>
    /// Gets the undirected weighted edges in the graph.
    /// </summary>
    public IReadOnlyList<WeightedEdge> Edges { get; }

    /// <summary>
    /// Creates a graph from node metadata and weighted edges.
    /// </summary>
    public static WeightedGraph Create(IReadOnlyList<ClusterNodeInfo> nodes, params WeightedEdge[] edges) =>
        new(nodes.ToDictionary(static node => node.SymbolId), edges);

    /// <summary>
    /// Returns the neighboring node identifiers that meet the given weight threshold.
    /// </summary>
    public IReadOnlyList<Guid> GetNeighbors(Guid nodeId, double minimumWeight)
    {
        if (!_adjacency.TryGetValue(nodeId, out List<WeightedEdge>? edges))
        {
            return [];
        }

        return edges
            .Where(edge => edge.Weight >= minimumWeight)
            .Select(edge => edge.SourceId == nodeId ? edge.TargetId : edge.SourceId)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Returns all neighbors of a node regardless of weight.
    /// </summary>
    public IReadOnlyList<Guid> GetNeighbors(Guid nodeId) => GetNeighbors(nodeId, double.MinValue);

    /// <summary>
    /// Gets the total undirected degree of a node.
    /// </summary>
    public int GetDegree(Guid nodeId) => _adjacency.TryGetValue(nodeId, out List<WeightedEdge>? edges) ? edges.Count : 0;

    /// <summary>
    /// Gets the sum of adjacent edge weights for a node.
    /// </summary>
    public double GetWeightedDegree(Guid nodeId) => _adjacency.TryGetValue(nodeId, out List<WeightedEdge>? edges) ? edges.Sum(static edge => edge.Weight) : 0d;

    /// <summary>
    /// Creates a filtered graph that excludes edges touching the supplied nodes.
    /// </summary>
    public WeightedGraph WithoutIncidentEdges(IReadOnlySet<Guid> nodeIds) =>
        new(
            Nodes,
            Edges.Where(edge => !nodeIds.Contains(edge.SourceId) && !nodeIds.Contains(edge.TargetId)).ToArray());
}

/// <summary>
/// Represents a weighted undirected edge.
/// </summary>
/// <param name="SourceId">The source node identifier.</param>
/// <param name="TargetId">The target node identifier.</param>
/// <param name="Weight">The clustering weight.</param>
public sealed record WeightedEdge(Guid SourceId, Guid TargetId, double Weight);

/// <summary>
/// Represents a graph partition.
/// </summary>
public sealed class Partition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Partition"/> class.
    /// </summary>
    public Partition(IReadOnlyDictionary<Guid, int> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        Assignments = new Dictionary<Guid, int>(assignments);
    }

    /// <summary>
    /// Gets the community identifier for each node.
    /// </summary>
    public IReadOnlyDictionary<Guid, int> Assignments { get; }

    /// <summary>
    /// Gets the number of discovered communities.
    /// </summary>
    public int CommunityCount => Assignments.Values.Distinct().Count();

    /// <summary>
    /// Gets the community identifier for a node.
    /// </summary>
    public int this[Guid nodeId] => Assignments[nodeId];

    /// <summary>
    /// Returns the nodes grouped by community.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Guid>> GetCommunities() =>
        Assignments
            .GroupBy(static pair => pair.Value)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<Guid>)group.Select(static pair => pair.Key).OrderBy(static id => id).ToArray());
}
