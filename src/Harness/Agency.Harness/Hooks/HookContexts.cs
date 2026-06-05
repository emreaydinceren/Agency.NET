using System.Text.Json;
using Agency.Harness;
using Agency.Harness.Contexts;

namespace Agency.Harness.Hooks;

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

/// <summary>Passed to <c>OnSessionEnd</c> hooks when a ChatSession is disposed.</summary>
public sealed record SessionEndedHookContext(string SessionId, Context AgentContext);

/// <summary>Passed to <c>OnUserPromptSubmit</c> hooks.</summary>
internal sealed record UserPromptSubmitHookContext(string Prompt, Context AgentContext);

/// <summary>Passed to <c>OnPreIteration</c> hooks.</summary>
internal sealed record PreIterationHookContext(Context AgentContext);

/// <summary>Passed to <c>OnPostToolBatch</c> hooks.</summary>
internal sealed record PostToolBatchHookContext(IReadOnlyList<ToolInvokedEvent> Events, Context AgentContext);