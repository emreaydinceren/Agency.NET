namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Describes the structural level represented by a chunk.
/// </summary>
public enum ChunkGranularity
{
    /// <summary>
    /// The chunk represents a namespace or module scope.
    /// </summary>
    Namespace = 0,

    /// <summary>
    /// The chunk represents a type declaration.
    /// </summary>
    Type,

    /// <summary>
    /// The chunk represents a member such as a method, function, property, or field.
    /// </summary>
    Member,

    /// <summary>
    /// The chunk represents a statement-level subdivision of a member.
    /// </summary>
    Statement,
}
