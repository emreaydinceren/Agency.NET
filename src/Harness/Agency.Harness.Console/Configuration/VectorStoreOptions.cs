namespace Agency.Harness.Console.Configuration;

/// <summary>
/// Configures which backing store is used for vector search.
/// </summary>
internal sealed class VectorStoreOptions
{
    /// <summary>
    /// The configuration section name this options type binds to.
    /// </summary>
    public const string SectionName = "VectorStore";

    /// <summary>
    /// Gets the vector store provider to use. Valid values are <c>"sqlite"</c> and <c>"postgres"</c>.
    /// </summary>
    public string Provider { get; init; } = "sqlite";
}
