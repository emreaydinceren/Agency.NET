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
    /// Gets or sets the maximum duration, in seconds, to wait for a turn to complete before timing out.
    /// </summary>
    public int? TurnTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the collection of options for configuring large language model clients.
    /// </summary>
    public LlmClientOptions[] LLmClients { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum context window size in tokens for the active model.
    /// When set, the agent injects a context budget hint into the system prompt each turn
    /// so the model can avoid generating output that would exceed the window.
    /// </summary>
    public int? ContextWindowSize { get; set; }
}
