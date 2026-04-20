using System.Text.Json;

namespace Agency.Agentic;

/// <summary>Base type for all events emitted by <see cref="Agent.RunAsync"/>.</summary>
public abstract record AgentEvent;

/// <summary>Emitted once at the very start of a session, before any LLM calls.</summary>
public sealed record SessionStartedEvent(string SessionId) : AgentEvent;

/// <summary>Emitted after each LLM response is appended to the conversation.</summary>
public sealed record AssistantTurnEvent(ChatMessage Message) : AgentEvent;

/// <summary>Emitted after a tool has been invoked and its result is ready.</summary>
public sealed record ToolInvokedEvent(
    string ToolName,
    JsonElement Input,
    ToolResult Result) : AgentEvent;

/// <summary>Emitted after each complete iteration (LLM call + optional tool calls).</summary>
public sealed record IterationCompletedEvent(
    int Iteration,
    LlmTokenUsage TurnUsage) : AgentEvent;

/// <summary>Emitted as a streaming text token arrives from the LLM (streaming path only).</summary>
public sealed record TextDeltaEvent(string Delta) : AgentEvent;

/// <summary>
/// Emitted when a complete tool call is received from the stream,
/// before execution begins. Gives UIs a chance to render "calling tool X…".
/// </summary>
public sealed record ToolUseReceivedEvent(string ToolName, string ToolUseId) : AgentEvent;

/// <summary>Terminal event — always the last event emitted by <see cref="Agent.RunAsync"/>.</summary>
public sealed record AgentResultEvent(
    AgentResultStatus Status,
    string? FinalText,
    LlmTokenUsage TotalUsage,
    decimal TotalCostUsd) : AgentEvent;

/// <summary>Describes why the agent loop terminated.</summary>
public enum AgentResultStatus
{
    /// <summary>The loop finished because no more tool calls were requested.</summary>
    Success,

    /// <summary>The loop was stopped by <see cref="StopConditions.StepCountIs"/>.</summary>
    MaxStepsReached,

    /// <summary>The loop was stopped by <see cref="StopConditions.BudgetExceeded"/>.</summary>
    BudgetExceeded,

    /// <summary>An unrecoverable error occurred.</summary>
    Error,
}

/// <summary>Accumulated token usage for a session or turn.</summary>
public sealed record LlmTokenUsage(long InputTokens, long OutputTokens)
{
    /// <summary>Gets the total token count.</summary>
    public long TotalTokens => this.InputTokens + this.OutputTokens;
}
