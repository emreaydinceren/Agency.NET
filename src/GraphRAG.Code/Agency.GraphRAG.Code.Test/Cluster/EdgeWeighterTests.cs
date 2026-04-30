using Agency.GraphRAG.Code.Cluster;
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="EdgeWeighter"/> and <see cref="ClusterOptions"/>.
/// </summary>
public sealed class EdgeWeighterTests
{
    [Fact]
    public void ClusterOptions_Defaults_MatchSpec()
    {
        ClusterOptions options = new();

        Assert.Equal(ProjectBoundaryMode.Hard, options.ProjectBoundaryMode);
        Assert.Equal(1.5d, options.NamespaceWeightMultiplier, precision: 6);
        Assert.Equal(0.5d, options.InterProjectWeightMultiplier, precision: 6);
        Assert.Equal(99d, options.UtilityDegreePercentile, precision: 6);
        Assert.Equal(50, options.UtilityDegreeFloor);
        Assert.Equal(0.6d, options.UtilityClusterSpreadThreshold, precision: 6);
        Assert.Equal(UtilityAssignmentStrategy.Dedicated, options.UtilityAssignmentStrategy);
    }

    [Fact]
    public void Weight_IntraNamespaceReferenceEdge_AppliesBoost()
    {
        EdgeWeighter weighter = new(new ClusterOptions());

        double weight = weighter.Weight(
            CreateEdge(0.8d),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Core", "project-a"),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Core", "project-a"));

        Assert.Equal(1.2d, weight, precision: 6);
    }

    [Fact]
    public void Weight_InterProjectReferenceEdgeInSoftMode_AppliesPenalty()
    {
        EdgeWeighter weighter = new(new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Soft });

        double weight = weighter.Weight(
            CreateEdge(0.8d),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Core", "project-a"),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Other", "project-b"));

        Assert.Equal(0.4d, weight, precision: 6);
    }

    [Fact]
    public void Weight_DefaultsToConfidence_WhenNoBoundaryRuleApplies()
    {
        EdgeWeighter weighter = new(new ClusterOptions());

        double weight = weighter.Weight(
            CreateEdge(0.8d),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Core", "project-a"),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Other", "project-a"));

        Assert.Equal(0.8d, weight, precision: 6);
    }

    [Fact]
    public void Weight_ProjectBoundaryModeOff_DoesNotPenalizeInterProjectEdges()
    {
        EdgeWeighter weighter = new(new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Off });

        double weight = weighter.Weight(
            CreateEdge(0.8d),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Core", "project-a"),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Other", "project-b"));

        Assert.Equal(0.8d, weight, precision: 6);
    }

    [Fact]
    public void Weight_ProjectBoundaryModeHard_SuppressesInterProjectEdges()
    {
        EdgeWeighter weighter = new(new ClusterOptions { ProjectBoundaryMode = ProjectBoundaryMode.Hard });

        double weight = weighter.Weight(
            CreateEdge(0.8d),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Core", "project-a"),
            new ClusterNodeInfo(Guid.NewGuid(), "Payments.Other", "project-b"));

        Assert.Equal(0d, weight, precision: 6);
    }

    private static Edge CreateEdge(double confidence) =>
        new()
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "symbol",
            TargetId = Guid.NewGuid(),
            TargetKind = "symbol",
            EdgeKind = EdgeKind.References,
            Confidence = confidence,
            Signals = [],
            Properties = new Dictionary<string, object?>(),
        };
}
