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
    public int? MaxRetries { get; set; }

    /// <summary>
    /// Sets the maximum time allowed for a complete HTTP call, not including retries. Defaults to null (uses SDK
    /// default).
    /// </summary>
    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// When <see langword="true"/>, injects <c>enable_thinking: false</c> and
    /// <c>thinking_budget_tokens: 0</c> into every chat-completion request body.
    /// Use with reasoning-capable models (e.g. Qwen3) when extended thinking must be
    /// suppressed unconditionally regardless of prompt-level directives. Takes precedence
    /// over <see cref="EnableThinking"/> and <see cref="ThinkingBudgetTokens"/> when set.
    /// </summary>
    public bool SuppressThinking { get; set; }

    /// <summary>
    /// When set, injects <c>enable_thinking: &lt;value&gt;</c> into every OpenAI-compatible
    /// chat-completion request body. <see langword="null"/> means unspecified: nothing is
    /// injected and the provider's own default applies. Ignored (and overridden by
    /// <see langword="false"/>) when <see cref="SuppressThinking"/> is <see langword="true"/>.
    /// </summary>
    public bool? EnableThinking { get; set; }

    /// <summary>
    /// When set, bounds the model's thinking/reasoning tokens: injected as
    /// <c>thinking_budget_tokens</c> for OpenAI-compatible clients, or as
    /// <c>thinking.budget_tokens</c> for Claude. <see langword="null"/> means unspecified:
    /// nothing is injected and the provider's own default applies. Ignored (and overridden
    /// by <c>0</c>) when <see cref="SuppressThinking"/> is <see langword="true"/>.
    /// </summary>
    public int? ThinkingBudgetTokens { get; set; }
}
