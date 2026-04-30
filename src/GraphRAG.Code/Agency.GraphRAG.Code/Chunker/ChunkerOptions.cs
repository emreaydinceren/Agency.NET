namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Configures chunking behavior.
/// </summary>
public sealed class ChunkerOptions
{
    /// <summary>
    /// Gets or sets the maximum raw-text length for a non-statement chunk before statement fallback is used.
    /// </summary>
    public int MaxChunkChars { get; set; } = 6000;
}
