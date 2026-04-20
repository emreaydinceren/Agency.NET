namespace Agency.Agentic;

public sealed class AgentOptions
{
    /// <summary>
    /// Name of the client to use for this agent
    /// </summary>
    public string DefaultClientName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model name or identifier.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use streaming mode for the agent loop. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool Stream { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum duration, in seconds, to wait for a turn to complete before timing out.
    /// </summary>
    public int? TurnTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the collection of options for configuring large language model clients.
    /// </summary>
    public LlmClientOptions[] LLmClients { get; set; } = [];
}
