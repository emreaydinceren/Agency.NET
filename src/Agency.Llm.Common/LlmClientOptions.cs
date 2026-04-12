namespace Agency.Llm.OpenAI;

/// <summary>
/// Options for configuring LlmClient client.
/// </summary>
public class LlmClientOptions
{
    /// <summary>
    /// Gets or sets the name associated with this instance.
    /// </summary>
    public string Name {  get; set; } = string.Empty;

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
}
