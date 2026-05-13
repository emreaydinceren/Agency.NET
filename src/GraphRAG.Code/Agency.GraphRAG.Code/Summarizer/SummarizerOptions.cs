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

    /// <summary>
    /// Gets or sets the timeout in minutes for each LLM or embedding request.
    /// </summary>
    public int RequestTimeoutMinutes { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of characters of source content included in any prompt.
    /// Content exceeding this limit is truncated with a marker. Defaults to 8000.
    /// </summary>
    public int MaxContentChars { get; set; } = 8000;

    /// <summary>
    /// Gets or sets the maximum number of characters of each parent summary injected as context.
    /// Defaults to 500. Keeps sub-chunk prompts from inheriting an overwhelming parent narrative.
    /// </summary>
    public int MaxParentContextChars { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum number of output tokens per LLM response. Defaults to 2048.
    /// Acts as a hard cap to prevent runaway repetition loops on local models.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the frequency penalty passed to the LLM on every request.
    /// Penalizes tokens proportional to how often they have already appeared, reducing repetition loops.
    /// Set to <see langword="null"/> to omit the parameter (provider default). Defaults to 0.3.
    /// </summary>
    public float? FrequencyPenalty { get; set; } = 0.3f;

    /// <summary>
    /// Gets or sets the temperature passed to the LLM on every request.
    /// Higher values increase randomness and can help reduce repetition loops.
    /// Set to <see langword="null"/> to omit the parameter (provider default). Defaults to 0.5.
    /// </summary>
    public float? Temperature { get; set; } = 0.5f;
}
