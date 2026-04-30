using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// A retrieved cluster summary and its rank.
/// </summary>
public sealed record QueryClusterResult
{
    /// <summary>Gets the retrieved cluster.</summary>
    public required ClusterRecord Cluster { get; init; }

    /// <summary>Gets the similarity score.</summary>
    public required double Score { get; init; }
}
