using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// A retrieved symbol, its retrieval score, and optional raw source.
/// </summary>
public sealed record QuerySymbolResult
{
    /// <summary>Gets the symbol.</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>Gets the relevance score.</summary>
    public required double Score { get; init; }

    /// <summary>Gets the traversal depth, where zero indicates a direct seed hit.</summary>
    public required int Depth { get; init; }

    /// <summary>Gets the raw source snippet if available.</summary>
    public string? RawCode { get; init; }
}
