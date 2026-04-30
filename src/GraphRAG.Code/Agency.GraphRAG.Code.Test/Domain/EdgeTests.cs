using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Domain;

/// <summary>
/// Tests for <see cref="Edge"/>, <see cref="EdgeKind"/>, and <see cref="Signal"/>.
/// </summary>
public sealed class EdgeTests
{
    [Fact]
    public void EdgeKind_ContainsExpectedValues()
    {
        var values = Enum.GetValues<EdgeKind>();

        Assert.Contains(EdgeKind.Contains, values);
        Assert.Contains(EdgeKind.DependsOn, values);
        Assert.Contains(EdgeKind.Imports, values);
        Assert.Contains(EdgeKind.References, values);
        Assert.Contains(EdgeKind.Defines, values);
        Assert.Contains(EdgeKind.MemberOf, values);
    }

    [Fact]
    public void Signal_ContainsExpectedValues()
    {
        var values = Enum.GetValues<Signal>();

        Assert.Contains(Signal.NameMatch, values);
        Assert.Contains(Signal.LlmExtraction, values);
        Assert.Contains(Signal.ExternalLikely, values);
        Assert.Contains(Signal.Unresolved, values);
    }

    [Fact]
    public void Edge_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        IReadOnlyList<Signal> signals = [Signal.NameMatch, Signal.LlmExtraction];
        IReadOnlyDictionary<string, object?> props = new Dictionary<string, object?> { ["weight"] = 0.9 };

        var edge = new Edge
        {
            Id = id,
            SourceId = sourceId,
            SourceKind = "symbol",
            TargetId = targetId,
            TargetKind = "symbol",
            EdgeKind = EdgeKind.References,
            Confidence = 0.85,
            Signals = signals,
            Properties = props,
        };

        Assert.Equal(id, edge.Id);
        Assert.Equal(sourceId, edge.SourceId);
        Assert.Equal("symbol", edge.SourceKind);
        Assert.Equal(targetId, edge.TargetId);
        Assert.Equal("symbol", edge.TargetKind);
        Assert.Equal(EdgeKind.References, edge.EdgeKind);
        Assert.Equal(0.85, edge.Confidence);
        Assert.Equal(signals, edge.Signals);
        Assert.Equal(props, edge.Properties);
    }

    [Fact]
    public void Edge_Confidence_IsDouble()
    {
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "file",
            TargetId = Guid.NewGuid(),
            TargetKind = "file",
            EdgeKind = EdgeKind.Imports,
            Confidence = 1.0,
            Signals = [],
            Properties = new Dictionary<string, object?>(),
        };

        Assert.Equal(1.0, edge.Confidence);
        Assert.IsType<double>(edge.Confidence);
    }

    [Fact]
    public void Edge_SignalsAsIReadOnlyList()
    {
        IReadOnlyList<Signal> signals = [Signal.Unresolved];
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "symbol",
            TargetId = Guid.NewGuid(),
            TargetKind = "module",
            EdgeKind = EdgeKind.MemberOf,
            Confidence = 0.5,
            Signals = signals,
            Properties = new Dictionary<string, object?>(),
        };

        Assert.IsAssignableFrom<IReadOnlyList<Signal>>(edge.Signals);
    }

    [Fact]
    public void Edge_PropertiesAsIReadOnlyDictionary()
    {
        IReadOnlyDictionary<string, object?> props = new Dictionary<string, object?> { ["key"] = "value" };
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "symbol",
            TargetId = Guid.NewGuid(),
            TargetKind = "symbol",
            EdgeKind = EdgeKind.DependsOn,
            Confidence = 0.75,
            Signals = [],
            Properties = props,
        };

        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(edge.Properties);
    }

    [Fact]
    public void Edge_EmptySignalsAndProperties_IsValid()
    {
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "file",
            TargetId = Guid.NewGuid(),
            TargetKind = "project",
            EdgeKind = EdgeKind.Contains,
            Confidence = 1.0,
            Signals = [],
            Properties = new Dictionary<string, object?>(),
        };

        Assert.Empty(edge.Signals);
        Assert.Empty(edge.Properties);
    }

    [Fact]
    public void Edge_MemberOfKind_KindPropertyAccessorReturnsValue()
    {
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "symbol",
            TargetId = Guid.NewGuid(),
            TargetKind = "module",
            EdgeKind = EdgeKind.MemberOf,
            Confidence = 1.0,
            Signals = [],
            Properties = new Dictionary<string, object?> { ["kind"] = "primary" },
        };

        Assert.Equal("primary", edge.MemberKind);
    }

    [Fact]
    public void Edge_MemberOfKind_KindPropertyAccessorReturnsNullWhenAbsent()
    {
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            SourceKind = "symbol",
            TargetId = Guid.NewGuid(),
            TargetKind = "module",
            EdgeKind = EdgeKind.MemberOf,
            Confidence = 1.0,
            Signals = [],
            Properties = new Dictionary<string, object?>(),
        };

        Assert.Null(edge.MemberKind);
    }
}
