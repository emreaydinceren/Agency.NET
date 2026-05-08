namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// The outcome of a <see cref="SymbolSummarizer.SummarizeAsync"/> run.
/// </summary>
/// <param name="Summaries">Summaries that were successfully generated, keyed by chunk identifier.</param>
/// <param name="FailedChunkIds">Identifiers of chunks that could not be summarized after all retry attempts.</param>
public sealed record SummarizationResult(
    IReadOnlyDictionary<string, SymbolSummary> Summaries,
    IReadOnlyList<string> FailedChunkIds);
