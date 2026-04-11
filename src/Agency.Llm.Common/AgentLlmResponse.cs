using Agency.Llm.Common.Messages;

namespace Agency.Llm.Common;

/// <summary>
/// The structured response returned by <see cref="ILlmClient.SendAgentAsync"/>.
/// Carries the assembled assistant message, the stop reason, and per-turn token usage.
/// </summary>
public sealed record AgentLlmResponse(
    AgentMessage Message,
    StopReason StopReason,
    LlmTokenUsage Usage);
