using Agency.Llm.OpenAI;

namespace Agency.Agentic;

public sealed class AgentOptions
{
    /// <summary>
    /// Name of the provider to use for this agent. Supported values are "OpenAI" and "Claude".
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model name or identifier.
    /// </summary>
    public string? Model { get; set; }

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

    /// <summary>
    /// Retrieves the configuration options for the currently selected language model provider. 
    /// </summary>
    /// <remarks>The method supports providers named "CLAUDE" and "OPENAI". The provider name comparison is
    /// case-insensitive.</remarks>
    /// <returns>The configuration options for the selected provider. The returned object corresponds to the provider specified
    /// by the Provider property.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the selected provider is not supported or if the configuration options for the selected provider are
    /// not found.</exception>
    public LlmClientOptions GetSelectedProviderOptions() => Provider.ToUpperInvariant() switch
    {
        "CLAUDE" => LLmClients.FirstOrDefault(o => o.Name.Equals("CLAUDE", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Claude options not configured."),
        "OPENAI" => LLmClients.FirstOrDefault(o => o.Name.Equals("OPENAI", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("OpenAI options not configured."),
        _ => throw new InvalidOperationException($"Unsupported provider '{Provider}'."),
    };
}
