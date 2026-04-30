using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Default cluster query source used when no cluster lookup service is configured.
/// </summary>
public sealed class EmptyClusterQuerySource : IClusterQuerySource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ClusterRecord>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ClusterRecord>>([]);
    }
}
