
using Agency.VectorStore.Common;

namespace Agency.Ingestion;
/// <summary>
/// Orchestrates the full load → split → store ingestion flow.
/// </summary>
public interface IIngestionPipeline<TValue>
{
    /// <summary>
    /// Runs the pipeline end-to-end and returns an aggregated result.
    /// </summary>
    /// <param name="loader">The document loader that provides source documents.</param>
    /// <param name="splitter">The text splitter that chunks documents into smaller pieces.</param>
    /// <param name="store">The vector store for persisting embeddings and metadata.</param>
    /// <param name="userId">The user ID associated with this ingestion operation.</param>
    /// <param name="sessionId">The optional session ID for grouping related ingestions.</param>
    /// <param name="projectId">The optional project ID for scoping related ingestions.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<IngestionResult> ExecuteAsync(
        IDocumentLoader loader,
        ITextSplitter splitter,
        IVectorStore store,
        string userId,
        string? sessionId,
        string? projectId = null,
        CancellationToken ct = default);
}
