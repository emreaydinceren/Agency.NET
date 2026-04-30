using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Storage;

/// <summary>
/// Parameters that control a graph traversal starting from one or more seed symbols.
/// </summary>
public sealed record TraversalRequest
{
    /// <summary>Gets the seed symbol IDs to start the traversal from.</summary>
    public required IReadOnlyList<Guid> SeedSymbolIds { get; init; }

    /// <summary>
    /// Gets the edge kinds to follow during traversal.
    /// An empty list means all edge kinds are followed.
    /// </summary>
    public IReadOnlyList<EdgeKind> EdgeKinds { get; init; } = [];

    /// <summary>Gets the maximum number of hops to follow from each seed (1–6).</summary>
    public int MaxHops { get; init; } = 2;

    /// <summary>Gets the minimum confidence threshold for edges to follow.</summary>
    public double MinConfidence { get; init; } = 0.0;

    /// <summary>Gets the direction of edges to follow during traversal.</summary>
    public TraversalDirection Direction { get; init; } = TraversalDirection.Outgoing;
}