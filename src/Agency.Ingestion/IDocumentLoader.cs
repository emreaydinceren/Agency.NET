namespace Agency.Ingestion;

/// <summary>
/// Streams documents from an underlying source.
/// </summary>
public interface IDocumentLoader
{
    /// <summary>
    /// Yields documents asynchronously. Implementations must not buffer all documents in memory.
    /// </summary>
    IAsyncEnumerable<Document> LoadAsync(CancellationToken ct = default);
}
