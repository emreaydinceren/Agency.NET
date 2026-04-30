namespace Agency.GraphRAG.Code.Storage;

/// <summary>
/// Specifies the direction of edges to follow during a graph traversal.
/// </summary>
public enum TraversalDirection
{
    /// <summary>Follow only outgoing edges (source → target).</summary>
    Outgoing,

    /// <summary>Follow only incoming edges (target → source).</summary>
    Incoming,

    /// <summary>Follow edges in both directions.</summary>
    Both,
}