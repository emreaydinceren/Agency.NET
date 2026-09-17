namespace Agency.Harness;

/// <summary>
/// Constructs <see cref="Agent"/> instances from the configured LLM clients and
/// <see cref="AgentOptions"/>, applying the host-independent assembly policy
/// (client/model defaulting and baseline/configured/user hook folding).
/// </summary>
public interface IAgentFactory
{
    /// <summary>
    /// Creates an <see cref="Agent"/> for the given client and model, falling back to
    /// <see cref="AgentOptions.DefaultClientName"/> and <see cref="AgentOptions.DefaultModel"/>
    /// when either argument is null or empty.
    /// </summary>
    Agent CreateAgent(string? clientName, string? modelName);

    /// <summary>
    /// Creates an <see cref="Agent"/> exactly as <see cref="CreateAgent(string?, string?)"/> does,
    /// except the resolved client's <see cref="LlmClientOptions"/> is passed through
    /// <paramref name="configureClientOptions"/> (typically a <c>with { ... }</c> expression) before
    /// its <see cref="IChatClient"/> is built. This is the seam for per-client effort (spec §6.7):
    /// each distinct effort level is a distinct client built from varied options, never a
    /// per-request setting.
    /// </summary>
    /// <param name="clientName">The LLM client configuration name; falls back to <see cref="AgentOptions.DefaultClientName"/> when null or empty.</param>
    /// <param name="modelName">The model identifier; falls back to <see cref="AgentOptions.DefaultModel"/> when null or empty.</param>
    /// <param name="configureClientOptions">
    /// Optional transform applied to the resolved <see cref="LlmClientOptions"/> before building its
    /// client. <see langword="null"/> (the default implementation) leaves the resolved options
    /// unchanged and delegates to <see cref="CreateAgent(string?, string?)"/> — implementers that
    /// have no notion of per-client options (e.g. a test double) need not override this member.
    /// </param>
    Agent CreateAgent(string? clientName, string? modelName, Func<LlmClientOptions, LlmClientOptions>? configureClientOptions) =>
        this.CreateAgent(clientName, modelName);
}
