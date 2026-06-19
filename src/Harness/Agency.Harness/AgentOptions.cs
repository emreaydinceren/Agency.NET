using Agency.Harness.Hooks;

namespace Agency.Harness;

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

    /// <summary>
    /// When true, tools are advertised to the model with their parameter schemas withheld
    /// (replaced by a minimal placeholder) plus a <c>tool_help</c> meta-tool that reveals a
    /// tool's full schema on demand. Reduces context size; tool-call behavior is unchanged.
    /// Default false.
    /// </summary>
    public bool ProgressiveDiscovery { get; set; }

    /// <summary>
    /// When true, the agent logs each tool call's full input arguments and a tool's error result
    /// content. This is verbose and may include sensitive data (file contents, commands, user ids),
    /// so it is opt-in and intended for on-demand debugging. When false (default), tool calls and
    /// failures are still logged by name, but their payloads are redacted.
    /// </summary>
    public bool LogToolPayloads { get; set; }

    /// <summary>
    /// Gets or sets the host-supplied user identity used for memory partitioning.
    /// When set, this value is propagated into <see cref="Contexts.UserSpecificContext.Id"/>
    /// so retrieved and distilled records are scoped to the correct user.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Gets or sets the baseline hooks built by the memory pipeline (e.g. retrieval, timer restart).
    /// Set by <c>AddAgencyMemory</c> via <c>PostConfigure</c>; null when memory is disabled.
    /// Baseline hooks always run <b>before</b> <see cref="UserHooks"/> per spec §6.5.
    /// </summary>
    public AgentHooks? BaselineHooks { get; set; }

    /// <summary>
    /// Gets or sets user-supplied hooks to compose <b>after</b> the baseline hooks.
    /// When null, only <see cref="BaselineHooks"/> are used (if present).
    /// Advanced callers who need to observe pre-retrieval context should use
    /// <see cref="AgentHooksExtensions.ComposeBefore"/> directly rather than this property.
    /// </summary>
    public AgentHooks? UserHooks { get; set; }

    /// <summary>Operator config hooks, composed between Baseline and User per §14.5.</summary>
    public AgentHooks? ConfiguredHooks { get; set; }
}
