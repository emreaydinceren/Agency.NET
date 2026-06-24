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
}
