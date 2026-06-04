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
}
