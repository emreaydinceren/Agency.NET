using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.References;

namespace Agency.GraphRAG.Code.Test.References;

/// <summary>
/// Tests for <see cref="ReferenceScorer"/>.
/// </summary>
public sealed class ReferenceScorerTests
{
    private readonly ReferenceScorer scorer = new(new ExternalPackageHeuristic());

    [Fact]
    public void Score_NameMatchOnly_ReturnsMidConfidence()
    {
        IReadOnlyList<ResolutionResult> results = scorer.Score(
            identifier: "ChargeAsync",
            candidateSymbols: [CreateSymbol("Payments.Service.ChargeAsync")],
            externalPackages: []);

        ResolutionResult result = Assert.Single(results);
        Assert.Equal(0.60d, result.Confidence);
        Assert.Equal([Signal.NameMatch], result.Signals);
        Assert.NotNull(result.TargetSymbolId);
    }

    [Fact]
    public void Score_NameMatchAndLlmExtraction_ReturnsHighConfidenceForMatchedTarget()
    {
        Symbol target = CreateSymbol("Payments.Service.ChargeAsync");

        IReadOnlyList<ResolutionResult> results = scorer.Score(
            identifier: "ChargeAsync",
            candidateSymbols: [target],
            externalPackages: [],
            llmExtractedTarget: "Payments.Service.ChargeAsync");

        ResolutionResult result = Assert.Single(results);
        Assert.Equal(0.90d, result.Confidence);
        Assert.Equal([Signal.NameMatch, Signal.LlmExtraction], result.Signals);
        Assert.Equal(target.Id, result.TargetSymbolId);
    }

    [Fact]
    public void Score_LlmExtractionOnly_WithTraceableExternalPackage_ReturnsExternalLikely()
    {
        IReadOnlyList<ResolutionResult> results = scorer.Score(
            identifier: "JToken",
            candidateSymbols: [],
            externalPackages: [CreatePackage("Newtonsoft.Json")],
            llmExtractedTarget: "Newtonsoft.Json.Linq.JToken");

        ResolutionResult result = Assert.Single(results);
        Assert.Equal(0.75d, result.Confidence);
        Assert.Equal([Signal.LlmExtraction, Signal.ExternalLikely], result.Signals);
        Assert.Equal("Newtonsoft.Json", result.ExternalPackageName);
        Assert.Null(result.TargetSymbolId);
    }

    [Fact]
    public void Score_LlmExtractionOnly_WithoutTraceablePackage_ReturnsUnresolved()
    {
        IReadOnlyList<ResolutionResult> results = scorer.Score(
            identifier: "JToken",
            candidateSymbols: [],
            externalPackages: [],
            llmExtractedTarget: "Unknown.Library.JToken");

        ResolutionResult result = Assert.Single(results);
        Assert.Equal(0.20d, result.Confidence);
        Assert.Equal([Signal.LlmExtraction, Signal.Unresolved], result.Signals);
    }

    [Fact]
    public void Score_MultipleNameMatches_SplitsLowConfidenceEdges()
    {
        IReadOnlyList<ResolutionResult> results = scorer.Score(
            identifier: "Execute",
            candidateSymbols:
            [
                CreateSymbol("Payments.A.Execute"),
                CreateSymbol("Payments.B.Execute"),
            ],
            externalPackages: []);

        Assert.Equal(2, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal(0.35d, result.Confidence);
            Assert.Equal([Signal.NameMatch], result.Signals);
        });
    }

    private static Symbol CreateSymbol(string fullyQualifiedName) =>
        new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            ModuleId = null,
            Name = fullyQualifiedName.Split('.').Last(),
            FullyQualifiedName = fullyQualifiedName,
            Kind = SymbolKind.Method,
            Signature = null,
            Summary = null,
            OneLineSummary = null,
            ContentHash = null,
            Embedding = null,
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 1,
        };

    private static ExternalPackage CreatePackage(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = name,
            Version = "1.0.0",
            Ecosystem = "nuget",
            Scope = "runtime",
        };
}
