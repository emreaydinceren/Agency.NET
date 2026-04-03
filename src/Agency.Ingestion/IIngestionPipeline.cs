namespace Agency.Ingestion;

using Agency.VectorStore.Common;

/// <summary>
/// Orchestrates the full load → split → store ingestion flow.
/// </summary>
public interface IIngestionPipeline<TValue>
{
    /// <summary>
    /// Runs the pipeline end-to-end and returns an aggregated result.
    /// </summary>
    Task<IngestionResult> ExecuteAsync(
        IDocumentLoader loader,
        ITextSplitter splitter,
        IKVStore store,
        CancellationToken ct = default);
}
