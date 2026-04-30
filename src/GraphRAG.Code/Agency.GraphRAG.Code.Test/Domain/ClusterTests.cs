using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Domain;

/// <summary>
/// Tests for <see cref="Cluster"/> and <see cref="ClusterType"/>.
/// </summary>
public sealed class ClusterTests
{
    [Fact]
    public void ClusterType_ContainsExpectedValues()
    {
        var values = Enum.GetValues<ClusterType>();

        Assert.Contains(ClusterType.Business, values);
        Assert.Contains(ClusterType.Infrastructure, values);
        Assert.Contains(ClusterType.Mixed, values);
    }

    [Fact]
    public void Cluster_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var embedding = new float[] { 0.1f, 0.5f, 0.9f };

        var cluster = new Agency.GraphRAG.Code.Domain.Cluster
        {
            Id = id,
            Label = "Payment Processing",
            Type = ClusterType.Business,
            CoherenceScore = 0.92,
            Summary = "Handles all payment flows.",
            Embedding = embedding,
        };

        Assert.Equal(id, cluster.Id);
        Assert.Equal("Payment Processing", cluster.Label);
        Assert.Equal(ClusterType.Business, cluster.Type);
        Assert.Equal(0.92, cluster.CoherenceScore);
        Assert.Equal("Handles all payment flows.", cluster.Summary);
        Assert.Equal(embedding, cluster.Embedding);
    }

    [Fact]
    public void Cluster_CoherenceScore_IsDouble()
    {
        var cluster = new Agency.GraphRAG.Code.Domain.Cluster
        {
            Id = Guid.NewGuid(),
            Label = "Infra",
            Type = ClusterType.Infrastructure,
            CoherenceScore = 0.5,
        };

        Assert.IsType<double>(cluster.CoherenceScore);
    }

    [Fact]
    public void Cluster_NullSummaryAndEmbedding_IsValid()
    {
        var cluster = new Agency.GraphRAG.Code.Domain.Cluster
        {
            Id = Guid.NewGuid(),
            Label = "Mixed Bag",
            Type = ClusterType.Mixed,
            CoherenceScore = 0.3,
            Summary = null,
            Embedding = null,
        };

        Assert.Null(cluster.Summary);
        Assert.Null(cluster.Embedding);
    }

    [Fact]
    public void Cluster_WithExpression_MutatesType()
    {
        var original = new Agency.GraphRAG.Code.Domain.Cluster
        {
            Id = Guid.NewGuid(),
            Label = "Core",
            Type = ClusterType.Business,
            CoherenceScore = 0.8,
        };

        var mutated = original with { Type = ClusterType.Mixed };

        Assert.Equal(ClusterType.Mixed, mutated.Type);
        Assert.Equal(original.Id, mutated.Id);
        Assert.Equal(original.Label, mutated.Label);
    }
}
