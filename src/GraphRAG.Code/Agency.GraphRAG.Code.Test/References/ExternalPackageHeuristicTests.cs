using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.References;

namespace Agency.GraphRAG.Code.Test.References;

/// <summary>
/// Tests for <see cref="ExternalPackageHeuristic"/>.
/// </summary>
public sealed class ExternalPackageHeuristicTests
{
    [Fact]
    public void MatchPackage_ReturnsLongestMatchingPrefix()
    {
        ExternalPackageHeuristic heuristic = new();
        IReadOnlyList<ExternalPackage> packages =
        [
            CreatePackage("Newtonsoft"),
            CreatePackage("Newtonsoft.Json"),
        ];

        string? match = heuristic.MatchPackage("Newtonsoft.Json.Linq.JToken", packages);

        Assert.Equal("Newtonsoft.Json", match);
    }

    [Fact]
    public void MatchPackage_ReturnsNull_WhenNoPackageMatches()
    {
        ExternalPackageHeuristic heuristic = new();

        string? match = heuristic.MatchPackage("Contoso.Payments.Handler", [CreatePackage("Serilog")]);

        Assert.Null(match);
    }

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
