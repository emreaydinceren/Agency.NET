namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Configures model names for summarization tiers.
/// </summary>
public sealed class SummarizerOptions
{
    /// <summary>
    /// Gets or sets the model used for interfaces and abstract symbols.
    /// </summary>
    public string StrongModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model used for non-leaf detailed summaries.
    /// </summary>
    public string StandardModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model used for leaf detailed summaries.
    /// </summary>
    public string CheapModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model used for one-line summaries.
    /// </summary>
    public string CheapestModel { get; set; } = string.Empty;
}
