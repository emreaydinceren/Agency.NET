namespace Agency.Harness.Console.Configuration;

/// <summary>
/// Configures how documents are split into chunks before ingestion into the vector store.
/// </summary>
internal sealed class IngestionOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Gets the target size, in characters, for each text chunk.
    /// </summary>
    public int ChunkSize { get; init; } = 512;

    /// <summary>
    /// Gets the number of characters shared between consecutive chunks.
    /// </summary>
    public int ChunkOverlap { get; init; } = 64;

    /// <summary>
    /// Gets the glob pattern used to select files for ingestion.
    /// </summary>
    public string SearchPattern { get; init; } = "*.md";
}
