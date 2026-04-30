namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Represents the generated summary payload for a symbol chunk.
/// </summary>
/// <param name="OneLine">The one-line purpose summary used for embedding.</param>
/// <param name="Detailed">The detailed summary used for retrieval context.</param>
/// <param name="ProbableCallees">The probable callees extracted from the detailed summary.</param>
/// <param name="OneLineEmbedding">The embedding generated from <paramref name="OneLine"/>.</param>
public sealed record SymbolSummary(
    string OneLine,
    string Detailed,
    IReadOnlyList<string> ProbableCallees,
    ReadOnlyMemory<float> OneLineEmbedding);
