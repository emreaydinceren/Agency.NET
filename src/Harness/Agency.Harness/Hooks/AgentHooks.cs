using Agency.Harness.Contexts;

namespace Agency.Harness.Hooks;

/// <summary>Lifecycle hook delegates for an <see cref="Agency.Harness.Agent"/> run.</summary>
public sealed record AgentHooks
{
    /// <summary>Fires once before the first iteration of the agent loop.</summary>
    public Func<SessionStartedHookContext, CancellationToken, Task>? OnSessionStarted { get; init; }

    /// <summary>
    /// Fires every time <c>ChatAsync</c> is called (i.e., on every user turn), before the agent loop starts.
    /// Receives the raw user message and the current <see cref="Context"/>.
    /// </summary>
    public Func<Context, CancellationToken, Task>? OnUserPromptSubmit { get; init; }

    /// <summary>
    /// Fires at the start of every agent loop iteration, before the system prompt is rebuilt.
    /// Intended for retrieval-engine injection (mutate <see cref="Context.Knowledge"/> and
    /// <see cref="Context.Memory"/> here).
    /// </summary>
    public Func<Context, CancellationToken, Task>? OnPreIteration { get; init; }

    /// <summary>
    /// Fires before each tool invocation. Return a <see cref="PreToolUseDecision"/>
    /// to allow, block, or rewrite the call.
    /// </summary>
    public Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? OnPreToolUse { get; init; }

    /// <summary>Fires after each tool invocation, whether it succeeded or errored.</summary>
    public Func<PostToolUseHookContext, CancellationToken, Task>? OnPostToolUse { get; init; }

    /// <summary>
    /// Fires after <c>Task.WhenAll</c> of all parallel tool calls completes,
    /// before the next LLM call. Receives all tool events from this batch.
    /// </summary>
    public Func<IReadOnlyList<ToolInvokedEvent>, Context, CancellationToken, Task>? OnPostToolBatch { get; init; }

    /// <summary>Fires after the LLM emits a response and it has been appended to the conversation.</summary>
    public Func<AssistantTurnHookContext, CancellationToken, Task>? OnAssistantTurn { get; init; }

    /// <summary>Fires just before the agent emits <see cref="Agency.Harness.AgentResultEvent"/> and stops.</summary>
    public Func<StopHookContext, CancellationToken, Task>? OnStop { get; init; }

    /// <summary>
    /// Fires once when the owning <see cref="Agency.Harness.ChatSession"/> is disposed,
    /// signalling the end of the whole session (not a single turn). Unlike <see cref="OnStop"/>
    /// (which fires every turn), this fires exactly once per session lifetime.
    /// </summary>
    public Func<SessionEndedHookContext, CancellationToken, Task>? OnSessionEnd { get; init; }

    /// <summary>Empty hooks — all delegates are null.</summary>
    public static AgentHooks None { get; } = new();
}