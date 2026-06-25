using System.Text.Json;

using Agency.Harness.Loop;
using Agency.Harness.Permissions;

namespace Agency.Harness;

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
    LlmTokenUsage TurnUsage,
    TimeSpan LlmDuration) : AgentEvent;

/// <summary>Emitted for each tool call that needs user permission. The turn ends with
/// <see cref="AgentResultStatus.AwaitingPermission"/> after all such events are yielded.</summary>
/// <param name="RequestId">Unique identifier for this permission request; must be echoed back in <see cref="PermissionResponse"/>.</param>
/// <param name="ToolName">The name of the tool whose invocation requires approval.</param>
/// <param name="Input">The (post-rewrite) input the tool would receive on approval.</param>
/// <param name="KeyValue">Extracted key field (command/path) for concise display; <see langword="null"/> when none.</param>
/// <param name="ProposedRule">Rule persisted to the local rules file if the answer is an "always", e.g. <c>ExecutePowershell(git status)</c>.</param>
/// <param name="Source">Why this call is asking: no rule matched, or a hook escalated.</param>
/// <param name="Reason">Hook-supplied reason when <paramref name="Source"/> is <see cref="PermissionRequestSource.Hook"/>; <see langword="null"/> otherwise.</param>
public sealed record PermissionRequestedEvent(
    Guid RequestId,
    string ToolName,
    JsonElement Input,
    string? KeyValue,
    string ProposedRule,
    PermissionRequestSource Source,
    string? Reason) : AgentEvent;

/// <summary>Why a tool call is asking: no rule matched, or a hook escalated.</summary>
public enum PermissionRequestSource
{
    /// <summary>No configured allow or deny rule matched this tool call.</summary>
    UnresolvedRule,

    /// <summary>A hook returned an escalation decision requiring user confirmation.</summary>
    Hook,
}

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

    /// <summary>The turn is parked: one or more tool calls await user permission.
    /// Answer via <see cref="ChatSession.ResumeWithPermissionsAsync"/>.</summary>
    AwaitingPermission,
}

/// <summary>Accumulated token usage for a session or turn.</summary>
public sealed record LlmTokenUsage(long InputTokens, long OutputTokens)
{
    /// <summary>Gets the total token count.</summary>
    public long TotalTokens => this.InputTokens + this.OutputTokens;
}

// ── Loop Kit event subtypes (§7.2) ────────────────────────────────────────────

/// <summary>Emitted the first time a <see cref="GoalSpec"/> is observed by the <c>LoopRunner</c>.</summary>
public sealed record GoalSetEvent(GoalSpec Goal) : AgentEvent;

/// <summary>Emitted at the start of each loop turn, before <c>ChatSession.SendAsync</c> is called.</summary>
public sealed record TurnStartedEvent(int TurnIndex, string Directive) : AgentEvent;

/// <summary>Emitted after the Goalkeeper evaluates a completed turn.</summary>
public sealed record VerdictEvent(int TurnIndex, Verdict Verdict) : AgentEvent;

/// <summary>
/// Terminal event for a loop run — always the last event emitted by <c>LoopRunner.RunAsync</c>.
/// Analogous to <see cref="AgentResultEvent"/> for the outer loop.
/// </summary>
public sealed record LoopResultEvent(
    LoopOutcome Outcome,
    string? FinalText,
    LlmTokenUsage TotalUsage,
    decimal TotalCostUsd) : AgentEvent;
