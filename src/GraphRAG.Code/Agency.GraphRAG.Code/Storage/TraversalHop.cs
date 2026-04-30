using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Storage;

/// <summary>
/// Represents one hop in a graph traversal result, describing a symbol reached
/// and the edge that connected to it.
/// </summary>
public sealed record TraversalHop
{
    /// <summary>Gets the symbol ID at this hop.</summary>
    public required Guid SymbolId { get; init; }

    /// <summary>
    /// Gets the edge that connected to this hop, or <c>null</c> for seed nodes at depth 0.
    /// </summary>
    public Edge? ViaEdge { get; init; }

    /// <summary>Gets the depth from the seed node (0 = seed node itself).</summary>
    public required int Depth { get; init; }
}