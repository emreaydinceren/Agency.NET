namespace Agency.Ingestion;

/// <summary>
/// Splits a single document into a sequence of smaller chunks.
/// </summary>
public interface ITextSplitter
{
    /// <summary>
    /// Produces zero or more chunk documents from <paramref name="document"/>.
    /// </summary>
    IEnumerable<Document> Split(Document document);
}
