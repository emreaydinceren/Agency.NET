namespace Agency.Harness.Console.Configuration;

/// <summary>
/// Configures how many results are returned from vector store similarity searches.
/// </summary>
internal sealed class RetrievalOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "Retrieval";

    /// <summary>
    /// Gets the maximum number of results to return from a similarity search.
    /// </summary>
    public int TopK { get; init; } = 5;
}
