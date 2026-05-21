using System.Text.Json;
using Agency.Agentic.Contexts;

namespace Agency.Agentic.Hooks;

/// <summary>Passed to <c>OnSessionStarted</c> hooks.</summary>
public sealed record SessionStartedHookContext(string SessionId, Context AgentContext);

/// <summary>Passed to <c>OnPreToolUse</c> hooks.</summary>
public sealed record PreToolUseHookContext(
    string ToolName,
    JsonElement Input,
    Context AgentContext);

/// <summary>Passed to <c>OnPostToolUse</c> hooks.</summary>
public sealed record PostToolUseHookContext(
    string ToolName,
    JsonElement Input,
    ToolResult Result,
    Context AgentContext);

/// <summary>Passed to <c>OnAssistantTurn</c> hooks.</summary>
public sealed record AssistantTurnHookContext(ChatMessage Message, Context AgentContext);

/// <summary>Passed to <c>OnStop</c> hooks.</summary>
public sealed record StopHookContext(AgentResultEvent Result, Context AgentContext);