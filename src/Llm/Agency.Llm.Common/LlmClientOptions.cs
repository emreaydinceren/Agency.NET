namespace Agency.Llm.Common;

/// <summary>
/// Options for configuring LlmClient client.
/// </summary>
public sealed record class LlmClientOptions
{
    /// <summary>
    /// Gets or sets the name associated with this instance.
    /// </summary>
    public string Name {  get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the client.
    /// </summary>
    public string ClientType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenAI API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The maximum number of times to retry failed requests. Defaults to null (uses SDK default).
    /// </summary>
    /// <summary>
    /// Gets or sets the retry count.
    /// </summary>
    public int? MaxRetries { get; set; } = null;

    /// <summary>
    /// Sets the maximum time allowed for a complete HTTP call, not including retries. Defaults to null (uses SDK
    /// default).
    /// </summary>
    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; } = null;

    /// <summary>
    /// When <see langword="true"/>, injects <c>enable_thinking: false</c> and
    /// <c>thinking_budget_tokens: 0</c> into every chat-completion request body.
    /// Use with reasoning-capable models (e.g. Qwen3) when extended thinking must be
    /// suppressed unconditionally regardless of prompt-level directives.
    /// </summary>
    public bool SuppressThinking { get; set; } = false;
}
