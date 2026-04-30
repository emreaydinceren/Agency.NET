using Agency.GraphRAG.Code.Cluster;
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="ClusterTuningInstrumentation"/>.
/// </summary>
public sealed class ClusterTuningInstrumentationTests
{
    [Fact]
    public void Evaluate_ComputesCoherenceDistributionMixedCountAndOversizedFlags()
    {
        ClusterTuningInstrumentation instrumentation = new();
        Guid oversizedId = Guid.NewGuid();
        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters =
        [
            new() { Id = Guid.NewGuid(), Label = "Auth", Type = ClusterType.Business, CoherenceScore = 0.9d },
            new() { Id = oversizedId, Label = "Shared", Type = ClusterType.Mixed, CoherenceScore = 0.3d },
            new() { Id = Guid.NewGuid(), Label = "Orders", Type = ClusterType.Infrastructure, CoherenceScore = 0.6d },
        ];
        IReadOnlyDictionary<Guid, int> sizes = new Dictionary<Guid, int>
        {
            [clusters[0].Id] = 120,
            [oversizedId] = 250,
            [clusters[2].Id] = 80,
        };

        ClusterTuningSnapshot snapshot = instrumentation.Evaluate(clusters, sizes);

        Assert.Equal(0.6d, snapshot.MeanCoherence, precision: 6);
        Assert.Equal(0.3d, snapshot.LowEndTail, precision: 6);
        Assert.Equal(1, snapshot.MixedClusterCount);
        Assert.Equal([oversizedId], snapshot.OversizedClusterIds);
    }
}
