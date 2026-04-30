using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Computes the Phase 14 tuning metrics for a clustering run.
/// </summary>
public sealed class ClusterTuningInstrumentation
{
    /// <summary>
    /// Evaluates the supplied clusters and sizes.
    /// </summary>
    public ClusterTuningSnapshot Evaluate(
        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters,
        IReadOnlyDictionary<Guid, int> clusterSizes)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(clusterSizes);

        double meanCoherence = clusters.Count == 0 ? 0d : clusters.Average(static cluster => cluster.CoherenceScore);
        double lowEndTail = clusters.Count == 0 ? 0d : clusters.Min(static cluster => cluster.CoherenceScore);
        int mixedClusterCount = clusters.Count(static cluster => cluster.Type == ClusterType.Mixed);
        IReadOnlyList<Guid> oversizedClusterIds = clusterSizes
            .Where(static pair => pair.Value > 200)
            .Select(static pair => pair.Key)
            .OrderBy(static id => id)
            .ToArray();

        return new ClusterTuningSnapshot(meanCoherence, lowEndTail, mixedClusterCount, oversizedClusterIds);
    }
}

/// <summary>
/// Captures the high-level tuning metrics for one clustering run.
/// </summary>
/// <param name="MeanCoherence">The mean coherence across all clusters.</param>
/// <param name="LowEndTail">The lowest observed coherence score.</param>
/// <param name="MixedClusterCount">The number of mixed clusters.</param>
/// <param name="OversizedClusterIds">The clusters that exceed the 200-symbol warning threshold.</param>
public sealed record ClusterTuningSnapshot(
    double MeanCoherence,
    double LowEndTail,
    int MixedClusterCount,
    IReadOnlyList<Guid> OversizedClusterIds);
