using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Loads cluster summaries for query-time expansion.
/// </summary>
public interface IClusterQuerySource
{
    /// <summary>
    /// Loads the clusters for the supplied identifiers.
    /// </summary>
    Task<IReadOnlyList<ClusterRecord>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default);
}
